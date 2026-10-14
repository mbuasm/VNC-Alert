using System.Text.Json;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class DowntimeJournalReadRecoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
    private static DowntimeEpisode Episode(string machine = "aidite1") =>
        new(machine, machine, "Servo", T0, T0.AddMinutes(10), T0.AddMinutes(10),
            EpisodeId: Guid.NewGuid(), Category: "inny");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleReplyAfterFailedStartupReadNeverShortensThePersistedClosure(bool deliverWhileLocked)
    {
        using var dir = new TempDir("downtime-recovery");
        var path = dir.File("downtime.jsonl");
        var complete = Episode();
        new DowntimeJournal(path).Merge(complete);
        var stale = complete with { LastObservedAt = T0.AddMinutes(5), EndedAt = T0.AddMinutes(5), Category = null };
        DowntimeJournal journal;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal = new DowntimeJournal(path);
            Assert.NotNull(journal.Error);
            if (deliverWhileLocked) journal.Apply([stale]);
        }
        if (!deliverWhileLocked) journal.Apply([stale]);
        journal.Flush();
        Assert.Equal(complete, Assert.Single(new DowntimeJournal(path).Snapshot()));
        Assert.Equal(complete, Assert.Single(journal.Snapshot()));
    }

    [Fact]
    public void RecoveryKeepsNewLocalObservationOpenAndRestoresTheRestOfTheDiskHistory()
    {
        using var dir = new TempDir("downtime-recovery");
        var path = dir.File("downtime.jsonl");
        var oldOpen = Episode() with { EndedAt = null };
        var other = Episode("aidite2");
        var seed = new DowntimeJournal(path);
        seed.Merge(oldOpen); seed.Merge(other);
        var live = oldOpen with { LastObservedAt = T0.AddMinutes(12), Reason = "Nowy odczyt", Origin = "PC-A" };
        DowntimeJournal journal;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal = new DowntimeJournal(path);
            Assert.True(journal.Merge(live));
        }
        journal.Flush();
        Assert.Contains(other, journal.Snapshot());
        Assert.Equal(live, Assert.Single(journal.Snapshot(), e => e.EpisodeId == live.EpisodeId));
        var restarted = new DowntimeJournal(path).Snapshot();
        Assert.Contains(other, restarted);
        Assert.Equal(live.LastObservedAt, Assert.Single(restarted, e => e.EpisodeId == live.EpisodeId).End);
    }

    [Fact]
    public void RecoveredReadWithWriteStillDeniedRetainsMergedDataForTheNextFlush()
    {
        // Rozdzielenie dostępu do odczytu/zapisu przez FileShare.Read sprawdzamy na Windows.
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("downtime-recovery");
        var path = dir.File("downtime.jsonl");
        var complete = Episode();
        var fresh = Episode("aidite2");
        new DowntimeJournal(path).Merge(complete);
        DowntimeJournal journal;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal = new DowntimeJournal(path);
            journal.Apply([complete with { LastObservedAt = T0.AddMinutes(5), EndedAt = T0.AddMinutes(5) }, fresh]);
        }
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            journal.Flush();
            Assert.NotNull(journal.Error);
            Assert.Contains(complete, journal.Snapshot());
            Assert.Contains(fresh, journal.Snapshot());
            Assert.Equal(complete, Assert.Single(new DowntimeJournal(path).Snapshot()));
        }
        journal.Flush();
        Assert.Equal(journal.Snapshot(), new DowntimeJournal(path).Snapshot());
    }

    [Fact]
    public void PartialStartupReadDefersAppendUntilTheUnreadTailCanBeMerged()
    {
        // Blokada zakresu zatrzymuje odczyt w połowie tylko na Windows; na innych systemach bez sprawdzenia.
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("downtime-recovery");
        var path = dir.File("downtime.jsonl");
        var history = Enumerable.Range(0, 500).Select(i => Episode("aidite" + i)).ToArray();
        File.WriteAllText(path, string.Join("\n", history.Select(e => JsonSerializer.Serialize(e))) + "\n");
        var before = new FileInfo(path).Length;
        DowntimeJournal journal;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            held.Lock(held.Length / 2, held.Length - held.Length / 2);
            journal = new DowntimeJournal(path);
            Assert.NotNull(journal.Error);
            journal.Apply([history[^1] with { LastObservedAt = T0.AddMinutes(5), EndedAt = T0.AddMinutes(5) }]);
            journal.Flush();
            Assert.Equal(before, new FileInfo(path).Length); // dopisywanie za końcem blokady byłoby możliwe
        }
        journal.Flush();
        Assert.Equal(500, journal.Snapshot().Count);
        Assert.Contains(history[^1], new DowntimeJournal(path).Snapshot());
    }

    [Fact]
    public void RecoveryPreservesLegacyNullMachineAndGoodRowsAroundCorruptJson()
    {
        using var dir = new TempDir("downtime-recovery");
        var path = dir.File("downtime.jsonl");
        var legacy = Episode() with { MachineId = null!, EpisodeId = Guid.Empty, Category = " " };
        var complete = Episode("aidite2");
        File.WriteAllText(path, JsonSerializer.Serialize(legacy) + "\n{broken\n" + JsonSerializer.Serialize(complete) + "\n");
        var fresh = Episode("aidite3");
        DowntimeJournal journal;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal = new DowntimeJournal(path);
            journal.Merge(fresh);
        }
        journal.Flush();
        Assert.Equal(3, journal.Snapshot().Count);
        var recoveredLegacy = Assert.Single(journal.Snapshot(), e => e.MachineId is null);
        Assert.Equal(DowntimeEpisode.LegacyId(null!, T0), recoveredLegacy.EpisodeId);
        Assert.Null(recoveredLegacy.Category);
        Assert.Contains(complete, journal.Snapshot());
        Assert.Contains(fresh, journal.Snapshot());
        Assert.NotNull(journal.Error); // uszkodzony wiersz pozostaje jawnie zgłoszony
        Assert.Equal(journal.Snapshot(), new DowntimeJournal(path).Snapshot());
    }
}