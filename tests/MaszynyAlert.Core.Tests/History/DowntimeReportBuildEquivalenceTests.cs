using System.Globalization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class DowntimeReportBuildEquivalenceTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly TimeSpan[] Offsets = [TimeSpan.Zero, TimeSpan.FromHours(2), TimeSpan.FromHours(1), TimeSpan.FromHours(-5)];

    // ── Wyrocznia: DowntimeReport.Build ───────────────────────────

    private static List<DowntimeSlice> ReferenceBuild(IEnumerable<DowntimeEpisode> episodes, TimeZoneInfo zone,
        DowntimeCategoryClassifier? classifier = null, DateTimeOffset? now = null)
    {
        var result = new List<DowntimeSlice>();
        var merged = DowntimeReport.MergeOverlaps(episodes);
        var errorsByMachine = merged.Where(e => !DowntimeReport.IsIdleEpisode(e))
            .ToLookup(ReportMachineKey.Of, e => (Start: e.StartedAt, End: e.End));
        foreach (var episode in merged)
        {
            IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> own = [(episode.StartedAt, ReferenceSliceEnd(episode, now))];
            TimeSpan measured = episode.End - episode.StartedAt;
            if (DowntimeReport.IsIdleEpisode(episode) && errorsByMachine[ReportMachineKey.Of(episode)].ToArray() is { Length: > 0 } errors)
            {
                own = ReferenceSubtract(own[0].Start, own[0].End, errors);
                measured = ReferenceSubtract(episode.StartedAt, episode.End, errors)
                    .Aggregate(TimeSpan.Zero, (sum, part) => sum + (part.End - part.Start));
            }
            if (measured <= DowntimeReport.MinEpisode) continue;
            bool isLong = measured > DowntimeReport.MaxEpisode;
            string category = classifier?.CategoryOf(episode)
                ?? DowntimeCategories.Of(episode) ?? DowntimeCategories.Unrecognized;
            foreach (var (partStart, partEnd) in own.SelectMany(part => ReportCalendar.ReportedIntervals(part.Start, part.End, zone)))
            {
                var cursor = partStart;
                while (cursor < partEnd)
                {
                    var local = ReportCalendar.LocalWall(cursor, zone);
                    int hour = local.Hour;
                    var day = DateOnly.FromDateTime(hour < 6 ? ReportCalendar.AddWall(local, TimeSpan.FromDays(-1)) ?? local : local);
                    string shift = hour is >= 6 and < 14 ? "06:00–14:00" : hour is >= 14 and < 22 ? "14:00–22:00" : "22:00–06:00";
                    var nextLocal = ReportCalendar.AddWall(local.Date,
                        TimeSpan.FromHours(hour < 6 ? 6 : hour < 14 ? 14 : hour < 22 ? 22 : 30));
                    var boundary = ReportCalendar.NextInstant(nextLocal, zone, cursor);
                    var until = partEnd < boundary ? partEnd : boundary;
                    result.Add(new(day, shift, episode.MachineName, episode.Reason, cursor, until,
                        episode.Interrupted, episode.EndedAt is null)
                    {
                        IsLong = isLong, Category = category, MachineId = episode.MachineId, EpisodeId = episode.EpisodeId,
                    });
                    cursor = until;
                }
            }
        }
        return result;
    }

    private static DateTimeOffset ReferenceSliceEnd(DowntimeEpisode episode, DateTimeOffset? now)
    {
        DateTimeOffset limit = now is { } current
            ? (current > DateTimeOffset.MaxValue - DowntimeReport.FutureTolerance ? DateTimeOffset.MaxValue : current + DowntimeReport.FutureTolerance)
            : (episode.StartedAt > DateTimeOffset.MaxValue - DowntimeReport.MaxSlicedSpan ? DateTimeOffset.MaxValue : episode.StartedAt + DowntimeReport.MaxSlicedSpan);
        return episode.End < limit ? episode.End : limit;
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> ReferenceSubtract(DateTimeOffset start, DateTimeOffset end,
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> cut)
    {
        var parts = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var cursor = start;
        foreach (var (cutStart, cutEnd) in cut.Where(c => c.End > start && c.Start < end).OrderBy(c => c.Start))
        {
            if (cutStart > cursor) parts.Add((cursor, cutStart < end ? cutStart : end));
            if (cutEnd > cursor) cursor = cutEnd;
            if (cursor >= end) break;
        }
        if (cursor < end) parts.Add((cursor, end));
        return parts;
    }

    // ── Porównanie co do wartości i zapisu ─────────────────────────────────────────────────────────────

    /// <summary>Wiersz jako tekst ze WSZYSTKIMI polami; czasy w formacie „O” — z przesunięciem strefy, nie tylko chwilą.</summary>
    private static string Canonical(DowntimeSlice s) => string.Join('|',
        s.Day.ToString("O", CultureInfo.InvariantCulture), s.Shift, s.Machine ?? "<null>", s.Reason ?? "<null>",
        s.Start.ToString("O", CultureInfo.InvariantCulture), s.End.ToString("O", CultureInfo.InvariantCulture),
        s.Interrupted, s.Ongoing, s.IsLong, s.Category,
        s.MachineId ?? "<null>", s.EpisodeId, s.Kind);

    private static void AssertSame(IReadOnlyList<DowntimeSlice> expected, IReadOnlyList<DowntimeSlice> actual, string context)
    {
        string[] a = [.. expected.Select(Canonical)];
        string[] b = [.. actual.Select(Canonical)];
        if (a.SequenceEqual(b)) return;
        int first = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i], Math.Min(a.Length, b.Length));
        Assert.Fail($"{context}: wierszy {a.Length} → {b.Length}; pierwsza różnica na pozycji {first}:\n"
            + $"  przed: {(first < a.Length ? a[first] : "-")}\n  po:    {(first < b.Length ? b[first] : "-")}");
    }

    // ── Dane losowe: nakładanie, styki, weekendy, zmiana czasu, wpisy bez ID, przyszłość, przesunięcia stref ──

    private static readonly (string? Id, string? Name)[] Machines =
        [("a1", "Aidite 1"), ("a2", "Aidite 2"), ("amm580", "580"), (null, "Bez ID"), ("", "Bez ID"), (null, "Aidite 1"), (null, null)];

    private static (string Reason, string? Category) RandomKind(Random random) => random.Next(10) switch
    {
        0 or 1 => (AlertsIdle, DowntimeCategories.Idle),
        2 => (AlertsIdleAtZero, DowntimeCategories.IdleAtZero),
        3 => (AlertsIdle, null), // postój bez pracy rozpoznany z tekstu (wpis bez kategorii)
        4 => ("Servo 6", "restart"),
        5 => ("Błąd: X", null),
        6 => (AlertsIdle, Rule.SuppressedCategory), // „bez alarmu” = nieznana kategoria → postój z tekstu
        7 => ("Tacka nie pobrana", Rule.SuppressedCategory),
        8 => ("Servo 6", DowntimeCategories.Idle), // rodzaj z kategorii, tekst błędu
        _ => ("Drzwi otwarte", "  "),
    };

    private const string AlertsIdle = MaszynyAlert.Core.Alerts.AlertStateMachine.MachineIdleShortText;
    private const string AlertsIdleAtZero = MaszynyAlert.Core.Alerts.AlertStateMachine.MachineIdleAtZeroShortText;

    private static List<DowntimeEpisode> RandomEpisodes(Random random)
    {
        var origin = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2));
        int windowMinutes = random.Next(3) switch { 0 => 6 * 60, 1 => 3 * 24 * 60, _ => 16 * 24 * 60 };
        int count = random.Next(0, 45);
        var ids = new List<Guid>();
        var result = new List<DowntimeEpisode>(count);
        for (int i = 0; i < count; i++)
        {
            var (id, name) = Machines[random.Next(Machines.Length)];
            var start = origin.AddMinutes(random.Next(windowMinutes)).AddSeconds(random.Next(60)).AddTicks(random.Next(3) == 0 ? random.Next(10_000_000) : 0);
            TimeSpan length = random.Next(12) switch
            {
                0 => TimeSpan.Zero,
                1 => -TimeSpan.FromMinutes(random.Next(1, 90)), // koniec przed początkiem (uszkodzony wiersz pliku)
                2 => TimeSpan.FromSeconds(random.Next(1, 120)),
                3 or 4 or 5 => TimeSpan.FromMinutes(random.Next(1, 120)),
                6 or 7 => TimeSpan.FromMinutes(random.Next(55, 400)),
                8 => TimeSpan.FromHours(random.Next(1, 80)),
                9 => TimeSpan.FromDays(random.Next(1, 30)),
                10 when result.Count > 0 => result[random.Next(result.Count)].End - start, // styk z innym epizodem
                _ => TimeSpan.FromMinutes(random.Next(1, 30)),
            };
            if (random.Next(8) == 0 && result.Count > 0)
            {
                // Początek dokładnie na końcu albo początku innego epizodu (styk, wspólny początek).
                var other = result[random.Next(result.Count)];
                start = random.Next(2) == 0 ? other.End : other.StartedAt;
            }
            start = start.ToOffset(Offsets[random.Next(Offsets.Length)]);
            var end = (start + length).ToOffset(Offsets[random.Next(Offsets.Length)]);
            bool open = random.Next(5) == 0;
            var lastObserved = open ? end : random.Next(4) == 0 ? start : end;
            var (reason, category) = RandomKind(random);
            Guid episodeId = random.Next(12) switch
            {
                0 when ids.Count > 0 => ids[random.Next(ids.Count)], // ten sam identyfikator dwa razy (dwaj obserwatorzy)
                1 => Guid.Empty,
                _ => Guid.NewGuid(),
            };
            ids.Add(episodeId);
            result.Add(new DowntimeEpisode(id!, name!, reason, start, lastObserved, open ? null : end,
                Interrupted: random.Next(6) == 0, EpisodeId: episodeId, Origin: random.Next(3) == 0 ? "PC-" + random.Next(3) : null,
                Category: category));
        }
        if (random.Next(15) == 0)
        {
            var far = new DateTimeOffset(9999, 12, 31, 20, 0, 0, TimeSpan.Zero);
            result.Add(new DowntimeEpisode("a1", "Aidite 1", AlertsIdle, far.AddHours(-30), far, far, EpisodeId: Guid.NewGuid(),
                Category: DowntimeCategories.Idle));
            result.Add(new DowntimeEpisode("a1", "Aidite 1", "Servo 6", far.AddHours(-2), far, null, EpisodeId: Guid.NewGuid(), Category: "inny"));
        }
        return result;
    }

    private static DateTimeOffset? RandomNow(Random random, IReadOnlyList<DowntimeEpisode> episodes)
    {
        var origin = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2));
        var ordinary = episodes.Where(e => e.End.Year < 9000).ToArray();
        return random.Next(8) switch
        {
            0 => null,
            1 => DateTimeOffset.MaxValue.AddHours(-random.Next(1, 48)),
            2 when ordinary.Length > 0 => ordinary[random.Next(ordinary.Length)].End.AddHours(random.Next(-30, 30)),
            _ => origin.AddMinutes(random.Next(-3 * 24 * 60, 20 * 24 * 60)),
        };
    }

    [Fact]
    public void Build_EpisodeEndingInYear9999_SameRowsAsBeforeTheChange()
    {
        var start = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.FromHours(2));
        var far = new DateTimeOffset(9999, 12, 31, 20, 0, 0, TimeSpan.Zero);
        List<DowntimeEpisode> episodes =
        [
            new("a1", "Aidite 1", AlertsIdle, start, far, null, EpisodeId: Guid.NewGuid(), Category: DowntimeCategories.Idle),
            new("a1", "Aidite 1", "Servo 6", start.AddHours(2), start.AddHours(3), start.AddHours(3), EpisodeId: Guid.NewGuid(), Category: "restart"),
            new("a1", "Aidite 1", "Servo 6", start.AddDays(40), start.AddDays(41), start.AddDays(41), EpisodeId: Guid.NewGuid(), Category: "restart"),
        ];
        foreach (DateTimeOffset? now in new DateTimeOffset?[] { start.AddHours(5), start.AddDays(100), null })
            AssertSame(ReferenceBuild(episodes, Warsaw, null, now), DowntimeReport.Build(episodes, Warsaw, null, now), $"now {now:O}");
    }

    private static RuleSet ExampleRules() =>
        ConfigLoader.LoadRules(Path.Combine(TestPaths.RepositoryRoot, "config", "rules.example.json"));

    [Fact]
    public void Build_RandomEpisodes_SameRowsAsBeforeTheChange()
    {
        var rules = ExampleRules();
        MachineConfig[] machines =
        [
            new() { Id = "a1", Name = "Aidite 1", Host = "127.0.0.1", Profile = "opencnc" },
            new() { Id = "amm580", Name = "580", Host = "127.0.0.1", Profile = "amm580" },
        ];
        var random = new Random(20261012);
        int rows = 0, idleWithErrors = 0;
        for (int round = 0; round < 3000; round++)
        {
            var episodes = RandomEpisodes(random);
            var zone = random.Next(3) == 0 ? TimeZoneInfo.Utc : Warsaw;
            var now = RandomNow(random, episodes);
            var classifier = random.Next(2) == 0 ? null : new DowntimeCategoryClassifier(rules, machines);
            var expected = ReferenceBuild(episodes, zone, classifier is null ? null : new DowntimeCategoryClassifier(rules, machines), now);
            var actual = DowntimeReport.Build(episodes, zone, classifier, now);
            AssertSame(expected, actual, $"przypadek {round} (strefa {zone.Id}, now {now:O}, epizodów {episodes.Count})");
            rows += expected.Count;
            var merged = DowntimeReport.MergeOverlaps(episodes);
            idleWithErrors += merged.Count(e => DowntimeReport.IsIdleEpisode(e)
                && merged.Any(o => !DowntimeReport.IsIdleEpisode(o) && ReportMachineKey.Of(o) == ReportMachineKey.Of(e)
                    && o.End > e.StartedAt && o.StartedAt < e.End));
        }
        // Dane naprawdę ćwiczą odejmowanie błędów od postojów bez pracy, nie tylko puste przypadki.
        Assert.True(rows > 50_000, $"wierszy {rows}");
        Assert.True(idleWithErrors > 5_000, $"postojów bez pracy nakładających się na błąd tej samej maszyny: {idleWithErrors}");
    }

    /// <summary>Błędy tej samej maszyny nakładające się na siebie w dowolny sposób (wejście inne niż po scaleniu) — odejmowanie.</summary>
    [Fact]
    public void Build_IdleAgainstManyOverlappingErrorsOfOneMachine_SameRowsAsBeforeTheChange()
    {
        var random = new Random(7);
        var day = new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.FromHours(2));
        for (int round = 0; round < 500; round++)
        {
            var episodes = new List<DowntimeEpisode>
            {
                new("a1", "Aidite 1", AlertsIdle, day.AddMinutes(random.Next(0, 60)), day.AddMinutes(random.Next(200, 600)), null,
                    EpisodeId: Guid.NewGuid(), Category: DowntimeCategories.Idle),
            };
            for (int i = 0; i < random.Next(1, 25); i++)
            {
                var start = day.AddMinutes(random.Next(-30, 650)).ToOffset(Offsets[random.Next(Offsets.Length)]);
                var end = start.AddMinutes(random.Next(0, 90)).ToOffset(Offsets[random.Next(Offsets.Length)]);
                episodes.Add(new("a1", "Aidite 1", "Servo " + i, start, end, end, EpisodeId: Guid.NewGuid(), Category: "restart"));
            }
            var now = day.AddHours(random.Next(-2, 30));
            AssertSame(ReferenceBuild(episodes, Warsaw, null, now), DowntimeReport.Build(episodes, Warsaw, null, now), $"runda {round}");
        }
    }

    [Fact]
    public void Build_QuarterOfHallHistory_SameRowsAsBeforeTheChange()
    {
        var episodes = SyntheticHall(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
        var expected = ReferenceBuild(episodes, Warsaw, null, now);
        var actual = DowntimeReport.Build(episodes, Warsaw, null, now);
        Assert.True(expected.Count > 5_000, $"wierszy {expected.Count}");
        AssertSame(expected, actual, "kwartał hali");
    }

    internal static List<DowntimeEpisode> SyntheticHall(DateTimeOffset from, DateTimeOffset to)
    {
        var random = new Random(12);
        var result = new List<DowntimeEpisode>();
        string[] errors = ["Servo 6", "Bazowanie (Home)", "Tacka nie pobrana", "Ciśnienie powietrza", "Błąd: X", "Narzędzie nie wróciło"];
        for (int m = 1; m <= 7; m++)
            for (var day = from; day < to; day = day.AddDays(1))
            {
                var cursor = day;
                void Add(int count, string reason, string category, double meanMinutes, bool overlap)
                {
                    for (int i = 0; i < count; i++)
                    {
                        cursor = cursor.AddMinutes(random.Next(5, 60));
                        var stop = cursor.AddMinutes(Math.Max(0.5, -meanMinutes * Math.Log(1 - random.NextDouble())));
                        // Część błędów zaczyna się w trakcie postoju bez pracy (błąd zamyka alert postoju) — odejmowanie.
                        var start = overlap && random.Next(3) == 0 ? cursor.AddMinutes(-random.Next(1, 40)) : cursor;
                        result.Add(new DowntimeEpisode("aidite" + m, "Aidite " + m, reason, start, stop, stop,
                            EpisodeId: Guid.NewGuid(), Category: category));
                        cursor = stop;
                    }
                }
                Add(6, AlertsIdle, DowntimeCategories.Idle, 33, overlap: false);
                Add(3, errors[random.Next(errors.Length)], "inny", 20, overlap: true);
                Add(5, AlertsIdleAtZero, DowntimeCategories.IdleAtZero, 5, overlap: false);
            }
        return result;
    }

    // ── Chwila „teraz” (Panel nie liczy raportu od nowa, gdy wynik od niej nie zależy) ──────────────────

    [Fact]
    public void DependsOnNow_FalseWhenNoEpisodeReachesNowPlusTolerance_ResultIdenticalForLaterNow()
    {
        var episodes = SyntheticHall(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        // Epizod „z przyszłości” w granicach tolerancji (zegary grupy): nie przycinany ani teraz, ani później.
        var latest = episodes.Max(e => e.End);
        episodes.Add(new DowntimeEpisode("aidite1", "Aidite 1", "Servo 6", latest, latest.AddHours(20), null, EpisodeId: Guid.NewGuid(), Category: "inny"));
        var builtAt = latest;
        var later = latest.AddDays(3);
        var latestEnd = episodes.Max(e => e.End);
        Assert.False(DowntimeReport.DependsOnNow(latestEnd, builtAt));
        Assert.False(DowntimeReport.DependsOnNow(latestEnd, later));
        AssertSame(DowntimeReport.Build(episodes, Warsaw, null, builtAt), DowntimeReport.Build(episodes, Warsaw, null, later), "później");
    }

    [Fact]
    public void DependsOnNow_TrueWhenEpisodeEndsAtOrBeyondNowPlusTolerance_ResultChangesWithNow()
    {
        var now = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.FromHours(2));
        var far = new DowntimeEpisode("a1", "Aidite 1", AlertsIdle, now.AddHours(-1), now.AddDays(5), null, EpisodeId: Guid.NewGuid(),
            Category: DowntimeCategories.Idle);
        Assert.True(DowntimeReport.DependsOnNow(far.End, now));
        Assert.True(DowntimeReport.DependsOnNow(now + DowntimeReport.FutureTolerance, now)); // równo granicy: przycięcie zmienia zapis wartości
        Assert.False(DowntimeReport.DependsOnNow(now + DowntimeReport.FutureTolerance - TimeSpan.FromTicks(1), now));
        Assert.False(DowntimeReport.DependsOnNow(DateTimeOffset.MinValue, now)); // pusty dziennik
        Assert.True(DowntimeReport.DependsOnNow(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue.AddHours(-1))); // bez przepełnienia
        var first = DowntimeReport.Build([far], Warsaw, null, now);
        var second = DowntimeReport.Build([far], Warsaw, null, now.AddMinutes(1));
        Assert.NotEqual(first.Sum(s => s.Minutes), second.Sum(s => s.Minutes));
    }
}
