using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeChartsKindSplitTests
{
    private static readonly DateOnly Mon = new(2026, 9, 14);
    private static readonly DateOnly Tue = new(2026, 9, 15);
    private static readonly DateOnly Wed = new(2026, 9, 16);
    private static readonly DateOnly Fri = new(2026, 9, 18);
    private static readonly DateTimeOffset Noon = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static DowntimeSlice Row(DateOnly day, string machineId, double minutes, string reason = "Servo 6",
        string category = "inny", bool isLong = false, string shift = "06:00–14:00")
    {
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);
        return new DowntimeSlice(day, shift, "Nazwa " + machineId, reason, start, start.AddMinutes(minutes), false, false)
        {
            IsLong = isLong, Category = category, MachineId = machineId, EpisodeId = Guid.NewGuid(),
        };
    }

    private static DowntimeSlice Idle(DateOnly day, string machineId, double minutes, bool isLong = false) =>
        Row(day, machineId, minutes, AlertStateMachine.MachineIdleShortText, DowntimeCategories.Idle, isLong);

    private static DowntimeSlice IdleAtZero(DateOnly day, string machineId, double minutes, bool isLong = false) =>
        Row(day, machineId, minutes, AlertStateMachine.MachineIdleAtZeroShortText, DowntimeCategories.IdleAtZero, isLong);

    private static DowntimeChartOptions Options(bool includeIdle = true, bool perMachine = false, params string[] machines) => new()
    {
        From = Mon, To = Fri, Machines = machines.Select(m => new ChartMachine(m, "Nazwa " + m)).ToArray(),
        IncludeIdle = includeIdle, LinePerMachine = perMachine, GeneratedAt = Noon, Zone = TimeZoneInfo.Utc,
    };

    private static DowntimeSlice[] HallRows() =>
    [
        Idle(Mon, "a", 600, isLong: true), Idle(Tue, "a", 300), Idle(Wed, "b", 500), Idle(Fri, "c", 400),
        IdleAtZero(Tue, "b", 60), IdleAtZero(Wed, "c", 40),
        Row(Mon, "a", 61, "Servo 6", "restart", isLong: true), Row(Tue, "b", 10, "Tacka nie pobrana", "tacka"),
        Row(Tue, "b", 8, "Bazowanie (Home)", "restart"), Row(Wed, "c", 6, "Ciśnienie powietrza", "inny"),
        Row(Wed, "a", 5, "Drzwi otwarte", "inny"), Row(Fri, "c", 4, "Wrzeciono", "narzedzie"),
        Row(Fri, "b", 3, "Stop awaryjny", "inny"), Row(Fri, "a", 3, "Tekst sprzed słownika", DowntimeCategories.Unrecognized),
    ];

    // ── Rodzaj wiersza i sumy ByKind ───────────────────────────────────────────────────────────

    [Fact]
    public void Kind_PostojBezPracyPoKategorii_ResztaToBladMaszyny()
    {
        Assert.Equal(DowntimeKind.Idle, Idle(Mon, "a", 5).Kind);
        Assert.Equal(DowntimeKind.IdleAtZero, IdleAtZero(Mon, "a", 5).Kind);
        Assert.Equal(DowntimeKind.Error, Row(Mon, "a", 5).Kind);
        Assert.Equal(DowntimeKind.Error, Row(Mon, "a", 5, category: DowntimeCategories.Unrecognized).Kind);
        Assert.Equal(DowntimeKind.Error, Row(Mon, "a", 5, category: "").Kind);
        Assert.True(Idle(Mon, "a", 5).IsIdle);
        Assert.False(Row(Mon, "a", 5).IsIdle);
        Assert.Equal(DowntimeKind.Idle, DowntimeCategories.KindOf(" POSTOJ "));
        Assert.Equal(DowntimeKind.Error, DowntimeCategories.KindOf(null));
    }

    [Fact]
    public void ByKind_SumaRodzajowRownaSieSumieCalosci_DlugieTylkoWSwoimRodzaju()
    {
        var rows = HallRows();
        var totals = DowntimeReport.Totals(rows);
        var kinds = DowntimeReport.ByKind(rows);

        Assert.Equal(100, kinds.Errors.All.Minutes, 6);
        Assert.Equal(1800, kinds.Idle.All.Minutes, 6);
        Assert.Equal(100, kinds.IdleAtZero.All.Minutes, 6);
        Assert.Equal(1900, kinds.IdleMinutes, 6);
        Assert.Equal(totals.All.Minutes, kinds.Errors.All.Minutes + kinds.IdleMinutes, 6);
        Assert.Equal(totals.All.Episodes, kinds.Errors.All.Episodes + kinds.IdleEpisodes);
        Assert.Equal(8, kinds.Errors.All.Episodes);
        Assert.Equal(6, kinds.IdleEpisodes);
        // Długie: 61 min błędu i 600 min bez pracy — każdy w swoim rodzaju.
        Assert.Equal(39, kinds.Errors.WithoutLong.Minutes, 6);
        Assert.Equal(1200, kinds.Idle.WithoutLong.Minutes, 6);
        Assert.Equal(totals.WithoutLong.Minutes,
            kinds.Errors.WithoutLong.Minutes + kinds.Idle.WithoutLong.Minutes + kinds.IdleAtZero.WithoutLong.Minutes, 6);
        Assert.Equal(new DowntimeTotal(0, 0), DowntimeReport.ByKind([]).Errors.All);
    }

    // ── Słupki według maszyny: części = rodzaje, kreskowanie = długie ─────────────────────────

    [Fact]
    public void Machines_CzesciSlupkaToRodzaje_DlugiePostojeWLongValue_SumyZgodneZTabela()
    {
        var charts = DowntimeCharts.Build(HallRows(), Options(machines: ["a", "b", "c"]));
        var bar = charts.Machines;

        Assert.Equal([DowntimeCharts.ErrorsName, "Bez pracy (100 %)", "Stoi na 0 %"], bar.Series.Select(s => s.Name));
        Assert.Equal(ChartPalette.Errors, bar.Series[0].Color);
        Assert.Equal(ChartPalette.Idle, bar.Series[1].Color);
        Assert.Equal(ChartPalette.IdleAtZero, bar.Series[2].Color);
        // Malejąco po sumie: a = 969, b = 581, c = 450.
        Assert.Equal(["Nazwa a", "Nazwa b", "Nazwa c"], bar.Categories);
        Assert.Equal([969, 581, 450], bar.Totals.Select(t => Math.Round(t, 6)));
        var kinds = DowntimeReport.ByKind(HallRows());
        Assert.Equal(kinds.Errors.All.Minutes, bar.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Equal(kinds.Idle.All.Minutes, bar.Series[1].Points.Sum(p => p.Value), 6);
        Assert.Equal(kinds.IdleAtZero.All.Minutes, bar.Series[2].Points.Sum(p => p.Value), 6);
        // Długie: błąd 61 min i postój 600 min maszyny „a” — w LongValue swojej części, nigdzie indziej.
        var a = bar.Categories.ToList().IndexOf("Nazwa a");
        Assert.Equal(61, bar.Series[0].Points[a].LongValue, 6);
        Assert.Equal(600, bar.Series[1].Points[a].LongValue, 6);
        Assert.Equal(0, bar.Series[2].Points[a].LongValue);
        Assert.True(bar.HasLong);
        Assert.Equal(0, bar.Series[0].Points[bar.Categories.ToList().IndexOf("Nazwa b")].LongValue);
        Assert.Contains("długie postoje (> 1 h)", bar.Subtitle);
    }

    [Fact]
    public void Machines_BezPostojowBezPracy_TylkoSeriaBledowWPelnejSkali()
    {
        var charts = DowntimeCharts.Build(HallRows(), Options(includeIdle: false, machines: ["a", "b", "c"]));
        var bar = charts.Machines;

        Assert.Equal([DowntimeCharts.ErrorsName], bar.Series.Select(s => s.Name));
        Assert.Equal(["Nazwa a", "Nazwa b", "Nazwa c"], bar.Categories); // a = 69, b = 21, c = 10
        Assert.Equal([69, 21, 10], bar.Totals.Select(t => Math.Round(t, 6)));
        Assert.Equal("Minuty błędów maszyn", bar.ValueAxisTitle);
        Assert.Contains("ukryte", bar.Subtitle);
        // Wykres rodzajów nadal z postojami bez pracy, wykresy błędów bez zmian.
        Assert.Equal(3, charts.Kinds.Slices.Count);
        var on = DowntimeCharts.Build(HallRows(), Options(machines: ["a", "b", "c"]));
        Assert.Equal(on.Errors.Slices.Select(s => (s.Label, s.Value)), charts.Errors.Slices.Select(s => (s.Label, s.Value)));
        Assert.Equal(on.ErrorMachines.Totals, charts.ErrorMachines.Totals);
    }

    [Fact]
    public void Machines_SamePostojeBezPracyIWylaczonyPrzelacznik_PustyNapisOBledach()
    {
        var rows = new[] { Idle(Mon, "a", 30), IdleAtZero(Tue, "b", 20) };
        var on = DowntimeCharts.Build(rows, Options(machines: ["a", "b"]));
        var off = DowntimeCharts.Build(rows, Options(includeIdle: false, machines: ["a", "b"]));

        Assert.False(on.Machines.IsEmpty);
        Assert.True(off.Machines.IsEmpty);
        Assert.Contains("nie ma błędów maszyn", off.Machines.EmptyText);
        Assert.Contains("nie ma postojów", on.Machines.EmptyText);
    }

    // ── Kołowy według rodzaju ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Kinds_TrzyWycinki_UdzialyZCalosci_ZeroPominiete()
    {
        var pie = DowntimeCharts.Build(HallRows(), Options()).Kinds;

        Assert.Equal("Czas postojów według rodzaju", pie.Title);
        Assert.Equal([DowntimeCharts.ErrorsName, "Bez pracy (100 %)", "Stoi na 0 %"], pie.Slices.Select(s => s.Label));
        Assert.Equal(2000, pie.Total, 6);
        Assert.Equal(0.05, pie.Slices[0].Share, 6);
        Assert.Equal("5,0 %", pie.Slices[0].ShareText);
        Assert.Equal("100,0 min", pie.Slices[0].ValueText);
        Assert.Equal(0.9, pie.Slices[1].Share, 6);
        Assert.Equal(0.05, pie.Slices[2].Share, 6);
        Assert.Equal([ChartPalette.Errors, ChartPalette.Idle, ChartPalette.IdleAtZero], pie.Slices.Select(s => s.Color));

        var onlyErrors = DowntimeCharts.Build([Row(Mon, "a", 5)], Options()).Kinds;
        Assert.Equal([DowntimeCharts.ErrorsName], onlyErrors.Slices.Select(s => s.Label));
        Assert.Equal(1, onlyErrors.Slices[0].Share, 6);
        Assert.True(DowntimeCharts.Build([], Options()).Kinds.IsEmpty);
    }

    // ── Kołowy błędów według nazwy: bez postojów bez pracy, pełne udziały ─────────────────────

    [Fact]
    public void Errors_BezPostojowBezPracy_UdzialyZCzasuBledow_PozostaleINierozpoznany()
    {
        var pie = DowntimeCharts.Build(HallRows(), Options()).Errors;

        Assert.Equal("Błędy maszyn według nazwy", pie.Title);
        Assert.Equal(100, pie.Total, 6);
        Assert.DoesNotContain(pie.Slices, s => s.Label.Contains("Stoi", StringComparison.Ordinal));
        // 7 nazw + nierozpoznany: 6 własnych, „Nierozpoznany”, „Pozostałe (1 nazwa)”? Nie — jedna nazwa ponad limit idzie wprost.
        Assert.Equal(["Servo 6", "Ramka niepobrana", "Bazowanie (Home)", "Ciśnienie powietrza", "Drzwi otwarte", "Wrzeciono", "Stop awaryjny",
            DowntimePresentation.UnrecognizedName], pie.Slices.Select(s => s.Label));
        Assert.Equal(0.61, pie.Slices[0].Share, 6);
        Assert.Equal("61,0 %", pie.Slices[0].ShareText);
        Assert.Equal(0.03, pie.Slices[^1].Share, 6);
        Assert.Equal(PieSliceKind.Unrecognized, pie.Slices[^1].Kind);
        Assert.Equal(1, pie.Slices.Sum(s => s.Share), 6);
        for (int i = 0; i < 7; i++) Assert.Equal(ChartPalette.Slot(i), pie.Slices[i].Color);
    }

    [Fact]
    public void Errors_NadmiarNazw_PozostaleZbieraReszte_SlupkiBledowMajaTeSameSerie()
    {
        var rows = Enumerable.Range(1, 9).Select(i => Row(Mon, i % 2 == 0 ? "a" : "b", 100 - i * 10, $"Błąd {i:00}")).ToList();
        rows.Add(Idle(Mon, "a", 5000));
        var charts = DowntimeCharts.Build(rows, Options(machines: ["a", "b"]));
        var pie = charts.Errors;

        Assert.Equal(DowntimeCharts.MaxPieNames + 1, pie.Slices.Count);
        Assert.Equal("Pozostałe (3 nazwy)", pie.Slices[^1].Label);
        Assert.Equal(30 + 20 + 10, pie.Slices[^1].Value, 6);
        Assert.Equal(450, pie.Total, 6);

        var bar = charts.ErrorMachines;
        Assert.Equal(pie.Slices.Select(s => s.Label), bar.Series.Select(s => s.Name));
        Assert.Equal(pie.Slices.Select(s => s.Color), bar.Series.Select(s => s.Color));
        // Każda seria słupków sumuje się do swojego wycinka; „Pozostałe” rozpisane na maszyny.
        for (int i = 0; i < pie.Slices.Count; i++) Assert.Equal(pie.Slices[i].Value, bar.Series[i].Points.Sum(p => p.Value), 6);
        Assert.Equal(["Nazwa b", "Nazwa a"], bar.Categories); // b = 90+70+50+30+10 = 250, a = 200
        Assert.Equal([250, 200], bar.Totals.Select(t => Math.Round(t, 6)));
        Assert.Equal(pie.Total, bar.Totals.Sum(), 6);
    }

    [Fact]
    public void Errors_BrakBledow_PusteZNapisemOBledach_SlupkiBledowPuste()
    {
        var charts = DowntimeCharts.Build([Idle(Mon, "a", 30), IdleAtZero(Tue, "a", 10)], Options(machines: ["a"]));

        Assert.True(charts.Errors.IsEmpty);
        Assert.Contains("nie ma błędów maszyn", charts.Errors.EmptyText);
        Assert.True(charts.ErrorMachines.IsEmpty);
        Assert.Empty(charts.ErrorMachines.Series);
        Assert.Equal(["Nazwa a"], charts.ErrorMachines.Categories);
        Assert.Contains("nie ma błędów maszyn", charts.ErrorMachines.EmptyText);
        Assert.False(charts.Machines.IsEmpty);
        Assert.False(charts.Kinds.IsEmpty);
    }

    // ── Słupki błędów według maszyny ──────────────────────────────────────────────────────────

    [Fact]
    public void ErrorMachines_BezPostojowBezPracy_MaszynaZKonfiguracjiBezBledowMaZero_DlugieWLongValue()
    {
        var bar = DowntimeCharts.Build(HallRows(), Options(machines: ["a", "b", "c", "d"])).ErrorMachines;

        Assert.Equal("Błędy maszyn według maszyny", bar.Title);
        Assert.Equal(["Nazwa a", "Nazwa b", "Nazwa c", "Nazwa d"], bar.Categories);
        Assert.Equal([69, 21, 10, 0], bar.Totals.Select(t => Math.Round(t, 6)));
        Assert.Equal(["69,0 min", "21,0 min", "10,0 min", "0,0 min"], bar.TotalTexts);
        var servo = bar.Series.Single(s => s.Name == "Servo 6");
        Assert.Equal(61, servo.Points[0].Value, 6);
        Assert.Equal(61, servo.Points[0].LongValue, 6);
        Assert.True(bar.HasLong);
        Assert.Equal(DowntimeReport.ByKind(HallRows()).Errors.All.Minutes, bar.Totals.Sum(), 6);
    }

    // ── Linia dzienna: trzecia seria „Błędy maszyn”; bez postojów bez pracy — same błędy ──────

    [Fact]
    public void Days_TrzeciaSeriaBledow_SumyZgodneZTabela_OsBezZmian()
    {
        var charts = DowntimeCharts.Build(HallRows(), Options());
        var line = charts.Days;
        var totals = DowntimeReport.Totals(HallRows());
        var kinds = DowntimeReport.ByKind(HallRows());

        Assert.Equal([DowntimeCharts.AllName, DowntimeCharts.WithoutLongName, DowntimeCharts.ErrorsName], line.Series.Select(s => s.Name));
        Assert.Equal([Mon, Tue, Wed, new(2026, 9, 17), Fri], line.Days);
        Assert.Equal(totals.All.Minutes, line.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Equal(totals.WithoutLong.Minutes, line.Series[1].Points.Sum(p => p.Value), 6);
        Assert.Equal(kinds.Errors.All.Minutes, line.Series[2].Points.Sum(p => p.Value), 6);
        Assert.Equal(61, line.Series[2].Points[0].Value, 6);
        Assert.Equal(ChartPalette.Errors, line.Series[2].Color);
        Assert.Equal(ChartPalette.Slot(2), line.Series[0].Color);
        Assert.Equal(ChartPalette.Slot(1), line.Series[1].Color);
        Assert.Contains("same błędy maszyn", line.Subtitle);

        var off = DowntimeCharts.Build(HallRows(), Options(includeIdle: false)).Days;
        Assert.Equal([DowntimeCharts.ErrorsName, DowntimeCharts.ErrorsWithoutLongName], off.Series.Select(s => s.Name));
        Assert.Equal(kinds.Errors.All.Minutes, off.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Equal(kinds.Errors.WithoutLong.Minutes, off.Series[1].Points.Sum(p => p.Value), 6);
        Assert.Equal("Minuty błędów maszyn na dzień", off.Title);
        Assert.Equal(line.Days, off.Days);
    }

    [Fact]
    public void Days_OsobnoDlaKazdejMaszyny_PrzelacznikWybieraWiersze()
    {
        var on = DowntimeCharts.Build(HallRows(), Options(perMachine: true, machines: ["a", "b", "c"])).Days;
        var off = DowntimeCharts.Build(HallRows(), Options(includeIdle: false, perMachine: true, machines: ["a", "b", "c"])).Days;

        Assert.Equal(["Nazwa a", "Nazwa b", "Nazwa c"], on.Series.Select(s => s.Name));
        Assert.Equal(969, on.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Equal(["Nazwa a", "Nazwa b", "Nazwa c"], off.Series.Select(s => s.Name));
        Assert.Equal(69, off.Series[0].Points.Sum(p => p.Value), 6);
        Assert.Contains("same błędy maszyn", off.Subtitle);
    }

    // ── Klucz treści: przełącznik zmienia klucz, nowe wykresy w kluczu ────────────────────────

    [Fact]
    public void ContentKey_ZmieniaSiePoPrzelacznikuIPoZmianieBledowLubRodzaju()
    {
        var rows = HallRows();
        string on = DowntimeCharts.Build(rows, Options(machines: ["a", "b", "c"])).ContentKey();
        string off = DowntimeCharts.Build(rows, Options(includeIdle: false, machines: ["a", "b", "c"])).ContentKey();
        string again = DowntimeCharts.Build(rows, Options(machines: ["a", "b", "c"]) with { GeneratedAt = Noon.AddMinutes(7) }).ContentKey();

        Assert.NotEqual(on, off);
        Assert.Equal(on, again);
        // Nowy błąd (zmienia kołowy błędów i słupki błędów) i nowy postój bez pracy (zmienia rodzaje) — oba zmieniają klucz.
        Assert.NotEqual(on, DowntimeCharts.Build([.. rows, Row(Wed, "c", 2, "Nowy błąd")], Options(machines: ["a", "b", "c"])).ContentKey());
        Assert.NotEqual(on, DowntimeCharts.Build([.. rows, Idle(Wed, "c", 2)], Options(machines: ["a", "b", "c"])).ContentKey());
        // Zmiana samego podziału na długie (ta sama suma) też zmienia klucz — kreskowanie ma się przerysować.
        var withLong = rows.Select(r => r.Reason == "Drzwi otwarte" ? r with { IsLong = true } : r).ToArray();
        Assert.NotEqual(on, DowntimeCharts.Build(withLong, Options(machines: ["a", "b", "c"])).ContentKey());
        Assert.Contains("Czas postojów według rodzaju", on);
        Assert.Contains("Błędy maszyn według maszyny", on);
    }

    // ── Nazwy rodzajów = kolumna „Grupa błędu” ───────────────────────────────────────────────

    [Fact]
    public void NazwyRodzajow_TeSameCoGrupaBleduWTabeli()
    {
        Assert.Equal(DowntimePresentation.CategoryName(DowntimeCategories.Idle), DowntimeCharts.IdleName);
        Assert.Equal(DowntimePresentation.CategoryName(DowntimeCategories.IdleAtZero), DowntimeCharts.IdleAtZeroName);
        Assert.Equal("Bez pracy (100 %)", DowntimeCharts.IdleName);
        Assert.Equal("Stoi na 0 %", DowntimeCharts.IdleAtZeroName);
    }
}
