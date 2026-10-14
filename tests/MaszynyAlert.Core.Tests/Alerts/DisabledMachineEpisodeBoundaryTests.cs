using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using Xunit.Abstractions;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class DisabledMachineEpisodeBoundaryTests(ITestOutputHelper output)
{
    private static readonly MachineConfig Machine = new()
    { Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Port = 9100, Profile = "opencnc" };
    private static readonly MappedMessage Home = new("code-1200", "Bazowanie (Home)", "restart",
        "Request home alarm", "REQUEST HOME ALARM", true);

    private static AlertStateMachine State(FakeClock clock, bool group, AckMemory? memory = null)
    {
        var state = new AlertStateMachine(clock, new TimingConfig(), memory ?? new AckMemory(clock, TimeSpan.FromHours(2)));
        state.UseGroupProtocol(group);
        return state;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReEnabledMachine_RecordsTwoObservedIntervalsWithoutTheDisabledGap(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        using var dir = new TempDir("disabled-gap");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        state.Changed += change => journal.Record(change.Alert, "PC-test");
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        clock.Advance(TimeSpan.FromSeconds(15));
        journal.Record(state.OnRedConfirmed(Machine, Home, null));
        state.OnMachineDisabled(Machine.Id);
        Assert.Equal(15d, Seconds(journal), 6);

        clock.Advance(TimeSpan.FromSeconds(20));
        DateTimeOffset resumedAt = clock.UtcNow;
        Alert returned = state.OnRedConfirmed(Machine, Home, null);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Record(state.OnRedConfirmed(Machine, Home, null));
        double openSeconds = Seconds(journal);
        state.OnMachineDisabled(Machine.Id);
        double closedSeconds = Seconds(journal);

        output.WriteLine($"group={group}; observed=20s; disabled=20s; open={openSeconds}s; closed={closedSeconds}s");
        Assert.Null(journal.Error);
        Assert.Equal(20d, closedSeconds, 6);
        Assert.Equal(20d, openSeconds, 6);
        Assert.Equal(2, journal.Snapshot().Count);
        Assert.Equal(resumedAt, returned.StartedAt);
        Assert.NotEqual(first.EpisodeId, returned.EpisodeId);
        Assert.Equal(AlertStatus.Active, returned.Status);
        Assert.Null(returned.AcknowledgedAt);
        Assert.Empty(returned.SupersededEpisodeIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisableAfterTheErrorAlreadyClosed_ForgetsItsRejoinCandidate(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        state.OnRedCleared(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.Tick();
        Assert.Empty(state.ActiveAlerts);
        Alert closed = Assert.Single(state.RecentHistory);
        state.OnMachineDisabled(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        var events = new List<AlertEvent>();
        state.Changed += events.Add;

        Alert current = state.OnRedConfirmed(Machine, Home, null);

        Assert.Equal(clock.UtcNow, current.StartedAt);
        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.NotEqual(first.EpisodeId, current.EpisodeId);
        Assert.Empty(current.SupersededEpisodeIds);
        Assert.Null(current.AcknowledgedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(events).Kind);
        Assert.Equal(closed, Assert.Single(state.RecentHistory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisablingOneMachine_PreservesOtherMachinesLiveAndRejoinableEpisodes(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        MachineConfig other = Machine with { Id = "aidite2", Name = "Aidite 2" };
        MachineConfig live = Machine with { Id = "aidite3", Name = "Aidite 3" };
        state.OnRedConfirmed(Machine, Home, null);
        Alert retired = state.OnRedConfirmed(other, Home, null);
        state.AcknowledgeLocal(retired.Key, "PC-test", "operator");
        Alert liveFirst = state.OnRedConfirmed(live, Home, null);
        state.AcknowledgeLocal(liveFirst.Key, "PC-test", "operator");
        state.OnRedCleared(other.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.OnRedConfirmed(Machine, Home, null);
        Alert liveBefore = state.OnRedConfirmed(live, Home, null);
        state.Tick();
        Assert.DoesNotContain(state.ActiveAlerts, alert => alert.MachineId == other.Id);

        state.OnMachineDisabled(Machine.Id);
        Alert resumedOther = state.OnRedConfirmed(other, Home, null);

        Assert.Equal(AlertStatus.Acknowledged, resumedOther.Status);
        Assert.Equal(retired.StartedAt, resumedOther.StartedAt);
        if (group) Assert.Contains(retired.EpisodeId, resumedOther.SupersededEpisodeIds);
        else Assert.Equal(retired.EpisodeId, resumedOther.EpisodeId);
        Assert.Equal(liveBefore, Assert.Single(state.ActiveAlerts, alert => alert.MachineId == live.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedAckOfDisabledEpisode_CannotSilenceTheNewEpisode(bool group)
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        var state = State(clock, group, memory);
        Alert old = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(old.Key, "PC-test", "operator");
        var stale = new AckRecord(old.Key, "late-PC", "operator", clock.UtcNow)
        { EpisodeId = group ? old.EpisodeId : null };
        clock.Advance(TimeSpan.FromSeconds(15));
        state.OnMachineDisabled(Machine.Id);
        state.ApplyRemoteAck(stale);
        Assert.Null(memory.Get(old.Key)); // Retired ACK stays hidden from a NEW episode.
        clock.Advance(TimeSpan.FromSeconds(20));
        Alert current = state.OnRedConfirmed(Machine, Home, null);
        state.ApplyRemoteAck(stale);

        Assert.Equal(AlertStatus.Active, Assert.Single(state.ActiveAlerts).Status);
        Assert.NotEqual(old.EpisodeId, current.EpisodeId);
        Assert.Null(current.AcknowledgedAt);
        Assert.Contains(state.RecentHistory, alert => alert.EpisodeId == old.EpisodeId && alert.Status == AlertStatus.Closed);
    }

    private static double Seconds(DowntimeJournal journal) =>
        DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60;
}
