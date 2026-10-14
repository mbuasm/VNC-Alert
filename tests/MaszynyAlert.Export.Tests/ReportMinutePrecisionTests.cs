using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Export.Tests;

/// <summary>rzeczywiste bajty PDF/Excel na wspólnych danych; jawna strefa i stały czas.</summary>
public sealed class ReportMinutePrecisionTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");
    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    private static Guid Id(int index) => Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}");

    private static DowntimeExportData Create(IEnumerable<DowntimeEpisode> episodes, DateOnly from, DateOnly to,
        DateTimeOffset now)
    {
        var slices = DowntimeReport.Build(episodes, Warsaw, now: now)
            .Where(s => s.Day >= from && s.Day <= to).ToArray();
        MachineConfig[] machines =
        [new() { Id = "m580", Name = "580", Host = "127.0.0.1", Profile = "amm580" }];
        return DowntimeExportData.Create(slices, from, to, null, [], machines, Warsaw, now,
            "AUDIT-PC", "Dane lokalne tego komputera");
    }

    private static DowntimeExportData FractionalData(int count, int seconds, DateTimeOffset? first = null)
    {
        var initial = first ?? At("2026-10-05T08:00:00+02:00");
        var episodes = Enumerable.Range(0, count).Select(i =>
        {
            var start = initial.AddSeconds(i * (seconds + 1));
            var end = start.AddSeconds(seconds);
            return new DowntimeEpisode("m580", "580", "Servo 6", start, end, end, EpisodeId: Id(i), Category: "restart");
        });
        return Create(episodes, new(2026, 10, 5), new(2026, 10, 5), At("2026-10-05T18:00:00+02:00"));
    }

    private static double ParseNumber(string text) =>
        double.Parse(text.Replace('\u00a0', ' ').Replace('\u202f', ' ').Trim(), NumberStyles.Number, Pl);
    private static IReadOnlyList<string> PdfLines(PdfProbe pdf) =>
        Enumerable.Range(1, pdf.PageCount).SelectMany(p => pdf.Lines(p)).Select(l => l.Text).ToArray();
    private static string[] Cells(string line) => line.Split(" | ", StringSplitOptions.None);

    [Fact]
    public void SameWeekendData_PdfAndExcelRowSumsEqualTotals()
    {
        var start = At("2026-10-23T22:00:00+02:00");
        var end = At("2026-10-26T08:00:00+01:00");
        var data = Create([
            new("m580", "580", "Servo 6", start, end, null, EpisodeId: Id(0), Category: "restart")
        ], new(2026, 10, 23), new(2026, 10, 26), end);
        Assert.Equal(2, data.Episodes.Count);
        Assert.Equal(600, data.Totals.All.Minutes, 6);
        using var stream = new MemoryStream();
        var result = DowntimeExcelExporter.Write(data, stream);
        Assert.True(result.Success, result.Error);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Epizody");
        double rows = sheet.RowsUsed().Skip(1).Sum(r => r.Cell(7).GetDouble());
        double total = workbook.Worksheet("Sumy").RowsUsed().First(r => r.Cell(1).GetString() == "Razem").Cell(2).GetDouble();
        Assert.Equal(600, rows, 6);
        Assert.Equal(rows, total, 6);
        using var pdf = PdfProbe.Write(data);
        var lines = PdfLines(pdf);
        var pdfRows = lines.Where(l => Regex.IsMatch(l, @"^(23|26)\.10\.2026 \| ")).ToArray();
        Assert.Equal(2, pdfRows.Length);
        Assert.Equal(rows, pdfRows.Sum(l => ParseNumber(Cells(l)[5])), 6);
        var pdfTotal = lines.Single(l => l.StartsWith("Razem (z długimi postojami) | ", StringComparison.Ordinal));
        Assert.Equal(rows, ParseNumber(Cells(pdfTotal)[1]), 6);
    }

    [Theory]
    [InlineData(10, 4, -0.3)]
    [InlineData(10, 2, 0.3)]
    [InlineData(10, 6, 0)]
    [InlineData(10, 3, -0.5)]
    [InlineData(1000, 4, -33.3)]
    [InlineData(1000, 1, 16.7)]
    public void PdfFullTable_VisibleRowsPlusExplicitRoundingDifferenceEqualTrueTotal(
        int count, int seconds, double expectedDifference)
    {
        var data = FractionalData(count, seconds);
        using var pdf = PdfProbe.Write(data);
        var lines = PdfLines(pdf);
        var rows = lines.Where(l => l.StartsWith("05.10.2026 | ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(count, rows.Length);
        double visibleRows = rows.Sum(l => ParseNumber(Cells(l)[5]));
        var total = lines.Single(l => l.StartsWith("Razem (z długimi postojami) | ", StringComparison.Ordinal));
        double printedTotal = ParseNumber(Cells(total)[1]);
        Assert.Equal(ParseNumber(data.Totals.All.Minutes.ToString("N1", Pl)), printedTotal, 6);
        Assert.Equal(data.Episodes.Sum(e => e.Minutes), data.Totals.All.Minutes, 10);
        Assert.All(rows, row => Assert.Equal(ParseNumber(data.Episodes[0].Minutes.ToString("N1", Pl)), ParseNumber(Cells(row)[5]), 6));
        var differences = lines.Where(l => l.StartsWith("Różnica zaokrągleń | ", StringComparison.Ordinal)).ToArray();
        double difference = expectedDifference == 0 ? 0 : ParseNumber(Cells(Assert.Single(differences))[1]);
        if (expectedDifference == 0) Assert.Empty(differences);
        else Assert.Contains("Łączny czas obliczono przed zaokrągleniem wierszy.", pdf.AllText(), StringComparison.Ordinal);
        Assert.Equal(expectedDifference, difference, 6);
        Assert.Equal(printedTotal, visibleRows + difference, 6);
        Assert.DoesNotContain("najnowszych z", pdf.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PdfTruncatedTable_OnlyNewestThousandRows_TotalStillIncludesAll_NotARoundingDifference()
    {
        var data = FractionalData(1001, 4, At("2026-10-05T07:59:55+02:00"));
        using var pdf = PdfProbe.Write(data);
        var lines = PdfLines(pdf);
        var rows = lines.Where(l => l.StartsWith("05.10.2026 | ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(1000, rows.Length);
        Assert.DoesNotContain("Różnica zaokrągleń", pdf.AllText(), StringComparison.Ordinal);
        Assert.Contains("1000 najnowszych z 1001", pdf.AllText(), StringComparison.Ordinal);
        Assert.Contains("sumy, wykresy i wiersz „Razem” obejmują wszystkie postoje.", pdf.AllText(), StringComparison.Ordinal);
        var total = lines.Single(l => l.StartsWith("Razem (z długimi postojami) | ", StringComparison.Ordinal));
        Assert.Equal(ParseNumber(data.Totals.All.Minutes.ToString("N1", Pl)), ParseNumber(Cells(total)[1]), 6);
        Assert.Equal(data.Episodes[1].Start.ToString("dd.MM.yyyy HH:mm", Pl), Cells(rows[0])[3]);
        Assert.DoesNotContain(rows, row => Cells(row)[3] == data.Episodes[0].Start.ToString("dd.MM.yyyy HH:mm", Pl));
        Assert.Equal(data.Episodes[^1].Start.ToString("dd.MM.yyyy HH:mm", Pl), Cells(rows[^1])[3]);
    }

    [Fact]
    public void EmptyPdf_HasNoRoundingDifference()
    {
        using var pdf = PdfProbe.Write(FractionalData(0, 1));
        Assert.Contains("W wybranym okresie i zmianie nie ma postojów.", pdf.AllText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Różnica zaokrągleń", pdf.AllText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 1)]
    [InlineData(10, 4)]
    public void Excel_NumericCellsRetainMinutes_AndTwoDecimalDisplay(int count, int seconds)
    {
        var data = FractionalData(count, seconds);
        using var stream = new MemoryStream();
        var result = DowntimeExcelExporter.Write(data, stream);
        Assert.True(result.Success, result.Error);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var rows = workbook.Worksheet("Epizody").RowsUsed().Skip(1).ToArray();
        Assert.Equal(count, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(data.Episodes[i].Minutes, rows[i].Cell(7).GetDouble(), 12);
            Assert.Equal("0.00", rows[i].Cell(7).Style.NumberFormat.Format);
        }
        double sum = rows.Sum(r => r.Cell(7).GetDouble());
        var total = workbook.Worksheet("Sumy").RowsUsed().First(r => r.Cell(1).GetString() == "Razem").Cell(2);
        Assert.Equal(data.Totals.All.Minutes, total.GetDouble(), 10);
        Assert.Equal(sum, total.GetDouble(), 10);
        Assert.Equal("0.00", total.Style.NumberFormat.Format);
        Assert.Equal(sum, data.Charts.Machines.Totals.Sum(), 10);
    }
    [Fact]
    public void ExcelStoredMinutes_TotalEqualsSumOfStoredShortRows()
    {
        // 1000 legalnych jednosekundowych epizodów. Arkusz przechowuje minuty zaokrąglone do4 miejsc.
        var data = FractionalData(1000, 1);
        using var stream = new MemoryStream();
        var result = DowntimeExcelExporter.Write(data, stream);
        Assert.True(result.Success, result.Error);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var episodes = workbook.Worksheet("Epizody").RowsUsed().Skip(1).ToArray();
        Assert.Equal(1000, episodes.Length);
        double sum = episodes.Sum(r => r.Cell(7).GetDouble());
        double total = workbook.Worksheet("Sumy").RowsUsed().First(r => r.Cell(1).GetString() == "Razem").Cell(2).GetDouble();
        // Porównanie liczb zapisanych, nie samego formatu komórek0.00.
        Assert.Equal(total, sum, 4);
    }
}

