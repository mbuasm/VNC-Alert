using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Ocr;

/// <summary>
/// Jedno słowo rozpoznane przez OCR, wraz z jego położeniem na obrazie.
/// </summary>
/// <param name="Text">Rozpoznany tekst słowa (bez sąsiadów — np. „Status:”, ze znakiem interpunkcyjnym, jeśli silnik OCR go zwrócił).</param>
/// <param name="Bounds">
/// Prostokąt otaczający słowo, we współrzędnych obrazu PRZEKAZANEGO do <see cref="IOcrEngine.RecognizeAsync"/>
/// (czyli obrazu w skali szarości, zwykle wyjścia <c>ImageOps.PrepareForOcr</c> — NIE oryginalnej klatki
/// VNC, jeśli ROI było wycięte wcześniej; przeliczenie na współrzędne klatki, jeśli potrzebne, robi wołający).
/// </param>
/// <param name="LineIndex">Indeks linii w <see cref="OcrResult.Lines"/>, do której to słowo należy (0-based, w kolejności odczytu).</param>
public sealed record OcrWord(string Text, RectInt Bounds, int LineIndex);

/// <summary>Wynik rozpoznawania tekstu (OCR) na obrazie przygotowanym przez <c>ImageOps.PrepareForOcr</c>.</summary>
/// <param name="Text">Rozpoznane linie złączone pojedynczą spacją.</param>
/// <param name="Lines">Rozpoznane linie tekstu, w kolejności odczytu.</param>
public sealed record OcrResult(string Text, IReadOnlyList<string> Lines)
{
    /// <summary>Pusty wynik (brak tekstu) — zwracany m.in. przez <see cref="NullOcrEngine"/>.</summary>
    public bool Unavailable { get; init; }
    public static OcrResult NotAvailable { get; } = new(string.Empty, Array.Empty<string>()) { Unavailable = true };

    public static OcrResult Empty { get; } = new(string.Empty, Array.Empty<string>());

    /// <summary>Prawda, gdy <see cref="Text"/> jest puste po przycięciu białych znaków.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Text?.Trim());

    /// <summary>
    /// Rozpoznane słowa z ich położeniem na obrazie (patrz <see cref="OcrWord.Bounds"/>) — używane m.in.
    /// przez <c>Vision.LabelAnchorLocator</c> do znajdowania pól wartości „na prawo od etykiety” bez
    /// ręcznego podawania współrzędnych ROI. Pusta lista, gdy silnik OCR nie zwraca pozycji słów (albo
    /// gdy wynik zbudowano przez konstruktor pozycyjny <c>OcrResult(Text, Lines)</c>, który jej nie ustawia).
    /// </summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();

    /// <summary>
    /// Buduje <see cref="OcrResult"/> ze słów wraz z ich pozycjami — wygodna fabryka dla silników OCR,
    /// które (jak Windows.Media.Ocr) zwracają słowa pogrupowane w linie. <see cref="Lines"/> odtwarzamy,
    /// grupując słowa po <see cref="OcrWord.LineIndex"/> (linie w kolejności rosnącego indeksu; słowa
    /// wewnątrz linii w kolejności rosnącego X — czyli kolejności odczytu od lewej do prawej, niezależnie
    /// od kolejności, w jakiej trafiły do <paramref name="words"/>), a słowa jednej linii łączymy spacją.
    /// <see cref="Text"/> to te linie złączone spacją (jak w konstruktorze pozycyjnym).
    /// </summary>
    public static OcrResult FromWords(IReadOnlyList<OcrWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        if (words.Count == 0)
        {
            return Empty;
        }

        // Grupujemy po LineIndex (kolejność rosnąca), a słowa w każdej linii sortujemy po X — silnik OCR
        // zwraca słowa w kolejności odczytu, ale nie chcemy zależeć od tego założenia przy budowaniu Lines.
        List<string> lines = words
            .GroupBy(w => w.LineIndex)
            .OrderBy(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.Bounds.X).Select(w => w.Text)))
            .ToList();

        return new OcrResult(string.Join(' ', lines), lines) { Words = words };
    }
}
