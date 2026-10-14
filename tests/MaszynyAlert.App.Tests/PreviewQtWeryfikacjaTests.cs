using System.IO;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.App.Tests;

public sealed class PreviewQtWeryfikacjaTests : IAsyncLifetime
{
    private const string MachineId = PreviewQtOrchestratorRig.MachineId;
    private readonly PreviewQtOrchestratorRig _rig = new();


    [Fact]
    public async Task Obserwator_SerwerQtZamykaPrzedPierwszaKlatka_ProfilOpencnc_PodgladNieLaczySieZHmiWCzasieBezpiecznika()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: false);
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "opencnc");
        await _rig.StartObserving(orchestrator);
        await PreviewQtOrchestratorRig.WaitUntil(() => PreviewQtOrchestratorRig.Snapshot(orchestrator).LastError?
            .Contains("Kolejna próba za 5 min", StringComparison.Ordinal) == true, "bezpiecznik Qt po zamknięciu przed pierwszą klatką");
        Assert.Equal(1, hmi.Accepted);
        Assert.Contains("zamknął połączenie przed pierwszą klatką", PreviewQtOrchestratorRig.Snapshot(orchestrator).LastError);

        // Operator widzi „Brak łączności” i klika „Pokaż ekran”.
        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(300);

        Assert.True(hmi.Accepted == 1,
            $"Podgląd otworzył drugie połączenie z HMI Qt w czasie bezpiecznika (5 min): połączeń {hmi.Accepted}; nazwa pulpitu " +
            $"w migawce: „{PreviewQtOrchestratorRig.Snapshot(orchestrator).Info?.DesktopName ?? "null"}”; wynik podglądu: " +
            $"{error?.GetType().Name ?? "obraz"}: {error?.Message}");
        var unavailable = Assert.IsType<InvalidOperationException>(error);
        Assert.StartsWith(MachinePreviewService.QtPreviewNoConnection, unavailable.Message);
        Assert.Contains("Kolejna próba za 5 min", unavailable.Message);
    }


    [Fact]
    public async Task KomputerGrupyBezObserwacji_ObserwatorZnaSerwerQt_ProfilOpencnc_PodgladNieOtwieraDrugiegoPolaczenia()
    {
        await using var hmi = new FakeQtVncServer(handshake: false, serveFrames: false);
        string rulesPath = Path.Combine(PreviewQtOrchestratorRig.RepositoryRoot(), "config", "rules.example.json");
        GroupFile? file = null;
        InMemoryPeerHub? hub = null;
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "opencnc", cfg =>
        {
            var created = GroupFile.Create("Hala podgląd", GroupProfile.Capture(cfg, ConfigLoader.LoadRules(rulesPath), new Dictionary<string, string>()));
            var createdHub = new InMemoryPeerHub(HmacSigner.FromBase64(created.NetworkKey), _rig.Clock);
            file = created;
            hub = createdHub;
            var runtime = new ClusterRuntime(new ClusterConfig { Enabled = true, Group = created.Identity }, created.NetworkKey, _rig.Clock,
                new AckMemory(_rig.Clock, TimeSpan.FromHours(2)), "UI-PC", "test", (c, _) => createdHub.CreateBus("UI-PC", c));
            return (runtime, cfg with { Cluster = new ClusterConfig { Enabled = true, Group = created.Identity } });
        });
        await using var observer = new GroupClusterNode(new ClusterConfig { Enabled = true, Group = file!.Identity, Priority = 1 },
            hub!.CreateBus("OBS-PC"), _rig.Clock, "test");
        observer.SetReadiness(new WatcherReadiness(true, false, []));
        await observer.StartAsync(default);
        await orchestrator.Cluster.StartAsync(default);
        var observed = new MachineStatusSnapshot
        {
            MachineId = MachineId, Name = "520 (1)", Host = "127.0.0.1", Port = hmi.Port, Profile = "opencnc",
            State = MachineConnectionState.Connected, Source = "vnc",
            Info = new ScreenSourceInfo("vnc", "3.3", "None", "16bpp depth 16 RGB565 LE", 1024, 768, QtEmbeddedVncServer.DesktopName),
        };

        // Ten komputer obserwuje przy starcie, aż obserwator z pierwszeństwem przejmie rolę (~20 s) — potem zatrzymuje monitory.
        bool ready = false;
        var trace = new List<string>();
        for (int i = 0; i < 60 && !ready; i++)
        {
            if (observer.IsWatcher) observer.SetLocalState([], [observed with { LastFrameAt = _rig.Clock.UtcNow, UpdatedAt = _rig.Clock.UtcNow }]);
            await observer.TickAsync(default);
            await PreviewQtOrchestratorRig.Tick(orchestrator);
            _rig.Clock.Advance(TimeSpan.FromSeconds(1));
            ready = observer.IsWatcher && !orchestrator.Cluster.IsWatcher && !orchestrator.Supervisor.IsRunning
                && orchestrator.Cluster.RemoteMachines
                    .Any(m => m.MachineId == MachineId && QtEmbeddedVncServer.IsQtEmbedded(m.Info?.DesktopName));
            trace.Add($"{i}:obs={observer.IsWatcher},ten={orchestrator.Cluster.IsWatcher},monitory={orchestrator.Supervisor.IsRunning}");
        }

        Assert.True(ready, "Ten komputer nie dostał migawki obserwatora z nazwą pulpitu Qt (albo ma działające monitory) — test nie " +
            "sprawdzi podglądu. Przebieg: " + string.Join(" ", trace));

        int before = hmi.Accepted;
        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(300);

        Assert.True(hmi.Accepted == before,
            $"Podgląd na komputerze bez obserwacji otworzył {hmi.Accepted - before} połączenie(a) z maszyną Qt, choć migawka obserwatora " +
            $"(ClusterRuntime.RemoteMachines) niesie nazwę pulpitu „{QtEmbeddedVncServer.DesktopName}” — na hali to zerwanie sesji " +
            $"obserwatora (bezpiecznik przejęcia 1 min i WRN „sprawdź, czy RealVNC nie łączy się”). Wynik podglądu: " +
            $"{error?.GetType().Name ?? "obraz"}: {error?.Message}");
        var unavailable = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(MachinePreviewService.QtPreviewUnavailable, unavailable.Message);
    }


    [Fact]
    public async Task KomputerBezObserwacji_MaszynaQtZPlikiemTestEkran_PodgladPokazujePlikBezVnc()
    {
        await using var hmi = new FakeQtVncServer(handshake: false, serveFrames: false);
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "aidite5x");
        var image = new RgbaImage(64, 48);
        image.Fill(new Rgba(200, 30, 30));
        File.WriteAllBytes(Path.Combine(_rig.TestScreensDir, MachineId + ".png"), PngCodec.Encode(image));
        Assert.False(orchestrator.Supervisor.IsRunning); // monitory nie działają (jak na komputerze grupy bez obserwacji)

        Exception? error = null;
        MachinePreviewFrame? frame = null;
        try { frame = await orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None); }
        catch (Exception ex) { error = ex; }
        await Task.Delay(200);

        Assert.Equal(0, hmi.Accepted); // bez VNC
        Assert.True(frame is not null,
            $"Plik test-ekran maszyny Qt nie jest pokazywany (na 86220e1 był — openSource zwracał plik): {error?.GetType().Name}: {error?.Message}");
        Assert.Equal("plik", frame!.Source);
        Assert.Equal(64, frame.Image.Width);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask();
}
