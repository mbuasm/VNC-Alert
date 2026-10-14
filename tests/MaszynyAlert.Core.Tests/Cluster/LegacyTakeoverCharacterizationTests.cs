using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>
/// evidence of the legacy handover gap, NOT v2 acceptance tests.
/// The existing ClusterRegressionTests separately reproduce heartbeat-without-STATE dropping known alerts.
/// </summary>
public sealed class LegacyTakeoverCharacterizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyHandover_RemembersAckButLosesOriginalEpisodeStart(bool acknowledged)
    {
        await using var cluster = new TestCluster();
        var a = await cluster.AddAsync("PC-A", priority: 10);
        var b = await cluster.AddAsync("PC-B", priority: 20);
        await cluster.TickSecondsAsync(25);
        var machine = TestPaths.ExampleConfig.Machines.First(m => m.Enabled);
        var message = new MappedMessage("code-705", "Servo 6", "restart", "No.705", "NO 705", true);
        var originalState = new AlertStateMachine(cluster.Clock, new TimingConfig(), a.Acks);
        var takeoverState = new AlertStateMachine(cluster.Clock, new TimingConfig(), b.Acks);
        a.Node.AckReceived += originalState.ApplyRemoteAck;
        b.Node.AckReceived += takeoverState.ApplyRemoteAck;
        var original = originalState.OnRedConfirmed(machine, message, null);
        a.Node.SetLocalState(originalState.ActiveAlerts, []);
        await cluster.TickSecondsAsync(3);
        Assert.Equal(original.StartedAt, Assert.Single(b.Node.MergedAlerts).StartedAt);
        if (acknowledged)
        {
            await b.Node.SendAckAsync(original.Key, "operator", CancellationToken.None);
            a.Node.SetLocalState(originalState.ActiveAlerts, []);
            await cluster.TickSecondsAsync(1);
            Assert.Equal(AlertStatus.Acknowledged, Assert.Single(b.Node.MergedAlerts).Status);
        }
        // A is unreachable; the fresh state machine on B follows today's AppOrchestrator path:
        // it has ACK memory but no adoption of the remote Alert before its first observation.
        cluster.Hub.Partition(a.Host, b.Host);
        await cluster.TickSecondsAsync(36);
        Assert.True(b.Node.IsWatcher);
        var observed = takeoverState.OnRedConfirmed(machine, message, null);
        Assert.NotEqual(original.StartedAt, observed.StartedAt);
        Assert.Equal(acknowledged ? AlertStatus.Acknowledged : AlertStatus.Active, observed.Status);
        Assert.Equal(acknowledged ? b.Acks.Get(original.Key)!.At : cluster.Clock.UtcNow, observed.StartedAt);
    }
}
