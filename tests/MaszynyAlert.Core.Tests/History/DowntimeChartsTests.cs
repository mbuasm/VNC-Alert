using System.Diagnostics;
using System.Globalization;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeChartsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Limit 1000 wierszy: 200 ms lokalnie, 1000 ms na CI — <see cref="PerformanceLimits"/>.</summary>
    private static readonly TimeSpan ThousandRowsLocal = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ThousandRowsCi = TimeSpan.FromMilliseconds(1000);

    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly DateOnly Mon = new(2026, 9, 14);
    private static readonly DateOnly Tue = new(2026, 9, 15);
    private static readonly DateOnly Wed = new(2026, 9, 16);
    private static readonly DateOnly Thu = new(2026, 9, 17);
    private static readonly DateOnly Fri = new(2026, 9, 18);
    private static readonly DateOnly Sat = new(2026, 9, 19);
    private static readonly DateOnly NextMon = new(2026, 9, 21);

    private static readonly DateTimeOffset Noon = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string S1 = "06:00–14:00";
    private const string S2 = "14:00–22:00";
    private const string S3 = "22:00–06:00";

    private static DowntimeSlice Row(DateOnly day, string machineId, double minutes, string reason = "Servo 6",
        bool isLong = false, string category = "inny", string shift = S1, string? machineName = null,
        Guid? episode = null)
    {
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);
        return new DowntimeSlice(day, shift, machineName ?? "Nazwa " + machineId, reason, start, start.AddMinutes(minutes),
            false, false)
        {
            IsLong = isLong, Category = category, MachineId = machineId, EpisodeId = episode ?? Guid.NewGuid(),
        };
    }

    private static DowntimeChartOptions Options(DateOnly? from = null, DateOnly? to = null, string? shift = null,
        IReadOnlyList<ChartMachine>? machines = null, IReadOnlySet<string>? filter = null, bool perMachine = false,
        DateTimeOffset? now = null, TimeZoneInfo? zone = null, IReadOnlyList<ObservationInterval>? observation = null) => new()
    {
        From = from, To = to, Shift = shift, Machines = machines ?? [], MachineFilter = filter, LinePerMachine = perMachine,
        GeneratedAt = now ?? Noon, Zone = zone ?? TimeZoneInfo.Utc, Observation = observation,
    };

    private static ChartMachine M(string id) => new(id, "Nazwa " + id);

    private static double Value(ChartSeries series, string label) => series.Points.Single(p => p.Label == label).Value;

    private static string Dm(DateOnly day) => day.ToString("dd.MM", Pl);


    [Fact]
    public void Bar_PartsAreMinutesOfTheMachine_LongPartOnlyIsLong()
    {
        var rows = new[]
        {
            Row(Tue, "a", 10), Row(Wed, "a", 20),
            Row(Wed, "a", 90, isLong: true), Row(Thu, "b", 7),
        };

        var bar = DowntimeCharts.Build(rows, Options(Mon, Fri)).Machines;

        Assert.Equal(["Nazwa a", "Nazwa b"], bar.Categories);
        Assert.Equal([DowntimeCharts.ErrorsName, DowntimeCharts.IdleName, DowntimeCharts.IdleAtZeroName], bar.Series.Select(s => s.Name));
        var errors = bar.Series[0];
        Assert.Equal(120, Value(errors, "Nazwa a"), 9);
        Assert.Equal(90, errors.Points.Single(p => p.Label == "Nazwa a").LongValue, 9);
        Assert.Equal(120, bar.Totals[0], 9);
        Assert.Equal(7, Value(errors, "Nazwa b"), 9);
        Assert.Equal(0, errors.Points.Single(p => p.Label == "Nazwa b").LongValue);
        Assert.All(bar.Series.Skip(1), s => Assert.All(s.Points, p => Assert.Equal(0, p.Value)));
        Assert.Equal(DowntimeCharts.MinutesText(120), bar.TotalTexts[0]);
        Assert.Equal("120,0 min", bar.TotalTexts[0]);
    }

    // ── Suma słupków = Totals tabeli (na prawdziwym DowntimeReport.Build) ─────────────────────

    [Fact]
    public void Bar_SumOfAllBarsEqualsReportTotals_OnRealReportRows()
    {
        DowntimeEpisode Ep(string machine, string start, string end, string? category = null) =>
            new(machine, "Nazwa " + machine, "Servo 6", DateTimeOffset.Parse(start), DateTimeOffset.Parse(end),
                DateTimeOffset.Parse(end), EpisodeId: Guid.NewGuid(), Category: category);
        var episodes = new[]
        {
            Ep("a", "2026-09-15T13:50:00Z", "2026-09-15T14:20:00Z"),          // przez granicę zmian
            Ep("a", "2026-09-16T08:00:00Z", "2026-09-16T10:30:00Z"),          // długi
            Ep("b", "2026-09-18T21:00:00Z", "2026-09-19T09:00:00Z", "tacka"), // przez noc piątkową w weekend
            Ep("b", "2026-09-17T08:00:00Z", "2026-09-17T08:02:00Z"),          // 2 min — poza raportem
            Ep("c", "2026-09-14T05:00:00Z", "2026-09-14T05:30:00Z"),          // poniedziałek przed 06:00 = weekend
        };
        var rows = DowntimeReport.Build(episodes, TimeZoneInfo.Utc);
        var totals = DowntimeReport.Totals(rows);

        var charts = DowntimeCharts.Build(rows, Options(Mon, Fri, machines: [M("a"), M("b"), M("c")]));

        Assert.Equal(totals.All.Minutes, charts.Machines.Totals.Sum(), 6);
        // Same błędy (bez postojów bez pracy): seria „Błędy maszyn” = całość, długie w LongValue.
        Assert.Equal(totals.All.Minutes, charts.Machines.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Equal(totals.All.Minutes - totals.WithoutLong.Minutes, charts.Machines.Series[0].Points.Sum(p => p.LongValue), 6);
        Assert.Equal(totals.All.Minutes, charts.Errors.Total, 6);
        Assert.Equal(totals.All.Minutes, charts.Kinds.Total, 6);
        Assert.Equal(totals.All.Minutes, charts.ErrorMachines.Totals.Sum(), 6);
        Assert.Equal(totals.All.Minutes, charts.Days.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Equal(totals.WithoutLong.Minutes, charts.Days.Series[1].Points.Sum(p => p.Value), 6);
        Assert.Equal(totals.All.Minutes, charts.Days.Series[2].Points.Sum(p => p.Value), 6);
        Assert.Equal(0, Value(charts.Machines.Series[0], "Nazwa c") + Value(charts.Machines.Series[1], "Nazwa c"));
    }

    // ── Maszyny: z konfiguracji z zerem, z wierszy (grupa), bez powtórzeń ────────────────────

    [Fact]
    public void Bar_ConfigMachineWithoutDowntimeHasZeroBar_RowOnlyMachineHasOwnBar_NoDuplicates()
    {
        var rows = new[] { Row(Tue, "a", 10), Row(Wed, "a", 5), Row(Tue, "grupa-x", 4, machineName: "Obca X") };

        var bar = DowntimeCharts.Build(rows, Options(Mon, Fri, machines: [M("a"), M("zero"), M("a")])).Machines;

        Assert.Equal(["Nazwa a", "Obca X", "Nazwa zero"], bar.Categories);
        Assert.Equal(0, bar.Totals[2]);
        Assert.Equal("0,0 min", bar.TotalTexts[2]);
        Assert.Equal(3, DowntimeCharts.Build(rows, Options(Mon, Fri, machines: [M("a"), M("zero"), M("a")])).Header.Machines);
    }

    [Fact]
    public void Bar_RowWithoutMachineIdIsKeyedByName_NotMergedWithConfigMachineOfTheSameId()
    {
        var rows = new[] { Row(Tue, "", 6, machineName: "Stara nazwa"), Row(Tue, "", 4, machineName: "Stara nazwa"), Row(Tue, "a", 3) };

        var bar = DowntimeCharts.Build(rows, Options(Mon, Fri, machines: [M("a")])).Machines;

        Assert.Equal(["Stara nazwa", "Nazwa a"], bar.Categories);
        Assert.Equal(10, bar.Totals[0], 9);
    }

    [Fact]
    public void Bar_InvalidConfigEntriesAreIgnored()
    {
        var bar = DowntimeCharts.Build([Row(Tue, "a", 5)],
            Options(Mon, Fri, machines: [null!, new ChartMachine("", "Pusta"), new ChartMachine("  ", "Biała"), M("a")])).Machines;
        Assert.Equal(["Nazwa a"], bar.Categories);
    }

    // ── Sortowanie i filtr maszyn ───────────────────────────────────────────────────────────

    [Fact]
    public void Bar_SortedDescending_TieKeepsConfigurationOrder()
    {
        var rows = new[] { Row(Tue, "c", 5), Row(Tue, "b", 5), Row(Tue, "a", 9), Row(Tue, "d", 5) };

        var bar = DowntimeCharts.Build(rows, Options(Mon, Fri, machines: [M("d"), M("c"), M("b"), M("a"), M("e")])).Machines;

        Assert.Equal(["Nazwa a", "Nazwa d", "Nazwa c", "Nazwa b", "Nazwa e"], bar.Categories);
    }

    [Fact]
    public void MachineFilter_RemovesMachinesAndTheirRowsFromAllCharts()
    {
        var rows = new[] { Row(Tue, "a", 5, reason: "Błąd A"), Row(Tue, "b", 7, reason: "Błąd B") };

        var charts = DowntimeCharts.Build(rows, Options(Mon, Fri, machines: [M("a"), M("b"), M("c")], filter: new HashSet<string> { "b" }));

        Assert.Equal(["Nazwa b"], charts.Machines.Categories);
        Assert.Equal(1, charts.Header.Machines);
        Assert.Equal("Błąd B", Assert.Single(charts.Errors.Slices).Label);
        Assert.Equal(7, charts.Days.Series[0].Points.Sum(p => p.Value), 9);
    }

    // ── Kołowy: udziały ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pie_SharesSumToOne_TotalIsSumOfMinutes_OnlyPositiveSlices()
    {
        var rows = Enumerable.Range(0, 11).Select(i => Row(Tue, "a", 3.3 + i * 1.7, reason: "Błąd " + i))
            .Append(Row(Tue, "a", 0, reason: "Zero"))
            .Append(Row(Tue, "a", 4, reason: "", category: "nierozpoznany")).ToArray();

        var pie = DowntimeCharts.Build(rows, Options(Mon, Fri)).Errors;

        Assert.Equal(1, pie.Slices.Sum(s => s.Share), 9);
        Assert.Equal(rows.Sum(r => r.Minutes), pie.Total, 9);
        Assert.Equal(pie.Total, pie.Slices.Sum(s => s.Value), 9);
        Assert.All(pie.Slices, s => Assert.True(s.Value > 0));
        Assert.DoesNotContain(pie.Slices, s => s.Label == "Zero");
        Assert.Equal(DowntimeCharts.MinutesText(pie.Total), pie.TotalText);
        Assert.All(pie.Slices, s => Assert.Equal((s.Share * 100).ToString("N1", Pl) + " %", s.ShareText));
    }

    // ── Kołowy: 7 nazw, 8 nazw, odmiana „nazwa/nazwy/nazw” ─────────────────────────────────

    [Theory]
    [InlineData(1, 1, null)]
    [InlineData(6, 6, null)]
    [InlineData(7, 7, null)]
    [InlineData(8, 6, "Pozostałe (2 nazwy)")]
    [InlineData(9, 6, "Pozostałe (3 nazwy)")]
    [InlineData(10, 6, "Pozostałe (4 nazwy)")]
    [InlineData(11, 6, "Pozostałe (5 nazw)")]
    [InlineData(12, 6, "Pozostałe (6 nazw)")]
    [InlineData(18, 6, "Pozostałe (12 nazw)")]
    [InlineData(19, 6, "Pozostałe (13 nazw)")]
    [InlineData(20, 6, "Pozostałe (14 nazw)")]
    [InlineData(28, 6, "Pozostałe (22 nazwy)")]
    [InlineData(31, 6, "Pozostałe (25 nazw)")]
    public void Pie_OwnSlicesAndRemainderWithPolishPlural(int names, int own, string? remainder)
    {
        var rows = Enumerable.Range(0, names).Select(i => Row(Tue, "a", 100 - i, reason: $"Błąd {i:00}")).ToArray();

        var pie = DowntimeCharts.Build(rows, Options(Mon, Fri)).Errors;

        Assert.Equal(own, pie.Slices.Count(s => s.Kind == PieSliceKind.Name));
        Assert.Equal(Enumerable.Range(0, own).Select(i => $"Błąd {i:00}"), pie.Slices.Where(s => s.Kind == PieSliceKind.Name).Select(s => s.Label));
        var rest = pie.Slices.SingleOrDefault(s => s.Kind == PieSliceKind.Remainder);
        Assert.Equal(remainder, rest?.Label);
        if (rest is not null)
        {
            Assert.Equal(names - own, rest.NameCount);
            Assert.Equal(Enumerable.Range(own, names - own).Sum(i => 100.0 - i), rest.Value, 9);
            Assert.Equal(ChartPalette.Remainder, rest.Color);
            Assert.Same(rest, pie.Slices[^1]);
        }
        Assert.True(pie.Slices.Count <= ChartPalette.SlotCount);
    }

    // ── „Nierozpoznany” zawsze osobno ──────────────────────────────────────────────────────

    [Fact]
    public void Pie_UnrecognizedIsAlwaysOwnSlice_NeverInRemainder_EvenWhenSmallest()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Row(Tue, "a", 50 - i, reason: $"Błąd {i}"))
            .Append(Row(Tue, "a", 0.5, reason: "Stary tekst spoza słownika", category: "nierozpoznany"))
            .Append(Row(Tue, "b", 0.25, reason: "   ", category: "inny"))
            .Append(Row(Tue, "b", 0.25, reason: "Cokolwiek", category: "NIEROZPOZNANY")).ToArray();

        var pie = DowntimeCharts.Build(rows, Options(Mon, Fri)).Errors;

        var unrecognized = Assert.Single(pie.Slices, s => s.Kind == PieSliceKind.Unrecognized);
        Assert.Equal(DowntimePresentation.UnrecognizedName, unrecognized.Label);
        Assert.Equal(1.0, unrecognized.Value, 9);
        Assert.Equal(ChartPalette.Unrecognized, unrecognized.Color);
        Assert.Equal(4, Assert.Single(pie.Slices, s => s.Kind == PieSliceKind.Remainder).NameCount);
        Assert.DoesNotContain(pie.Slices, s => s.Label.Contains("Stary tekst") || s.Label.Contains("Cokolwiek"));
        // Kolejność: nazwy od największej, „Nierozpoznany”, „Pozostałe”.
        Assert.Equal([PieSliceKind.Name, PieSliceKind.Name, PieSliceKind.Name, PieSliceKind.Name, PieSliceKind.Name, PieSliceKind.Name,
            PieSliceKind.Unrecognized, PieSliceKind.Remainder], pie.Slices.Select(s => s.Kind));
    }

    [Fact]
    public void Pie_OnlyUnrecognized_IsOneFullSlice()
    {
        var pie = DowntimeCharts.Build([Row(Tue, "a", 5, category: "nierozpoznany")], Options(Mon, Fri)).Errors;
        var slice = Assert.Single(pie.Slices);
        Assert.Equal(PieSliceKind.Unrecognized, slice.Kind);
        Assert.Equal(1, slice.Share);
        Assert.Equal("100,0 %", slice.ShareText);
    }

    // ── „Tacka nie pobrana” z dwóch maszyn → jedna pozycja ────────────────────────────────

    [Fact]
    public void Pie_TrayNamesFromTwoMachinesBecomeOneFrameSliceEach()
    {
        var rows = new[]
        {
            Row(Tue, "a", 5, reason: "Tacka nie pobrana", category: "tacka"),
            Row(Wed, "b", 6, reason: "Tacka nie pobrana", category: "tacka"),
            Row(Tue, "a", 4, reason: "Tacka nie odłożona", category: "tacka"),
            Row(Tue, "b", 2, reason: "Stacka", category: "inny"),
            Row(Tue, "b", 1, reason: "atacka", category: "inny"),
        };

        var labels = DowntimeCharts.Build(rows, Options(Mon, Fri)).Errors.Slices.ToDictionary(s => s.Label, s => s.Value);

        Assert.Equal(11, labels["Ramka niepobrana"], 9);
        Assert.Equal(4, labels["Ramka nieodłożona"], 9);
        Assert.Equal(2, labels["Stacka"], 9);
        Assert.Equal(1, labels["atacka"], 9);
        Assert.DoesNotContain(labels.Keys, k => k.Contains("Tacka", StringComparison.Ordinal));
        // Wiersze raportu (i tabela/CSV) zostają z oryginalnym tekstem.
        Assert.Equal("Tacka nie pobrana", rows[0].Reason);
    }

    // ── Kolory kołowego = ranga; remis deterministyczny ────────────────────────────────────

    [Fact]
    public void Pie_ColorsFollowRank_TieIsOrdinalAndIndependentOfInputOrder()
    {
        var rows = new[]
        {
            Row(Tue, "a", 5, reason: "Gamma"), Row(Tue, "a", 9, reason: "Alfa"), Row(Tue, "a", 5, reason: "Beta"),
            Row(Tue, "a", 5, reason: "Delta"),
        };

        var pie = DowntimeCharts.Build(rows, Options(Mon, Fri)).Errors;
        var reversed = DowntimeCharts.Build(rows.Reverse().ToArray(), Options(Mon, Fri)).Errors;

        Assert.Equal(["Alfa", "Beta", "Delta", "Gamma"], pie.Slices.Select(s => s.Label));
        Assert.Equal(pie.Slices.Select(s => s.Label), reversed.Slices.Select(s => s.Label));
        for (int i = 0; i < pie.Slices.Count; i++) Assert.Equal(ChartPalette.Slot(i), pie.Slices[i].Color);
    }

    // ── Liniowy: dni robocze, wartości, noc piątkowa do piątku ────────────────────────────

    [Fact]
    public void Line_DaysAreWorkingDaysOnly_ValuesAreMinutesPerDay()
    {
        var rows = new[] { Row(Mon, "a", 4), Row(Mon, "b", 6), Row(Fri, "a", 10, isLong: true) };

        var line = DowntimeCharts.Build(rows, Options(Mon, NextMon)).Days;

        Assert.Equal([Mon, Tue, Wed, Thu, Fri, NextMon], line.Days);
        Assert.DoesNotContain(line.Days, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.False(line.PerMachine);
        Assert.Equal([DowntimeCharts.AllName, DowntimeCharts.WithoutLongName, DowntimeCharts.ErrorsName], line.Series.Select(s => s.Name));
        Assert.Equal(10, Value(line.Series[0], Dm(Mon)), 9);
        Assert.Equal(10, Value(line.Series[0], Dm(Fri)), 9);
        Assert.Equal(0, Value(line.Series[1], Dm(Fri)));
        Assert.Equal(0, Value(line.Series[0], Dm(Wed)));
        Assert.All(line.Series, s => Assert.Equal(line.Days.Count, s.Points.Count));
        Assert.Equal(line.Days.Select(Dm), line.Series[0].Points.Select(p => p.Label));
    }

    [Fact]
    public void Line_FridayNightShiftCountsForFriday_WeekendPartIsCut()
    {
        // Piątek 23:00 – sobota 09:00 (UTC): do raportu wchodzi 23:00–06:00 jako nocna zmiana PIĄTKU.
        var episode = new DowntimeEpisode("a", "A", "Servo 6", DateTimeOffset.Parse("2026-09-18T23:00:00Z"),
            DateTimeOffset.Parse("2026-09-19T09:00:00Z"), DateTimeOffset.Parse("2026-09-19T09:00:00Z"), EpisodeId: Guid.NewGuid());
        var rows = DowntimeReport.Build([episode], TimeZoneInfo.Utc);

        var line = DowntimeCharts.Build(rows, Options(Mon, NextMon)).Days;

        Assert.All(rows, r => Assert.Equal(Fri, r.Day));
        Assert.Equal(7 * 60, Value(line.Series[0], Dm(Fri)), 9);
        Assert.Equal(0, Value(line.Series[1], Dm(Fri))); // 10 h = długi postój
        Assert.Equal(0, Value(line.Series[0], Dm(NextMon)));
    }

    // ── Zakres w przyszłość: oś kończy się na dziś (dzień zmian) ──────────────────────────

    [Fact]
    public void Line_ToInFuture_AxisEndsToday_HeaderSaysDataUntil()
    {
        var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero); // środa
        var charts = DowntimeCharts.Build([Row(Mon, "a", 5)], Options(Mon, new DateOnly(2026, 9, 25), now: now));

        Assert.Equal([Mon, Tue, Wed], charts.Days.Days);
        Assert.Equal(3, charts.Header.WorkingDays);
        Assert.Equal(9, charts.Header.Shifts);
        Assert.Contains("dane do 16.09.2026", charts.Header.RangeText);
        Assert.Equal("14.09.2026 – 25.09.2026, dane do 16.09.2026", charts.Header.RangeText);
    }

    [Fact]
    public void Line_At0559TodayIsStillThePreviousShiftDay()
    {
        var at0559 = new DateTimeOffset(2026, 9, 16, 5, 59, 0, TimeSpan.Zero); // środa 05:59 = nocna zmiana wtorku
        var at0600 = at0559.AddMinutes(1);

        Assert.Equal(Tue, DowntimeCharts.Build([], Options(Mon, Fri, now: at0559)).Days.Days[^1]);
        Assert.Equal(Wed, DowntimeCharts.Build([], Options(Mon, Fri, now: at0600)).Days.Days[^1]);
    }

    [Fact]
    public void Line_TodayIsComputedInTheHallZone_NotUtc()
    {
        var now = new DateTimeOffset(2026, 9, 16, 4, 30, 0, TimeSpan.Zero);
        Assert.Equal(Wed, DowntimeCharts.Build([], Options(Mon, Fri, now: now, zone: TestZones.Warsaw)).Days.Days[^1]);
        Assert.Equal(Tue, DowntimeCharts.Build([], Options(Mon, Fri, now: now, zone: TimeZoneInfo.Utc)).Days.Days[^1]);
    }

    [Fact]
    public void Line_RowFromTheFutureExtendsTheAxis()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero); // wtorek
        var charts = DowntimeCharts.Build([Row(Tue, "a", 5), Row(Thu, "a", 4)], Options(Mon, Fri, now: now));

        Assert.Equal([Mon, Tue, Wed, Thu], charts.Days.Days);
        Assert.Equal(4, Value(charts.Days.Series[0], Dm(Thu)), 9);
    }

    [Fact]
    public void Header_ToOnSaturdayAfterFriday_NoDataUntilNote()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero); // piątek
        var header = DowntimeCharts.Build([], Options(Mon, Sat, now: now)).Header;
        Assert.Equal("14.09.2026 – 19.09.2026", header.RangeText);
    }

    // ── Bez From i bez wierszy; bardzo długi zakres; DateOnly.MaxValue ────────────────────

    [Fact]
    public void Line_NoFromAndNoRows_OneDayToday_OrNoneOnSaturday()
    {
        var charts = DowntimeCharts.Build([], Options(now: Noon));
        Assert.Equal([NextMon], charts.Days.Days);
        Assert.StartsWith("cała historia, ", charts.Header.RangeText);

        var saturday = DowntimeCharts.Build([], Options(now: new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)));
        Assert.Empty(saturday.Days.Days);
        Assert.Equal("brak dni roboczych w zakresie", saturday.Header.RangeText);
        Assert.Equal(0, saturday.Header.Shifts);
    }

    [Fact]
    public void Line_RangeLongerThan735Days_KeepsNewest520WorkingDays_WithNote()
    {
        var rows = new[] { Row(new DateOnly(2023, 1, 3), "a", 5), Row(Fri, "a", 7) };

        var line = DowntimeCharts.Build(rows, Options(new DateOnly(2023, 1, 2), Fri)).Days;

        Assert.Equal(DowntimeCharts.MaxLineDays, line.Days.Count);
        Assert.Equal(Fri, line.Days[^1]);
        Assert.NotNull(line.Note);
        Assert.Contains("520", line.Note);
        Assert.Equal(7, line.Series[0].Points.Sum(p => p.Value), 9); // najstarszy dzień poza osią
        Assert.True(line.Days.Zip(line.Days.Skip(1)).All(p => p.First < p.Second));
    }

    [Fact]
    public void Line_RangeOfExactly520WorkingDays_HasNoNote()
    {
        var to = Fri;
        var from = to.AddDays(-(520 / 5 * 7) + 3); // poniedziałek 104 tygodnie wcześniej
        Assert.Equal(DayOfWeek.Monday, from.DayOfWeek);

        var line = DowntimeCharts.Build([], Options(from, to)).Days;

        Assert.Equal(520, line.Days.Count);
        Assert.Null(line.Note);
    }

    [Fact]
    public void Header_TruncatedLineAxis_StillCountsTheWholeRange()
    {
        var from = new DateOnly(2023, 1, 2);
        int workingDays = 0;
        for (var d = from; d <= Fri; d = d.AddDays(1)) if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) workingDays++;
        var rows = new[] { Row(new DateOnly(2023, 1, 3), "a", 5), Row(Fri, "a", 7) };

        var header = DowntimeCharts.Build(rows, Options(from, Fri, now: Noon.AddDays(-3))).Header;
        var history = DowntimeCharts.Build(rows, Options(now: Noon.AddDays(-3))).Header;

        Assert.Equal(workingDays, header.WorkingDays);
        Assert.Equal(workingDays * 3, header.Shifts);
        Assert.Equal("cała historia, 03.01.2023 – 18.09.2026", history.RangeText);
    }

    [Fact]
    public void Extremes_MinAndMaxDates_DoNotThrowAndDoNotEnumerateMillionsOfDays()
    {
        var far = new DateOnly(9999, 12, 30);
        var rows = new[] { Row(Tue, "a", 5), Row(far, "a", 3) };
        var watch = Stopwatch.StartNew();

        var a = DowntimeCharts.Build(rows, Options(DateOnly.MinValue, DateOnly.MaxValue));
        var b = DowntimeCharts.Build(rows, Options(null, DateOnly.MaxValue, observation: []));
        var c = DowntimeCharts.Build([Row(DateOnly.MaxValue, "a", 3)], Options(observation: [new ObservationInterval("a", ObservationState.NoConnection,
            new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero))]));
        var d = DowntimeCharts.Build(rows, Options(DateOnly.MinValue, DateOnly.MinValue));

        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
        foreach (var set in new[] { a, b, c })
        {
            Assert.Equal(DowntimeCharts.MaxLineDays, set.Days.Days.Count);
            Assert.NotNull(set.Days.Note);
        }
        Assert.Equal(DateOnly.MaxValue, c.Days.Days[^1]);
        Assert.Equal([DateOnly.MinValue], d.Days.Days);
        Assert.True(d.Machines.IsEmpty);
        Assert.Equal(8, a.Machines.Totals.Sum(), 9);
        Assert.NotNull(c.NoData);
    }

    // ── Liniowy osobno dla maszyn ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(7, 7, false)]
    [InlineData(8, 8, false)]
    [InlineData(9, 8, true)]
    [InlineData(12, 8, true)]
    public void Line_PerMachine_UpToEightOwnLines_ThenSevenPlusOthers(int machines, int series, bool others)
    {
        var rows = Enumerable.Range(0, machines).Select(i => Row(Tue, "m" + i, 50 - i)).Append(Row(Wed, "m0", 3)).ToArray();

        var line = DowntimeCharts.Build(rows, Options(Mon, Fri, perMachine: true,
            machines: Enumerable.Range(0, 12).Select(i => M("m" + i)).ToArray())).Days;

        Assert.True(line.PerMachine);
        Assert.Equal(series, line.Series.Count);
        Assert.Equal(others, line.Series[^1].Name == DowntimeCharts.OtherMachinesName);
        Assert.Equal("Nazwa m0", line.Series[0].Name);
        Assert.Equal(53, line.Series[0].Points.Sum(p => p.Value), 9);
        if (others)
        {
            Assert.Equal(ChartPalette.Remainder, line.Series[^1].Color);
            Assert.Equal(Enumerable.Range(7, machines - 7).Sum(i => 50.0 - i), line.Series[^1].Points.Sum(p => p.Value), 9);
        }
        Assert.Equal(rows.Sum(r => r.Minutes), line.Series.Sum(s => s.Points.Sum(p => p.Value)), 9);
        for (int i = 0; i < Math.Min(series, 7); i++) Assert.Equal(ChartPalette.Slot(i), line.Series[i].Color);
    }

    [Fact]
    public void Line_PerMachine_WithoutDowntime_OneZeroSeries()
    {
        var line = DowntimeCharts.Build([], Options(Mon, Fri, perMachine: true, machines: [M("a"), M("b")])).Days;
        var series = Assert.Single(line.Series);
        Assert.All(series.Points, p => Assert.Equal(0, p.Value));
        Assert.True(line.IsEmpty);
    }

    [Fact]
    public void Line_PerMachine_RowOnlyMachineUsesRowName()
    {
        var line = DowntimeCharts.Build([Row(Tue, "x", 5, machineName: "Obca"), Row(Tue, "", 4, machineName: "Bez Id")],
            Options(Mon, Fri, perMachine: true)).Days;
        Assert.Equal(["Obca", "Bez Id"], line.Series.Select(s => s.Name));
    }

    // ── Filtr zmiany ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShiftFilter_OnlyThatShiftsRows_ShiftsInRangeIsWorkingDaysTimesOne()
    {
        var rows = new[] { Row(Tue, "a", 5, shift: S1), Row(Tue, "a", 7, shift: S2), Row(Wed, "a", 11, shift: S3) };

        var night = DowntimeCharts.Build(rows, Options(Mon, Fri, shift: S3));
        var all = DowntimeCharts.Build(rows, Options(Mon, Fri));

        Assert.Equal(11, night.Machines.Totals.Sum(), 9);
        Assert.Equal(11, night.Errors.Total, 9);
        Assert.Equal(5, night.Header.Shifts);
        Assert.Equal(15, all.Header.Shifts);
        Assert.Contains("Zmiana: " + S3, night.Header.Text);
        Assert.Contains("Zmiana: wszystkie", all.Header.Text);
        Assert.Equal(23, all.Machines.Totals.Sum(), 9);
    }

    [Fact]
    public void RangeFilter_IsAppliedAgainEvenIfCallerDidNotFilter()
    {
        var rows = new[] { Row(Mon, "a", 5), Row(Wed, "a", 7), Row(NextMon, "a", 11) };
        var charts = DowntimeCharts.Build(rows, Options(Tue, Fri));
        Assert.Equal(7, charts.Machines.Totals.Sum(), 9);
        Assert.Equal([Tue, Wed, Thu, Fri], charts.Days.Days);
    }

    [Fact]
    public void NullRowsAreSkipped_NullArgumentsThrow()
    {
        var charts = DowntimeCharts.Build([null!, Row(Tue, "a", 5)], Options(Mon, Fri));
        Assert.Equal(5, charts.Machines.Totals.Sum(), 9);
        Assert.Throws<ArgumentNullException>(() => DowntimeCharts.Build(null!, Options()));
        Assert.Throws<ArgumentNullException>(() => DowntimeCharts.Build([], null!));
        Assert.Throws<ArgumentNullException>(() => DowntimeCharts.CountedParts(null!, null));
    }

    // ── CountedParts ───────────────────────────────────────────────────────────────────────

    private static DateTimeOffset U(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void CountedParts_ShiftFilterCutsToThatShift()
    {
        var start = U("2026-09-15T05:00:00Z"); // wtorek
        var end = U("2026-09-15T15:00:00Z");

        Assert.Equal([(U("2026-09-15T06:00:00Z"), U("2026-09-15T14:00:00Z"))], DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S1)(start, end));
        Assert.Equal([(start, U("2026-09-15T06:00:00Z"))], DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S3)(start, end));
        Assert.Equal([(U("2026-09-15T14:00:00Z"), end)], DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S2)(start, end));
        Assert.Equal([(start, end)], DowntimeCharts.CountedParts(TimeZoneInfo.Utc, null)(start, end));
    }

    [Fact]
    public void CountedParts_MondayBefore0600IsStillWeekend()
    {
        // Poniedziałek 05:00–06:00 to nocna zmiana NIEDZIELI — weekend, poza raportem.
        var parts = DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S3)(U("2026-09-21T05:00:00Z"), U("2026-09-21T15:00:00Z"));
        Assert.Empty(parts);
    }

    [Fact]
    public void CountedParts_WeekendIsCut_FridayNightCounts()
    {
        var start = U("2026-09-18T20:00:00Z");
        var end = U("2026-09-21T08:00:00Z");

        Assert.Equal([(start, U("2026-09-19T06:00:00Z")), (U("2026-09-21T06:00:00Z"), end)],
            DowntimeCharts.CountedParts(TimeZoneInfo.Utc, null)(start, end));
        Assert.Equal([(U("2026-09-18T22:00:00Z"), U("2026-09-19T06:00:00Z"))],
            DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S3)(start, end));
        Assert.Equal([(start, U("2026-09-18T22:00:00Z"))], DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S2)(start, end));
        Assert.Equal([(U("2026-09-21T06:00:00Z"), end)], DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S1)(start, end));
    }

    [Fact]
    public void CountedParts_EmptyOrReversedInterval_IsEmpty()
    {
        var t = U("2026-09-15T08:00:00Z");
        Assert.Empty(DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S1)(t, t));
        Assert.Empty(DowntimeCharts.CountedParts(TimeZoneInfo.Utc, S1)(t, t.AddHours(-3)));
    }

    // DST: w Europe/Warsaw zmiana czasu wypada w niedzielę, więc sztuczna strefa ze zmianą w ŚRODĘ (TestZones).

    [Fact]
    public void CountedParts_SpringForwardInsideMorningShift_RealElapsedTime()
    {
        var zone = TestZones.WednesdayDst(10);
        var start = new DateTimeOffset(2026, 3, 25, 5, 0, 0, TimeSpan.FromHours(1));
        var end = new DateTimeOffset(2026, 3, 25, 15, 0, 0, TimeSpan.FromHours(2));

        var parts = DowntimeCharts.CountedParts(zone, S1)(start, end).ToArray();

        var (from, to) = Assert.Single(parts);
        Assert.Equal(new DateTimeOffset(2026, 3, 25, 6, 0, 0, TimeSpan.FromHours(1)), from);
        Assert.Equal(new DateTimeOffset(2026, 3, 25, 14, 0, 0, TimeSpan.FromHours(2)), to);
        Assert.Equal(TimeSpan.FromHours(7), to - from);
    }

    [Fact]
    public void CountedParts_FallBackInsideMorningShift_RealElapsedTime()
    {
        var zone = TestZones.WednesdayDst(10);
        var start = new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.FromHours(1));

        var parts = DowntimeCharts.CountedParts(zone, S1)(start, end).ToArray();

        Assert.Equal(TimeSpan.FromHours(9), parts.Aggregate(TimeSpan.Zero, (sum, p) => sum + (p.End - p.Start)));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 6, 0, 0, TimeSpan.FromHours(2)), parts[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.FromHours(1)), parts[^1].End);
    }

    [Fact]
    public void NoData_OnDstDay_CountsRealElapsedTimeOfTheShift()
    {
        var zone = TestZones.WednesdayDst(10);
        var day = new DateOnly(2026, 3, 25);
        var observation = new[]
        {
            new ObservationInterval("a", ObservationState.NoConnection,
                new DateTimeOffset(2026, 3, 25, 0, 0, 0, TimeSpan.FromHours(1)), new DateTimeOffset(2026, 3, 26, 0, 0, 0, TimeSpan.FromHours(2))),
        };
        var now = new DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero);

        var charts = DowntimeCharts.Build([], Options(day, day, shift: S1, machines: [M("a")], now: now, zone: zone, observation: observation));

        var entry = Assert.Single(charts.NoData!.Machines);
        Assert.Equal(TimeSpan.FromHours(7), entry.NoConnection);
        Assert.Contains("Nazwa a — 7 godz. 0 min", charts.NoData.Lines[0]);
    }

    // ── „Brak danych” z dziennika obserwacji ──────────────────────────────────────────

    private static ObservationInterval Obs(string machine, ObservationState state, string start, string end) =>
        new(machine, state, U(start), U(end));

    [Fact]
    public void NoData_NullObservation_NoAnnotation_EmptyJournal_SentenceAboutNoEntries()
    {
        Assert.Null(DowntimeCharts.Build([], Options(Mon, Fri, machines: [M("a")])).NoData);

        var empty = DowntimeCharts.Build([], Options(Mon, Fri, machines: [M("a")], observation: [])).NoData!;
        Assert.Empty(empty.Machines);
        Assert.Equal([DowntimeCharts.NoConnectionName + ": dziennik czasu obserwacji nie ma jeszcze wpisów, więc tego czasu nie da się pokazać."],
            empty.Lines);
    }

    [Fact]
    public void NoData_SaturdayDoesNotCount_TwoAndHalfHoursText()
    {
        var observation = new[]
        {
            Obs("a", ObservationState.Observed, "2026-09-14T06:00:00Z", "2026-09-14T07:00:00Z"),
            Obs("a", ObservationState.NoConnection, "2026-09-15T08:00:00Z", "2026-09-15T10:30:00Z"),
            Obs("a", ObservationState.NoConnection, "2026-09-19T08:00:00Z", "2026-09-19T18:00:00Z"), // sobota
        };

        var noData = DowntimeCharts.Build([], Options(Mon, Fri, machines: [M("a"), M("b")], observation: observation)).NoData!;

        var entry = Assert.Single(noData.Machines);
        Assert.Equal("a", entry.MachineId);
        Assert.Equal(TimeSpan.FromMinutes(150), entry.NoConnection);
        Assert.Equal(DowntimeCharts.NoConnectionName + " (poza sumami postojów): Nazwa a — 2 godz. 30 min.", Assert.Single(noData.Lines));
    }

    [Fact]
    public void NoData_FridayNightCounts_ShiftFilterApplies()
    {
        var observation = new[]
        {
            Obs("a", ObservationState.Observed, "2026-09-14T06:00:00Z", "2026-09-14T07:00:00Z"),
            Obs("a", ObservationState.NoConnection, "2026-09-18T23:00:00Z", "2026-09-19T08:00:00Z"),
        };

        TimeSpan NoConn(string? shift) =>
            DowntimeCharts.Build([], Options(Mon, Fri, shift: shift, machines: [M("a")], observation: observation)).NoData!
                .Machines.SingleOrDefault()?.NoConnection ?? TimeSpan.Zero;

        Assert.Equal(TimeSpan.FromHours(7), NoConn(null));
        Assert.Equal(TimeSpan.FromHours(7), NoConn(S3));
        Assert.Equal(TimeSpan.Zero, NoConn(S1));
    }

    [Fact]
    public void NoData_OverlapOfTwoObserversCountsOnce_ScreenHiddenIsSeparateLine()
    {
        var observation = new[]
        {
            Obs("a", ObservationState.Observed, "2026-09-01T06:00:00Z", "2026-09-01T07:00:00Z"), // dziennik starszy niż zakres
            Obs("a", ObservationState.NoConnection, "2026-09-15T08:00:00Z", "2026-09-15T10:00:00Z"),
            Obs("a", ObservationState.NoConnection, "2026-09-15T09:00:00Z", "2026-09-15T11:00:00Z"), // drugi obserwator
            Obs("a", ObservationState.Observed, "2026-09-15T10:30:00Z", "2026-09-15T12:00:00Z"),
            Obs("b", ObservationState.ScreenHidden, "2026-09-16T08:00:00Z", "2026-09-16T08:45:00Z"),
            Obs("b", ObservationState.NoConnection, "2026-09-16T08:30:00Z", "2026-09-16T09:00:00Z"),
        };

        var noData = DowntimeCharts.Build([], Options(Mon, Fri, machines: [M("a"), M("b")], observation: observation)).NoData!;

        Assert.Equal(TimeSpan.FromMinutes(150), noData.Machines.Single(e => e.MachineId == "a").NoConnection);
        var b = noData.Machines.Single(e => e.MachineId == "b");
        Assert.Equal(TimeSpan.FromMinutes(45), b.ScreenHidden);
        Assert.Equal(TimeSpan.FromMinutes(15), b.NoConnection);
        Assert.Equal(2, noData.Lines.Count);
        Assert.Equal(DowntimeCharts.NoConnectionName + " (poza sumami postojów): Nazwa a — 2 godz. 30 min; Nazwa b — 15 min.", noData.Lines[0]);
        Assert.Equal(DowntimeCharts.ScreenHiddenName + " (poza sumami postojów): Nazwa b — 45 min.", noData.Lines[1]);
    }

    [Fact]
    public void NoData_JournalStartedAfterRangeStart_SaysSinceWhen_InHallLocalTime()
    {
        var observation = new[] { Obs("a", ObservationState.NoConnection, "2026-09-16T10:15:00Z", "2026-09-16T10:45:00Z") };

        var noData = DowntimeCharts.Build([], Options(Mon, Fri, machines: [M("a")], zone: TestZones.Warsaw, observation: observation)).NoData!;

        Assert.Equal("Dziennik czasu obserwacji prowadzony jest od 16.09.2026 12:15; wcześniej czas bez połączenia nie jest znany.", noData.Lines[^1]);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(noData.Machines).NoConnection);
    }

    [Fact]
    public void NoData_NothingRecordedInRange_SaysSo_AndCountsOnlyUntilGeneratedAt()
    {
        var observation = new[]
        {
            Obs("a", ObservationState.Observed, "2026-09-01T06:00:00Z", "2026-09-01T07:00:00Z"),
            Obs("a", ObservationState.NoConnection, "2026-09-21T13:00:00Z", "2026-09-21T15:00:00Z"), // po „teraz” (12:00)
        };

        var noData = DowntimeCharts.Build([], Options(Mon, NextMon, machines: [M("a")], observation: observation)).NoData!;

        Assert.Empty(noData.Machines);
        Assert.Equal([DowntimeCharts.NoConnectionName + ": w wybranym zakresie nie zapisano (poza sumami postojów)."], noData.Lines);
    }

    [Fact]
    public void NoData_NoWorkingDaysInRange()
    {
        var noData = DowntimeCharts.Build([], Options(Sat, Sat.AddDays(1), machines: [M("a")],
            observation: [Obs("a", ObservationState.Observed, "2026-09-01T06:00:00Z", "2026-09-01T07:00:00Z")])).NoData!;
        Assert.Equal([DowntimeCharts.NoConnectionName + ": brak dni roboczych w wybranym zakresie."], noData.Lines);
    }

    [Fact]
    public void NoData_MachineWithoutIdFromGroupRowsIsSkipped_FilteredMachineIsSkipped()
    {
        var observation = new[]
        {
            Obs("a", ObservationState.NoConnection, "2026-09-15T08:00:00Z", "2026-09-15T09:00:00Z"),
            Obs("b", ObservationState.NoConnection, "2026-09-15T08:00:00Z", "2026-09-15T09:00:00Z"),
        };

        var noData = DowntimeCharts.Build([Row(Tue, "", 5, machineName: "Bez Id")],
            Options(Mon, Fri, machines: [M("a"), M("b")], filter: new HashSet<string> { "a", "" }, observation: observation)).NoData!;

        Assert.Equal(["a"], noData.Machines.Select(m => m.MachineId));
    }

    /// <summary>
    /// dziennik obserwacji przyjmuje wpis z dowolnym końcem w przyszłości (<c>ObservationInterval.IsValid()</c> bez
    /// <c>notAfter</c> w <c>Apply</c>/odczycie pliku; początek ≥ 2020). Gdy jedyny wpis zaczyna się tuż przed końcem kalendarza,
    /// zdanie „prowadzony jest od …” robi <c>ToOffset(+02:00)</c> poza zakresem dat — wyjątek przerywa budowę WSZYSTKICH
    /// wykresów, a Panel odświeża raport co sekundę.
    /// </summary>
    [Fact]
    public void NoData_JournalEntryAtTheEndOfCalendar_DoesNotThrow()
    {
        var bogus = new ObservationInterval("a", ObservationState.NoConnection,
            DateTimeOffset.MaxValue.AddMinutes(-30), DateTimeOffset.MaxValue);
        Assert.True(bogus.IsValid());

        var charts = DowntimeCharts.Build([Row(Tue, "a", 5)], Options(Mon, Fri, machines: [M("a")], zone: TestZones.Warsaw, observation: [bogus]));

        Assert.NotNull(charts.NoData);
    }

    // ── ContentKey ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentKey_IgnoresGeneratedAt_ChangesWithShownValues()
    {
        var rows = new[] { Row(Tue, "a", 10), Row(Wed, "b", 5, reason: "Inny") };
        string Key(IReadOnlyList<DowntimeSlice> source, DateTimeOffset now) =>
            DowntimeCharts.Build(source, Options(Mon, Fri, machines: [M("a"), M("b")], now: now)).ContentKey();

        string key = Key(rows, Noon);
        Assert.Equal(key, Key(rows, Noon.AddMinutes(7)));
        Assert.NotEqual(DowntimeCharts.Build(rows, Options(Mon, Fri, now: Noon)).Header.Text,
            DowntimeCharts.Build(rows, Options(Mon, Fri, now: Noon.AddMinutes(7))).Header.Text);

        var plusTenth = new[] { Row(Tue, "a", 10.1), rows[1] };
        var plusHundredth = new[] { Row(Tue, "a", 10.01), rows[1] };
        Assert.NotEqual(key, Key(plusTenth, Noon));
        Assert.Equal(key, Key(plusHundredth, Noon));
    }

    [Fact]
    public void ContentKey_ChangesWithPerMachineMode_Shift_Filter_AndNoDataLines()
    {
        var rows = new[] { Row(Tue, "a", 10), Row(Wed, "b", 5) };
        var keys = new[]
        {
            DowntimeCharts.Build(rows, Options(Mon, Fri)).ContentKey(),
            DowntimeCharts.Build(rows, Options(Mon, Fri, perMachine: true)).ContentKey(),
            DowntimeCharts.Build(rows, Options(Mon, Fri, shift: S2)).ContentKey(),
            DowntimeCharts.Build(rows, Options(Mon, Fri, filter: new HashSet<string> { "a" })).ContentKey(),
            DowntimeCharts.Build(rows, Options(Mon, Fri, observation: [])).ContentKey(),
            DowntimeCharts.Build(rows, Options(Tue, Fri)).ContentKey(),
        };
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void ContentKey_ReorderOfPieSlices_ChangesKey()
    {
        var a = new[] { Row(Tue, "a", 10, reason: "X"), Row(Tue, "a", 5, reason: "Y") };
        var b = new[] { Row(Tue, "a", 5, reason: "X"), Row(Tue, "a", 10, reason: "Y") };
        Assert.NotEqual(DowntimeCharts.Build(a, Options(Mon, Fri)).ContentKey(), DowntimeCharts.Build(b, Options(Mon, Fri)).ContentKey());
    }

    // ── Teksty czasu ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5, "0 min")]
    [InlineData(0, "0 min")]
    [InlineData(20, "< 1 min")]
    [InlineData(29, "< 1 min")]
    [InlineData(30, "1 min")]
    [InlineData(35 * 60, "35 min")]
    [InlineData(59 * 60 + 29, "59 min")]
    [InlineData(59 * 60 + 40, "1 godz. 0 min")]
    [InlineData(60 * 60, "1 godz. 0 min")]
    [InlineData(125 * 60, "2 godz. 5 min")]
    [InlineData(150 * 60, "2 godz. 30 min")]
    [InlineData(100 * 3600, "100 godz. 0 min")]
    public void DurationText_Cases(int seconds, string expected) =>
        Assert.Equal(expected, DowntimeCharts.DurationText(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void MinutesText_PolishDecimalComma_AndThousandsSeparator()
    {
        string group = Pl.NumberFormat.NumberGroupSeparator;
        Assert.Equal("12,3 min", DowntimeCharts.MinutesText(12.34));
        Assert.Equal("0,0 min", DowntimeCharts.MinutesText(0));
        Assert.Equal("0,1 min", DowntimeCharts.MinutesText(0.05));
        Assert.Equal($"1{group}234,5 min", DowntimeCharts.MinutesText(1234.5));
        Assert.Equal($"12{group}345{group}678,0 min", DowntimeCharts.MinutesText(12345678));
    }

    // ── Nagłówek ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Header_Text_RangeShiftsMachinesAndGeneratedInHallTime()
    {
        var now = new DateTimeOffset(2026, 9, 21, 10, 5, 0, TimeSpan.Zero); // 12:05 w Warszawie
        var header = DowntimeCharts.Build([Row(Tue, "a", 5)], Options(Mon, Fri, machines: [M("a"), M("b")], now: now, zone: TestZones.Warsaw)).Header;

        Assert.Equal("Zakres: 14.09.2026 – 18.09.2026 (bez weekendów) · Zmiana: wszystkie · Zmian w zakresie: 15 · Maszyn: 2 · Wygenerowano: 21.09.2026 12:05",
            header.Text);
        Assert.Equal(Mon, header.From);
        Assert.Equal(Fri, header.To);
        Assert.Equal(5, header.WorkingDays);
        Assert.Equal(now, header.GeneratedAt);
    }

    [Fact]
    public void ReversedRange_HeaderSaysInvalid_NoDays_NoException()
    {
        var charts = DowntimeCharts.Build([Row(Tue, "a", 5)], Options(Fri, Mon, machines: [M("a")], observation: []));

        Assert.Equal("zakres dat jest niepoprawny", charts.Header.RangeText);
        Assert.Empty(charts.Days.Days);
        Assert.Equal(0, charts.Header.Shifts);
        Assert.True(charts.Machines.IsEmpty);
        Assert.True(charts.Errors.IsEmpty);
        Assert.True(charts.Days.IsEmpty);
    }

    // ── Puste dane, jeden wiersz, wydajność ──────────────────────────────────────────────────

    [Fact]
    public void EmptyReport_AllChartsEmpty_ButMachinesListed()
    {
        var charts = DowntimeCharts.Build([], Options(Mon, Fri, machines: [M("a"), M("b")]));

        Assert.True(charts.Machines.IsEmpty);
        Assert.Equal(["Nazwa a", "Nazwa b"], charts.Machines.Categories);
        Assert.True(charts.Errors.IsEmpty);
        Assert.Empty(charts.Errors.Slices);
        Assert.Equal("0,0 min", charts.Errors.TotalText);
        Assert.True(charts.Days.IsEmpty);
        Assert.Equal(5, charts.Days.Days.Count);
        Assert.Equal(3, charts.Days.Series.Count);
        Assert.Null(charts.Days.Note);
        Assert.True(charts.Kinds.IsEmpty);
        Assert.True(charts.ErrorMachines.IsEmpty);
        Assert.Equal(["Nazwa a", "Nazwa b"], charts.ErrorMachines.Categories);
    }

    [Fact]
    public void SingleRow_EveryChartShowsIt()
    {
        var charts = DowntimeCharts.Build([Row(Wed, "a", 4.26, reason: "Bazowanie (Home)")], Options(Mon, Fri, machines: [M("a")]));

        Assert.Equal(4.26, Assert.Single(charts.Machines.Totals), 6);
        var slice = Assert.Single(charts.Errors.Slices);
        Assert.Equal(("Bazowanie (Home)", 1.0, ChartPalette.Slot(0)), (slice.Label, slice.Share, slice.Color));
        Assert.Equal(4.26, Value(charts.Days.Series[0], Dm(Wed)), 6);
        Assert.Equal("4,3 min", charts.Days.Series[0].Points.Single(p => p.Label == Dm(Wed)).ValueText);
    }

    [Fact]
    public void ThousandRows_BuildUnder200Milliseconds()
    {
        var random = new Random(7);
        var machines = Enumerable.Range(0, 12).Select(i => M("m" + i)).ToArray();
        var days = Enumerable.Range(0, 120).Select(i => new DateOnly(2026, 6, 1).AddDays(i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
        var rows = Enumerable.Range(0, 1000).Select(i => Row(days[random.Next(days.Length)], "m" + random.Next(12),
            3 + random.NextDouble() * 80, reason: random.Next(4) == 0 ? "Tacka nie pobrana" : "Błąd " + random.Next(30),
            isLong: random.Next(10) == 0, category: random.Next(8) == 0 ? "nierozpoznany" : "inny",
            shift: new[] { S1, S2, S3 }[random.Next(3)])).ToArray();
        var observation = Enumerable.Range(0, 2000).Select(i =>
        {
            var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i * 80);
            return new ObservationInterval("m" + (i % 12), (ObservationState)(i % 3), start, start.AddMinutes(70));
        }).Where(o => o.IsValid()).ToArray();
        var options = Options(new DateOnly(2026, 6, 1), new DateOnly(2026, 9, 28), machines: machines, perMachine: true,
            now: new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), zone: TestZones.Warsaw, observation: observation);
        DowntimeCharts.Build(rows, options).ContentKey(); // rozgrzewka (JIT, Regex)

        DowntimeChartSet charts = null!;
        TimeSpan elapsed = PerformanceLimits.Measure(() => { charts = DowntimeCharts.Build(rows, options); _ = charts.ContentKey(); });
        output.WriteLine($"1000 wierszy + 2000 przedziałów: {elapsed.TotalMilliseconds:0} ms");

        PerformanceLimits.AssertUnder(elapsed, ThousandRowsLocal, ThousandRowsCi, "1000 wierszy + 2000 przedziałów obserwacji");
        Assert.Equal(rows.Sum(r => r.Minutes), charts.Machines.Totals.Sum(), 6);
    }

    [Fact]
    public void ThousandRows_WholeHistory_WithShiftFilter_Under200Milliseconds()
    {
        var days = Enumerable.Range(0, 400).Select(i => new DateOnly(2025, 8, 4).AddDays(i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
        var rows = Enumerable.Range(0, 1000).Select(i => Row(days[i % days.Length], "m" + (i % 12), 4, shift: S3)).ToArray();
        var observation = Enumerable.Range(0, 3000).Select(i =>
        {
            var start = new DateTimeOffset(2025, 8, 4, 0, 0, 0, TimeSpan.Zero).AddHours(i * 3.1);
            return new ObservationInterval("m" + (i % 12), ObservationState.NoConnection, start, start.AddHours(2.5));
        }).ToArray();
        var options = Options(shift: S3, machines: Enumerable.Range(0, 12).Select(i => M("m" + i)).ToArray(),
            now: new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), zone: TestZones.Warsaw, observation: observation);
        DowntimeCharts.Build(rows, options).ContentKey(); // rozgrzewka (JIT, Regex)

        TimeSpan elapsed = PerformanceLimits.Measure(() => DowntimeCharts.Build(rows, options).ContentKey());
        output.WriteLine($"cała historia + filtr zmiany + 3000 przedziałów: {elapsed.TotalMilliseconds:0} ms");

        PerformanceLimits.AssertUnder(elapsed, ThousandRowsLocal, ThousandRowsCi, "cała historia + filtr zmiany + 3000 przedziałów");
    }
}
