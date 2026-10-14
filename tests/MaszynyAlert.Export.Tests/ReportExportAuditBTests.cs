using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Export.Tests;

/// <summary>rzeczywiste bajty PDF/Excel na wspólnych danych; jawna strefa i stały czas.</summary>
public sealed class ReportExportAuditBTests
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

    private static DowntimeExportData FractionalData(int count, int seconds)
    {
        var first = At("2026-10-05T08:00:00+02:00");
        var episodes = Enumerable.Range(0, count).Select(i =>
        {
            var start = first.AddSeconds(i * (seconds + 1));
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

