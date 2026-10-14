using System.Diagnostics;
using MaszynyAlert.Core.Export;
using Xunit.Abstractions;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

[Collection(GlobalFontStateCollection.Name)]
public sealed class PdfExportPerformanceTests(ITestOutputHelper output)
{
    /// <summary>Limit na komputerze deweloperskim (lokalnie ~1–2 s).</summary>
    public static readonly TimeSpan LocalLimit = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan CiLimit = TimeSpan.FromSeconds(30);

    /// <summary>Limit dla bieżącego środowiska: CI po zmiennych <c>GITHUB_ACTIONS</c> / <c>CI</c> (runner GitHuba ustawia obie).</summary>
    public static TimeSpan Limit =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            ? LocalLimit : CiLimit;

    [Fact]
    public void PiecTysiecyWierszy_PdfIExcelPonizej5SekundIRozsadnyRozmiar()
    {
        var start = At("2026-09-28T06:00:00Z");
        // 25 maszyn × 200 postojów po 4 min (pn–pt, co 10 min od 06:00) — bez nakładania na jednej maszynie.
        var episodes = Enumerable.Range(0, 5000).Select(i =>
        {
            int k = i / 25;
            return Ep("m" + (i % 25), "Błąd " + (i % 40), start.AddDays(k % 5).AddMinutes(k / 5 * 10).ToString("o"), 4, "inny");
        }).ToArray();
        var data = Data(episodes, Mon, Tue);
        Assert.Equal(5000, data.Episodes.Count);

        // Rozgrzewka: pierwszy eksport w procesie płaci za rejestrację czcionek i pamięć podręczną PDFsharp — to nie jest koszt eksportu.
        using (var warmUp = new MemoryStream()) Assert.True(DowntimePdfExporter.Write(WeekData(), warmUp).Success);

        var watch = Stopwatch.StartNew();
        using var pdfStream = new MemoryStream();
        Assert.True(DowntimePdfExporter.Write(data, pdfStream).Success);
        var pdfTime = watch.Elapsed;
        watch.Restart();
        using var xlsxStream = new MemoryStream();
        Assert.True(DowntimeExcelExporter.Write(data, xlsxStream).Success);
        var xlsxTime = watch.Elapsed;
        output.WriteLine($"PDF {pdfTime.TotalSeconds:0.00} s ({pdfStream.Length} B), Excel {xlsxTime.TotalSeconds:0.00} s ({xlsxStream.Length} B), limit {Limit.TotalSeconds:0} s");

        // Poprawność i rozmiar — zawsze, niezależnie od środowiska.
        Assert.True(pdfStream.Length < 5_000_000, $"PDF: {pdfStream.Length} B");
        Assert.True(xlsxStream.Length < 2_000_000, $"XLSX: {xlsxStream.Length} B");
        using var pdf = PdfProbe.Open(pdfStream.ToArray());
        Assert.InRange(pdf.PageCount, 5, 60);
        Assert.Contains("Tabela pokazuje 1000 najnowszych z 5000 postojów", pdf.AllText(), StringComparison.Ordinal);

        Assert.True(pdfTime < Limit, $"PDF 5000 wierszy: {pdfTime.TotalSeconds:0.0} s (limit {Limit.TotalSeconds:0} s)");
        Assert.True(xlsxTime < Limit, $"Excel 5000 wierszy: {xlsxTime.TotalSeconds:0.0} s (limit {Limit.TotalSeconds:0} s)");
    }
}
