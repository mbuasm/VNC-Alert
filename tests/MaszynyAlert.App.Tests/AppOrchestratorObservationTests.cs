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

public sealed class AppOrchestratorObservationTests : IAsyncLifetime
{
    private sealed class ManualClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;
        public DateTimeOffset Now => UtcNow;
        public long MonotonicMs { get; private set; } = 1_000_000;
        public void Advance(TimeSpan delta) { UtcNow += delta; MonotonicMs += (long)delta.TotalMilliseconds; }
    }

    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public DateTimeOffset Now => DateTimeOffset.Now;
        public long MonotonicMs => Environment.TickCount64;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r1b-orchestrator-" + Guid.NewGuid().ToString("N"));
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

    private string HistoryFile => Path.Combine(_dir, "data", "history.csv");
    private string ObservationFile => Path.Combine(_dir, "data", "state", "observation.jsonl");

    private (AppOrchestrator Orchestrator, ClusterRuntime Cluster, AppConfig Config) Build(IClock clock,
        Func<ClusterRuntime>? cluster = null, Func<AppConfig, AppConfig>? adjust = null)
    {
        string root = RepositoryRoot();
        var example = ConfigLoader.LoadAppConfig(Path.Combine(root, "config", "config.example.json"));
        var rules = ConfigLoader.LoadRules(Path.Combine(root, "config", "rules.example.json"));
        var sample = example.Machines.First(m => m.Enabled);
        var cfg = example with
        {
            Machines = [sample with { Id = "m1", Name = "Maszyna 1", Host = "127.0.0.1", Port = ClosedPort(), Enabled = true }],
            Paths = example.Paths with
            {
                HistoryFile = HistoryFile, LogsDir = Path.Combine(_dir, "logs"),
                CapturesDir = Path.Combine(_dir, "captures"), TestScreensDir = Path.Combine(_dir, "test-ekran"),
            },
        };
        cfg = adjust?.Invoke(cfg) ?? cfg;
        Directory.CreateDirectory(Path.Combine(_dir, "test-ekran"));

        var secrets = new InMemorySecretStore();
        secrets.Set(SecretKeys.VncPassword("m1"), "haslo-testowe");
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, cfg.Timing, acks);
        var supervisor = new MonitorSupervisor(cfg, m => secrets.Get(SecretKeys.VncPassword(m.Id)), new NullOcrEngine(),
            new MessageMapper(rules), alerts, clock, new FileCaptureStore(Path.Combine(_dir, "captures")), Path.Combine(_dir, "test-ekran"));
        var runtime = cluster?.Invoke() ?? new ClusterRuntime(new ClusterConfig(), null, clock, acks, "TEST-PC", "test");
        var power = new PowerModeWatcher();
        var orchestrator = new AppOrchestrator(runtime, supervisor, alerts, new HistoryWriter(HistoryFile, clock), power, clock,
            new StartupConfig(cfg, cfg),
            new SharedConfigSync(new SharedConfigStore(Path.Combine(_dir, "shared-config", "shared-config.json")), "TEST-PC"),
            new NullOcrEngine(), NullLogger<AppOrchestrator>.Instance,
            new AlarmHelpService(rules, Path.Combine(_dir, "alarm-instructions.json"), false, clock: clock), secrets);
        _disposables.Add(supervisor); _disposables.Add(runtime); _disposablesSync.Add(power);
        return (orchestrator, runtime, cfg);
    }

    private static Task Tick(AppOrchestrator orchestrator) =>
        (Task)typeof(AppOrchestrator).GetMethod("TickOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(orchestrator, [CancellationToken.None])!;

    private static void SleepDetected(AppOrchestrator orchestrator) =>
        typeof(AppOrchestrator).GetMethod("OnSleepDetected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(orchestrator, []);

    private static Task? Stopping(AppOrchestrator orchestrator) =>
        (Task?)typeof(AppOrchestrator).GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(orchestrator);

    private static async Task Ticks(AppOrchestrator orchestrator, ManualClock clock, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await Tick(orchestrator);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private static string[] Lines(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() : [];

    // ── Obserwator zapisuje co tik, plik w state\ ────────────────────────────────────────

    [Fact]
    public async Task ObserverWithRunningMonitorsRecordsEveryTickIntoTheStateFolder()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var (orchestrator, _, _) = Build(clock);
        Assert.Equal(Path.GetFullPath(ObservationFile), Path.GetFullPath(Path.Combine(Path.GetDirectoryName(HistoryFile)!, "state", "observation.jsonl")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ObservationFile))); // katalog powstaje przy pierwszym zapisie

        var start = clock.UtcNow;
        await Ticks(orchestrator, clock, 15);

        var interval = Assert.Single(orchestrator.Observation.Snapshot());
        Assert.Equal("m1", interval.MachineId);
        Assert.Equal(ObservationState.NoConnection, interval.State); // zamknięty port — nigdy „obserwowana”
        Assert.Equal(start, interval.Start);
        Assert.Equal(start + TimeSpan.FromSeconds(14), interval.End);
        Assert.Single(Lines(ObservationFile)); // pierwszy zapis po 10 s
        Assert.Null(orchestrator.Observation.Error);
    }

    // ── Uśpienie ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SleepDetectedClosesAtTheLastObservationAndTheRestartNeverBridgesTheSleep()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var (orchestrator, _, _) = Build(clock);
        var start = clock.UtcNow;
        await Ticks(orchestrator, clock, 5); // obserwacje t0..t4
        var lastBeforeSleep = start + TimeSpan.FromSeconds(4);

        SleepDetected(orchestrator);
        Assert.Equal([new ObservationInterval("m1", ObservationState.NoConnection, start, lastBeforeSleep)], orchestrator.Observation.Snapshot());
        Assert.Single(Lines(ObservationFile)); // domknięcie zapisane od razu

        // Wybudzenie: kolejny tik zatrzymuje monitory w tle (wtedy nic nie jest obserwowane), potem start od nowa.
        clock.Advance(TimeSpan.FromMinutes(30));
        await Tick(orchestrator);
        if (Stopping(orchestrator) is { } stopping) await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = clock.UtcNow;
        await Ticks(orchestrator, clock, 5);

        var snapshot = orchestrator.Observation.Snapshot();
        Assert.Contains(new ObservationInterval("m1", ObservationState.NoConnection, start, lastBeforeSleep), snapshot);
        Assert.All(snapshot.Where(i => i.Start > lastBeforeSleep), i => Assert.True(i.Start >= resumed, $"przedział od {i.Start:O}"));
        Assert.DoesNotContain(snapshot, i => i.Start <= lastBeforeSleep && i.End > lastBeforeSleep);
    }

    // ── Utrata roli obserwatora ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LosingTheObserverRoleClosesTheIntervalAndStopsRecording()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        GroupFile? file = null;
        InMemoryPeerHub? hub = null;
        var (orchestrator, runtime, cfg) = Build(clock,
            cluster: () => new ClusterRuntime(new ClusterConfig { Enabled = true, Group = file!.Identity }, file.NetworkKey, clock,
                new AckMemory(clock, TimeSpan.FromHours(2)), "TEST-PC", "test", (c, _) => hub!.CreateBus("TEST-PC", c)),
            adjust: c =>
            {
                file = GroupFile.Create("Hala R1b", GroupProfile.Capture(c, ConfigLoader.LoadRules(
                    Path.Combine(RepositoryRoot(), "config", "rules.example.json")), new Dictionary<string, string>()));
                hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
                return c with { Cluster = new ClusterConfig { Enabled = true, Group = file.Identity } };
            });
        await runtime.StartAsync(default);
        for (int i = 0; i < 40 && !runtime.IsWatcher; i++) { await Tick(orchestrator); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.True(runtime.IsWatcher, "orkiestrator nie został obserwatorem — test nie sprawdzi utraty roli");
        await Ticks(orchestrator, clock, 12);
        Assert.NotEmpty(orchestrator.Observation.Snapshot());

        // Utrata roli: węzeł grupy oddaje ją (ta sama ścieżka co wykryte uśpienie / ręczne ustąpienie).
        // Kandydatem zostaje ten sam komputer, więc rolę odzyska dopiero po TakeoverDelayMs (20 s).
        runtime.GroupNode!.ForceYield();
        var lostAt = clock.UtcNow;
        await Tick(orchestrator);
        Assert.False(runtime.IsWatcher);
        // RecordObservation idzie PRZED _cluster.TickAsync: tik, w którym rola przepada, jeszcze obserwował
        // (monitory działały), a domknięcie przychodzi w następnym tiku — na ostatniej obserwacji, nie później.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Tick(orchestrator);
        var atLoss = orchestrator.Observation.Snapshot();
        Assert.All(atLoss, i => Assert.True(i.End <= lostAt, $"koniec {i.End:O} po utracie roli {lostAt:O}"));
        Assert.Equal(atLoss, new ObservationJournal(ObservationFile).Snapshot()); // domknięte i zapisane

        clock.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 15 && !runtime.IsWatcher; i++)
        {
            await Tick(orchestrator);
            Assert.Equal(atLoss, orchestrator.Observation.Snapshot()); // bez roli nic nie przybywa
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Odzyskanie roli: nowy przedział, czas bez roli nie jest doliczony.
        for (int i = 0; i < 40 && !runtime.IsWatcher; i++) { await Tick(orchestrator); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.True(runtime.IsWatcher);
        if (Stopping(orchestrator) is { } stopping) await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        var regainedAt = clock.UtcNow;
        await Ticks(orchestrator, clock, 5);
        var after = orchestrator.Observation.Snapshot();
        Assert.DoesNotContain(after, i => i.Start < lostAt && i.End > lostAt);
        Assert.Contains(after, i => i.Start >= regainedAt - TimeSpan.FromSeconds(2));
    }

    // ── Zamknięcie programu ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShutdownClosesAndWritesTheOpenInterval()
    {
        var (orchestrator, _, _) = Build(new RealClock());
        await orchestrator.StartAsync(CancellationToken.None);
        for (int waited = 0; waited < 8000 && orchestrator.Observation.Snapshot().Count == 0; waited += 100) await Task.Delay(100);
        await Task.Delay(1500); // jeszcze jeden–dwa tiki
        Assert.Empty(Lines(ObservationFile)); // < 10 s — nic jeszcze nie zapisane

        await orchestrator.StopAsync(CancellationToken.None);

        var written = new ObservationJournal(ObservationFile).Snapshot();
        var interval = Assert.Single(written);
        Assert.Equal("m1", interval.MachineId);
        Assert.True(interval.End > interval.Start);
        Assert.Equal(orchestrator.Observation.Snapshot(), written);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var item in _disposables) { try { await item.DisposeAsync(); } catch (Exception) { } }
        foreach (var item in _disposablesSync) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
