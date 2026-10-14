using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;
using Xunit.Sdk;

namespace MaszynyAlert.Probe.Tests;

public sealed class TestTestyProbeProcessTests
{
    /// <summary>Limit pomocnika w teście limitu.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    /// <summary>
    /// NATURALNY koniec <c>probe capture</c> przy serwerze, który przyjmuje TCP i milczy: nagłówka RFB klient czeka do limitu odczytu
    /// (<see cref="RfbClientOptions.ReadTimeout"/>, 30 s — probe go nie zmienia; limit połączenia 5 s obejmuje samo TCP).
    /// capture sam kończy się po 30,1 s (kod 1, „przekroczono limit czasu odczytu (30s)”, wiersz końcowy „Raport: …”).
    /// </summary>
    private static readonly TimeSpan NaturalEnd = new RfbClientOptions { Host = "127.0.0.1" }.ReadTimeout;

    [Fact]
    public async Task ProbeProcess_PrzekroczonyLimit_ZabijaProcesPotomnyIPadaZOpisem()
    {
        // Serwer „milczy”: połączenie TCP wchodzi do kolejki nasłuchu, nikt nie wysyła wersji RFB — capture czeka na nagłówek
        // do limitu odczytu (NaturalEnd, 30 s). Limit pomocnika (3 s) upływa dużo wcześniej.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        int port = ((IPEndPoint)silent.LocalEndpoint).Port;
        string dir = DiagnozaTestConfig.TempDir("probeproc-limit");
        string outDir = Path.Combine(dir, "out");
        string report = Path.Combine(outDir, "raport.txt");
        try
        {
            var sw = Stopwatch.StartNew();
            FailException failure = await Assert.ThrowsAsync<FailException>(() =>
                ProbeProcess.RunAsync([], Limit, dir, "capture", $"127.0.0.1:{port}", "--out", outDir));
            TimeSpan failedAt = sw.Elapsed;

            Assert.Contains("nie zakończył się w 3 s — zabity", failure.Message, StringComparison.Ordinal);
            // Chwila zabicia mierzona przez pomocnika od startu procesu (wątek czekający w jądrze, nie pula): na limicie, z zapasem
            // tylko na wybudzenie wątku — niezależnie od tego, kiedy test wróci do swojego wątku.
            Match killed = Regex.Match(failure.Message, @"zabity \((?<s>[0-9]+\.[0-9]+) s od startu procesu\)");
            Assert.True(killed.Success, "opis porażki bez chwili zabicia: " + failure.Message);
            var killedAfter = TimeSpan.FromSeconds(double.Parse(killed.Groups["s"].Value, CultureInfo.InvariantCulture));
            Assert.True(killedAfter >= Limit && killedAfter < Limit + TimeSpan.FromSeconds(2),
                $"pomocnik zabił proces po {killedAfter.TotalSeconds:0.00} s od startu, limit {Limit.TotalSeconds:0} s");
            Assert.True(failedAt >= Limit, $"pomocnik zabił po {failedAt.TotalSeconds:0.0} s, przed limitem {Limit.TotalSeconds:0} s");
            Assert.True(failedAt < NaturalEnd / 2, $"pomocnik zwrócił po {failedAt.TotalSeconds:0.0} s (limit {Limit.TotalSeconds:0} s, "
                + $"proces potomny kończy sam najwcześniej po {NaturalEnd.TotalSeconds:0} s)");

            // Proces potomny naprawdę zniknął: raport, który trzymał otwarty (StreamWriter), zwalnia się chwilę po Kill...
            Assert.True(File.Exists(report), "probe nie zdążył otworzyć raportu przed upływem limitu (zbyt wolny start).");
            bool released = await ReleasedWithinAsync(report, TimeSpan.FromSeconds(1.5));
            Assert.True(released, "raport.txt nadal otwarty 1,5 s po zabiciu — proces potomny żyje.");
            // ...i to skutek zabicia, nie naturalnego końca: capture po limicie odczytu dopisałby wiersz końcowy „Raport: …”.
            Assert.DoesNotContain("Raport: ", File.ReadAllText(report), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [DpapiFact]
    public async Task HasloZapisz_HasloZArgumentuIZOpcjiPassword_Pominiete_ZapisanyTylkoPotok_IBezSladuWRaporcie()
    {
        string dir = DiagnozaTestConfig.TempDir("haslo-wabik");
        string path = Path.Combine(dir, "hasla.dpapi");
        string outDir = Path.Combine(dir, "out");
        try
        {
            ProbeProcess.Result run = await ProbeProcess.RunAsync(Encoding.UTF8.GetBytes("Dobry-Z-Potoku\n"), TimeSpan.FromSeconds(60), dir,
                "haslo", "zapisz", "amm580", "Zly-Z-Argumentu", "--password", "Zly-Z-Opcji", "--plik", path, "--out", outDir);
            Assert.True(run.ExitCode == 0, $"kod wyjścia {run.ExitCode}; stderr: {run.StdErr}");

            var store = KolektorPasswordStore.Open(path);
            Assert.Equal("Dobry-Z-Potoku", store.Get("amm580"));
            Assert.Equal(new[] { "amm580" }, store.Keys);

            string reportText = File.ReadAllText(Path.Combine(outDir, "raport.txt"));
            foreach (string secret in new[] { "Dobry-Z-Potoku", "Zly-Z-Opcji" })
            {
                Assert.DoesNotContain(secret, reportText, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, run.StdOut + run.StdErr, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> ReleasedWithinAsync(string path, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) when (sw.Elapsed < limit)
            {
                await Task.Delay(20);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
