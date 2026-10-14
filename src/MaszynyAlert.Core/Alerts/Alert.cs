namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Pojedynczy epizod alertu (błąd maszyny albo brak łączności) — od wykrycia po zamknięcie.
/// Serializowalny przez System.Text.Json (przesyłany między komputerami w <c>StateMessage</c>).
/// </summary>
public sealed record Alert
{
    public Guid EpisodeId { get; init; } = Guid.NewGuid();
    /// <summary>Independent UUIDs replaced during v2 partition reconciliation; never ACK aliases.</summary>
    public IReadOnlyList<Guid> SupersededEpisodeIds { get; init; } = Array.Empty<Guid>();
    /// <summary>Ambiguous continuity requires a new acknowledgement after this boundary.</summary>
    public DateTimeOffset? ReconciledAt { get; init; }
    public IReadOnlyList<AlertKey> EpisodeKeys { get; init; } = Array.Empty<AlertKey>();
    /// <summary>Klucz epizodu (maszyna + rozróżnik) — identyczny na wszystkich komputerach.</summary>
    public required AlertKey Key { get; init; }

    /// <summary>Identyfikator maszyny (<c>MachineConfig.Id</c>).</summary>
    public required string MachineId { get; init; }

    public required string MachineName { get; init; }

    /// <summary>Rodzaj alertu.</summary>
    public AlertKind Kind { get; init; }

    /// <summary>Krótki, polski opis błędu pokazywany w oknie alertu (np. "Nie udało się pobrać tacki").</summary>
    public required string ShortText { get; init; }

    /// <summary>Pełny tekst odczytany z ekranu maszyny (surowy OCR), jeśli udało się go odczytać.</summary>
    public string? FullText { get; init; }

    /// <summary>Kategoria błędu: narzedzie | tacka | restart | inny | offline | postoj | ekran.</summary>
    public required string Category { get; init; }

    /// <summary>Kiedy epizod się rozpoczął (pierwsze wykrycie tego błędu).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Kiedy ostatnio potwierdzono, że epizod nadal trwa (czerwień/brak łączności wciąż widoczne).</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>Bieżący status epizodu.</summary>
    public AlertStatus Status { get; init; }

    /// <summary>Kiedy alert potwierdzono przyciskiem "Idę sprawdzić" (jeśli dotyczy).</summary>
    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>Nazwa komputera, z którego potwierdzono alert.</summary>
    public string? AckedByHost { get; init; }

    /// <summary>Nazwa użytkownika, który potwierdził alert.</summary>
    public string? AckedByUser { get; init; }

    /// <summary>Kiedy nastąpiła (ostatnia) eskalacja potwierdzonego alertu.</summary>
    public DateTimeOffset? EscalatedAt { get; init; }

    /// <summary>Kiedy epizod zamknięto.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Ścieżka do zrzutu ekranu zapisanego przy wykryciu błędu, jeśli jest.</summary>
    public string? CaptureFile { get; init; }

    /// <summary>
    /// Ważność dopasowanej reguły (<c>Config.Rule.Priority</c>, 0 dla niedopasowanej/brak treści) w
    /// chwili ostatniego zapisu — decyduje, czy KOLEJNY komunikat widziany w tym samym epizodzie zastąpi
    /// treść tego alertu (patrz <see cref="AlertStateMachine.OnRedConfirmed"/>, <c>Ocr.MappedMessage.IsBetterThan</c>).
    /// Zwykłe pole rekordu — przechodzi przez <c>Cluster.MessageCodec</c>/<c>StateMerger</c> bez żadnej
    /// szczególnej obsługi (domyślna wartość 0 jest bezpieczna dla starszych nadawców, którzy tego pola
    /// jeszcze nie znają).
    /// </summary>
    public int Priority { get; init; } = 0;

    /// <summary>
    /// Czy alert ma odtwarzać dźwięk: dla <see cref="AlertKind.Error"/> zawsze <see langword="true"/>;
    /// dla <see cref="AlertKind.Offline"/> zgodnie z <c>TimingConfig.OfflineAlertSound</c>; dla
    /// <see cref="AlertKind.MachineIdle"/> zawsze <see langword="false"/> (mały pop-up, nie duże czerwone
    /// okno błędu — nie powinien straszyć dźwiękiem jak błąd maszyny); dla <see cref="AlertKind.WrongScreen"/>
    /// zgodnie z <c>TimingConfig.WrongScreenAlertSound</c> (domyślnie <see langword="false"/>).
    /// </summary>
    public bool Sound { get; init; }

    /// <summary>
    /// Jedna linia do wyświetlenia w oknie alertu: "NAZWA — opis — od HH:MM". Gdy <paramref name="tz"/> podano,
    /// czas jest konwertowany do tej strefy (<see cref="TimeZoneInfo.ConvertTime(DateTimeOffset, TimeZoneInfo)"/>);
    /// w przeciwnym razie używana jest strefa lokalna tego komputera.
    /// </summary>
    public string DisplayLine(TimeZoneInfo? tz = null)
    {
        DateTimeOffset local = tz is null ? StartedAt.ToLocalTime() : TimeZoneInfo.ConvertTime(StartedAt, tz);
        return $"{MachineName} — {ShortText} — od {local:HH:mm}";
    }
}
