using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class DowntimeJournalVersionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.FromHours(2));

    private static DowntimeEpisode Open(Guid id, DateTimeOffset observed, string reason = "Servo 6", string machineId = "aidite1") =>
        new(machineId, "Aidite 1", reason, T0, observed, null, EpisodeId: id, Category: "restart");

    private static void AssertSameSnapshot(IReadOnlyList<DowntimeEpisode> a, IReadOnlyList<DowntimeEpisode> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void Version_ChangesWithEveryChangeOfTheSnapshot_AndOnlyThen()
    {
        using var dir = new TempDir("downtime-version");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        Assert.Equal(0, journal.Version);
        var id = Guid.NewGuid();

        Assert.True(journal.Merge(Open(id, T0.AddMinutes(1))));
        long afterNew = journal.Version;
        Assert.True(afterNew > 0);

        // Ta sama obserwacja jeszcze raz (np. z dwóch źródeł) — bez zmiany stanu, bez zmiany numeru.
        Assert.False(journal.Merge(Open(id, T0.AddMinutes(1))));
        Assert.Equal(afterNew, journal.Version);
        // Ta sama chwila w innym przesunięciu strefy to ten sam stan (równość rekordu) — numer bez zmian.
        Assert.False(journal.Merge(Open(id, T0.AddMinutes(1)).ToOffsetEpisode(TimeSpan.Zero)));
        Assert.Equal(afterNew, journal.Version);

        // Otwarty epizod przesuwa ostatnią obserwację co sekundę — w pamięci to zmiana (Panel ma ją pokazać), plik czeka 10 s.
        Assert.True(journal.Merge(Open(id, T0.AddMinutes(1).AddSeconds(1))));
        long afterObservation = journal.Version;
        Assert.True(afterObservation > afterNew);

        Assert.True(journal.MergeClosure(id, T0.AddMinutes(2)));
        long afterClosure = journal.Version;
        Assert.True(afterClosure > afterObservation);

        Assert.True(journal.Apply([Open(Guid.NewGuid(), T0.AddMinutes(3), "Tacka nie pobrana"), Open(Guid.NewGuid(), T0.AddMinutes(4), "Drzwi")]));
        long afterApply = journal.Version;
        Assert.True(afterApply > afterClosure);
        // Powtórzona porcja z sieci niczego nie zmienia.
        Assert.False(journal.Apply(journal.Snapshot()));
        Assert.Equal(afterApply, journal.Version);

        // Odczyty i dopisanie do pliku nie zmieniają stanu w pamięci.
        var snapshot = journal.Snapshot();
        _ = journal.LatestObservedAt;
        _ = journal.EpisodesSince(DateTimeOffset.MinValue, 10);
        journal.Flush();
        Assert.Equal(afterApply, journal.Version);
        AssertSameSnapshot(snapshot, journal.Snapshot());
    }

    [Fact]
    public void Version_AfterReadingTheFile_IsNotZero_AndSameVersionMeansSameSnapshot()
    {
        using var dir = new TempDir("downtime-version-read");
        string path = dir.File("downtime.jsonl");
        var first = new DowntimeJournal(path);
        for (int i = 0; i < 20; i++)
            first.Merge(new DowntimeEpisode("aidite" + (i % 3), "Aidite", "Servo " + i, T0.AddMinutes(-i * 7), T0.AddMinutes(-i * 7 + 5),
                T0.AddMinutes(-i * 7 + 5), EpisodeId: Guid.NewGuid(), Category: "inny"));
        first.Flush();

        var journal = new DowntimeJournal(path);
        long version = journal.Version;
        Assert.True(version > 0, "odczyt pliku zmienia stan w pamięci (pusty → wczytany), więc numer nie może zostać zerem");
        var snapshot = journal.Snapshot();
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(version, journal.Version);
            AssertSameSnapshot(snapshot, journal.Snapshot());
        }
    }
}

internal static class DowntimeEpisodeOffsetExtensions
{
    /// <summary>Ten sam epizod z czasami zapisanymi w innym przesunięciu strefy (te same chwile).</summary>
    public static DowntimeEpisode ToOffsetEpisode(this DowntimeEpisode episode, TimeSpan offset) => episode with
    {
        StartedAt = episode.StartedAt.ToOffset(offset),
        LastObservedAt = episode.LastObservedAt.ToOffset(offset),
        EndedAt = episode.EndedAt?.ToOffset(offset),
    };
}
