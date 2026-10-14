using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorRedFieldOcrTests
{
    private static readonly ScreenProfile Profile = TestPaths.Profile("aidite5x") with { MainScreen = null, Idle = null };

    /// <summary>Reszta paska.</summary>
    private const string RestOfBar = "EDIT AUTO USB PROTECT 2626. 69. 25 19164141";

    private const string ToolTooShort =
        "STOPPED [3600] Job_2026-09-24_20-06-39.NC line3000824: The actual measured tool length is less than the set minimum tool length";

    private const string BeforeUsing = "EDIT READY TIP:M024. 01 Before using machi ne. Please AUTO USB PROTECT 2626. 69. 25";

    private static MachineConfig Machine() => new()
    {
        Id = "amm520-2",
        Name = "520 szyny",
        Host = "192.168.1.102",
        Port = 5900,
        Profile = "aidite5x",
    };

    /// <summary>Klatka 1024×768 z natywnym wycinkiem paska stanu (czerwone pole z białym tekstem) w statusBarRoi.</summary>
    private static RgbaImage Frame()
    {
        RgbaImage bar = PngCodec.DecodeFile(Path.Combine(
            TestPaths.SamplesOutput, "production-aidite5x", "pasek-ocr-2026-09-25", "520-akrony-093951-blad.png"));
        var frame = new RgbaImage(1024, 768);
        frame.Fill(SyntheticScreens.LightBackground);
        frame.Blit(bar, Profile.StatusBarRoi.X, Profile.StatusBarRoi.Y);
        return frame;
    }

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

        public CancellationTokenSource Cts { get; } = new();

        public Task Delay(TimeSpan interval, CancellationToken ct)
        {
            clock.Advance(interval);
            if (++_cycles >= maxCycles)
            {
                Cts.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private static async Task<AlertStateMachine> RunAsync(FakeOcrEngine ocr, int cycles = 15)
    {
        var clock = new FakeClock();
        var driver = new LoopDriver(cycles, clock);
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var source = new FakeScreenSource("amm520-2", Frame());
        var monitor = new MachineMonitor(
            Machine(), Profile, () => source, testOverride: null, ocr,
            new MessageMapper(TestPaths.ExampleRules), alerts, clock, timing, new NullCaptureStore(), logger: null, driver.Delay);
        await monitor.RunAsync(driver.Cts.Token);
        return alerts;
    }

    [Fact]
    public async Task TekstNaCzerwonymPolu_OpisujeAlertZamiastResztyPaska()
    {
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(ToolTooShort, [ToolTooShort]) };
        ocr.Enqueue(RestOfBar).Enqueue(RestOfBar);

        AlertStateMachine alerts = await RunAsync(ocr);

        Alert alert = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.Equal("rule:aidite5x-tool-too-short", alert.Key.Discriminator);
        Assert.Equal("narzedzie", alert.Category);
    }

    [Fact]
    public async Task WyciszonePrzypomnienie_NieZaglusza_BleduZPola()
    {
        // Pierwszy odczyt widzi tylko przypomnienie „bez alarmu”; pole niesie prawdziwy błąd — ma dzwonić.
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(BeforeUsing, [BeforeUsing]) };
        ocr.Enqueue(BeforeUsing).Enqueue(ToolTooShort);

        AlertStateMachine alerts = await RunAsync(ocr);

        Alert alert = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal("rule:aidite5x-tool-too-short", alert.Key.Discriminator);
        Assert.True(alert.Sound);
    }

    [Fact]
    public async Task SamoPrzypomnienie_NaPoluIPasku_ZostajeBezAlarmu()
    {
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(BeforeUsing, [BeforeUsing]) };

        AlertStateMachine alerts = await RunAsync(ocr);

        Assert.Empty(alerts.ActiveAlerts);
    }
}
