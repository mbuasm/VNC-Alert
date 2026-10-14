using ClosedXML.Excel;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using PdfSharp.Pdf.IO;

namespace MaszynyAlert.Export.Tests;

public class ExportSkeletonTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");

    private static DowntimeEpisode Episode(string machine, string reason, string start, int minutes, string? category = null) =>
        new(machine, machine.ToUpperInvariant(), reason, DateTimeOffset.Parse(start),
            DateTimeOffset.Parse(start).AddMinutes(minutes), DateTimeOffset.Parse(start).AddMinutes(minutes),
            EpisodeId: Guid.NewGuid(), Category: category);

    internal static DowntimeExportData Sample(int count = 12)
    {
        var episodes = new List<DowntimeEpisode>
        {
            Episode("a1", "Tacka nie pobrana", "2026-09-30T07:00:00Z", 12, "tacka"),
            Episode("a2", "Servo 6", "2026-10-01T15:00:00Z", 75, "restart"),
            Episode("a1", "Bazowanie (Home)", "2026-10-02T23:00:00Z", 9),
        };
        for (int i = 0; i < count; i++)
            episodes.Add(Episode("a" + (i % 4 + 1), "Błąd " + (i % 9), $"2026-10-0{i % 5 + 1}T{8 + i % 6:00}:10:00Z", 4 + i, "inny"));
        var slices = DowntimeReport.Build(episodes, Zone, now: Now);
        var observations = new[]
        {
            new ObservationInterval("a1", ObservationState.Observed, DateTimeOffset.Parse("2026-09-29T06:00:00Z"), DateTimeOffset.Parse("2026-10-06T06:00:00Z")),
            new ObservationInterval("a2", ObservationState.NoConnection, DateTimeOffset.Parse("2026-10-01T06:00:00Z"), DateTimeOffset.Parse("2026-10-01T08:00:00Z")),
        };
        MachineConfig[] machines = [new() { Id = "a1", Name = "Aidite 1", Host = "h", Profile = "aidite" }];
        return DowntimeExportData.Create(slices, new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 6), null,
            observations, machines, Zone, Now, "TEST-PC", "Dane lokalne tego komputera");
    }

    [Theory]
    [InlineData("Tacka nie pobrana", "Ramka niepobrana")]
    [InlineData("Tacka nie odłożona", "Ramka nieodłożona")]
    [InlineData("Nie znaleziono tacki", "Nie znaleziono ramki")]
    [InlineData("Miejsce tacki zajęte", "Miejsce ramki zajęte")]
    [InlineData("Servo 6", "Servo 6")]
    public void TrayIsPresentedAsFrame(string reason, string expected) =>
        Assert.Equal(expected, ReportPresentationNames.ErrorName(reason));

    [Fact]
    public void TrayCategoryIsPresentedAsFrame() => Assert.Equal("Ramka", ReportPresentationNames.CategoryName("tacka"));

    [Fact]
    public void FileNamesFollowRange()
    {
        var data = Sample();
        Assert.Equal("raport-postojow-2026-09-29_2026-10-06.pdf", data.FileName("pdf"));
        Assert.Equal("raport-postojow-2026-09-29_2026-10-06.xlsx", data.FileName(".xlsx"));
    }

    [Fact]
    public void PdfOpensAndHasTitlePageChartsAndTable()
    {
        using var stream = new MemoryStream();
        var outcome = DowntimePdfExporter.Write(Sample(), stream);
        Assert.True(outcome.Success, outcome.Error);
        stream.Position = 0;
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 3, $"stron: {pdf.PageCount}");
        Assert.True(pdf.Pages[0].Width.Point > pdf.Pages[0].Height.Point, "A4 poziomo");
        Assert.Equal(DowntimeExportData.Title, pdf.Info.Title);
    }

    [Fact]
    public void ExcelHasThreeSheetsRealDatesAndNumbers()
    {
        var data = Sample();
        using var stream = new MemoryStream();
        Assert.True(DowntimeExcelExporter.Write(data, stream).Success);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal(["Epizody", "Sumy", "Błędy", "Informacje"], workbook.Worksheets.Select(w => w.Name));
        var sheet = workbook.Worksheet("Epizody");
        Assert.Equal(data.Episodes.Count + 1, sheet.LastRowUsed()!.RowNumber());
        Assert.True(sheet.Cell(2, 2).Value.IsDateTime);
        Assert.True(sheet.Cell(2, 5).Value.IsDateTime);
        Assert.True(sheet.Cell(2, 7).Value.IsNumber);
        Assert.Contains(data.Episodes, e => e.ErrorName == "Ramka niepobrana");
        Assert.DoesNotContain(workbook.Worksheets.SelectMany(w => w.CellsUsed()), c => c.HasFormula);
    }

    [Fact]
    public void EmptyReportStillExportsBothFormats()
    {
        var data = DowntimeExportData.Create([], null, null, null, [], [], Zone, Now, "TEST-PC", "Dane lokalne tego komputera");
        using var pdf = new MemoryStream();
        using var xlsx = new MemoryStream();
        Assert.True(DowntimePdfExporter.Write(data, pdf).Success);
        Assert.True(DowntimeExcelExporter.Write(data, xlsx).Success);
    }
}
