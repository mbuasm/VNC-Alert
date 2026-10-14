using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

internal sealed class PreviewQtMonitorRig
{
    public static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null };

    private const string ClosedByServer = "Połączenie z 127.0.0.1:5900 zostało zamknięte przez serwer w trakcie odczytu.";
    private readonly int _maxCycles;
    private int _cycles;

    public PreviewQtMonitorRig(string profile, int maxCycles)
    {
        _maxCycles = maxCycles;
        MachineConfig = new MachineConfig
        {
            Id = "amm520-1", Name = "520 (1)", Host = "127.0.0.1", Port = 5900, Profile = profile, Enabled = true,
            RequiresPassword = false, Mode = ConnectionMode.Persistent,
        };
        Alerts = new AlertStateMachine(Clock, Timing, new AckMemory(Clock, TimeSpan.FromHours(Timing.AckMemoryHours)));
        Source = new FakeScreenSource("amm520-1", NormalFrame()) { DesktopName = QtEmbeddedVncServer.DesktopName };
        Monitor = new MachineMonitor(
            MachineConfig, Profile, () => Source, null, new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules),
            Alerts, Clock, Timing, new NullCaptureStore(), null, Delay);
    }

    public static RgbaImage NormalFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));

    public MachineConfig MachineConfig { get; }

    public FakeClock Clock { get; } = new();

    public TimingConfig Timing { get; } = new() { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };

    public AlertStateMachine Alerts { get; }

    public FakeScreenSource Source { get; }

    public MachineMonitor Monitor { get; }

    public CancellationTokenSource Cts { get; } = new();

    public Action<int>? AfterCycle { get; set; }

    public Task RunAsync() => Monitor.RunAsync(Cts.Token);

    public void ArmEarlyClose() => Source.CaptureFailure = () =>
    {
        Source.CaptureFailure = null;
        Clock.Advance(TimeSpan.FromMilliseconds(216));
        return new RfbException(ClosedByServer);
    };

    /// <summary>Jednorazowe zerwanie działającej sesji (przejęcie przez innego klienta).</summary>
    public void ArmKick() => Source.CaptureFailure = () =>
    {
        Source.CaptureFailure = null;
        return new RfbException(ClosedByServer);
    };

    public MachinePreviewService Preview(Func<IScreenSource> openSource)
    {
        var config = new AppConfig { Enabled = true, Machines = [MachineConfig] };
        return new MachinePreviewService(
            () => config,
            _ => Monitor.PreviewFrame,
            (_, _) => openSource(),
            Clock,
            _ => Monitor.Snapshot.Info?.DesktopName,
            observedSnapshot: _ => Monitor.Snapshot);
    }

    private Task Delay(TimeSpan interval, CancellationToken ct)
    {
        Clock.Advance(interval);
        Alerts.Tick();
        _cycles++;
        AfterCycle?.Invoke(_cycles);
        if (_cycles >= _maxCycles) Cts.Cancel();
        return Task.CompletedTask;
    }
}
