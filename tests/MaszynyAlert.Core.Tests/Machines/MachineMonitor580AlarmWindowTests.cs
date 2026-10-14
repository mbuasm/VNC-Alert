using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitor580AlarmWindowTests
{
    private const int H = 14;

    private static OcrWord Word(string text, int x, int y, int line) => new(text, new RectInt(x, y, text.Length * 8, H), line);

    private static OcrResult ScreenWithAlarmWindow() => OcrResult.FromWords(
    [
        Word("{T3}{T8}", 160, 5, 0), Word("Tool", 230, 5, 0), Word("life", 270, 5, 0), Word("was", 305, 5, 0),
        Word("reached.", 335, 5, 0), Word("Please", 400, 5, 0), Word("change", 455, 5, 0), Word("the", 515, 5, 0),
        Word("tool.", 545, 5, 0),
        Word("Alarm", 150, 40, 1), Word("Information", 200, 40, 1),
        Word("Line", 10, 55, 2), Word("Selection", 45, 55, 2), Word("BreakPoint", 200, 55, 2),
    ]);

    private sealed class NullCaptureStore : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

        public void Prune(TimeSpan retention)
        {
        }
    }

    private static async Task<(AlertStateMachine Alerts, List<AlertEvent> Events)> RunAsync(ScreenProfile profile, int cycles)
    {
        var clock = new FakeClock();
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        RgbaImage frame = SyntheticScreens.FullFrame(profile, SyntheticScreens.StatusBar(
            profile.StatusBarRoi.Width, profile.StatusBarRoi.Height, new Rgba(255, 0, 0), new Rgba(40, 40, 40), "tool-life", 0.05));
        var source = new FakeScreenSource("amm580", frame);
        var ocr = new FakeOcrEngine { DefaultResult = ScreenWithAlarmWindow() };
        using var cts = new CancellationTokenSource();
        int cycle = 0;

        var monitor = new MachineMonitor(
            new MachineConfig { Id = "amm580", Name = "580", Host = "192.168.1.110", Port = 5900, Profile = "amm580" },
            profile, () => source, testOverride: null, ocr, new MessageMapper(TestPaths.ExampleRules), alerts, clock,
            timing, new NullCaptureStore(), new RecordingLoggerFactory().CreateLogger("MachineMonitor"),
            (interval, _) =>
            {
                clock.Advance(interval);
                alerts.Tick();
                if (++cycle >= cycles) cts.Cancel();
                return Task.CompletedTask;
            });

        await monitor.RunAsync(cts.Token);
        return (alerts, events);
    }

    [Fact]
    public async Task OknoAlarmInformation_DajeZlyEkran_AToolLifeDalejBezAlarmu()
    {
        ScreenProfile profile = TestPaths.Profile("amm580") with { Idle = null, ProgramState = null };

        var (alerts, events) = await RunAsync(profile, 40);

        Alert alert = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal(AlertKind.WrongScreen, alert.Kind);
        Assert.DoesNotContain(events, e => e.Alert.Kind == AlertKind.Error);
    }

    [Fact]
    public async Task StaryProfilBezListyOkien_TraktujeOknoJakEkranGlowny()
    {
        // Stanowisko ze starym config.json (bez „otherScreenLabels”).
        ScreenProfile old = TestPaths.Profile("amm580") with { Idle = null, ProgramState = null };
        old = old with { MainScreen = old.MainScreen! with { OtherScreenLabels = [] } };

        var (alerts, events) = await RunAsync(old, 40);

        Assert.Empty(alerts.ActiveAlerts);
        Assert.DoesNotContain(events, e => e.Kind == AlertEventKind.Raised);
    }
}
