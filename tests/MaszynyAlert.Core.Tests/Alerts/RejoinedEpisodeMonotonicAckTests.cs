using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using Xunit.Abstractions;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class RejoinedEpisodeMonotonicAckTests(ITestOutputHelper output)
{
    private static readonly MachineConfig Machine = new()
    { Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Port = 9100, Profile = "opencnc" };
    private static readonly MappedMessage Home = new("code-1200", "Bazowanie (Home)", "restart",
        "Request home alarm", "REQUEST HOME ALARM", true);

    [Theory]
    [InlineData(false, -24)]
    [InlineData(false, 0)]
    [InlineData(false, 24)]
    [InlineData(true, -24)]
    [InlineData(true, 0)]
    [InlineData(true, 24)]
    public void Rejoin_PreservesTheOriginalMonotonicEscalationDeadline(bool group, int wallJumpHours)
    {
        var clock = new FakeClock();
        var state = new AlertStateMachine(clock, new TimingConfig { EscalationAfterAckMs = 60000 },
            new AckMemory(clock, TimeSpan.FromHours(2)));
        state.UseGroupProtocol(group);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        clock.Advance(TimeSpan.FromSeconds(5));
        state.OnRedConfirmed(Machine, Home, null);
        state.OnRedCleared(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.Tick();
        Assert.Empty(state.ActiveAlerts);
        clock.Advance(TimeSpan.FromSeconds(4)); // t=40; ACK was at monotonic t=0.
        clock.UtcNow += TimeSpan.FromHours(wallJumpHours);

        Alert rejoined = state.OnRedConfirmed(Machine, Home, null);
        Assert.Equal(AlertStatus.Acknowledged, rejoined.Status);
        state.Tick();
        output.WriteLine($"group={group}; UTC jump={wallJumpHours}h; monotonic age=40s; status={Assert.Single(state.ActiveAlerts).Status}");
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.Advance(TimeSpan.FromSeconds(19));
        state.OnRedConfirmed(Machine, Home, null);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.Advance(TimeSpan.FromSeconds(1)); // t=60: exactly the original deadline, with fresh red.
        state.OnRedConfirmed(Machine, Home, null);
        state.Tick();

        output.WriteLine($"group={group}; UTC jump={wallJumpHours}h; monotonic age=60s; status={Assert.Single(state.ActiveAlerts).Status}");
        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedDescriptionAndASecondRejoin_KeepTheSameMonotonicAck(bool group)
    {
        var clock = new FakeClock();
        var state = new AlertStateMachine(clock, new TimingConfig { EscalationAfterAckMs = 120000 },
            new AckMemory(clock, TimeSpan.FromHours(2)));
        state.UseGroupProtocol(group);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        var better = new MappedMessage("code-705", "Servo 6", "restart", "Axis 6 servo alarm", "AXIS 6 SERVO ALARM", true)
        { Priority = 90 };
        clock.Advance(TimeSpan.FromSeconds(5));
        Alert renamed = state.OnRedConfirmed(Machine, better, null);
        Assert.Equal(first.EpisodeId, renamed.EpisodeId);
        Assert.NotEqual(first.Key, renamed.Key);
        clock.Advance(TimeSpan.FromSeconds(5));
        state.OnRedConfirmed(Machine, better, null);
        state.OnRedCleared(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.Tick();
        Assert.Empty(state.ActiveAlerts);
        clock.Advance(TimeSpan.FromSeconds(9)); // t=50, first rejoin before any wall-clock jump.
        state.OnRedConfirmed(Machine, better, null);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.Advance(TimeSpan.FromSeconds(5));
        state.OnRedConfirmed(Machine, better, null);
        state.OnRedCleared(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.Tick();
        Assert.Empty(state.ActiveAlerts);
        clock.Advance(TimeSpan.FromSeconds(4)); // t=90, second rejoin.
        clock.UtcNow -= TimeSpan.FromHours(24);
        state.OnRedConfirmed(Machine, better, null);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.Advance(TimeSpan.FromSeconds(29));
        state.OnRedConfirmed(Machine, better, null);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.Advance(TimeSpan.FromSeconds(1)); // t=120 from the original ACK, not either rejoin.
        state.OnRedConfirmed(Machine, better, null);
        state.Tick();

        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedAckBeforeClosing_KeepsTheDeadlineAlreadyUsedByTheObserver(bool restore)
    {
        var clock = new FakeClock();
        var state = new AlertStateMachine(clock, new TimingConfig { EscalationAfterAckMs = 60000 },
            new AckMemory(clock, TimeSpan.FromHours(2)));
        state.UseGroupProtocol(true);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator"); // AckMemory initially has t=0.
        clock.Advance(TimeSpan.FromSeconds(20));
        DateTimeOffset acceptedAck = FakeClock.DefaultStart.AddSeconds(10);
        Alert imported = first with { Status = AlertStatus.Acknowledged, AcknowledgedAt = acceptedAck,
            AckedByHost = "remote-PC", AckedByUser = "operator" };
        if (restore)
        {
            state.RestoreObservation([imported]);
            state.SetScreenObservationAvailable(Machine.Id, true);
        }
        else state.ReconcileGroupState([imported], []);
        Assert.Equal(acceptedAck, Assert.Single(state.ActiveAlerts).AcknowledgedAt);
        clock.Advance(TimeSpan.FromSeconds(10));
        state.OnRedConfirmed(Machine, Home, null);
        state.OnRedCleared(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.Tick();
        Assert.Empty(state.ActiveAlerts);
        clock.Advance(TimeSpan.FromSeconds(4)); // t=65, 55 seconds after the accepted t=10 ACK.
        clock.UtcNow -= TimeSpan.FromHours(24);
        state.OnRedConfirmed(Machine, Home, null);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.Advance(TimeSpan.FromSeconds(4)); // t=69.
        state.OnRedConfirmed(Machine, Home, null);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.Advance(TimeSpan.FromSeconds(1)); // t=70: the actual previously established deadline.
        state.OnRedConfirmed(Machine, Home, null);
        state.Tick();

        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
    }}
