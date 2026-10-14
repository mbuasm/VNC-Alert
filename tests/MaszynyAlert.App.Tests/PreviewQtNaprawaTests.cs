using System.IO;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.App.Tests;

public sealed class PreviewQtNaprawaTests : IAsyncLifetime
{
    private const string MachineId = PreviewQtOrchestratorRig.MachineId;
    private readonly PreviewQtOrchestratorRig _rig = new();

    [Fact]
    public async Task PodgladNaprawa01_Obserwator_PrzebudowaMonitorowWTrakcieBezpiecznika_ProfilOpencnc_ZeroNowychPolaczen()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: false);
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "opencnc");
        await _rig.StartObserving(orchestrator);
        await PreviewQtOrchestratorRig.WaitUntil(() => PreviewQtOrchestratorRig.Snapshot(orchestrator).LastError?
            .Contains("Kolejna próba za 5 min", StringComparison.Ordinal) == true, "bezpiecznik Qt po zamknięciu przed pierwszą klatką");
        Assert.Equal(1, hmi.Accepted);
        MachineMonitor before = orchestrator.Supervisor.Get(MachineId)!;

        TimingConfig timing = orchestrator.Config.Timing with { OfflineAlertAfterMs = orchestrator.Config.Timing.OfflineAlertAfterMs + 1000 };
        await orchestrator.ApplySharedConfigAsync(timing, null, null);
        await PreviewQtOrchestratorRig.WaitUntil(() => orchestrator.Supervisor.Get(MachineId) is { } m && !ReferenceEquals(m, before),
            "nowy monitor po przebudowie");
        Assert.True(QtEmbeddedVncServer.IsQtEmbedded(PreviewQtOrchestratorRig.Snapshot(orchestrator).Info?.DesktopName));

        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(1500);

        Assert.Equal(1, hmi.Accepted); // ani nowy monitor, ani Podgląd nie połączyli się z HMI
        // Obserwator: komunikat o braku połączenia z opisem wstrzymania sprzed przebudowy.
        string message = Assert.IsType<InvalidOperationException>(error).Message;
        Assert.StartsWith(MachinePreviewService.QtPreviewNoConnection, message);
        Assert.Contains("Kolejna próba za 5 min", message);
    }

    /// <summary>
    /// Ten komputer jako członek grupy BEZ obserwacji: obserwator (inny komputer, pierwszeństwo 1) rozsyła migawkę maszyny
    /// z podaną nazwą pulpitu (STATE); zwraca orkiestrator po ustaleniu ról (monitory tego komputera zatrzymane).
    /// </summary>
    private async Task<(AppOrchestrator Orchestrator, GroupClusterNode Observer)> NonObservingGroupMemberAsync(
        FakeQtVncServer hmi, string profile, string? observerDesktopName)
    {
        string rulesPath = Path.Combine(PreviewQtOrchestratorRig.RepositoryRoot(), "config", "rules.example.json");
        GroupFile? file = null;
        InMemoryPeerHub? hub = null;
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, profile, cfg =>
        {
            var created = GroupFile.Create("Hala podgląd", GroupProfile.Capture(cfg, ConfigLoader.LoadRules(rulesPath), new Dictionary<string, string>()));
            var createdHub = new InMemoryPeerHub(HmacSigner.FromBase64(created.NetworkKey), _rig.Clock);
            file = created;
            hub = createdHub;
            var runtime = new ClusterRuntime(new ClusterConfig { Enabled = true, Group = created.Identity }, created.NetworkKey, _rig.Clock,
                new AckMemory(_rig.Clock, TimeSpan.FromHours(2)), "UI-PC", "test", (c, _) => createdHub.CreateBus("UI-PC", c));
            return (runtime, cfg with { Cluster = new ClusterConfig { Enabled = true, Group = created.Identity } });
        });
        var observer = new GroupClusterNode(new ClusterConfig { Enabled = true, Group = file!.Identity, Priority = 1 },
            hub!.CreateBus("OBS-PC"), _rig.Clock, "test");
        observer.SetReadiness(new WatcherReadiness(true, false, []));
        await observer.StartAsync(default);
        await orchestrator.Cluster.StartAsync(default);
        var observed = new MachineStatusSnapshot
        {
            MachineId = MachineId, Name = "520 (1)", Host = "127.0.0.1", Port = hmi.Port, Profile = profile,
            State = MachineConnectionState.Connected, Source = "vnc",
            Info = observerDesktopName is null
                ? null
                : new ScreenSourceInfo("vnc", "3.3", "None", "16bpp depth 16 RGB565 LE", 1024, 768, observerDesktopName),
        };

        bool ready = false;
        for (int i = 0; i < 60 && !ready; i++)
        {
            if (observer.IsWatcher) observer.SetLocalState([], [observed with { LastFrameAt = _rig.Clock.UtcNow, UpdatedAt = _rig.Clock.UtcNow }]);
            await observer.TickAsync(default);
            await PreviewQtOrchestratorRig.Tick(orchestrator);
            _rig.Clock.Advance(TimeSpan.FromSeconds(1));
            ready = observer.IsWatcher && !orchestrator.Cluster.IsWatcher && !orchestrator.Supervisor.IsRunning
                && orchestrator.Cluster.RemoteMachines.Any(m => m.MachineId == MachineId);
        }

        Assert.True(ready, "Ten komputer nie przeszedł w rolę bez obserwacji albo nie dostał migawki obserwatora.");
        await orchestrator.Supervisor.StoppedTask;
        await hmi.SettleAsync();
        return (orchestrator, observer);
    }

    [Fact]
    public async Task PodgladNaprawa02_KomputerGrupyBezObserwacji_ObserwatorZnaSerwerInnyNizQt_ProfilOpencnc_PodgladJakDotadLaczySie()
    {
        await using var hmi = new FakeQtVncServer(handshake: false, serveFrames: false);
        (AppOrchestrator orchestrator, GroupClusterNode observer) = await NonObservingGroupMemberAsync(hmi, "opencnc", "WinCE");
        await using GroupClusterNode _ = observer;

        int before = hmi.Accepted;
        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(300);

        Assert.Equal(before + 1, hmi.Accepted); // odczyt na żądanie (atrapa zamyka bez handshake'u → błąd, ale próba była)
        Assert.False(error is InvalidOperationException { Message: var m } && m.StartsWith("Podgląd niedostępny", StringComparison.Ordinal),
            $"Serwer inny niż Qt dostał komunikat Qt: {error?.Message}");
    }

    [Fact]
    public async Task PodgladNaprawa02_KomputerGrupyBezObserwacji_ObserwatorZnaSerwerQt_ProfilAidite5x_DalejBezPolaczenia()
    {
        await using var hmi = new FakeQtVncServer(handshake: false, serveFrames: false);
        (AppOrchestrator orchestrator, GroupClusterNode observer) =
            await NonObservingGroupMemberAsync(hmi, "aidite5x", QtEmbeddedVncServer.DesktopName);
        await using GroupClusterNode _ = observer;

        int before = hmi.Accepted;
        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(300);

        Assert.Equal(before, hmi.Accepted);
        Assert.Equal(MachinePreviewService.QtPreviewUnavailable, Assert.IsType<InvalidOperationException>(error).Message);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask();
}
