using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorMainScreenTests
{
    private const int WordHeight = 14;

    /// <summary>Profil bez obserwacji postoju (idle = null), żeby jedyne wywołania OCR pochodziły ze sprawdzania ekranu.</summary>
    private static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with
    {
        Idle = null,
        MainScreen = new MainScreenWatchProfile(),
    };

    private static readonly TimingConfig Timing = new()
    {
        MainScreenCheckIntervalMs = 5000,
        WrongScreenAlertAfterMs = 10000,
    };

    private static MachineConfig Machine() => new()
    {
        Id = "aidite1",
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
    };

    private static OcrWord Word(string text, int x, int y, int width, int lineIndex = 0)
        => new(text, new RectInt(x, y, width, WordHeight), lineIndex);

    private static OcrResult MainScreenOcr() => OcrResult.FromWords(
    [
        Word("Milling", 10, 30, 70),
        Word("Status", 90, 30, 60),
        Word("79.08%", 170, 30, 70),
    ]);

    private static OcrResult ProductionInfoOcr() => OcrResult.FromWords(
    [
        Word("Production", 10, 30, 100),
        Word("info", 120, 30, 40),
    ]);

    private static RgbaImage NormalFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));

    /// <summary>Ten sam pasek statusu, ale inne tło reszty ekranu — „operator przełączył zakładkę HMI”.</summary>
    private static RgbaImage OtherTabFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"),
        SyntheticScreens.DarkBackground);

    private static RgbaImage RedFrame() => SyntheticScreens.FullFrame(
        Profile, SyntheticScreens.StatusBar(Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.RedText, "ALARM"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingScreen_BreaksIdleCountdownAndRequiresTwoNewMinutes(bool unknown)
    {
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        var progress = new RectInt(10, 110, 100, 20);
        var profile = Profile with { Idle = new IdleWatchProfile { Mode = ProgressMode.Bar, ProgressRoi = progress } };
        Harness h = Build(235, ocr, profile);
        var bar = new RgbaImage(progress.Width, progress.Height);
        bar.Fill(new Rgba(40, 200, 60));
        h.Source.Frame!.Blit(bar, progress.X, progress.Y);
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 60) ocr.DefaultResult = unknown ? OcrResult.Empty : ProductionInfoOcr();
            if (cycle == 100) ocr.DefaultResult = MainScreenOcr();
            if (cycle == 99) Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
            if (cycle == 220) Assert.DoesNotContain(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        };
        await h.Monitor.RunAsync(h.Driver.Cts.Token);
        Alert idle = Assert.Single(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.True(idle.StartedAt >= FakeClock.DefaultStart.AddSeconds(100));
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.MachineIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenOrUnknownScreen_DoesNotCloseExistingError(bool unknown)
    {
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(65, ocr);
        h.Source.Frame = RedFrame();
        Alert? initial = null;
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 3)
            {
                initial = Assert.Single(h.Alerts.ActiveAlerts);
                h.Source.Frame = NormalFrame();
                ocr.DefaultResult = unknown ? OcrResult.Empty : ProductionInfoOcr();
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert remaining = Assert.Single(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
        Assert.Equal(initial!.StartedAt, remaining.StartedAt);
        Assert.Equal(AlertStatus.Active, remaining.Status);
        Assert.True(h.Monitor.Snapshot.ScreenObservationUnavailable);
        Assert.Equal(unknown ? null : (bool?)false, h.Monitor.Snapshot.MainScreenVisible);
        // Utrzymanie alarmu nie udaje kolejnych odczytów błędu z zasłoniętego ekranu.
        Assert.True(remaining.LastSeenAt < h.Clock.UtcNow.AddSeconds(-30));
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Error);
    }

    [Fact]
    public async Task ReturningToNormalScreen_RequiresFullClearIntervalAfterReturn()
    {
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(95, ocr);
        h.Source.Frame = RedFrame();
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 3)
            {
                h.Source.Frame = NormalFrame();
                ocr.DefaultResult = ProductionInfoOcr();
            }
            if (cycle == 60) ocr.DefaultResult = MainScreenOcr();
            if (cycle == 61 || cycle == 89)
                Assert.Contains(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);
        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Error);
    }

    [Fact]
    public async Task RecognizedMenu_WithRedDecoration_DoesNotRaiseMachineError()
    {
        Harness h = Build(20, new FakeOcrEngine { DefaultResult = ProductionInfoOcr() });
        h.Source.Frame = RedFrame();
        await h.Monitor.RunAsync(h.Driver.Cts.Token);
        Assert.DoesNotContain(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
        Assert.Contains(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.WrongScreen);
    }

    [Fact]
    public async Task StatusBarAlwaysVisible_NaInnymEkranie_KolorNadalPodnosiAlarm()
    {
        var profile = Profile with
        {
            MainScreen = new MainScreenWatchProfile { StatusBarAlwaysVisible = true },
        };
        Harness h = Build(20, new FakeOcrEngine { DefaultResult = ProductionInfoOcr() }, profile);
        h.Source.Frame = RedFrame();

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Contains(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
        // Operator i tak ma wrócić na ekran z procentem, więc przypomnienie o ekranie zostaje.
        Assert.Contains(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.WrongScreen);
        Assert.False(h.Monitor.Snapshot.MainScreenVisible);
    }

    [Fact]
    public async Task StatusBarAlwaysVisible_NaInnymEkranie_NadalNieZamykaBleduBezCzerwieni()
    {
        var profile = Profile with
        {
            MainScreen = new MainScreenWatchProfile { StatusBarAlwaysVisible = true },
        };
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(65, ocr, profile);
        h.Source.Frame = RedFrame();
        Alert? initial = null;
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 3)
            {
                initial = Assert.Single(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
                h.Source.Frame = NormalFrame();
                ocr.DefaultResult = ProductionInfoOcr();
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert remaining = Assert.Single(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
        Assert.Equal(initial!.StartedAt, remaining.StartedAt);
        Assert.True(h.Monitor.Snapshot.ScreenObservationUnavailable);
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Error);
    }

    /// <summary>
    /// Obserwacja postoju zostaje wstrzymana — na innej zakładce procentu NIE widać,
    /// więc <c>statusBarAlwaysVisible</c> nie może udawać, że postęp został odczytany.
    /// </summary>
    [Fact]
    public async Task StatusBarAlwaysVisible_NaInnymEkranie_NieLiczyPostoju()
    {
        var progress = new RectInt(10, 110, 100, 20);
        var profile = Profile with
        {
            Idle = new IdleWatchProfile { Mode = ProgressMode.Bar, ProgressRoi = progress },
            MainScreen = new MainScreenWatchProfile { StatusBarAlwaysVisible = true },
        };
        var ocr = new FakeOcrEngine { DefaultResult = ProductionInfoOcr() };
        Harness h = Build(200, ocr, profile);
        var bar = new RgbaImage(progress.Width, progress.Height);
        bar.Fill(new Rgba(40, 200, 60));
        h.Source.Frame!.Blit(bar, progress.X, progress.Y);
        h.Driver.AfterCycle = _ => h.Alerts.Tick();

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.DoesNotContain(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
    }

    [Fact]
    public async Task NoOcr_ColorStillRaisesError_ButBlankFrameDoesNotProveRepair()
    {
        Harness h = Build(65, new FakeOcrEngine { IsAvailable = false });
        h.Source.Frame = RedFrame();
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 3) h.Source.Frame = NormalFrame();
        };
        await h.Monitor.RunAsync(h.Driver.Cts.Token);
        Assert.Equal(AlertKind.Error, Assert.Single(h.Alerts.ActiveAlerts).Kind);
        Assert.True(h.Monitor.Snapshot.ScreenObservationUnavailable);
        Assert.False(h.Monitor.Snapshot.MainScreenWatchEnabled);
    }

    [Fact]
    public async Task UnknownAfterWrongScreen_DoesNotSilentlyDismissReminder()
    {
        var ocr = new FakeOcrEngine { DefaultResult = ProductionInfoOcr() };
        Harness h = Build(80, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 20) ocr.DefaultResult = OcrResult.Empty;
        };
        await h.Monitor.RunAsync(h.Driver.Cts.Token);
        Assert.Equal(AlertKind.WrongScreen, Assert.Single(h.Alerts.ActiveAlerts).Kind);
        Assert.Null(h.Monitor.Snapshot.MainScreenVisible);
    }

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

    /// <summary>Sterownik pętli: zamiast czekać, przesuwa sztuczny zegar i po zadanej liczbie cykli przerywa pętlę.</summary>
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

        public Action<int>? AfterCycle { get; set; }

        public Task Delay(TimeSpan interval, CancellationToken ct)
        {
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
        FakeOcrEngine Ocr,
        FakeScreenSource Source,
        RecordingCaptureStore Captures,
        RecordingLoggerFactory Logs,
        List<AlertEvent> Events);

    private static Harness Build(int maxCycles, FakeOcrEngine ocr, ScreenProfile? profile = null, TimingConfig? timing = null,
        DiagnosticsConfig? diagnostics = null)
    {
        timing ??= Timing;
        var clock = new FakeClock();
        var driver = new LoopDriver(maxCycles, clock);
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, timing, acks);
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var captures = new RecordingCaptureStore();
        var logs = new RecordingLoggerFactory();
        var source = new FakeScreenSource("aidite1", NormalFrame());

        var monitor = new MachineMonitor(
            Machine(),
            profile ?? Profile,
            () => source,
            testOverride: null,
            ocr,
            new MessageMapper(TestPaths.ExampleRules),
            alerts,
            clock,
            timing,
            captures,
            logs.CreateLogger("MachineMonitor"),
            driver.Delay,
            diagnostics);

        return new Harness(monitor, driver, clock, alerts, ocr, source, captures, logs, events);
    }

    [Fact]
    public async Task RunAsync_GdyHmiPokazujeEkranGlowny_SprawdzaCoOdstepIWymuszaPelnaKlatke()
    {
        // Dlaczego: sprawdzenie to OCR całego ekranu — nie może lecieć co sekundę na 7 maszyn, ale musi
        // wymuszać pełną klatkę (nie tylko pasek statusu), bo etykieta „Milling Status” leży poza ROI.
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(maxCycles: 12, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        // Sprawdzenia w t = 0, 5, 10 s (cykl = 1 s, odstęp 5 s) — po jednym OCR każde (etykieta od razu).
        Assert.Equal(3, h.Ocr.CallCount);
        Assert.Equal(3, h.Source.CaptureFullCount);
        Assert.Empty(h.Alerts.ActiveAlerts);

        MachineStatusSnapshot s = h.Monitor.Snapshot;
        Assert.True(s.MainScreenWatchEnabled);
        Assert.True(s.MainScreenVisible);
        Assert.Null(s.WrongScreenSince);
        Assert.NotNull(s.LastMainScreenCheckAt);
    }

    [Fact]
    public async Task RunAsync_GdyOperatorPrzelaczyZakladkeIZapomniWrocic_TworzyAlertPoProguZJednymZrzutem()
    {
        // Dlaczego: HMI na „Production info”, program nie widzi alertów.
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(maxCycles: 22, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                ocr.DefaultResult = ProductionInfoOcr();   // operator przełączył zakładkę
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        // t=0: ekran główny (1 OCR). t=5,10,15,20: inny ekran (po 2 OCR: 1:1 i ×2). Próg 10 s liczony
        // od t=5 → alert w sprawdzeniu t=15.
        Assert.Equal(1 + (4 * 2), h.Ocr.CallCount);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.WrongScreen, alert.Kind);
        Assert.Equal(AlertKey.WrongScreen("aidite1"), alert.Key);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(5), alert.StartedAt);
        Assert.Equal("captures/aidite1_ekran.png", alert.CaptureFile);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);

        (string _, string _, int width, int height) = Assert.Single(h.Captures.Saved, c => c.Tag == "ekran");
        RgbaImage klatka = NormalFrame();
        Assert.Equal(klatka.Width / 2, width);
        Assert.Equal(klatka.Height / 2, height);

        MachineStatusSnapshot s = h.Monitor.Snapshot;
        Assert.False(s.MainScreenVisible);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(5), s.WrongScreenSince);
        Assert.NotEmpty(h.Logs.Containing("HMI pokazuje inny ekran niż główny"));
    }

    [Fact]
    public async Task RunAsync_ZrzutEkranu_PrzyWlaczonychZrzutachDiagnostycznych_ZapisujeSieBezPomniejszania()
    {
        // Dlaczego: przy diagnostics.captureBreaks = true zrzut „ekran” służy diagnozie OCR pełnej klatki
        // — tam liczy się każdy piksel, więc skalowanie 1:2 jest wyłączone.
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(maxCycles: 22, ocr, diagnostics: new DiagnosticsConfig { CaptureBreaks = true });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3) ocr.DefaultResult = ProductionInfoOcr();
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        (string _, string _, int width, int height) = Assert.Single(h.Captures.Saved, c => c.Tag == "ekran");
        RgbaImage klatka = NormalFrame();
        Assert.Equal(klatka.Width, width);
        Assert.Equal(klatka.Height, height);
    }

    [Fact]
    public async Task RunAsync_ZrzutEkranu_PrzyDrugimEpizodzieWCiaguGodziny_NiePowstajePonownie()
    {
        // Dlaczego: Operator potrafi przełączać zakładkę HMI w kółko; „raz na
        // epizod” dawało wtedy pełną klatkę za każdym razem. Drugi alert w tej samej godzinie powstaje
        // normalnie, ale bez nowego pliku (i bez przycisku „Pokaż zrzut”).
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(maxCycles: 60, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            // inny ekran → alert; powrót na główny (alert zamknięty); znów inny ekran → drugi alert.
            if (cycle is >= 3 and < 25) ocr.DefaultResult = ProductionInfoOcr();
            else if (cycle is >= 25 and < 35) ocr.DefaultResult = MainScreenOcr();
            else if (cycle >= 35) ocr.DefaultResult = ProductionInfoOcr();
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(2, h.Events.Count(e => e.Kind == AlertEventKind.Raised));
        Assert.Single(h.Captures.Saved, c => c.Tag == "ekran");
        Assert.Null(Assert.Single(h.Alerts.ActiveAlerts).CaptureFile);
    }

    [Fact]
    public async Task RunAsync_GdyZmieniSieKontekstEkranu_SprawdzaPrzedUplywemOdstepu()
    {
        // Dlaczego: odstęp sprawdzania wyznacza tylko GÓRNĄ
        // granicę opóźnienia. Każda pełna klatka, na której kontekst (ekran bez paska statusu i pola
        // procentu) się zmienił, wymusza nowy odczyt od razu — dzięki temu przełączenie zakładki HMI
        // widać w ciągu jednego cyklu, a nie dopiero po pełnym mainScreenCheckIntervalMs.
        var timing = new TimingConfig
        {
            MainScreenCheckIntervalMs = 20000,
            FullFrameIntervalMs = 5000,
            WrongScreenAlertAfterMs = 0,
        };
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(maxCycles: 12, ocr, timing: timing);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 5)
            {
                ocr.DefaultResult = ProductionInfoOcr();
                h.Source.Frame = OtherTabFrame();
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        // Pierwsze sprawdzenie w t = 0; następne wypadałoby dopiero w t = 20 s, ale pełna klatka z t = 5 s
        // ma inny kontekst, więc odczyt i alert (próg 0) powstają już w t = 5 s.
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.WrongScreen, alert.Kind);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(5), alert.StartedAt);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(5), h.Monitor.Snapshot.WrongScreenSince);
    }

    [Fact]
    public async Task RunAsync_GdyOperatorWrociNaEkranGlowny_AlertZamykaSieOdRazu()
    {
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        Harness h = Build(maxCycles: 22, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                ocr.DefaultResult = ProductionInfoOcr();
            }
            else if (cycle == 17)
            {
                ocr.DefaultResult = MainScreenOcr();       // operator wrócił — sprawdzenie w t=20 to zobaczy
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Closed);

        MachineStatusSnapshot s = h.Monitor.Snapshot;
        Assert.True(s.MainScreenVisible);
        Assert.Null(s.WrongScreenSince);
        Assert.NotEmpty(h.Logs.Containing("znów pokazuje ekran główny"));
    }

    [Fact]
    public async Task RunAsync_GdyOcrNicNieOdczytuje_NieTworzyAlertuIStanPozostajeNieznany()
    {
        // Dlaczego: pusty odczyt to awaria/przeciążenie OCR, nie inna zakładka — fałszywe przypomnienie
        // byłoby gorsze niż opóźnione.
        var ocr = new FakeOcrEngine { DefaultResult = OcrResult.Empty };
        Harness h = Build(maxCycles: 22, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Empty(h.Events);
        Assert.True(h.Monitor.Snapshot.MainScreenWatchEnabled);
        Assert.Null(h.Monitor.Snapshot.MainScreenVisible);
        Assert.Null(h.Monitor.Snapshot.WrongScreenSince);
    }

    [Fact]
    public async Task RunAsync_GdyOcrNiedostepny_WylaczaTylkoTeFunkcjeIOstrzegaRaz()
    {
        // Dlaczego: bez silnika OCR nie ma czego sprawdzać — ale wykrywanie czerwieni (po kolorze) ma
        // działać dalej, a pełna klatka nie może być wymuszana co 5 s bez powodu.
        var ocr = new FakeOcrEngine { IsAvailable = false, DefaultResult = MainScreenOcr() };
        Harness h = Build(maxCycles: 12, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, h.Ocr.CallCount);
        Assert.Equal(1, h.Source.CaptureFullCount);   // tylko pierwsza pełna klatka
        Assert.False(h.Monitor.Snapshot.MainScreenWatchEnabled);
        Assert.Null(h.Monitor.Snapshot.MainScreenVisible);
        Assert.Single(h.Logs.Containing("obserwacja ekranu głównego HMI"));
        Assert.Equal(MachineConnectionState.Connected, h.Monitor.Snapshot.State);
    }

    [Fact]
    public async Task RunAsync_BezSekcjiMainScreen_ZachowujeSieJakDotad()
    {
        // Regresja: profil bez sekcji "mainScreen" — zero OCR, jedna pełna klatka na start, brak alertów.
        var ocr = new FakeOcrEngine { DefaultResult = ProductionInfoOcr() };
        Harness h = Build(maxCycles: 12, ocr, Profile with { MainScreen = null });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, h.Ocr.CallCount);
        Assert.Equal(1, h.Source.CaptureFullCount);
        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.False(h.Monitor.Snapshot.MainScreenWatchEnabled);
    }

    [Fact]
    public async Task RunAsync_SekcjaMainScreenWylaczona_ZachowujeSieJakDotad()
    {
        var ocr = new FakeOcrEngine { DefaultResult = ProductionInfoOcr() };
        Harness h = Build(maxCycles: 12, ocr, Profile with { MainScreen = new MainScreenWatchProfile { Enabled = false } });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, h.Ocr.CallCount);
        Assert.False(h.Monitor.Snapshot.MainScreenWatchEnabled);
    }

    [Fact]
    public async Task RunAsync_KolejneNierozstrzygnieteSprawdzenia_LiczyStreakIOstrzegaRaz()
    {
        var ocr = new FakeOcrEngine { DefaultResult = OcrResult.Empty };
        Harness h = Build(maxCycles: 60, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 5) Assert.Equal(1, h.Monitor.Snapshot.MainScreenUnknownStreak);
            // Sprawdzenia wypadają w t = 0, 5, 10, 15, 20 s (odstęp 5 s) — piąte kończy serię.
            if (cycle == 21)
            {
                Assert.Equal(MainScreenUnknownWarnStreak, h.Monitor.Snapshot.MainScreenUnknownStreak);
                Assert.Equal("ocr-bez-tekstu", h.Monitor.Snapshot.MainScreenUnknownReason);
            }
            if (cycle == 26) ocr.DefaultResult = MainScreenOcr();
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, h.Monitor.Snapshot.MainScreenUnknownStreak);
        Assert.Null(h.Monitor.Snapshot.MainScreenUnknownReason);
        Assert.True(h.Monitor.Snapshot.MainScreenVisible);
        // Jedno ostrzeżenie na całą serię (nie jedno na sprawdzenie) i jeden wpis debug o powodzie.
        Assert.Single(h.Logs.Containing("kolejnych sprawdzeń ekranu głównego HMI bez rozstrzygnięcia"));
        Assert.Single(h.Logs.Containing("nierozstrzygnięte (powód: ocr-bez-tekstu)"));
    }

    /// <summary>Ten sam próg co <c>MachineMonitor.MainScreenUnknownWarnStreak</c> (pole prywatne).</summary>
    private const int MainScreenUnknownWarnStreak = 5;
}
