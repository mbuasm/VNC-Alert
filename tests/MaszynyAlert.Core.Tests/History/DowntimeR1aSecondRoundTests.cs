using System.Diagnostics;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeR1aSecondRoundTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);
    private static readonly DateTimeOffset Start = At("2026-09-15T08:00:00Z");
    private static readonly Guid Id = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static DowntimeEpisode Make(int observedMinutes, string? category, Guid? id = null, int startMinutes = 0,
        string machine = "aidite1", string reason = "Servo 6") =>
        new(machine, "Aidite 1", reason, Start.AddMinutes(startMinutes), Start.AddMinutes(observedMinutes), null,
            EpisodeId: id ?? Id, Origin: "PC-A", Category: category);

    private static string[] Lines(string path) => File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();

    private static DowntimeCategoryClassifier Classifier() => new(TestPaths.ExampleRules, TestPaths.ExampleConfig.Machines);

    // --- Normalize ---

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("\t\n", null)]
    [InlineData("bez alarmu", null)]
    [InlineData("BEZ ALARMU", null)]
    [InlineData(" bez alarmu ", null)]
    [InlineData("nie błąd", null)]
    [InlineData("Nie Błąd", null)]
    [InlineData("NIE BŁĄD", null)]
    [InlineData(" tacka ", "tacka")]
    [InlineData("restart", "restart")]
    [InlineData("Restart", "Restart")]
    [InlineData("nierozpoznany", "nierozpoznany")]
    [InlineData("bez alarmu!", "bez alarmu!")]
    public void Normalize(string? input, string? expected)
    {
        Assert.Equal(expected, DowntimeCategories.Normalize(input));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData(" tacka ")]
    [InlineData("bez alarmu")]
    [InlineData("")]
    public void Normalize_JestIdempotentne(string input)
    {
        string? once = DowntimeCategories.Normalize(input);
        Assert.Equal(once, DowntimeCategories.Normalize(once));
    }

    // --- Record ---

    private static Alert Alarm(string category, string shortText = "Tacka nie pobrana") => new()
    {
        Key = AlertKey.ForRule("aidite1", "tray-grab"), MachineId = "aidite1", MachineName = "Aidite 1",
        ShortText = shortText, Category = category, Kind = AlertKind.Error, Status = AlertStatus.Closed,
        StartedAt = Start, LastSeenAt = Start.AddMinutes(10), EpisodeId = Id,
    };

    [Theory]
    [InlineData("bez alarmu")]
    [InlineData("nie błąd")]
    [InlineData(" BEZ ALARMU ")]
    public void Record_KategoriaBezAlarmuLubNieBlad_EpizodBezKategoriiRaportZTekstu(string category)
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);

        journal.Record(Alarm(category));

        var episode = Assert.Single(journal.Snapshot());
        Assert.Null(episode.Category);
        Assert.DoesNotContain("Category", Assert.Single(Lines(path)), StringComparison.Ordinal);
        var row = Assert.Single(DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc, classifier: Classifier()));
        Assert.Equal("tacka", row.Category);
    }

    [Fact]
    public void Record_KategoriaZeSpacjami_Przycieta()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        journal.Record(Alarm(" restart "));
        Assert.Equal("restart", Assert.Single(journal.Snapshot()).Category);
        Assert.Contains("\"Category\":\"restart\"", Assert.Single(Lines(path)), StringComparison.Ordinal);
    }

    // --- Wczytanie pliku ---

    private static string Line(Guid id, string? categoryJson) =>
        "{\"MachineId\":\"aidite1\",\"MachineName\":\"Aidite 1\",\"Reason\":\"Servo 6\","
        + "\"StartedAt\":\"2026-09-15T08:00:00+00:00\",\"LastObservedAt\":\"2026-09-15T08:10:00+00:00\","
        + "\"EndedAt\":\"2026-09-15T08:10:00+00:00\",\"Interrupted\":false,\"EpisodeId\":\"" + id + "\",\"Origin\":\"PC-A\""
        + (categoryJson is null ? "" : ",\"Category\":" + categoryJson) + "}";

    [Fact]
    public void Plik_KategoriePuste_BezAlarmu_ZeSpacjami_NormalizowanePrzyWczytaniu()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid();
        File.WriteAllLines(path,
        [
            Line(a, "\"bez alarmu\""), Line(b, "\"  \""), Line(c, "\" tacka \""), Line(d, "null"), Line(e, "\"Nie Błąd\""),
        ]);

        var journal = new DowntimeJournal(path);

        Assert.Null(journal.Error);
        var byId = journal.Snapshot().ToDictionary(x => x.EpisodeId);
        Assert.Null(byId[a].Category);
        Assert.Null(byId[b].Category);
        Assert.Equal("tacka", byId[c].Category);
        Assert.Null(byId[d].Category);
        Assert.Null(byId[e].Category);
    }

    [Fact]
    public void Plik_NormalizacjaPrzyWczytaniu_NieDopisujeWierszyAPonownyWpisNiczegoNieZmienia()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var id = Guid.NewGuid();
        File.WriteAllLines(path, [Line(id, "\" tacka \"")]);
        byte[] before = File.ReadAllBytes(path);

        var journal = new DowntimeJournal(path);
        Assert.Equal(before, File.ReadAllBytes(path));

        // Ten sam epizod z sieci z kategorią po normalizacji i tą samą obserwacją — bez zmian, bez zapisu.
        var same = journal.Snapshot()[0];
        Assert.False(journal.Merge(same with { Category = "tacka" }));
        Assert.False(journal.Merge(same with { Category = " tacka " }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // --- Apply / Merge ---

    [Fact]
    public void Merge_ZnanaKategoriaNiePrzegrywaZBezAlarmuANowszaPrzycietaWygrywa()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        journal.Apply([Make(5, "restart")]);

        journal.Apply([Make(6, "bez alarmu")]);
        Assert.Equal("restart", journal.Snapshot()[0].Category);
        journal.Merge(Make(7, "NIE BŁĄD"));
        Assert.Equal("restart", journal.Snapshot()[0].Category);

        journal.Apply([Make(8, " narzedzie ")]);
        Assert.Equal("narzedzie", journal.Snapshot()[0].Category);
        Assert.All(Lines(path), l => Assert.DoesNotContain("bez alarmu", l, StringComparison.OrdinalIgnoreCase));
        Assert.All(Lines(path), l => Assert.DoesNotContain("\" narzedzie", l, StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ZmianaZeSpacjiNaPrzycieta_BezZapisu()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        journal.Merge(Make(5, " tacka "));
        Assert.Equal("tacka", journal.Snapshot()[0].Category);
        int lines = Lines(path).Length;

        Assert.False(journal.Merge(Make(5, "tacka")));
        Assert.False(journal.Apply([Make(5, "\ttacka ")]));
        Assert.Equal(lines, Lines(path).Length);
    }

    [Fact]
    public void Apply_BezAlarmuNaNowymEpizodzie_ZapisanyBezKategorii()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        Assert.True(journal.Apply([Make(5, "bez alarmu")]));
        Assert.Null(journal.Snapshot()[0].Category);
        Assert.DoesNotContain("Category", Assert.Single(Lines(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_KategoriaZeSpacjamiPonad100ZnakowPoPrzycieciu_NadalOdrzucana()
    {
        // IsValid liczy długość przed normalizacją — wpis z 101 znakami (także po przycięciu) nie wchodzi.
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        Assert.False(journal.Apply([Make(5, " " + new string('k', 101) + " ")]));
        Assert.Empty(journal.Snapshot());
    }

    // --- MergeOverlaps / Combine ---

    [Fact]
    public void MergeOverlaps_BezAlarmuNieWygrywaZeZnana_ObiePusteDajaNull()
    {
        var older = Make(10, "tacka", Guid.NewGuid());
        var newer = Make(12, "bez alarmu", Guid.NewGuid(), startMinutes: 2);
        Assert.Equal("tacka", Assert.Single(DowntimeReport.MergeOverlaps([older, newer])).Category);

        var emptyA = Make(10, "  ", Guid.NewGuid());
        var emptyB = Make(12, "nie błąd", Guid.NewGuid(), startMinutes: 2);
        Assert.Null(Assert.Single(DowntimeReport.MergeOverlaps([emptyA, emptyB])).Category);

        var trimmed = Make(12, " restart ", Guid.NewGuid(), startMinutes: 2);
        Assert.Equal("restart", Assert.Single(DowntimeReport.MergeOverlaps([older, trimmed])).Category);
    }

    // --- CategoryOf, Build ---

    [Theory]
    [InlineData("bez alarmu", "Servo 6", "restart")]
    [InlineData("nie błąd", "Tacka nie pobrana", "tacka")]
    [InlineData(" tacka ", "Servo 6", "tacka")]
    [InlineData("bez alarmu", "Coś spoza słownika", "nierozpoznany")]
    public void CategoryOf_KategoriaNieBledu_KlasyfikujeTekst(string category, string reason, string expected)
    {
        Assert.Equal(expected, Classifier().CategoryOf(Make(5, category, reason: reason)));
    }

    [Theory]
    [InlineData("bez alarmu", "nierozpoznany")]
    [InlineData("nie błąd", "nierozpoznany")]
    [InlineData(" tacka ", "tacka")]
    [InlineData("", "nierozpoznany")]
    public void Build_BezKlasyfikatora_NormalizujeKategorie(string category, string expected)
    {
        var row = Assert.Single(DowntimeReport.Build([Make(10, category) with { EndedAt = Start.AddMinutes(10) }], TimeZoneInfo.Utc));
        Assert.Equal(expected, row.Category);
    }

    // --- NextInstant i podział na zmiany przy zmianie czasu ---

    private static TimeZoneInfo FallBackAt0630() => TestZones.Transitions("test-cofniecie-0630", TimeSpan.FromHours(1), TimeSpan.FromHours(1),
        new DateOnly(2026, 3, 25), new TimeOnly(2, 0), new DateOnly(2026, 10, 28), new TimeOnly(6, 30));

    [Fact]
    public void NextInstant_KursorWDrugimWystapieniu_GranicaPozniejszaNizKursor()
    {
        var zone = FallBackAt0630();
        var wall = new DateTime(2026, 10, 28, 6, 0, 0);
        var firstSix = At("2026-10-28T06:00:00+02:00");
        var secondSix = At("2026-10-28T06:00:00+01:00");

        // Kursor w PIERWSZYM 05:45 (czas letni) → pierwsze 06:00.
        Assert.Equal(firstSix, ReportCalendar.NextInstant(wall, zone, At("2026-10-28T05:45:00+02:00")));
        // Kursor w DRUGIM 05:45 (czas standardowy, po cofnięciu) → drugie 06:00, nie granica w przeszłości.
        Assert.Equal(secondSix, ReportCalendar.NextInstant(wall, zone, At("2026-10-28T05:45:00+01:00")));
        // Kursor dokładnie na pierwszym 06:00 → drugie.
        Assert.Equal(secondSix, ReportCalendar.NextInstant(wall, zone, firstSix));
        // Kursor po obu → zabezpieczenie: kursor + 1 h (zawsze do przodu).
        Assert.Equal(secondSix.AddHours(1), ReportCalendar.NextInstant(wall, zone, secondSix));
    }

    [Fact]
    public void NextInstant_GodzinaZwyklaIPrzeskok_TakJakKonwersjaStrefy()
    {
        var warsaw = TestZones.Warsaw;
        var after = At("2026-09-15T00:00:00Z");
        Assert.Equal(At("2026-09-15T06:00:00+02:00"), ReportCalendar.NextInstant(new DateTime(2026, 9, 15, 6, 0, 0), warsaw, after));
        Assert.Equal(At("2026-03-29T03:00:00+02:00"),
            ReportCalendar.NextInstant(new DateTime(2026, 3, 29, 2, 30, 0), warsaw, At("2026-03-28T00:00:00Z")));
        // DateTimeKind.Utc/Local na wejściu nie zmienia znaczenia zegara ściennego.
        Assert.Equal(At("2026-09-15T06:00:00+02:00"),
            ReportCalendar.NextInstant(DateTime.SpecifyKind(new DateTime(2026, 9, 15, 6, 0, 0), DateTimeKind.Utc), warsaw, after));
    }

    /// <summary>
    /// Wspólne sprawdzenie kawałków <see cref="DowntimeReport.Build"/>: dodatnie, rosnące, ciągłe w obrębie czasu raportowanego,
    /// suma = <see cref="ReportCalendar.ReportedDuration"/>, etykieta zmiany i dzień zgodne z zegarem ściennym początku kawałka.
    /// </summary>
    private static IReadOnlyList<DowntimeSlice> AssertSlicesSound(TimeZoneInfo zone, DateTimeOffset start, DateTimeOffset end)
    {
        IReadOnlyList<DowntimeSlice>? rows = null;
        var work = Task.Run(() => rows = DowntimeReport.Build(
            [new DowntimeEpisode("a", "A", "Błąd", start, end, end, EpisodeId: Guid.NewGuid())], zone));
        Assert.True(work.Wait(TimeSpan.FromSeconds(10)), "Build się zapętlił.");
        Assert.Null(work.Exception);

        Assert.All(rows!, r => Assert.True(r.Start < r.End, $"kawałek {r.Start:o}–{r.End:o} nie jest dodatni"));
        for (int i = 1; i < rows!.Count; i++) Assert.True(rows[i - 1].End <= rows[i].Start, "kawałki nachodzą na siebie");
        var expected = ReportCalendar.ReportedDuration(start, end, zone);
        Assert.Equal(expected.TotalMinutes, rows.Sum(r => r.Minutes), 6);
        // Kawałki pokrywają dokładnie przedziały raportowane (bez dziur).
        var parts = ReportCalendar.ReportedIntervals(start, end, zone);
        foreach (var (partStart, partEnd) in parts)
        {
            var inside = rows.Where(r => r.Start >= partStart && r.End <= partEnd).ToArray();
            Assert.Equal(partStart, inside[0].Start);
            Assert.Equal(partEnd, inside[^1].End);
            for (int i = 1; i < inside.Length; i++) Assert.Equal(inside[i - 1].End, inside[i].Start);
        }
        foreach (var r in rows)
        {
            var local = TimeZoneInfo.ConvertTime(r.Start, zone).DateTime;
            string shift = local.Hour is >= 6 and < 14 ? "06:00–14:00" : local.Hour is >= 14 and < 22 ? "14:00–22:00" : "22:00–06:00";
            Assert.Equal(shift, r.Shift);
            Assert.Equal(DateOnly.FromDateTime(local.Hour < 6 ? local.AddDays(-1) : local), r.Day);
        }
        return rows;
    }

    [Fact]
    public void Build_PrzezCofniecieZegaraZGranica0600WSrodku_PrawdziwyCzasBezUjemnychKawalkow()
    {
        var zone = FallBackAt0630();
        var rows = AssertSlicesSound(zone, At("2026-10-28T04:00:00+02:00"), At("2026-10-28T08:00:00+01:00"));
        Assert.Equal(300, rows.Sum(r => r.Minutes), 6); // 04:00 CEST → 08:00 CET = 5 h
        // Nocna zmiana kończy się na PIERWSZYM 06:00.
        Assert.Equal(At("2026-10-28T06:00:00+02:00"), rows[0].End);
    }

    public static TheoryData<string> Strefy => new()
    {
        "przeskok-3h-o-05", "przeskok-13h-o-05", "cofniecie-o-14", "cofniecie-o-22", "cofniecie-0630", "przeskok-1h-o-06-sobota",
        "przeskok-3h-o-05-sobota",
    };

    private static TimeZoneInfo Zone(string key) => key switch
    {
        "przeskok-3h-o-05" => TestZones.Transitions(key, TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            new DateOnly(2026, 3, 25), new TimeOnly(5, 0), new DateOnly(2026, 10, 28), new TimeOnly(8, 0)),
        "przeskok-13h-o-05" => TestZones.Transitions(key, TimeSpan.Zero, TimeSpan.FromHours(13),
            new DateOnly(2026, 3, 25), new TimeOnly(5, 0), new DateOnly(2026, 10, 28), new TimeOnly(18, 0)),
        "cofniecie-o-14" => TestZones.Transitions(key, TimeSpan.FromHours(1), TimeSpan.FromHours(1),
            new DateOnly(2026, 3, 25), new TimeOnly(14, 0), new DateOnly(2026, 10, 28), new TimeOnly(15, 0)),
        "cofniecie-o-22" => TestZones.Transitions(key, TimeSpan.FromHours(1), TimeSpan.FromHours(1),
            new DateOnly(2026, 3, 25), new TimeOnly(22, 0), new DateOnly(2026, 10, 28), new TimeOnly(23, 0)),
        "cofniecie-0630" => FallBackAt0630(),
        "przeskok-1h-o-06-sobota" => TestZones.Transitions(key, TimeSpan.FromHours(1), TimeSpan.FromHours(1),
            new DateOnly(2026, 3, 28), new TimeOnly(6, 0), new DateOnly(2026, 10, 31), new TimeOnly(7, 0)),
        "przeskok-3h-o-05-sobota" => TestZones.Transitions(key, TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            new DateOnly(2026, 3, 28), new TimeOnly(5, 0), new DateOnly(2026, 11, 30), new TimeOnly(8, 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Theory]
    [MemberData(nameof(Strefy))]
    public void Build_WiosnaIJesien_KawalkiCiagleISumaRownaCzasowiRaportowanemu(string key)
    {
        var zone = Zone(key);
        AssertSlicesSound(zone, At("2026-03-23T12:00:00Z"), At("2026-04-01T12:00:00Z"));
        AssertSlicesSound(zone, At("2026-10-26T12:00:00Z"), At("2026-11-04T12:00:00Z"));
    }

    [Theory]
    [InlineData("przeskok-3h-o-05")]
    [InlineData("przeskok-13h-o-05")]
    [InlineData("cofniecie-o-14")]
    [InlineData("cofniecie-o-22")]
    [InlineData("cofniecie-0630")]
    public void ReportedDuration_DzienRoboczyZPrzeskokiem_PrawdziwyUplyw(string key)
    {
        // Wtorek 12:00 UTC → czwartek 12:00 UTC: w całości dni robocze, czas raportowany = 48 h prawdziwego czasu.
        var zone = Zone(key);
        Assert.Equal(TimeSpan.FromHours(48), ReportCalendar.ReportedDuration(At("2026-03-24T12:00:00Z"), At("2026-03-26T12:00:00Z"), zone));
        Assert.Equal(TimeSpan.FromHours(48), ReportCalendar.ReportedDuration(At("2026-10-27T12:00:00Z"), At("2026-10-29T12:00:00Z"), zone));
    }

    [Theory]
    [InlineData("przeskok-3h-o-05")]
    [InlineData("przeskok-13h-o-05")]
    [InlineData("cofniecie-o-14")]
    [InlineData("cofniecie-o-22")]
    [InlineData("przeskok-1h-o-06-sobota")]
    [InlineData("przeskok-3h-o-05-sobota")]
    public void IsReportedTimeZgodneZReportedIntervals_NoweStrefy(string key)
    {
        var zone = Zone(key);
        foreach (var (from, to) in new[] { (At("2026-03-20T00:00:00Z"), At("2026-04-03T00:00:00Z")),
                                          (At("2026-10-23T00:00:00Z"), At("2026-11-06T00:00:00Z")) })
        {
            var parts = ReportCalendar.ReportedIntervals(from, to, zone);
            for (var t = from; t < to; t = t.AddMinutes(5))
            {
                bool inPart = parts.Any(p => p.Start <= t && t < p.End);
                Assert.True(inPart == ReportCalendar.IsReportedTime(t, zone),
                    $"{key}: {t:u} lokalnie {TimeZoneInfo.ConvertTime(t, zone):yyyy-MM-dd HH:mm zzz}: IsReportedTime={!inPart}, ReportedIntervals={inPart}");
            }
        }
    }

    // --- Wydajność i dane skrajne ---

    [Fact]
    public void Build_Epizod30Dni_OczekiwanaLiczbaKawalkowSzybko()
    {
        var start = At("2026-09-01T08:00:00Z");
        var end = At("2026-10-01T08:00:00Z");
        var watch = Stopwatch.StartNew();
        var rows = DowntimeReport.Build([new DowntimeEpisode("a", "A", "Błąd", start, end, end, EpisodeId: Guid.NewGuid())], TestZones.Warsaw);
        var parts = ReportCalendar.ReportedIntervals(start, end, TestZones.Warsaw);
        watch.Stop();

        // Oczekiwana liczba kawałków liczona niezależnie: różne (dzień zmian, zmiana) w czasie raportowanym co 15 minut.
        var expected = new HashSet<(DateOnly, int)>();
        for (var t = start; t < end; t = t.AddMinutes(15))
        {
            if (!ReportCalendar.IsReportedTime(t, TestZones.Warsaw)) continue;
            var local = TimeZoneInfo.ConvertTime(t, TestZones.Warsaw).DateTime;
            expected.Add((DateOnly.FromDateTime(local.AddHours(-6)), local.Hour is >= 6 and < 14 ? 0 : local.Hour is >= 14 and < 22 ? 1 : 2));
        }
        Assert.Equal(expected.Count, rows.Count);
        Assert.Equal(5, parts.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Build + ReportedIntervals 30 dni: {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.All(rows, r => Assert.True(r.IsLong));
        Assert.Equal(1, DowntimeReport.Totals(rows).All.Episodes);
    }

    [Fact]
    public void Build_EpizodRok_Szybko()
    {
        var start = At("2026-01-05T08:00:00Z");
        var end = start.AddDays(365);
        var watch = Stopwatch.StartNew();
        var rows = DowntimeReport.Build([new DowntimeEpisode("a", "A", "Błąd", start, end, end, EpisodeId: Guid.NewGuid())], TestZones.Warsaw);
        watch.Stop();
        Assert.InRange(rows.Count, 3 * 5 * 52 - 10, 3 * 5 * 53 + 10);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void Build_EpizodPrzyKoncuZakresuDat_NieRzuca()
    {
        var start = new DateTimeOffset(9999, 12, 29, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(9999, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var episode = new DowntimeEpisode("a", "A", "Błąd", start, end, end, EpisodeId: Guid.NewGuid());
        Assert.True(episode.IsValid());

        var ex = Record.Exception(() => DowntimeReport.Build([episode], TimeZoneInfo.Utc));
        Assert.Null(ex);
    }
}
