namespace MaszynyAlert.Core.Machines;

public sealed record MachineStatusSnapshot
{
    /// <summary>Identyfikator maszyny (<c>MachineConfig.Id</c>).</summary>
    public required string MachineId { get; init; }

    public required string Name { get; init; }

    /// <summary>Adres IP/nazwa hosta maszyny.</summary>
    public required string Host { get; init; }

    /// <summary>Port VNC/RFB maszyny.</summary>
    public int Port { get; init; }

    /// <summary>Identyfikator profilu ekranu tej maszyny.</summary>
    public required string Profile { get; init; }

    /// <summary>Bieżący stan połączenia obserwatora z maszyną.</summary>
    public MachineConnectionState State { get; init; }

    /// <summary>Ostatni błąd po polsku (np. "Nie udało się połączyć: przekroczono limit czasu"), albo null gdy brak.</summary>
    public string? LastError { get; init; }

    /// <summary>Kiedy ostatnio udało się pobrać jakikolwiek obraz (ROI albo pełną klatkę) z tej maszyny.</summary>
    public DateTimeOffset? LastFrameAt { get; init; }

    /// <summary>Ten cykl analizuje obraz z pamięci; LastFrameAt nie oznacza czasu tej analizy.</summary>
    public bool UsingCachedFrame { get; init; }
    public bool OcrAvailable { get; init; } = true;

    /// <summary>Udział czerwonych pikseli w ROI z ostatniej analizy (0..1).</summary>
    public double LastRedFraction { get; init; }

    /// <summary>Czy w OSTATNIM pojedynczym odczycie wykryto czerwień (przed filtrem "2 z 3").</summary>
    public bool RedNow { get; init; }

    /// <summary>Czy czerwień jest POTWIERDZONA (filtr "2 z 3" — <c>RedEpisodeFilter.Confirmed</c>).</summary>
    public bool RedConfirmed { get; init; }

    /// <summary>Ostatni surowy tekst odczytany przez OCR w bieżącym epizodzie, albo null.</summary>
    public string? LastOcrText { get; init; }

    /// <summary>Ostatni krótki (polski) opis błędu wynikający z dopasowania reguł, albo null.</summary>
    public string? LastShortText { get; init; }

    /// <summary>Informacje o źródle obrazu (wersja RFB, format piksela, rozmiar) z ostatniego udanego połączenia/odczytu.</summary>
    public ScreenSourceInfo? Info { get; init; }

    /// <summary>Od kiedy maszyna jest bez łączności (VNC), albo null gdy jest połączona.</summary>
    public DateTimeOffset? OfflineSince { get; init; }

    /// <summary>Źródło obrazu użyte w ostatnim cyklu: "vnc" | "plik" | "-" (maszyna wyłączona / brak jeszcze odczytu).</summary>
    public string Source { get; init; } = "-";

    /// <summary>Kiedy ta migawka została zaktualizowana (zegar <c>IClock</c>, nie zegar systemowy).</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Czy obserwacja „maszyna stoi bez pracy – dołóż prace” jest aktywna dla tej maszyny —
    /// <see langword="false"/>, gdy profil ekranu nie ma sekcji <c>idle</c> (albo ma <c>Enabled=false</c>),
    /// ALBO gdy <c>ProgressRoi</c> po przycięciu do rozmiaru ekranu okazało się puste (błąd konfiguracji —
    /// patrz <c>MachineMonitor</c>).
    /// </summary>
    public bool IdleWatchEnabled { get; init; }

    /// <summary>Wypełnienie paska postępu bieżącej pracy z OSTATNIEGO pojedynczego odczytu (0..1, przed filtrem "2 z 3").</summary>
    public double LastProgressFraction { get; init; }

    /// <summary>Czy w OSTATNIM pojedynczym odczycie pasek postępu wyglądał na wypełniony do 100% (przed filtrem "2 z 3").</summary>
    public bool ProgressFullNow { get; init; }

    /// <summary>
    /// Czy pasek postępu na 100% jest POTWIERDZONY filtrem "2 z 3" (<c>RedEpisodeFilter</c> na
    /// <c>ProgressDetectionResult.IsFull</c>).
    /// </summary>
    public bool ProgressFullConfirmed { get; init; }

    /// <summary>
    /// Od kiedy pasek postępu jest nieprzerwanie potwierdzony na 100% (<see cref="ProgressFullConfirmed"/>),
    /// albo <see langword="null"/>, gdy aktualnie nie jest. Pokazywane operatorowi jako „100% od HH:MM”.
    /// </summary>
    public DateTimeOffset? ProgressFullSince { get; init; }

