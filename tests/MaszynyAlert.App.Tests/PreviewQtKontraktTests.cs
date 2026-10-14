using System.IO;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.App.Tests;

public sealed class PreviewQtKontraktTests : IAsyncLifetime
{
    private const string MachineId = PreviewQtOrchestratorRig.MachineId;
    private readonly PreviewQtOrchestratorRig _rig = new();

    [Fact]
    public async Task Obserwator_SerwerQtZamykaPrzedPierwszaKlatka_ProfilAidite5x_PodgladKomunikatBezPolaczenia()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: false);
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "aidite5x");
        await _rig.StartObserving(orchestrator);
        await PreviewQtOrchestratorRig.WaitUntil(() => PreviewQtOrchestratorRig.Snapshot(orchestrator).LastError?
            .Contains("Kolejna próba za 5 min", StringComparison.Ordinal) == true, "bezpiecznik Qt po zamknięciu przed pierwszą klatką");

        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(300);

        Assert.Equal(1, hmi.Accepted);
        var unavailable = Assert.IsType<InvalidOperationException>(error);
        Assert.StartsWith(MachinePreviewService.QtPreviewNoConnection, unavailable.Message);
        Assert.Contains("Kolejna próba za 5 min", unavailable.Message);
        Assert.DoesNotContain("jeszcze", unavailable.Message);
        Assert.DoesNotContain("komputerze-obserwatorze", unavailable.Message);
    }

    [Fact]
    public async Task Obserwator_SerwerQtZKlatkami_ProfilOpencnc_PodgladZwracaKlatkeMonitoraStarszaNiz5sBezDrugiegoPolaczenia()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: true);
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "opencnc");
        await _rig.StartObserving(orchestrator);
        await PreviewQtOrchestratorRig.WaitUntil(() => orchestrator.Supervisor.Get(MachineId)?.PreviewFrame is not null
            && QtEmbeddedVncServer.IsQtEmbedded(PreviewQtOrchestratorRig.Snapshot(orchestrator).Info?.DesktopName), "pierwsza klatka monitora");
        MachinePreviewFrame monitorFrame = orchestrator.Supervisor.Get(MachineId)!.PreviewFrame!;
        _rig.Clock.Advance(TimeSpan.FromSeconds(7));

        MachinePreviewFrame frame = await orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None);
        await Task.Delay(300);

        Assert.Same(monitorFrame, frame);
        Assert.Equal("vnc", frame.Source); // okno: „lokalna obserwacja”
        Assert.Equal(1, hmi.Accepted);
    }

    [Fact]
    public async Task KomputerBezObserwacji_ProfilAidite5x_PodgladKomunikatBezPolaczenia()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: true);
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "aidite5x");
        Assert.False(orchestrator.Supervisor.IsRunning);

        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(200);

        Assert.Equal(0, hmi.Accepted);
        var unavailable = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(MachinePreviewService.QtPreviewUnavailable, unavailable.Message);
    }

    [Fact]
    public async Task MaszynaWylaczonaWPaneluD3_PodgladNieLaczySie()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: true);
        AppOrchestrator orchestrator = _rig.Build(hmi.Port, "opencnc");
        await _rig.StartObserving(orchestrator);
        await PreviewQtOrchestratorRig.WaitUntil(() => orchestrator.Supervisor.Get(MachineId)?.PreviewFrame is not null, "pierwsza klatka monitora");

        await orchestrator.ApplySharedConfigAsync(null, null, new Dictionary<string, bool> { [MachineId] = false });
        Assert.False(orchestrator.Config.Machines.Single(m => m.Id == MachineId).Enabled);
        int before = hmi.Accepted;

        Exception? error = await Record.ExceptionAsync(() => orchestrator.Preview.CaptureAsync(MachineId, CancellationToken.None));
        await Task.Delay(300);

        Assert.Equal(before, hmi.Accepted);
        var disabled = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("Maszyna nie jest włączona w bieżącej konfiguracji.", disabled.Message);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask();
}

public sealed class ScreenPreviewWindowQtKontraktTests
{
    private const string MachineId = "amm520-1";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now => UtcNow;
        public long MonotonicMs => 10_000_000;
    }

    private static MachinePreviewService QtService(Func<string, MachinePreviewFrame?> localFrame, Func<AppConfig>? configuration = null)
    {
        var config = new AppConfig
        {
            Enabled = true,
            Machines = [new MachineConfig { Id = MachineId, Name = "520 (1)", Host = "127.0.0.1", Port = 5900, Profile = "aidite5x" }],
        };
        return new MachinePreviewService(configuration ?? (() => config), localFrame,
            (_, _) => throw new InvalidOperationException("Podgląd maszyny Qt nie może otwierać połączenia."),
            new FixedClock(), _ => QtEmbeddedVncServer.DesktopName);
    }

    private static T WithWindow<T>(MachinePreviewService service, Func<ScreenPreviewWindow, T> inspect) => WpfTestHost.Run(() =>
    {
        var window = WpfTestHost.ShowOffScreen(new ScreenPreviewWindow(service, MachineId, "520 (1)"));
        try
        {
            for (int i = 0; i < 50 && !window.RefreshCompletion.IsCompleted; i++) WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            Assert.True(window.RefreshCompletion.IsCompleted);
            return inspect(window);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SerwerQt_KlatkaSprzed20Min_OknoPokazujeCzasKlatkiIZrodloLokalnaObserwacja()
    {
        var clock = new FixedClock();
        var image = new RgbaImage(32, 24);
        image.Fill(new Rgba(10, 20, 30));
        var captured = clock.UtcNow.AddMinutes(-20);
        var local = new MachinePreviewFrame(image, captured, clock.MonotonicMs - 20 * 60_000, "vnc");

        (string text, bool hasImage, bool refreshEnabled) = WithWindow(QtService(_ => local),
            w => (w.StatusText.Text, w.PreviewImage.Source is not null, w.RefreshButton.IsEnabled));

        Assert.StartsWith($"Obraz z {captured.ToLocalTime():HH:mm:ss} · lokalna obserwacja", text);
        Assert.True(hasImage);
        Assert.True(refreshEnabled);
    }

    [Fact]
    public void SerwerQt_BezKlatki_OknoPokazujePelnyKomunikat()
    {
        (string text, bool hasImage, bool refreshEnabled) = WithWindow(QtService(_ => null),
            w => (w.StatusText.Text, w.PreviewImage.Source is not null, w.RefreshButton.IsEnabled));

        Assert.Equal(MachinePreviewService.QtPreviewUnavailable, text);
        Assert.False(hasImage);
        Assert.True(refreshEnabled);
    }

    [Fact]
    public void WyjatekZSerwisu_OknoNiePadaIPokazujeKomunikat()
    {
        var service = QtService(_ => null, () => throw new IOException("konfiguracja nieczytelna (test)"));

        (string text, bool visible, bool refreshEnabled) = WithWindow(service,
            w => (w.StatusText.Text, w.IsVisible, w.RefreshButton.IsEnabled));

        Assert.StartsWith("Nie można pobrać ekranu.", text);
        Assert.True(visible);
        Assert.True(refreshEnabled);
    }
}
