using System.Text.RegularExpressions;

namespace MaszynyAlert.Core.Ocr;

/// <summary>
/// Ocenia, czy surowy tekst zwrócony przez OCR jest w ogóle CZYTELNY (wygląda jak tekst, a nie "śmieci"
/// pozostałe po nieudanym rozpoznaniu czerwonego napisu — pojedyncze symbole, przypadkowe znaki) — patrz
/// <c>MessageMapper.Map</c>, gałąź braku dopasowania reguły: nieczytelny surowy tekst dostaje komunikat
/// „Błąd (nie odczytano treści)” zamiast wyświetlenia operatorowi bezsensownych znaków w
/// „Błąd: &lt;surowy tekst&gt;”.
/// </summary>
public static class TextReadability
{
    private const double MinAsciiFraction = 0.7;
    private const int MinWordLetters = 4;
    private const int RegexTimeoutMs = 200;

    private static readonly Regex AlarmNumberPattern = new(
        @"\bN[O0]\s*([0-9OIL]{2,4})\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(RegexTimeoutMs));

    public static bool IsReadable(string? rawText)
    {
        string trimmed = (rawText ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        int allowed = 0;
        foreach (char c in trimmed)
        {
            if (IsAsciiAlnumOrSpace(c))
            {
                allowed++;
            }
        }

        if ((double)allowed / trimmed.Length < MinAsciiFraction)
        {
            return false;
        }

        if (MatchesAlarmNumber(TextNormalizer.NormalizeKeepDigits(rawText)))
        {
            return true;
        }

        return HasLetterWord(TextNormalizer.Normalize(rawText));
    }

    /// <summary>
    /// Wyciąga numer alarmu (np. "1532") z pierwszego tokenu "No." + 2–4 znaki numeru w wariancie
    /// <see cref="TextNormalizer.NormalizeKeepDigits"/> tekstu <paramref name="rawText"/>, albo
    /// <see langword="null"/>, gdy taki token nie występuje. Pomyłki OCR wewnątrz numeru są poprawiane
    /// (<c>O</c>→<c>0</c>, <c>I</c>/<c>L</c>→<c>1</c>), więc "No.7O5" daje "705" — zwracany tekst to zawsze
    /// same cyfry, bez wiodących zer (patrz <see cref="AlarmDigits"/>).
    /// </summary>
    public static string? ExtractAlarmNumber(string? rawText) =>
        TryExtractAlarmCode(rawText, out int code) ? code.ToString() : null;

    /// <summary>
    /// Jak <see cref="ExtractAlarmNumber"/>, ale zwraca numer jako liczbę — do wyszukania w słowniku kodów
    /// (<see cref="AlarmCodeTable"/>). <see langword="false"/>, gdy tekst nie zawiera tokenu numeru alarmu.
    /// </summary>
    public static bool TryExtractAlarmCode(string? rawText, out int code)
    {
        string? digits = AlarmDigits(TextNormalizer.NormalizeKeepDigits(rawText));
        if (digits is not null && int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out code))
        {
            return true;
        }

        code = 0;
        return false;
    }

    public static bool TryExtractAlarmCode(string? rawText, Regex pattern, out int code)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        code = 0;
        string text = (rawText ?? string.Empty).ToUpperInvariant();
        if (text.Length == 0)
        {
            return false;
        }

        // Wzorzec pochodzi z konfiguracji, a MatchCollection szuka LENIWIE, więc limit czasu potrafi
        // wybuchnąć dopiero w pętli — całe przeszukanie jest w try, żeby zły regex w rules.json nie
        // przewrócił obserwacji maszyny, tylko dał „brak numeru”.
        try
        {
            foreach (Match match in pattern.Matches(text))
            {
                // Grupa 1 = sam numer; gdy wzorzec jej nie ma, bierzemy całe trafienie (łatwiej wybaczyć
                // niedopisany nawias w regexie niż zgłosić „brak numeru” na maszynie, która numer pokazuje).
                // Przy alternatywie z kilkoma grupami („\[(\d+)\]|NO\.(\d+)”) grupa 1 istnieje, ale bywa
                // PUSTA, gdy trafiła druga gałąź — wtedy liczy się pierwsza grupa, która coś złapała.
                string token = match.Groups.Count > 1 ? string.Empty : match.Value;
                for (int g = 1; g < match.Groups.Count; g++)
                {
                    if (match.Groups[g].Success && match.Groups[g].Length > 0)
                    {
                        token = match.Groups[g].Value;
                        break;
                    }
                }

                if (Digits(token) is { } digits
                    && int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out code))
                {
                    return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            code = 0;
            return false;
        }

        code = 0;
        return false;
    }

    private static bool MatchesAlarmNumber(string keepDigitsText) => AlarmDigits(keepDigitsText) is not null;

    /// <summary>
    /// Pierwsze trafienie <see cref="AlarmNumberPattern"/> w <paramref name="keepDigitsText"/>, które zawiera
    /// choć jedną prawdziwą cyfrę, po zamianie pomyłek OCR na cyfry — albo <see langword="null"/>.
    /// </summary>
    private static string? AlarmDigits(string keepDigitsText)
    {
        MatchCollection matches;
        try
        {
            matches = AlarmNumberPattern.Matches(keepDigitsText);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        foreach (Match match in matches)
        {
            if (Digits(match.Groups[1].Value) is { } digits)
            {
                return digits;
            }
        }

        return null;
    }

    /// <summary>
    /// Same cyfry z tokenu numeru alarmu: pomyłki OCR zamienione na cyfry (<c>O</c>→<c>0</c>,
    /// <c>I</c>/<c>L</c>→<c>1</c>), reszta znaków pominięta; <see langword="null"/>, gdy token nie ma ANI
    /// JEDNEJ prawdziwej cyfry (np. „NO OIL” nie jest numerem alarmu).
    /// </summary>
    private static string? Digits(string token)
    {
        bool hasDigit = false;
        var digits = new System.Text.StringBuilder(token.Length);
        foreach (char c in token)
        {
            switch (c)
            {
                case >= '0' and <= '9':
                    hasDigit = true;
                    digits.Append(c);
                    break;
                case 'O':
                    digits.Append('0');
                    break;
                case 'I':
                case 'L':
                    digits.Append('1');
                    break;
            }
        }

        return hasDigit && digits.Length > 0 ? digits.ToString() : null;
    }

    private static bool HasLetterWord(string normalizedText)
    {
        foreach (string word in normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length < MinWordLetters)
            {
                continue;
            }

            bool allLetters = true;
            foreach (char c in word)
            {
                if (c is < 'A' or > 'Z')
                {
                    allLetters = false;
                    break;
                }
            }

            if (allLetters)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiAlnumOrSpace(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ';
}
