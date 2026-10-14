using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeReportFutureEndTests
{
    private static readonly TimeSpan BuildLocal = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BuildCi = TimeSpan.FromMilliseconds(1000);

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);

    private static DowntimeEpisode Episode(DateTimeOffset start, DateTimeOffset end, string machine = "a") =>
        new(machine, "A", "Servo 6", start, end, null, EpisodeId: Guid.NewGuid());

    private static readonly DateTimeOffset FarFuture = new(9999, 12, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EpizodDoRoku9999_BezChwiliTeraz_KosztOgraniczony()
    {
        var episode = Episode(At("2026-09-15T08:00:00Z"), FarFuture);
        Assert.True(episode.IsValid());

        _ = DowntimeReport.Build([episode], TimeZoneInfo.Utc); // rozgrzewka (JIT)
        IReadOnlyList<DowntimeSlice> rows = null!;
        TimeSpan elapsed = PerformanceLimits.Measure(() => rows = DowntimeReport.Build([episode], TimeZoneInfo.Utc));

        PerformanceLimits.AssertUnder(elapsed, BuildLocal, BuildCi, "Build (epizod do 9999, bez chwili „teraz”)");
        // 400 dni po trzy zmiany (weekendy wycięte) — kilkaset kawałków, nie miliony.
        Assert.InRange(rows.Count, 1, 3 * 400);
        Assert.All(rows, r => Assert.True(r.IsLong));
        Assert.True(rows.Max(r => r.End) <= episode.StartedAt + DowntimeReport.MaxSlicedSpan);
    }

    [Fact]
    public void EpizodDoRoku9999_ZChwilaTeraz_KoniecNaTerazPlusDoba()
    {
        var now = At("2026-09-16T10:00:00Z"); // środa
        var episode = Episode(At("2026-09-15T08:00:00Z"), FarFuture);

        _ = DowntimeReport.Build([episode], TimeZoneInfo.Utc, now: now); // rozgrzewka (JIT)
        IReadOnlyList<DowntimeSlice> rows = null!;
        TimeSpan elapsed = PerformanceLimits.Measure(() => rows = DowntimeReport.Build([episode], TimeZoneInfo.Utc, now: now));

        PerformanceLimits.AssertUnder(elapsed, BuildLocal, BuildCi, "Build (epizod do 9999, z chwilą „teraz”)");
        Assert.Equal(now + DowntimeReport.FutureTolerance, rows.Max(r => r.End));
        // Wt 08:00 → czw 10:00 = 50 h = 7 kawałków zmian; nadal jeden postój, oznaczony jako długi (czas całego epizodu).
        Assert.Equal(7, rows.Count);
        Assert.Equal(50 * 60, rows.Sum(r => r.Minutes), 6);
        Assert.All(rows, r => Assert.True(r.IsLong));
        Assert.Equal(1, DowntimeReport.Totals(rows).All.Episodes);
    }

    [Fact]
    public void EpizodCalyPoTerazPlusDoba_NieWchodzi()
    {
        var now = At("2026-09-15T10:00:00Z");
        var episode = Episode(At("2026-09-20T08:00:00Z"), At("2026-09-22T08:00:00Z"));

        Assert.Empty(DowntimeReport.Build([episode], TimeZoneInfo.Utc, now: now));
    }

    [Fact]
    public void ZwykleEpizody_BezZmianZChwilaTerazIBezNiej()
    {
        var now = At("2026-09-17T12:00:00Z");
        DowntimeEpisode[] episodes =
        [
            Episode(At("2026-09-15T13:58:00Z"), At("2026-09-15T14:04:00Z")),
            Episode(At("2026-09-15T20:30:00Z"), At("2026-09-16T07:15:00Z"), "b"),
            // Trwający, z ostatnią obserwacją w przyszłości w granicy doby (rozjechane zegary komputerów) — bez przycięcia.
            Episode(At("2026-09-17T11:00:00Z"), At("2026-09-18T02:00:00Z"), "c"),
        ];

        var withNow = DowntimeReport.Build(episodes, TimeZoneInfo.Utc, now: now);
        var without = DowntimeReport.Build(episodes, TimeZoneInfo.Utc);

        Assert.Equal(without, withNow);
        Assert.Equal(6 + 645 + 900, without.Sum(r => r.Minutes), 6);
    }

    [Fact]
    public void Przyciecie_NieZmieniaProgowLiczonychNaCalymEpizodzie()
    {
        var now = At("2026-09-15T08:00:00Z");
        var episode = Episode(At("2026-09-16T07:58:00Z"), At("2026-09-16T09:00:00Z"));

        var row = Assert.Single(DowntimeReport.Build([episode], TimeZoneInfo.Utc, now: now));
        Assert.Equal(2, row.Minutes, 6);
        Assert.True(row.IsLong);
    }

    [Fact]
    public void Widok_PodajeChwileTeraz()
    {
        string code = File.ReadAllText(Path.Combine(TestUtil.TestPaths.RepositoryRoot,
            "src", "MaszynyAlert.App", "Views", "DowntimeView.xaml.cs"));
        Assert.Contains("var checkedAt = _orchestrator.Clock.Now;", code, StringComparison.Ordinal);
        Assert.Contains("now: checkedAt)", code, StringComparison.Ordinal);
    }
}
