using System.Text.RegularExpressions;
using System.Xml.Linq;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class ExportR3SourceTests
{
    private static string Root => TestPaths.RepositoryRoot;

    [Theory]
    [InlineData("src/MaszynyAlert.Core/MaszynyAlert.Core.csproj")]
    [InlineData("src/MaszynyAlert.Probe/MaszynyAlert.Probe.csproj")]
    [InlineData("src/MaszynyAlert.Rfb/MaszynyAlert.Rfb.csproj")]
    public void BibliotekiPdfIExcel_TylkoWProjekcieExport(string project)
    {
        string path = Path.Combine(Root, project);
        if (!File.Exists(path)) return;
        string text = File.ReadAllText(path);
        Assert.DoesNotMatch(new Regex("PDFsharp|MigraDoc|ClosedXML|MaszynyAlert\\.Export", RegexOptions.IgnoreCase), text);
    }

    [Fact]
    public void ProjektExport_BezWpfIZPakietamiP8()
    {
        var project = XDocument.Load(Path.Combine(Root, "src", "MaszynyAlert.Export", "MaszynyAlert.Export.csproj"));
        string framework = project.Descendants("TargetFramework").Single().Value;
        Assert.Equal("net10.0", framework);
        Assert.Empty(project.Descendants("UseWPF"));
        var packages = project.Descendants("PackageReference").Select(p => p.Attribute("Include")!.Value).ToArray();
        Assert.Equal(["PDFsharp-MigraDoc", "ClosedXML", "System.Security.Cryptography.Pkcs"], packages);
    }

    [Fact]
    public void Panel_PrzyciskiPdfIExcelObokCsv()
    {
        var view = XDocument.Load(Path.Combine(Root, "src", "MaszynyAlert.App", "Views", "DowntimeView.xaml"));
        XElement Button(string name) => view.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        var csv = Button("ExportButton");
        var pdf = Button("PdfButton");
        var excel = Button("ExcelButton");

        Assert.Equal("Zapisz PDF", pdf.Attribute("Content")?.Value);
        Assert.Equal("Zapisz Excel", excel.Attribute("Content")?.Value);
        Assert.Equal("Pdf_Click", pdf.Attribute("Click")?.Value);
        Assert.Equal("Excel_Click", excel.Attribute("Click")?.Value);
        // Ten sam WrapPanel (zawija się w 1024×768), PDF i Excel zaraz po CSV.
        Assert.Equal("WrapPanel", csv.Parent!.Name.LocalName);
        Assert.Same(csv.Parent, pdf.Parent);
        Assert.Same(csv.Parent, excel.Parent);
        var siblings = csv.Parent.Elements().ToList();
        Assert.Equal(siblings.IndexOf(csv) + 1, siblings.IndexOf(pdf));
        Assert.Equal(siblings.IndexOf(pdf) + 1, siblings.IndexOf(excel));
        // Kolory tylko z motywu (tryb ciemny): przyciski bez własnego tła i pisma.
        foreach (var button in new[] { pdf, excel })
            Assert.DoesNotContain(button.Attributes(), a => a.Name.LocalName is "Background" or "Foreground");
    }

    [Fact]
    public void Panel_EksportCsvBezZmian_OryginalnyPowodBezMapowaniaRamki()
    {
        string code = File.ReadAllText(Path.Combine(Root, "src", "MaszynyAlert.App", "Views", "DowntimeView.xaml.cs"));
        int start = code.IndexOf("private void Export_Click", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string csv = code[start..];

        Assert.Contains("\"Dzień zmiany;Zmiana;Maszyna;Powód;Od;Do;Minuty;Stan\"", csv, StringComparison.Ordinal);
        Assert.Contains("s.Machine, s.Reason,", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportPresentationNames", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorName", csv, StringComparison.Ordinal);
        Assert.Contains("StateText(s)", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_EksportPdfIExcel_ZTychSamychWierszyCoTabela()
    {
        string code = File.ReadAllText(Path.Combine(Root, "src", "MaszynyAlert.App", "Views", "DowntimeView.xaml.cs"));
        Assert.Contains("DowntimeExportData.Create(_slices,", code, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => save(data, path))", code, StringComparison.Ordinal);
        Assert.Contains("ReportFileWriter.SavePdf", code, StringComparison.Ordinal);
        Assert.Contains("ReportFileWriter.SaveExcel", code, StringComparison.Ordinal);
    }
}
