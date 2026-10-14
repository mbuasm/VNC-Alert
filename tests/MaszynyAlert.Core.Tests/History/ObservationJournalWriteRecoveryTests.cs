using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class ObservationJournalWriteRecoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
    private static ObservationInterval Interval(string machine = "aidite1") =>
        new(machine, ObservationState.Observed, T0, T0.AddMinutes(10));

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("close")]
    [InlineData("observe")]
    public void RemoteWriteFailureIsRetriedWithoutRequiringNewData(string trigger)
    {
        using var dir = new TempDir("observation-recovery");
        var path = dir.File("observation.jsonl");
        File.WriteAllText(path, "");
        var journal = new ObservationJournal(path);
        var missed = Interval();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(journal.Apply([missed]));
            Assert.False(journal.Apply([missed]));
            journal.CloseAll();
            Assert.NotNull(journal.Error);
            Assert.Equal(missed, Assert.Single(journal.Snapshot()));
        }
        switch (trigger)
        {
            case "duplicate": Assert.False(journal.Apply([missed])); break;
            case "empty": Assert.False(journal.Apply([])); break;
            case "close": journal.CloseAll(); break;
            case "observe": journal.Observe([], T0.AddMinutes(11)); break;
        }
        Assert.Equal(missed, Assert.Single(new ObservationJournal(path).Snapshot()));
        Assert.Null(journal.Error);
    }

    [Fact]
    public void RepeatedCloseAllRecoversClosedLocalIntervalsWithoutBridgingTheGap()
    {
        using var dir = new TempDir("observation-recovery");
        var path = dir.File("observation.jsonl");
        File.WriteAllText(path, "");
        var journal = new ObservationJournal(path);
        journal.Observe([("aidite1", ObservationState.Observed)], T0);
        journal.Observe([("aidite1", ObservationState.Observed)], T0.AddSeconds(5));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal.CloseAll();
            journal.CloseAll();
            Assert.NotNull(journal.Error);
        }
        journal.CloseAll();
        Assert.Equal(new ObservationInterval("aidite1", ObservationState.Observed, T0, T0.AddSeconds(5)),
            Assert.Single(new ObservationJournal(path).Snapshot()));
        Assert.Null(journal.Error);

        journal.Observe([("aidite1", ObservationState.Observed)], T0.AddSeconds(20));
        journal.Observe([("aidite1", ObservationState.Observed)], T0.AddSeconds(25));
        journal.CloseAll();
        var restored = new ObservationJournal(path).Snapshot();
        Assert.Equal(2, restored.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), TimeSpan.FromTicks(restored.Sum(i => (i.End - i.Start).Ticks)));
    }

    [Fact]
    public void FailedCompactionKeepsPendingRemoteAndLocalWritesForCloseAll()
    {
        using var dir = new TempDir("observation-recovery");
        var path = dir.File("observation.jsonl");
        File.WriteAllText(path, "");
        var journal = new ObservationJournal(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal.Apply([Interval()]);
            journal.Observe([("aidite2", ObservationState.NoConnection)], T0);
            journal.Observe([("aidite2", ObservationState.NoConnection)], T0.AddSeconds(5));
            journal.CloseAll();
        }
        Directory.CreateDirectory(path + ".tmp"); // readable journal, but the replacement file cannot be created
        journal.Compact();
        Assert.NotNull(journal.Error);
        Assert.Empty(new ObservationJournal(path).Snapshot());
        Directory.Delete(path + ".tmp");
        journal.CloseAll();
        Assert.Equal(journal.Snapshot(), new ObservationJournal(path).Snapshot());
        Assert.Equal(2, journal.Snapshot().Count);
    }

    [Fact]
    public void SuccessfulCompactionPersistsPendingWritesAndDoesNotReplayThemAgain()
    {
        using var dir = new TempDir("observation-recovery");
        var path = dir.File("observation.jsonl");
        File.WriteAllText(path, "");
        var journal = new ObservationJournal(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal.Apply([Interval()]);
            journal.Apply([Interval("aidite2")]);
        }
        journal.Compact();
        Assert.Null(journal.Error);
        Assert.Equal(journal.Snapshot(), new ObservationJournal(path).Snapshot());
        var compacted = File.ReadAllText(path);
        journal.CloseAll();
        Assert.False(journal.Apply([Interval()]));
        Assert.Equal(compacted, File.ReadAllText(path));
    }
}