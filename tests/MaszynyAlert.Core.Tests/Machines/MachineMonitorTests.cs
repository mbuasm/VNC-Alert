using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorTests
{
    /// <summary>Profil bez obserwacji ekranu głównego HMI (sekcja mainScreen) — te testy liczą wywołania OCR i pełne klatki; sama obserwacja ekranu ma własne testy (<see cref="MachineMonitorMainScreenTests"/>).</summary>
    private static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null };

    private static MachineConfig Machine(bool enabled = true, ConnectionMode mode = ConnectionMode.Persistent) => new()
    {
        Id = "aidite1",
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
        Enabled = enabled,
        Mode = mode,
    };

    private static RgbaImage RedFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.RedText, "blad"));

    private static RgbaImage NormalFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));

    /// <summary>Magazyn przechwyceń zapamiętujący wywołania (bez dotykania dysku).</summary>
    private sealed class RecordingCaptureStore : ICaptureStore
    {
        public List<(string MachineId, string Tag, int Width, int Height)> Saved { get; } = [];

        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag)
        {
            Saved.Add((machineId, tag, image.Width, image.Height));
            return $"captures/{machineId}_{tag}.png";
        }

        public void Prune(TimeSpan retention)
        {
        }
    }

    /// <summary>
    /// Sterownik pętli monitora: zamiast czekać, przesuwa sztuczny zegar o żądany odstęp,
    /// zapisuje kolejne odstępy i po zadanej liczbie cykli anuluje token.
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

    private sealed record Harness(
        MachineMonitor Monitor,
        LoopDriver Driver,
        FakeClock Clock,
        AlertStateMachine Alerts,
        AckMemory Acks,
        FakeOcrEngine Ocr,
        RecordingCaptureStore Captures,
        List<AlertEvent> Events);

    private static Harness Build(
        int maxCycles,
        FakeScreenSource? source = null,
        FolderScreenSource? testOverride = null,
        FakeOcrEngine? ocr = null,
        TimingConfig? timing = null,
        MachineConfig? machine = null,
        Func<IScreenSource>? factory = null)
    {
        var clock = new FakeClock();
        var driver = new LoopDriver(maxCycles, clock);
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        TimingConfig t = timing ?? new TimingConfig();
        var alerts = new AlertStateMachine(clock, t, acks);
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var captures = new RecordingCaptureStore();
        FakeOcrEngine engine = ocr ?? new FakeOcrEngine();
        var mapper = new MessageMapper(TestPaths.ExampleRules);

        var monitor = new MachineMonitor(
            machine ?? Machine(),
            Profile,
            factory ?? (() => source ?? throw new InvalidOperationException("Brak źródła VNC w teście.")),
            testOverride,
            engine,
            mapper,
            alerts,
            clock,
            t,
            captures,
            logger: null,
            delay: driver.Delay);

        return new Harness(monitor, driver, clock, alerts, acks, engine, captures, events);
    }

    [Fact]
    public async Task RunAsync_PoDwochZTrzechCzerwonychCykli_TworzyAlertZOpisemZReguly()
    {
        var source = new FakeScreenSource("aidite1", RedFrame());
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("Failed to grab a tray");
        Harness h = Build(maxCycles: 3, source: source, ocr: ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("Tacka nie pobrana", alert.ShortText);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), alert.Key);
        Assert.Equal("captures/aidite1_blad.png", alert.CaptureFile);
        Assert.Contains(h.Events, e => e.Kind == AlertEventKind.Raised);

        (string machineId, string tag, int width, int height) = Assert.Single(h.Captures.Saved);
        Assert.Equal("aidite1", machineId);
        Assert.Equal("blad", tag);
        Assert.Equal(Profile.StatusBarRoi.Width, width);
        Assert.Equal(Profile.StatusBarRoi.Height, height);

        Assert.True(h.Monitor.Snapshot.RedConfirmed);
        Assert.True(h.Monitor.Snapshot.RedNow);
        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
        Assert.Equal("vnc", h.Monitor.Snapshot.Source);
    }

    [Fact]
    public async Task RunAsync_GdyPierwszyCyklJestCzerwony_JeszczeNieTworzyAlertu()
    {
        var source = new FakeScreenSource("aidite1", RedFrame());
        Harness h = Build(maxCycles: 1, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.True(h.Monitor.Snapshot.RedNow);
        Assert.False(h.Monitor.Snapshot.RedConfirmed);
    }

    [Fact]
    public async Task RunAsync_GdyOcrCiagleZwracaPusto_ProbujeNajwyzejTrzyRazy()
    {
        // pusty tekst
        // jest zawsze "nieczytelny" (TextReadability.IsReadable("") == false) i nigdy nie dopasowuje reguły,
        // więc KAŻDA z 3 prób epizodu (OcrMaxAttempts) robi DWA wywołania silnika OCR — pierwsze z maską
        // czerwieni (ImageOps.PrepareForOcr), drugie bez niej (ImageOps.PrepareTextForOcr) — licząc się jako
        // JEDNA próba (_ocrAttempts rośnie raz na parę wywołań, stąd wciąż "najwyżej trzy PRÓBY" w tytule).
        var source = new FakeScreenSource("aidite1", RedFrame());
        var ocr = new FakeOcrEngine();
        ocr.EnqueueEmpty(10);
        Harness h = Build(maxCycles: 10, source: source, ocr: ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(6, h.Ocr.CallCount);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("Błąd (nie odczytano treści)", alert.ShortText);
        Assert.Equal("text:empty", alert.Key.Discriminator);
    }

    [Fact]
    public async Task RunAsync_GdyOcrOdczytaTekstDopieroWTrzeciejProbie_AktualizujeOpisAlertu()
    {
        var source = new FakeScreenSource("aidite1", RedFrame());
        var ocr = new FakeOcrEngine();
        ocr.EnqueueEmpty(2).Enqueue("Failed to grab a tray");
        Harness h = Build(maxCycles: 6, source: source, ocr: ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(3, h.Ocr.CallCount);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), alert.Key);
        Assert.Equal("Tacka nie pobrana", alert.ShortText);
        Assert.Contains(h.Events, e => e.Kind == AlertEventKind.Updated);
        Assert.NotNull(h.Ocr.LastImage);
    }

    [Fact]
    public async Task RunAsync_GdySilnikOcrJestNiedostepny_WcaleGoNieWywoluje()
    {
        var source = new FakeScreenSource("aidite1", RedFrame());
        var ocr = new FakeOcrEngine { IsAvailable = false };
        Harness h = Build(maxCycles: 5, source: source, ocr: ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, h.Ocr.CallCount);
        Assert.Equal("Błąd (nie odczytano treści)", Assert.Single(h.Alerts.ActiveAlerts).ShortText);
    }

    [Fact]
    public async Task RunAsync_GdyIstniejePlikTestowegoEkranu_UzywaGoZamiastVnc()
    {
        using var dir = new TempDir("test-ekran");
        var testOverride = new FolderScreenSource("aidite1", dir.Path);
        PngCodec.EncodeToFile(RedFrame(), testOverride.FilePath);

        int factoryCalls = 0;
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("Failed to grab a tray");
        Harness h = Build(
            maxCycles: 3,
            testOverride: testOverride,
            ocr: ocr,
            factory: () =>
            {
                factoryCalls++;
                return new FakeScreenSource("aidite1", NormalFrame());
            });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, factoryCalls);
        Assert.Equal("plik", h.Monitor.Snapshot.Source);
        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
        Assert.Equal("plik", h.Monitor.Snapshot.Info?.Kind);
        Assert.Single(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task RunAsync_GdyPolaczenieSieNieUdaje_UstawiaStanBleduIOpisBledu()
    {
        var source = new FakeScreenSource("aidite1") { ConnectFailure = FakeScreenSource.RfbFailure("Brak odpowiedzi maszyny (test).") };
        Harness h = Build(maxCycles: 2, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(MachineConnectionState.Error, h.Monitor.Snapshot.State);
        Assert.False(string.IsNullOrWhiteSpace(h.Monitor.Snapshot.LastError));
        Assert.Contains("Brak odpowiedzi maszyny", h.Monitor.Snapshot.LastError!, StringComparison.Ordinal);
        Assert.NotNull(h.Monitor.Snapshot.OfflineSince);
    }

    [Fact]
    public async Task RunAsync_PoKolejnychNieudanychProbach_StosujeBackoff2_5_15_30_30()
    {
        var source = new FakeScreenSource("aidite1") { ConnectFailure = FakeScreenSource.RfbFailure() };
        Harness h = Build(maxCycles: 5, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(
            [
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            ],
            h.Driver.Delays);
        Assert.Equal(5, source.ConnectCount);
    }

    [Fact]
    public async Task RunAsync_PoTrzechMinutachBrakuLacznosci_MaszynaStanowTworzyAlertOffline()
    {
        var source = new FakeScreenSource("aidite1") { ConnectFailure = FakeScreenSource.RfbFailure() };
        Harness h = Build(maxCycles: 12, source: source);
        h.Driver.AfterCycle = _ => h.Alerts.Tick();

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert offline = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Offline, offline.Kind);
        Assert.Equal(AlertKey.Offline("aidite1"), offline.Key);
        Assert.False(offline.Sound);
    }

    [Fact]
    public async Task RunAsync_PoPowrocieLacznosci_ZamykaAlertBrakuLacznosci()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame()) { ConnectFailure = FakeScreenSource.RfbFailure() };
        Harness h = Build(maxCycles: 14, source: source);
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 11)
            {
                source.ConnectFailure = null;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Contains(h.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline);
        Assert.Contains(h.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Offline);
        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
        Assert.Null(h.Monitor.Snapshot.OfflineSince);
    }

    [Fact]
    public async Task RunAsync_PobieraPelnaKlatkeNaStarcieIPotemCoSzescdziesiatSekund()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 61, source: source);
        MachinePreviewFrame? first = null;
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 1) first = h.Monitor.PreviewFrame;
            if (cycle == 30) Assert.Same(first, h.Monitor.PreviewFrame);
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(2, source.CaptureFullCount);
        Assert.Equal(59, source.CaptureCount);
        Assert.NotNull(h.Monitor.LastFullFrame);
        Assert.NotNull(first);
        Assert.Equal("vnc", first.Source);
        Assert.True(h.Monitor.PreviewFrame!.MonotonicMs > first.MonotonicMs);
        Assert.NotSame(first, h.Monitor.PreviewFrame);
    }

    [Fact]
    public async Task RequestFullRefreshAsync_WymuszaPobraniePelnejKlatkiWNastepnymCyklu()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 4, source: source);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 2)
            {
                h.Monitor.RequestFullRefreshAsync();
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(2, source.CaptureFullCount);
    }

    [Fact]
    public async Task RunAsync_AktualizujeZnacznikCzasuMigawki()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 3, source: source);
        DateTimeOffset before = h.Monitor.Snapshot.UpdatedAt;

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(h.Monitor.Snapshot.UpdatedAt > before);
        Assert.NotNull(h.Monitor.Snapshot.LastFrameAt);
        Assert.Equal("aidite1", h.Monitor.Snapshot.MachineId);
        Assert.Equal("Aidite 1", h.Monitor.Snapshot.Name);
        Assert.Equal("opencnc", h.Monitor.Snapshot.Profile);
        Assert.Equal(9100, h.Monitor.Snapshot.Port);
    }

    [Fact]
    public async Task RunAsync_ZgłaszaZdarzenieStatusChangedPrzyZmianieStanu()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 3, source: source);
        var snapshots = new List<MachineStatusSnapshot>();
        h.Monitor.StatusChanged += snapshots.Add;

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.NotEmpty(snapshots);
        Assert.Equal(MachineConnectionState.Connected, snapshots[0].State);
    }

    [Fact]
    public async Task RunAsync_PoAnulowaniuTokenu_KonczySieBezWyjatku()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 100, source: source);
        await h.Driver.Cts.CancelAsync();

        // Zachowanie utrwalone: anulowanie kończy pętlę normalnie (bez OperationCanceledException na zewnątrz).
        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        // Token anulowany przed startem — pętla nie wykonała ani jednego cyklu.
        Assert.Equal(0, source.ConnectCount);
        Assert.Empty(h.Driver.Delays);
    }

    [Fact]
    public async Task RunAsync_PoZakonczeniuPetli_ZwalniaZrodloObrazu()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 3, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(1, source.ConnectCount);
    }

    [Fact]
    public async Task RunAsync_DlaMaszynyWylaczonej_UstawiaStanDisabledIniczegoNiePobiera()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 5, source: source, machine: Machine(enabled: false));

        Task run = h.Monitor.RunAsync(h.Driver.Cts.Token);
        await h.Driver.Cts.CancelAsync();
        await run;

        Assert.Equal(MachineConnectionState.Disabled, h.Monitor.Snapshot.State);
        Assert.Equal("-", h.Monitor.Snapshot.Source);
        Assert.Equal(0, source.ConnectCount);
    }

    [Fact]
    public async Task RunAsync_WTrybiePolling_UzywaDluzszegoOdstepuIZawszePobieraPelnaKlatke()
    {
        var source = new FakeScreenSource("aidite1", NormalFrame());
        Harness h = Build(maxCycles: 3, source: source, machine: Machine(mode: ConnectionMode.Polling));

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.All(h.Driver.Delays, d => Assert.Equal(TimeSpan.FromSeconds(5), d));
        Assert.Equal(3, source.CaptureFullCount);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task RunAsync_GdyCzerwienZniknie_ZglaszaWyczyszczenieEpizodu()
    {
        var source = new FakeScreenSource("aidite1", RedFrame());
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("Failed to grab a tray");
        Harness h = Build(maxCycles: 40, source: source);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                source.Frame = NormalFrame();
            }

            h.Alerts.Tick();
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.False(h.Monitor.Snapshot.RedNow);
        Assert.False(h.Monitor.Snapshot.RedConfirmed);
        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Contains(h.Events, e => e.Kind == AlertEventKind.Closed);
    }

    [Fact]
    public async Task RunAsync_GdyZrodloVncRzucaScreenSourceException_OznaczaMaszynyJakoOffline()
    {
        // ScreenSourceException ze źródła VNC (IScreenSource inne niż VncScreenSource) jest
        // łapane na tych samych zasadach co RfbException/IOException/SocketException.
        var source = new FakeScreenSource("aidite1") { ConnectFailure = FakeScreenSource.ScreenFailure() };
        Harness h = Build(maxCycles: 3, source: source);
        h.Driver.AfterCycle = _ => h.Alerts.Tick();

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(MachineConnectionState.Error, h.Monitor.Snapshot.State);
        Assert.False(string.IsNullOrWhiteSpace(h.Monitor.Snapshot.LastError));
        Assert.NotNull(h.Monitor.Snapshot.OfflineSince);
    }

    [Fact]
    public async Task RunAsync_GdyPlikTestowegoEkranuJestUszkodzony_NieOznaczaMaszynyJakoOffline()
    {
        // Ścieżka "testOverride" ma pozostać łagodna: uszkodzony/niekompletny plik test-ekran
        // (FileExists==true, ale dekodowanie rzuca ScreenSourceException) to tylko pominięty cykl —
        // NIE utrata łączności z maszyną VNC (patrz FolderScreenSource.DecodeOrThrow).
        using var dir = new TempDir("test-ekran");
        var testOverride = new FolderScreenSource("aidite1", dir.Path);
        File.WriteAllBytes(testOverride.FilePath, [0x00, 0x01, 0x02, 0x03]); // nie jest poprawnym PNG

        Harness h = Build(maxCycles: 3, testOverride: testOverride);
        h.Driver.AfterCycle = _ => h.Alerts.Tick();

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(MachineConnectionState.Error, h.Monitor.Snapshot.State);
        Assert.Contains("pliku testowego", h.Monitor.Snapshot.LastError);
        Assert.Null(h.Monitor.Snapshot.OfflineSince);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }
}
