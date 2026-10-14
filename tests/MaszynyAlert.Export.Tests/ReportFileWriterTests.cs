using System.Security.AccessControl;
using System.Security.Principal;
using ClosedXML.Excel;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

public sealed class ReportFileWriterTests : IDisposable
{
    private readonly string _dir = NewWorkDir();

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly byte[] OldContent = "poprzedni raport"u8.ToArray();

    private void AssertNoTemp() => Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories));

    [Fact]
    public void Pdf_ZapisujeIPodmieniaPoprzedni_BezPlikuTmp()
    {
        string path = Path.Combine(_dir, "raport.pdf");
        File.WriteAllBytes(path, OldContent);

        var outcome = ReportFileWriter.SavePdf(WeekData(), path);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Null(outcome.Error);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("%PDF-"u8.ToArray(), bytes[..5]);
        using var pdf = PdfProbe.Open(bytes);
        Assert.Equal(4, pdf.PageCount);
        AssertNoTemp();
    }

    [Fact]
    public void Excel_ZapisujeCzytelnySkoroszyt_BezPlikuTmp()
    {
        string path = Path.Combine(_dir, "raport.xlsx");

        var outcome = ReportFileWriter.SaveExcel(WeekData(), path);

        Assert.True(outcome.Success, outcome.Error);
        using var workbook = new XLWorkbook(path);
        Assert.Equal(4, workbook.Worksheets.Count);
        AssertNoTemp();
    }

    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.Read)] // jak przeglądarka PDF / Excel: czytać wolno, podmienić — nie
    public void PlikDocelowyOtwarty_WynikZPodpowiedziaZamknij_StaryPlikNienaruszony(FileShare share)
    {
        string path = Path.Combine(_dir, "otwarty.xlsx");
        File.WriteAllBytes(path, OldContent);
        ExportOutcome outcome;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, share))
        {
            outcome = ReportFileWriter.SaveExcel(WeekData(), path);
        }

        Assert.False(outcome.Success);
        Assert.Contains("zamknij go", outcome.Error, StringComparison.Ordinal);
        Assert.StartsWith("Nie udało się zapisać pliku.", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(OldContent, File.ReadAllBytes(path));
        AssertNoTemp();
    }

    [Fact]
    public void PlikDocelowyTylkoDoOdczytu_Wynik_StaryPlikNienaruszony()
    {
        string path = Path.Combine(_dir, "tylko-odczyt.pdf");
        File.WriteAllBytes(path, OldContent);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var outcome = ReportFileWriter.SavePdf(WeekData(), path);

        Assert.False(outcome.Success);
        Assert.Equal(OldContent, File.ReadAllBytes(path));
        AssertNoTemp();
    }

    [Fact]
    public void NieistniejacyKatalog_WynikBezWyjatku_BezPodpowiedziOZamknieciu()
    {
        string path = Path.Combine(_dir, "nie-ma", "takiego", "raport.pdf");

        var outcome = ReportFileWriter.SavePdf(WeekData(), path);

        Assert.False(outcome.Success);
        Assert.DoesNotContain("zamknij", outcome.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "nie-ma")));
    }

    [Theory]
    [InlineData("a<b>.pdf")]
    [InlineData("a|b.pdf")]
    [InlineData("a\"b.pdf")]
    [InlineData("a?.pdf")]
    public void NiedozwolonyZnakWNazwie_WynikBezWyjatku(string name)
    {
        var outcome = ReportFileWriter.SavePdf(WeekData(), Path.Combine(_dir, name));
        Assert.False(outcome.Success);
        AssertNoTemp();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PustaSciezka_Wynik(string? path)
    {
        var outcome = ReportFileWriter.SaveExcel(WeekData(), path!);
        Assert.False(outcome.Success);
        Assert.Equal("Nie podano nazwy pliku.", outcome.Error);
    }

    [Fact]
    public void SciezkaToKatalog_WynikBezWyjatku()
    {
        string path = Path.Combine(_dir, "katalog.pdf");
        Directory.CreateDirectory(path);

        var outcome = ReportFileWriter.SavePdf(WeekData(), path);

        Assert.False(outcome.Success);
        Assert.True(Directory.Exists(path));
        AssertNoTemp();
    }

    [Fact]
    public void KatalogBezPrawaZapisu_WynikBezWyjatku_StaryPlikNienaruszony()
    {
        if (!OperatingSystem.IsWindows()) return; // ACL — tylko Windows (projekt testów jest uruchamiany wyłącznie w testy.ps1)
        string locked = Path.Combine(_dir, "bez-zapisu");
        Directory.CreateDirectory(locked);
        string path = Path.Combine(locked, "raport.pdf");
        File.WriteAllBytes(path, OldContent);
        var info = new DirectoryInfo(locked);
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        try
        {
            info.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        try
        {
            var outcome = ReportFileWriter.SavePdf(WeekData(), path);

            Assert.False(outcome.Success);
            Assert.StartsWith("Nie udało się zapisać pliku.", outcome.Error, StringComparison.Ordinal);
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
        Assert.Equal(OldContent, File.ReadAllBytes(path));
        AssertNoTemp();
    }

    [Fact]
    public void ZapisRownolegleDoRoznychPlikow_BezWyjatku()
    {
        var data = WeekData();
        var outcomes = new ExportOutcome[16];
        Parallel.For(0, outcomes.Length, i => outcomes[i] = i % 2 == 0
            ? ReportFileWriter.SavePdf(data, Path.Combine(_dir, $"r{i}.pdf"))
            : ReportFileWriter.SaveExcel(data, Path.Combine(_dir, $"r{i}.xlsx")));

        Assert.All(outcomes, o => Assert.True(o.Success, o.Error));
        for (int i = 0; i < outcomes.Length; i += 2)
        {
            using var pdf = PdfProbe.Open(File.ReadAllBytes(Path.Combine(_dir, $"r{i}.pdf")));
            Assert.Equal(4, pdf.PageCount);
        }
        AssertNoTemp();
    }
}
