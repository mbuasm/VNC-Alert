using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Pole wartości znalezione na ekranie „po etykiecie” — patrz <see cref="LabelAnchorLocator.Find"/>.
/// </summary>
/// <param name="Label">Prostokąt obejmujący słowa etykiety, tak jak faktycznie odczytał je OCR (suma ich <c>Bounds</c>).</param>
/// <param name="Value">Wyznaczony prostokąt pola wartości — patrz opis <see cref="LabelAnchorLocator.Find"/>.</param>
/// <param name="LabelText">Tekst etykiety odczytany przez OCR (może się różnić od szukanych słów — dopasowanie jest rozmyte).</param>
/// <param name="ValueText">Tekst wartości odczytany przez OCR (słowa na prawo od etykiety, złączone spacją) — puste, gdy OCR nie odczytał tam żadnego słowa.</param>
public sealed record AnchoredField(RectInt Label, RectInt Value, string LabelText, string ValueText);

public static class LabelAnchorLocator
{
    /// <summary>Domyślny próg odległości Levenshteina dla dopasowania rozmytego pojedynczego słowa etykiety.</summary>
    private const int DefaultMaxDistance = 2;

    /// <summary>
    /// Szuka w <paramref name="ocr"/> KOLEJNYCH słów w tej samej linii, pasujących rozmyto (odległość
    /// Levenshteina &lt;= <paramref name="maxDistance"/>, bez rozróżniania wielkości liter, po odcięciu
    /// znaków interpunkcyjnych na końcach słowa — np. „Status:” dopasuje się do „Status”) do kolejnych
    /// elementów <paramref name="labelWords"/> — np. dla <c>["Milling", "Status"]</c> w linii
    /// „Milling Status 79.08%” dopasuje słowa „Milling” i „Status”.
    /// </summary>
    /// <remarks>
    /// Wyznaczanie wyniku:
    /// <list type="bullet">
    /// <item><see cref="AnchoredField.Label"/> = suma (<see cref="RectInt.Union"/>) prostokątów dopasowanych słów.</item>
    /// <item><see cref="AnchoredField.Value"/> = suma prostokątów słów TEJ SAMEJ LINII leżących na prawo od
    /// etykiety (środek słowa na prawo od <c>Label.Right</c>), powiększona o margines: <c>max(4, wysokość etykiety / 2)</c>
    /// w pionie i 6 px w poziomie (<see cref="RectInt.Inflate"/>), przycięta do klatki (<see cref="RectInt.Clamp"/>).</item>
    /// <item>Gdy na prawo od etykiety nie ma żadnych słów (OCR nie odczytał wartości w tym cyklu — pole może
    /// akurat migać albo być nieostre) — <see cref="AnchoredField.Value"/> to prostokąt zaczynający się
    /// w <c>Label.Right + 6</c>, o szerokości <c>max(2 * szerokość etykiety, 120)</c> i wysokości etykiety
    /// (z tym samym marginesem pionowym co wyżej), przycięty do klatki; <see cref="AnchoredField.ValueText"/> = "".
    /// To jest tylko PRZYBLIŻONE miejsce, w którym wartość PRAWDOPODOBNIE się pojawi w kolejnym cyklu —
    /// wołający powinien traktować taki wynik jako mniej pewny (np. spróbować ponownie w następnym cyklu).</item>
    /// <item>Gdy etykieta występuje na ekranie kilka razy, wygrywa pierwsze dopasowanie w kolejności czytania
    /// (najwyższa linia, a w niej najbardziej lewe wystąpienie) — patrz uwaga o „DS: 100%” w opisie klasy:
    /// to etykieta „Milling Status” jest unikatowa, więc w praktyce ta reguła nie powinna mieć znaczenia,
    /// ale zabezpiecza przed niejednoznacznością, gdyby się pomyliła.</item>
    /// </list>
    /// Zwraca <see langword="null"/>, gdy <paramref name="labelWords"/> jest puste albo żadna linia nie
    /// zawiera dopasowania (np. OCR nie odczytał tej etykiety w tym cyklu — pole HMI akurat migało/było
    /// nieostre po ruchu ramienia, albo silnik OCR jest niedostępny i <paramref name="ocr"/> jest pusty).
    /// </remarks>
    public static AnchoredField? Find(
        OcrResult ocr,
        IReadOnlyList<string> labelWords,
        int frameWidth,
        int frameHeight,
        int maxDistance = DefaultMaxDistance)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(labelWords);

        if (labelWords.Count == 0)
        {
            return null;
        }

