namespace MaszynyAlert.Core.Alerts;

/// <summary>Rodzaj zdarzenia zgłaszanego przez <see cref="AlertStateMachine"/> (do UI, historii, sieci).</summary>
public enum AlertEventKind
{
    /// <summary>Nowy epizod alertu — ma zadzwonić na wszystkich komputerach.</summary>
    Raised,

    /// <summary>Alert potwierdzony ("Idę sprawdzić").</summary>
    Acknowledged,

    /// <summary>Potwierdzony alert wyeskalowany ponownie, bo błąd nadal trwa.</summary>
    Escalated,

    /// <summary>Epizod zamknięty.</summary>
    Closed,

    /// <summary>Zmienił się tekst/kategoria istniejącego alertu (np. OCR poprawił odczyt) — bez dźwięku.</summary>
    Updated,
}
