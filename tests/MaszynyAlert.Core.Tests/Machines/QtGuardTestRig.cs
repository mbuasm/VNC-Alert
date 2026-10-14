using System.Text.RegularExpressions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Machines;

internal sealed class QtGuardTestRig
{
    public static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null };

    public const string ClosedByServer = "Połączenie z 192.168.1.101:5900 zostało zamknięte przez serwer w trakcie odczytu.";

    public const string Refused =
        "Nie udało się połączyć z 192.168.1.101:5900: Nie można nawiązać połączenia, ponieważ komputer docelowy aktywnie go odmawia.";

    private readonly int _maxCycles;

    public QtGuardTestRig(int maxCycles, string desktopName, FolderScreenSource? testOverride = null)
    {
        _maxCycles = maxCycles;
        Alerts = new AlertStateMachine(Clock, Timing, new AckMemory(Clock, TimeSpan.FromHours(Timing.AckMemoryHours)));
        Alerts.Changed += Events.Add;
        Source = new FakeScreenSource("amm520-1", NormalFrame()) { DesktopName = desktopName };
        Source.OnConnect = () =>
        {
            Connects.Add(Clock.MonotonicMs);
            CyclesSinceConnect = 0;
            OnConnectNumber?.Invoke(Connects.Count);
        };
        Monitor = new MachineMonitor(
            Machine(), Profile, () => Source, testOverride, new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules),
            Alerts, Clock, Timing, new NullCaptureStore(), Loggers.CreateLogger<MachineMonitor>(), Delay);
    }

    public static RgbaImage NormalFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));

    public static MachineConfig Machine() => new()
    {
        Id = "amm520-1",
        Name = "520 (1)",
        Host = "192.168.1.101",
        Port = 5900,
        Profile = "opencnc",
        Enabled = true,
        Mode = ConnectionMode.Persistent,
    };

    public FakeClock Clock { get; } = new();

    public TimingConfig Timing { get; } = new() { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };

    public AlertStateMachine Alerts { get; }

    public List<AlertEvent> Events { get; } = [];

    public RecordingLoggerFactory Loggers { get; } = new();

    public FakeScreenSource Source { get; }

    public MachineMonitor Monitor { get; }

    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Odstępy kolejnych cykli pętli (argument haka opóźnienia).</summary>
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>LastError migawki na końcu każdego cyklu (ta sama kolejność co <see cref="Delays"/>).</summary>
    public List<string?> LastErrors { get; } = [];

    /// <summary>Chwile (zegar monotoniczny) każdej próby połączenia — <see cref="FakeScreenSource.OnConnect"/>.</summary>
    public List<long> Connects { get; } = [];

    /// <summary>Chwile (zegar monotoniczny) każdej zaskryptowanej awarii — patrz <see cref="Fail"/> i <see cref="ArmKick"/>.</summary>
    public List<long> Failures { get; } = [];

    /// <summary>Wołane po zapisaniu próby połączenia z jej numerem (od 1), przed ewentualnym <c>ConnectFailure</c>.</summary>
    public Action<int>? OnConnectNumber { get; set; }

    public Action<int>? AfterCycle { get; set; }

    public int CyclesSinceConnect { get; private set; }

    public Task RunAsync() => Monitor.RunAsync(Cts.Token);

    /// <summary>Awaria po <paramref name="advanceMs"/> ms (zegar ręczny) — zapisuje chwilę awarii w <see cref="Failures"/>.</summary>
    public Func<Exception> Fail(Func<Exception> make, int advanceMs = 0) => () =>
    {
        if (advanceMs > 0) Clock.Advance(TimeSpan.FromMilliseconds(advanceMs));
        Failures.Add(Clock.MonotonicMs);
        return make();
    };

    /// <summary>Jednorazowe zerwanie działającej sesji przez serwer; opcjonalnie dokładnie w chwili <paramref name="atMs"/>.</summary>
    public void ArmKick(long? atMs = null) => Source.CaptureFailure = () =>
    {
        Source.CaptureFailure = null;
        if (atMs is { } at) Clock.Advance(TimeSpan.FromMilliseconds(at - Clock.MonotonicMs));
        Failures.Add(Clock.MonotonicMs);
        return new RfbException(ClosedByServer);
    };

    /// <summary>Odstęp od każdej awarii do następnej próby połączenia (ms; −1 — brak kolejnej próby).</summary>
    public long[] WaitsAfterFailures()
    {
        var waits = new long[Failures.Count];
        for (int i = 0; i < Failures.Count; i++)
        {
            long next = Connects.FirstOrDefault(c => c > Failures[i], -1);
            waits[i] = next < 0 ? -1 : next - Failures[i];
        }

        return waits;
    }

    /// <summary>Czasy „następna próba za …” z kolejnych WRN bezpiecznika/przejęcia (ms).</summary>
    public long[] AnnouncedWaitsMs() => MonitorWarnings()
        .Select(w => Regex.Match(w.Message, @"następna próba za (\d+) (min|s)"))
        .Where(m => m.Success)
        .Select(m => long.Parse(m.Groups[1].Value) * (m.Groups[2].Value == "min" ? 60_000 : 1_000))
        .ToArray();

    public IReadOnlyList<LogEntry> MonitorWarnings() => Loggers.Entries
        .Where(e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(MachineMonitor), StringComparison.Ordinal))
        .ToArray();

    private Task Delay(TimeSpan interval, CancellationToken ct)
    {
        Delays.Add(interval);
        LastErrors.Add(Monitor.Snapshot.LastError);
        Clock.Advance(interval);
        Alerts.Tick();
        CyclesSinceConnect++;
        AfterCycle?.Invoke(Delays.Count);
        if (Delays.Count >= _maxCycles) Cts.Cancel();
        return Task.CompletedTask;
    }
}
