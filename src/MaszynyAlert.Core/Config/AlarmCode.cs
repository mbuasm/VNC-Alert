using System.Text.Json.Serialization;

namespace MaszynyAlert.Core.Config;

public sealed record AlarmCode
{
    /// <summary>Pojedynczy numer alarmu (np. 1200). Wyklucza się z <see cref="From"/>/<see cref="To"/>.</summary>
    public int? Code { get; init; }

    /// <summary>Początek zakresu numerów (włącznie), np. 700. Wymaga <see cref="To"/>.</summary>
    public int? From { get; init; }

    /// <summary>Koniec zakresu numerów (włącznie), np. 719. Wymaga <see cref="From"/>.</summary>
    public int? To { get; init; }

    /// <summary>
    /// Krótki opis po polsku pokazywany na pasku alertu (np. „Servo 6”, „Zużyte narzędzie 3”). W zakresie
    /// znacznik <c>{n}</c> = pozycja numeru w zakresie liczona od 1 (patrz uwagi do klasy).
    /// </summary>
    public required string ShortText { get; init; }

    /// <summary>Kategoria błędu: narzedzie | tacka | restart | inny (domyślnie „inny”).</summary>
    public string Category { get; init; } = "inny";

    public int Priority { get; init; } = 0;

    /// <summary>
    /// Oryginalna (angielska) treść alarmu z listy producenta — tylko informacyjnie, do odnalezienia
    /// alarmu w dokumentacji; program jej nie używa do dopasowania.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>Prawda, gdy wpis opisuje zakres numerów (<see cref="From"/> i <see cref="To"/> ustawione).</summary>
    [JsonIgnore]
    public bool IsRange => From.HasValue || To.HasValue;

    /// <summary>Czytelna etykieta wpisu do komunikatów o błędach konfiguracji („705” albo „700–719”).</summary>
    [JsonIgnore]
    public string Label => IsRange ? $"{From?.ToString() ?? "?"}–{To?.ToString() ?? "?"}" : (Code?.ToString() ?? "?");
}
