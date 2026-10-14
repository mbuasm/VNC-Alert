using System.Text;
using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Commands;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Probe.Tests;

/// <summary>
/// <c>[Fact]</c>, który jest POMIJANY (nie udaje przejścia), gdy DPAPI w tej sesji odmawia dostępu — np. sesja usługi
/// bez profilu interaktywnego daje <c>CryptographicException: Odmowa dostępu</c> w <c>ProtectedData.Protect</c>.
/// Wynik testu w takiej sesji to „pominięty” z powodem, a na koncie operatora i w CI — zwykły przebieg.
/// </summary>
public sealed class DpapiFactAttribute : FactAttribute
{
    public DpapiFactAttribute()
    {
        if (!KolektorPasswordStore.IsDpapiAvailable(out string? error))
        {
            Skip = $"DPAPI niedostępne w tej sesji ({error}) — test wymaga konta z dostępem do ProtectedData.";
        }
    }
}

/// <summary>Magazyn haseł kolektora (DPAPI CurrentUser) i użycie hasła z magazynu przez <c>diagnoza --tryb dlugi</c>.</summary>
public sealed class KolektorPasswordStoreTests
{
    [DpapiFact]
    public void RoundTrip_ZapisOdczytUsuniecie_PlikJsonZBase64BezJawnegoHasla()
    {
        string dir = DiagnozaTestConfig.TempDir("hasla");
        string path = Path.Combine(dir, "hasla.dpapi");
        try
        {
            ISecretStore store = KolektorPasswordStore.Open(path);
            store.Set("amm580", "Tajne-580!");

            Assert.True(File.Exists(path));
            string raw = File.ReadAllText(path);
            Assert.DoesNotContain("Tajne-580!", raw, StringComparison.Ordinal);
            Dictionary<string, string> doc = JsonSerializer.Deserialize<Dictionary<string, string>>(raw)!;
            Assert.True(doc.ContainsKey("amm580"));
            Assert.NotEmpty(Convert.FromBase64String(doc["amm580"])); // base64 szyfrogramu

            ISecretStore again = KolektorPasswordStore.Open(path);
            Assert.Equal("Tajne-580!", again.Get("amm580"));
            Assert.Null(again.Get("premile"));
            Assert.Contains("amm580", again.Keys);

            Assert.True(again.Remove("amm580"));
            Assert.Null(KolektorPasswordStore.Open(path).Get("amm580"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    [DpapiFact]
    public async Task HasloZapisz_CzytaZPotokuNieZArgumentu_IHasloNieTrafiaDoRaportu()
    {
        string dir = DiagnozaTestConfig.TempDir("haslocmd");
        string path = Path.Combine(dir, "hasla.dpapi");
        string outDir = Path.Combine(dir, "out");
        try
        {
            ProbeProcess.Result run = await ProbeProcess.RunAsync(Encoding.UTF8.GetBytes("Sekret-123\n"), ProbeTimeout, dir,
                "haslo", "zapisz", "amm580", "--plik", path, "--out", outDir);
            Assert.True(run.ExitCode == 0, $"kod wyjścia {run.ExitCode}; stdout: {run.StdOut}; stderr: {run.StdErr}");

            Assert.Equal("Sekret-123", KolektorPasswordStore.Open(path).Get("amm580"));
            Assert.DoesNotContain("Sekret-123", File.ReadAllText(Path.Combine(outDir, "raport.txt")), StringComparison.Ordinal);
            Assert.DoesNotContain("Sekret-123", run.StdOut + run.StdErr, StringComparison.Ordinal);

            var global = new GlobalOptions(outDir, null, true);
            using (var report = new Report(global.OutDir, "haslo"))
            {
                Assert.Equal(0, await new HasloCommand().RunAsync(["usun", "--plik", path], global, report, new ProbeSession(global), CancellationToken.None));
            }

            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [DpapiFact]
    public async Task HasloZapisz_ZPotokuZBom_NieZapisujeZnacznikaBom()
    {
        // PS 5.1 przy części sposobów uruchomienia poprzedza potok znacznikiem BOM; to nie jest część hasła.
        // Bajty jak w potoku: BOM UTF-8 (EF BB BF) + hasło z polską literą; probe czyta wejście jako UTF-8.
        string dir = DiagnozaTestConfig.TempDir("haslobom");
        string path = Path.Combine(dir, "hasla.dpapi");
        try
        {
            byte[] input = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Sekret-ł\n")];
            ProbeProcess.Result run = await ProbeProcess.RunAsync(input, ProbeTimeout, dir,
                "haslo", "zapisz", "amm580", "--plik", path, "--out", Path.Combine(dir, "out"));
            Assert.True(run.ExitCode == 0, $"kod wyjścia {run.ExitCode}; stdout: {run.StdOut}; stderr: {run.StdErr}");

            Assert.Equal("Sekret-ł", KolektorPasswordStore.Open(path).Get("amm580"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HasloBezPodkomendy_BladUzycia()
    {
        string dir = DiagnozaTestConfig.TempDir("haslobad");
        try
        {
            var global = new GlobalOptions(Path.Combine(dir, "out"), null, true);
            using var report = new Report(global.OutDir, "haslo");
            await Assert.ThrowsAsync<ProbeUsageException>(() => new HasloCommand().RunAsync(["cos"], global, report, new ProbeSession(global), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybDlugi_UzywaHaslaZMagazynu_DlaSerweraVncAuth()
    {
        // Magazyn wstrzyknięty przez interfejs (InMemorySecretStore) — test nie zależy od DPAPI; sprawdza, że --tryb dlugi
        // bez --password łączy się z serwerem wymagającym hasła, bo faza 0 i kolektor biorą hasło z magazynu.
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            SecurityTypes = [2],
            Password = "tajne-580",
            Screen = DiagnozaTestConfig.Solid(16, 16, new Rgba(240, 240, 240)),
            IncrementalSendsNothingWhenUnchanged = true,
        });
        string dir = DiagnozaTestConfig.TempDir("dlugihaslo");
        DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("amm580", server.Port));
        var store = new InMemorySecretStore();
        store.Set("amm580", "tajne-580");
        try
        {
            string outDir = Path.Combine(dir, "out");
            var global = new GlobalOptions(outDir, null, NoAuthPrompt: true, ConfigPath: Path.Combine(dir, "config.json"), All: true);
            int exit;
            using (var report = new Report(outDir, "diagnoza"))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                exit = await DiagnozaCommand.RunCoreAsync(["--tryb", "dlugi", "--seconds", "6", "--interval-s", "1", "--no-ocr"], global, report, new ProbeSession(global), [], store, cts.Token);
            }

            Assert.Equal(ProbeExitCodes.AllConnected, exit);
            string summary = File.ReadAllText(Path.Combine(outDir, "dlugi", "podsumowanie.json"));
            Assert.Contains("\"status\": \"połączono\"", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("tajne-580", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("tajne-580", File.ReadAllText(Path.Combine(outDir, "raport.txt")), StringComparison.Ordinal);
            string log = File.ReadAllText(Directory.GetFiles(Path.Combine(outDir, "dlugi", "amm580"), "dziennik-*.txt").Single());
            Assert.DoesNotContain("tajne-580", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybDlugi_BezHaslaWMagazynie_SerwerVncAuth_StatusWymagaHasla()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { SecurityTypes = [2], Password = "tajne-580" });
        string dir = DiagnozaTestConfig.TempDir("dlugibez");
        DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("amm580", server.Port));
        try
        {
            string outDir = Path.Combine(dir, "out");
            var global = new GlobalOptions(outDir, null, NoAuthPrompt: true, ConfigPath: Path.Combine(dir, "config.json"), All: true);
            int exit;
            using (var report = new Report(outDir, "diagnoza"))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                exit = await DiagnozaCommand.RunCoreAsync(["--tryb", "dlugi", "--seconds", "4", "--interval-s", "1", "--no-ocr"], global, report, new ProbeSession(global), [], new InMemorySecretStore(), cts.Token);
            }

            Assert.Equal(ProbeExitCodes.NoneConnected, exit);
            Assert.Contains("\"status\": \"wymaga hasła\"", File.ReadAllText(Path.Combine(outDir, "dlugi", "podsumowanie.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }


    private static string WriteInstalledConfig(string dir, bool enabled520)
    {
        string path = Path.Combine(dir, "installed-config.json");
        AppConfig cfg = DiagnozaTestConfig.Config(
            DiagnozaTestConfig.Machine("amm520-1", 5900, "aidite5x", enabled: enabled520, host: "192.168.1.101"),
            DiagnozaTestConfig.Machine("amm520-2", 5900, "aidite5x", enabled: enabled520, host: "192.168.1.102"),
            DiagnozaTestConfig.Machine("amm580", 5900, "amm580", enabled: true, host: "192.168.1.110"));
        File.WriteAllText(path, ConfigLoader.Serialize(cfg));
        return path;
    }

    [Fact]
    public void VncAlertConflict_Pomija520ObserwowanePrzezZainstalowanyProgram_NieInneMaszyny()
    {
        string dir = DiagnozaTestConfig.TempDir("konflikt");
        try
        {
            string installed = WriteInstalledConfig(dir, enabled520: true);
            IReadOnlyCollection<string> watched = VncAlertConflict.WatchedByInstalledApp(installed);
            Assert.Contains("amm520-1", watched);
            Assert.Contains("192.168.1.102:5900", watched);

            MachineConfig[] machines =
            [
                DiagnozaTestConfig.Machine("amm520-1", 5900, "aidite5x", host: "192.168.1.101"),
                DiagnozaTestConfig.Machine("inne-id-520", 5900, "aidite5x", host: "192.168.1.102"), // po host:port
                DiagnozaTestConfig.Machine("amm580", 5900, "amm580", host: "192.168.1.110"),
                DiagnozaTestConfig.Machine("premile", 5900, "amm580", host: "192.168.1.114"),
            ];
            IReadOnlyList<VncAlertConflict.Skip> skips = VncAlertConflict.SkipsForLongMode(machines, watched, force: false);

            Assert.Equal(["amm520-1", "inne-id-520"], skips.Select(s => s.Id));
            Assert.All(skips, s => Assert.Contains("bez nowych połączeń", s.Powod, StringComparison.Ordinal));
            Assert.Equal(2, VncAlertConflict.SkipsForLongMode(machines, watched, force: true).Count);
            Assert.Equal(2, VncAlertConflict.SkipsForLongMode(machines, VncAlertConflict.WatchedByInstalledApp(WriteInstalledConfig(dir, enabled520: false)), force: false).Count);
            Assert.Empty(VncAlertConflict.WatchedByInstalledApp(Path.Combine(dir, "nie-ma.json")));
            File.WriteAllText(Path.Combine(dir, "zly.json"), "{ nie json");
            Assert.Empty(VncAlertConflict.WatchedByInstalledApp(Path.Combine(dir, "zly.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrybDlugi_520ObserwowanePrzezVncAlert_PomijaneZPowodemWPodsumowaniu_TakzeZOpcjaZ520()
    {
        await using var s520 = new FakeRfbServer(new FakeRfbServerOptions { ProtocolVersion = "3.3", Screen = DiagnozaTestConfig.Solid(16, 16, new Rgba(200, 200, 200)) });
        await using var s580 = new FakeRfbServer(new FakeRfbServerOptions { Screen = DiagnozaTestConfig.Solid(16, 16, new Rgba(200, 200, 200)), IncrementalSendsNothingWhenUnchanged = true });
        string dir = DiagnozaTestConfig.TempDir("dlugi520");
        DiagnozaTestConfig.WriteConfig(dir,
            DiagnozaTestConfig.Machine("amm520-1", s520.Port, "aidite5x"),
            DiagnozaTestConfig.Machine("amm580", s580.Port, "amm580"));
        // Zainstalowany VNC Alert "obserwuje" amm520-1 pod tym samym id.
        string installed = Path.Combine(dir, "installed-config.json");
        File.WriteAllText(installed, ConfigLoader.Serialize(DiagnozaTestConfig.Config(
            DiagnozaTestConfig.Machine("amm520-1", 5900, "aidite5x", host: "192.168.1.101"))));
        try
        {
            string outDir = Path.Combine(dir, "out");
            var global = new GlobalOptions(outDir, null, NoAuthPrompt: true, ConfigPath: Path.Combine(dir, "config.json"), All: true);
            int exit;
            using (var report = new Report(outDir, "diagnoza"))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                exit = await DiagnozaCommand.RunCoreAsync(["--tryb", "dlugi", "--seconds", "5", "--interval-s", "1", "--no-ocr", "--vnc-alert-config", installed],
                    global, report, new ProbeSession(global), [], new InMemorySecretStore(), cts.Token);
            }

            Assert.Equal(ProbeExitCodes.AllConnected, exit);
            string summary = File.ReadAllText(Path.Combine(outDir, "dlugi", "podsumowanie.json"));
            Assert.Contains("\"pominiete\"", summary, StringComparison.Ordinal);
            Assert.Contains("\"amm520-1\": \"520 pominięta", summary, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(outDir, "dlugi", "amm520-1")));
            Assert.Equal(0, s520.ConnectionCount);

            string outDir2 = Path.Combine(dir, "out2");
            var global2 = global with { OutDir = outDir2 };
            using (var report = new Report(outDir2, "diagnoza"))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                exit = await DiagnozaCommand.RunCoreAsync(["--tryb", "dlugi", "--seconds", "5", "--interval-s", "1", "--no-ocr", "--vnc-alert-config", installed, "--z-520"],
                    global2, report, new ProbeSession(global2), [], new InMemorySecretStore(), cts.Token);
            }

            Assert.Equal(ProbeExitCodes.AllConnected, exit);
            Assert.False(Directory.Exists(Path.Combine(outDir2, "dlugi", "amm520-1")));
            Assert.Equal(0, s520.ConnectionCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
