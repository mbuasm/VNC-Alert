using System.Text;

namespace MaszynyAlert.Core.Ocr;

/// <summary>
/// Normalizuje surowy tekst z OCR przed dopasowaniem do reguł: wielkie litery, poprawa typowych
/// pomyłek OCR (0/O, 1/I), usunięcie polskich znaków diakrytycznych (OCR w trybie pl-PL potrafi je
/// wstawiać do angielskich komunikatów) i pozostawienie tylko liter A-Z, cyfr i pojedynczych spacji.
/// </summary>
public static class TextNormalizer
{
    private static readonly Dictionary<char, char> DiacriticsMap = new()
    {
        ['Ą'] = 'A',
        ['Ć'] = 'C',
        ['Ę'] = 'E',
        ['Ł'] = 'L',
        ['Ń'] = 'N',
        ['Ó'] = 'O',
        ['Ś'] = 'S',
        ['Ź'] = 'Z',
        ['Ż'] = 'Z',
    };

    /// <summary>Normalizuje <paramref name="text"/> (patrz podsumowanie w dokumentacji klasy). <see langword="null"/> → "".</summary>
    public static string Normalize(string? text) => NormalizeCore(text, substituteDigits: true);

    public static string NormalizeKeepDigits(string? text) => NormalizeCore(text, substituteDigits: false);

    private static string NormalizeCore(string? text, bool substituteDigits)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string upper = trimmed.ToUpperInvariant();

        // Polskie znaki diakrytyczne → ASCII (OCR pl-PL bywa "twórczy" nawet na angielskim tekście).
        var deaccented = new StringBuilder(upper.Length);
        foreach (char c in upper)
        {
            deaccented.Append(DiacriticsMap.TryGetValue(c, out char mapped) ? mapped : c);
        }

        string source = deaccented.ToString();

        string substitutedText;
        if (substituteDigits)
        {
            // '0'->'O', '1'->'I' zawsze; '|' i '!' -> 'I' TYLKO gdy leżą MIĘDZY dwoma literami (litera po
            // obu stronach — sąsiadują z literami), w przeciwnym razie -> spacja (typowa końcówka zdania,
            // np. "!" na końcu komunikatu nie powinna doklejać fałszywego "I"). '5' zostaje bez zmian.
            var substituted = new StringBuilder(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                switch (c)
                {
                    case '0':
                        substituted.Append('O');
                        break;
                    case '1':
                        substituted.Append('I');
                        break;
                    case '|':
                    case '!':
                        bool prevIsLetter = i > 0 && IsLetterLike(source[i - 1]);
                        bool nextIsLetter = i < source.Length - 1 && IsLetterLike(source[i + 1]);
                        substituted.Append(prevIsLetter && nextIsLetter ? 'I' : ' ');
                        break;
                    default:
                        substituted.Append(c);
                        break;
                }
            }

            substitutedText = substituted.ToString();
        }
        else
        {
            // NormalizeKeepDigits: cyfry i '|'/'!' zostają BEZ ZMIAN — jedyna różnica względem Normalize.
            substitutedText = source;
        }

        // Wszystko poza [A-Z0-9 ] -> spacja.
        var cleaned = new StringBuilder(substitutedText.Length);
        foreach (char c in substitutedText)
        {
            bool allowed = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ';
            cleaned.Append(allowed ? c : ' ');
        }

        return CollapseSpaces(cleaned.ToString()).Trim();
    }

    /// <summary>Litera A-Z albo cyfra, która po podstawieniu staje się literą (0→O, 1→I).</summary>
    private static bool IsLetterLike(char c) => (c >= 'A' && c <= 'Z') || c == '0' || c == '1';

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (char c in s)
        {
            if (c == ' ')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }
}
