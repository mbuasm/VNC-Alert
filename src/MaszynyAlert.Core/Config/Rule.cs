namespace MaszynyAlert.Core.Config;

/// <summary>
/// Reguła słownika błędów — mapuje tekst odczytany przez OCR na krótki polski opis i kategorię.
/// Dopasowanie: najpierw <see cref="Pattern"/> (wyrażenie regularne na tekście znormalizowanym,
/// bez rozróżniania wielkości liter), potem <see cref="Phrases"/> (dopasowanie rozmyte — odległość
/// Levenshteina do <see cref="MaxDistance"/>).
/// </summary>
public sealed record Rule
{
    /// <summary>Identyfikator reguły (unikalny w rules.json).</summary>
    public required string Id { get; init; }

    /// <summary>Wyrażenie regularne dopasowywane do znormalizowanego tekstu (IgnoreCase). Opcjonalne.</summary>
    public string? Pattern { get; init; }

    /// <summary>Frazy do dopasowania rozmytego (contains z tolerancją odległości Levenshteina).</summary>
    public string[] Phrases { get; init; } = [];

    /// <summary>
    /// Maksymalna odległość Levenshteina dopuszczalna przy dopasowaniu rozmytym fraz. Dopasowanie liczy
    /// <see cref="Ocr.MessageMapper"/> na CAŁYCH SŁOWACH tekstu (okna słów), nie na dowolnym podciągu — żeby
    /// np. „door" nie łapało „DUR" w środku słowa „PROCEDURES".
    /// </summary>
    public int MaxDistance { get; init; } = 2;

    /// <summary>Krótki opis błędu po polsku, pokazywany w oknie alertu.</summary>
    public required string ShortText { get; init; }

    /// <summary>Kategoria błędu: narzedzie | tacka | restart | inny, albo <see cref="IgnoredCategory"/> / <see cref="SuppressedCategory"/>.</summary>
    public required string Category { get; init; }

    public const string IgnoredCategory = "nie błąd";

    /// <summary>Czy to reguła ignorująca (<see cref="Category"/> = <see cref="IgnoredCategory"/>) — patrz jej opis.</summary>
    public bool Ignored => string.Equals(Category, IgnoredCategory, StringComparison.OrdinalIgnoreCase);

    public const string SuppressedCategory = "bez alarmu";

    /// <summary>Czy to reguła wyciszająca alarm (<see cref="Category"/> = <see cref="SuppressedCategory"/>) — patrz jej opis.</summary>
    public bool Suppressed => string.Equals(Category, SuppressedCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Identyfikator profilu, do którego reguła jest ograniczona; null = dotyczy wszystkich profili.</summary>
    public string? Profile { get; init; }

    public int Priority { get; init; } = 0;
}

public sealed record RuleDatabase
{
    public string? Name { get; init; }

    /// <summary>Opis bazy — skąd pochodzi lista błędów i czego dotyczy. Program jej nie używa do dopasowania.</summary>
    public string? Description { get; init; }

    public string CodeIdPrefix { get; init; } = string.Empty;

    public string? CodePattern { get; init; }

    /// <summary>Słownik KODÓW alarmów tego typu maszyn — jak <see cref="RuleSet.Codes"/>, ale tylko dla niego.</summary>
    public List<AlarmCode> Codes { get; init; } = new();

    /// <summary>Reguły na TREŚCI komunikatu tego typu maszyn — jak <see cref="RuleSet.Rules"/>, ale tylko dla niego.</summary>
    public List<Rule> Rules { get; init; } = new();
}

/// <summary>Cała zawartość pliku rules.json.</summary>
public sealed record RuleSet
{
    public Dictionary<string, RuleDatabase> Databases { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Słownik KODÓW alarmów (numer → krótki opis) — sprawdzany PRZED <see cref="Rules"/>, bo numer alarmu
    /// (np. „No.705”) OCR czyta pewnie, a treść bywa przekręcona albo po chińsku. Patrz <see cref="AlarmCode"/>.
    /// </summary>
    public List<AlarmCode> Codes { get; init; } = new();

    /// <summary>
    /// Lista reguł na TREŚCI komunikatu (regex/frazy), sprawdzanych w kolejności: reguły profilowe przed
    /// globalnymi — droga zapasowa, gdy numeru alarmu nie widać (HMI przycina długi komunikat z obu stron)
    /// albo nie ma go w <see cref="Codes"/>.
    /// </summary>
    public List<Rule> Rules { get; init; } = new();
}
