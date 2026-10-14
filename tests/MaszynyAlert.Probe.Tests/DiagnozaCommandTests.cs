using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Commands;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Probe.Tests;

/// <summary>
/// Cała komenda <c>probe.exe diagnoza</c> (tryb szybki i długi) na plikach: <c>--config</c> z maszynami na fałszywych
/// serwerach, <c>--seconds</c>, bez OCR Windows (silnik podstawiony). Sprawdza kody wyjścia, podsumowania i pliki kolektora.
/// </summary>
public sealed class DiagnozaCommandTests
{
    private static async Task<(int Exit, string Dir)> RunAsync(string dir, string[] args, bool noOcr = true, ISecretStore? passwordStore = null)
    {
        string outDir = Path.Combine(dir, "out");
        var global = new GlobalOptions(outDir, null, NoAuthPrompt: true, ConfigPath: Path.Combine(dir, "config.json"), All: true);
        using var report = new Report(outDir, "diagnoza");
        var session = new ProbeSession(global);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int exit = await DiagnozaCommand.RunCoreAsync(args, global, report, session, noOcr ? [] : [new QueueOcrEngine()], passwordStore ?? new InMemorySecretStore(), cts.Token);
        return (exit, outDir);
    }

    [Fact]
    public async Task TrybSzybki_DwieMaszyny_JednaMilczy_Kod3IPodsumowanieZObiema()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = DiagnozaTestConfig.Solid(32, 32, new Rgba(250, 250, 250)) });
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        int closedPort = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        string dir = DiagnozaTestConfig.TempDir("cmd");
        DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("amm580", server.Port), DiagnozaTestConfig.Machine("cameo1", closedPort, enabled: false));
        try
        {
            (int exit, string outDir) = await RunAsync(dir, ["--seconds", "6", "--co-sekund", "1", "--no-zip", "--no-ocr"]);

            Assert.Equal(ProbeExitCodes.SomeConnected, exit);
            string txt = File.ReadAllText(Path.Combine(outDir, "diagnoza", "podsumowanie.txt"));
            string json = File.ReadAllText(Path.Combine(outDir, "diagnoza", "podsumowanie.json"));
            Assert.Contains("AMM580", txt, StringComparison.Ordinal);
            Assert.Contains("CAMEO1", txt, StringComparison.Ordinal); // enabled:false, a mimo to objęta (--all domyślnie)
            Assert.Contains("nie odpowiada", txt, StringComparison.Ordinal);
            Assert.Contains("Co dalej:", txt, StringComparison.Ordinal);
            Assert.Contains("\"kodWyjscia\": 3", json, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(outDir, "diagnoza", "amm580", "dziennik.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "diagnoza", "cameo1", "dziennik.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "podsumowanie.json")));
            Assert.True(File.Exists(Path.Combine(outDir, MachineModeStore.FileName)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybSzybki_ZadnaNieOdpowiada_Kod4()
    {
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        int closedPort = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        string dir = DiagnozaTestConfig.TempDir("cmd4");
        DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("premile", closedPort));
        try
        {
            (int exit, _) = await RunAsync(dir, ["--seconds", "3", "--co-sekund", "1", "--no-zip", "--no-ocr"]);
            Assert.Equal(ProbeExitCodes.NoneConnected, exit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybNieznany_BladUzycia()
    {
        string dir = DiagnozaTestConfig.TempDir("tryb");
        var global = new GlobalOptions(Path.Combine(dir, "out"), null, true);
        try
        {
            using var report = new Report(global.OutDir, "diagnoza");
            await Assert.ThrowsAsync<ProbeUsageException>(() => DiagnozaCommand.RunCoreAsync(["--tryb", "inny"], global, report, new ProbeSession(global), [], new InMemorySecretStore(), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybDlugi_EpizodCzerwieni_TrafiaDoEpizodyJsonlZBboxIOcr()
    {
        var options = new FakeRfbServerOptions
        {
            Screen = DiagnozaTestConfig.Solid(32, 32, new Rgba(255, 255, 255)),
            IncrementalNoResponseWhenUnchanged = true,
            DeliverPendingIncrementalOnScreenChange = true,
        };
        await using var server = new FakeRfbServer(options);
        string dir = DiagnozaTestConfig.TempDir("dlugi");
        DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("premile", server.Port));
        try
        {
            // Po 3 s ekran dostaje czerwony pasek, po 6 s wraca do białego: epizod ma się zamknąć (EndAfter = 10 s) przed końcem (--seconds 20).
            Task swap = Task.Run(async () =>
            {
                await Task.Delay(3000);
                server.Screen = DiagnozaTestConfig.WithRed(32, 32, new RectInt(4, 20, 24, 6));
                await Task.Delay(3000);
                server.Screen = DiagnozaTestConfig.Solid(32, 32, new Rgba(255, 255, 255));
            });

            (int exit, string outDir) = await RunAsync(dir, ["--tryb", "dlugi", "--seconds", "20", "--interval-s", "1", "--limit-mb", "100"], noOcr: false);
            await swap;

            Assert.Equal(ProbeExitCodes.AllConnected, exit);
            string mdir = Path.Combine(outDir, "dlugi", "premile");
            string epizody = Path.Combine(mdir, "epizody.jsonl");
            Assert.True(File.Exists(epizody), "brak epizody.jsonl");
            string[] rows = File.ReadAllLines(epizody);
            Assert.Contains(rows, r => r.Contains("\"kolor\":\"czerwien\"", StringComparison.Ordinal) && r.Contains("\"bbox\":{\"x\":4,\"y\":20,\"width\":24,\"height\":6}", StringComparison.Ordinal));
            Assert.Contains(rows, r => r.Contains("\"maszyna\":\"premile\"", StringComparison.Ordinal) && r.Contains("\"poczatek\"", StringComparison.Ordinal));
            string day = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(mdir, day, "epizody"), "*czerwien-0s.png"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(mdir, day, "zmiany"), "*.png"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(mdir, day, "okresowe"), "*.png"));
            Assert.True(File.Exists(Path.Combine(mdir, $"dziennik-{day}.txt")));
            Assert.True(File.Exists(Path.Combine(mdir, $"pomiary-{day}.jsonl")));
            string summary = File.ReadAllText(Path.Combine(outDir, "dlugi", "podsumowanie.json"));
            Assert.Contains("\"tryb\": \"trwale\"", summary, StringComparison.Ordinal);
            Assert.Contains("\"czerwien\": 1", summary, StringComparison.Ordinal);
            Assert.Contains("\"zakonczony\": true", summary, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(outDir, "dlugi", $"podsumowanie-{day}.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybDlugi_520BezLokalnejInstalacji_NigdyNieLaczy()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = DiagnozaTestConfig.Solid(16, 16, new Rgba(200, 200, 200)), ProtocolVersion = "3.3" });
        string dir = DiagnozaTestConfig.TempDir("poll");
        DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("amm520-1", server.Port, "aidite5x"));
        try
        {
            (int exit, string outDir) = await RunAsync(dir, ["--tryb", "dlugi", "--seconds", "10", "--interval-s", "1", "--no-ocr", "--vnc-alert-config", Path.Combine(dir, "brak.json")]);

            Assert.Equal(ProbeExitCodes.NoneConnected, exit);
            Assert.Equal(0, server.ConnectionCount);
            Assert.False(Directory.Exists(Path.Combine(outDir, "dlugi", "amm520-1")));
            Assert.Contains(VncAlertConflict.SafetyReason, File.ReadAllText(Path.Combine(outDir, "dlugi", "podsumowanie.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybDlugi_LimitDysku_KonczyZbieranieBezWzrostuKataloguIZapisujePodsumowanie()
    {
        var options = new FakeRfbServerOptions { Screen = DiagnozaTestConfig.Solid(64, 64, new Rgba(250, 250, 250)), IncrementalSendsNothingWhenUnchanged = true };
        await using var server = new FakeRfbServer(options);
        string dir = DiagnozaTestConfig.TempDir("limit");
        DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("amm580", server.Port));
        string mdir = Path.Combine(dir, "out", "dlugi", "amm580");
        // Katalog maszyny dokładnie na limicie przed startem — żadnego nowego zapisu, także epizodu/OCR/dziennika.
        Directory.CreateDirectory(Path.Combine(mdir, "2020-01-01", "okresowe"));
        File.WriteAllBytes(Path.Combine(mdir, "2020-01-01", "okresowe", "20200101-000000.png"), new byte[1024 * 1024]);
        try
        {
            (int exit, string outDir) = await RunAsync(dir, ["--tryb", "dlugi", "--seconds", "8", "--interval-s", "1", "--limit-mb", "1", "--no-ocr"]);

            Assert.Equal(ProbeExitCodes.Error, exit);
            string day = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            Assert.False(Directory.Exists(Path.Combine(mdir, day)), "nowe dane mimo limitu");
            Assert.Equal(1024 * 1024, DiskBudget.Size(mdir));
            Assert.Single(Directory.GetFiles(mdir, "*", SearchOption.AllDirectories));
            Assert.Contains("\"limitPrzekroczony\": true", File.ReadAllText(Path.Combine(outDir, "dlugi", "podsumowanie.json")), StringComparison.Ordinal);
            Assert.Contains("\"zakonczony\": true", File.ReadAllText(Path.Combine(outDir, "dlugi", "podsumowanie.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
