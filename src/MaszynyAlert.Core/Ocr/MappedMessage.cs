using System.Security.Cryptography;
using System.Text;

namespace MaszynyAlert.Core.Ocr;

/// <summary>
/// Wynik dopasowania tekstu OCR do słownika błędów (<c>MessageMapper</c>).
/// </summary>
/// <param name="RuleId">Identyfikator dopasowanej reguły, albo <see langword="null"/>, gdy nic nie dopasowano.</param>
/// <param name="ShortText">Krótki opis błędu po polsku, pokazywany w oknie alertu.</param>
/// <param name="Category">Kategoria błędu: narzedzie | tacka | restart | inny.</param>
/// <param name="RawText">Surowy tekst zwrócony przez OCR (bez normalizacji).</param>
/// <param name="NormalizedText">Tekst po <see cref="TextNormalizer.Normalize"/>.</param>
/// <param name="Matched"><see langword="true"/>, gdy dopasowano regułę ze słownika (<see cref="RuleId"/> nie jest <see langword="null"/>).</param>
public sealed record MappedMessage(string? RuleId, string ShortText, string Category, string RawText, string NormalizedText, bool Matched)
{
    public int Priority { get; init; } = 0;

    public string? AlarmNumber { get; init; }

    public string CodeNamespace { get; init; } = string.Empty;

    public bool Ignored { get; init; }

    public bool Suppressed { get; init; }

    /// <summary>
    /// Klucz do deduplikacji tego samego błędu niezależnie od tego, który komputer go zgłosił, w kolejności
    /// sprawdzania: <c>"rule:{RuleId}"</c>, gdy dopasowano regułę; w przeciwnym razie
    /// <c>"alarm:{CodeNamespace}{AlarmNumber}"</c>, gdy <see cref="AlarmNumber"/> jest znany (numer alarmu bez
    /// dopasowanej reguły — patrz <see cref="AlarmNumber"/> i <see cref="CodeNamespace"/>); w przeciwnym razie <c>"text:{12 hex znaków SHA-256(NormalizedText)}"</c>,
    /// albo <c>"text:empty"</c>, gdy <see cref="NormalizedText"/> jest puste.
    /// </summary>
    public string Discriminator => RuleId is not null
        ? $"rule:{RuleId}"
        : AlarmNumber is not null
            ? $"alarm:{CodeNamespace}{AlarmNumber}"
            : string.IsNullOrEmpty(NormalizedText)
                ? "text:empty"
                : $"text:{Sha256Hex12(NormalizedText)}";

    /// <summary>
    /// Czy TA wiadomość powinna zastąpić <paramref name="other"/> jako „najlepszy komunikat epizodu”
    /// (patrz <c>Machines.MachineMonitor</c> — pole <c>_bestMapped</c> — i <c>Alerts.AlertStateMachine.OnRedConfirmed</c>,
    /// gdzie ten sam epizod cyklicznie pokazuje kilka RÓŻNYCH komunikatów, a do alertu ma trafić opis
    /// najważniejszego z nich, nie pierwszego zobaczonego). Reguły porównania, w kolejności sprawdzania:
    /// <list type="bullet">
    /// <item><paramref name="other"/> jest <see langword="null"/> (jeszcze nic nie widzieliśmy) → zawsze <see langword="true"/>.</item>
    /// <item>Ta wiadomość dopasowała regułę, a <paramref name="other"/> nie → <see langword="true"/>
    /// (dopasowana reguła jest ZAWSZE lepsza niż "Błąd: &lt;surowy tekst&gt;" albo "nie odczytano treści",
    /// niezależnie od <see cref="Priority"/>).</item>
    /// <item>Obie dopasowały regułę → wygrywa ta z WYŻSZYM <see cref="Priority"/>; przy RÓWNYM priorytecie
    /// (w tym tej samej reguły widzianej ponownie) <see langword="false"/> — nie ma powodu migać alertem
    /// bez różnicy w treści.</item>
    /// <item>Obie NIE dopasowały reguły → wygrywa ta z <see cref="AlarmNumber"/>, gdy <paramref name="other"/>
    /// go nie ma (numer alarmu bez opisu w słowniku jest zawsze bardziej użyteczny niż surowy tekst bez
    /// numeru — patrz <see cref="AlarmNumber"/>); w przeciwnym razie wygrywa ta z niepustym
    /// <see cref="MappedMessage.NormalizedText"/>, gdy <paramref name="other"/> jest pusty (surowy tekst OCR
    /// jest zawsze lepszy niż całkowity brak treści); w przeciwnym razie <see langword="false"/>.</item>
    /// </list>
    /// </summary>
    public bool IsBetterThan(MappedMessage? other)
    {
        if (Ignored)
        {
            // Znany komunikat „nie błąd” nigdy nie opisuje epizodu czerwieni — patrz <see cref="Ignored"/>.
            return false;
        }

        if (other is null)
        {
            return true;
        }

        if (other.Ignored)
        {
            return true;
        }

        if (Matched && !other.Matched)
        {
            return true;
        }

        if (Matched && other.Matched)
        {
            return Priority > other.Priority;
        }

        if (!Matched && !other.Matched)
        {
            if (AlarmNumber is not null && other.AlarmNumber is null)
            {
                return true;
            }

            if (AlarmNumber is null && other.AlarmNumber is not null)
            {
                return false;
            }

            return NormalizedText.Length > 0 && other.NormalizedText.Length == 0;
        }

        // Ostatni przypadek: ta wiadomość NIE dopasowała, a other TAK — other zawsze lepszy.
        return false;
    }

    private static string Sha256Hex12(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(12);
        for (int i = 0; i < 6; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }

        return sb.ToString();
    }
}
