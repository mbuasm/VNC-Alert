using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitor580FlickerTests
{
    private static readonly ScreenProfile Profile = TestPaths.Profile("amm580") with { MainScreen = null, Idle = null };

    private const string ToolLife = "{T3}{T8} Tool life was reached. Please change the tool.";

    private const string ToolLengthInfo = "2 Set current tool length[T7] as-60.247";

    private static MachineConfig Machine() => new()
    {
        Id = "amm580",
        Name = "580",
        Host = "192.168.1.110",
        Port = 5900,
        Profile = "amm580",
    };

    private static RgbaImage Bar(Rgba background, Rgba text, string seed, double coverage) => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height, background, text, seed, coverage));

    private static readonly RgbaImage ToolLifeFrame = Bar(new Rgba(255, 0, 0), new Rgba(40, 40, 40), "tool-life", 0.05);

    /// <summary>Ten sam pasek z białym komunikatem na szarym tle — bez czerwieni.</summary>
    private static readonly RgbaImage WhiteInfoFrame = Bar(new Rgba(43, 43, 43), SyntheticScreens.WhiteText, "info", 0.05);

    /// <summary>Pasek chwilowo pusty (mignięcie).</summary>
    private static readonly RgbaImage BlankFrame = Bar(new Rgba(43, 43, 43), SyntheticScreens.WhiteText, null!, 0);

    /// <summary>OCR, który „czyta” to, co akurat stoi na pasku — odpowiedź wybiera test.</summary>
    private sealed class ScreenOcr : IOcrEngine
    {
        public string Name => "test";

        public bool IsAvailable => true;

        public string? LanguageTag => "en-US";

        public string Current { get; set; } = string.Empty;

        public int Calls { get; private set; }

        public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Current.Length == 0 ? OcrResult.Empty : new OcrResult(Current, [Current]));
        }
    }

    private sealed class NullCaptureStore : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

        public void Prune(TimeSpan retention)
        {
        }
    }

    private static async Task<(AlertStateMachine Alerts, List<AlertEvent> Events, ScreenOcr Ocr)> RunAsync(
        int cycles, Func<int, (RgbaImage Frame, string Text)> screenAt)
    {
        var clock = new FakeClock();
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var ocr = new ScreenOcr();
        (RgbaImage first, string firstText) = screenAt(0);
        ocr.Current = firstText;
        var source = new FakeScreenSource("amm580", first);
        using var cts = new CancellationTokenSource();
        int cycle = 0;

        var monitor = new MachineMonitor(
            Machine(), Profile, () => source, testOverride: null, ocr,
            new MessageMapper(TestPaths.ExampleRules), alerts, clock, timing, new NullCaptureStore(),
            new RecordingLoggerFactory().CreateLogger("MachineMonitor"),
            (interval, _) =>
            {
                clock.Advance(interval);
                alerts.Tick();
                cycle++;
                (RgbaImage frame, string text) = screenAt(cycle);
                source.Frame = frame;
                ocr.Current = text;
                if (cycle >= cycles) cts.Cancel();
                return Task.CompletedTask;
            });

        await monitor.RunAsync(cts.Token);
        return (alerts, events, ocr);
    }

    [Fact]
    public void ToolLifeZDwomaNumeramiNarzedzi_JestBezAlarmu()
    {
        MappedMessage mapped = new MessageMapper(TestPaths.ExampleRules).Map(ToolLife, "amm580");
        Assert.Equal("amm580-tool-life", mapped.RuleId);
        Assert.True(mapped.Suppressed);
    }

    [Fact]
    public async Task ToolLifePrzeplataneBialymKomunikatem_NieDajeAlarmu()
    {
        // Co piąta klatka to biały komunikat informacyjny (filtr „2 z 3” trzyma epizod przez tę klatkę).
        var (alerts, events, ocr) = await RunAsync(300, cycle =>
            cycle % 5 == 4 ? (WhiteInfoFrame, ToolLengthInfo) : (ToolLifeFrame, ToolLife));

        Assert.True(ocr.Calls >= 1);
        Assert.DoesNotContain(events, e => e.Kind == AlertEventKind.Raised);
        Assert.Empty(alerts.ActiveAlerts);
    }

    [Fact]
    public async Task ToolLifeMigajacy_NieDajeAlarmuBezTresci()
    {
        var (alerts, events, _) = await RunAsync(300, cycle =>
            cycle % 5 == 4 ? (BlankFrame, string.Empty) : (ToolLifeFrame, ToolLife));

        Assert.DoesNotContain(events, e => e.Kind == AlertEventKind.Raised);
        Assert.Empty(alerts.ActiveAlerts);
    }

    [Fact]
    public async Task PrawdziwyBladNaCzerwonymPolu_NadalDzwoniPoToolLife()
    {
        RgbaImage otherRed = Bar(new Rgba(255, 0, 0), new Rgba(40, 40, 40), "inny-blad", 0.12);
        var (alerts, events, _) = await RunAsync(40, cycle =>
            cycle < 10 ? (ToolLifeFrame, ToolLife) : (otherRed, "Feed shaft servo alarm"));

        Assert.Contains(events, e => e.Kind == AlertEventKind.Raised);
        Alert alert = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal("rule:amm580-servo", alert.Key.Discriminator);
    }
}
