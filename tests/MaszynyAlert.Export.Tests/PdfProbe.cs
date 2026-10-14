using MaszynyAlert.Core.Export;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace MaszynyAlert.Export.Tests;

/// <summary>Jedna linia tekstu strony PDF (litery o tej samej linii bazowej, od lewej) z największym stopniem pisma.</summary>
internal sealed record PdfLine(double Y, string Text, double Size, IReadOnlyList<Letter> Letters);

internal sealed class PdfProbe : IDisposable
{
    private readonly PigDocument _pig;

    private PdfProbe(byte[] bytes)
    {
        Bytes = bytes;
        _pig = PigDocument.Open(bytes);
        using var stream = new MemoryStream(bytes);
        Sharp = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
    }

    public byte[] Bytes { get; }
    public PdfDocument Sharp { get; }
    public int PageCount => _pig.NumberOfPages;

    public static PdfProbe Write(DowntimeExportData data)
    {
        using var stream = new MemoryStream();
        var outcome = DowntimePdfExporter.Write(data, stream);
        Assert.True(outcome.Success, outcome.Error);
        return new PdfProbe(stream.ToArray());
    }

    public static PdfProbe Open(byte[] bytes) => new(bytes);

    /// <summary>Linie strony (numeracja od 1), od góry do dołu.</summary>
    public IReadOnlyList<PdfLine> Lines(int page)
    {
        var letters = _pig.GetPage(page).Letters;
        return letters
            .GroupBy(l => Math.Round(l.StartBaseLine.Y, 0))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(l => l.StartBaseLine.X).ToArray();
                var text = new System.Text.StringBuilder();
                for (int i = 0; i < ordered.Length; i++)
                {
                    // MigraDoc stawia słowa osobno (bez znaku spacji): odstęp > 0,15 stopnia pisma = spacja, a duża
                    // przerwa (> 1,2 stopnia — sąsiednia komórka tabeli albo etykieta) = " | ".
                    if (i > 0 && text.Length > 0 && text[^1] != ' ' && ordered[i].Value != " ")
                    {
                        double gap = ordered[i].StartBaseLine.X - ordered[i - 1].EndBaseLine.X;
                        if (gap > ordered[i].PointSize * 1.2) text.Append(" | ");
                        else if (gap > ordered[i].PointSize * 0.15) text.Append(' ');
                    }
                    text.Append(ordered[i].Value);
                }
                return new PdfLine(g.Key, text.ToString(), ordered.Max(l => l.PointSize), ordered);
            })
            .ToArray();
    }

    /// <summary>Cały tekst strony (linie złączone znakiem nowej linii).</summary>
    public string Text(int page) => string.Join("\n", Lines(page).Select(l => l.Text));

    public string AllText() => string.Join("\n\f\n", Enumerable.Range(1, PageCount).Select(Text));

    /// <summary>Czy w pliku jest osadzony program czcionki TrueType (<c>/FontFile2</c>).</summary>
    public bool HasEmbeddedTrueType() =>
        Sharp.Internals.GetAllObjects().OfType<PdfDictionary>().Any(d => d.Elements.ContainsKey("/FontFile2"));

    public void Dispose()
    {
        _pig.Dispose();
        Sharp.Dispose();
    }
}
