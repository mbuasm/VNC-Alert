namespace MaszynyAlert.Core.Alerts;

/// <summary>Rodzaj alertu.</summary>
public enum AlertKind
{
    /// <summary>Czerwony komunikat błędu wykryty na pasku statusu maszyny.</summary>
    Error,

    /// <summary>Brak łączności z maszyną utrzymujący się dłużej niż skonfigurowany próg (VNC nie odpowiada).</summary>
    Offline,

    MachineIdle,

    /// <summary>
    /// HMI maszyny pokazuje INNY ekran niż główny (okno stanu maszyny z alertami i procentem) — np.
    /// operator przełączył na „Production info” i zapomniał wrócić — nieprzerwanie od co najmniej
    /// <c>TimingConfig.WrongScreenAlertAfterMs</c>. Dopóki trwa, program nie widzi paska alertów ani
    /// liczby procent tej maszyny. Wykrywane przez OCR pełnej klatki (etykieta ekranu głównego, patrz
    /// <c>Vision.MainScreenDetector</c>), pokazywane jako osobny pop-up „włącz okno podglądu stanu
    /// maszyny” (nie duże czerwone okno błędu), domyślnie bez dźwięku; po zamknięciu ✕ ponawiane po
    /// <c>TimingConfig.WrongScreenRemindAfterMs</c>, jeśli ekran nadal jest niewłaściwy.
    /// </summary>
    WrongScreen,
}
