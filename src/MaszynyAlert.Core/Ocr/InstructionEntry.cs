using System.Globalization;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace MaszynyAlert.Core.Ocr;

public sealed record InstructionEntry
{
    public required string Key { get; init; }

    /// <summary>
    /// Treść instrukcji albo <see langword="null"/> dla nagrobka (usunięto własną treść). Pole musi
    /// trafić na sieć TAKŻE jako <c>null</c> — w wiadomościach klastra puste pola są domyślnie pomijane,
    /// a pominięty nagrobek nie dałby się odczytać (wymagana właściwość) i usunięcie nigdy by nie doszło.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? Text { get; init; }

    /// <summary>Czas zmiany (UTC po stronie nadawcy) — pierwsze kryterium rozstrzygania konfliktu.</summary>
    public required DateTimeOffset EditedAt { get; init; }

    /// <summary>Nazwa komputera, na którym zmieniono wpis — drugie kryterium i informacja dla człowieka.</summary>
    public required string EditedBy { get; init; }

    public bool Deleted => Text is null;

    /// <summary>
    /// Czy <paramref name="candidate"/> ma zastąpić <paramref name="current"/>: nowszy czas wygrywa,
    /// a przy równym czasie większa ordinalnie nazwa komputera. Reguła musi dać ten sam wynik na każdym
    /// komputerze, inaczej grupa nigdy by się nie uzgodniła.
    /// </summary>
    public static bool Wins(InstructionEntry candidate, InstructionEntry current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);
        if (candidate.EditedAt != current.EditedAt) return candidate.EditedAt > current.EditedAt;
        return string.CompareOrdinal(candidate.EditedBy, current.EditedBy) > 0;
    }

    /// <summary>Czy wpis nadaje się do zapisania i rozesłania (te same granice, co w edytorze panelu).</summary>
    public bool IsValid()
    {
        if (!AlarmInstructionStore.IsValidKey(Key)) return false;
        if (Text is not null && (string.IsNullOrWhiteSpace(Text) || Text.Length > AlarmInstructionStore.MaxTextLength)) return false;
        return !string.IsNullOrWhiteSpace(EditedBy) && EditedBy.Length <= 100 && !EditedBy.Any(char.IsControl);
    }

    /// <summary>
    /// Odcisk całego zbioru instrukcji: SHA-256 posortowanych wierszy
    /// <c>klucz|editedAt|editedBy|deleted|text</c>. Równy odcisk u obu komputerów oznacza „nie ma czego
    /// wysyłać”, więc heartbeat niesie go w każdym cyklu.
    /// </summary>
    public static string Hash(IEnumerable<InstructionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append('|')
                .Append(entry.EditedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('|')
                .Append(entry.EditedBy).Append('|')
                .Append(entry.Deleted ? '1' : '0').Append('|')
                .Append(entry.Text).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>Największy <see cref="EditedAt"/> w zbiorze — „jak świeże” są instrukcje tego komputera.</summary>
    public static DateTimeOffset? LatestEditedAt(IEnumerable<InstructionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DateTimeOffset? latest = null;
        foreach (var entry in entries) if (latest is null || entry.EditedAt > latest) latest = entry.EditedAt;
        return latest;
    }
}
