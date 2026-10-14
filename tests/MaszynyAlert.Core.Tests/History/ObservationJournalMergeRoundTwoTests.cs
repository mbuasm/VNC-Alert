using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class ObservationJournalMergeRoundTwoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    private static ObservationInterval I(string machine, double start, double end,
        ObservationState state = ObservationState.Observed) => new(machine, state, T0.AddSeconds(start), T0.AddSeconds(end));

    private static int Lines(string path) =>
        File.Exists(path) ? File.ReadLines(path).Count(l => !string.IsNullOrWhiteSpace(l)) : 0;

    /// <summary>2000 rozłącznych przedziałów maszyny „old” — plik ~200 KB, więc odczyt idzie wieloma blokami.</summary>
    private static ObservationInterval[] History() =>
        Enumerable.Range(0, 2000).Select(k => I("old", -100_000 + 10 * k, -100_000 + 10 * k + 5)).ToArray();

    private static ObservationInterval[] Growing(string machine, int count, int from = 1) =>
        Enumerable.Range(from, count).Select(k => I(machine, 0, k)).ToArray();

    /// <summary>Uchwyt blokujący drugą połowę pliku: odczyt dochodzi do niej i dostaje IOException, dopisywanie za końcem działa.</summary>
    private static FileStream LockSecondHalf(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long half = stream.Length / 2;
        stream.Lock(half, stream.Length - half);
        return stream;
    }

    // ── Urwany odczyt przy starcie ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadBrokenHalfwayAtStartupLosesNothingAtTheNextMerge()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply(History());
        Assert.Equal(2000, Lines(path));

        ObservationJournal journal;
        using (LockSecondHalf(path))
            journal = new ObservationJournal(path);
        Assert.NotNull(journal.Error);
        Assert.DoesNotContain(History()[^1], journal.Snapshot()); // druga połowa nieprzeczytana

        journal.Apply(Growing("m1", 20_001)); // > 20 000 wierszy → scalenie

        Assert.True(Lines(path) <= 2001, $"plik po scaleniu: {Lines(path)} wierszy");
        var reloaded = new ObservationJournal(path).Snapshot();
        Assert.Equal(2001, reloaded.Count);
        Assert.All(History(), h => Assert.Contains(h, reloaded));
        Assert.Contains(I("m1", 0, 20_001), reloaded);
        Assert.Equal(reloaded, journal.Snapshot()); // doczytana historia także w pamięci
    }

    // ── Zablokowany przy starcie i przy pierwszym scaleniu; odkładanie i jego granice ────

    [Fact]
    public void LockedAtStartAndAtTheFirstMergeThenMergesCompletelyAfterAnotherBatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply(History());

        var locked = LockSecondHalf(path);
        ObservationJournal journal;
        try
        {
            journal = new ObservationJournal(path);
            journal.Apply(Growing("m1", 20_001));
            // Scalenie nie mogło doczytać pliku: brak scalenia, komunikat, brak .tmp, wszystko nadal w pliku.
            Assert.NotNull(journal.Error);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { locked.Dispose(); }
        Assert.Equal(2000 + 20_001, Lines(path)); // (czytamy dopiero po zdjęciu blokady)

        journal.Apply(Growing("m2", 20_001));

        Assert.Equal(2002, Lines(path)); // scalone: 2000 × old + m1 + m2
        var reloaded = new ObservationJournal(path).Snapshot();
        Assert.All(History(), h => Assert.Contains(h, reloaded));
        Assert.Contains(I("m1", 0, 20_001), reloaded);
        Assert.Contains(I("m2", 0, 20_001), reloaded);
    }

    [Fact]
    public void DeferredMergeWaitsForExactlyTwentyThousandMoreLinesAndExplicitCompactIgnoresTheWait()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply(History());
        var journal = new ObservationJournal(path); // odczyt pełny: 2000 wierszy

        using (LockSecondHalf(path))
            journal.Apply(Growing("m1", 20_001)); // 22 001 > 20 000 → próba, nieudana → następna przy > 42 001
        Assert.Equal(22_001, Lines(path));

        journal.Apply(Growing("m2", 20_000)); // dokładnie 42 001 — jeszcze nie
        Assert.Equal(42_001, Lines(path));

        journal.Apply([I("m3", 0, 1)]); // 42 002 — tak
        Assert.Equal(2003, Lines(path));

        // Jawne Compact() nie czeka na odkładanie: po kolejnej nieudanej próbie i usunięciu przeszkody scala od razu.
        journal.Apply([I("m4", 0, 1), I("m4", 0, 2)]);
        using (LockSecondHalf(path)) journal.Compact();
        Assert.Equal(2005, Lines(path));
        journal.Compact();
        Assert.Equal(2004, Lines(path));
    }

    // ── Doczytanie nie dubluje i nie rozszerza ───────────────────────────────────────────────

    [Fact]
    public void MergeRereadDoesNotChangeTheSumAndOpenIntervalsGetNoDuplicateLine()
    {
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply(History());
        for (int s = 0; s <= 25; s++)
            journal.Observe([("m1", ObservationState.Observed), ("m2", ObservationState.NoConnection)], T0.AddSeconds(s));
        var before = journal.Snapshot();
        var summaryBefore = ObservationReport.Summarize(before, "m1", T0.AddDays(-2), T0.AddDays(1));

        for (int round = 0; round < 3; round++) journal.Compact();

        Assert.Equal(before, journal.Snapshot());
        Assert.Equal(summaryBefore, ObservationReport.Summarize(journal.Snapshot(), "m1", T0.AddDays(-2), T0.AddDays(1)));
        Assert.Equal(before.Count, Lines(path)); // jeden wiersz na przedział, z bieżącym zasięgiem otwartych
        Assert.Equal(before, new ObservationJournal(path).Snapshot());

        journal.CloseAll(); // koniec == zapisany przy scaleniu → bez nowego wiersza
        Assert.Equal(before.Count, Lines(path));
    }

    [Fact]
    public void RereadOfAPartlyWrittenOpenIntervalNeverStretchesIt()
    {
        // Plik ma starszą (krótszą) wersję otwartego przedziału, pamięć — dłuższą; doczytanie nie może jej ani skrócić,
        // ani wydłużyć ponad ostatnią obserwację.
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        for (int s = 0; s <= 17; s++) journal.Observe([("m1", ObservationState.Observed)], T0.AddSeconds(s));
        Assert.Equal([I("m1", 0, 10)], new ObservationJournal(path).Snapshot());

        journal.Compact();

        Assert.Equal([I("m1", 0, 17)], journal.Snapshot());
        Assert.Equal([I("m1", 0, 17)], new ObservationJournal(path).Snapshot());
    }

    // ── Plik znika / pojawia się w trakcie pracy ────────────────────────────────────────────────

    [Fact]
    public void FileDeletedWhileRunningIsRebuiltFromMemoryAtTheNextMerge()
    {
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply(History());
        File.Delete(path);

        journal.Apply([I("m1", 0, 60)]);
        Assert.Equal(1, Lines(path)); // dopisanie odtwarza plik, ale tylko z nowym wierszem
        journal.Compact();

        Assert.Equal(2001, Lines(path));
        Assert.Equal(journal.Snapshot(), new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void LinesAddedToTheFileFromOutsideAreMergedNotLost()
    {
        // Np. przywrócona kopia zapasowa albo ręcznie dopisany wiersz — scalenie nie może ich wyrzucić.
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply([I("m1", 0, 60)]);
        File.AppendAllText(path,
            "\n{\"machineId\":\"backup\",\"state\":\"noConnection\",\"start\":\"2026-10-04T08:00:00+00:00\",\"end\":\"2026-10-04T09:00:00+00:00\"}\n");

        journal.Compact();

        Assert.Contains(journal.Snapshot(), i => i.MachineId == "backup");
        Assert.Equal(2, Lines(path));
    }

    [Fact]
    public void FileReplacedByAnUnrelatedOlderCopyIsUnitedWithMemory()
    {
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        var other = dir.File("copy.jsonl");
        new ObservationJournal(other).Apply([I("m1", -600, -300), I("m9", 0, 30)]);
        var journal = new ObservationJournal(path);
        journal.Apply([I("m1", -400, 100)]);
        File.Copy(other, path, overwrite: true);

        journal.Compact();

        Assert.Equal([I("m1", -600, 100), I("m9", 0, 30)], journal.Snapshot());
        Assert.Equal(journal.Snapshot(), new ObservationJournal(path).Snapshot());
    }

    // ── Wyścigi ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ObserveApplyAndCompactInParallelKeepEverythingAndLeaveNoTmp()
    {
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        using var start = new ManualResetEventSlim();
        var ticks = Task.Run(() =>
        {
            start.Wait();
            for (int s = 0; s <= 3000; s++)
                journal.Observe([("m1", ObservationState.Observed), ("m2", s % 500 < 250 ? ObservationState.Observed : ObservationState.ScreenHidden)],
                    T0.AddSeconds(s));
        });
        var network = Task.Run(() =>
        {
            start.Wait();
            for (int k = 0; k < 3000; k++) journal.Apply([I("remote", 2 * k, 2 * k + 1), I("grow", 0, k + 1)]);
        });
        var merges = Task.Run(() =>
        {
            start.Wait();
            for (int k = 0; k < 60; k++) { journal.Compact(); Thread.Sleep(1); }
        });
        start.Set();
        await Task.WhenAll(ticks, network, merges);
        journal.CloseAll();

        var snapshot = journal.Snapshot();
        Assert.Contains(I("m1", 0, 3000), snapshot);
        Assert.Contains(I("grow", 0, 3000), snapshot);
        Assert.Equal(3000, snapshot.Count(i => i.MachineId == "remote"));
        Assert.Equal(snapshot, new ObservationJournal(path).Snapshot());
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Null(journal.Error);
    }

    [Fact]
    public void TwoJournalsOnTheSameFileNeverLoseEachOthersLines()
    {
        // Program ma jedną instancję (muteks), ale np. Panel czytający dziennik obok działającego obserwatora to
        // drugi obiekt na tym samym pliku. Scalenie jednego nie może skasować tego, co dopisał drugi.
        using var dir = new TempDir("observation-r2");
        var path = dir.File("observation.jsonl");
        var first = new ObservationJournal(path);
        var second = new ObservationJournal(path);
        first.Apply([I("a", 0, 10)]);
        second.Apply([I("b", 0, 10)]);
        first.Compact();
        second.Apply([I("c", 0, 10)]);
        second.Compact();

        var reloaded = new ObservationJournal(path).Snapshot();
        Assert.Equal(["a", "b", "c"], reloaded.Select(i => i.MachineId));
    }
}
