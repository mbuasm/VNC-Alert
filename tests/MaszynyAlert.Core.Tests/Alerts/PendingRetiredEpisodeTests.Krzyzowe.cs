using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed partial class PendingRetiredEpisodeTests
{
    private static double ReportSeconds(DowntimeJournal journal) =>
        DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RecA1_DisableDuringPending_ThenReEnable_NoDisabledGapAndNoLateEvents(bool group, bool readDone)
    {
        using var h = new ClosedEpisode(group);
        var read = new TaskCompletionSource<MappedMessage>();
        Assert.True(h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
        Assert.Empty(h.State.ActiveAlerts);
        h.Clock.AdvanceMs(2000);
        Assert.True(h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
        if (readDone) read.SetResult(Home);
        h.State.OnMachineDisabled(Machine.Id);
        Assert.Empty(h.State.ActiveAlerts);
        var afterDisable = h.Events.Count;
        if (!readDone) read.SetResult(Home); // late OCR after disable
        h.State.Tick();
        Assert.Equal(afterDisable, h.Events.Count);

        h.Clock.AdvanceMs(20000); // disabled for 20 s, still inside the old rejoin window
        var read2 = new TaskCompletionSource<MappedMessage>();
        Assert.False(h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read2.Task));
        Alert fresh = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(AlertStatus.Active, fresh.Status);
        Assert.NotEqual(h.Old.EpisodeId, fresh.EpisodeId);
        Assert.Empty(fresh.SupersededEpisodeIds);
        h.Clock.AdvanceMs(5000);
        h.State.OnRedConfirmed(Machine, Home, null);
        h.Journal.Record(Assert.Single(h.State.ActiveAlerts));
        h.State.OnMachineDisabled(Machine.Id);

        // Old 0..10, pending 78..80 (observed before disable, rejoin => old start 0..80 when read done), new 100..105.
        double expected = readDone ? 80 + 5 : 10 + 2 + 5;
        Assert.Equal(expected, ReportSeconds(h.Journal), 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecA1_PendingRejoinThenBetterDescription_KeepsGroupIdentityAndRejectsOldAck(bool group)
    {
        using var h = new ClosedEpisode(group);
        var read = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        read.SetResult(Home);
        h.State.Tick();
        Alert rejoined = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, rejoined.Status);
        Assert.Equal(h.Old.StartedAt, rejoined.StartedAt);
        if (group)
        {
            Assert.NotEqual(h.Old.EpisodeId, rejoined.EpisodeId);
            Assert.Contains(h.Old.EpisodeId, rejoined.SupersededEpisodeIds);
        }
        else Assert.Equal(h.Old.EpisodeId, rejoined.EpisodeId);

        h.Clock.AdvanceMs(1000);
        Alert better = h.State.OnRedConfirmed(Machine, Servo, null);
        Assert.Equal(rejoined.EpisodeId, better.EpisodeId);
        Assert.Equal(rejoined.SupersededEpisodeIds, better.SupersededEpisodeIds);
        Assert.Equal(rejoined.ReconciledAt, better.ReconciledAt);
        Assert.Equal(AlertStatus.Acknowledged, better.Status);
        Assert.Equal(h.Old.StartedAt, better.StartedAt);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Updated && e.Alert.Key == better.Key);
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false, 24)]
    [InlineData(true, 24)]
    [InlineData(false, -24)]
    [InlineData(true, -24)]
    public void RecA1_PendingRejoinAfterUtcJump_EscalatesAtOriginalMonotonicDeadline(bool group, int hours)
    {
        // ClosedEpisode: ACK at monotonic 0, pending at 78 s. Escalation threshold 100 s.
        using var h = new ClosedEpisode(group, new TimingConfig { EscalationAfterAckMs = 100000 });
        h.Clock.UtcNow += TimeSpan.FromHours(hours);
        var read = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        h.Clock.AdvanceMs(1000);
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        read.SetResult(Home);
        h.State.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(h.State.ActiveAlerts).Status);
        for (int t = 80; t < 100; t++)
        {
            h.Clock.AdvanceMs(1000);
            h.State.OnRedConfirmed(Machine, Home, null);
            h.State.Tick();
            Assert.Equal(AlertStatus.Acknowledged, Assert.Single(h.State.ActiveAlerts).Status);
        }
        h.Clock.AdvanceMs(1000); // t=100
        h.State.OnRedConfirmed(Machine, Home, null);
        h.State.Tick();
        Assert.Equal(AlertStatus.Escalated, Assert.Single(h.State.ActiveAlerts).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecA1_DisableAfterClose_ReEnableWithinWindow_DoesNotPendOrEscalateFromOldAck(bool group)
    {
        using var h = new ClosedEpisode(group, new TimingConfig { EscalationAfterAckMs = 60000 });
        h.State.OnMachineDisabled(Machine.Id);
        h.Clock.AdvanceMs(1000);
        var read = new TaskCompletionSource<MappedMessage>();
        Assert.False(h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
        read.SetResult(Home);
        h.State.OnRedConfirmed(Machine, Home, null);
        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Null(current.AcknowledgedAt);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(79), current.StartedAt);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
    }
}
