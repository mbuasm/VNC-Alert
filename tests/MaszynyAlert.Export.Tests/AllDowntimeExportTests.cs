using ClosedXML.Excel;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

public sealed class AllDowntimeExportTests
{
    private static DowntimeExportData MixedData() => Data([
        Ep("a1", "Servo 6", "2026-09-29T08:00:00Z", 1, "inny"),
        Ep("a2", AlertStateMachine.MachineIdleShortText, "2026-09-29T09:00:00Z", 25, AlertStateMachine.IdleCategory),
        Ep("a3", AlertStateMachine.MachineIdleAtZeroShortText, "2026-09-29T10:00:00Z", 12, AlertStateMachine.IdleAtZeroCategory),
    ], DateOnly.Parse("2026-09-28"), DateOnly.Parse("2026-10-02"));

    [Fact]
    public void DaneEksportu_ZawierajaPostojeBezPracyIKrotkiBlad()
    {
        var data = MixedData();
        Assert.Equal(3, data.Episodes.Count);
        Assert.Contains(data.Episodes, r => r.ErrorName == "Stoi bez pracy (100 %) – dołóż prace" && r.ErrorGroup == "Bez pracy (100 %)");
        Assert.Contains(data.Episodes, r => r.ErrorName == "Stoi na 0 % – wciśnij Start" && r.ErrorGroup == "Stoi na 0 %");
        Assert.Contains(data.Episodes, r => r.ErrorName == "Servo 6" && Math.Abs(r.Minutes - 1) < 1e-9);
        Assert.Equal(38, data.Totals.All.Minutes, 6);
        Assert.Equal(3, data.Totals.All.Episodes);
        Assert.Equal(3, data.Charts.Kinds.Slices.Count);
        Assert.Contains(data.Charts.Kinds.Slices, s => s.Label == "Stoi na 0 %");
        Assert.Equal("Servo 6", Assert.Single(data.Charts.Errors.Slices).Label);
        Assert.Equal(1, data.Kinds.Errors.All.Minutes, 6);
        Assert.Equal(37, data.Kinds.IdleMinutes, 6);
    }

    [Fact]
    public void Excel_EpizodyMajaPostojeBezPracy()
    {
        var stream = new MemoryStream();
        var outcome = DowntimeExcelExporter.Write(MixedData(), stream);
        Assert.True(outcome.Success, outcome.Error);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var text = string.Join("\n", workbook.Worksheet("Epizody").CellsUsed().Select(c => c.GetString()));
        Assert.Contains("Stoi bez pracy (100 %) – dołóż prace", text, StringComparison.Ordinal);
        Assert.Contains("Stoi na 0 % – wciśnij Start", text, StringComparison.Ordinal);
        Assert.Contains("Bez pracy (100 %)", text, StringComparison.Ordinal);
        var info = string.Join("\n", workbook.Worksheet("Informacje").CellsUsed().Select(c => c.GetString()));
        Assert.DoesNotContain("ponad 3 minuty", info, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_TabelaMaPostojeBezPracy_OpisBezProguTrzechMinut()
    {
        using var pdf = PdfProbe.Write(MixedData());
        string text = pdf.AllText();
        Assert.Contains("Stoi bez pracy (100 %)", text, StringComparison.Ordinal);
        Assert.Contains("Stoi na 0 %", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ponad 3 minuty", text, StringComparison.Ordinal);
    }
}
