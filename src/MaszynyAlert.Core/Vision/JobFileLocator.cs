using System.Text.RegularExpressions;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Vision;

public static class JobFileLocator
{
    /// <summary>Domyślna liczba słów przed członem <c>.nc</c> dobieranych do obszaru nazwy — patrz uwagi klasy.</summary>
    public const int DefaultMaxPrecedingWords = 2;

    /// <summary>Rozszerzenie pliku pracy na sterowniku maszyn (G-code), po którym rozpoznajemy nazwę.</summary>
    private const string JobExtension = ".nc";

    /// <summary>Człon nazwy pliku: litery, cyfry i znaki, których OCR nie gubi w nazwach typu <c>Job_16-27-30</c>.</summary>
    private static readonly Regex FileNamePart = new(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Szuka nazwy pliku pracy w <paramref name="ocr"/>: pierwszego (w kolejności odczytu — najwyższa
    /// linia, w niej najbardziej lewe wystąpienie) słowa kończącego się na <c>.nc</c>, a następnie
    /// dołącza do niego maksymalnie <paramref name="maxPrecedingWords"/> słów stojących przed nim w TEJ
    /// SAMEJ linii, dopóki wyglądają na człony nazwy pliku.
    /// </summary>
    /// <returns>
    /// <see cref="AnchoredField"/>, w którym <see cref="AnchoredField.Label"/> to prostokąt samego członu
    /// z <c>.nc</c> (kotwica, na której się oparliśmy), a <see cref="AnchoredField.Value"/> to obszar
    /// CAŁEJ znalezionej nazwy (suma prostokątów dobranych słów, powiększona o margines i przycięta do
    /// klatki) — czyli to samo, czego wołający używa przy kotwiczeniu na etykiecie.
    /// <see langword="null"/>, gdy w <paramref name="ocr"/> nie ma żadnego słowa z tym rozszerzeniem
    /// (np. OCR nie odczytał nazwy w tym cyklu albo silnik OCR jest niedostępny i lista słów jest pusta).
    /// </returns>
    public static AnchoredField? Find(
        OcrResult ocr,
        int frameWidth,
        int frameHeight,
        int maxPrecedingWords = DefaultMaxPrecedingWords)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPrecedingWords);

        foreach (List<OcrWord> lineWords in ByLine(ocr))
        {
            for (int anchor = 0; anchor < lineWords.Count; anchor++)
            {
                if (!IsJobFile(lineWords[anchor].Text))
                {
                    continue;
                }

                int first = anchor;
                while (first > 0
                    && anchor - first < maxPrecedingWords
                    && FileNamePart.IsMatch(lineWords[first - 1].Text))
                {
                    first--;
                }

                RectInt span = lineWords[first].Bounds;
                for (int i = first + 1; i <= anchor; i++)
                {
                    span = span.Union(lineWords[i].Bounds);
                }

                string text = string.Join(
                    ' ',
                    lineWords.Skip(first).Take(anchor - first + 1).Select(w => w.Text));

                RectInt value = span
                    .Inflate(6, Math.Max(4, span.Height / 2))
                    .Clamp(frameWidth, frameHeight);

                return new AnchoredField(lineWords[anchor].Bounds, value, lineWords[anchor].Text, text);
            }
        }

        return null;
    }

    /// <summary>
    /// Czy słowo to nazwa pliku pracy — kończy się na <c>.nc</c> (bez rozróżniania wielkości liter, po
    /// odcięciu końcowej interpunkcji, którą silnik OCR czasem dokleja) i ma przed rozszerzeniem co
    /// najmniej jeden znak, żeby samotne „.nc” nie udawało nazwy.
    /// </summary>
    private static bool IsJobFile(string word)
    {
        string trimmed = word.TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');
        return trimmed.Length > JobExtension.Length
            && trimmed.EndsWith(JobExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Słowa pogrupowane po linii (rosnąco, czyli od góry ekranu), a w linii uporządkowane po X (od
    /// lewej) — tak samo jak w <see cref="LabelAnchorLocator"/>, żeby „kolejność odczytu” znaczyła w
    /// obu klasach to samo, niezależnie od kolejności, w jakiej słowa trafiły do <see cref="OcrResult"/>.
    /// </summary>
    private static List<List<OcrWord>> ByLine(OcrResult ocr) => ocr.Words
        .GroupBy(w => w.LineIndex)
        .OrderBy(g => g.Key)
        .Select(g => g.OrderBy(w => w.Bounds.X).ToList())
        .ToList();
}
