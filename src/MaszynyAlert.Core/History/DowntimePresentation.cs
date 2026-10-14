using System.Globalization;
using System.Text.RegularExpressions;

namespace MaszynyAlert.Core.History;

public static class DowntimePresentation
{
    public const string UnrecognizedName = "Nierozpoznany";

    /// <summary>Pozycja zbiorcza najmniejszych nazw błędów na wykresie kołowym.</summary>
    public const string RemainderName = "Pozostałe";

    private static readonly Regex TrayWord = new(
        @"\b(?:([Tt])ac(kami|kach|kom|ka|ki|kę|ką|ko|ce|ek)|(T)AC(KAMI|KACH|KOM|KA|KI|KĘ|KĄ|KO|CE|EK))\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex SeparateNegation = new(
        @"\b(nie) (pobran|odłożon|odlozon)(ymi|ych|ego|emu|ej|ym|a|e|y|i|ą)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

    private static readonly IReadOnlyDictionary<string, string> CategoryNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["narzedzie"] = "Narzędzie",
            ["tacka"] = "Ramka",
            ["restart"] = "Restart",
            [DowntimeCategories.Other] = "Inny",
            [DowntimeCategories.Unrecognized] = UnrecognizedName,
            [DowntimeCategories.Idle] = "Bez pracy (100 %)",
            [DowntimeCategories.IdleAtZero] = "Stoi na 0 %",
        };

    /// <summary>
    /// Nazwa błędu do pokazania: zapisany tekst (<see cref="DowntimeSlice.Reason"/>) po mapowaniu „Tacka” → „Ramka”.
    /// Pusty albo biały tekst = <see cref="UnrecognizedName"/>.
    /// </summary>
    public static string ErrorName(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return UnrecognizedName;
        string text = reason.Trim();
        try
        {
            if (!TrayWord.IsMatch(text)) return text;
            string mapped = TrayWord.Replace(text, m => m.Groups[3].Success
                ? "RAM" + m.Groups[4].Value
                : (m.Groups[1].Value == "T" ? "R" : "r") + "am" + m.Groups[2].Value);
            return SeparateNegation.Replace(mapped, m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    /// <summary>
    /// Nazwa pozycji wykresu „wg nazwy błędu” dla jednego wiersza raportu: postój bez rozpoznanego rodzaju
    /// (<see cref="DowntimeCategories.Unrecognized"/>) albo bez tekstu to <see cref="UnrecognizedName"/>, reszta — <see cref="ErrorName"/>.
    /// </summary>
    public static string ChartErrorName(DowntimeSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return string.Equals(slice.Category, DowntimeCategories.Unrecognized, StringComparison.OrdinalIgnoreCase)
            ? UnrecognizedName
            : ErrorName(slice.Reason);
    }

    /// <summary>Nazwa rodzaju (kategorii) błędu do pokazania — np. „tacka” → „Ramka”; nieznana — z wielkiej litery.</summary>
    public static string CategoryName(string? category)
    {
        string? normalized = DowntimeCategories.Normalize(category);
        if (normalized is null) return UnrecognizedName;
        if (CategoryNames.TryGetValue(normalized, out var name)) return name;
        string mapped = ErrorName(normalized);
        return mapped.Length == 0 ? UnrecognizedName
            : char.ToUpper(mapped[0], CultureInfo.GetCultureInfo("pl-PL")) + mapped[1..];
    }
}
