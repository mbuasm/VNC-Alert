using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
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

public sealed class AppOrchestratorMachinesTests : IAsyncLifetime
{
    private sealed class ManualClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;
        public DateTimeOffset Now => UtcNow;
        public long MonotonicMs { get; private set; } = 1_000_000;
        public void Advance(TimeSpan delta) { UtcNow += delta; MonotonicMs += (long)delta.TotalMilliseconds; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d3-orchestrator-" + Guid.NewGuid().ToString("N"));
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

    private string SharedConfigFile => Path.Combine(_dir, "shared-config", "shared-config.json");

    private sealed record Built(AppOrchestrator Orchestrator, AlertStateMachine Alerts, AppConfig Config, List<AlertEvent> Events);

    /// <summary>Dwie maszyny: m1 włączona (hasło zapisane, zamknięty port), m2 wyłączona (bez hasła, wymaga hasła).</summary>
    private Built Build(IClock clock)
    {
        string root = RepositoryRoot();
        var example = ConfigLoader.LoadAppConfig(Path.Combine(root, "config", "config.example.json"));
        var rules = ConfigLoader.LoadRules(Path.Combine(root, "config", "rules.example.json"));
        var sample = example.Machines.First(m => m.Enabled);
        var cfg = example with
        {
            Machines =
            [
                sample with { Id = "m1", Name = "Maszyna 1", Host = "127.0.0.1", Port = ClosedPort(), Enabled = true, RequiresPassword = true },
                sample with { Id = "m2", Name = "Maszyna 2", Host = "127.0.0.1", Port = ClosedPort(), Enabled = false, RequiresPassword = true },
            ],
            Paths = example.Paths with
            {
                HistoryFile = Path.Combine(_dir, "data", "history.csv"), LogsDir = Path.Combine(_dir, "logs"),
                CapturesDir = Path.Combine(_dir, "captures"), TestScreensDir = Path.Combine(_dir, "test-ekran"),
            },
        };
        Directory.CreateDirectory(Path.Combine(_dir, "test-ekran"));

        var secrets = new InMemorySecretStore();
        secrets.Set(SecretKeys.VncPassword("m1"), "haslo-testowe");
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, cfg.Timing, acks);
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var supervisor = new MonitorSupervisor(cfg, m => secrets.Get(SecretKeys.VncPassword(m.Id)), new NullOcrEngine(),
            new MessageMapper(rules), alerts, clock, new FileCaptureStore(Path.Combine(_dir, "captures")), Path.Combine(_dir, "test-ekran"));
        var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, acks, "TEST-PC", "test");
        var power = new PowerModeWatcher();
        var orchestrator = new AppOrchestrator(runtime, supervisor, alerts, new HistoryWriter(cfg.Paths.HistoryFile, clock), power, clock,
            new StartupConfig(cfg, cfg), new SharedConfigSync(new SharedConfigStore(SharedConfigFile), "TEST-PC"),
            new NullOcrEngine(), NullLogger<AppOrchestrator>.Instance,
            new AlarmHelpService(rules, Path.Combine(_dir, "alarm-instructions.json"), false, clock: clock), secrets);
        _disposables.Add(supervisor); _disposables.Add(runtime); _disposablesSync.Add(power);
        return new Built(orchestrator, alerts, cfg, events);
    }

    private static Task Tick(AppOrchestrator orchestrator) =>
        (Task)typeof(AppOrchestrator).GetMethod("TickOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(orchestrator, [CancellationToken.None])!;

    private static async Task Ticks(AppOrchestrator orchestrator, ManualClock clock, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await Tick(orchestrator);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private static MappedMessage TrayMessage() =>
        new("tray-grab", "Nie udało się pobrać tacki", "tacka", "Failed to grab a tray", "FAILED TO GRAB A TRAY", true);

    private static async Task<MachineStatusSnapshot> WaitForError(AppOrchestrator orchestrator, string machineId)
    {
        for (int waited = 0; waited < 10_000; waited += 100)
        {
            MachineStatusSnapshot snapshot = orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == machineId);
            if (!string.IsNullOrEmpty(snapshot.LastError)) return snapshot;
            await Task.Delay(100);
        }
        return orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == machineId);
    }

    [Fact]
    public async Task SwitchFromThePanel_RebuildsMonitorsHot_ClosesAlertsOfTheDisabledMachine_AndEndsItsObservation()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
        Built b = Build(clock);
        AppOrchestrator orchestrator = b.Orchestrator;
        await Ticks(orchestrator, clock, 3);
        Assert.True(orchestrator.Supervisor.IsRunning);
        Assert.Equal(MachineConnectionState.Disabled, orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == "m2").State);
        Alert raised = b.Alerts.OnRedConfirmed(b.Config.Machines[0], TrayMessage(), null);
        Assert.Contains(orchestrator.VisibleAlerts, a => a.Key == raised.Key);
        int machinesChanged = 0, configChanged = 0;
        orchestrator.MachinesChanged += () => machinesChanged++;
        orchestrator.ConfigChanged += () => configChanged++;
        var switchedAt = clock.UtcNow;

        await orchestrator.ApplySharedConfigAsync(null, null, new Dictionary<string, bool> { ["m1"] = false, ["m2"] = true });

        // Konfiguracja efektywna i plik warstwy.
        Assert.False(orchestrator.Config.Machines.Single(m => m.Id == "m1").Enabled);
        Assert.True(orchestrator.Config.Machines.Single(m => m.Id == "m2").Enabled);
        Assert.True(orchestrator.BaseConfig.Machines.Single(m => m.Id == "m1").Enabled); // baza (config.json) bez zmian
        Assert.True(orchestrator.SharedConfig.Current!.Machines!["m2"].Enabled);
        Assert.True(File.Exists(SharedConfigFile));
        Assert.True(machinesChanged >= 1); // raz z zastosowania, potem z migawek nowych monitorów
        Assert.Equal(1, configChanged);

        // Monitory: m1 Disabled, m2 obserwowana — bez zapisanego hasła monitor powstaje jak przy starcie programu z
        // enabled:true bez hasła (zwykła karta z błędem połączenia / „brak hasła”, nie wyjątek); tu port jest zamknięty,
        // więc błąd to odmowa połączenia.
        Assert.True(orchestrator.Supervisor.IsRunning);
        Assert.Equal(MachineConnectionState.Disabled, orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == "m1").State);
        MachineStatusSnapshot m2 = await WaitForError(orchestrator, "m2");
        Assert.NotEqual(MachineConnectionState.Disabled, m2.State);
        Assert.False(string.IsNullOrEmpty(m2.LastError));

        // Alert m1 zamknięty od razu (epizod zakończony), zniknął z widocznych.
        AlertEvent closed = Assert.Single(b.Events, e => e.Kind == AlertEventKind.Closed);
        Assert.Equal(raised.Key, closed.Alert.Key);
        Assert.Equal(AlertStatus.Closed, closed.Alert.Status);
        Assert.DoesNotContain(orchestrator.VisibleAlerts, a => a.MachineId == "m1");

        // Dziennik czasu obserwacji: przedział m1 kończy się na ostatniej obserwacji, m2 dostaje nowy.
        await Ticks(orchestrator, clock, 12);
        var snapshot = orchestrator.Observation.Snapshot();
        Assert.All(snapshot.Where(i => i.MachineId == "m1"), i => Assert.True(i.End <= switchedAt, $"m1 obserwowana po wyłączeniu: {i.End:O}"));
        Assert.Contains(snapshot, i => i.MachineId == "m2" && i.Start >= switchedAt);

        // Z powrotem: m1 włączona — monitor wraca, m2 wyłączona.
        await orchestrator.ApplySharedConfigAsync(null, null, new Dictionary<string, bool> { ["m1"] = true, ["m2"] = false });
        Assert.NotEqual(MachineConnectionState.Disabled, orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == "m1").State);
        Assert.Equal(MachineConnectionState.Disabled, orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == "m2").State);
    }

    [Fact]
    public async Task SwitchArrivingThroughTheLayerFile_IsAppliedHotLikeAChangeFromAnotherComputer()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
        Built b = Build(clock);
        AppOrchestrator orchestrator = b.Orchestrator;
        await Ticks(orchestrator, clock, 3);
        Assert.True(orchestrator.Supervisor.IsRunning);

        // Inny komputer (albo komenda) zapisał przełącznik w pliku warstwy — program czyta plik i stosuje.
        new SharedConfigStore(SharedConfigFile).Save(null, null, clock.UtcNow, "PC-B", machines: new Dictionary<string, bool> { ["m2"] = true });
        await orchestrator.ReloadSharedConfigFromDiskAsync();

        Assert.True(orchestrator.Config.Machines.Single(m => m.Id == "m2").Enabled);
        Assert.True(orchestrator.Config.Machines.Single(m => m.Id == "m1").Enabled); // bez wpisu = jak w config.json
        Assert.NotEqual(MachineConnectionState.Disabled, orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == "m2").State);
        Assert.Equal(2, orchestrator.Supervisor.Snapshots.Count);
    }

    [Fact]
    public async Task SavingTimingOnly_DoesNotTouchTheSwitchesSavedMeanwhileInTheFile()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
        Built b = Build(clock);
        AppOrchestrator orchestrator = b.Orchestrator;
        await Ticks(orchestrator, clock, 2);
        new SharedConfigStore(SharedConfigFile).Save(null, null, clock.UtcNow, "PC-B", machines: new Dictionary<string, bool> { ["m2"] = true });

        await orchestrator.ApplySharedConfigAsync(b.Config.Timing with { AlertSoundRepeatMs = 30000 }, null, null);

        SharedConfigDocument file = new SharedConfigStore(SharedConfigFile).Load()!;
        Assert.True(file.Machines!["m2"].Enabled);
        Assert.Equal(30000, file.Timing!.AlertSoundRepeatMs);
        // Zapis czasu z Panelu wczytuje dokument faktycznie zapisany — razem z przełącznikiem z pliku.
        Assert.True(orchestrator.Config.Machines.Single(m => m.Id == "m2").Enabled);
    }

    [Fact]
    public async Task PanelInputFromTheOrchestrator_UsesTheReadinessPasswordCheck_AndWarnsBeforeEnablingAMachineWithoutPassword()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        Built b = Build(clock);
        AppOrchestrator orchestrator = b.Orchestrator;
        await Ticks(orchestrator, clock, 2);
        SharedConfigViewInput input = SharedConfigViewInput.From(orchestrator);
        Assert.False(input.InGroup);
        Assert.NotNull(input.HasLocalVncPassword);
        Assert.True(input.HasLocalVncPassword!("m1"));
        Assert.False(input.HasLocalVncPassword("m2"));
        Assert.Empty(orchestrator.GetWatcherReadiness().MissingPasswordMachineIds);
        var confirmation = new SettingsChangeConfirmationTests.RecordingConfirmation(true);
        var vm = new SharedConfigViewModel(() => SharedConfigViewInput.From(orchestrator),
            (timing, overlay, machines, ct) => orchestrator.ApplySharedConfigAsync(timing, overlay, machines, ct), confirmation);

        vm.MachineFields.Single(f => f.Key == SharedConfigViewModel.MachineKey("m2")).Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        SettingChange warning = Assert.Single(Assert.Single(confirmation.Asked), c => c.IsWarning);
        Assert.Equal(SharedConfigViewModel.MissingPasswordWarningName("Maszyna 2"), warning.Name);
        Assert.True(orchestrator.Config.Machines.Single(m => m.Id == "m2").Enabled);
        Assert.Equal(["m2"], orchestrator.GetWatcherReadiness().MissingPasswordMachineIds);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var item in _disposables) { try { await item.DisposeAsync(); } catch (Exception) { } }
        foreach (var item in _disposablesSync) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
