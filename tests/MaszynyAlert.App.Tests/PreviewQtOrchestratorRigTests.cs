using System.IO;
using MaszynyAlert.App.Services;

namespace MaszynyAlert.App.Tests;

public sealed class PreviewQtOrchestratorRigTests
{
    [Fact]
    public async Task DisposeAsync_ZatrzymujeMonitoryZamykaPolaczenieZHmiIKasujeKatalog()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: true);
        var rig = new PreviewQtOrchestratorRig();
        bool disposed = false;
        try
        {
            AppOrchestrator orchestrator = rig.Build(hmi.Port, "opencnc");
            string dir = Path.GetDirectoryName(rig.TestScreensDir)!;
            await rig.StartObserving(orchestrator);
            await PreviewQtOrchestratorRig.WaitUntil(
                () => orchestrator.Supervisor.Get(PreviewQtOrchestratorRig.MachineId)?.PreviewFrame is not null, "pierwsza klatka monitora");
            // Przed sprzątaniem: monitor działa i trzyma jedno połączenie z HMI, katalog istnieje.
            Assert.True(orchestrator.Supervisor.IsRunning);
            Assert.Equal(1, hmi.Open);
            Assert.True(Directory.Exists(dir));

            disposed = true;
            await rig.DisposeAsync();

            Assert.False(orchestrator.Supervisor.IsRunning);
            await PreviewQtOrchestratorRig.WaitUntil(() => hmi.Open == 0, "zamknięcie połączenia monitora z HMI po sprzątaniu");
            Assert.False(Directory.Exists(dir), $"Katalog {dir} został po sprzątaniu.");
            await Task.Delay(300);
            Assert.Equal(1, hmi.Accepted); // zatrzymany monitor nie łączy się ponownie
        }
        finally
        {
            if (!disposed) await rig.DisposeAsync();
        }
    }
}
