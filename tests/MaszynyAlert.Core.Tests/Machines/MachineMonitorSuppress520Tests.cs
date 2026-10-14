using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorSuppress520Tests
{
    private static readonly ScreenProfile Profile = TestPaths.Profile("aidite5x") with { MainScreen = null, Idle = null };

    private const string BeforeUsing = "EDIT READY M024. 01 Before using machi ne. AUTO USB PROTECT 2026.09.25 68";

    private const string BeforeUsingAndAir = "EDIT READY M024. 01 Before using machi ne. Insufficient external air pressure";

    private const string MillingFluid =
        "HOLD ING [1699] M023. 62 Mil ling fluid is abnormal and the mill ing has been suspended, Add";

    private static MachineConfig Machine() => new()
    {
        Id = "amm520-1",
        Name = "520 akrony",
        Host = "192.168.1.101",
        Port = 5900,
        Profile = "aidite5x",
    };

    private static RgbaImage Frame(string seed, double coverage, Rgba textColor) => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, textColor, seed, coverage));

    private static RgbaImage RedFrame(string seed, double coverage) => Frame(seed, coverage, SyntheticScreens.RedText);

    private static RgbaImage StatusRoi(RgbaImage frame) => frame.Crop(Profile.StatusBarRoi.Clamp(frame.Width, frame.Height));

    private sealed class NullCaptureStore : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

        public void Prune(TimeSpan retention)
        {
        }
    }

    private sealed class LoopDriver(int maxCycles, FakeClock clock)
    {
        private int _cycles;

        public FakeClock Clock { get; } = clock;

        public CancellationTokenSource Cts { get; } = new();

        public Action<int>? AfterCycle { get; set; }

        public Task Delay(TimeSpan interval, CancellationToken ct)
        {
            Clock.Advance(interval);
            _cycles++;
            AfterCycle?.Invoke(_cycles);
            if (_cycles >= maxCycles)
            {
                Cts.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private sealed record Harness(MachineMonitor Monitor, LoopDriver Driver, AlertStateMachine Alerts, FakeOcrEngine Ocr, List<AlertEvent> Events);

    private static Harness Build(int maxCycles, FakeScreenSource source, FakeOcrEngine ocr, TimingConfig? timing = null)
    {
        var clock = new FakeClock();
        var driver = new LoopDriver(maxCycles, clock);
        TimingConfig t = timing ?? new TimingConfig();
        var alerts = new AlertStateMachine(clock, t, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var logs = new RecordingLoggerFactory();

        var monitor = new MachineMonitor(
            Machine(),
            Profile,
            () => source,
            testOverride: null,
            ocr,
            new MessageMapper(TestPaths.ExampleRules),
            alerts,
            clock,
            t,
            new NullCaptureStore(),
            logs.CreateLogger("MachineMonitor"),
            driver.Delay);

        return new Harness(monitor, driver, alerts, ocr, events);
    }

    private static int Raised(Harness h) => h.Events.Count(e => e.Kind == AlertEventKind.Raised);

    [Fact]
    public void Warunki_ProfilRegulaIKlatkaSaTakieJakNaStanowisku()
    {
        Assert.Equal("aidite5x", Profile.Id);
        var mapper = new MessageMapper(TestPaths.ExampleRules);
        Assert.True(mapper.HasSuppressRules("aidite5x"));

        Assert.True(new RedTextDetector(Profile).AnalyzeFullFrame(RedFrame("m024", 0.05)).IsRed,
            "Czerwona klatka testowa nie przechodzi bramki koloru profilu aidite5x.");
        Assert.True(new RedTextDetector(Profile).AnalyzeFullFrame(Frame("m024", 0.05, SyntheticScreens.YellowText)).IsRed,
            "Żółta klatka testowa nie przechodzi bramki koloru profilu aidite5x.");

        MappedMessage tip = mapper.Map(BeforeUsing, "aidite5x");
        Assert.Equal("aidite5x-before-using", tip.RuleId);
        Assert.True(tip.Suppressed);
    }

    [Fact]
    public async Task PrzypomnienieM024NaPasku_NieDajeAlarmu()
    {
        var source = new FakeScreenSource("aidite5x", RedFrame("m024", 0.05));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(BeforeUsing);
        Harness h = Build(maxCycles: 40, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(h.Ocr.CallCount >= 1, "Monitor w ogóle nie przeczytał treści paska.");
        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Equal(0, Raised(h));
    }

    [Fact]
    public async Task PrzypomnienieM024NaZoltymPasku_NieDajeAlarmu()
    {
        var source = new FakeScreenSource("aidite5x", Frame("m024", 0.05, SyntheticScreens.YellowText));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(BeforeUsing);
        Harness h = Build(maxCycles: 40, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(h.Ocr.CallCount >= 1, "Monitor w ogóle nie przeczytał treści paska.");
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task PrzypomnienieWiszaceDlugo_JestCzytaneOkresowo_IDalejBezAlarmu()
    {
        // 100 s nieruchomego przypomnienia: kontrola co SuppressRecheckMs (30 s) — wyciszenie trwa tylko,
        // dopóki OCR naprawdę widzi ten komunikat. Dlaczego: M024.01 wisi zwykle aż do skasowania przez
        // operatora, a gdyby w tym czasie treść się zmieniła, monitor musi to zauważyć bez zmiany klatki.
        var source = new FakeScreenSource("aidite5x", RedFrame("m024", 0.05));
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(BeforeUsing, [BeforeUsing]) };
        Harness h = Build(maxCycles: 100, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.InRange(h.Ocr.CallCount, 3, 6);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task PrzypomnienieRazemZPrawdziwymBledem_DajeAlarmZNazwaBledu()
    {
        // Dlaczego: reguła „bez alarmu” przegrywa z każdą inną pasującą regułą — przypomnienie nie może
        // zasłonić braku powietrza, który HMI dopisał na tym samym pasku.
        var source = new FakeScreenSource("aidite5x", RedFrame("m024-air", 0.05));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(BeforeUsingAndAir);
        Harness h = Build(maxCycles: 12, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("rule:aidite5x-air-pressure", alert.Key.Discriminator);
        Assert.Equal(1, Raised(h));
    }

    [Fact]
    public async Task PrzypomnienieAPotemPrawdziwyBlad_DajeAlarm()
    {
        // Najpierw samo przypomnienie (wyciszone), potem na pasku pojawia się awaria płynu obróbkowego.
        RgbaImage tip = RedFrame("m024", 0.02);
        RgbaImage fluid = RedFrame("milling-fluid", 0.08);
        Assert.True(FrameDiff.ChangedFraction(StatusRoi(tip), StatusRoi(fluid)) > 0.02,
            "Klatki testowe są zbyt podobne — monitor nie uznałby ich za zmianę treści.");

        var source = new FakeScreenSource("aidite5x", tip);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(BeforeUsing).Enqueue(MillingFluid);
        Harness h = Build(maxCycles: 12, source, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 5)
            {
                source.Frame = fluid;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(1, Raised(h));
    }

    [Fact]
    public async Task InnaTrescNaPasku_DajeAlarm()
    {
        // Kontrola: ta sama klatka i ta sama pętla, tylko inna treść — alarm musi zadzwonić. Dlaczego: gdyby
        // dzwonił i tu cisza, testy „bez alarmu” wyżej niczego by nie dowodziły.
        var source = new FakeScreenSource("aidite5x", RedFrame("m024", 0.05));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(MillingFluid);
        Harness h = Build(maxCycles: 12, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.False(new MessageMapper(TestPaths.ExampleRules).Map(MillingFluid, "aidite5x").Suppressed);
        Assert.NotEqual("rule:aidite5x-before-using", alert.Key.Discriminator);
        Assert.Equal(1, Raised(h));
    }

    [Fact]
    public async Task PustyOdczytTresci_DajeAlarm()
    {
        // Nie wiemy, co jest na pasku — wolimy fałszywy alarm niż przeoczony.
        var source = new FakeScreenSource("aidite5x", RedFrame("nieczytelne", 0.05));
        var ocr = new FakeOcrEngine(); // każdy odczyt pusty
        Harness h = Build(maxCycles: 5, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task BezSilnikaOcr_AlarmDzwoniOdRazu()
    {
        // Bez OCR nie da się sprawdzić, że na pasku jest wyłącznie przypomnienie — obowiązuje „alarm po kolorze”.
        var source = new FakeScreenSource("aidite5x", RedFrame("m024", 0.05));
        var ocr = new FakeOcrEngine { IsAvailable = false };
        ocr.Enqueue(BeforeUsing);
        Harness h = Build(maxCycles: 3, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task BrakOdczytuTresci_AlarmPoLimicieCzekania()
    {
        // OcrMaxAttempts = 0: monitor nie czyta treści wcale, więc nie ma czym rozstrzygnąć. Alarm czeka
        // najwyżej SuppressDecisionTimeoutMs (10 s), a potem dzwoni.
        var source = new FakeScreenSource("aidite5x", RedFrame("m024", 0.05));
        var ocr = new FakeOcrEngine();

        Harness early = Build(maxCycles: 8, source, ocr, new TimingConfig { OcrMaxAttempts = 0 });
        await early.Monitor.RunAsync(early.Driver.Cts.Token);
        Assert.Empty(early.Alerts.ActiveAlerts);

        Harness late = Build(maxCycles: 16, source, ocr, new TimingConfig { OcrMaxAttempts = 0 });
        await late.Monitor.RunAsync(late.Driver.Cts.Token);
        Assert.Single(late.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task SilnikOcrPadaWTrakcieWyciszenia_AlarmDzwoni()
    {
        // Zawieszony WinRT (SerializingOcrEngine.IsAvailable = false) — nie da się już sprawdzić, czy na pasku
        // wisi wyłącznie przypomnienie M024.01, więc wyciszenie nie może trwać dalej.
        var source = new FakeScreenSource("aidite5x", RedFrame("m024", 0.05));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(BeforeUsing);
        Harness h = Build(maxCycles: 10, source, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 5)
            {
                ocr.IsAvailable = false;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Alerts.ActiveAlerts);
    }
}
