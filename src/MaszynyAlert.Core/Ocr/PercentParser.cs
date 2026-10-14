using System.Text;
using System.Text.RegularExpressions;

namespace MaszynyAlert.Core.Ocr;

public static class PercentParser
{
    private static readonly Regex PercentAsZRegex = new(
        @"(?<![\p{L}\d])([0-9OoeEIilL|łŁ]{1,3}(?:[.,][0-9OoeEIilL|łŁ]{1,2})?)[zZ](?![\p{L}\d])",
        RegexOptions.Compiled);

    private static readonly Regex DigitsRegex = new(@"(?<!\d)(?<![.,]\d{0,2})(\d{1,3})(?:[.,](\d{1,2}))?(?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// Próbuje odczytać procent (0..100) z surowego tekstu OCR <paramref name="ocrText"/>. Zwraca
    /// <see langword="false"/> (z <paramref name="percent"/> = 0), gdy tekst jest pusty, nie zawiera
    /// żadnej liczby, albo wybrana liczba wykracza poza zakres 0..100.
    /// </summary>
    public static bool TryParse(string? ocrText, out int percent) => TryParse(ocrText, out percent, out _);

    public static bool TryParse(string? ocrText, out int percent, out bool hasNonZeroFraction) =>
        TryParse(ocrText, out percent, out hasNonZeroFraction, out _);

    /// <summary>
    /// Jak <see cref="TryParse(string?, out int, out bool)"/>, a dodatkowo <paramref name="hasPercentSign"/> mówi,
    /// czy wybrana liczba stała przed znakiem '%' (także 'z' odczytanym przez polski OCR zamiast '%') — a nie była
    /// gołą cyfrą wziętą w zastępstwie. <c>Vision.ProgressNumberReader</c> przyjmuje odczyt z zapasowej próby OCR
    /// tylko wtedy.
    /// </summary>
    public static bool TryParse(string? ocrText, out int percent, out bool hasNonZeroFraction, out bool hasPercentSign)
    {
        percent = 0;
        hasNonZeroFraction = false;
        hasPercentSign = false;

        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return false;
        }

        // Najpierw tokeny zakończone 'z' zamiast '%' (polski OCR), potem litery→cyfry przy prawdziwych cyfrach.
        // Obie zamiany zachowują długość, więc HasRealDigit patrzy w ocrText.
        string fixedText = FixDigitLookalikes(PercentAsZRegex.Replace(ocrText, ReplacePercentAsZ));
        MatchCollection matches = DigitsRegex.Matches(fixedText);
        if (matches.Count == 0)
        {
            return false;
        }

        Match? chosen = null;
        bool chosenHasPercentSign = false;
        foreach (Match m in matches)
        {
            if (IsFollowedByPercentSign(fixedText, m.Index + m.Length))
            {
                chosen = m;
                chosenHasPercentSign = true;
                break;
            }
        }

        if (chosen is null)
        {
            foreach (Match m in matches)
            {
                if (HasRealDigit(ocrText, m.Index, m.Length))
                {
                    chosen = m;
                    break;
                }
            }
        }

        if (chosen is null)
        {
            return false;
        }

        // Grupa 1 = tylko część całkowita (patrz DigitsRegex) — to ona jest wynikiem; ewentualna część
        // ułamkowa dopasowana razem z nią (np. „.08” w „79.08%”) jest odcinana, nigdy zaokrąglana.
        if (!int.TryParse(chosen.Groups[1].Value, out int value) || value is < 0 or > 100)
        {
            return false;
        }

        percent = value;
        hasNonZeroFraction = chosen.Groups[2].Success && chosen.Groups[2].Value.Any(c => c != '0');
        hasPercentSign = chosenHasPercentSign;
        return true;
    }

    /// <summary>
    /// Token dopasowany przez <see cref="PercentAsZRegex"/> (np. „łez”, „e.łz”) → ta sama liczba cyframi
    /// z '%' na końcu („10%”, „0.1%”). Długość bez zmian.
    /// </summary>
    private static string ReplacePercentAsZ(Match match) =>
        string.Concat(match.Groups[1].Value.Select(ToDigit)) + "%";

    private static char ToDigit(char c) => c switch
    {
        'O' or 'o' or 'e' or 'E' => '0',
        'I' or 'i' or 'l' or 'L' or '|' or 'ł' or 'Ł' => '1',
        _ => c,
    };

    private static bool IsFollowedByPercentSign(string text, int index)
    {
        int i = index;
        while (i < text.Length && text[i] == ' ')
        {
            i++;
        }

        return i < text.Length && text[i] == '%';
    }

    private static string FixDigitLookalikes(string text)
    {
        var sb = new StringBuilder(text);
        int i = 0;
        while (i < text.Length)
        {
            if (!IsDigitLookalike(text[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && IsDigitLookalike(text[i]))
            {
                i++;
            }

            bool digitBefore = start > 0 && IsDigitOrPercent(text[start - 1]);
            bool digitAfter = i < text.Length && IsDigitOrPercent(text[i]);
            if (!digitBefore && !digitAfter)
            {
                continue;
            }

            for (int j = start; j < i; j++)
            {
                sb[j] = ToDigit(text[j]);
            }
        }

        return sb.ToString();
    }

    private static bool IsDigitLookalike(char c) =>
        c is 'O' or 'o' or 'e' or 'E' or 'I' or 'i' or 'l' or 'L' or '|' or 'ł' or 'Ł';

    private static bool IsDigitOrPercent(char c) => (c >= '0' && c <= '9') || c == '%';

    /// <summary>
    /// Czy fragment tekstu ŹRÓDŁOWEGO (przed podstawieniami) o tym samym położeniu co dopasowana liczba ma
    /// choć jedną prawdziwą cyfrę. Podstawienia nie zmieniają długości tekstu, więc położenia się zgadzają.
    /// </summary>
    private static bool HasRealDigit(string originalText, int index, int length)
    {
        int end = Math.Min(originalText.Length, index + length);
        for (int i = Math.Max(0, index); i < end; i++)
        {
            if (originalText[i] >= '0' && originalText[i] <= '9')
            {
                return true;
            }
        }

        return false;
    }
}
