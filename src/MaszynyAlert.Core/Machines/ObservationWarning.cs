using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Machines;

/// <summary>Operator-facing observation warning. An unchanged VNC frame is normal until refresh is overdue.</summary>
public static class ObservationWarning
{
    public static string? For(MachineStatusSnapshot snapshot, DateTimeOffset now, TimingConfig timing)
    {
        if (!snapshot.OcrAvailable) return "Odczyt tekstu (OCR) jest niedostępny. Nie można potwierdzić ekranu głównego ani postoju.";
        if (snapshot.ScreenObservationUnavailable)
            return snapshot.MainScreenVisible == false ? "Maszyna pokazuje inną zakładkę. Wróć do ekranu z komunikatami i procentem pracy."
                : "Odczyt tekstu (OCR) nie potwierdził ekranu głównego. Trwa ponowna próba.";
        long maxAgeMs = (long)timing.FullFrameIntervalMs + timing.ReadTimeoutMs + timing.PollIntervalMs;
        if (snapshot.LastFrameAt is not { } last || (now - last).TotalMilliseconds > maxAgeMs)
            return "Brak świeżego obrazu z maszyny. Trwa oczekiwanie na aktualizację.";
        return null;
    }
}
