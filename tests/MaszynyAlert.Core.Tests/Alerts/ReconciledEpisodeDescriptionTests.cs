using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class ReconciledEpisodeDescriptionTests
{
    private static readonly MachineConfig Machine = new()
    { Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Port = 9100, Profile = "opencnc" };
    private static readonly MappedMessage Home = new("code-1200", "Bazowanie (Home)", "restart",
        "Request home alarm", "REQUEST HOME ALARM", true) { Priority = 50 };
    private static readonly MappedMessage Servo = new("code-705", "Servo 6", "restart",
        "Axis 6 servo alarm", "AXIS 6 SERVO ALARM", true) { Priority = 90 };

    [Fact]
    public void BetterDescription_KeepsTheExistingReconciliationMetadata()
    {
        var (state, clock, reconciled) = ReconciledEpisode();
        var events = new List<AlertEvent>();
        state.Changed += events.Add;
        clock.Advance(TimeSpan.FromSeconds(10));

        Alert described = state.OnRedConfirmed(Machine, Servo, null);

        Assert.Equal(reconciled.EpisodeId, described.EpisodeId);
        Assert.Equal(reconciled.StartedAt, described.StartedAt);
        Assert.NotEqual(reconciled.Key, described.Key);
        Assert.Equal(AlertEventKind.Updated, Assert.Single(events).Kind);
        Assert.Equal(reconciled.ReconciledAt, described.ReconciledAt);
        Assert.Equal(reconciled.SupersededEpisodeIds, described.SupersededEpisodeIds);
    }

    [Theory]
    [InlineData(-5, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void BetterDescription_PreservesWhichRemoteAcksAreNewerThanReconciliation(int offsetSeconds, bool accepted)
    {
        var (state, clock, reconciled) = ReconciledEpisode();
        clock.Advance(TimeSpan.FromSeconds(10));
        Alert described = state.OnRedConfirmed(Machine, Servo, null);
        DateTimeOffset acknowledgedAt = reconciled.ReconciledAt!.Value.AddSeconds(offsetSeconds);

        state.ApplyRemoteAck(new AckRecord(described.Key, "late-PC", "operator", acknowledgedAt)
        { EpisodeId = described.EpisodeId });

        Alert current = Assert.Single(state.ActiveAlerts);
        Assert.Equal(accepted ? AlertStatus.Acknowledged : AlertStatus.Active, current.Status);
        Assert.Equal(accepted ? acknowledgedAt : (DateTimeOffset?)null, current.AcknowledgedAt);
    }

    private static (AlertStateMachine State, FakeClock Clock, Alert Reconciled) ReconciledEpisode()
    {
        var clock = new FakeClock();
        var state = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        state.UseGroupProtocol(true);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        clock.Advance(TimeSpan.FromSeconds(20));
        Guid[] predecessors = [Guid.NewGuid(), Guid.NewGuid()];
        state.ReconcileGroupState([first with { ReconciledAt = clock.UtcNow, SupersededEpisodeIds = predecessors }], []);
        Alert reconciled = Assert.Single(state.ActiveAlerts);
        Assert.Equal(clock.UtcNow, reconciled.ReconciledAt);
        Assert.Equal(predecessors, reconciled.SupersededEpisodeIds);
        return (state, clock, reconciled);
    }
}
