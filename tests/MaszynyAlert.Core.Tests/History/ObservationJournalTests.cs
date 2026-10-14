using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class ObservationJournalTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(double seconds) => Base + TimeSpan.FromSeconds(seconds);

    private static ObservationInterval I(string machine, ObservationState state, double start, double end) =>
        new(machine, state, T(start), T(end));

    private static void Tick(ObservationJournal journal, double second, params (string, ObservationState)[] machines) =>
        journal.Observe(machines, T(second));

    private static void Ticks(ObservationJournal journal, int from, int to, string machine = "m1",
        ObservationState state = ObservationState.Observed)
    {
        for (int s = from; s <= to; s++) Tick(journal, s, (machine, state));
    }

    /// <summary>Niepuste wiersze pliku — tak liczy je dziennik (wiodący znak nowej linii przy dopisaniu).</summary>
    private static string[] Lines(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() : [];

    // ── Ciągła obserwacja i zmiana stanu ──────────────────────────────────────────────────────

    [Fact]
    public void ContinuousObservationEverySecondGivesOneIntervalFromFirstToLastTick()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 30);

        Assert.Equal([I("m1", ObservationState.Observed, 0, 30)], journal.Snapshot());
    }

    [Fact]
    public void StateChangeEndsTheOldIntervalAtTheChangeTickAndStartsTheNewOneThere()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 5);
        Ticks(journal, 6, 9, state: ObservationState.NoConnection);
        journal.CloseAll();

        Assert.Equal(
            [I("m1", ObservationState.Observed, 0, 6), I("m1", ObservationState.NoConnection, 6, 9)],
            journal.Snapshot());
    }

    // ── Dziura i cofnięty zegar: przedział NIGDY nie jest rozciągany ──────────────────────

    [Fact]
    public void GapLongerThanTenSecondsClosesAtTheLastObservationAndStartsAnewAtTheCurrentOne()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 5);
        Ticks(journal, 16, 20); // 11 s przerwy
        journal.CloseAll();

        Assert.Equal([I("m1", ObservationState.Observed, 0, 5), I("m1", ObservationState.Observed, 16, 20)],
            journal.Snapshot());
        // Ten sam wynik po restarcie — plik też nie ma czasu z dziury.
        Assert.Equal(journal.Snapshot(), new ObservationJournal(dir.File("observation.jsonl")).Snapshot());
    }

    [Fact]
    public void GapOfExactlyTenSecondsIsStillContinuous()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 5);
        Ticks(journal, 15, 20);

        Assert.Equal([I("m1", ObservationState.Observed, 0, 20)], journal.Snapshot());
    }

    [Fact]
    public void GapJustOverTenSecondsBreaksTheInterval()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Tick(journal, 0, ("m1", ObservationState.Observed));
        Tick(journal, 5, ("m1", ObservationState.Observed));
        journal.Observe([("m1", ObservationState.Observed)], T(15) + TimeSpan.FromTicks(1));
        Tick(journal, 16, ("m1", ObservationState.Observed));

        var snapshot = journal.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(T(5), snapshot[0].End);
        Assert.Equal(T(15) + TimeSpan.FromTicks(1), snapshot[1].Start);
    }

    [Fact]
    public void ClockMovedBackIsAGapNotAStretch()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 10);
        // Zegar cofnięty o 100 s: stary przedział kończy się na ostatniej obserwacji, nowy zaczyna od „teraz”.
        Ticks(journal, -100, -95);
        journal.CloseAll();

        Assert.Equal([I("m1", ObservationState.Observed, -100, -95), I("m1", ObservationState.Observed, 0, 10)],
            journal.Snapshot());
    }

    [Fact]
    public void ClockMovedBackInsideAKnownIntervalAddsNothing()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        Ticks(journal, 0, 20);
        Ticks(journal, 5, 8); // cofnięcie o 15 s, obserwacja wewnątrz znanego czasu
        journal.CloseAll();

        Assert.Equal([I("m1", ObservationState.Observed, 0, 20)], journal.Snapshot());
        Assert.Equal(journal.Snapshot(), new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void StateChangeTogetherWithAGapClosesTheOldStateAtItsLastObservation()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 5);
        Ticks(journal, 60, 65, state: ObservationState.NoConnection);
        journal.CloseAll();

        Assert.Equal(
            [I("m1", ObservationState.Observed, 0, 5), I("m1", ObservationState.NoConnection, 60, 65)],
            journal.Snapshot());
    }

    // ── Maszyna znika z odczytu ──────────────────────────────────────────────────────────────

    [Fact]
    public void MachineMissingFromTheReadingIsClosedAtItsLastObservationAndWritten()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        for (int s = 0; s <= 5; s++) Tick(journal, s, ("m1", ObservationState.Observed), ("m2", ObservationState.Observed));
        for (int s = 6; s <= 30; s++) Tick(journal, s, ("m2", ObservationState.Observed));

        var snapshot = journal.Snapshot();
        Assert.Contains(I("m1", ObservationState.Observed, 0, 5), snapshot);
        Assert.Contains(I("m2", ObservationState.Observed, 0, 30), snapshot);
        // m1 zapisany od razu przy domknięciu (bez CloseAll), m2 — co 10 s.
        var reloaded = new ObservationJournal(path).Snapshot();
        Assert.Contains(I("m1", ObservationState.Observed, 0, 5), reloaded);
        Assert.Contains(I("m2", ObservationState.Observed, 0, 30), reloaded);
    }

    [Fact]
    public void MachineReturningAfterAShortAbsenceStartsANewInterval()
    {
        // Brak maszyny w jednym odczycie (np. wyłączona i od razu włączona) domyka przedział — czasu bez odczytu
        // nie wolno doliczyć, nawet jeśli przerwa jest krótsza niż MaxObservationGap.
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 5);
        journal.Observe([], T(6));
        journal.Observe([], T(7));
        Ticks(journal, 8, 12);

        Assert.Equal([I("m1", ObservationState.Observed, 0, 5), I("m1", ObservationState.Observed, 8, 12)],
            journal.Snapshot());
    }

    // ── CloseAll i pojedyncza obserwacja ─────────────────────────────────────────────────────

    [Fact]
    public void SingleObservationIsAMomentAndNeverReachesTheFileOrTheSum()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        Tick(journal, 0, ("m1", ObservationState.Observed));
        journal.CloseAll();

        Assert.Empty(journal.Snapshot());
        Assert.Empty(Lines(path));
        Assert.Null(journal.LatestEnd);
    }

    [Fact]
    public void CloseAllClosesEveryOpenIntervalAndWritesIt()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        for (int s = 0; s <= 3; s++)
            Tick(journal, s, ("m1", ObservationState.Observed), ("m2", ObservationState.ScreenHidden),
                ("m3", ObservationState.NoConnection));
        Assert.Empty(Lines(path)); // < 10 s — jeszcze nic nie zapisane

        journal.CloseAll();

        Assert.Equal(3, Lines(path).Length);
        Assert.Equal(journal.Snapshot(), new ObservationJournal(path).Snapshot());
        // Po CloseAll kolejny tik zaczyna nowy przedział, a nie przedłuża starego.
        Tick(journal, 4, ("m1", ObservationState.Observed));
        Tick(journal, 5, ("m1", ObservationState.Observed));
        Assert.Contains(I("m1", ObservationState.Observed, 0, 3), journal.Snapshot());
        Assert.Contains(I("m1", ObservationState.Observed, 4, 5), journal.Snapshot());
    }

    [Fact]
    public void CloseAllThenObservationAfterAPauseNeverBridgesThePause()
    {
        // Utrata roli obserwatora / uśpienie: CloseAll, a po 5 s (krócej niż MaxObservationGap) znów obserwacja.
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 10);
        journal.CloseAll();
        Ticks(journal, 15, 20);

        Assert.Equal([I("m1", ObservationState.Observed, 0, 10), I("m1", ObservationState.Observed, 15, 20)],
            journal.Snapshot());
    }

    [Fact]
    public void CloseAllTwiceWritesNothingTheSecondTime()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        Ticks(journal, 0, 3);
        journal.CloseAll();
        journal.CloseAll();

        Assert.Single(Lines(path));
    }

    // ── Rytm zapisu otwartego przedziału ─────────────────────────────────────────────────────

    [Fact]
    public void OpenIntervalIsWrittenAfterTenSecondsThenEveryTenSecondsAndClosingAddsNoDuplicate()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        Ticks(journal, 0, 9);
        Assert.Empty(Lines(path));
        Ticks(journal, 10, 10);
        Assert.Single(Lines(path));
        Ticks(journal, 11, 19);
        Assert.Single(Lines(path));
        Ticks(journal, 20, 20);
        Assert.Equal(2, Lines(path).Length);

        journal.CloseAll(); // koniec == ostatnio zapisany
        Assert.Equal(2, Lines(path).Length);

        Ticks(journal, 30, 35);
        journal.CloseAll(); // koniec 35 ≠ zapisany — od razu jeden wiersz
        Assert.Equal(3, Lines(path).Length);
    }

    [Fact]
    public void CrashWithoutCloseKeepsOnlyTheLastWrittenEndNeverMore()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        Ticks(journal, 0, 27);
        // Brak CloseAll (awaria procesu): po restarcie koniec = ostatni zapis (20), nie 27 i nic dalej.
        Assert.Equal([I("m1", ObservationState.Observed, 0, 20)], new ObservationJournal(path).Snapshot());
    }

    // ── Restart i uszkodzony plik ────────────────────────────────────────────────────────────

    [Fact]
    public void RestartOnTheSameFileGivesTheSameSnapshot()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        for (int s = 0; s <= 40; s++)
        {
            var state = s < 20 ? ObservationState.Observed : ObservationState.ScreenHidden;
            Tick(journal, s, ("m1", state), ("m2", s % 15 == 0 ? ObservationState.NoConnection : ObservationState.Observed));
        }
        journal.CloseAll();
        journal.Apply([I("m9", ObservationState.Observed, -500, -400)]);

        var reloaded = new ObservationJournal(path);
        Assert.Equal(journal.Snapshot(), reloaded.Snapshot());
        Assert.Null(reloaded.Error);
    }

    [Fact]
    public void TruncatedLastLineIsSkippedReportedAndTheNextWriteStaysReadable()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply([I("m1", ObservationState.Observed, 0, 100)]);
        journal.Apply([I("m2", ObservationState.Observed, 0, 100)]);
        File.AppendAllText(path, "\n{\"machineId\":\"m3\",\"state\":\"obs");

        var reloaded = new ObservationJournal(path);
        Assert.NotNull(reloaded.Error);
        Assert.Equal(2, reloaded.Snapshot().Count);

        // Kolejny zapis po urwanym wierszu zaczyna się od nowej linii — nie skleja się z nim.
        reloaded.Apply([I("m4", ObservationState.Observed, 0, 50)]);
        var third = new ObservationJournal(path);
        Assert.Equal(3, third.Snapshot().Count);
        Assert.Contains(I("m4", ObservationState.Observed, 0, 50), third.Snapshot());
    }

    [Theory]
    [InlineData("{\"machineId\":\"m1\",\"state\":\"paused\",\"start\":\"2026-10-05T08:00:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"m1\",\"start\":\"2026-10-05T08:00:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"m1\",\"state\":\"observed\"}")]
    [InlineData("{\"machineId\":\"m1\",\"state\":\"observed\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"test\",\"state\":\"observed\",\"start\":\"2026-10-05T08:00:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"m1\",\"state\":\"observed\",\"start\":\"2026-10-05T08:02:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"m1\",\"state\":0,\"start\":\"2026-10-05T08:00:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"m1\",\"state\":\"none\",\"start\":\"2026-10-05T08:00:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"\",\"state\":\"observed\",\"start\":\"2026-10-05T08:00:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"state\":\"observed\",\"start\":\"2026-10-05T08:00:00+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("{\"machineId\":\"m1\",\"state\":\"observed\",\"start\":\"2019-12-31T23:59:59+00:00\",\"end\":\"2026-10-05T08:01:00+00:00\"}")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("\"tekst\"")]
    [InlineData("{}")]
    public void InvalidLineIsSkippedAndTheRestOfTheFileSurvives(string bad)
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var good = "{\"machineId\":\"m2\",\"state\":\"observed\",\"start\":\"2026-10-05T07:00:00+00:00\",\"end\":\"2026-10-05T07:30:00+00:00\"}";
        File.WriteAllText(path, good + "\n" + bad + "\n" + good.Replace("m2", "m3") + "\n");

        var journal = new ObservationJournal(path);

        Assert.Equal(["m2", "m3"], journal.Snapshot().Select(i => i.MachineId));
        Assert.DoesNotContain(journal.Snapshot(), i => i.MachineId is "m1" or "test" or "" || i.State == ObservationState.None);
    }

    [Fact]
    public void MachineIdLongerThanOneHundredCharactersIsSkipped()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Assert.False(journal.Apply([I(new string('x', 101), ObservationState.Observed, 0, 10)]));
        Assert.True(journal.Apply([I(new string('x', 100), ObservationState.Observed, 0, 10)]));
    }

    [Fact]
    public void StatesAreWrittenAsCamelCaseTextReadableInNotepad()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply([I("m1", ObservationState.Observed, 0, 10), I("m1", ObservationState.ScreenHidden, 10, 20),
            I("m1", ObservationState.NoConnection, 20, 30)]);

        string text = File.ReadAllText(path);
        Assert.Contains("\"state\":\"observed\"", text);
        Assert.Contains("\"state\":\"screenHidden\"", text);
        Assert.Contains("\"state\":\"noConnection\"", text);
        Assert.Contains("\"machineId\":\"m1\"", text);
    }

    // ── Suma zbiorów ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OverlappingAndTouchingIntervalsOfTheSameMachineAndStateAreMerged()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        journal.Apply([I("m1", ObservationState.Observed, 0, 10), I("m1", ObservationState.Observed, 5, 20),
            I("m1", ObservationState.Observed, 20, 30), I("m1", ObservationState.Observed, 40, 50)]);

        Assert.Equal([I("m1", ObservationState.Observed, 0, 30), I("m1", ObservationState.Observed, 40, 50)],
            journal.Snapshot());
    }

    [Fact]
    public void IntervalBridgingSeveralKnownOnesMergesThemAll()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        journal.Apply([I("m1", ObservationState.Observed, 0, 10), I("m1", ObservationState.Observed, 20, 30),
            I("m1", ObservationState.Observed, 40, 50)]);
        Assert.True(journal.Apply([I("m1", ObservationState.Observed, 5, 45)]));

        Assert.Equal([I("m1", ObservationState.Observed, 0, 50)], journal.Snapshot());
    }

    [Fact]
    public void DifferentStatesAndMachinesAreNeverMergedTogether()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        journal.Apply([I("m1", ObservationState.Observed, 0, 10), I("m1", ObservationState.NoConnection, 5, 20),
            I("m2", ObservationState.Observed, 10, 20)]);

        Assert.Equal(3, journal.Snapshot().Count);
    }

    [Fact]
    public void IntervalContainedInAKnownOneIsNotAppliedAndNotWritten()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        Assert.True(journal.Apply([I("m1", ObservationState.Observed, 0, 100)]));
        int lines = Lines(path).Length;

        Assert.False(journal.Apply([I("m1", ObservationState.Observed, 10, 20)]));
        Assert.False(journal.Apply([I("m1", ObservationState.Observed, 0, 100)]));
        Assert.False(journal.Apply([I("m1", ObservationState.Observed, 0, 0)])); // chwila — nie przedział
        Assert.False(journal.Apply([]));

        Assert.Equal(lines, Lines(path).Length);
    }

    [Fact]
    public void InvalidOrNullEntriesInAPortionAreSkippedAndTheRestApplied()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Assert.True(journal.Apply([null!, I("test", ObservationState.Observed, 0, 10), I("m1", ObservationState.None, 0, 10),
            I("m1", ObservationState.Observed, 10, 5), I("m1", ObservationState.Observed, 0, 10)]));

        Assert.Equal([I("m1", ObservationState.Observed, 0, 10)], journal.Snapshot());
    }

    [Fact]
    public void SameDataInAnyOrderGivesTheSameSum()
    {
        var random = new Random(20261005);
        var data = Enumerable.Range(0, 300).Select(_ =>
        {
            int start = random.Next(0, 5000);
            return I("m" + random.Next(1, 4), (ObservationState)random.Next(1, 4), start, start + random.Next(1, 120));
        }).ToArray();

        IReadOnlyList<ObservationInterval>? expected = null;
        for (int round = 0; round < 5; round++)
        {
            using var dir = new TempDir("observation");
            var journal = new ObservationJournal(dir.File("observation.jsonl"));
            var shuffled = data.OrderBy(_ => random.Next()).ToArray();
            // Część pojedynczo, część porcjami i powtórzenie całości — wynik ten sam.
            foreach (var item in shuffled.Take(100)) journal.Apply([item]);
            journal.Apply(shuffled.Skip(100).ToArray());
            journal.Apply(shuffled);
            expected ??= journal.Snapshot();
            Assert.Equal(expected, journal.Snapshot());
            Assert.Equal(expected, new ObservationJournal(dir.File("observation.jsonl")).Snapshot());
        }

        // Suma per maszyna i stan jest rozłączna i niestykająca się.
        foreach (var group in expected!.GroupBy(i => (i.MachineId, i.State)))
        {
            var list = group.OrderBy(i => i.Start).ToArray();
            for (int k = 1; k < list.Length; k++) Assert.True(list[k - 1].End < list[k].Start);
        }
    }

    // ── Scalanie pliku ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileOverTwentyThousandRedundantLinesIsRewrittenMergedWithoutLeavingTmp()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        // 20 001 coraz dłuższych wersji jednego przedziału — każda powiększa sumę, więc każda trafia do pliku.
        journal.Apply(Enumerable.Range(1, 20_001).Select(k => I("m1", ObservationState.Observed, 0, k)).ToArray());

        Assert.Single(Lines(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal([I("m1", ObservationState.Observed, 0, 20_001)], new ObservationJournal(path).Snapshot());
        Assert.Null(journal.Error);
    }

    [Fact]
    public void FileWithExactlyTwentyThousandLinesIsNotRewritten()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply(Enumerable.Range(1, 20_000).Select(k => I("m1", ObservationState.Observed, 0, k)).ToArray());

        Assert.Equal(20_000, Lines(path).Length);
    }

    [Fact]
    public void ManyDistinctIntervalsAreNotRewrittenBecauseMergingWouldNotHelp()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply(Enumerable.Range(0, 20_500).Select(k => I("m1", ObservationState.Observed, 3 * k, 3 * k + 1)).ToArray());

        Assert.Equal(20_500, Lines(path).Length);
    }

    [Fact]
    public void FileOverFiveThousandRedundantLinesIsMergedAtStartup()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        File.WriteAllLines(path, Enumerable.Range(1, 5_001).Select(k =>
            $"{{\"machineId\":\"m1\",\"state\":\"observed\",\"start\":\"{T(0):O}\",\"end\":\"{T(k):O}\"}}"));

        var journal = new ObservationJournal(path);

        Assert.Single(Lines(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal([I("m1", ObservationState.Observed, 0, 5_001)], journal.Snapshot());
    }

    [Fact]
    public void FileWithFiveThousandLinesIsNotMergedAtStartup()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        File.WriteAllLines(path, Enumerable.Range(1, 5_000).Select(k =>
            $"{{\"machineId\":\"m1\",\"state\":\"observed\",\"start\":\"{T(0):O}\",\"end\":\"{T(k):O}\"}}"));

        _ = new ObservationJournal(path);

        Assert.Equal(5_000, Lines(path).Length);
    }

    [Fact]
    public void MergedFileKeepsTheOpenIntervalAndContinuesFromIt()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        Ticks(journal, 0, 5);
        journal.Compact();
        Ticks(journal, 6, 8);
        journal.CloseAll();

        Assert.Equal([I("m1", ObservationState.Observed, 0, 8)], new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void LeftoverTmpFromAnInterruptedMergeIsIgnoredAndReplaced()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply([I("m1", ObservationState.Observed, 0, 100)]);
        // Awaria w połowie poprzedniego scalania: plik tymczasowy zostaje z połową danych.
        File.WriteAllText(path + ".tmp", "{\"machineId\":\"m1\",\"state\":\"obs");

        var journal = new ObservationJournal(path);
        Assert.Equal([I("m1", ObservationState.Observed, 0, 100)], journal.Snapshot());
        journal.Compact();

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal([I("m1", ObservationState.Observed, 0, 100)], new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void MergeThatCannotWriteTheTmpFileLeavesTheOldFileUntouched()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply([I("m1", ObservationState.Observed, 0, 100)]);
        journal.Apply([I("m1", ObservationState.Observed, 50, 200)]);
        byte[] before = File.ReadAllBytes(path);
        Directory.CreateDirectory(path + ".tmp"); // nie da się utworzyć pliku tymczasowego

        journal.Compact();

        Assert.NotNull(journal.Error);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal([I("m1", ObservationState.Observed, 0, 200)], journal.Snapshot());
    }

    [Fact]
    public void ReadOnlyFileGivesAnErrorKeepsDataInMemoryAndLeavesTheFileUntouched()
    {
        if (!OperatingSystem.IsWindows()) return; // atrybut „tylko do odczytu” blokuje zapis tylko na Windows
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        journal.Apply([I("m1", ObservationState.Observed, 0, 100)]);
        byte[] before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.True(journal.Apply([I("m2", ObservationState.Observed, 0, 100)]));
            Ticks(journal, 200, 230);
            journal.CloseAll();
            journal.Compact();

            Assert.NotNull(journal.Error);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Contains(I("m2", ObservationState.Observed, 0, 100), journal.Snapshot());
            Assert.Contains(I("m1", ObservationState.Observed, 200, 230), journal.Snapshot());
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void FileLockedAtStartupIsNotOverwrittenByALaterMerge()
    {
        // Plik zablokowany przy starcie (np. skaner antywirusowy, kopia zapasowa): dziennik nie wczytał historii.
        // Późniejsze scalenie zapisuje plik od nowa z PAMIĘCI — nie może przy tym skasować historii,
        // której nigdy nie przeczytał.
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        new ObservationJournal(path).Apply([I("old", ObservationState.Observed, -86_400, -3_600)]);

        ObservationJournal journal;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            journal = new ObservationJournal(path);
        Assert.NotNull(journal.Error);

        journal.Apply(Enumerable.Range(1, 20_001).Select(k => I("m1", ObservationState.Observed, 0, k)).ToArray());

        Assert.Contains(I("old", ObservationState.Observed, -86_400, -3_600), new ObservationJournal(path).Snapshot());
    }

    // ── IntervalsSince / LatestEnd ──────────────────────────────────────────────────────────

    [Fact]
    public void IntervalsSinceReturnsOnlyEndsNotBeforeSinceOrderedByEndMachineStateStartWithLimit()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        journal.Apply([
            I("m2", ObservationState.Observed, 0, 50),
            I("m1", ObservationState.NoConnection, 10, 50),
            I("m1", ObservationState.Observed, 20, 50),
            I("m1", ObservationState.Observed, 0, 10),
            I("m3", ObservationState.Observed, 0, 40),
            I("m1", ObservationState.ScreenHidden, 60, 70),
        ]);

        Assert.Equal(
            [I("m3", ObservationState.Observed, 0, 40), I("m1", ObservationState.Observed, 20, 50),
             I("m1", ObservationState.NoConnection, 10, 50), I("m2", ObservationState.Observed, 0, 50),
             I("m1", ObservationState.ScreenHidden, 60, 70)],
            journal.IntervalsSince(T(40), 100));
        Assert.Equal(2, journal.IntervalsSince(T(40), 2).Count);
        Assert.Equal(I("m3", ObservationState.Observed, 0, 40), journal.IntervalsSince(T(40), 1)[0]);
        Assert.Empty(journal.IntervalsSince(T(71), 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.IntervalsSince(T(0), 0));
        Assert.Equal(T(70), journal.LatestEnd);
    }

    [Fact]
    public void IntervalsSinceIncludesTheCurrentReachOfOpenIntervals()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 4); // nic jeszcze nie zapisane do pliku

        Assert.Equal([I("m1", ObservationState.Observed, 0, 4)], journal.IntervalsSince(T(3), 10));
        Assert.Equal(T(4), journal.LatestEnd);
    }

    // ── StateOf ─────────────────────────────────────────────────────────────────────────────

    private static MachineStatusSnapshot Snap(MachineConnectionState state, string id = "aidite1", string source = "vnc",
        bool watchRequired = false, bool? mainVisible = null) => new()
    {
        MachineId = id, Name = id, Host = "10.0.0.1", Profile = "aidite", State = state, Source = source,
        MainScreenWatchRequired = watchRequired, MainScreenVisible = mainVisible,
    };

    [Theory]
    [InlineData(MachineConnectionState.Disconnected)]
    [InlineData(MachineConnectionState.Connecting)]
    [InlineData(MachineConnectionState.Error)]
    public void StateOf_NotConnectedIsNoConnection(MachineConnectionState state)
    {
        Assert.Equal(ObservationState.NoConnection, ObservationInterval.StateOf(Snap(state)));
        // Brak połączenia wygrywa z flagą ekranu.
        Assert.Equal(ObservationState.NoConnection, ObservationInterval.StateOf(Snap(state, watchRequired: true, mainVisible: false)));
    }

    [Fact]
    public void StateOf_DisabledTestMachineAndTestScreenFileGiveNoEntry()
    {
        Assert.Null(ObservationInterval.StateOf(Snap(MachineConnectionState.Disabled)));
        Assert.Null(ObservationInterval.StateOf(Snap(MachineConnectionState.Connected, id: "test")));
        Assert.Null(ObservationInterval.StateOf(Snap(MachineConnectionState.Connected, source: "plik")));
        Assert.Null(ObservationInterval.StateOf(Snap(MachineConnectionState.Error, source: "plik")));
        Assert.Throws<ArgumentNullException>(() => ObservationInterval.StateOf(null!));
    }

    [Theory]
    [InlineData(false, null, ObservationState.Observed)]
    [InlineData(false, false, ObservationState.Observed)]
    [InlineData(true, true, ObservationState.Observed)]
    [InlineData(true, false, ObservationState.ScreenHidden)]
    [InlineData(true, null, ObservationState.ScreenHidden)] // zaraz po połączeniu, przed pierwszym sprawdzeniem HMI
    public void StateOf_ConnectedDependsOnMainScreenCheck(bool required, bool? visible, ObservationState expected) =>
        Assert.Equal(expected, ObservationInterval.StateOf(Snap(MachineConnectionState.Connected, watchRequired: required, mainVisible: visible)));

    // ── Wejście Observe ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ObserveSkipsInvalidMachinesAndDuplicatesInOneReading()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        for (int s = 0; s <= 5; s++)
            Tick(journal, s, ("test", ObservationState.Observed), ("", ObservationState.Observed),
                (new string('x', 101), ObservationState.Observed), ("m1", ObservationState.None),
                ("m2", ObservationState.Observed), ("m2", ObservationState.NoConnection));

        Assert.Equal([I("m2", ObservationState.Observed, 0, 5)], journal.Snapshot());
        Assert.Throws<ArgumentNullException>(() => journal.Observe(null!, T(6)));
    }

    [Fact]
    public void ObservationOfAMachineThatTurnsInvalidClosesItsInterval()
    {
        using var dir = new TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        Ticks(journal, 0, 5);
        Ticks(journal, 6, 8, state: ObservationState.None); // nieważny stan = brak maszyny w odczycie
        Ticks(journal, 9, 12);

        Assert.Equal([I("m1", ObservationState.Observed, 0, 5), I("m1", ObservationState.Observed, 9, 12)],
            journal.Snapshot());
    }

    [Fact]
    public async Task ObserveAndApplyFromTwoThreadsKeepTheSumConsistent()
    {
        using var dir = new TempDir("observation");
        var path = dir.File("observation.jsonl");
        var journal = new ObservationJournal(path);
        var network = Task.Run(() =>
        {
            for (int k = 0; k < 2_000; k++) journal.Apply([I("remote", ObservationState.Observed, 2 * k, 2 * k + 1)]);
        });
        for (int s = 0; s <= 2_000; s++) Tick(journal, s, ("m1", ObservationState.Observed));
        await network;
        journal.CloseAll();

        var snapshot = new ObservationJournal(path).Snapshot();
        Assert.Contains(I("m1", ObservationState.Observed, 0, 2_000), snapshot);
        Assert.Equal(2_000, snapshot.Count(i => i.MachineId == "remote"));
    }

    // ── IsValid (wpisy z sieci) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IsValidRejectsEndAfterTheGivenLimitAndAcceptsExactlyTheLimit()
    {
        var limit = T(300);
        Assert.True(I("m1", ObservationState.Observed, 0, 300).IsValid(limit));
        Assert.False(new ObservationInterval("m1", ObservationState.Observed, T(0), limit + TimeSpan.FromTicks(1)).IsValid(limit));
        Assert.True(new ObservationInterval("m1", ObservationState.Observed, T(0), T(1_000_000)).IsValid()); // bez limitu
    }

    [Fact]
    public void IsValidRejectsUndefinedEnumValues()
    {
        Assert.False(new ObservationInterval("m1", (ObservationState)4, T(0), T(1)).IsValid());
        Assert.False(new ObservationInterval("m1", (ObservationState)(-1), T(0), T(1)).IsValid());
    }
}
