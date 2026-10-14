using System.Reflection;
using MaszynyAlert.Core.Export;
using PdfSharp.Fonts;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

/// <summary>Testy zmieniające globalny stan czcionek PDFsharp — wykonywane same, po wszystkich pozostałych.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalFontStateCollection
{
    public const string Name = "Globalny resolver czcionek PDFsharp";
}

[Collection(GlobalFontStateCollection.Name)]
public sealed class ExportFontResolverTests
{
    private static readonly Type ResolverType = typeof(DowntimePdfExporter).Assembly.GetType("MaszynyAlert.Export.ExportFontResolver", true)!;

    private static FieldInfo Field(string name) =>
        ResolverType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException(name);

    private static string? EnsureRegistered() =>
        (string?)ResolverType.GetMethod("EnsureRegistered", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);

    /// <summary>Stan sprzed testu: zerowanie PDFsharp i resolvera eksportu, potem zwykła rejestracja.</summary>
    private static void Restore()
    {
        PdfSharp.Diagnostics.PdfSharpCore.ResetFontManagement();
        Field("_registered").SetValue(null, null);
        Field("_registrationError").SetValue(null, null);
        Assert.Null(EnsureRegistered());
    }

    [Fact]
    public void Rejestracja_ZnajdujeCzcionkeSystemuZPolskimiZnakami()
    {
        Assert.Null(EnsureRegistered());
        string? path = (string?)ResolverType.GetProperty("RegularPath", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        Assert.NotNull(path);
        Assert.True(File.Exists(path), path);
        Assert.IsType(ResolverType, GlobalFontSettings.FontResolver);
    }

    [Fact]
    public void BrakCzcionki_WynikFailBezWyjatku_StrumienPusty()
    {
        const string error = "Nie znaleziono czcionki z polskimi znakami (Segoe UI, Arial, DejaVu Sans ani Liberation Sans).";
        var registered = Field("_registered").GetValue(null);
        Field("_registered").SetValue(null, null);
        Field("_registrationError").SetValue(null, error);
        try
        {
            using var stream = new MemoryStream();
            var outcome = DowntimePdfExporter.Write(WeekData(), stream);

            Assert.False(outcome.Success);
            Assert.Equal(error, outcome.Error);
            Assert.Equal(0, stream.Length);

            string path = Path.Combine(NewWorkDir(), "raport.pdf");
            File.WriteAllText(path, "stary");
            var saved = ReportFileWriter.SavePdf(WeekData(), path);
            Assert.False(saved.Success);
            Assert.Equal("stary", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            // Excel czcionki PDF nie potrzebuje.
            Assert.True(ReportFileWriter.SaveExcel(WeekData(), Path.ChangeExtension(path, ".xlsx")).Success);
        }
        finally
        {
            Field("_registrationError").SetValue(null, null);
            Field("_registered").SetValue(null, registered);
        }
    }

    [Fact]
    public void ObcyResolverBezCzcionek_WynikFailBezWyjatku()
    {
        try
        {
            PdfSharp.Diagnostics.PdfSharpCore.ResetFontManagement();
            Field("_registered").SetValue(null, null);
            Field("_registrationError").SetValue(null, null);
            GlobalFontSettings.FontResolver = new EmptyResolver();

            using var stream = new MemoryStream();
            var outcome = DowntimePdfExporter.Write(WeekData(), stream);

            Assert.False(outcome.Success);
            Assert.StartsWith("Nie udało się utworzyć pliku PDF.", outcome.Error, StringComparison.Ordinal);
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void PierwszaRejestracja_WieleEksportowNaraz_WszystkiePoprawne()
    {
        try
        {
            PdfSharp.Diagnostics.PdfSharpCore.ResetFontManagement();
            Field("_registered").SetValue(null, null);
            Field("_registrationError").SetValue(null, null);
            var data = WeekData();
            var results = new byte[12][];
            var outcomes = new ExportOutcome[12];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
            {
                using var stream = new MemoryStream();
                outcomes[i] = DowntimePdfExporter.Write(data, stream);
                results[i] = stream.ToArray();
            });

            Assert.All(outcomes, o => Assert.True(o.Success, o.Error));
            foreach (var bytes in results)
            {
                using var pdf = PdfProbe.Open(bytes);
                Assert.Equal(4, pdf.PageCount);
                Assert.Contains("Raport stworzony przez VNC Alert", pdf.Text(1), StringComparison.Ordinal);
            }
        }
        finally
        {
            Restore();
        }
    }

    private sealed class EmptyResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) => null;
        public byte[]? GetFont(string faceName) => null;
    }
}