        // Słowa pogrupowane po linii (LineIndex, kolejność rosnąca = od góry ekranu) i wewnątrz linii
        // uporządkowane po X (od lewej) — niezależnie od kolejności, w jakiej trafiły do OcrResult.Words.
        List<List<OcrWord>> byLine = ocr.Words
            .GroupBy(w => w.LineIndex)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(w => w.Bounds.X).ToList())
            .ToList();

        foreach (List<OcrWord> lineWords in byLine)
        {
            for (int start = 0; start + labelWords.Count <= lineWords.Count; start++)
            {
                if (!MatchesLabelAt(lineWords, start, labelWords, maxDistance))
                {
                    continue;
                }

                return BuildResult(lineWords, start, labelWords.Count, frameWidth, frameHeight);
            }
        }

        return null;
    }

    /// <summary>
    /// Jak <see cref="Find"/>, ale próbuje po kolei kilka wariantów słów etykiety (np. inny zapis wielkości
    /// liter albo słowa sklejone przez OCR w jedno) i zwraca wynik pierwszego wariantu, który się dopasował.
    /// Przydatne, gdy OCR danego HMI bywa niekonsekwentny — np. „Milling Status” raz jako dwa słowa, raz
    /// jako jedno „MillingStatus”. Zwraca <see langword="null"/>, gdy żaden wariant się nie dopasował
    /// (albo <paramref name="labelVariants"/> jest puste).
    /// </summary>
    public static AnchoredField? FindAny(
        OcrResult ocr,
        IReadOnlyList<IReadOnlyList<string>> labelVariants,
        int frameWidth,
        int frameHeight,
        int maxDistance = DefaultMaxDistance)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(labelVariants);

        foreach (IReadOnlyList<string> variant in labelVariants)
        {
            AnchoredField? found = Find(ocr, variant, frameWidth, frameHeight, maxDistance);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Sprawdza, czy słowa <paramref name="lineWords"/> zaczynając od <paramref name="start"/> pasują rozmyto do <paramref name="labelWords"/>.</summary>
    private static bool MatchesLabelAt(List<OcrWord> lineWords, int start, IReadOnlyList<string> labelWords, int maxDistance)
    {
        for (int k = 0; k < labelWords.Count; k++)
        {
            string candidate = StripEdgePunctuation(lineWords[start + k].Text).ToUpperInvariant();
            string expected = StripEdgePunctuation(labelWords[k]).ToUpperInvariant();
            if (Levenshtein.Distance(candidate, expected) > maxDistance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Buduje <see cref="AnchoredField"/> dla dopasowania etykiety znalezionego w <paramref name="lineWords"/> od indeksu <paramref name="start"/>.</summary>
    private static AnchoredField BuildResult(List<OcrWord> lineWords, int start, int labelWordCount, int frameWidth, int frameHeight)
    {
        RectInt label = RectInt.Empty;
        for (int k = 0; k < labelWordCount; k++)
        {
            label = label.Union(lineWords[start + k].Bounds);
        }

        string labelText = string.Join(' ', Enumerable.Range(start, labelWordCount).Select(i => lineWords[i].Text));

        // Słowa tej samej linii leżące na prawo od etykiety — Skip przeskakuje słowa etykiety, a warunek
        // na położenie jest tu głównie zabezpieczeniem (patrz XML-doc Find). Porównujemy ŚRODEK słowa z
        // prawą krawędzią etykiety, a nie jego lewą krawędź: ramki słów z Windows.Media.Ocr potrafią się
        // o piksel–dwa nakładać (antyaliasing, ciasny kerning), a wymóg X >= Label.Right odrzuciłby wtedy
        // prawdziwą wartość stojącą tuż za etykietą.
        List<OcrWord> valueWords = lineWords
            .Skip(start + labelWordCount)
            .Where(w => w.Bounds.X + (w.Bounds.Width / 2) >= label.Right)
            .ToList();

        int verticalPad = Math.Max(4, label.Height / 2);
        const int horizontalPad = 6;

        if (valueWords.Count > 0)
        {
            RectInt valueUnion = RectInt.Empty;
            foreach (OcrWord w in valueWords)
            {
                valueUnion = valueUnion.Union(w.Bounds);
            }

            RectInt value = valueUnion.Inflate(horizontalPad, verticalPad).Clamp(frameWidth, frameHeight);
            string valueText = string.Join(' ', valueWords.Select(w => w.Text));
            return new AnchoredField(label, value, labelText, valueText);
        }

        // OCR nie odczytał żadnego słowa na prawo od etykiety w tym cyklu — zwracamy PRZYBLIŻONE miejsce,
        // w którym wartość prawdopodobnie się pojawi (patrz XML-doc Find), z tekstem pustym jako oznaczeniem
        // "nieodczytane", a nie "puste pole" — wołający rozróżnia te przypadki po samym ValueText.
        int width = Math.Max(2 * label.Width, 120);
        RectInt fallbackRaw = new(label.Right + horizontalPad, label.Y, width, label.Height);
        RectInt fallbackValue = fallbackRaw.Inflate(0, verticalPad).Clamp(frameWidth, frameHeight);
        return new AnchoredField(label, fallbackValue, labelText, string.Empty);
    }

    /// <summary>Usuwa znaki interpunkcyjne/symbole z OBU końców słowa (np. „Status:” → „Status”) — środek słowa zostaje bez zmian.</summary>
    private static string StripEdgePunctuation(string text)
    {
        int start = 0;
        int end = text.Length;
        while (start < end && IsEdgePunctuation(text[start]))
        {
            start++;
        }

        while (end > start && IsEdgePunctuation(text[end - 1]))
        {
            end--;
        }

        return text.Substring(start, end - start);
    }

    private static bool IsEdgePunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);
}
