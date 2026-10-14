using System.Diagnostics;
using System.Text.Json;
using MaszynyAlert.Probe.Commands;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;
using Xunit.Abstractions;

namespace MaszynyAlert.Probe.Tests;

public sealed class WeryfikacjaKolektorPodsumowanieTests(ITestOutputHelper output)
{
    [Fact]
    public async Task KoncowePodsumowanie_CzytelnikBezFileShareDeletePrzez1s_ZapisanePoPonowieniach()
    {
        string dir = DiagnozaTestConfig.TempDir("wer-summary-1s");
        try
        {
            using var report = new Report(dir, "test");
            Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: false));
            string path = Path.Combine(dir, "podsumowanie.json");
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Task release = Task.Run(async () =>
            {
                await Task.Delay(1000);
                reader.Dispose();
            });
            var clock = Stopwatch.StartNew();
            bool saved;
            try
            {
                saved = DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: true);
            }
            finally
            {
                await release;
                reader.Dispose();
            }

            output.WriteLine($"zapis koncowy przy czytelniku 1 s: {saved}, {clock.ElapsedMilliseconds} ms");
            Assert.True(saved, "końcowe podsumowanie nie zapisane mimo czytelnika krótszego niż budżet ponowień");
            Assert.True(Final(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task KoncowePodsumowanie_CzytelnikBezFileShareDeletePrzez5s_NieUszkadzaPlikuIWracaPoZwolnieniu()
    {
        // Granica: dłuższy czytelnik bez FileShare.Delete (np. własny skrypt z File.OpenRead) może zablokować podmianę dłużej
        // niż ponowienia. Wtedy wolno nie zapisać, ale nie wolno uszkodzić ani skasować poprzedniego podsumowania.
        string dir = DiagnozaTestConfig.TempDir("wer-summary-5s");
        try
        {
            using var report = new Report(dir, "test");
            Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: false));
            string path = Path.Combine(dir, "podsumowanie.json");
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Task release = Task.Run(async () =>
            {
                await Task.Delay(5000);
                reader.Dispose();
            });
            var clock = Stopwatch.StartNew();
            bool saved;
            try
            {
                saved = DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: true);
            }
            finally
            {
                await release;
                reader.Dispose();
            }

            output.WriteLine($"zapis koncowy przy czytelniku 5 s: {saved}, {clock.ElapsedMilliseconds} ms");
            if (saved)
            {
                Assert.True(Final(path));
            }
            else
            {
                Assert.False(Final(path), "nieudany zapis zmienił poprzednie podsumowanie");
                Assert.True(Final(path + ".tmp"), "brak końcowej treści w pliku tymczasowym");
            }

            Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: true), "zapis po zwolnieniu pliku");
            Assert.True(Final(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Podsumowanie_PlikTymczasowyNiedostepny_ZwracaFalseNieRuszaPoprzedniegoIWracaPoUsunieciuPrzeszkody()
    {
        string dir = DiagnozaTestConfig.TempDir("wer-summary-tmp-denied");
        try
        {
            string path = Path.Combine(dir, "podsumowanie.json");
            string reportPath;
            using (var report = new Report(dir, "test"))
            {
                reportPath = report.Path;
                Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: false));
                string before = File.ReadAllText(path);
                Directory.CreateDirectory(path + ".tmp"); // atrapa odmowy dostępu przy zapisie pliku tymczasowego

                Assert.False(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: true));
                Assert.Equal(before, File.ReadAllText(path));

                Directory.Delete(path + ".tmp");
                Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: true));
                Assert.True(Final(path));
            }

            Assert.Contains("Nie udało się zapisać podsumowania", File.ReadAllText(reportPath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Podsumowanie_CelTylkoDoOdczytu_NieUszkadzaPoprzedniego()
    {
        string dir = DiagnozaTestConfig.TempDir("wer-summary-readonly");
        string path = Path.Combine(dir, "podsumowanie.json");
        try
        {
            using var report = new Report(dir, "test");
            Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: false));
            string before = File.ReadAllText(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            bool saved = DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: true);
            output.WriteLine($"zapis do pliku tylko do odczytu: {saved}");
            if (saved)
            {
                Assert.True(Final(path));
            }
            else
            {
                Assert.Equal(before, File.ReadAllText(path));
            }
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Podsumowanie_PierwszyZapisBezPlikuDocelowego_TworzyGlowneIDzienneBezPlikowTymczasowych()
    {
        string dir = DiagnozaTestConfig.TempDir("wer-summary-first");
        try
        {
            using var report = new Report(dir, "test");
            Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: false));
            Assert.True(File.Exists(Path.Combine(dir, "podsumowanie.json")));
            Assert.Single(Directory.GetFiles(dir, "podsumowanie-????-??-??.json"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static bool Final(string path)
    {
        using var summary = JsonDocument.Parse(File.ReadAllText(path));
        return summary.RootElement.GetProperty("zakonczony").GetBoolean();
    }

    private static DlugiOptions Options() => new(null, 600, 300, 15, 1, 1, 600, 1024 * 1024, true);
}

[Collection("Diagnoza credentials")]
public sealed class WeryfikacjaHasloZEnvTests
{
    [DpapiFact]
    public async Task HasloZapisz_ZEnvBezZmiennej_Kod1IBezPlikuHasel()
    {
        string env = DiagnozaCommand.RunnerPasswordVariable;
        string? previous = Environment.GetEnvironmentVariable(env);
        string dir = DiagnozaTestConfig.TempDir("wer-haslo-bez-env");
        string path = Path.Combine(dir, "hasla.dpapi");
        try
        {
            Environment.SetEnvironmentVariable(env, null);
            var global = new GlobalOptions(Path.Combine(dir, "out"), null, true);
            using (var report = new Report(global.OutDir, "haslo"))
            {
                Assert.Equal(ProbeExitCodes.Error, await new HasloCommand().RunAsync(["zapisz", "amm580", "--z-env", "--plik", path], global, report, new ProbeSession(global), CancellationToken.None));
            }

            Assert.False(File.Exists(path), "plik haseł powstał bez hasła");
            Assert.Contains("Puste hasło", File.ReadAllText(Path.Combine(dir, "out", "raport.txt")), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(env, previous); Directory.Delete(dir, true); }
    }
}
