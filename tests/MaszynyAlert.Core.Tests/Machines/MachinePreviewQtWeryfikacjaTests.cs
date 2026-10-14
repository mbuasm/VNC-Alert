using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachinePreviewQtWeryfikacjaTests
{
    [Fact]
    public async Task Monitor_SerwerQtZamknalPrzedPierwszaKlatka_MigawkaNiesieNazwePulpituZHandshakeu()
    {
        var rig = new PreviewQtMonitorRig("opencnc", maxCycles: 4);
        rig.Source.OnConnect = () => { if (rig.Source.ConnectCount == 1) rig.ArmEarlyClose(); };

        await rig.RunAsync();

        Assert.Equal(1, rig.Source.ConnectCount);
        Assert.Contains("Kolejna próba za 5 min", rig.Monitor.Snapshot.LastError);
        Assert.Contains("zamknął połączenie przed pierwszą klatką", rig.Monitor.Snapshot.LastError);
        Assert.True(QtEmbeddedVncServer.IsQtEmbedded(rig.Monitor.Snapshot.Info?.DesktopName),
            $"Migawka monitora po udanym handshake'u z serwerem Qt: Info.DesktopName = „{rig.Monitor.Snapshot.Info?.DesktopName ?? "null"}” " +
            "— Podgląd (AppOrchestrator: Snapshot.Info?.DesktopName) nie rozpozna serwera Qt i przy profilu spoza QtProfiles otworzy " +
            "połączenie z HMI w czasie bezpiecznika.");
    }
}
