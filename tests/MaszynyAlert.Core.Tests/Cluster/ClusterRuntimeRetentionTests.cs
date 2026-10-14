using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>Stan epizodów grupy (zamknięcia, ACK) przetrwa wyłączenie i ponowne włączenie tej samej grupy;
/// zmiana grupy zaczyna od czystego stanu.</summary>
public sealed class ClusterRuntimeRetentionTests
{
    [Fact]
    public async Task DisableThenEnableSameGroup_KeepsClosuresAndAcks_OtherGroupStartsClean()
    {
        var clock = new FakeClock(); var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        await using var runtime = new ClusterRuntime(config, file.NetworkKey, clock,
            new AckMemory(clock, TimeSpan.FromHours(2)), "A", "test", (c, _) => hub.CreateBus("A", c));
        runtime.SetReadiness(new(true, false, []));
        await runtime.StartAsync(default);

        var alert = new Alert
        {
            Key = new AlertKey("aidite1", "rule:code-705"), MachineId = "aidite1", MachineName = "Aidite 1",
            Kind = AlertKind.Error, ShortText = "Servo 6", Category = "restart", Status = AlertStatus.Active,
            StartedAt = clock.UtcNow, LastSeenAt = clock.UtcNow, Sound = true,
        };
        runtime.SetLocalState([alert], []);
        runtime.RecordClosure(alert with { Status = AlertStatus.Closed, ClosedAt = clock.UtcNow });
        var before = runtime.GroupNode!.CaptureState();
        Assert.Equal(alert.EpisodeId, Assert.Single(before.Closed).EpisodeId);

        await runtime.SwitchAsync(config with { Enabled = false }, file.NetworkKey, default);
        Assert.Null(runtime.GroupNode);
        Assert.False(runtime.NetworkActive);

        await runtime.SwitchAsync(config, file.NetworkKey, default);
        var after = runtime.GroupNode!.CaptureState();
        Assert.Equal(before.Closed.Select(c => c.EpisodeId), after.Closed.Select(c => c.EpisodeId));
        Assert.True(runtime.IsWatcher);

        var other = GroupFileTests.Invitation();
        await runtime.SwitchAsync(config with { Group = other.Identity }, other.NetworkKey, default);
        Assert.Empty(runtime.GroupNode!.CaptureState().Closed);
    }
}
