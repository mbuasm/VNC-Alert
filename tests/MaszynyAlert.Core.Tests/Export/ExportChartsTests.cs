using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.Export;

public sealed class ExportChartsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");

    private static DowntimeSlice Slice(string machine, string reason, DateOnly day, double minutes, bool isLong = false,
        string category = "inny")
    {
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);
        return new DowntimeSlice(day, "06:00–14:00", machine.ToUpperInvariant(), reason, start, start.AddMinutes(minutes), false, false)
        {
            MachineId = machine, IsLong = isLong, Category = category, EpisodeId = Guid.NewGuid(),
        };
    }

    private static DowntimeSlice[] Errors(params double[] minutes) =>
        minutes.Select((m, i) => Slice("a1", $"Błąd {i + 1}", new DateOnly(2026, 9, 28).AddDays(i % 5), m))
            .Where(s => s.Minutes > 0).ToArray();

    private static DowntimeChartSet Charts(IReadOnlyList<DowntimeSlice> slices, DateOnly from, DateOnly to,
        IReadOnlyList<MachineConfig>? machines = null) =>
        DowntimeExportData.Create(slices, from, to, null, [], machines ?? [], Utc, Now, "PC", "Dane").Charts;

    private static readonly DateOnly Mon = new(2026, 9, 28);
    private static readonly DateOnly Fri = new(2026, 10, 2);

    [Fact]
    public void Kolowy_NadmiarNazw_PozostaleZSumaResztyISzare()
    {
        var pie = Charts(Errors(90, 80, 70, 60, 50, 40, 30, 20, 10), Mon, Fri).Errors;
        int named = DowntimeCharts.MaxPieNames;

        Assert.Equal(named + 1, pie.Slices.Count);
        Assert.Equal(Enumerable.Range(1, named).Select(i => $"Błąd {i}"), pie.Slices.Take(named).Select(s => s.Label));
        var rest = pie.Slices[^1];
        Assert.Equal(PieSliceKind.Remainder, rest.Kind);
        Assert.StartsWith(DowntimePresentation.RemainderName, rest.Label, StringComparison.Ordinal);
        Assert.Equal(new[] { 90.0, 80, 70, 60, 50, 40, 30, 20, 10 }.Skip(named).Sum(), rest.Value, 6);
        Assert.Equal(ChartPalette.Remainder, rest.Color);
        Assert.Equal(450, pie.Slices.Sum(s => s.Value), 6);
    }

    [Fact]
    public void Kolowy_JednaNazwaPonadLimit_BezPozostale()
    {
        var pie = Charts(Errors(Enumerable.Range(0, DowntimeCharts.MaxPieNames + 1).Select(i => 90.0 - i * 10).ToArray()), Mon, Fri).Errors;
        Assert.Equal(DowntimeCharts.MaxPieNames + 1, pie.Slices.Count);
        Assert.DoesNotContain(pie.Slices, s => s.Kind == PieSliceKind.Remainder);
    }

    [Fact]
    public void Kolowy_KazdyWycinekMaInnyKolor()
    {
        foreach (int count in new[] { 3, 6, 7, 8, 9, 15 })
        {
            var slices = Errors(Enumerable.Range(1, count).Select(i => 100.0 - i).ToArray()).ToList();
            slices.Add(Slice("a2", "Coś z OCR", Mon, 12, category: DowntimeCategories.Unrecognized));
            var pie = Charts(slices, Mon, Fri).Errors;
            Assert.Equal(pie.Slices.Count, pie.Slices.Select(s => s.Color.LightArgb).Distinct().Count());
        }
    }

    [Fact]
    public void Kolowy_PozycjeZerowePominiete_KolejnoscMalejaca()
    {
        var pie = Charts(Errors(0, 5, 50, 0, 10), Mon, Fri).Errors;
        Assert.Equal(["Błąd 3", "Błąd 5", "Błąd 2"], pie.Slices.Select(s => s.Label));
        Assert.True(Charts([], Mon, Fri).Errors.IsEmpty);
    }

    [Fact]
    public void Kolowy_NierozpoznanyZawszeOsobno_NigdyWPozostale()
    {
        var slices = Errors(90, 80, 70, 60, 50, 40, 30, 20, 10).ToList();
        slices.Add(Slice("a2", "Coś z OCR", Mon, 1, category: DowntimeCategories.Unrecognized));
        var pie = Charts(slices, Mon, Fri).Errors;
        var unrecognized = Assert.Single(pie.Slices, s => s.Kind == PieSliceKind.Unrecognized);
        Assert.Equal(DowntimePresentation.UnrecognizedName, unrecognized.Label);
        Assert.Equal(1, unrecognized.Value, 6);
        Assert.Equal(451, pie.Slices.Sum(s => s.Value), 6);
    }

    [Fact]
    public void Dzienny_DniRoboczeZakresu_WeekendPominiety_ZeraDlaDniBezPostoju()
    {
        DowntimeSlice[] slices =
        [
            Slice("a1", "Servo 6", new DateOnly(2026, 9, 29), 20), Slice("a1", "Servo 6", new DateOnly(2026, 9, 29), 10),
            Slice("a2", "Servo 6", new DateOnly(2026, 9, 29), 75, isLong: true),
            Slice("a1", "Servo 6", new DateOnly(2026, 10, 2), 5),
        ];
        var days = Charts(slices, Mon, new DateOnly(2026, 10, 6)).Days;

        Assert.Equal(3, days.Series.Count);
        Assert.Equal(["28.09", "29.09", "30.09", "01.10", "02.10", "05.10", "06.10"], days.Series[0].Points.Select(p => p.Label));
        Assert.Equal([0, 105, 0, 0, 5, 0, 0], days.Series[0].Points.Select(p => p.Value));
        Assert.Equal([0, 30, 0, 0, 5, 0, 0], days.Series[1].Points.Select(p => p.Value));
        Assert.Equal(DowntimeCharts.AllName, days.Series[0].Name);
        Assert.Equal(DowntimeCharts.WithoutLongName, days.Series[1].Name);
    }

    [Fact]
    public void Dzienny_SamWeekend_PustyWykres()
    {
        var days = Charts([], new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 4)).Days;
        Assert.True(days.IsEmpty);
        Assert.Empty(days.Days);
        Assert.Empty(days.Series[0].Points);
    }

    [Fact]
    public void Dzienny_BardzoDlugiZakres_NajnowszeDni()
    {
        var to = new DateOnly(2026, 10, 6);
        DowntimeSlice[] slices = [Slice("a1", "Servo 6", to, 7), Slice("a1", "Servo 6", new DateOnly(2020, 1, 7), 9)];
        var days = Charts(slices, new DateOnly(2020, 1, 1), to).Days;

        Assert.Equal(DowntimeCharts.MaxLineDays, days.Series[0].Points.Count);
        Assert.Equal("06.10", days.Series[0].Points[^1].Label);
        Assert.Equal(7, days.Series[0].Points[^1].Value);
        Assert.Equal(7, days.Series[0].Points.Sum(p => p.Value)); // 2020 poza oknem wykresu
        Assert.NotNull(days.Note);
    }

    [Fact]
    public void Slupkowy_WedlugMaszynyMalejaco_DwieSerieWRoznychKolorach_MaszynaBezPostojuZZerem()
    {
        DowntimeSlice[] slices =
        [
            Slice("a1", "Servo 6", Mon, 10), Slice("a2", "Servo 6", Mon, 5), Slice("a2", "Servo 6", Mon.AddDays(1), 75, isLong: true),
        ];
        MachineConfig[] machines =
        [
            new() { Id = "a1", Name = "A1", Host = "h", Profile = "p" }, new() { Id = "a2", Name = "A2", Host = "h", Profile = "p" },
            new() { Id = "a3", Name = "A3", Host = "h", Profile = "p" },
        ];
        var bar = Charts(slices, Mon, Fri, machines).Machines;

        Assert.Equal(["A2", "A1", "A3"], bar.Categories);
        Assert.Equal([80, 10, 0], bar.Totals);
        Assert.Equal([DowntimeCharts.ErrorsName, DowntimeCharts.IdleName, DowntimeCharts.IdleAtZeroName], bar.Series.Select(s => s.Name));
        Assert.Equal([80, 10, 0], bar.Series[0].Points.Select(p => p.Value));     // błędy maszyn
        Assert.Equal([75, 0, 0], bar.Series[0].Points.Select(p => p.LongValue));  // w tym długie
        Assert.Equal([0, 0, 0], bar.Series[1].Points.Select(p => p.Value));
        Assert.NotEqual(bar.Series[0].Color.LightArgb, bar.Series[1].Color.LightArgb);
        Assert.NotEqual(bar.Series[1].Color.LightArgb, bar.Series[2].Color.LightArgb);
    }

    [Fact]
    public void Paleta_RozneKolory_PozostaleINierozpoznanySzareSpozaPalety()
    {
        var slots = Enumerable.Range(0, ChartPalette.SlotCount).Select(i => ChartPalette.Slot(i).LightArgb).ToArray();
        Assert.Equal(slots.Length, slots.Distinct().Count());
        Assert.DoesNotContain(ChartPalette.Remainder.LightArgb, slots);
        Assert.DoesNotContain(ChartPalette.Unrecognized.LightArgb, slots);
        Assert.All(slots, c => Assert.Equal(0xFFu, c >> 24));
    }

    [Fact]
    public void IsEmpty_SameZera()
    {
        var empty = Charts([], Mon, Fri);
        Assert.True(empty.Machines.IsEmpty);
        Assert.True(empty.Errors.IsEmpty);
        Assert.True(empty.Days.IsEmpty);
        var some = Charts([Slice("a1", "Servo 6", Mon, 4)], Mon, Fri);
        Assert.False(some.Machines.IsEmpty);
        Assert.False(some.Errors.IsEmpty);
        Assert.False(some.Days.IsEmpty);
    }
}
