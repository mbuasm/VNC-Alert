using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Unikalny, stabilny klucz epizodu alertu: identyfikator maszyny + "rozróżnik" — id dopasowanej reguły
/// (<c>"rule:{id}"</c>), numer alarmu bez dopasowanej reguły (<c>"alarm:{numer}"</c>, patrz
/// <see cref="MappedMessage.AlarmNumber"/>) albo hash znormalizowanego tekstu OCR (<c>"text:{hash}"</c>/
/// <c>"text:empty"</c>). Nie zależy od tego, który komputer wykrył błąd — dzięki temu wszystkie komputery
/// w klastrze zgadzają się co do tego, czy chodzi o "ten sam" alert.
/// </summary>
/// <remarks>
/// Serializowany przez System.Text.Json jako zwykły obiekt <c>{"machineId":"...","discriminator":"..."}</c> —
/// jedyny publiczny konstruktor (wygenerowany dla pozycyjnego <c>record struct</c>) jest oznaczony
/// <see cref="JsonConstructorAttribute"/>, żeby deserializacja nie zależała od domyślnych heurystyk STJ.
/// </remarks>
[method: JsonConstructor]
public readonly record struct AlertKey(string MachineId, string Discriminator)
{
    /// <summary>Klucz dla błędu dopasowanego do reguły ze słownika.</summary>
    public static AlertKey ForRule(string machineId, string ruleId) => new(machineId, $"rule:{ruleId}");

    /// <summary>
    /// Klucz dla błędu bez dopasowanej reguły — rozróżnik to pierwsze 12 znaków (małymi literami) hasza
    /// SHA-256 znormalizowanego tekstu (UTF-8); dla pustego tekstu zawsze <c>"text:empty"</c>.
    /// </summary>
    public static AlertKey ForText(string machineId, string normalizedText)
    {
        string text = normalizedText ?? string.Empty;
        string discriminator = text.Length == 0 ? "text:empty" : $"text:{Sha256Hex12(text)}";
        return new AlertKey(machineId, discriminator);
    }

    /// <summary>Klucz na podstawie wyniku dopasowania OCR — deleguje do <see cref="MappedMessage.Discriminator"/>.</summary>
    public static AlertKey ForMessage(string machineId, MappedMessage m) => new(machineId, m.Discriminator);

    /// <summary>Klucz alertu "brak łączności" dla danej maszyny.</summary>
    public static AlertKey Offline(string machineId) => new(machineId, "offline");

    /// <summary>Klucz alertu "maszyna stoi bez pracy – dołóż prace" dla danej maszyny.</summary>
    public static AlertKey Idle(string machineId) => new(machineId, "idle");

    /// <summary>Klucz alertu "HMI pokazuje inny ekran niż główny – włącz okno podglądu stanu maszyny" dla danej maszyny.</summary>
    public static AlertKey WrongScreen(string machineId) => new(machineId, "screen");

    /// <summary>Postać tekstowa "maszyna|rozróżnik" — używana m.in. jako klucz w CLI/logach.</summary>
    public override string ToString() => $"{MachineId}|{Discriminator}";

    /// <summary>Odwraca <see cref="ToString"/> — rozdziela po PIERWSZYM znaku '|'.</summary>
    public static AlertKey Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int separator = s.IndexOf('|');
        if (separator < 0)
        {
            throw new FormatException($"Nieprawidłowy format klucza alertu: \"{s}\" (brak separatora '|').");
        }

        return new AlertKey(s[..separator], s[(separator + 1)..]);
    }

    private static string Sha256Hex12(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash)[..12];
    }
}
