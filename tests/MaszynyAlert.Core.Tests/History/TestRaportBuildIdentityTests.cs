using System.Collections.Concurrent;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class TestRaportBuildIdentityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // ── Wyrocznia: DowntimeReport.Build, słowo w słowo ───────────────────

    private static List<DowntimeSlice> ReferenceBuild(IEnumerable<DowntimeEpisode> episodes, TimeZoneInfo zone,
        DowntimeCategoryClassifier? classifier = null, DateTimeOffset? now = null)
    {
        var result = new List<DowntimeSlice>();
        var merged = DowntimeReport.MergeOverlaps(episodes);
        var errorsByMachine = merged.Where(e => !DowntimeReport.IsIdleEpisode(e))
            .ToLookup(ReportMachineKey.Of, e => (Start: e.StartedAt, End: e.End));
        foreach (var episode in merged)
        {
            IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> own = [(episode.StartedAt, SliceEnd(episode, now))];
            TimeSpan measured = episode.End - episode.StartedAt;
            if (DowntimeReport.IsIdleEpisode(episode) && errorsByMachine[ReportMachineKey.Of(episode)].ToArray() is { Length: > 0 } errors)
            {
                own = Subtract(own[0].Start, own[0].End, errors);
                measured = Subtract(episode.StartedAt, episode.End, errors)
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

    private static DateTimeOffset SliceEnd(DowntimeEpisode episode, DateTimeOffset? now)
    {
        DateTimeOffset limit = now is { } current
            ? (current > DateTimeOffset.MaxValue - DowntimeReport.FutureTolerance ? DateTimeOffset.MaxValue : current + DowntimeReport.FutureTolerance)
            : (episode.StartedAt > DateTimeOffset.MaxValue - DowntimeReport.MaxSlicedSpan ? DateTimeOffset.MaxValue : episode.StartedAt + DowntimeReport.MaxSlicedSpan);
        return episode.End < limit ? episode.End : limit;
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Subtract(DateTimeOffset start, DateTimeOffset end,
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

    // ── Porównanie co do zapisu (czasy w „O” — z przesunięciem strefy, minuty w „R”) ───────────────────────

    private static void AssertSameRows(IReadOnlyList<DowntimeSlice> expected, IReadOnlyList<DowntimeSlice> actual, string context)
    {
        string[] a = [.. expected.Select(TestRaportScenarios.Canonical)];
        string[] b = [.. actual.Select(TestRaportScenarios.Canonical)];
        if (a.SequenceEqual(b)) return;
        int n = Math.Min(a.Length, b.Length);
        int first = Enumerable.Range(0, n).FirstOrDefault(i => a[i] != b[i], n);
        Assert.Fail($"{context}: wierszy {a.Length} → {b.Length}; pierwsza różnica na pozycji {first}:\n"
            + $"  oczekiwane: {(first < a.Length ? a[first] : "-")}\n  jest:       {(first < b.Length ? b[first] : "-")}");
    }

    [Fact]
    public void Build_WlasnyGenerator_TenSamWynikCoPrzedNaprawa()
    {
        var random = new Random(1_210_202_608);
        int rows = 0, idleCut = 0, ties = 0, withoutId = 0;
        for (int i = 0; i < 4000; i++)
        {
            var scenario = TestRaportScenarios.Next(random, i);
            var expected = ReferenceBuild(scenario.Episodes, scenario.Zone, scenario.Classifier(), scenario.Now);
            var actual = DowntimeReport.Build(scenario.Episodes, scenario.Zone, scenario.Classifier(), scenario.Now);
            AssertSameRows(expected, actual, scenario.Describe());
            rows += expected.Count;
            var merged = DowntimeReport.MergeOverlaps(scenario.Episodes);
            var errors = merged.Where(e => !DowntimeReport.IsIdleEpisode(e)).ToArray();
            idleCut += merged.Count(e => DowntimeReport.IsIdleEpisode(e)
                && errors.Any(o => ReportMachineKey.Of(o) == ReportMachineKey.Of(e) && o.End > e.StartedAt && o.StartedAt < e.End));
            ties += errors.GroupBy(e => (ReportMachineKey.Of(e), e.StartedAt.UtcDateTime)).Count(g => g.Count() > 1);
            withoutId += scenario.Episodes.Count(e => string.IsNullOrEmpty(e.MachineId));
        }
        // Dane naprawdę ćwiczą odejmowanie błędów od postojów bez pracy, remisy początku i wpisy bez identyfikatora maszyny.
        output.WriteLine($"wierszy {rows}, postojów pod błędem {idleCut}, remisów początku błędów {ties}, wpisów bez MachineId {withoutId}");
        Assert.True(rows > 40_000, $"wierszy {rows}");
        Assert.True(idleCut > 3_000, $"postojów bez pracy nakładających się na błąd tej samej maszyny: {idleCut}");
        Assert.True(ties > 100, $"błędów tej samej maszyny o tym samym początku (po scaleniu): {ties}");
        Assert.True(withoutId > 10_000, $"wpisów bez MachineId: {withoutId}");
    }

    /// <summary>
    /// Błąd obejmujący WCZEŚNIEJ zaczęty długi przedział, a po nim krótkie — przedział z największym końcem nie jest ostatnim
    /// z kończących się przed postojem (wyszukiwanie binarne po samym końcu byłoby złe; najdalszy koniec prefiksu — dobre).
    /// Wejście scalone ręcznie nie powstaje (MergeOverlaps łączy nakładające się błędy), ale ujemne długości z uszkodzonego pliku tak.
    /// </summary>
    [Fact]
    public void Build_BladObejmujacyIUjemneDlugosci_TenSamWynikCoPrzedNaprawa()
    {
        var day = new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.FromHours(2));
        DowntimeEpisode Error(int from, int to) => new("m1", "Maszyna 1", "Servo 6", day.AddMinutes(from), day.AddMinutes(to),
            day.AddMinutes(to), EpisodeId: Guid.NewGuid(), Category: "restart");
        DowntimeEpisode Idle(int from, int to) => new("m1", "Maszyna 1", TestRaportScenarios.IdleText, day.AddMinutes(from),
            day.AddMinutes(to), day.AddMinutes(to), EpisodeId: Guid.NewGuid(), Category: DowntimeCategories.Idle);
        List<DowntimeEpisode>[] cases =
        [
            [Error(0, 100), Error(10, 20), Error(30, 40), Idle(50, 60)], // błąd obejmujący — postój cały pod błędem
            [Error(0, 55), Error(10, 20), Idle(50, 60)], // obejmujący kończy się w trakcie postoju
            [Error(40, 5), Error(20, 3), Idle(4, 45)], // ujemne długości (uszkodzony plik) — nie scalają się
            [Error(10, 10), Idle(0, 30)], // błąd zerowej długości dzieli postój na dwie części
            [Error(0, 30), Error(0, 30), Error(0, 10), Idle(0, 30), Idle(0, 30)], // identyczne granice
            [Error(30, 60), Idle(0, 30), Idle(60, 90)], // styki z obu stron
            // Po scaleniu: [0, 50) i [60, 40) (ujemna długość, uszkodzony wiersz) — końce NIE rosną z początkiem. Wyszukiwanie binarne po
            // samym końcu pominęłoby [0, 50) dla postoju [45, 70); najdalszy koniec prefiksu — nie. Oczekiwane: postój (50, 70) = 20 min.
            [Error(0, 50), Error(60, 40), Idle(45, 70)],
        ];
        foreach (var (episodes, index) in cases.Select((c, i) => (c, i)))
            foreach (DateTimeOffset? now in new DateTimeOffset?[] { day.AddHours(3), day.AddMinutes(45) - DowntimeReport.FutureTolerance, null })
                AssertSameRows(ReferenceBuild(episodes, TestZones.Warsaw, null, now), DowntimeReport.Build(episodes, TestZones.Warsaw, null, now),
                    $"przypadek {index}, teraz {now:O}");
        // Jawna wartość dla pierwszego: postój bez pracy w całości pod błędem → poza raportem (zostaje tylko błąd 100 min).
        var first = DowntimeReport.Build(cases[0], TestZones.Warsaw, null, day.AddHours(3));
        Assert.DoesNotContain(first, s => s.IsIdle);
        // Czwarty: postój 30 min przecięty błędem zerowej długości — dwa wiersze po 10 i 20 min.
        var fourth = DowntimeReport.Build(cases[3], TestZones.Warsaw, null, day.AddHours(3)).Where(s => s.IsIdle).ToArray();
        Assert.Equal([10.0, 20.0], fourth.Select(s => Math.Round(s.Minutes, 6)));
        // Siódmy: końce błędów po scaleniu nie są monotoniczne — z postoju 25 min zostaje 20 min po końcu błędu [0, 50).
        var seventh = DowntimeReport.Build(cases[6], TestZones.Warsaw, null, day.AddHours(3)).Where(s => s.IsIdle).ToArray();
        Assert.Equal([20.0], seventh.Select(s => Math.Round(s.Minutes, 6)));
        Assert.Equal(day.AddMinutes(50), seventh[0].Start);
    }

    // ── Kontrakt DependsOnNow, na którym Panel opiera brak przeliczania ─────────────────────────────────────────

    [Fact]
    public void DependsOnNow_FalszywyWObuChwilach_WynikIdentycznyCoDoZapisu()
    {
        var random = new Random(1_210_202_609);
        int asserted = 0, backwards = 0, acrossShift = 0, dependentDiffers = 0;
        for (int i = 0; i < 3000; i++)
        {
            var scenario = TestRaportScenarios.Next(random, i);
            var latestEnd = scenario.Episodes.Count == 0 ? DateTimeOffset.MinValue : scenario.Episodes.Max(e => e.End);
            bool ordinaryEnd = latestEnd.Year is > 1 and < 9998;
            var built = random.Next(3) switch
            {
                1 when ordinaryEnd => latestEnd - DowntimeReport.FutureTolerance + TimeSpan.FromTicks(1),
                2 when ordinaryEnd => latestEnd + TimeSpan.FromMinutes(random.Next(0, 3 * 24 * 60)),
                _ => scenario.Now ?? scenario.Origin,
            };
            if (built.Year >= 9998) continue; // „teraz” przy końcu kalendarza — bez arytmetyki dat w teście (Build sprawdza wyrocznia wyżej)
            DateTimeOffset[] checks =
            [
                built + TimeSpan.FromTicks(1), built + TimeSpan.FromHours(random.Next(1, 30)), built + TimeSpan.FromDays(random.Next(1, 400)),
                built - TimeSpan.FromMinutes(random.Next(1, 120)), // zegar cofnięty (NTP)
                latestEnd > DateTimeOffset.MinValue + DowntimeReport.FutureTolerance && latestEnd.Year < 9999
                    ? latestEnd - DowntimeReport.FutureTolerance + TimeSpan.FromTicks(random.Next(-1, 2)) : built,
            ];
            var atBuild = DowntimeReport.Build(scenario.Episodes, scenario.Zone, scenario.Classifier(), built);
            foreach (var check in checks)
            {
                if (check.Year >= 9999) continue;
                var atCheck = DowntimeReport.Build(scenario.Episodes, scenario.Zone, scenario.Classifier(), check);
                bool reuse = !DowntimeReport.DependsOnNow(latestEnd, built) && !DowntimeReport.DependsOnNow(latestEnd, check);
                if (reuse)
                {
                    AssertSameRows(atCheck, atBuild, $"{scenario.Describe()} — policzone {built:O}, sprawdzone {check:O}");
                    asserted++;
                    if (check < built) backwards++;
                    if (ReportCalendar.LocalWall(check, scenario.Zone).Hour != ReportCalendar.LocalWall(built, scenario.Zone).Hour) acrossShift++;
                }
                else if (!atCheck.Select(TestRaportScenarios.Canonical).SequenceEqual(atBuild.Select(TestRaportScenarios.Canonical)))
                    dependentDiffers++;
            }
        }
        string counts = $"par sprawdzonych {asserted}, z cofniętym zegarem {backwards}, po zmianie godziny {acrossShift}, zależnych z innym wynikiem {dependentDiffers}";
        output.WriteLine(counts);
        Assert.True(asserted > 6_000, counts);
        Assert.True(backwards > 1_000, counts);
        Assert.True(acrossShift > 2_000, counts); // północ, 06:00, 14:00, 22:00 bez nowych danych
        // Sprawdzenie nie jest puste: tam, gdzie DependsOnNow mówi „zależy”, wynik naprawdę bywa inny.
        Assert.True(dependentDiffers > 50, counts);
    }

    // ── Kontrakt DowntimeJournal.Version ─────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.FromHours(2));

    /// <summary>
    /// Plik dziennika zablokowany przy starcie (odczyt nieudany, pamięć pusta, numer 0) — po zwolnieniu pierwsze scalenie wczytuje
    /// plik i numer MUSI się zmienić, inaczej Panel pokazywałby pusty raport mimo odzyskanej historii.
    /// </summary>
    [Fact]
    public void Version_OdczytPlikuPoNieudanymStarcie_ZmieniaNumerIMigawke()
    {
        using var dir = new TempDir("test-raport-version");
        string path = dir.File("downtime.jsonl");
        var writer = new DowntimeJournal(path);
        for (int i = 0; i < 5; i++)
            writer.Merge(new DowntimeEpisode("m1", "Maszyna 1", "Servo " + i, T0.AddMinutes(-30 * i), T0.AddMinutes(-30 * i + 10),
                T0.AddMinutes(-30 * i + 10), EpisodeId: Guid.NewGuid(), Category: "inny"));
        writer.Flush();

        DowntimeJournal journal;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal = new DowntimeJournal(path);
            Assert.NotNull(journal.Error);
            Assert.Empty(journal.Snapshot());
        }
        long before = journal.Version;
        var snapshotBefore = journal.Snapshot();
        Assert.Equal(before, journal.Version); // sama migawka nie wczytuje pliku ani nie zmienia numeru
        Assert.Empty(snapshotBefore);

        // Zamknięcie z sieci nieznanego epizodu: wczytuje odzyskany plik, ale niczego nie scala — numer zmienia SAM odczyt.
        Assert.False(journal.MergeClosure(Guid.NewGuid(), T0));
        long afterRead = journal.Version;
        Assert.True(afterRead > before, "odczyt pliku zmienił migawkę (0 → 5 epizodów), więc numer musi się zmienić");
        Assert.Equal(5, journal.Snapshot().Count);
        Assert.Null(journal.Error);
        Assert.True(journal.Merge(new DowntimeEpisode("m2", "Maszyna 2", "Servo 9", T0, T0.AddMinutes(1), null, EpisodeId: Guid.NewGuid(), Category: "inny")));
        Assert.True(journal.Version > afterRead);
        Assert.Equal(6, journal.Snapshot().Count);
    }

    /// <summary>
    /// Ten sam numer = ta sama migawka także przy równoległych zmianach (obserwacje co sekundę, zamknięcia, porcje z sieci):
    /// czytelnik jak Panel — numer, migawka, numer; para z niezmienionym numerem musi mieć zawsze tę samą migawkę.
    /// </summary>
    [Fact]
    public async Task Version_RownolegleScalenia_TenSamNumerToTaSamaMigawka()
    {
        using var dir = new TempDir("test-raport-version-race");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var seen = new ConcurrentDictionary<long, string>();
        int conflicts = 0, samples = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            var random = new Random(w);
            int tick = 0;
            while (!stop.IsCancellationRequested)
            {
                var id = ids[random.Next(ids.Length)];
                var observed = T0.AddSeconds(++tick);
                string machine = "m" + (Array.IndexOf(ids, id) % 3);
                switch (random.Next(20))
                {
                    case 0 or 1 or 2: journal.MergeClosure(id, observed); break;
                    case 3 or 4 or 5:
                        journal.Apply([new DowntimeEpisode(machine, "Maszyna", "Servo", T0, observed, null, EpisodeId: id, Category: "inny")]);
                        break;
                    default:
                        // Jak obserwacja otwartego alertu co sekundę: w pamięci zawsze zmiana, do pliku co 10 s.
                        journal.Merge(new DowntimeEpisode(machine, "Maszyna", "Servo", T0, observed, null, EpisodeId: id, Category: "inny"));
                        break;
                }
                if (tick % 64 == 0) Thread.Sleep(1); // okna bez zmian dla czytelnika (jak tik Panelu między obserwacjami)
                else Thread.Yield();
            }
        })).ToArray();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long v1 = journal.Version;
                string snapshot = TestRaportScenarios.Hash(journal.Snapshot().Select(TestRaportScenarios.Canonical));
                long v2 = journal.Version;
                if (v1 != v2) continue;
                Interlocked.Increment(ref samples);
                if (seen.GetOrAdd(v1, snapshot) != snapshot) Interlocked.Increment(ref conflicts);
            }
        });
        await Task.WhenAll([.. writers, reader]);
        string counts = $"próbek {samples}, różnych numerów {seen.Count}, konfliktów {conflicts}";
        output.WriteLine(counts);
        Assert.True(conflicts == 0, counts);
        Assert.True(samples > 200, counts);
        Assert.True(seen.Count > 20, counts);
    }
}
