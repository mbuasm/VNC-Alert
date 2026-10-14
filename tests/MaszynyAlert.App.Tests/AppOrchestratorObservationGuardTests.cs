using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class AppOrchestratorObservationGuardTests : IAsyncLifetime
{
    private sealed class ManualClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;
        public DateTimeOffset Now => UtcNow;
        public long MonotonicMs { get; private set; } = 1_000_000;
        public void Advance(TimeSpan delta) { UtcNow += delta; MonotonicMs += (long)delta.TotalMilliseconds; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r1b-guard-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _disposablesSync = [];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }

    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private (AppOrchestrator Orchestrator, ClusterRuntime Cluster) Build(IClock clock)
    {
        string root = RepositoryRoot();
        var example = ConfigLoader.LoadAppConfig(Path.Combine(root, "config", "config.example.json"));
        var rules = ConfigLoader.LoadRules(Path.Combine(root, "config", "rules.example.json"));
        var sample = example.Machines.First(m => m.Enabled);
        string history = Path.Combine(_dir, "data", "history.csv");
        var cfg = example with
        {
            Machines = [sample with { Id = "m1", Name = "Maszyna 1", Host = "127.0.0.1", Port = ClosedPort(), Enabled = true }],
            Paths = example.Paths with
            {
                HistoryFile = history, LogsDir = Path.Combine(_dir, "logs"),
                CapturesDir = Path.Combine(_dir, "captures"), TestScreensDir = Path.Combine(_dir, "test-ekran"),
            },
        };
        Directory.CreateDirectory(Path.Combine(_dir, "test-ekran"));
        var secrets = new InMemorySecretStore();
        secrets.Set(SecretKeys.VncPassword("m1"), "haslo-testowe");
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, cfg.Timing, acks);
        var supervisor = new MonitorSupervisor(cfg, m => secrets.Get(SecretKeys.VncPassword(m.Id)), new NullOcrEngine(),
            new MessageMapper(rules), alerts, clock, new FileCaptureStore(Path.Combine(_dir, "captures")), Path.Combine(_dir, "test-ekran"));
        var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, acks, "TEST-PC", "test");
        var power = new PowerModeWatcher();
        var orchestrator = new AppOrchestrator(runtime, supervisor, alerts, new HistoryWriter(history, clock), power, clock,
            new StartupConfig(cfg, cfg),
            new SharedConfigSync(new SharedConfigStore(Path.Combine(_dir, "shared-config", "shared-config.json")), "TEST-PC"),
            new NullOcrEngine(), NullLogger<AppOrchestrator>.Instance,
            new AlarmHelpService(rules, Path.Combine(_dir, "alarm-instructions.json"), false, clock: clock), secrets);
        _disposables.Add(supervisor); _disposables.Add(runtime); _disposablesSync.Add(power);
        return (orchestrator, runtime);
    }

    private static Task Tick(AppOrchestrator orchestrator) =>
        (Task)typeof(AppOrchestrator).GetMethod("TickOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(orchestrator, [CancellationToken.None])!;

    private static int RestartAfterSleep(AppOrchestrator orchestrator) =>
        (int)typeof(AppOrchestrator).GetField("_restartAfterSleep", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(orchestrator)!;

    /// <summary>Podmienia dziennik na taki, którego każdy zapis rzuca <see cref="ArgumentException"/> (NUL w ścieżce).</summary>
    private void BreakJournal(AppOrchestrator orchestrator)
    {
        var broken = new ObservationJournal(Path.Combine(_dir, "zly\0katalog", "observation.jsonl"));
        typeof(AppOrchestrator).GetField("<Observation>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(orchestrator, broken);
    }

    [Fact]
    public async Task ExceptionFromTheObservationJournalNeverSkipsTheClusterTick()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero));
        var (orchestrator, cluster) = Build(clock);
        int sleeps = 0;
        cluster.SleepDetected += () => sleeps++; // w programie subskrybuje to ExecuteAsync; tu wystarczy samo zdarzenie
        BreakJournal(orchestrator);

        // 15 tików co sekundę: po 10 s otwarty przedział chce się zapisać — dziennik rzuca przy każdej próbie.
        for (int i = 0; i < 15; i++) { await Tick(orchestrator); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(0, sleeps);

        // Skok zegara o minutę (uśpienie). Tik klastra musi go wykryć, mimo że dziennik znów rzuca przy domykaniu przedziału.
        clock.Advance(TimeSpan.FromMinutes(1));
        await Tick(orchestrator);

        Assert.Equal(1, sleeps);
    }

    [Fact]
    public async Task ExceptionFromTheJournalWhenSleepIsDetectedDoesNotEscapeTheHandler()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero));
        var (orchestrator, _) = Build(clock);
        for (int i = 0; i < 15; i++) { await Tick(orchestrator); clock.Advance(TimeSpan.FromSeconds(1)); }
        BreakJournal(orchestrator);
        // Podmieniony dziennik nie ma otwartych przedziałów — otwieramy jeden i każemy go domknąć w OnSleepDetected.
        orchestrator.Observation.Observe([("m1", ObservationState.NoConnection)], clock.UtcNow.AddSeconds(-20));
        orchestrator.Observation.Observe([("m1", ObservationState.NoConnection)], clock.UtcNow.AddSeconds(-15));

        var handler = typeof(AppOrchestrator).GetMethod("OnSleepDetected", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var thrown = Record.Exception(() => handler.Invoke(orchestrator, []));

        Assert.Null(thrown);
        Assert.Equal(1, RestartAfterSleep(orchestrator));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
        foreach (var disposable in _disposablesSync) disposable.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
