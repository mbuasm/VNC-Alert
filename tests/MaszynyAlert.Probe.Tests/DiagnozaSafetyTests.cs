using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Commands;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Probe.Tests;

[CollectionDefinition("Diagnoza credentials", DisableParallelization = true)]
public sealed class DiagnozaCredentialsCollection;

[Collection("Diagnoza credentials")]
public sealed class DiagnozaSafetyTests
{
    [Theory]
    [InlineData("szybki", "amm520-1", "aidite5x")]
    [InlineData("dlugi", "amm520-1", "aidite5x")]
    [InlineData("szybki", "amm520-2", "amm580")]
    [InlineData("dlugi", "amm520-2", "amm580")]
    [InlineData("szybki", "other-id", "aidite5x")]
    [InlineData("dlugi", "other-id", "aidite5x")]
    public async Task Maszyna520_NieDostajeNawetTcpPrzedHandshake(string mode, string id, string profile)
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { KickPreviousClientOnNewConnection = true });
        string dir = DiagnozaTestConfig.TempDir("safety520");
        try
        {
            DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine(id, server.Port, profile));
            var global = new GlobalOptions(Path.Combine(dir, "out"), null, true, ConfigPath: Path.Combine(dir, "config.json"), All: true);
            using (var report = new Report(global.OutDir, "diagnoza"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                int code = await DiagnozaCommand.RunCoreAsync(["--tryb", mode, "--seconds", "2", "--co-sekund", "1", "--no-ocr", "--z-520"], global, report, new ProbeSession(global), [], new InMemorySecretStore(), timeout.Token);
                Assert.Equal(ProbeExitCodes.NoneConnected, code);
            }
            Assert.Equal(0, server.ConnectionCount);
            string summary = File.ReadAllText(Path.Combine(global.OutDir, mode == "dlugi" ? "dlugi" : "diagnoza", "podsumowanie.json"));
            Assert.Contains(VncAlertConflict.SafetyReason, summary, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HasloRunnera_DotyczyTylko580_AOdmowaNieJestPonawianaWKolektorze()
    {
        const string secret = "AUDIT_ONLY_PASSWORD";
        const string env = "VNCALERT_DIAGNOZA_PASSWORD_580";
        string? previous = Environment.GetEnvironmentVariable(env);
        await using var s580 = new FakeRfbServer(new FakeRfbServerOptions { SecurityTypes = [2], RejectVncAuth = true });
        await using var other = new FakeRfbServer(new FakeRfbServerOptions { SecurityTypes = [2], Password = secret });
        string dir = DiagnozaTestConfig.TempDir("authsafety");
        try
        {
            Environment.SetEnvironmentVariable(env, secret);
            DiagnozaTestConfig.WriteConfig(dir, DiagnozaTestConfig.Machine("amm580", s580.Port), DiagnozaTestConfig.Machine("premile", other.Port));
            var global = new GlobalOptions(Path.Combine(dir, "out"), null, true, ConfigPath: Path.Combine(dir, "config.json"), All: true);
            using (var report = new Report(global.OutDir, "diagnoza"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                int code = await DiagnozaCommand.RunCoreAsync(["--tryb", "dlugi", "--seconds", "7", "--no-ocr"], global, report, new ProbeSession(global), [], new InMemorySecretStore(), timeout.Token);
                Assert.Equal(ProbeExitCodes.NoneConnected, code);
            }
            Assert.Equal(2, s580.ConnectionCount);
            Assert.Equal(1, other.ConnectionCount);
            Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(global.OutDir, "raport.txt")), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(env, previous); Directory.Delete(dir, true); }
    }

    [DpapiFact]
    public async Task HasloZapisz_ZeSrodowiskaRunnera_PolskieZnakiBezZmianIBezPotoku()
    {
        // Runner przekazuje hasło jak w trybie szybkim — w środowisku procesu dziecka, z --z-env.
        string secret = "SYNTHETIC_Zażółć \"$`1";
        string env = DiagnozaCommand.RunnerPasswordVariable;
        string? previous = Environment.GetEnvironmentVariable(env);
        string dir = DiagnozaTestConfig.TempDir("haslo-env");
        string path = Path.Combine(dir, "hasla.dpapi");
        try
        {
            Environment.SetEnvironmentVariable(env, secret);
            var global = new GlobalOptions(Path.Combine(dir, "out"), null, true);
            using (var report = new Report(global.OutDir, "haslo"))
            {
                Assert.Equal(0, await new HasloCommand().RunAsync(["zapisz", "amm580", "--z-env", "--plik", path], global, report, new ProbeSession(global), CancellationToken.None));
                await Assert.ThrowsAsync<ProbeUsageException>(() =>
                    new HasloCommand().RunAsync(["zapisz", "premile", "--z-env", "--plik", path], global, report, new ProbeSession(global), CancellationToken.None));
            }

            Assert.Equal(secret, KolektorPasswordStore.Open(path).Get("amm580"));
            Assert.Null(KolektorPasswordStore.Open(path).Get("premile"));
            Assert.DoesNotContain("SYNTHETIC_Za", File.ReadAllText(Path.Combine(dir, "out", "raport.txt")), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(env, previous); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Kolektor_BladHaslaPoFazie0_NieTworzyPetliLogowania()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { SecurityTypes = [2], RejectVncAuth = true });
        string dir = DiagnozaTestConfig.TempDir("collector-auth");
        try
        {
            MachineConfig machine = DiagnozaTestConfig.Machine("amm580", server.Port);
            using var report = new Report(dir, "test");
            using var collector = new MachineCollector(machine, DiagnozaTestConfig.Config(machine), Path.Combine(dir, "machine"),
                DlugiOptions.Parse(["--seconds", "7", "--no-ocr"]), MachineMode.Trwale, "AUDIT_BAD", null, report, []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            await collector.RunAsync(timeout.Token);
            Assert.Equal(1, server.ConnectionCount);
            Assert.Null(collector.Stats.ConnectedAt);
            Assert.Equal(MachineStatus.WrongPassword, collector.Stats.Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BrakKlatek_NiePotwierdzaBrakuCzerni()
    {
        var r = Result();
        MachineConclusion c = DiagnozaConclusions.Build(r, DiagnozaTestConfig.Profiles());
        Assert.Contains("brak danych", c.CoDalej, StringComparison.Ordinal);
        Assert.DoesNotContain("nie czernieje", c.CoDalej, StringComparison.Ordinal);
        Assert.Contains(DiagnozaSummary.RenderText([r], DiagnozaTestConfig.Profiles(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), l => l.Contains("BRAK POMIARU", StringComparison.Ordinal));
    }

    [Fact]
    public void CzerwienPozaZnanymiRoi_NieJestBrakiemCzerwieni()
    {
        var r = Result();
        r.Frames.Add(Sample(DiagnozaTestConfig.WithRed(100, 100, new RectInt(80, 80, 10, 10))));
        MachineConclusion c = DiagnozaConclusions.Build(r, DiagnozaTestConfig.Profiles());
        Assert.Contains("profil nieustalony", c.NajlepszyProfil, StringComparison.Ordinal);
        Assert.DoesNotContain("brak czerwieni", c.CoDalej, StringComparison.Ordinal);
        Assert.NotEqual("-", c.SugerowanyRoi);
    }

    [Fact]
    public void RoiNaKrawedziMiesciSieWEkranie_ARozneRozmiaryNieSaLaczone()
    {
        var r = Result();
        r.Frames.Add(Sample(DiagnozaTestConfig.WithRed(32, 32, new RectInt(30, 30, 2, 2))));
        Assert.StartsWith("22,22,10,10 ", DiagnozaConclusions.SuggestedRedRoi(r), StringComparison.Ordinal);
        r.Frames.Add(Sample(DiagnozaTestConfig.WithRed(64, 64, new RectInt(10, 10, 4, 4))));
        Assert.Equal("-", DiagnozaConclusions.SuggestedRedRoi(r));
    }

    [Theory]
    [InlineData(MachineStatus.MissingUsername)]
    [InlineData(MachineStatus.WrongPassword)]
    [InlineData(MachineStatus.PasswordRequired)]
    [InlineData(MachineStatus.Unreachable)]
    public void CoDalejNieZalecaZmianSerweraAniPonownegoLogowania(string status)
    {
        var r = Result(); r.ConnectedAt = null; r.Status = status;
        string next = DiagnozaConclusions.Build(r, DiagnozaTestConfig.Profiles()).CoDalej;
        Assert.DoesNotContain("przełącz serwer", next, StringComparison.Ordinal);
        Assert.DoesNotContain("i powtórz", next, StringComparison.Ordinal);
    }

    [Fact]
    public void CzarnyObrazNieNakazujeDotykacMaszyny()
    {
        var r = Result(); r.Frames.Add(Sample(DiagnozaTestConfig.Solid(32,32,new Rgba(0,0,0))));
        Assert.Contains("bez dotykania", DiagnozaConclusions.Build(r, DiagnozaTestConfig.Profiles()).CoDalej, StringComparison.Ordinal);
    }

    private static MachineDiagnosticResult Result() => new("test", "Test", "127.0.0.1", 5900, "amm580")
        { Status = MachineStatus.Connected, ConnectedAt = DateTimeOffset.UnixEpoch };
    private static FrameSample Sample(RgbaImage image) => new(DateTimeOffset.UnixEpoch, 0, 1,
        FrameMetrics.Measure(image, DiagnozaTestConfig.Profiles()), null, true);
}
