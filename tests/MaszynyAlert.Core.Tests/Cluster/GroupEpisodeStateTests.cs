using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class GroupEpisodeStateTests
{
    private readonly FakeClock _clock = new();
    private readonly MachineConfig _machine = TestPaths.ExampleConfig.Machines.First(m => m.Enabled);
    private readonly MappedMessage _message = new("code-705", "Servo 6", "restart", "No.705", "NO 705", true);
    private AlertStateMachine Create(bool group = true)
    {
        var state = new AlertStateMachine(_clock, new TimingConfig(), new AckMemory(_clock, TimeSpan.FromHours(2)));
        state.UseGroupProtocol(group);
        return state;
    }

    [Fact]
    public void Replacement_RebindsLocalUuidWithoutRefreshingObservation_AndOldClosureCannotRemoveIt()
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null);
        _clock.Advance(TimeSpan.FromSeconds(20));
        var replacement = original with { EpisodeId = Guid.NewGuid(), SupersededEpisodeIds = [original.EpisodeId],
            ReconciledAt = _clock.UtcNow, StartedAt = _clock.UtcNow, LastSeenAt = _clock.UtcNow };
        state.ReconcileGroupState([replacement], [new(original.EpisodeId, original.MachineId, original.Kind, _clock.UtcNow)]);
        var actual = Assert.Single(state.ActiveAlerts);
        Assert.Equal(replacement.EpisodeId, actual.EpisodeId);
        Assert.Equal(original.LastSeenAt, actual.LastSeenAt);
        _clock.Advance(TimeSpan.FromSeconds(11)); state.Tick();
        Assert.Empty(state.ActiveAlerts);
    }

    [Fact]
    public void AmbiguousReconciliation_RejectsPreBoundaryAck_ThenAcceptsNewAck()
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null);
        state.AcknowledgeLocal(original.Key, "A", "operator");
        var oldAck = Assert.Single(state.ActiveAlerts);
        var reset = original with { SupersededEpisodeIds = [Guid.NewGuid()], ReconciledAt = _clock.UtcNow };
        state.ReconcileGroupState([reset], []);
        state.ReconcileGroupState([oldAck], []);
        state.ApplyRemoteAck(new(original.Key, "B", "old", _clock.UtcNow) { EpisodeId = original.EpisodeId });
        Assert.Equal(AlertStatus.Active, Assert.Single(state.ActiveAlerts).Status);
        Assert.True(state.AcknowledgeLocal(original.Key, "A", "new"));
        var current = Assert.Single(state.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, current.Status);
        Assert.True(current.AcknowledgedAt > reset.ReconciledAt);
        state.ReconcileGroupState([reset], []);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
    }

    [Fact]
    public void OtherGroup_DoesNotInheritRetirementMetadata()
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null) with
            { SupersededEpisodeIds = [Guid.NewGuid()], ReconciledAt = _clock.UtcNow };
        state.RestoreObservation([original], preserveAcknowledgements: false);
        var actual = Assert.Single(state.ActiveAlerts);
        Assert.NotEqual(original.EpisodeId, actual.EpisodeId);
        Assert.Empty(actual.SupersededEpisodeIds); Assert.Null(actual.ReconciledAt);
    }

    [Fact]
    public void RepeatedState_DoesNotResetObservationOrAcknowledgementAge()
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null);
        var ack = original with { Status = AlertStatus.Acknowledged, AcknowledgedAt = _clock.UtcNow,
            AckedByHost = "B", AckedByUser = "operator" };
        _clock.Advance(TimeSpan.FromSeconds(20));
        state.ReconcileGroupState([ack], []);
        state.ReconcileGroupState([ack], []);
        Assert.Equal(original.LastSeenAt, Assert.Single(state.ActiveAlerts).LastSeenAt);
        _clock.Advance(TimeSpan.FromSeconds(11)); state.Tick();
        Assert.Empty(state.ActiveAlerts);
    }

    [Fact]
    public void StateCarriesOldAcknowledgement_WithoutRestartingEscalationTimer()
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null);
        var ack = original with { Status = AlertStatus.Acknowledged, AcknowledgedAt = _clock.UtcNow,
            AckedByHost = "B", AckedByUser = "operator" };
        _clock.Advance(TimeSpan.FromMinutes(14));
        state.OnRedConfirmed(_machine, _message, null);
        state.ReconcileGroupState([ack], []);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        _clock.Advance(TimeSpan.FromMinutes(1));
        state.OnRedConfirmed(_machine, _message, null); state.Tick();
        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
        state.ReconcileGroupState([ack], []);
        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
    }

    [Theory]
    [InlineData(AlertKind.Error)]
    [InlineData(AlertKind.Offline)]
    [InlineData(AlertKind.MachineIdle)]
    [InlineData(AlertKind.WrongScreen)]
    public void ClosureIsIdempotent_AndCannotCloseNewUuidWithSameKey(AlertKind kind)
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null) with { Kind = kind };
        state.RestoreObservation([original]);
        var closure = new EpisodeClosure(original.EpisodeId, original.MachineId, kind, _clock.UtcNow);
        int closed = 0;
        state.Changed += e => { if (e.Kind == AlertEventKind.Closed) closed++; };
        state.ReconcileGroupState([], [closure]);
        state.ReconcileGroupState([], [closure]);
        Assert.Empty(state.ActiveAlerts); Assert.Equal(1, closed);
        var next = original with { EpisodeId = Guid.NewGuid() };
        state.RestoreObservation([next]);
        state.ReconcileGroupState([], [closure]);
        Assert.Equal(next.EpisodeId, Assert.Single(state.ActiveAlerts).EpisodeId);
    }

    [Fact]
    public void AbsentEpisodesAndForeignIdentityCannotChangeLocalObservation()
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null);
        state.ReconcileGroupState([], []);
        state.ReconcileGroupState([], [new(original.EpisodeId, "other-machine", original.Kind, _clock.UtcNow)]);
        state.ReconcileGroupState([original with { EpisodeId = Guid.NewGuid(), Status = AlertStatus.Acknowledged,
            AcknowledgedAt = _clock.UtcNow }], []);
        state.ApplyRemoteAck(new(original.Key, "B", "operator", _clock.UtcNow));
        Assert.Equal(original, Assert.Single(state.ActiveAlerts));
    }

    [Fact]
    public void EscalationWinsTimestampTie_UntilLaterEpisodeAcknowledgement()
    {
        var state = Create();
        var original = state.OnRedConfirmed(_machine, _message, null);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var ack = original with { Status = AlertStatus.Acknowledged, AcknowledgedAt = _clock.UtcNow };
        var escalated = ack with { Status = AlertStatus.Escalated, EscalatedAt = _clock.UtcNow };
        state.ReconcileGroupState([ack, escalated, ack], []);
        state.ApplyRemoteAck(new(original.Key, "B", "operator", _clock.UtcNow) { EpisodeId = original.EpisodeId });
        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
        _clock.Advance(TimeSpan.FromSeconds(1));
        state.ApplyRemoteAck(new(original.Key, "B", "operator", _clock.UtcNow) { EpisodeId = original.EpisodeId });
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
    }

    [Fact]
    public void GroupTransitionsAreIgnoredInSingleComputerMode()
    {
        var state = Create(group: false);
        var original = state.OnRedConfirmed(_machine, _message, null);
        state.ReconcileGroupState([], [new(original.EpisodeId, original.MachineId, original.Kind, _clock.UtcNow)]);
        Assert.Equal(original, Assert.Single(state.ActiveAlerts));
    }
}
