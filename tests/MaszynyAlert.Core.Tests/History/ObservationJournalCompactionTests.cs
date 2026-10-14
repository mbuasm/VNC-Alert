using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class ObservationJournalCompactionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    private static ObservationInterval I(string machine, int start, int end) =>
        new(machine, ObservationState.Observed, T0.AddSeconds(start), T0.AddSeconds(end));

    private static int Lines(string path) => File.ReadLines(path).Count(l => !string.IsNullOrWhiteSpace(l));

    [Fact]
    public void FailedMergeIsRetriedOnlyAfterAnotherBatchOfLines()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        // Katalog w miejscu pliku tymczasowego: scalenie nie może go utworzyć.
        Directory.CreateDirectory(path + ".tmp");
        var journal = new ObservationJournal(path);
        journal.Apply(Enumerable.Range(1, 20_001).Select(k => I("m1", 0, k)).ToArray());
        Assert.NotNull(journal.Error);
        Assert.Equal(20_001, Lines(path));

        // Przeszkoda znika, ale do kolejnych 20 000 wierszy nie ma nowej próby — pojedyncze zapisy jej nie wywołują.
        Directory.Delete(path + ".tmp");
        journal.Apply(Enumerable.Range(1, 100).Select(k => I("m2", 0, k)).ToArray());
        Assert.Equal(20_101, Lines(path));

        // Po następnej porcji wierszy scalenie się udaje: plik scalony, suma ta sama.
        journal.Apply(Enumerable.Range(1, 20_000).Select(k => I("m3", 0, k)).ToArray());
        Assert.Equal(3, Lines(path));
        Assert.Equal([I("m1", 0, 20_001), I("m2", 0, 100), I("m3", 0, 20_000)], new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void MergeAfterAnUnreadableStartReadsTheFileFirstAndKeepsItsHistoryInMemory()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply([I("old", -86_400, -3_600)]);

        ObservationJournal journal;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            journal = new ObservationJournal(path);
        Assert.DoesNotContain(I("old", -86_400, -3_600), journal.Snapshot());

        journal.Compact();

        // Doczytana historia trafia też do pamięci (raport i odpowiedzi dla grupy), nie tylko do pliku.
        Assert.Contains(I("old", -86_400, -3_600), journal.Snapshot());
        Assert.Contains(I("old", -86_400, -3_600), new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void RepeatedWriteFailureLogsOneWarningAndASuccessfulWriteClearsTheError()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        Directory.CreateDirectory(path); // katalog w miejscu pliku: każde dopisanie się nie udaje
        var log = new RecordingLoggerFactory();
        var journal = new ObservationJournal(path, log.CreateLogger("test"));

        for (int k = 1; k <= 30; k++) journal.Apply([I("m" + k, 0, 5)]);
        Assert.NotNull(journal.Error);
        Assert.Single(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);

        Directory.Delete(path);
        journal.Apply([I("m99", 0, 5)]);

        Assert.Null(journal.Error);
        Assert.Single(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information);
        Assert.Contains(I("m99", 0, 5), new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void SuccessfulMergeClearsTheCorruptionError()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply([I("m1", 0, 5)]);
        File.AppendAllText(path, "{to nie jest JSON\n");
        var journal = new ObservationJournal(path);
        Assert.NotNull(journal.Error);

        journal.Compact();

        Assert.Null(journal.Error);
        Assert.Equal(1, Lines(path));
        Assert.Null(new ObservationJournal(path).Error);
    }

    [Fact]
    public void MergeIsSkippedWhileTheFileCannotBeRead()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply([I("old", -86_400, -3_600)]);
        var journal = new ObservationJournal(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            journal.Compact();

        Assert.NotNull(journal.Error);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Contains(I("old", -86_400, -3_600), new ObservationJournal(path).Snapshot());
    }
}
