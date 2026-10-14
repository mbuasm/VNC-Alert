namespace MaszynyAlert.Core.Config;

/// <summary>Odstępy czasowe i progi sterujące pętlą obserwatora oraz cyklem życia alertów.</summary>
public sealed record TimingConfig
{
    /// <summary>Odstęp między kolejnymi odczytami ROI w trybie Persistent (ms).</summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>Jak często pobierać pełną klatkę ekranu zamiast tylko ROI (ms).</summary>
    public int FullFrameIntervalMs { get; init; } = 60000;

    /// <summary>Odstęp między połącz-pobierz-rozłącz w trybie Polling (ms).</summary>
    public int PollingModeIntervalMs { get; init; } = 5000;

    /// <summary>Po jakim czasie braku czerwieni epizod jest automatycznie zamykany, gdy nikt nie potwierdził (ms).</summary>
    public int EpisodeCloseAfterMs { get; init; } = 30000;

    public int EpisodeBreakAfterMs { get; init; } = 10000;

    public int SameMessageRejoinMs { get; init; } = 120000;

    public int EscalationAfterAckMs { get; init; } = 240000;

    public int AlertSoundRepeatMs { get; init; } = 60000;

    /// <summary>Po jakim czasie braku łączności z maszyną pokazujemy alert "brak łączności" (ms).</summary>
    public int OfflineAlertAfterMs { get; init; } = 180000;

    /// <summary>Czy alert o braku łączności ma odtwarzać dźwięk (domyślnie nie — restart maszyny jest normalny).</summary>
    public bool OfflineAlertSound { get; init; } = false;

    /// <summary>Kolejne odstępy przy ponawianiu połączenia po utracie łączności (ms); ostatnia wartość jest powtarzana.</summary>
    public int[] ReconnectBackoffMs { get; init; } = [2000, 5000, 15000, 30000];

    /// <summary>Maksymalny czas oczekiwania na nawiązanie połączenia VNC (ms).</summary>
    public int ConnectTimeoutMs { get; init; } = 10000;

    /// <summary>
    /// Twardy limit czasu na POJEDYNCZY odczyt z gniazda VNC (ms) — przekazywany do
    /// <c>RfbClientOptions.ReadTimeout</c>. Rzadko trafiany w normalnej pracy: żądania przyrostowe na
    /// niezmienionym ekranie obsługuje <see cref="IdleUpdateTimeoutMs"/> — ten limit chroni
    /// przed prawdziwie zerwanym/zawieszonym połączeniem (np. maszyna zniknęła z sieci w środku odpowiedzi).
    /// </summary>
    public int ReadTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// Ile czasu (ms) obserwator czeka na odpowiedź serwera VNC na żądanie PRZYROSTOWE, zanim uzna, że
    /// nic się nie zmieniło i przejdzie dalej (bez zamykania połączenia). Serwery WinCE/OpenCnc
    /// na nieruchomym ekranie potrafią milczeć w nieskończoność zamiast odpowiedzieć „bez zmian”, więc bez
    /// tego limitu obserwator czekałby do <see cref="ReadTimeoutMs"/> i fałszywie rozpoznawał to jako
    /// utratę łączności. Musi być krótszy niż <see cref="ReadTimeoutMs"/>.
    /// </summary>
    public int IdleUpdateTimeoutMs { get; init; } = 1000;

    /// <summary>Ile razy powtórzyć próbę OCR w ramach jednego epizodu, gdy poprzednia nie dała tekstu.</summary>
    public int OcrMaxAttempts { get; init; } = 3;

    public int OcrRetryIntervalMs { get; init; } = 2000;

    /// <summary>Jak długo pamiętać potwierdzenia (ACK) alertów, żeby nowy obserwator ich nie wznawiał (godziny).</summary>
    public int AckMemoryHours { get; init; } = 2;

    /// <summary>
    /// Ile ms maszyna musi nieprzerwanie pokazywać pasek postępu na 100% (<c>IdleWatchProfile.FullAtFraction</c>),
    /// zanim uznamy ją za „stojącą bez pracy” i podniesiemy alert <see cref="Alerts.AlertKind.MachineIdle"/>.
    /// Domyślnie 2 minuty — maszyna normalnie stoi na 100% ok. 1 minutę między
    /// zakończeniem jednej pracy a startem następnej (gdy jest zaplanowana kolejna praca), więc 2 minuty
    /// to zapas, który nie dzwoni fałszywie przy każdej zwykłej zmianie pracy.
    /// </summary>
    public int IdleAlertAfterMs { get; init; } = 120000;

    public int IdleAtZeroAfterMs { get; init; } = 180000;

    public int IdlePopupSeconds { get; init; } = 0;

    public int MainScreenCheckIntervalMs { get; init; } = 10000;

    public int MainScreenOcrGraceMs { get; init; } = 15000;

    public int WrongScreenAlertAfterMs { get; init; } = 0;

    /// <summary>
    /// Czy powiadomienie „włącz okno podglądu stanu maszyny” ma odtworzyć dźwięk (raz, przy pojawieniu
    /// się — nie w pętli jak alarm błędu). Domyślnie nie — to przypomnienie organizacyjne, pokazywane w
    /// osobnym pop-upie (jak „dołóż prace”), a nie awaria maszyny.
    /// </summary>
    public bool WrongScreenAlertSound { get; init; } = false;

    public int WrongScreenRemindAfterMs { get; init; } = 240000;
}
