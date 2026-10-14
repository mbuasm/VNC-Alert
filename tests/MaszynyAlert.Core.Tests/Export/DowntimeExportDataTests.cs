using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Export;

public sealed class DowntimeExportDataTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
    private static readonly DateOnly Mon = new(2026, 9, 28);
    private static readonly DateOnly Tue = new(2026, 10, 6);

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso);

    private static DowntimeEpisode Ep(string machine, string reason, string start, double minutes, string? category = null,
        Guid? id = null, string? name = null)
    {
        var s = At(start);
        return new DowntimeEpisode(machine, name ?? machine.ToUpperInvariant(), reason, s, s.AddMinutes(minutes), s.AddMinutes(minutes),
            EpisodeId: id ?? Guid.NewGuid(), Category: category);
    }

    private static MachineConfig[] Machines() =>
    [
        new() { Id = "a1", Name = "Aidite 1", Host = "h1", Profile = "aidite" },
        new() { Id = "a2", Name = "Aidite 2", Host = "h2", Profile = "aidite" },
    ];

    /// <summary>Wiersze Panelu: Build + filtr okresu i zmiany jak w <c>DowntimeView.Refresh</c>.</summary>
    private static DowntimeSlice[] Panel(IEnumerable<DowntimeEpisode> episodes, DateOnly? from, DateOnly? to, string? shift = null,
        TimeZoneInfo? zone = null)
    {
        var f = from ?? DateOnly.MinValue;
        var t = to ?? DateOnly.MaxValue;
        return DowntimeReport.Build(episodes, zone ?? Utc, null, Now)
            .Where(s => s.Day >= f && s.Day <= t && (shift is null || s.Shift == shift)).ToArray();
    }

    private static DowntimeExportData Create(IEnumerable<DowntimeEpisode> episodes, DateOnly? from, DateOnly? to, string? shift = null,
        IReadOnlyList<ObservationInterval>? observations = null, TimeZoneInfo? zone = null, DateTimeOffset? now = null) =>
        DowntimeExportData.Create(Panel(episodes, from, to, shift, zone), from, to, shift, observations ?? [], Machines(),
            zone ?? Utc, now ?? Now, "TEST-PC", "Dane lokalne tego komputera");

    // ---- zakres i nazwa pliku ----

    [Fact]
    public void NazwaPliku_ZakresWybrany()
    {
        var data = Create([], Mon, Tue);
        Assert.Equal("raport-postojow-2026-09-28_2026-10-06.pdf", data.FileName("pdf"));
        Assert.Equal("raport-postojow-2026-09-28_2026-10-06.xlsx", data.FileName(".xlsx"));
        Assert.Equal("raport-postojow-2026-09-29_2026-10-06.pdf", DowntimeExportData.FileNameFor(new(2026, 9, 29), Tue, "pdf"));
        Assert.Throws<ArgumentNullException>(() => DowntimeExportData.FileNameFor(Mon, Tue, null!));
    }

    [Fact]
    public void CalaHistoria_OdNajstarszegoDniaDoDzis()
    {
        var data = Create([Ep("a1", "Servo 6", "2026-09-29T08:00:00Z", 10), Ep("a2", "Servo 6", "2026-10-01T08:00:00Z", 10)], null, null);
        Assert.Equal(new DateOnly(2026, 9, 29), data.From);
        Assert.Equal(Tue, data.To);
        Assert.Equal("raport-postojow-2026-09-29_2026-10-06.pdf", data.FileName("pdf"));
    }

    [Fact]
    public void CalaHistoria_BezPostojow_OdPierwszegoWpisuDziennika()
    {
        ObservationInterval[] observations = [new("a1", ObservationState.Observed, At("2026-10-01T03:00:00Z"), At("2026-10-02T08:00:00Z"))];
        var data = Create([], null, null, observations: observations);
        Assert.Equal(new DateOnly(2026, 9, 30), data.From);
        Assert.Equal(Tue, data.To);
    }

    [Fact]
    public void CalaHistoria_BezNiczego_DzisToDzis()
    {
        var data = Create([], null, null);
        Assert.Equal(Tue, data.From);
        Assert.Equal(Tue, data.To);
    }

    [Fact]
    public void Dzis_LiczonyOdGodziny6()
    {
        var data = Create([], null, null, now: At("2026-10-06T05:30:00Z"));
        Assert.Equal(new DateOnly(2026, 10, 5), data.To);
    }

    [Fact]
    public void OdWiekszeOdDo_DoRowneOd()
    {
        var data = Create([Ep("a1", "Servo 6", "2026-09-29T08:00:00Z", 10)], Tue, Mon);
        Assert.Equal(Tue, data.From);
        Assert.Equal(Tue, data.To);
        Assert.Empty(data.Episodes); // Panel pokazuje pustą tabelę z ostrzeżeniem
        Assert.Equal("raport-postojow-2026-10-06_2026-10-06.xlsx", data.FileName("xlsx"));
    }

    [Fact]
    public void TylkoOd_DoToDzis()
    {
        var data = Create([Ep("a1", "Servo 6", "2026-09-29T08:00:00Z", 10)], Mon, null);
        Assert.Equal(Mon, data.From);
        Assert.Equal(Tue, data.To);
    }

    [Theory]
    [InlineData(false, null, "Dane lokalne tego komputera")]
    [InlineData(false, "Hala A", "Dane lokalne tego komputera")]
    [InlineData(true, "Hala A", "Dane grupy komputerów „Hala A”")]
    [InlineData(true, "  Hala A  ", "Dane grupy komputerów „Hala A”")]
    [InlineData(true, "", "Dane grupy komputerów")]
    [InlineData(true, "   ", "Dane grupy komputerów")]
    [InlineData(true, null, "Dane grupy komputerów")]
    public void ZakresDanych(bool group, string? name, string expected) =>
        Assert.Equal(expected, DowntimeExportData.ScopeTextFor(group, name));

    [Fact]
    public void ZmianaWybrana_TekstZmiany()
    {
        Assert.Equal("Wszystkie zmiany", Create([], Mon, Tue).ShiftText);
        Assert.Equal("22:00–06:00", Create([], Mon, Tue, "22:00–06:00").ShiftText);
    }

    // ---- wiersze ----

    [Fact]
    public void Wiersze_ChronologicznieRosnaco_ZegarScienny_TackaJakoRamka()
    {
        var warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var data = Create(
        [
            Ep("a2", "Servo 6", "2026-10-01T08:00:00Z", 10, "restart"),
            Ep("a1", "Tacka nie pobrana", "2026-09-29T05:30:00Z", 10, "tacka"),
        ], Mon, Tue, zone: warsaw);

        Assert.Equal(["Ramka niepobrana", "Servo 6"], data.Episodes.Select(e => e.ErrorName));
        Assert.Equal(["Ramka", "Restart"], data.Episodes.Select(e => e.ErrorGroup));
        Assert.Equal(new DateTime(2026, 9, 29, 7, 30, 0), data.Episodes[0].Start);
        Assert.Equal(DateTimeKind.Unspecified, data.Episodes[0].Start.Kind);
        Assert.Equal("a1", data.Episodes[0].MachineId);
    }

    [Fact]
    public void PostojNaDwochZmianach_DwaWierszeJedenPostoj()
    {
        var data = Create([Ep("a1", "Servo 6", "2026-09-30T13:30:00Z", 60)], Mon, Tue);
        Assert.Equal(2, data.Episodes.Count);
        Assert.Single(data.Episodes.Select(e => e.EpisodeId).Distinct());
        Assert.Equal(1, data.Totals.All.Episodes);
        Assert.Equal(1, data.Machines.Single().Totals.All.Episodes);
        Assert.Equal(1, data.Errors.Single().Totals.All.Episodes);
        Assert.Equal(60, data.Totals.All.Minutes, 6);
    }

    [Fact]
    public void PostojPrzezPolnoc_DwaDniKalendarzowe_JedenDzienZmiany()
    {
        var data = Create([Ep("a1", "Servo 6", "2026-09-30T23:30:00Z", 60)], Mon, Tue);
        var row = Assert.Single(data.Episodes);
        Assert.Equal(new DateOnly(2026, 9, 30), row.Day);
        Assert.Equal(new DateOnly(2026, 9, 30), Assert.Single(data.Days).Day);
    }

    // ---- sumy = Totals i tabela Panelu ----

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(123)]
    [InlineData(2026)]
    public void Sumy_LosoweZestawy_ZgodneZTotalsIPanelem(int seed)
    {
        var random = new Random(seed);
        string[] reasons = ["Tacka nie pobrana", "Ramka niepobrana", "Servo 6", "Bazowanie (Home)", "Nr tacki = 0", "Błąd: X", "", "Wrzeciono"];
        string?[] categories = ["tacka", "restart", null, "inny", "narzedzie"];
        var episodes = Enumerable.Range(0, 120).Select(_ =>
        {
            var start = At("2026-09-28T00:00:00Z").AddMinutes(random.Next(0, 9 * 24 * 60));
            double minutes = random.Next(0, 8) == 0 ? random.Next(61, 600) : random.Next(1, 59) + random.NextDouble();
            return Ep("a" + random.Next(1, 5), reasons[random.Next(reasons.Length)], start.ToString("o"), minutes,
                categories[random.Next(categories.Length)]);
        }).ToArray();
        string? shift = random.Next(2) == 0 ? null : "14:00–22:00";
        var slices = Panel(episodes, Mon, Tue, shift);
        var data = DowntimeExportData.Create(slices, Mon, Tue, shift, [], Machines(), Utc, Now, "PC", "x");
        var totals = DowntimeReport.Totals(slices);

        Assert.Equal(totals, data.Totals);
        Assert.Equal(slices.Length, data.Episodes.Count);
        Assert.Equal(slices.Sum(s => s.Minutes), data.Episodes.Sum(e => e.Minutes), 6);
        // Według maszyny / nazwy / dnia: minuty sumują się do całości.
        Assert.Equal(totals.All.Minutes, data.Machines.Sum(m => m.Totals.All.Minutes), 6);
        Assert.Equal(totals.WithoutLong.Minutes, data.Machines.Sum(m => m.Totals.WithoutLong.Minutes), 6);
        Assert.Equal(totals.All.Episodes, data.Machines.Sum(m => m.Totals.All.Episodes));
        Assert.Equal(totals.All.Minutes, data.Errors.Sum(e => e.Totals.All.Minutes), 6);
        Assert.Equal(totals.All.Minutes, data.Days.Sum(d => d.Totals.All.Minutes), 6);
        foreach (var day in slices.GroupBy(s => s.Day))
            Assert.Equal(day.Sum(s => s.Minutes), data.Days.Single(d => d.Day == day.Key).Totals.All.Minutes, 6);
        Assert.Equal(totals.All.Episodes - totals.WithoutLong.Episodes, data.LongEpisodes);
        // „Tacka nie pobrana” i „Ramka niepobrana” to w eksporcie ta sama nazwa.
        Assert.Equal(data.Errors.Count, data.Errors.Select(e => e.ErrorName).Distinct().Count());
        Assert.DoesNotContain(data.Errors, e => e.ErrorName.Contains("ack", StringComparison.OrdinalIgnoreCase));
        // Wykres kołowy: suma wycinków (z „Pozostałe”) = całość.
        Assert.Equal(totals.All.Minutes, data.Charts.Errors.Slices.Sum(p => p.Value), 6);
        // Wykres dzienny: suma punktów = całość (wszystkie dni z postojami są dniami roboczymi zakresu).
        Assert.Equal(totals.All.Minutes, data.Charts.Days.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Equal(totals.WithoutLong.Minutes, data.Charts.Days.Series[1].Points.Sum(p => p.Value), 6);
    }

    [Fact]
    public void NazwaBleduWKilkuGrupach_NajczestszaZnana()
    {
        var data = Create(
        [
            Ep("a1", "Servo 6", "2026-09-29T08:00:00Z", 10),            // stary, nierozpoznany
            Ep("a1", "Servo 6", "2026-09-29T09:00:00Z", 10),            // stary, nierozpoznany
            Ep("a1", "Servo 6", "2026-09-29T10:00:00Z", 10, "restart"),
            Ep("a2", "Wrzeciono", "2026-09-29T10:00:00Z", 10),
        ], Mon, Tue);

        Assert.Equal("Restart", data.Errors.Single(e => e.ErrorName == "Servo 6").ErrorGroup);
        Assert.Equal("Nierozpoznany", data.Errors.Single(e => e.ErrorName == "Wrzeciono").ErrorGroup);
        Assert.Equal("Servo 6", data.Errors[0].ErrorName); // malejąco po minutach
    }

    // ---- „brak danych” ----

    [Fact]
    public void BrakPolaczenia_WDzienRoboczy_Liczony()
    {
        ObservationInterval[] observations = [new("a1", ObservationState.NoConnection, At("2026-09-30T08:00:00Z"), At("2026-09-30T10:00:00Z"))];
        var data = Create([], Mon, Tue, observations: observations);

        Assert.Equal(TimeSpan.FromHours(2), data.NoConnection);
        Assert.True(data.HasObservationJournal);
        var machine = Assert.Single(data.Machines);
        Assert.Equal("Aidite 1", machine.Name); // nazwa z konfiguracji
        Assert.Equal(TimeSpan.FromHours(2), machine.NoConnection);
        Assert.Equal(0, machine.Totals.All.Episodes);
    }

    [Fact]
    public void BrakPolaczenia_WWeekend_NieLiczony()
    {
        ObservationInterval[] observations =
        [
            new("a1", ObservationState.NoConnection, At("2026-10-03T08:00:00Z"), At("2026-10-04T20:00:00Z")),
            // pt 22:00 – sob 06:00 należy do piątku (liczony), sob 06:00 – 08:00 już nie.
            new("a2", ObservationState.NoConnection, At("2026-10-03T04:00:00Z"), At("2026-10-03T08:00:00Z")),
        ];
        var data = Create([], Mon, Tue, observations: observations);

        Assert.Equal(TimeSpan.FromHours(2), data.NoConnection);
        Assert.Equal(TimeSpan.FromHours(2), data.Machines.Single(m => m.MachineId == "a2").NoConnection);
        Assert.DoesNotContain(data.Machines, m => m.MachineId == "a1");
    }

    [Fact]
    public void BrakPolaczenia_WybranaZmiana_TylkoJejCzesc()
    {
        ObservationInterval[] observations = [new("a1", ObservationState.NoConnection, At("2026-09-30T13:00:00Z"), At("2026-09-30T15:00:00Z"))];
        Assert.Equal(TimeSpan.FromHours(1), Create([], Mon, Tue, "06:00–14:00", observations).NoConnection);
        Assert.Equal(TimeSpan.FromHours(1), Create([], Mon, Tue, "14:00–22:00", observations).NoConnection);
        Assert.Equal(TimeSpan.Zero, Create([], Mon, Tue, "22:00–06:00", observations).NoConnection);
        Assert.Equal(TimeSpan.FromHours(2), Create([], Mon, Tue, null, observations).NoConnection);
    }

    [Fact]
    public void BrakPolaczenia_PozaZakresemIPoTeraz_NieLiczony()
    {
        ObservationInterval[] observations =
        [
            new("a1", ObservationState.NoConnection, At("2026-09-25T08:00:00Z"), At("2026-09-25T10:00:00Z")), // przed zakresem
            new("a1", ObservationState.NoConnection, At("2026-10-06T11:00:00Z"), At("2026-10-06T15:00:00Z")), // sięga za „teraz”
        ];
        var data = Create([], Mon, Tue, observations: observations);
        Assert.Equal(TimeSpan.FromHours(1), data.NoConnection);
    }

    [Fact]
    public void BrakPolaczenia_OdDoJednegoDnia_GraniceZmianDnia()
    {
        ObservationInterval[] observations = [new("a1", ObservationState.NoConnection, At("2026-09-30T04:00:00Z"), At("2026-10-01T08:00:00Z"))];
        var day = new DateOnly(2026, 9, 30);
        Assert.Equal(TimeSpan.FromHours(24), Create([], day, day, observations: observations).NoConnection);
    }

    [Fact]
    public void PierwszenstwoStanow_ObserwowanaWygrywaZBrakiemPolaczenia()
    {
        var a = At("2026-09-30T08:00:00Z");
        ObservationInterval[] observations =
        [
            new("a1", ObservationState.Observed, a, a.AddHours(2)),
            new("a1", ObservationState.NoConnection, a, a.AddHours(2)),
            new("a1", ObservationState.ScreenHidden, a.AddHours(1), a.AddHours(3)),
            new("a1", ObservationState.NoConnection, a.AddHours(2.5), a.AddHours(4)),
        ];
        var data = Create([], Mon, Tue, observations: observations);

        Assert.Equal(TimeSpan.FromHours(1), data.ScreenHidden);      // 10:00–11:00 (wcześniej Observed)
        Assert.Equal(TimeSpan.FromHours(1), data.NoConnection);      // 11:00–12:00 (10:30–11:00 to ekran niewidoczny)
    }

    [Fact]
    public void Dziennik_ZaczetyPoPoczatkuZakresu_ZnanyOd()
    {
        var first = At("2026-10-01T09:15:00Z");
        ObservationInterval[] observations = [new("a1", ObservationState.Observed, first, first.AddHours(5))];
        var data = Create([], Mon, Tue, observations: observations);

        Assert.True(data.HasObservationJournal);
        Assert.Equal(first, data.ObservationKnownFrom);
        Assert.Empty(data.Machines); // sama obserwacja bez postoju i bez braku danych
    }

    [Fact]
    public void Dziennik_ZaczetyPrzedZakresem_BezNoty()
    {
        ObservationInterval[] observations = [new("a1", ObservationState.Observed, At("2026-09-01T08:00:00Z"), At("2026-10-06T08:00:00Z"))];
        var data = Create([], Mon, Tue, observations: observations);
        Assert.Null(data.ObservationKnownFrom);
    }

    [Fact]
    public void BezDziennika_NieznanyNieZerowy()
    {
        var data = Create([Ep("a1", "Servo 6", "2026-09-29T08:00:00Z", 10)], Mon, Tue);
        Assert.False(data.HasObservationJournal);
        Assert.Null(data.ObservationKnownFrom);
        Assert.Equal(TimeSpan.Zero, data.NoConnection);
    }

    [Fact]
    public void WpisyDziennikaNiepoprawne_Pomijane()
    {
        ObservationInterval[] observations =
        [
            null!,
            new("", ObservationState.NoConnection, At("2026-09-30T08:00:00Z"), At("2026-09-30T09:00:00Z")),
            new("a1", ObservationState.NoConnection, At("2026-09-30T10:00:00Z"), At("2026-09-30T09:00:00Z")), // odwrócony
            new("a1", ObservationState.NoConnection, default, default),
        ];
        var data = Create([], Mon, Tue, observations: observations);
        Assert.Equal(TimeSpan.Zero, data.NoConnection);
        Assert.Empty(data.Machines);
    }

    [Fact]
    public void MaszynaSpozaKonfiguracji_NazwaZWierszaAlboIdentyfikator()
    {
        ObservationInterval[] observations = [new("x9", ObservationState.NoConnection, At("2026-09-30T08:00:00Z"), At("2026-09-30T09:00:00Z"))];
        var data = Create([Ep("z7", "Servo 6", "2026-09-29T08:00:00Z", 10, name: "Stara nazwa"), Ep("z7", "Servo 6", "2026-09-30T08:00:00Z", 10, name: "Nowa nazwa")],
            Mon, Tue, observations: observations);

        Assert.Equal("Nowa nazwa", data.Machines.Single(m => m.MachineId == "z7").Name);
        Assert.DoesNotContain(data.Machines, m => m.MachineId == "x9");
        Assert.Equal(TimeSpan.Zero, data.NoConnection);
    }


    [Fact]
    public void ShiftParts_ZgodneZPodzialemBuild_TakzeZeZmianaCzasu()
    {
        foreach (var zone in new[] { Utc, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"), TestZones.WednesdayDst(2), TestZones.WednesdayDst(6) })
        {
            var random = new Random(7);
            for (int i = 0; i < 200; i++)
            {
                var start = At("2026-03-23T00:00:00Z").AddMinutes(random.Next(0, 4 * 24 * 60));
                var end = start.AddMinutes(random.Next(4, 30 * 60)); // > 3 min — inaczej Build pomija epizod
                // Build kroi przedział na zmiany — ShiftParts musi dać dokładnie te same kawałki tej samej zmiany.
                var slices = DowntimeReport.Build([new DowntimeEpisode("a", "A", "r", start, end, end, EpisodeId: Guid.NewGuid())], zone)
                    .Where(s => s.Start >= start).ToArray();
                foreach (string shift in new[] { "06:00–14:00", "14:00–22:00", "22:00–06:00" })
                {
                    var expected = slices.Where(s => s.Shift == shift).Sum(s => (s.End - s.Start).TotalMinutes);
                    var actual = ReportCalendar.ReportedIntervals(start, end, zone)
                        .SelectMany(p => DowntimeCharts.ShiftParts(p.Start, p.End, shift, zone)).Sum(p => (p.End - p.Start).TotalMinutes);
                    Assert.True(Math.Abs(expected - actual) < 1e-6, $"{zone.Id} {start:o} {end:o} {shift}: {expected} != {actual}");
                }
            }
        }
    }

    [Fact]
    public void ShiftParts_BezZmiany_CalyPrzedzial_PustyGdyOdwrocony()
    {
        var a = At("2026-09-30T08:00:00Z");
        Assert.Equal([(a, a.AddHours(30))], DowntimeCharts.ShiftParts(a, a.AddHours(30), null, Utc));
        Assert.Empty(DowntimeCharts.ShiftParts(a, a, null, Utc));
        Assert.Empty(DowntimeCharts.ShiftParts(a, a.AddHours(-1), "06:00–14:00", Utc));
        Assert.Empty(DowntimeCharts.ShiftParts(a, a.AddHours(30), "Nieznana", Utc));
    }

    [Fact]
    public void ShiftParts_NocnaPrzezPolnoc()
    {
        var parts = DowntimeCharts.ShiftParts(At("2026-09-30T20:00:00Z"), At("2026-10-01T08:00:00Z"), "22:00–06:00", Utc).ToArray();
        Assert.Equal([(At("2026-09-30T22:00:00Z"), At("2026-10-01T06:00:00Z"))], parts);
    }

    [Fact]
    public void ArgumentyNull_Rzucaja()
    {
        Assert.Throws<ArgumentNullException>(() => DowntimeExportData.Create(null!, Mon, Tue, null, [], [], Utc, Now, "", ""));
        Assert.Throws<ArgumentNullException>(() => DowntimeExportData.Create([], Mon, Tue, null, null!, [], Utc, Now, "", ""));
        Assert.Throws<ArgumentNullException>(() => DowntimeExportData.Create([], Mon, Tue, null, [], null!, Utc, Now, "", ""));
        Assert.Throws<ArgumentNullException>(() => DowntimeExportData.Create([], Mon, Tue, null, [], [], null!, Now, "", ""));
    }
}
