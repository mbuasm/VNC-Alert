using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Probe.Tests;

/// <summary>
/// Przebieg diagnostyki jednej maszyny (<see cref="MachineDiagnostic"/>) na fałszywym serwerze RFB z pętli zwrotnej —
/// krótki harmonogram (<c>--seconds</c>), wszystkie fazy. Każdy test ma własny katalog wyjściowy i własny serwer.
/// </summary>
public sealed class MachineDiagnosticTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private static (MachineDiagnostic Diag, string Dir, MachineSummary Row, Report Report) Create(
        MachineConfig machine, DiagnozaSchedule schedule, IReadOnlyList<IOcrEngine>? ocr = null, string? initialSkip = null, string? password = null)
    {
        string dir = DiagnozaTestConfig.TempDir("diag");
        var report = new Report(Path.Combine(dir, "out"), "test");
        var row = new MachineSummary(machine.Id);
        var diag = new MachineDiagnostic(machine, DiagnozaTestConfig.Config(machine), Path.Combine(dir, "out", "diagnoza", machine.Id), schedule,
            password, null, report, row, ocr ?? [], initialSkip);
        return (diag, dir, row, report);
    }

    private static async Task RunAsync(MachineDiagnostic diag, Report report, string dir)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        try
        {
            await diag.RunAsync(cts.Token);
        }
        finally
        {
            await diag.DisposeAsync();
            report.Dispose();
        }
    }

    [Fact]
    public async Task PelnyKrotkiPrzebieg_PrzechodziPrzezWszystkieFazyIZapisujePliki()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = DiagnozaTestConfig.WithRed(32, 32, new RectInt(0, 0, 32, 8)),
            DesktopName = "FakeHMI",
            IncrementalSendsNothingWhenUnchanged = true,
        });
        MachineConfig machine = DiagnozaTestConfig.Machine("amm580", server.Port);
        var ocr = new QueueOcrEngine();
        for (int i = 0; i < 40; i++)
        {
            ocr.EnqueueText("Progress: 12%");
        }

        (MachineDiagnostic diag, string dir, MachineSummary row, Report report) = Create(machine, DiagnozaSchedule.For(12, 1), [ocr]);
        await RunAsync(diag, report, dir);
        MachineDiagnosticResult r = diag.Result;

        try
        {
            Assert.Equal(MachineStatus.Connected, r.Status);
            Assert.NotNull(r.Server);
            Assert.Equal("FakeHMI", r.Server!.DesktopName);
            Assert.Equal("RFB 003.008\\n", r.Server.RawVersionHeader);
            Assert.False(r.RequiresPassword);
            Assert.NotNull(r.FirstFrameMs);
            Assert.True(r.Frames.Count >= 5, $"klatek {r.Frames.Count}");
            Assert.True(r.BlackTests.Count >= 4, $"punktów czerni {r.BlackTests.Count}");
            Assert.All(r.BlackTests, b => Assert.False(b.Czarna));
            Assert.Contains(r.Disconnects, d => d.Przyczyna.StartsWith("planowe", StringComparison.Ordinal));
            Assert.NotNull(r.ReconnectTest);
            Assert.True(r.ReconnectTest!.Udane, r.ReconnectTest.Opis);
            Assert.NotNull(r.SharedTest);
            Assert.True(r.SharedTest!.DrugiPolaczony && r.SharedTest.PierwszyPrzezyl, r.SharedTest.Opis);
            Assert.NotNull(r.UpdateRate);
            Assert.True(r.Connections >= 2);
            Assert.Contains("Raw", r.Encodings);
            Assert.True(r.OcrRuns.Count >= 1);
            Assert.NotNull(r.Anchors);
            Assert.Empty(r.Errors);

            string mdir = Path.Combine(dir, "out", "diagnoza", "amm580");
            Assert.True(File.Exists(Path.Combine(mdir, "dziennik.txt")));
            Assert.True(File.Exists(Path.Combine(mdir, "pomiary.jsonl")));
            Assert.True(File.Exists(Path.Combine(mdir, "sugerowane-roi.json")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(mdir, "klatki"), "*.png"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(mdir, "ocr"), "*.json"));
            string[] lines = File.ReadAllLines(Path.Combine(mdir, "pomiary.jsonl"));
            Assert.Equal(r.Frames.Count, lines.Length);
            Assert.Contains("\"czerwienBbox\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"powyzejProgu\":true", lines[0], StringComparison.Ordinal);

            Assert.Equal(MachineStatus.Connected, row.Status);
            Assert.Equal(r.Frames.Count, row.Frames);
            Assert.Equal("FakeHMI", row.ServerName);
            Assert.NotEqual("-", row.ProgressField);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CzarnyEkran_JestWykrywanyWTescieCzerni()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = DiagnozaTestConfig.Solid(32, 32, new Rgba(0, 0, 0)) });
        (MachineDiagnostic diag, string dir, _, Report report) = Create(DiagnozaTestConfig.Machine("qt-black", server.Port, "amm580"), DiagnozaSchedule.For(4, 1));
        await RunAsync(diag, report, dir);
        try
        {
            Assert.True(diag.Result.Frames.Count >= 2);
            Assert.All(diag.Result.BlackTests, b => Assert.True(b.Czarna));
            Assert.Equal(diag.Result.Frames.Count, diag.Result.BlackFrames);
            Assert.Equal("czarna od początku", DiagnozaConclusions.BlackVerdict(diag.Result));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ZmianaRozmiaruEkranu_NieKonczyPrzebiegu_INowyRozmiarJestZanotowany()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            // ServerInit mówi 32x32, a przy pierwszej aktualizacji serwer ogłasza DesktopSize 64x48 (prawdziwy rozmiar ekranu).
            Screen = DiagnozaTestConfig.Solid(64, 48, new Rgba(200, 200, 200)),
            Width = 32,
            Height = 32,
            AnnounceDesktopSize = true,
        });
        (MachineDiagnostic diag, string dir, MachineSummary row, Report report) = Create(DiagnozaTestConfig.Machine("premile", server.Port), DiagnozaSchedule.For(4, 1));
        await RunAsync(diag, report, dir);
        try
        {
            Assert.Equal(MachineStatus.Connected, diag.Result.Status);
            Assert.True(diag.Result.Frames.Count >= 2);
            Assert.Equal(64, diag.Result.Server!.Width);
            Assert.Equal(48, diag.Result.Server.Height);
            Assert.Equal("64x48", row.Screen);
            Assert.Contains(diag.Result.Frames, f => f.Pomiar.Width == 64 && f.Pomiar.Height == 48);
            Assert.Contains("zmienił rozmiar", File.ReadAllText(Path.Combine(dir, "out", "diagnoza", "premile", "dziennik.txt")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SerwerJednoklientowy_SharedTestWyrzucaPierwszego_PrzebiegTrwaIPonawia()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = DiagnozaTestConfig.Solid(32, 32, new Rgba(240, 240, 240)),
            KickPreviousClientOnNewConnection = true,
            ProtocolVersion = "3.3",
        });
        (MachineDiagnostic diag, string dir, _, Report report) = Create(DiagnozaTestConfig.Machine("qt-single-client", server.Port, "amm580"), DiagnozaSchedule.For(14, 1));
        await RunAsync(diag, report, dir);
        MachineDiagnosticResult r = diag.Result;
        try
        {
            Assert.Equal(MachineStatus.Connected, r.Status);
            Assert.Equal("3.3", r.Server!.ProtocolVersion);
            Assert.NotNull(r.SharedTest);
            Assert.True(r.SharedTest!.DrugiPolaczony);
            Assert.False(r.SharedTest.PierwszyPrzezyl);
            Assert.Contains("JEDNEGO klienta", r.SharedTest.Opis, StringComparison.Ordinal);
            Assert.Contains(r.Disconnects, d => !d.Przyczyna.StartsWith("planowe", StringComparison.Ordinal));
            Assert.True(r.Connections >= 3, $"połączeń {r.Connections}");
            Assert.Equal(MachineMode.Odpytywanie, MachineModeStore.FromResults([r])["qt-single-client"].Tryb);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MaszynaNieOdpowiada_SkanPortowIStatusNieOdpowiada()
    {
        await using var other = new FakeRfbServer(new FakeRfbServerOptions());
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        int closedPort = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        (MachineDiagnostic diag, string dir, MachineSummary row, Report report) = Create(DiagnozaTestConfig.Machine("cameo1", closedPort), DiagnozaSchedule.For(6, 1),
            initialSkip: "nie odpowiada (wyłączona?) — Nie udało się połączyć.");
        await RunAsync(diag, report, dir);
        try
        {
            Assert.Equal(MachineStatus.Unreachable, diag.Result.Status);
            Assert.False(diag.Result.EverConnected);
            Assert.True(diag.Result.TcpProbes.Count >= 1);
            Assert.NotNull(diag.Result.PortScan);
            Assert.Equal(PortScanner.DefaultScanPorts.Count, diag.Result.PortScan!.Count);
            Assert.Equal(MachineStatus.Unreachable, row.Status);
            Assert.Empty(diag.Result.Frames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SerwerWymagaHasla_BezHasla_StatusWymagaHaslaBezPonawianiaLogowania()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { SecurityTypes = [2], Password = "tajne" });
        (MachineDiagnostic diag, string dir, MachineSummary row, Report report) = Create(DiagnozaTestConfig.Machine("amm580", server.Port), DiagnozaSchedule.For(4, 1));
        await RunAsync(diag, report, dir);
        try
        {
            Assert.Equal(MachineStatus.PasswordRequired, diag.Result.Status);
            Assert.True(diag.Result.RequiresPassword);
            string log = File.ReadAllText(Path.Combine(dir, "out", "diagnoza", "amm580", "dziennik.txt"));
            Assert.Contains("nie ponawiam logowania", log, StringComparison.Ordinal);
            Assert.Equal(1, log.Split("RFB: handshake nieudany").Length - 1); // jedna próba logowania, potem tylko TCP
            Assert.Equal(MachineStatus.PasswordRequired, row.Status);
            Assert.Contains("hasło", DiagnozaConclusions.Build(diag.Result, DiagnozaTestConfig.Profiles()).CoDalej, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SerwerWymagaHasla_ZHaslem_Laczy()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { SecurityTypes = [2], Password = "tajne" });
        (MachineDiagnostic diag, string dir, _, Report report) = Create(DiagnozaTestConfig.Machine("amm580", server.Port), DiagnozaSchedule.For(3, 1), password: "tajne");
        await RunAsync(diag, report, dir);
        try
        {
            Assert.Equal(MachineStatus.Connected, diag.Result.Status);
            Assert.True(diag.Result.RequiresPassword);
            Assert.DoesNotContain("tajne", File.ReadAllText(Path.Combine(dir, "out", "diagnoza", "amm580", "dziennik.txt")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
