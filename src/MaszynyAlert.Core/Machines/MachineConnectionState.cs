namespace MaszynyAlert.Core.Machines;

/// <summary>Stan połączenia obserwatora (<see cref="MachineMonitor"/>) z jedną maszyną — pokazywany w oknie statusu.</summary>
public enum MachineConnectionState
{
    /// <summary>Brak połączenia — jeszcze nie próbowano albo trwa oczekiwanie na kolejną próbę (backoff).</summary>
    Disconnected,

    /// <summary>Trwa próba nawiązania połączenia.</summary>
    Connecting,

    /// <summary>Połączono (VNC) albo aktualny plik testowy jest odczytywany poprawnie.</summary>
    Connected,

    /// <summary>Ostatnia próba połączenia/odczytu zakończyła się błędem (patrz <c>LastError</c>).</summary>
    Error,

    /// <summary>Maszyna wyłączona w konfiguracji (<c>MachineConfig.Enabled = false</c>) — obserwator jej nie dotyka.</summary>
    Disabled,
}
