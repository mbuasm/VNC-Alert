using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorSuppressTests
{
    private static readonly ScreenProfile Profile = TestPaths.Profile("amm580") with { MainScreen = null, Idle = null };

    private const string ToolLife = "{T4} Tool life was reached. Please change the tool.";

    private const string SpindleInverter = "Spindle inverter alarm";

    private static MachineConfig Machine() => new()
    {
        Id = "amm580",
        Name = "580",
        Host = "192.168.1.110",
        Port = 5900,
        Profile = "amm580",
    };

    private static RgbaImage RedFrame(string seed, double coverage) => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.RedText, seed, coverage));

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
    public void Warunki_ProfilIRegulaSaTakieJakNaStanowisku()
    {
        Assert.Equal("amm580", Profile.Id);
        Assert.True(new MessageMapper(TestPaths.ExampleRules).HasSuppressRules("amm580"));
        // Na maszynach bez reguły wyciszającej alarm dzwoni od razu po kolorze.
        Assert.False(new MessageMapper(TestPaths.ExampleRules).HasSuppressRules("opencnc"));
        Assert.True(new MessageMapper(TestPaths.ExampleRules).HasSuppressRules("aidite5x"));
    }

    [Fact]
    public async Task ToolLifeNaPasku_NieDajeAlarmu()
    {
        var source = new FakeScreenSource("amm580", RedFrame("tool-life", 0.05));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(ToolLife);
        Harness h = Build(maxCycles: 40, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(h.Ocr.CallCount >= 1, "Monitor w ogóle nie przeczytał treści paska.");
        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Equal(0, Raised(h));
    }

    [Fact]
    public async Task ToolLifeWiszaceDlugo_JestCzytaneOkresowo_IDalejBezAlarmu()
    {
        var source = new FakeScreenSource("amm580", RedFrame("tool-life", 0.05));
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(ToolLife, [ToolLife]) };
        Harness h = Build(maxCycles: 100, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.InRange(h.Ocr.CallCount, 3, 6);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task ToolLifeAPotemPrawdziwyBlad_DajeAlarmZNazwaBledu()
    {
        RgbaImage toolLife = RedFrame("tool-life", 0.02);
        RgbaImage inverter = RedFrame("inverter", 0.08);
        Assert.True(FrameDiff.ChangedFraction(StatusRoi(toolLife), StatusRoi(inverter)) > 0.02,
            "Klatki testowe są zbyt podobne — monitor nie uznałby ich za zmianę treści.");

        var source = new FakeScreenSource("amm580", toolLife);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(ToolLife).Enqueue(SpindleInverter);
        Harness h = Build(maxCycles: 12, source, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 5)
            {
                source.Frame = inverter; // na pasku pojawił się prawdziwy błąd
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("rule:amm580-spindle-inverter", alert.Key.Discriminator);
        Assert.Equal(1, Raised(h));
    }

    [Fact]
    public async Task PustyOdczytTresci_DajeAlarm()
    {
        // Nie wiemy, co jest na pasku — wolimy fałszywy alarm niż przeoczony.
        var source = new FakeScreenSource("amm580", RedFrame("nieczytelne", 0.05));
        var ocr = new FakeOcrEngine(); // każdy odczyt pusty
        Harness h = Build(maxCycles: 5, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task BezSilnikaOcr_AlarmDzwoniOdRazu()
    {
        var source = new FakeScreenSource("amm580", RedFrame("tool-life", 0.05));
        var ocr = new FakeOcrEngine { IsAvailable = false };
        ocr.Enqueue(ToolLife);
        Harness h = Build(maxCycles: 3, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task BrakOdczytuTresci_AlarmPoLimicieCzekania()
    {
        // OcrMaxAttempts = 0: monitor nie czyta treści wcale, więc nie ma czym rozstrzygnąć. Alarm czeka
        // najwyżej SuppressDecisionTimeoutMs (10 s), a potem dzwoni.
        var source = new FakeScreenSource("amm580", RedFrame("tool-life", 0.05));
        var ocr = new FakeOcrEngine();

        Harness early = Build(maxCycles: 8, source, ocr, new TimingConfig { OcrMaxAttempts = 0 });
        await early.Monitor.RunAsync(early.Driver.Cts.Token);
        Assert.Empty(early.Alerts.ActiveAlerts);

        Harness late = Build(maxCycles: 16, source, ocr, new TimingConfig { OcrMaxAttempts = 0 });
        await late.Monitor.RunAsync(late.Driver.Cts.Token);
        Assert.Single(late.Alerts.ActiveAlerts);
    }

    [Fact]
    public void ToolLifeRazemZPrawdziwymBledem_NieJestWyciszane()
    {
        // Reguła „bez alarmu” przegrywa z każdą inną pasującą regułą, niezależnie od kolejności w pliku
        // (amm580-tool-life stoi w rules.example.json wysoko) — i z numerem alarmu.
        var mapper = new MessageMapper(TestPaths.ExampleRules);

        MappedMessage both = mapper.Map("{T4} Tool life was reached. Spindle inverter alarm", "amm580");
        Assert.Equal("amm580-spindle-inverter", both.RuleId);
        Assert.False(both.Suppressed);

        MappedMessage alone = mapper.Map(ToolLife, "amm580");
        Assert.True(alone.Suppressed);
    }

    [Fact]
    public async Task SilnikOcrPadaWTrakcieWyciszenia_AlarmDzwoni()
    {
        var source = new FakeScreenSource("amm580", RedFrame("tool-life", 0.05));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(ToolLife);
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

    [Fact]
    public async Task PoUsunieciuBledu_ZostajeSamToolLife_AlarmWracaDoWyciszenia()
    {
        RgbaImage inverter = RedFrame("inverter", 0.02);
        RgbaImage toolLife = RedFrame("tool-life", 0.08);
        var source = new FakeScreenSource("amm580", inverter);
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(ToolLife, [ToolLife]) };
        ocr.Enqueue(SpindleInverter);
        Harness h = Build(maxCycles: 110, source, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 5)
            {
                source.Frame = toolLife;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);
        h.Alerts.Tick();

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Equal(1, Raised(h));
    }

    [Fact]
    public async Task AlarmKtoryJuzZadzwonil_NieJestWyciszanyPrzezToolLife()
    {
        RgbaImage inverter = RedFrame("inverter", 0.02);
        RgbaImage toolLife = RedFrame("tool-life", 0.08);
        var source = new FakeScreenSource("amm580", inverter);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(SpindleInverter).Enqueue(ToolLife);
        Harness h = Build(maxCycles: 12, source, ocr);
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 5)
            {
                source.Frame = toolLife;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("Falownik wrzeciona — alarm", alert.ShortText);
    }
}
