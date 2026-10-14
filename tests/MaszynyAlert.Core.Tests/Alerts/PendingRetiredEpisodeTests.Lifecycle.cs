using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed partial class PendingRetiredEpisodeTests
{
    [Theory]
    [InlineData(false, "same")]
    [InlineData(true, "same")]
    [InlineData(false, "different")]
    [InlineData(true, "different")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    public void Tick_CompletesFirstReadWithoutAnotherFrame_UsesOnlyActualObservations(bool group, string result)
    {
        using var h = new ClosedEpisode(group);
        var read = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task); // 78
        h.Clock.AdvanceMs(2000);
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task); // 80
        Assert.Empty(h.State.ActiveAlerts);
        Assert.Empty(h.Events);
        Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot()));
        h.Clock.AdvanceMs(10000); // OCR completes at 90, without a fresh red frame.
        read.SetResult(result switch { "same" => Home, "different" => Servo, _ => Contentless });
        h.State.Tick();

        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(80), current.LastSeenAt);
        if (result == "same")
        {
            Assert.Equal(h.Old.StartedAt, current.StartedAt);
            Assert.Equal(AlertStatus.Acknowledged, current.Status);
            Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Raised);
            if (group) Assert.Contains(h.Old.EpisodeId, current.SupersededEpisodeIds);
            else Assert.Equal(h.Old.EpisodeId, current.EpisodeId);
        }
        else
        {
            Assert.Equal(FakeClock.DefaultStart.AddSeconds(78), current.StartedAt);
            Assert.Equal(AlertStatus.Active, current.Status);
            Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
            Assert.Equal(12d, DowntimeReport.Build(h.Journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60, 6);
            Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot(), row => row.EpisodeId == h.Old.EpisodeId));
        }
        h.Clock.AdvanceMs(20000);
        h.State.Tick(); // 30 s since last FRAME, not since OCR.
        Assert.Empty(h.State.ActiveAlerts);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.EpisodeId == current.EpisodeId);
    }

    [Theory]
    [InlineData(false, false, -3600)]
    [InlineData(true, false, -3600)]
    [InlineData(false, false, 3600)]
    [InlineData(true, false, 3600)]
    [InlineData(false, true, -3600)]
    [InlineData(true, true, -3600)]
    [InlineData(false, true, 3600)]
    [InlineData(true, true, 3600)]
    public void RejoinBoundaryDuringFirstRead_UsesMonotonicTimeEvenAfterUtcShift(bool group, bool crossesBoundary, int utcShiftSeconds)
    {
        using var h = new ClosedEpisode(group, new TimingConfig { EscalationAfterAckMs = 0 });
        h.Clock.AdvanceMs(51999); // 129.999: 119.999 s since red disappeared at 10.
        h.Clock.UtcNow += TimeSpan.FromSeconds(utcShiftSeconds);
        var firstRed = h.Clock.UtcNow;
        var read = new TaskCompletionSource<MappedMessage>();
        Assert.True(h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
        Assert.Empty(h.Events);
        if (crossesBoundary) h.Clock.JumpMonotonic(1);
        read.SetResult(Home);
        h.State.Tick();

        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(firstRed, current.LastSeenAt);
        if (crossesBoundary)
        {
            Assert.Equal(firstRed, current.StartedAt);
            Assert.Equal(AlertStatus.Active, current.Status);
            Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        }
        else
        {
            Assert.Equal(h.Old.StartedAt, current.StartedAt);
            Assert.Equal(AlertStatus.Acknowledged, current.Status);
            Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Raised);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void FirstRedAtOrAfterRejoinBoundary_DoesNotWaitOrInheritAck(bool group, int millisecondsAfter)
    {
        using var h = new ClosedEpisode(group);
        h.Clock.AdvanceMs(52000 + millisecondsAfter);
        var read = new TaskCompletionSource<MappedMessage>();
        Assert.False(h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(h.Clock.UtcNow, current.StartedAt);
        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        read.SetResult(Home);
        h.State.OnRedConfirmed(Machine, Home, null);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledRejoin_DoesNotDeferFirstRead(bool group)
    {
        using var h = new ClosedEpisode(group, new TimingConfig { SameMessageRejoinMs = 0 });
        var read = new TaskCompletionSource<MappedMessage>();
        Assert.False(h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
        Assert.Equal(AlertStatus.Active, Assert.Single(h.State.ActiveAlerts).Status);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExistingExactKeyRejoin_AfterSeveralClosedEpisodes_IsSameForDirectAndDelayedRead(bool group, bool delayed)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        var events = new List<AlertEvent>();
        state.Changed += events.Add;
        Alert home = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(home.Key, "PC-test", "operator");
        clock.AdvanceMs(10000);
        state.OnRedConfirmed(Machine, Home, null);
        state.OnRedCleared(Machine.Id);
        clock.AdvanceMs(31000); state.Tick(); // Home closed at 41.
        clock.AdvanceMs(1000);
        state.OnRedConfirmed(Machine, Servo, null); // Servo 42..52, closed at 83.
        clock.AdvanceMs(10000);
        state.OnRedConfirmed(Machine, Servo, null);
        state.OnRedCleared(Machine.Id);
        clock.AdvanceMs(31000); state.Tick();
        clock.AdvanceMs(1000);
        events.Clear();
        if (delayed)
        {
            var read = new TaskCompletionSource<MappedMessage>();
            state.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
            Assert.Empty(events);
            Assert.Empty(state.ActiveAlerts);
            clock.AdvanceMs(1000);
            read.SetResult(Home);
            state.Tick();
        }
        else state.OnRedConfirmed(Machine, Home, null);

        Alert current = Assert.Single(state.ActiveAlerts);
        Assert.Equal(home.StartedAt, current.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, current.Status);
        Assert.DoesNotContain(events, e => e.Kind == AlertEventKind.Raised);
        if (group) Assert.Contains(home.EpisodeId, current.SupersededEpisodeIds);
        else Assert.Equal(home.EpisodeId, current.EpisodeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderContentlessEntry_DoesNotHideKnownRetiredHome(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        var events = new List<AlertEvent>();
        state.Changed += events.Add;
        state.OnRedConfirmed(Machine, Contentless, null);
        state.OnRedCleared(Machine.Id);
        clock.AdvanceMs(31000); state.Tick();
        Alert home = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(home.Key, "PC-test", "operator");
        state.OnRedCleared(Machine.Id);
        clock.AdvanceMs(31000); state.Tick();
        events.Clear();
        var read = new TaskCompletionSource<MappedMessage>();
        state.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        Assert.Empty(events);
        Assert.Empty(state.ActiveAlerts);
        read.SetResult(Home); state.Tick();
        Alert current = Assert.Single(state.ActiveAlerts);
        Assert.Equal(home.StartedAt, current.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, current.Status);
        Assert.DoesNotContain(events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false, 9999)]
    [InlineData(true, 9999)]
    [InlineData(false, 10000)]
    [InlineData(true, 10000)]
    public void OfflineBeforeReturn_KeepsExistingShortAndLongGapBoundary(bool group, int offlineMs)
    {
        using var h = new ClosedEpisode(group);
        h.State.OnMachineOffline(Machine, h.Clock.UtcNow);
        h.Clock.AdvanceMs(offlineMs);
        h.State.OnMachineOnline(Machine.Id);
        var firstRed = h.Clock.UtcNow;
        var read = new TaskCompletionSource<MappedMessage>();
        bool deferred = h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        Assert.Equal(offlineMs < 10000, deferred);
        read.SetResult(Home);
        if (deferred) h.State.Tick();
        else h.State.OnRedConfirmed(Machine, Home, null);
        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(offlineMs < 10000 ? h.Old.StartedAt : firstRed, current.StartedAt);
        Assert.Equal(offlineMs < 10000 ? AlertStatus.Acknowledged : AlertStatus.Active, current.Status);
        Assert.Equal(offlineMs < 10000 ? 0 : 1, h.Events.Count(e => e.Kind == AlertEventKind.Raised));
    }

    [Theory]
    [InlineData(false, "clear")]
    [InlineData(true, "clear")]
    [InlineData(false, "offline")]
    [InlineData(true, "offline")]
    [InlineData(false, "disable")]
    [InlineData(true, "disable")]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [InlineData(false, "restore")]
    [InlineData(true, "restore")]
    public void ObservationEndsBeforeOcr_PreservesObservedIntervalAndDiscardsLateResult(bool group, string ending)
    {
        using var h = new ClosedEpisode(group);
        var read = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        h.Clock.AdvanceMs(2000);
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        h.Clock.AdvanceMs(5000);
        switch (ending)
        {
            case "clear": h.State.OnRedCleared(Machine.Id); break;
            case "offline": h.State.OnMachineOffline(Machine, h.Clock.UtcNow); break;
            case "disable": h.State.OnMachineDisabled(Machine.Id); break;
            case "stop": h.State.FlushPendingFirstRead(Machine.Id); break;
            case "restore": h.State.RestoreObservation([]); break;
        }
        var fresh = Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId != h.Old.EpisodeId);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(78), fresh.StartedAt);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(80), fresh.End);
        Assert.Equal(12d, DowntimeReport.Build(h.Journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60, 6);
        int publications = h.Events.Count;
        read.SetResult(Home);
        h.State.Tick();
        Assert.Equal(publications, h.Events.Count);
        Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId == h.Old.EpisodeId));
        Assert.DoesNotContain(h.State.ActiveAlerts, a => a.Key == h.Old.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingFramesAndUnfinishedRead_ExistingCloseTimerPublishesObservedInterval(bool group)
    {
        using var h = new ClosedEpisode(group);
        var read = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        h.Clock.AdvanceMs(2000);
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        h.Clock.AdvanceMs(30000);
        h.State.Tick();
        Assert.Empty(h.State.ActiveAlerts);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Closed);
        Assert.Equal(12d, DowntimeReport.Build(h.Journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60, 6);
    }

    [Theory]
    [InlineData(false, ProgressState.Full)]
    [InlineData(true, ProgressState.Full)]
    [InlineData(false, ProgressState.Zero)]
    [InlineData(true, ProgressState.Zero)]
    public void PendingRedClosesIdleAndPreventsNewIdleCounters(bool group, ProgressState progress)
    {
        using var h = new ClosedEpisode(group, new TimingConfig { IdleAlertAfterMs = 1000, IdleAtZeroAfterMs = 1000 });
        h.State.OnProgressState(Machine, progress);
        h.Clock.AdvanceMs(1000);
        h.State.OnProgressState(Machine, progress);
        Alert idle = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, idle.Kind);
        h.Events.Clear();
        var read = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.EpisodeId == idle.EpisodeId);
        for (int i = 0; i < 3; i++)
        {
            h.Clock.AdvanceMs(1000);
            h.State.OnProgressState(Machine, progress);
            h.State.Tick();
        }
        Assert.Empty(h.State.ActiveAlerts);
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingIsMachineLocal_OtherMachineStillRaisesImmediately(bool group)
    {
        using var h = new ClosedEpisode(group);
        var first = new TaskCompletionSource<MappedMessage>();
        var second = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, first.Task);
        MachineConfig other = Machine with { Id = "aidite2", Name = "Aidite 2" };
        Assert.False(h.State.OnRedConfirmedWithFirstRead(other, Contentless, null, second.Task));
        Assert.Equal(other.Id, Assert.Single(h.State.ActiveAlerts).MachineId);
        first.SetResult(Home); h.State.Tick();
        Assert.Equal(2, h.State.ActiveAlerts.Count);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.MachineId == other.Id);
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.MachineId == Machine.Id);
    }
}