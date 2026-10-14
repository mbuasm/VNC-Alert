using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorRegressionTests
{
    /// <summary>Profil bez obserwacji ekranu głównego HMI (sekcja mainScreen) — te testy liczą wywołania OCR i pełne klatki; sama obserwacja ekranu ma własne testy (<see cref="MachineMonitorMainScreenTests"/>).</summary>
    private static readonly ScreenProfile OpenCncProfile = TestPaths.Profile("opencnc") with { MainScreen = null };

    private static readonly ScreenProfile RoiPozaEkranemProfile =
        TestPaths.Profile("amm580") with
        {
            StatusBarRoi = new RectInt(0, 728, 1024, 40),
            Idle = null,
            MainScreen = null,
        };

    private static MachineConfig Machine(
        string id = "aidite1",
        bool enabled = true,
        ConnectionMode mode = ConnectionMode.Persistent,
        string profile = "opencnc") => new()
    {
        Id = id,
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = profile,
        Enabled = enabled,
        Mode = mode,
    };

    private static RgbaImage RedFrame(ScreenProfile profile) => SyntheticScreens.FullFrame(
        profile,
        SyntheticScreens.StatusBar(
            profile.StatusBarRoi.Width, profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.RedText, "blad"));

    private static RgbaImage NormalFrame(ScreenProfile profile) => SyntheticScreens.FullFrame(
        profile,
        SyntheticScreens.StatusBar(
            profile.StatusBarRoi.Width, profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));

    /// <summary>Jednolita klatka o zadanym rozmiarze — do testów ROI leżącego poza ekranem maszyny.</summary>
    private static RgbaImage BlankFrame(int width, int height)
    {
        var frame = new RgbaImage(width, height);
        frame.Fill(SyntheticScreens.DarkBackground);
        return frame;
    }

    /// <summary>
    /// Sterownik pętli monitora: zamiast czekać, przesuwa sztuczny zegar o żądany odstęp, zapisuje
    /// kolejne odstępy i po zadanej liczbie cykli anuluje token (jak w <see cref="MachineMonitorTests"/>).
    /// </summary>
    private sealed class LoopDriver
    {
        private readonly int _maxCycles;
        private int _cycles;

        public LoopDriver(int maxCycles, FakeClock clock)
        {
            _maxCycles = maxCycles;
            Clock = clock;
        }

        public FakeClock Clock { get; }

        public CancellationTokenSource Cts { get; } = new();

        public List<TimeSpan> Delays { get; } = [];

        public Action<int>? AfterCycle { get; set; }

        public Task Delay(TimeSpan interval, CancellationToken ct)
        {
            Delays.Add(interval);
            Clock.Advance(interval);
            _cycles++;
            AfterCycle?.Invoke(_cycles);
            if (_cycles >= _maxCycles)
            {
                Cts.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Wspólny „świat” testu: jeden zegar, jedna maszyna stanów alertów i lista zdarzeń. Może
    /// obsłużyć KILKA kolejnych monitorów (scenariusz „restart nadzorcy”).
    /// </summary>
    private sealed class World
    {
        public World(TimingConfig? timing = null)
        {
            Timing = timing ?? new TimingConfig();
            Acks = new AckMemory(Clock, TimeSpan.FromHours(Timing.AckMemoryHours));
            Alerts = new AlertStateMachine(Clock, Timing, Acks);
            Alerts.Changed += Events.Add;
        }

        public FakeClock Clock { get; } = new();

        public TimingConfig Timing { get; }

        public AckMemory Acks { get; }

        public AlertStateMachine Alerts { get; }

        public List<AlertEvent> Events { get; } = [];

        public RecordingLoggerFactory Loggers { get; } = new();

        public MessageMapper Mapper { get; } = new(TestPaths.ExampleRules);
    }

    private sealed record Harness(MachineMonitor Monitor, LoopDriver Driver, World World)
    {
        public Task RunAsync() => Monitor.RunAsync(Driver.Cts.Token);
    }

    private static Harness Build(
        World world,
        int maxCycles,
        FakeScreenSource? source = null,
        Func<IScreenSource>? factory = null,
        FolderScreenSource? testOverride = null,
        FakeOcrEngine? ocr = null,
        MachineConfig? machine = null,
        ScreenProfile? profile = null,
        bool tickAlertsEachCycle = false)
    {
        var driver = new LoopDriver(maxCycles, world.Clock);
        if (tickAlertsEachCycle)
        {
            driver.AfterCycle = _ => world.Alerts.Tick();
        }

        var monitor = new MachineMonitor(
            machine ?? Machine(),
            profile ?? OpenCncProfile,
            factory ?? (() => source ?? throw new InvalidOperationException("Brak źródła VNC w teście.")),
            testOverride,
            ocr ?? new FakeOcrEngine(),
            world.Mapper,
            world.Alerts,
            world.Clock,
            world.Timing,
            new NullCaptureStore(),
            world.Loggers.CreateLogger<MachineMonitor>(),
            driver.Delay);

        return new Harness(monitor, driver, world);
    }


    [Fact]
    public async Task RunAsync_GdySilnikOcrRzucaWyjatekWKazdymWywolaniu_TworzyAlertBezTresciIPetlaDzialaDalej()
    {
        var source = new FakeScreenSource("aidite1", RedFrame(OpenCncProfile));
        var ocr = new FakeOcrEngine { Failure = () => new InvalidOperationException("Silnik OCR padł (test).") };
        var world = new World();
        Harness h = Build(world, maxCycles: 8, source: source, ocr: ocr);

        await h.RunAsync();

        Alert alert = Assert.Single(world.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.Equal("Błąd (nie odczytano treści)", alert.ShortText);
        Assert.Equal("text:empty", alert.Key.Discriminator);
        Assert.Contains(world.Events, e => e.Kind == AlertEventKind.Raised);

        Assert.True(h.Monitor.Snapshot.RedConfirmed);
        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
        Assert.Null(h.Monitor.Snapshot.LastError);

        // Pętla przeżyła wszystkie cykle (nie wypadła po pierwszym wyjątku) i nie zalogowała
        // "nieoczekiwanego błędu" — wyjątek OCR jest obsłużony lokalnie, po polsku.
        Assert.Equal(8, h.Driver.Delays.Count);
        Assert.Empty(world.Loggers.Containing("Nieoczekiwany błąd"));
        Assert.NotEmpty(world.Loggers.Containing("Błąd silnika OCR"));

        // Nieudane próby ZUŻYWAJĄ limit (inaczej OCR byłby wołany w każdym cyklu epizodu).
        Assert.True(
            ocr.CallCount <= world.Timing.OcrMaxAttempts,
            $"OCR wywołany {ocr.CallCount} razy, a limit to {world.Timing.OcrMaxAttempts}.");
        Assert.Equal(world.Timing.OcrMaxAttempts, ocr.CallCount);
    }

    [Fact]
    public async Task RunAsync_GdyOcrMaxAttemptsWynosiZero_TworzyAlertBezTresciNieWolajacOcr()
    {
        var source = new FakeScreenSource("aidite1", RedFrame(OpenCncProfile));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("Failed to grab a tray");
        var world = new World(new TimingConfig { OcrMaxAttempts = 0 });
        Harness h = Build(world, maxCycles: 4, source: source, ocr: ocr);

        await h.RunAsync();

        Assert.Equal(0, ocr.CallCount);
        Alert alert = Assert.Single(world.Alerts.ActiveAlerts);
        Assert.Equal("Błąd (nie odczytano treści)", alert.ShortText);
        Assert.Equal("text:empty", alert.Key.Discriminator);
        Assert.True(h.Monitor.Snapshot.RedConfirmed);
    }

    [Fact]
    public async Task RunAsync_GdyOcrRzucaDwaRazyAPotemOdczytaTekst_KonczyZOpisemZReguly()
    {
        var source = new FakeScreenSource("aidite1", RedFrame(OpenCncProfile));
        var ocr = new FakeOcrEngine
        {
            Failure = () => new InvalidOperationException("Silnik OCR padł (test)."),
            FailingCalls = 2,
        };
        ocr.Enqueue("Failed to grab a tray");
        var world = new World();
        Harness h = Build(world, maxCycles: 6, source: source, ocr: ocr);

        await h.RunAsync();

        Assert.Equal(3, ocr.CallCount);
        Alert alert = Assert.Single(world.Alerts.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), alert.Key);
        Assert.Equal("Tacka nie pobrana", alert.ShortText);
        Assert.Contains(world.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Contains(world.Events, e => e.Kind == AlertEventKind.Updated);
    }


    [Fact]
    public async Task RunAsync_GdyRoiProfiluLezyPozaEkranemMaszyny_ZglaszaBladKonfiguracjiZamiastUtratyLacznosci()
    {
        var source = new FakeScreenSource("amm580", BlankFrame(1024, 600));
        var world = new World(new TimingConfig { PollIntervalMs = 5000, FullFrameIntervalMs = 60000 });
        Harness h = Build(
            world,
            maxCycles: 50, // 50 × 5 s = 250 s > offlineAlertAfterMs (180 s)
            source: source,
            machine: Machine(id: "amm580", profile: "amm580"),
            profile: RoiPozaEkranemProfile,
            tickAlertsEachCycle: true);

        await h.RunAsync();

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.Equal(MachineConnectionState.Error, snapshot.State);
        Assert.NotNull(snapshot.LastError);
        Assert.Contains("poza ekranem", snapshot.LastError!, StringComparison.Ordinal);
        Assert.Contains("1024×600", snapshot.LastError!, StringComparison.Ordinal);
        Assert.Contains("statusBarRoi", snapshot.LastError!, StringComparison.Ordinal);

        // To NIE jest offline: brak znacznika i brak alertu "brak łączności" nawet po ponad 4 minutach.
        Assert.Null(snapshot.OfflineSince);
        Assert.Empty(world.Alerts.ActiveAlerts);
        Assert.DoesNotContain(world.Events, e => e.Alert.Kind == AlertKind.Offline);

        // Brak reconnectów: jedno połączenie na cały czas życia pętli (nie jedno na cykl).
        Assert.Equal(1, source.ConnectCount);
        Assert.Equal(50, source.CaptureFullCount);
        Assert.Equal(0, source.CaptureCount);

        // Komunikat w logu dokładnie raz, mimo 50 cykli.
        IReadOnlyList<LogEntry> logged = world.Loggers.Containing("statusBarRoi");
        Assert.Single(logged);
        Assert.Equal(LogLevel.Error, logged[0].Level);
    }

    [Fact]
    public async Task RunAsync_GdyEkranMaszynyUrosnieDoOczekiwanegoRozmiaru_WracaDoStanuPolaczono()
    {
        var source = new FakeScreenSource("amm580", BlankFrame(1024, 600));
        var world = new World();
        Harness h = Build(
            world,
            maxCycles: 3,
            source: source,
            machine: Machine(id: "amm580", profile: "amm580"),
            profile: RoiPozaEkranemProfile);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 1)
            {
                Assert.Equal(MachineConnectionState.Error, h.Monitor.Snapshot.State);
                source.Frame = BlankFrame(1024, 768);
            }
        };

        await h.RunAsync();

        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
        Assert.Null(h.Monitor.Snapshot.LastError);
        Assert.Equal(1, source.ConnectCount);
    }


    [Fact]
    public async Task RunAsync_GdyZrodloNieZglaszaZmianPrzezWieleCykli_NieZamykaTrwajacegoAlertu()
    {
        var source = new FakeScreenSource("aidite1", RedFrame(OpenCncProfile)) { TryCaptureReturnsNull = true };
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("Failed to grab a tray");
        var world = new World();
        Harness h = Build(world, maxCycles: 45, source: source, ocr: ocr, tickAlertsEachCycle: true);

        await h.RunAsync();

        // 45 cykli × 1 s > episodeCloseAfterMs (30 s) — alert nadal aktywny.
        Alert alert = Assert.Single(world.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.Equal(AlertStatus.Active, alert.Status);
        Assert.Equal("Tacka nie pobrana", alert.ShortText);
        Assert.DoesNotContain(world.Events, e => e.Kind == AlertEventKind.Closed);

        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
        Assert.True(h.Monitor.Snapshot.RedConfirmed);
        Assert.Null(h.Monitor.Snapshot.OfflineSince);
        Assert.DoesNotContain(world.Events, e => e.Alert.Kind == AlertKind.Offline);

        // Pierwszy cykl to pełna klatka, każdy kolejny to żądanie przyrostowe bez odpowiedzi.
        Assert.Equal(1, source.CaptureFullCount);
        Assert.Equal(44, source.TryCaptureCount);
        Assert.Equal(0, source.CaptureCount);
        Assert.Equal(1, source.ConnectCount);
    }


    [Fact]
    public async Task RunAsync_GdyNowyMonitorPrzejmujeMaszyneOnline_ZamykaAlertBrakuLacznosciPoprzednika()
    {
        var world = new World();

        var failing = new FakeScreenSource("aidite1") { ConnectFailure = FakeScreenSource.RfbFailure() };
        Harness first = Build(world, maxCycles: 10, source: failing, tickAlertsEachCycle: true);
        await first.RunAsync();

        Alert offline = Assert.Single(world.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Offline, offline.Kind);
        Assert.Equal(AlertStatus.Active, offline.Status);

        // Druga generacja monitora (po "restarcie nadzorcy") — ta sama maszyna stanów alertów, działające źródło.
        var healthy = new FakeScreenSource("aidite1", NormalFrame(OpenCncProfile));
        Harness second = Build(world, maxCycles: 1, source: healthy, tickAlertsEachCycle: true);
        await second.RunAsync();

        Assert.Empty(world.Alerts.ActiveAlerts);
        Assert.Contains(
            world.Events,
            e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Offline);
        Assert.Equal(MachineConnectionState.Connected, second.Monitor.Snapshot.State);
        Assert.Null(second.Monitor.Snapshot.OfflineSince);
    }

    [Fact]
    public async Task RunAsync_GdyPrzyMaszynieOfflinePojawiSiePlikTestowegoEkranu_ZamykaAlertBrakuLacznosci()
    {
        using var dir = new TempDir("test-ekran");
        var testOverride = new FolderScreenSource("aidite1", dir.Path);

        var world = new World();
        var failing = new FakeScreenSource("aidite1") { ConnectFailure = FakeScreenSource.RfbFailure() };
        Harness h = Build(world, maxCycles: 12, source: failing, testOverride: testOverride, tickAlertsEachCycle: true);
        h.Driver.AfterCycle = cycle =>
        {
            world.Alerts.Tick();
            if (cycle == 10)
            {
                Assert.Contains(world.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Offline);
                PngCodec.EncodeToFile(NormalFrame(OpenCncProfile), testOverride.FilePath);
            }
        };

        await h.RunAsync();

        Assert.Empty(world.Alerts.ActiveAlerts);
        Assert.Contains(
            world.Events,
            e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Offline);
        Assert.Equal("plik", h.Monitor.Snapshot.Source);
        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
        Assert.Null(h.Monitor.Snapshot.OfflineSince);
    }


    [Theory]
    [InlineData("argument")]
    [InlineData("invalid-operation")]
    [InlineData("disposed")]
    public async Task RunAsync_GdyZrodloRzucaDowolnyWyjatek_ZwalniaJeITworzyNoweWKolejnymCyklu(string kind)
    {
        Func<Exception> failure = kind switch
        {
            "argument" => () => new ArgumentException("Zły argument (test)."),
            "invalid-operation" => () => new InvalidOperationException("Zły stan (test)."),
            _ => () => new ObjectDisposedException("RfbClient"),
        };

        var created = new List<FakeScreenSource>();
        var world = new World(new TimingConfig { ReconnectBackoffMs = [1000] });
        Harness h = Build(
            world,
            maxCycles: 5,
            factory: () =>
            {
                var s = new FakeScreenSource("aidite1", NormalFrame(OpenCncProfile)) { CaptureFailure = failure };
                created.Add(s);
                return s;
            },
            tickAlertsEachCycle: true);

        await h.RunAsync();

        Assert.Equal(MachineConnectionState.Error, h.Monitor.Snapshot.State);
        Assert.False(string.IsNullOrWhiteSpace(h.Monitor.Snapshot.LastError));
        Assert.NotNull(h.Monitor.Snapshot.OfflineSince);

        // Jedno źródło na cykl — i każde zwolnione dokładnie raz (brak wycieku połączeń).
        Assert.Equal(5, created.Count);
        Assert.All(created, s => Assert.Equal(1, s.DisposeCount));
        Assert.DoesNotContain(created, s => s.DisposeCount == 0);
        Assert.All(created, s => Assert.Equal(1, s.ConnectCount));

        // Pętla przeżyła wszystkie cykle i nie zalogowała "nieoczekiwanego błędu" (wyjątek jest obsłużony
        // jako awaria połączenia, z komunikatem po polsku).
        Assert.Equal(5, h.Driver.Delays.Count);
        Assert.Empty(world.Loggers.Containing("Nieoczekiwany błąd"));
    }

    [Fact]
    public async Task RunAsync_GdyZrodloRzucaWKazdymCyklu_NigdyNieZyjeWiecejNizJednoZrodloNaraz()
    {
        var created = new List<FakeScreenSource>();
        int maxAlive = 0;
        var world = new World(new TimingConfig { ReconnectBackoffMs = [1000] });
        Harness h = Build(
            world,
            maxCycles: 6,
            factory: () =>
            {
                var s = new FakeScreenSource("aidite1", NormalFrame(OpenCncProfile))
                {
                    CaptureFailure = () => new ObjectDisposedException("RfbClient"),
                };
                created.Add(s);
                maxAlive = Math.Max(maxAlive, created.Count(x => x.DisposeCount == 0));
                return s;
            });

        await h.RunAsync();

        Assert.True(maxAlive <= 1, $"W pętli żyło naraz {maxAlive} źródeł obrazu (dopuszczalne: 1).");
        Assert.Equal(6, created.Count);
        Assert.All(created, s => Assert.Equal(1, s.DisposeCount));
    }


    [Fact]
    public async Task RunAsync_GdySerwerVncWymagaHasla_LogujeOstrzezenieBezSladuStosu()
    {
        var source = new FakeScreenSource("aidite1")
        {
            ConnectFailure = () => new RfbAuthenticationException("Serwer wymaga hasła — zapisz je w Ustawieniach."),
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [1000] });
        Harness h = Build(world, maxCycles: 3, source: source);

        await h.RunAsync();

        LogEntry entry = world.Loggers.Containing("Błąd VNC maszyny").First();
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("Serwer wymaga hasła", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_GdyPolaczenieZawodziZInnegoPowodu_LogujeOstrzezenieZWyjatkiem()
    {
        var source = new FakeScreenSource("aidite1")
        {
            ConnectFailure = () => new IOException("Zerwane połączenie (test)."),
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [1000] });
        Harness h = Build(world, maxCycles: 3, source: source);

        await h.RunAsync();

        LogEntry entry = world.Loggers.Containing("Błąd VNC maszyny").First();
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.NotNull(entry.Exception);
        Assert.IsType<IOException>(entry.Exception);
    }


    [Fact]
    public async Task RunAsync_TenSamBladPolaczenia_LogowanyNajwyzejRazNa5Minut()
    {
        var source = new FakeScreenSource("aidite1")
        {
            ConnectFailure = () => new IOException("Nie udało się połączyć z 10.0.0.1:5900 w ciągu 10s."),
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [30000] });
        Harness h = Build(world, maxCycles: 40, source: source); // 40 × 30 s = 20 min

        await h.RunAsync();

        LogEntry[] warnings = world.Loggers.Containing("Błąd VNC maszyny").ToArray();
        Assert.InRange(warnings.Length, 2, 5);
        Assert.NotNull(warnings[0].Exception);
        Assert.All(warnings.Skip(1), w => Assert.Null(w.Exception));
        Assert.Contains("powtórzono", warnings[1].Message, StringComparison.Ordinal);
    }


    [Fact]
    public async Task RunAsync_OdrzuconeHaslo_KolejneProbyCorazRzadziejDoLimitu()
    {
        var source = new FakeScreenSource("aidite1")
        {
            ConnectFailure = () => new RfbAuthenticationException("Serwer 127.0.0.1:5901 odrzucił hasło VNC: Authentication failed"),
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [2000, 5000, 15000, 30000] });
        Harness h = Build(world, maxCycles: 9, source: source);

        await h.RunAsync();

        Assert.Equal(
            new[] { 30, 60, 120, 240, 480, 960, 1800, 1800 }.Select(s => TimeSpan.FromSeconds(s)),
            h.Driver.Delays.Take(8));
        Assert.Contains("hasło", h.Monitor.Snapshot.LastError, StringComparison.Ordinal);
        Assert.Contains("Kolejna próba za 30 min", h.Monitor.Snapshot.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ZwykleZerwanie_BackoffBezZmian()
    {
        // Wydłużanie dotyczy tylko odrzuconego hasła — zwykła awaria sieci zostaje przy timing.reconnectBackoffMs.
        var source = new FakeScreenSource("aidite1")
        {
            ConnectFailure = () => new IOException("Odmowa połączenia (test)."),
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [2000, 5000, 15000, 30000] });
        Harness h = Build(world, maxCycles: 6, source: source);

        await h.RunAsync();

        Assert.Equal(
            new[] { 2, 5, 15, 30, 30 }.Select(s => TimeSpan.FromSeconds(s)),
            h.Driver.Delays.Take(5));
    }

    [Fact]
    public async Task RunAsync_ZmianaTresciBledu_LogowanaOdRazu()
    {
        int attempt = 0;
        var source = new FakeScreenSource("aidite1")
        {
            ConnectFailure = () => ++attempt <= 3
                ? new IOException("Odmowa połączenia (test).")
                : new RfbAuthenticationException("Serwer odrzucił hasło VNC (test)."),
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [1000] });
        Harness h = Build(world, maxCycles: 6, source: source);

        await h.RunAsync();

        LogEntry[] warnings = world.Loggers.Containing("Błąd VNC maszyny").ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("Odmowa połączenia", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("odrzucił hasło", warnings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SerwerZmniejszylEkranPodROI_ZglaszaObszarPozaEkranemBezRozlaczania()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame(OpenCncProfile));
        var world = new World(new TimingConfig { ReconnectBackoffMs = [1000] });
        Harness h = Build(world, maxCycles: 6, source: source);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 2) source.CaptureFailure = () => new RfbScreenResizedException(400, 300);
        };

        await h.RunAsync();

        Assert.Empty(world.Loggers.Containing("Błąd VNC maszyny"));
        Assert.Equal(1, source.ConnectCount);
        Assert.Contains("poza ekranem maszyny (400×300)", h.Monitor.Snapshot.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AnulowanieZeZrodlaBezZamykaniaProgramu_TraktowaneJakAwariaPolaczenia()
    {
        var source = new FakeScreenSource("aidite1")
        {
            ConnectFailure = () => new OperationCanceledException("Limit czasu źródła (test)."),
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [7000] });
        Harness h = Build(world, maxCycles: 3, source: source);

        await h.RunAsync();

        Assert.Empty(world.Loggers.Containing("Nieoczekiwany błąd"));
        Assert.NotEmpty(world.Loggers.Containing("Błąd VNC maszyny"));
        Assert.Contains(TimeSpan.FromMilliseconds(7000), h.Driver.Delays);
    }

    [Fact]
    public async Task RunAsync_PowrotPoSeriiBledow_ZapisujeJedenWpisOPrzywroceniu()
    {
        int attempt = 0;
        var source = new FakeScreenSource("aidite1", NormalFrame(OpenCncProfile));
        source.ConnectFailure = () =>
        {
            if (++attempt >= 4) source.ConnectFailure = null; // czwarta próba jeszcze zawodzi, piąta się łączy
            return new IOException("Odmowa połączenia (test).");
        };
        var world = new World(new TimingConfig { ReconnectBackoffMs = [1000] });
        Harness h = Build(world, maxCycles: 8, source: source);

        await h.RunAsync();

        LogEntry restored = Assert.Single(world.Loggers.Containing("przywrócone"));
        Assert.Equal(LogLevel.Information, restored.Level);
        Assert.Contains("4", restored.Message, StringComparison.Ordinal);
    }
}