    public DateTimeOffset? ProgressZeroSince { get; init; }

    /// <summary>
    /// Tryb <c>ProgressMode.Number</c>: ostatnio odczytany przez OCR procent postępu (0..100), albo
    /// <see langword="null"/>, gdy odczyt jest nieczytelny (OCR nie zwrócił rozpoznawalnej liczby) albo
    /// obserwacja postoju działa w trybie <c>ProgressMode.Bar</c>/jest wyłączona.
    /// Podczas oczekiwania na nowy odczyt prezentacja zachowuje ostatni procent najwyżej przez
    /// <c>TimingConfig.MainScreenOcrGraceMs</c>; nie jest to próbka dla liczników postoju.
    /// Przy zmianie cyfr w czasie OCR świeży wynik 1..99 może zasilić wyłącznie prezentację
    /// (bez dowodu postoju); jego wiek liczymy od początku odczytu, nie od zakończenia.
    /// Zakończony nieczytelny odczyt aktualnego wycinka, brak OCR, zmiana źródła lub utrata
    /// ekranu głównego kasują go. Odczyty 0/100 ze zmienionego wycinka nie są publikowane.
    /// </summary>
    public int? LastProgressPercent { get; init; }

    /// <summary>
    /// Tryb <c>ProgressMode.Number</c>: surowy tekst zwrócony przez silnik OCR z ostatniego odczytu
    /// wycinka z liczbą procent (do diagnostyki w kolumnie „Postęp” — patrz <c>MachineRowViewModel</c>),
    /// albo <see langword="null"/> w trybie <c>ProgressMode.Bar</c>/gdy jeszcze nie było odczytu.
    /// </summary>
    public string? LastProgressText { get; init; }

    public string? LastProgressSource { get; init; }

    /// <summary>
    /// Opis ostatniego pomiaru paska postępu (np. „pasek 86/350 kol. → 25 %” albo „pasek: tor niewidoczny (…)”),
    /// albo <see langword="null"/>, gdy profil nie ma paska (<c>idle.progressBar</c>) albo pomiaru jeszcze nie było.
    /// </summary>
    public string? LastProgressBarText { get; init; }

    /// <summary>
    /// Opis stanu programu z pudełka stanu HMI (<c>Config.ProgramStateProfile</c>, np. „RUN (zieleń 90 %)” albo
    /// „IDLE, wzorzec Abort: zgodny”), albo <see langword="null"/>, gdy profil nie ma sekcji <c>programState</c>.
    /// </summary>
    public string? ProgramStateText { get; init; }

    public bool ProgramAborted { get; init; }

    /// <summary>
    /// Czy obserwacja „czy HMI pokazuje ekran główny” jest aktywna dla tej maszyny — <see langword="false"/>,
    /// gdy profil nie ma sekcji <c>mainScreen</c> (albo ma <c>Enabled=false</c>) ALBO silnik OCR jest
    /// niedostępny (patrz <c>MachineMonitor</c>, <c>Config.MainScreenWatchProfile</c>).
    /// </summary>
    public bool MainScreenWatchEnabled { get; init; }

    /// <summary>Profil wymaga kontroli HMI; true także wtedy, gdy brak OCR uniemożliwia kontrolę.</summary>
    public bool MainScreenWatchRequired { get; init; }

    public bool ScreenObservationUnavailable => MainScreenWatchRequired && MainScreenVisible != true;

    /// <summary>
    /// Wynik OSTATNIEGO sprawdzenia ekranu: <see langword="true"/> — HMI pokazuje ekran
    /// główny; <see langword="false"/> — inny ekran (np. „Production info”); <see langword="null"/> —
    /// jeszcze nie sprawdzano, brak OCR albo ostatni odczyt nie pozwala rozpoznać ekranu.
    /// </summary>
    public bool? MainScreenVisible { get; init; }

    public int MainScreenUnknownStreak { get; init; }

    /// <summary>Powód ostatniego nierozstrzygniętego sprawdzenia (<c>invalidated</c>, <c>grace</c>, <c>pending</c>, …), albo <see langword="null"/>.</summary>
    public string? MainScreenUnknownReason { get; init; }

    /// <summary>Od kiedy HMI nieprzerwanie pokazuje inny ekran niż główny (<see cref="MainScreenVisible"/> = false), albo <see langword="null"/>.</summary>
    public DateTimeOffset? WrongScreenSince { get; init; }

    /// <summary>Kiedy ostatnio sprawdzono ekran główny (OCR pełnej klatki), albo <see langword="null"/>.</summary>
    public DateTimeOffset? LastMainScreenCheckAt { get; init; }
}
