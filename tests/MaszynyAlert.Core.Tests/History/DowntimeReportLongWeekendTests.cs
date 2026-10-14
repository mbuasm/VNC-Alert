using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeReportLongWeekendTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);

    private static DowntimeEpisode Ep(string start, string end, string machine = "aidite2", Guid? id = null,
        string? category = null, string reason = "Servo 6") =>
        new(machine, "Nazwa " + machine, reason, At(start), At(end), At(end), EpisodeId: id ?? Guid.NewGuid(), Category: category);

    // --- Progi ---

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(180, true, false)]
    [InlineData(181, true, false)]
    [InlineData(3600, true, false)]
    [InlineData(3601, true, true)]
    public void ProgiTrzechMinutIGodziny(int seconds, bool reported, bool isLong)
    {
        var start = At("2026-09-15T08:00:00Z");
        var episode = new DowntimeEpisode("a", "A", "Błąd", start, start.AddSeconds(seconds), start.AddSeconds(seconds),
            EpisodeId: Guid.NewGuid());

        var rows = DowntimeReport.Build([episode], TimeZoneInfo.Utc);
        Assert.Equal(reported, rows.Count > 0);
        Assert.Equal(reported, DowntimeReport.IsReported(episode));
        Assert.Equal(isLong, DowntimeReport.IsLong(episode));
        Assert.All(rows, r => Assert.Equal(isLong, r.IsLong));
    }

    [Fact]
    public void DwaObserwatoryPo40MinutNaGranicyZmiany_KazdyKawalekJestDlugi()
    {
        // Scalony postój 13:30–14:50 = 80 minut. Kawałek z pierwszej zmiany ma tylko 30 minut, ale oznaczenie liczy się
        // z całego scalonego epizodu.
        var first = Ep("2026-09-15T13:30:00Z", "2026-09-15T14:10:00Z");
        var second = Ep("2026-09-15T14:00:00Z", "2026-09-15T14:50:00Z");

        var rows = DowntimeReport.Build([first, second], TimeZoneInfo.Utc);

        Assert.Equal(2, rows.Count);
        Assert.Equal(30, rows[0].Minutes);
        Assert.All(rows, r => Assert.True(r.IsLong));
        Assert.Single(rows.Select(r => r.EpisodeId).Distinct());
        Assert.Equal(first.EpisodeId, rows[0].EpisodeId);
        var totals = DowntimeReport.Totals(rows);
        Assert.Equal(1, totals.All.Episodes);
        Assert.Equal(0, totals.WithoutLong.Episodes);
    }

    // --- Sumy w dwóch wariantach ---

    [Fact]
    public void Totals_ZDlugimiIBezNich()
    {
        // Długi 70 min (13:30–14:40, dwa kawałki), zwykły 10 min tej samej maszyny, zwykły 5 min innej maszyny.
        var rows = DowntimeReport.Build(
        [
            Ep("2026-09-15T13:30:00Z", "2026-09-15T14:40:00Z"),
            Ep("2026-09-15T16:00:00Z", "2026-09-15T16:10:00Z"),
            Ep("2026-09-15T16:00:00Z", "2026-09-15T16:05:00Z", "aidite3"),
        ], TimeZoneInfo.Utc);

        Assert.Equal(4, rows.Count);
        var totals = DowntimeReport.Totals(rows);
        Assert.Equal(85, totals.All.Minutes, 6);
        Assert.Equal(3, totals.All.Episodes);
        Assert.Equal(15, totals.WithoutLong.Minutes, 6);
        Assert.Equal(2, totals.WithoutLong.Episodes);

        // Filtr zmiany PRZED Totals (tak jak w widoku): z długiego postoju zostaje kawałek 13:30–14:00.
        var morning = DowntimeReport.Totals(rows.Where(r => r.Shift == "06:00–14:00"));
        Assert.Equal(30, morning.All.Minutes, 6);
        Assert.Equal(1, morning.All.Episodes);
        Assert.Equal(new DowntimeTotal(0, 0), morning.WithoutLong);
    }

    [Fact]
    public void Totals_TenSamIdentyfikatorNaDwochMaszynach_DwaPostoje()
    {
        var id = Guid.NewGuid();
        var rows = DowntimeReport.Build(
            [Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", id: id), Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", "aidite3", id)],
            TimeZoneInfo.Utc);

        Assert.Equal(2, DowntimeReport.Totals(rows).All.Episodes);
    }

    [Fact]
    public void Totals_PustaLista_Zera()
    {
        var totals = DowntimeReport.Totals([]);
        Assert.Equal(new DowntimeTotals(new DowntimeTotal(0, 0), new DowntimeTotal(0, 0)), totals);
        Assert.Throws<ArgumentNullException>(() => DowntimeReport.Totals(null!));
    }

    [Fact]
    public void Totals_PostojPrzezTrzyZmianyLiczySieRaz()
    {
        var rows = DowntimeReport.Build([Ep("2026-09-15T13:00:00Z", "2026-09-15T22:30:00Z")], TimeZoneInfo.Utc);
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, DowntimeReport.Totals(rows).All.Episodes);
    }

    // --- Weekend ---

    [Fact]
    public void Weekend_EpizodPrzezSobote0600_ZostajeTylkoCzescZPiatkowejNocy()
    {
        var row = Assert.Single(DowntimeReport.Build([Ep("2026-09-19T05:55:00+02:00", "2026-09-19T06:05:00+02:00")], TestZones.Warsaw));

        Assert.Equal(5, row.Minutes, 6);
        Assert.Equal(new DateOnly(2026, 9, 18), row.Day);
        Assert.Equal("22:00–06:00", row.Shift);
        Assert.False(row.IsLong);
    }

    [Fact]
    public void Weekend_ProgLiczySieNaCalymEpizodzie_ZostajeJednaMinuta()
    {
        // 4 minuty (> 3), z czego w czasie raportowanym 1 minuta — wchodzi z tą minutą.
        var row = Assert.Single(DowntimeReport.Build([Ep("2026-09-19T05:59:00+02:00", "2026-09-19T06:03:00+02:00")], TestZones.Warsaw));
        Assert.Equal(1, row.Minutes, 6);
    }

    [Fact]
    public void Weekend_EpizodWCalosciWWeekendzie_NieMaWierszyISum()
    {
        var rows = DowntimeReport.Build(
            [Ep("2026-09-20T10:00:00+02:00", "2026-09-20T10:30:00+02:00"), Ep("2026-09-19T08:00:00+02:00", "2026-09-19T12:00:00+02:00")],
            TestZones.Warsaw);
        Assert.Empty(rows);
        Assert.Equal(0, DowntimeReport.Totals(rows).All.Episodes);
    }

    [Fact]
    public void Weekend_DlugiPostojOdPiatkuDoPoniedzialku_DwaKawalkiJedenPostoj()
    {
        var rows = DowntimeReport.Build([Ep("2026-09-18T23:00:00+02:00", "2026-09-21T08:00:00+02:00")], TestZones.Warsaw);

        Assert.Equal(2, rows.Count);
        Assert.Equal((At("2026-09-18T23:00:00+02:00"), At("2026-09-19T06:00:00+02:00")), (rows[0].Start, rows[0].End));
        Assert.Equal((At("2026-09-21T06:00:00+02:00"), At("2026-09-21T08:00:00+02:00")), (rows[1].Start, rows[1].End));
        Assert.Equal(new DateOnly(2026, 9, 18), rows[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 21), rows[1].Day);
        Assert.All(rows, r => Assert.True(r.IsLong));
        var totals = DowntimeReport.Totals(rows);
        Assert.Equal(1, totals.All.Episodes);
        Assert.Equal(540, totals.All.Minutes, 6);
    }

    [Fact]
    public void Weekend_MinutaWPiatekIMinutaWPoniedzialek_DlugiPostoj()
    {
        // oznaczenie „długi” liczy się z CAŁEGO czasu, także weekendu.
        var rows = DowntimeReport.Build([Ep("2026-09-19T05:59:00+02:00", "2026-09-21T06:01:00+02:00")], TestZones.Warsaw);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Sum(r => r.Minutes), 6);
        Assert.All(rows, r => Assert.True(r.IsLong));
    }

    [Fact]
    public void Weekend_TrwajacyEpizodWchodzacyWSobote_PokazujeCzescDoGranicy()
    {
        var start = At("2026-09-19T05:00:00+02:00");
        var ongoing = new DowntimeEpisode("a", "A", "Błąd", start, start.AddMinutes(80), null, EpisodeId: Guid.NewGuid());
        var row = Assert.Single(DowntimeReport.Build([ongoing], TestZones.Warsaw));
        Assert.Equal(60, row.Minutes, 6);
        Assert.True(row.Ongoing);
        Assert.True(row.IsLong);
    }

    // --- Rodzaj błędu w kawałkach raportu ---

    [Fact]
    public void Build_BezKlasyfikatora_KategoriaZEpizoduAlboNierozpoznany()
    {
        var rows = DowntimeReport.Build(
        [
            Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", category: "tacka"),
            Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", "aidite3"),
            Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", "aidite4", category: "  "),
        ], TimeZoneInfo.Utc);

        Assert.Equal("tacka", Assert.Single(rows, r => r.MachineId == "aidite2").Category);
        Assert.Equal(DowntimeCategories.Unrecognized, Assert.Single(rows, r => r.MachineId == "aidite3").Category);
        Assert.Equal(DowntimeCategories.Unrecognized, Assert.Single(rows, r => r.MachineId == "aidite4").Category);
    }

    [Fact]
    public void Build_ZKlasyfikatorem_KlasyfikujeTylkoEpizodyBezKategorii()
    {
        var classifier = new DowntimeCategoryClassifier(TestPaths.ExampleRules, TestPaths.ExampleConfig.Machines);
        var rows = DowntimeReport.Build(
        [
            // Zapisana kategoria wygrywa z tekstem („Tacka nie pobrana” to w słowniku „tacka”).
            Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", "aidite1", category: "restart", reason: "Tacka nie pobrana"),
            Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", "aidite2", reason: "Tacka nie pobrana"),
            Ep("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", "aidite3", reason: "Coś spoza słownika"),
        ], TimeZoneInfo.Utc, classifier: classifier);

        Assert.Equal("restart", Assert.Single(rows, r => r.MachineId == "aidite1").Category);
        Assert.Equal("tacka", Assert.Single(rows, r => r.MachineId == "aidite2").Category);
        Assert.Equal(DowntimeCategories.Unrecognized, Assert.Single(rows, r => r.MachineId == "aidite3").Category);
    }

    [Fact]
    public void Build_KazdyKawalekNiesieMaszyneIdentyfikatorIKategorie()
    {
        var id = Guid.NewGuid();
        var rows = DowntimeReport.Build([Ep("2026-09-15T13:00:00Z", "2026-09-15T22:30:00Z", id: id, category: "narzedzie")], TimeZoneInfo.Utc);
        Assert.All(rows, r =>
        {
            Assert.Equal("aidite2", r.MachineId);
            Assert.Equal(id, r.EpisodeId);
            Assert.Equal("narzedzie", r.Category);
            Assert.True(r.IsLong);
        });
    }

    [Fact]
    public void Build_NieZmieniaPlikuDziennika()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        var start = At("2026-09-15T08:00:00Z");
        journal.Merge(new DowntimeEpisode("aidite1", "Aidite 1", "Tacka nie pobrana", start, start.AddMinutes(10), start.AddMinutes(10),
            EpisodeId: Guid.NewGuid()));
        journal.Merge(new DowntimeEpisode("aidite2", "Aidite 2", "Servo 6", start, start.AddMinutes(90), null, EpisodeId: Guid.NewGuid(),
            Category: "restart"));
        byte[] before = File.ReadAllBytes(path);

        var classifier = new DowntimeCategoryClassifier(TestPaths.ExampleRules, TestPaths.ExampleConfig.Machines);
        var rows = DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc, classifier);

        Assert.NotEmpty(rows);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.All(journal.Snapshot(), e => Assert.NotEqual("tacka", e.Category));
    }
}
