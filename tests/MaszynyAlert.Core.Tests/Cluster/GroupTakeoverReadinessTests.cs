using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using static MaszynyAlert.Core.Machines.MachineConnectionState;
using static MaszynyAlert.Core.Tests.Cluster.GroupRuntimeTests;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>
/// Gotowość nowych monitorów przed ustąpieniem dotychczasowego obserwatora:
/// obserwator oddaje rolę dopiero, gdy wybrany następca faktycznie czyta każdą maszynę, którą on sam
/// czyta. Migawki pochodzą z rzeczywistego <see cref="MonitorSupervisor"/> albo odwzorowują jego przebieg.
/// </summary>
public sealed class GroupTakeoverReadinessTests
{
    private static readonly MachineConfig[] Machines = TestPaths.ExampleConfig.Machines.Where(m => m.Enabled).Take(2).ToArray();

    private static IReadOnlyList<MachineStatusSnapshot> Snapshots(Pair p, MachineConnectionState[] states) =>
        Machines.Select((m, i) => p.Snapshot(m, states[i])).ToArray();

    [Fact]
    public async Task Watcher_YieldsOnlyWhenSuccessorReadsEveryMachineItReads()
    {
        await using var p = new Pair(bPriority: 50);
        var aStates = new[] { Connected, Connected };
        var bStates = new[] { Error, Error };
        p.MachinesFor = host => Snapshots(p, host == "A" ? aStates : bStates);
        await p.StartA();
        Assert.True(p.A.IsWatcher);

        await p.StartB();
        await p.Tick(25);
        Assert.True(p.B.IsWatcher); // wybrany następca uruchamia monitory
        Assert.True(p.A.IsWatcher); // ale nic jeszcze nie czyta

        bStates[0] = Connected;
        await p.Tick(10);
        Assert.True(p.A.IsWatcher); // jedna z dwóch maszyn to za mało

        bStates[1] = Connected;
        await p.Tick(3);
        Assert.False(p.A.IsWatcher);
        Assert.True(p.B.IsWatcher);
        Assert.Equal("B", p.A.WatcherHost);
    }

    [Fact]
    public async Task Watcher_KeepsObservingWhileSuccessorCannotConnectAnything()
    {
        await using var p = new Pair(bPriority: 50);
        p.MachinesFor = host => Snapshots(p, host == "A" ? [Connected, Connected] : [Error, Error]);
        await p.StartA();
        await p.StartB();
        await p.Tick(120);

        Assert.True(p.A.IsWatcher);
        Assert.True(p.B.IsWatcher); // równoległa obserwacja jest dopuszczalna; luka nie
    }

    [Fact]
    public async Task WatcherReadingNothing_YieldsOnceSuccessorReadsAnything()
    {
        await using var p = new Pair(bPriority: 50);
        p.MachinesFor = host => Snapshots(p, host == "A" ? [Error, Error] : [Connected, Error]);
        await p.StartA();
        await p.StartB();
        await p.Tick(25);

        Assert.False(p.A.IsWatcher);
        Assert.True(p.B.IsWatcher);
    }

    [Fact]
    public async Task Takeover_WithRealSupervisorSnapshots_WaitsForSuccessorMonitors()
    {
        using var dirA = new TempDir();
        using var dirB = new TempDir();
        await using var p = new Pair(bPriority: 50);
        var cfg = LocalConfig();
        await using var supA = Build(cfg, dirA.Path, p.Clock);
        await using var supB = Build(cfg, dirB.Path, p.Clock);
        p.MachinesFor = host => host == "A" ? supA.Snapshots : supB.Snapshots;

        WriteScreen(dirA.Path, Machines[0].Id);
        WriteScreen(dirA.Path, Machines[1].Id);
        await supA.StartAsync(default);
        await WaitUntil(() => supA.Snapshots.Count(s => s.State == Connected) == 2);
        await p.StartA();
        Assert.True(p.A.IsWatcher);

        await p.StartB();
        await supB.StartAsync(default); // jak TickOnceAsync: monitory następcy startują po wyborze
        await p.Tick(25);
        Assert.True(p.B.IsWatcher);
        Assert.True(p.A.IsWatcher);
        Assert.DoesNotContain(supB.Snapshots, s => s.State == Connected); // 127.0.0.1:1 odrzuca połączenia

        WriteScreen(dirB.Path, Machines[0].Id);
        await WaitUntil(() => supB.Snapshots.Count(s => s.State == Connected) == 1);
        await p.Tick(5);
        Assert.True(p.A.IsWatcher);

        WriteScreen(dirB.Path, Machines[1].Id);
        await WaitUntil(() => supB.Snapshots.Count(s => s.State == Connected) == 2);
        await p.Tick(3);
        Assert.False(p.A.IsWatcher);
        Assert.True(p.B.IsWatcher);
    }

    private static AppConfig LocalConfig()
    {
        AppConfig cfg = TestPaths.ExampleConfig;
        return cfg with
        {
            Machines = cfg.Machines.Select(m => m with
            {
                Host = "127.0.0.1", Port = 1, Enabled = Machines.Any(x => x.Id == m.Id),
            }).ToList(),
            Timing = cfg.Timing with { ConnectTimeoutMs = 200, PollIntervalMs = 200, PollingModeIntervalMs = 200, ReconnectBackoffMs = [200] },
        };
    }

    private static MonitorSupervisor Build(AppConfig cfg, string screensDir, FakeClock clock) => new(
        cfg, _ => null, new FakeOcrEngine { IsAvailable = false }, new MessageMapper(TestPaths.ExampleRules),
        new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2))), clock,
        new NullCaptureStore(), testScreensDir: screensDir);

    private static void WriteScreen(string dir, string machineId)
    {
        var frame = new RgbaImage(1024, 768);
        frame.Fill(SyntheticScreens.LightBackground);
        PngCodec.EncodeToFile(frame, Path.Combine(dir, machineId + ".png"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "warunek migawek nadzorcy nie został spełniony w 5 s");
    }
}
