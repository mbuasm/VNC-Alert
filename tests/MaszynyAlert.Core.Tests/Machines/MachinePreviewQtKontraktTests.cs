using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachinePreviewQtKontraktTests
{
    private static Func<IScreenSource> CountingSource(Action opened) => () =>
    {
        opened();
        return new FakeScreenSource("amm520-1", PreviewQtMonitorRig.NormalFrame());
    };

    [Fact]
    public async Task Obserwator_SerwerQtPoPrzejeciuWBezpieczniku_PodgladKomunikatBezPolaczenia()
    {
        // Cykl 4: zerwanie działającej sesji → bezpiecznik przejęcia 1 min (odstępy po 30 s); ponowne połączenie dopiero w cyklu 6.
        var rig = new PreviewQtMonitorRig("opencnc", maxCycles: 5);
        int opened = 0;
        Task<MachinePreviewFrame>? preview = null;
        rig.AfterCycle = cycle =>
        {
            if (cycle == 3) rig.ArmKick();
            if (cycle == 4) preview = rig.Preview(CountingSource(() => opened++)).CaptureAsync("amm520-1", CancellationToken.None);
        };

        await rig.RunAsync();

        Assert.Equal(1, rig.Source.ConnectCount);
        Assert.Contains("inny klient VNC przejął", rig.Monitor.Snapshot.LastError);
        Assert.NotNull(preview);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preview!);
        Assert.Equal(MachinePreviewService.QtPreviewNoConnection + " " + rig.Monitor.Snapshot.LastError, error.Message);
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData("aidite5x")]
    [InlineData("premile")]
    [InlineData("cameo1")]
    [InlineData("aidite-light2")]
    public async Task Obserwator_ProfilZListyQt_ZamknieciePrzedPierwszaKlatka_PodgladKomunikatBezPolaczenia(string profile)
    {
        var rig = new PreviewQtMonitorRig(profile, maxCycles: 4);
        rig.Source.OnConnect = () => { if (rig.Source.ConnectCount == 1) rig.ArmEarlyClose(); };
        int opened = 0;
        Task<MachinePreviewFrame>? preview = null;
        rig.AfterCycle = cycle =>
        {
            if (cycle == 2) preview = rig.Preview(CountingSource(() => opened++)).CaptureAsync("amm520-1", CancellationToken.None);
        };

        await rig.RunAsync();

        Assert.Equal(1, rig.Source.ConnectCount);
        Assert.Contains("Kolejna próba za 5 min", rig.Monitor.Snapshot.LastError);
        Assert.NotNull(preview);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preview!);
        Assert.Equal(MachinePreviewService.QtPreviewNoConnection + " " + rig.Monitor.Snapshot.LastError, error.Message);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Obserwator_SerwerQtZKlatkami_PodgladZwracaKlatkeMonitoraStarszaNiz5sBezDrugiegoPolaczenia()
    {
        var rig = new PreviewQtMonitorRig("opencnc", maxCycles: 12);
        int opened = 0;
        Task<MachinePreviewFrame>? preview = null;
        MachinePreviewFrame? monitorFrame = null;
        long ageAtCallMs = 0;
        rig.AfterCycle = cycle =>
        {
            if (cycle != 10) return;
            monitorFrame = rig.Monitor.PreviewFrame;
            ageAtCallMs = rig.Clock.MonotonicMs - (monitorFrame?.MonotonicMs ?? rig.Clock.MonotonicMs);
            preview = rig.Preview(CountingSource(() => opened++)).CaptureAsync("amm520-1", CancellationToken.None);
        };

        await rig.RunAsync();

        Assert.NotNull(preview);
        MachinePreviewFrame frame = await preview!;
        Assert.NotNull(monitorFrame);
        Assert.Same(monitorFrame, frame);
        Assert.Equal("vnc", frame.Source); // okno: „lokalna obserwacja” + czas klatki
        Assert.True(ageAtCallMs > MachinePreviewService.LocalFrameMaxAge.TotalMilliseconds,
            $"Klatka monitora ma {ageAtCallMs} ms — powinna być starsza niż 5 s, inaczej test nie odróżnia ścieżki Qt od reguły 5 s.");
        Assert.Equal(1, rig.Source.ConnectCount);
        Assert.Equal(0, opened);
    }
}
