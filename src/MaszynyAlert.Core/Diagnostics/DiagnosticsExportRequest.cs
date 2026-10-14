namespace MaszynyAlert.Core.Diagnostics;

/// <summary>Dane jawnie wybrane przez aplikację do eksportu diagnostyki.</summary>
public sealed record DiagnosticsExportRequest
{
    public required BuildIdentity Build { get; init; }

    /// <summary>
    /// Migawka Statusu utworzona przez aplikację z jawnej listy bezpiecznych pól.
    /// Eksporter nie czyta konfiguracji ani magazynu sekretów.
    /// </summary>
    public required string StatusJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public required string LogsDirectory { get; init; }

    public required string CapturesDirectory { get; init; }

    public required string HistoryPath { get; init; }

    /// <summary>
    /// Katalog pobrań aktualizacji (<c>state\updates</c>); z niego trafiają do ZIP wyłącznie <c>install-*.log</c>
    /// i <c>install-*.result.json</c> (bez instalatora i skryptu). <see langword="null"/> = pomiń.
    /// </summary>
    public string? UpdatesDirectory { get; init; }

    public string? UpdateTaskDirectory { get; init; }
}
