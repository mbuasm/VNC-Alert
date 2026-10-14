using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public class ObservationReportTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset M(double minutes) => Base + TimeSpan.FromMinutes(minutes);
    private static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);

    private static ObservationInterval I(string machine, ObservationState state, double start, double end) =>
        new(machine, state, M(start), M(end));

    private const string M1 = "aidite1";

    // ── Pierwszeństwo stanów ────────────────────────────────────────────────────────────────

    [Fact]
    public void ObservedWinsOverNoConnectionForTheSameTime()
    {
        var summary = ObservationReport.Summarize(
            [I(M1, ObservationState.NoConnection, 0, 60), I(M1, ObservationState.Observed, 20, 40)], M1, M(0), M(60));

        Assert.Equal(Min(20), summary.Observed);
        Assert.Equal(Min(40), summary.NoConnection);
        Assert.Equal(TimeSpan.Zero, summary.ScreenHidden);
    }

    [Fact]
    public void ScreenHiddenWinsOverNoConnectionAndLosesToObserved()
    {
        var summary = ObservationReport.Summarize(
        [
            I(M1, ObservationState.NoConnection, 0, 60),
            I(M1, ObservationState.ScreenHidden, 10, 50),
            I(M1, ObservationState.Observed, 30, 40),
        ], M1, M(0), M(60));

        Assert.Equal(Min(10), summary.Observed);
        Assert.Equal(Min(30), summary.ScreenHidden);
        Assert.Equal(Min(20), summary.NoConnection);
        Assert.Equal(Min(60), summary.Observed + summary.ScreenHidden + summary.NoConnection);
    }

    [Fact]
    public void ResolveGivesDisjointIntervalsSortedByStart()
    {
        var resolved = ObservationReport.Resolve(
        [
            I(M1, ObservationState.NoConnection, 0, 60),
            I(M1, ObservationState.ScreenHidden, 10, 50),
            I(M1, ObservationState.Observed, 30, 40),
            I("other", ObservationState.Observed, 0, 60),
        ], M1);

        Assert.Equal(
        [
            I(M1, ObservationState.NoConnection, 0, 10),
            I(M1, ObservationState.ScreenHidden, 10, 30),
            I(M1, ObservationState.Observed, 30, 40),
            I(M1, ObservationState.ScreenHidden, 40, 50),
            I(M1, ObservationState.NoConnection, 50, 60),
        ], resolved);
    }

    // ── Dwóch obserwatorów ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoOverlappingObservedIntervalsFromTwoObserversCountOnce()
    {
        var summary = ObservationReport.Summarize(
            [I(M1, ObservationState.Observed, 0, 40), I(M1, ObservationState.Observed, 20, 60),
             I(M1, ObservationState.Observed, 0, 60)], M1, M(0), M(120));

        Assert.Equal(Min(60), summary.Observed);
    }

    [Fact]
    public void SplitBrainWithConflictingStatesNeverExceedsTheRangeLength()
    {
        // Podział sieci: obserwator A widzi maszynę, B nie ma połączenia — w tym samym czasie.
        var random = new Random(5102026);
        var data = Enumerable.Range(0, 500).Select(_ =>
        {
            double start = random.Next(0, 1440);
            return I(M1, (ObservationState)random.Next(1, 4), start, start + random.Next(1, 240));
        }).ToArray();

        var summary = ObservationReport.Summarize(data, M1, M(0), M(1440));
        var union = ObservationReport.Summarize(data.Select(i => i with { State = ObservationState.Observed }).ToArray(),
            M1, M(0), M(1440)).Observed;

        Assert.Equal(union, summary.Observed + summary.ScreenHidden + summary.NoConnection);
        Assert.True(union <= Min(1440));
    }

    // ── Zakres i pierwszy wpis ──────────────────────────────────────────────────────────────

    [Fact]
    public void TimeIsClippedToTheHalfOpenRange()
    {
        var data = new[] { I(M1, ObservationState.Observed, -30, 30), I(M1, ObservationState.NoConnection, 50, 200) };

        var summary = ObservationReport.Summarize(data, M1, M(0), M(100));

        Assert.Equal(Min(30), summary.Observed);
        Assert.Equal(Min(50), summary.NoConnection);
        Assert.Equal(M(-30), summary.FirstEntryAt); // sprzed zakresu — niezależnie od from
    }

    [Fact]
    public void IntervalTouchingTheRangeBoundaryAddsNothing()
    {
        var data = new[] { I(M1, ObservationState.Observed, -30, 0), I(M1, ObservationState.Observed, 100, 130) };

        var summary = ObservationReport.Summarize(data, M1, M(0), M(100));

        Assert.Equal(TimeSpan.Zero, summary.Observed);
    }

    [Fact]
    public void EmptyOrReversedRangeGivesZeros()
    {
        var data = new[] { I(M1, ObservationState.Observed, 0, 60) };
        Assert.Equal(TimeSpan.Zero, ObservationReport.Summarize(data, M1, M(30), M(30)).Observed);
        Assert.Equal(TimeSpan.Zero, ObservationReport.Summarize(data, M1, M(40), M(10)).Observed);
    }

    [Fact]
    public void NoEntriesGiveZerosAndNoFirstEntry()
    {
        var summary = ObservationReport.Summarize([I("other", ObservationState.Observed, 0, 60)], M1, M(0), M(60));

        Assert.Equal(new ObservationSummary(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null), summary);
        Assert.Null(ObservationReport.FirstEntryAt([], M1));
        Assert.Null(ObservationReport.FirstEntryAt([]));
    }

    [Fact]
    public void FirstEntryAtIsPerMachineOrGlobalInAnyStateIgnoringMoments()
    {
        var data = new[]
        {
            I(M1, ObservationState.NoConnection, 30, 40),
            I(M1, ObservationState.Observed, 10, 10), // chwila — nie wpis
            I("aidite2", ObservationState.ScreenHidden, 5, 6),
            I("test", ObservationState.Observed, 0, 100), // maszyna testowa — nieważna
        };

        Assert.Equal(M(30), ObservationReport.FirstEntryAt(data, M1));
        Assert.Equal(M(5), ObservationReport.FirstEntryAt(data));
        Assert.Equal(M(30), ObservationReport.Summarize(data, M1, M(100), M(200)).FirstEntryAt);
    }

    [Fact]
    public void InvalidEntriesNeverCount()
    {
        var data = new ObservationInterval[]
        {
            null!,
            new(M1, ObservationState.None, M(0), M(60)),
            new(M1, ObservationState.Observed, M(60), M(0)),
            new(M1, (ObservationState)7, M(0), M(60)),
        };

        var summary = ObservationReport.Summarize(data, M1, M(0), M(60));

        Assert.Equal(new ObservationSummary(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null), summary);
    }


    [Fact]
    public void CountedPartsRemovesExcludedTime()
    {
        // Filtr: wszystko poza [20, 30) — np. weekend.
        IEnumerable<(DateTimeOffset, DateTimeOffset)> Counted(DateTimeOffset from, DateTimeOffset to)
        {
            if (from < M(20)) yield return (from, to < M(20) ? to : M(20));
            if (to > M(30)) yield return (from > M(30) ? from : M(30), to);
        }

        var summary = ObservationReport.Summarize([I(M1, ObservationState.Observed, 0, 60)], M1, M(0), M(60), Counted);

        Assert.Equal(Min(50), summary.Observed);
    }

    [Fact]
    public void CountedPartsGetsTheAlreadyClippedInterval()
    {
        var calls = new List<(DateTimeOffset, DateTimeOffset)>();
        ObservationReport.Summarize([I(M1, ObservationState.Observed, -60, 600)], M1, M(0), M(60),
            (from, to) => { calls.Add((from, to)); return [(from, to)]; });

        Assert.Equal([(M(0), M(60))], calls);
    }

    [Fact]
    public void FilterReturningTimeOutsideTheIntervalCannotAddTime()
    {
        // Obca funkcja zwraca więcej niż dostała: części spoza przedziału, nakładające się i odwrócone.
        var summary = ObservationReport.Summarize([I(M1, ObservationState.Observed, 10, 20)], M1, M(0), M(60),
            (from, to) =>
            [
                (from - TimeSpan.FromHours(5), to + TimeSpan.FromHours(5)),
                (from, to), (from, to),
                (to, from),
            ]);

        Assert.Equal(Min(10), summary.Observed);
    }

    [Fact]
    public void FilterReturningNothingGivesZero()
    {
        var summary = ObservationReport.Summarize([I(M1, ObservationState.Observed, 0, 60)], M1, M(0), M(60), (_, _) => []);
        Assert.Equal(TimeSpan.Zero, summary.Observed);
        Assert.Equal(M(0), summary.FirstEntryAt); // pierwszy wpis nie zależy od filtra
    }

    [Fact]
    public void NullFilterCountsEverything()
    {
        var summary = ObservationReport.Summarize([I(M1, ObservationState.Observed, 0, 60)], M1, M(0), M(60), null);
        Assert.Equal(Min(60), summary.Observed);
    }

    [Fact]
    public void OtherMachinesInTheDataAreIgnored()
    {
        var summary = ObservationReport.Summarize(
            [I("aidite2", ObservationState.Observed, 0, 60), I(M1, ObservationState.ScreenHidden, 0, 15)], M1, M(0), M(60));

        Assert.Equal(TimeSpan.Zero, summary.Observed);
        Assert.Equal(Min(15), summary.ScreenHidden);
    }

    [Fact]
    public void MachineIdComparisonIsExact()
    {
        var summary = ObservationReport.Summarize([I("Aidite1", ObservationState.Observed, 0, 60)], M1, M(0), M(60));
        Assert.Equal(TimeSpan.Zero, summary.Observed);
    }

    [Fact]
    public void SummarizeAcceptsALazySequenceEnumeratedOnce()
    {
        int enumerations = 0;
        IEnumerable<ObservationInterval> Lazy()
        {
            enumerations++;
            yield return I(M1, ObservationState.Observed, 0, 60);
        }

        var summary = ObservationReport.Summarize(Lazy(), M1, M(0), M(60));

        Assert.Equal(Min(60), summary.Observed);
        Assert.Equal(M(0), summary.FirstEntryAt);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void JournalSnapshotFeedsTheReport()
    {
        using var dir = new TestUtil.TempDir("observation");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        journal.Apply([I(M1, ObservationState.Observed, 0, 30), I(M1, ObservationState.Observed, 15, 45),
            I(M1, ObservationState.NoConnection, 40, 60)]);

        var summary = ObservationReport.Summarize(journal.Snapshot(), M1, M(0), M(60));

        Assert.Equal(Min(45), summary.Observed);
        Assert.Equal(Min(15), summary.NoConnection);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ObservationReport.Summarize(null!, M1, M(0), M(1)));
        Assert.Throws<ArgumentNullException>(() => ObservationReport.Resolve([], null!));
        Assert.Throws<ArgumentNullException>(() => ObservationReport.FirstEntryAt(null!));
    }
}
