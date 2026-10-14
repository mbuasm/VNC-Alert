using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Vision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Machines;

public sealed class MachineMonitor
{
    private const int MainScreenUnknownWarnStreak = 5;
    private readonly MachineConfig _machine;
    private readonly ScreenProfile _profile;
    private readonly Func<IScreenSource> _vncFactory;
    private readonly FolderScreenSource? _testOverride;
    private readonly IOcrEngine _ocr;
    private Task<MappedMessage>? _errorRead;
    private Task<MappedMessage>? _firstReadResult;
    private Task<ProgressDetectionResult>? _progressRead;
    private RgbaImage? _progressReadImage;
    private Task<MainScreenDetectionResult>? _screenRead;
    private ObservationTime _progressReadAt, _screenReadAt;
    private bool _progressReadInvalidated, _progressReadImageChanged, _screenReadInvalidated, _screenReadStale;
    private RgbaImage? _screenReadImage, _lastScreenImage;
    private ObservationTime? _lastScreenResultAt;
    // „Pas kotwic”: obszar, w którym OCR NAPRAWDĘ odczytał etykietę ekranu głównego — domyślna wartość
    // MainScreenWatchProfile.ContextRoi, wyliczana samoczynnie po pierwszym udanym rozpoznaniu.
    private RectInt? _screenAnchorRoi;
    private int _mainScreenUnknownStreak;
    private string? _mainScreenUnknownReason;
    private int _sourceGeneration, _episodeGeneration;
    private int _errorSourceGeneration, _errorEpisodeGeneration, _progressGeneration, _screenGeneration;

    private readonly MessageMapper _mapper;
    private readonly AlertStateMachine _alerts;
    private readonly IClock _clock;
    private readonly TimingConfig _timing;
    private readonly ICaptureStore _captures;
    private readonly DiagnosticsConfig _diagnostics;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly RedTextDetector _detector;
    private readonly RedEpisodeFilter _filter;

    private readonly IdleWatchProfile? _idleProfile;
    private readonly ProgressBarDetector? _progressDetector;
    private readonly ProgressNumberReader? _numberReader;
    private readonly RedEpisodeFilter? _fullFilter;

    private readonly RedEpisodeFilter? _zeroFilter;

    /// <summary>
    /// Obszar (we współrzędnych pełnego ekranu, JUŻ przycięty do jego granic) faktycznie pobierany z VNC
    /// w tym cyklu — sam <c>StatusBarRoi</c>, gdy obserwacja postoju jest wyłączona, albo suma
    /// (<see cref="RectInt.Union"/>) <c>StatusBarRoi</c> ∪ <c>ProgressRoi</c>, gdy jest aktywna — jeden
    /// odczyt zamiast dwóch. Aktualizowane wyłącznie w <see cref="UpdateIdleBounds"/> (przy każdej pełnej
    /// klatce), więc między pełnymi klatkami pozostaje stałe.
    /// </summary>
    private RectInt _fetchArea;

    /// <summary>Ostatnio znany (przycięty) obszar paska statusu — do wycięcia pod-obrazu z <see cref="_fetchArea"/> (patrz <see cref="CropSub"/>).</summary>
    private RectInt _statusRoiClamped;

    /// <summary>Ostatnio znany (przycięty) obszar paska postępu — jak <see cref="_statusRoiClamped"/>.</summary>
    private RectInt _progressRoiClamped;

    private bool _idleWatchEnabled;
    private bool _idleRoiOutOfBoundsLogged;
    private string? _idleCaptureFile;
    private double _lastProgressFraction;
    private bool _progressFullNow;
    private bool _progressFullConfirmed;

    /// <summary>Tryb number: ostatnio odczytany procent (patrz <see cref="MachineStatusSnapshot.LastProgressPercent"/>), albo null (tryb bar/brak odczytu).</summary>
    private int? _lastProgressPercent;
    // Presentation only: never feed a held value into the idle filters or state machine.
    private ObservationTime? _lastProgressDisplayAt;

    /// <summary>Tryb number: surowy tekst OCR ostatniego odczytu (patrz <see cref="MachineStatusSnapshot.LastProgressText"/>).</summary>
    private string? _lastProgressText;

    /// <summary>
    /// Od kiedy pasek postępu jest nieprzerwanie POTWIERDZONY (filtr "2 z 3") na 100%, albo
    /// <see langword="null"/> gdy aktualnie nie jest — ustawiane przy pierwszym potwierdzeniu, zerowane,
    /// gdy potwierdzenie spada. Trzymane tu (osobno od identycznie nazwanego licznika w
    /// <see cref="Alerts.AlertStateMachine"/>) wyłącznie na potrzeby migawki (<see cref="MachineStatusSnapshot.ProgressFullSince"/>)
    /// i jednorazowego zrzutu ekranu — patrz <see cref="ProcessIdleState"/>.
    /// </summary>
    private ObservationTime? _progressFullSince;

    /// <summary>Od kiedy postęp nieprzerwanie stoi na 0 % — bliźniacze do <see cref="_progressFullSince"/>, do migawki i zrzutu.</summary>
    private ObservationTime? _progressZeroSince;

    private readonly ProgressBarSource? _barSource;

    /// <summary>Przycięty obszar pomiaru paska (tor + otoczenie, <see cref="ProgressBarReader.RequiredArea"/>) albo pusty.</summary>
    private RectInt _barAreaClamped;

    /// <summary>Pomiar paska z bieżącego cyklu (null — profil bez paska albo obserwacja postoju nieaktywna).</summary>
    private ProgressBarReading? _lastBar;

    /// <summary>Źródło procentu w migawce: "pasek", "ocr" albo null (patrz <see cref="MachineStatusSnapshot.LastProgressSource"/>).</summary>
    private string? _lastProgressSource;

    private readonly ProgramStateProfile? _programStateProfile;

    /// <summary>Pudełko stanu na ekranie (całe, nieprzycięte) albo pusty, gdy nie mieści się w ekranie.</summary>
    private RectInt _boxClamped;

    /// <summary>Odczyt pudełka stanu z bieżącego cyklu (null — profil bez programState albo pudełko poza ekranem).</summary>
    private ProgramStateReading? _programState;

    /// <summary>
    /// Czy OCR ostatniej rozstrzygniętej kontroli ekranu głównego (pełna klatka, co
    /// <see cref="TimingConfig.MainScreenCheckIntervalMs"/>) widział słowo „Abort” w pudełku stanu;
    /// <see langword="null"/> — brak rozstrzygniętego odczytu.
    /// </summary>
    private bool? _abortWordSeen;

    /// <summary>Filtr „2 z 3” próbek „program przerwany w trakcie pracy”.</summary>
    private readonly RedEpisodeFilter? _abortFilter;
    private bool _abortConfirmed;
    private string? _abortCaptureFile;

    private readonly MainScreenDetector? _mainScreenDetector;
    private readonly bool _mainScreenWatchEnabled;

    /// <summary>Kiedy ostatnio sprawdzono ekran główny (OCR pełnej klatki) — kolejne sprawdzenie dopiero po <see cref="TimingConfig.MainScreenCheckIntervalMs"/>.</summary>
    private ObservationTime? _lastMainScreenCheckAt;

    /// <summary>Wynik ostatniego sprawdzenia ekranu, także nierozstrzygniętego (null).</summary>
    private bool? _mainScreenVisible;
    private bool _alarmPageVisible;

    private bool StatusBarSurvivesOtherScreens =>
        _profile.MainScreen is { Enabled: true, StatusBarAlwaysVisible: true };

    /// <summary>Od kiedy HMI nieprzerwanie pokazuje inny ekran niż główny — do migawki i jednorazowego zrzutu (jak <see cref="_progressFullSince"/>).</summary>
    private ObservationTime? _wrongScreenSince;

    private string? _wrongScreenCaptureFile;

    // --- Stan źródła VNC (odtwarzany przez vncFactory przy (re)connect) --------------------------
    private IScreenSource? _vncSource;
    private int _backoffIndex;

    /// <summary>Kolejne odrzucenia hasła od ostatniego udanego połączenia — patrz <see cref="AuthRetryDelay"/>.</summary>
    private int _authFailures;

    private readonly QtGuardState _qtGuard;

    /// <summary>Czas monotoniczny udanego handshake'u bieżącej sesji, dopóki nie przyszła z niej pierwsza klatka; potem null.</summary>
    private long? _sessionAwaitingFirstFrameSinceMs;

    private long? _sessionConnectedAtMs;

    /// <summary>
    /// Monitor powstał w trakcie wstrzymania odziedziczonego po poprzednim monitorze tej maszyny (przebudowa) — migawka od początku
    /// pokazuje wstrzymanie; wpis INF w logu przy pierwszym cyklu czekania, potem flaga gaśnie.
    /// </summary>
    private bool _inheritedQtHold;

    // Dławienie logu błędów połączenia (LogVncFailure / LogVncRestored) — tylko wątek pętli tej maszyny.
    private int _failureSeriesCount;
    private int _failuresSuppressed;
    private string? _failureLoggedText;
    private DateTimeOffset? _failureLoggedAt;
    private DateTimeOffset? _offlineSince;
    private ObservationTime? _lastFullFrameAt;
    private volatile bool _forceFullFrame;
    private RgbaImage? _lastRoiImage;
    private bool _roiOutOfBounds;
    private bool _roiOutOfBoundsLogged;

    // --- Stan epizodu czerwieni/OCR --------------------------------------------------------------
    private bool _episodeActive;
    private int _ocrAttempts;
    private bool _ocrSatisfied;

    private enum RedAlarmDecision
    {
        /// <summary>Alarm jak zawsze (maszyna bez reguł wyciszających, brak OCR albo treść to nie wyciszony komunikat).</summary>
        Raise,

        /// <summary>Nowy epizod na maszynie z regułą wyciszającą: czekamy na pierwszy odczyt treści.</summary>
        Pending,

        /// <summary>OCR rozpoznał komunikat wyciszony — czerwień nie jest alarmem, dopóki treść się nie zmieni.</summary>
        Suppressed,
    }

    /// <summary>
    /// Najdłuższe czekanie z alarmem na pierwszy odczyt treści (<see cref="RedAlarmDecision.Pending"/>). Po tym
    /// czasie alarm i tak dzwoni — wolimy fałszywy alarm niż przeoczony, gdy OCR się spóźnia albo pada.
    /// </summary>
    internal const int SuppressDecisionTimeoutMs = 10000;

    /// <summary>
    /// Jak często ponawiać OCR, gdy pasek pokazuje komunikat wyciszony, nawet bez widocznej zmiany obrazu —
    /// zabezpieczenie na wypadek, gdyby prawdziwy błąd zmienił obraz poniżej progu <c>FrameDiff</c>.
    /// </summary>
    internal const int SuppressRecheckMs = 30000;

    internal const int SuppressReturnAfterMs = 60000;

    private RedAlarmDecision _redDecision = RedAlarmDecision.Raise;
    private ObservationTime? _episodeStartedAt;

    /// <summary>Od kiedy (w stanie „alarm”) OCR widzi wyłącznie komunikat wyciszony — patrz <see cref="SuppressReturnAfterMs"/>.</summary>
    private ObservationTime? _suppressedOnlySince;

    /// <summary>Opis ostatnio rozpoznanego komunikatu wyciszonego — do karty i diagnostyki.</summary>
    private string? _suppressedShortText;

    /// <summary>Czy trwający odczyt OCR ruszył z powodu ZMIANY obrazu paska (a nie okresowej kontroli).</summary>
    private bool _errorReadByChange;

    /// <summary>
    /// Od kiedy w BIEŻĄCYM (lokalnym) epizodzie czerwień jest NIEPRZERWANIE niewidoczna (filtr "2 z 3"
    /// nie potwierdza), albo <see langword="null"/>, gdy jest akurat widoczna. Histereza mrugania HMI:
    /// lokalny epizod (zrzut "blad", <see cref="_bestMapped"/>/<see cref="_ocrAttempts"/>) zamyka się
    /// dopiero, gdy przerwa trwa co najmniej <see cref="TimingConfig.EpisodeBreakAfterMs"/> — okienko
    /// statusu HMI między kolejnymi komunikatami w pętli potrafi na 1-2 s zgasnąć, a bez tej histerezy
    /// KAŻDA taka chwila otwierałaby nowy lokalny epizod (nowy zrzut, reset najlepszego komunikatu) —
    /// patrz <see cref="ProcessFrameAsync"/>. <see cref="AlertStateMachine"/> ma WŁASNĄ, analogiczną
    /// histerezę (<c>_redClearedSince</c>) — ta tutaj jest niezależna i dotyczy WYŁĄCZNIE stanu lokalnego
    /// tego monitora, NIE zmienia częstotliwości wywołań <see cref="AlertStateMachine.OnRedCleared"/>
    /// (wciąż wołane co cykl bez czerwieni).
    /// </summary>
    private ObservationTime? _redGoneSince;

    /// <summary>Kiedy ostatnio zapisano diagnostyczny zrzut "przerwa" (pierwsza klatka bez czerwieni po aktywnym epizodzie) — dławi zapis do najwyżej raz na 60 s na tę maszynę.</summary>
    private ObservationTime? _lastBreakCaptureAt;

    /// <summary>
    /// Ile najwyżej jeden zrzut pełnej klatki danego rodzaju na maszynę — patrz <see cref="_lastFullCaptureAt"/>.
    /// </summary>
    private static readonly TimeSpan FullCaptureThrottle = TimeSpan.FromHours(1);

    private const int FullCaptureDownscale = 2;

    private readonly Dictionary<string, ObservationTime> _lastFullCaptureAt = new(StringComparer.Ordinal);

    /// <summary>Wynik filtru "2 z 3" (<c>confirmed</c>) z POPRZEDNIEGO cyklu — do logu DBG tylko przy zmianie stanu (nie co cykl), patrz <see cref="ProcessFrameAsync"/>.</summary>
    private bool _prevConfirmed;

    /// <summary>Wynik OSTATNIEJ próby OCR w epizodzie (niezależnie, czy była "najlepsza") — steruje tylko
    /// gaszeniem prób (a) w <see cref="ProcessFrameAsync"/> (patrz <c>lastOcrWasEmpty</c>).</summary>
    private MappedMessage? _lastMapped;

    private MappedMessage? _bestMapped;

    /// <summary>Obraz ROI (pasek statusu) z chwili OSTATNIEGO OCR — baza porównania <see cref="Vision.FrameDiff"/>.</summary>
    private RgbaImage? _lastOcrRoi;

    /// <summary>Kiedy wykonano OSTATNI OCR w bieżącym epizodzie — gasi próby (b) do <see cref="TimingConfig.OcrRetryIntervalMs"/>.</summary>
    private ObservationTime? _lastOcrAt;

    /// <summary>Znormalizowane teksty (patrz <see cref="Ocr.TextNormalizer.Normalize"/>) już zalogowane w bieżącym epizodzie — do logu "kolejny komunikat…" (bez powtórzeń).</summary>
    private readonly HashSet<string> _episodeTexts = new(StringComparer.Ordinal);

    private string? _currentCaptureFile;

    // --- Ostatnie zaobserwowane wartości (do budowy Snapshot) -------------------------------------
    private MachineConnectionState _state;
    private string? _lastError;
    private DateTimeOffset? _lastFrameAt;
    private double _lastRedFraction;
    private bool _redNow;
    private bool _usingCachedFrame;
    private bool _redConfirmed;
    private string? _lastOcrText;
    private string? _lastShortText;
    private ScreenSourceInfo? _lastInfo;
    private string _lastSourceLabel = "-";

    public MachineMonitor(
        MachineConfig machine,
        ScreenProfile profile,
        Func<IScreenSource> vncFactory,
        FolderScreenSource? testOverride,
        IOcrEngine ocr,
        MessageMapper mapper,
        AlertStateMachine alerts,
        IClock clock,
        TimingConfig timing,
        ICaptureStore captures,
        ILogger? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        DiagnosticsConfig? diagnostics = null,
        QtGuardState? qtGuard = null)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _qtGuard = qtGuard is not null && qtGuard.Matches(machine) ? qtGuard : new QtGuardState(machine.Host, machine.Port);
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _vncFactory = vncFactory ?? throw new ArgumentNullException(nameof(vncFactory));
        _testOverride = testOverride;
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _captures = captures ?? throw new ArgumentNullException(nameof(captures));
        _diagnostics = diagnostics ?? new DiagnosticsConfig();
        _logger = logger ?? NullLogger.Instance;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));

        _detector = new RedTextDetector(profile);
        _filter = new RedEpisodeFilter(3, 2);

        bool ocrSupported = _ocr is SerializingOcrEngine serial ? serial.Inner.IsAvailable : _ocr.IsAvailable;
        _idleProfile = profile.Idle is { Enabled: true } i ? i : null;
        if (_idleProfile is not null)
        {
            if (_idleProfile.Mode == ProgressMode.Number && !ocrSupported && _idleProfile.ProgressBar is not { Enabled: true })
            {
                // Tryb number wymaga OCR (nie ma tu koloru wypełnienia do sprawdzenia jak w trybie bar) —
                // bez dostępnego silnika (np. brak pakietu językowego Windows) obserwacja postoju jest
                // dla tej maszyny bezużyteczna: zamiast cichego "zawsze 0%" (co dałoby fałszywy alert po
                // czasie IdleAlertAfterMs) WYŁĄCZAMY ją całkowicie — dokładnie jak profil bez sekcji "idle"
                // (_idleProfile = null poniżej) — i ostrzegamy RAZ, przy starcie tego obserwatora. Detekcja
                // czerwieni (najważniejsza funkcja programu) nie jest tym w żaden sposób dotknięta.
                _logger.LogWarning(
                    "Maszyna '{MachineId}': obserwacja postoju w trybie 'number' (profil '{Profile}') wymaga silnika OCR, a silnik ('{Engine}') jest niedostępny — obserwacja postoju dla tej maszyny jest wyłączona, wykrywanie błędów działa dalej bez zmian.",
                    _machine.Id, profile.Id, _ocr.Name);
                _idleProfile = null;
            }
            else if (_idleProfile.Mode == ProgressMode.Bar)
            {
                _progressDetector = new ProgressBarDetector(_idleProfile);
                _fullFilter = new RedEpisodeFilter(3, 2);
            }
            else
            {
                _numberReader = new ProgressNumberReader(_idleProfile, profile.DarkTheme, _ocr, _logger, _clock);
                _fullFilter = new RedEpisodeFilter(3, 2);
                _zeroFilter = new RedEpisodeFilter(3, 2);
            }
        }

        // Pasek postępu działa bez OCR, więc także przy niedostępnym silniku (wtedy jest jedynym źródłem procentu).
        _barSource = _idleProfile is { Mode: ProgressMode.Number, ProgressBar: { Enabled: true } bar } ? bar : null;
        _programStateProfile = profile.ProgramState is { Enabled: true } programState ? programState : null;
        if (_programStateProfile is not null)
        {
            _abortFilter = new RedEpisodeFilter(3, 2);
        }

        MainScreenWatchProfile? mainScreen = profile.MainScreen is { Enabled: true } m ? m : null;
        if (mainScreen is not null)
        {
            if (!ocrSupported)
            {
                // Jak tryb number obserwacji postoju: rozpoznanie ekranu głównego to OCR etykiety, bez
                // silnika nie ma czego sprawdzać — wyłączamy TYLKO tę funkcję i ostrzegamy raz.
                _logger.LogWarning(
                    "Maszyna '{MachineId}': obserwacja ekranu głównego HMI (sekcja 'mainScreen' profilu '{Profile}') wymaga silnika OCR, a silnik ('{Engine}') jest niedostępny — brak wiarygodnej kontroli ekranu. Kolor może podnieść alarm, ale nie potwierdzi naprawy.",
                    _machine.Id, profile.Id, _ocr.Name);
            }
            else
            {
                _mainScreenDetector = new MainScreenDetector(mainScreen, _ocr, _logger);
                _mainScreenWatchEnabled = true;
            }
        }

        // Dopóki nie znamy rozmiaru ekranu (przed pierwszą pełną klatką), obszar pobierany to sam ROI
        // paska statusu — UpdateIdleBounds poprawi to przy pierwszym cyklu, jeśli trzeba.
        _fetchArea = _profile.StatusBarRoi;
        _statusRoiClamped = _profile.StatusBarRoi;

        _state = _machine.Enabled ? MachineConnectionState.Disconnected : MachineConnectionState.Disabled;
        if (_machine.Enabled && _qtGuard.QtServer)
        {
            _lastInfo = _qtGuard.QtInfo;
        }

        if (_machine.Enabled && QtGuardHold() is not null)
        {
            _state = MachineConnectionState.Error;
            _lastError = _qtGuard.HoldError;
            _inheritedQtHold = true;
        }

        Snapshot = BuildSnapshot();
    }

    /// <summary>Migawka bieżącego stanu tej maszyny — aktualizowana po każdym cyklu pętli <see cref="RunAsync"/>.</summary>
    public MachineStatusSnapshot Snapshot { get; private set; }

    /// <summary>Zdarzenie wysyłane, gdy w <see cref="Snapshot"/> zmieniło się coś istotnego (stan, czerwień, tekst, źródło).</summary>
    public event Action<MachineStatusSnapshot>? StatusChanged;

    /// <summary>Ostatnia pełna klatka ekranu maszyny (VNC albo plik testowy) — do podglądu „Pokaż ekran” w oknie statusu.</summary>
    public RgbaImage? LastFullFrame => PreviewFrame?.Image;
    private MachinePreviewFrame? _previewFrame;
    public MachinePreviewFrame? PreviewFrame => Volatile.Read(ref _previewFrame);

    private void PublishPreview(RgbaImage frame, string source) =>
        Volatile.Write(ref _previewFrame, new(frame, _clock.UtcNow, _clock.MonotonicMs, source));

    /// <summary>Żąda pełnej klatki (zamiast tylko ROI) w NASTĘPNYM cyklu — np. gdy operator otwiera podgląd ekranu.</summary>
    public Task RequestFullRefreshAsync()
    {
        _forceFullFrame = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Główna pętla obserwatora — działa aż do anulowania <paramref name="ct"/>. Maszyna wyłączona
    /// w konfiguracji (<c>MachineConfig.Enabled = false</c>) nigdy się nie łączy — ustawia migawkę
    /// na <see cref="MachineConnectionState.Disabled"/> i po prostu czeka na anulowanie.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!_machine.Enabled)
        {
            _state = MachineConnectionState.Disabled;
            _lastSourceLabel = "-";
            PublishSnapshot();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Oczekiwane przy zatrzymywaniu (MonitorSupervisor.StopAsync).
            }

            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                long cycleStartMs = _clock.MonotonicMs;
                TimeSpan? backoffOverride = null;

                try
                {
                    backoffOverride = await RunCycleAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Nieoczekiwany błąd w pętli monitorowania maszyny '{MachineId}'.", _machine.Id);
                    ResetSourceObservation();
                    _state = MachineConnectionState.Error;
                    _lastError = "Błąd obserwacji: " + ex.Message;
                    PublishSnapshot();
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                TimeSpan interval = backoffOverride ?? NormalInterval();
                TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, _clock.MonotonicMs - cycleStartMs));
                TimeSpan remaining = interval - elapsed;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                try
                {
                    await _delay(remaining, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            AbandonErrorRead();
            IScreenSource? toDispose = _vncSource;
            _vncSource = null;
            RememberStableQtSession(toDispose);
            await DisposeSourceAsync(toDispose).ConfigureAwait(false);
        }
    }

    private void RememberStableQtSession(IScreenSource? source)
    {
        if (source is not null && _qtGuard.QtServer && _machine.Mode == ConnectionMode.Persistent
            && _sessionConnectedAtMs is { } since && _sessionAwaitingFirstFrameSinceMs is null)
        {
            _qtGuard.StableSinceMs = since;
        }
    }

    private TimeSpan NormalInterval() => TimeSpan.FromMilliseconds(
        _machine.Mode == ConnectionMode.Polling ? _timing.PollingModeIntervalMs : _timing.PollIntervalMs);

    /// <summary>Wykonuje jeden cykl (pobranie obrazu, detekcja, OCR, alert, publikacja migawki). Zwraca odstęp „backoff” do użycia zamiast normalnego interwału, gdy połączenie VNC właśnie zawiodło.</summary>
    private async Task<TimeSpan?> RunCycleAsync(CancellationToken ct)
    {
        bool useTestOverride = _testOverride is not null && _testOverride.FileExists;
        string sourceLabel = useTestOverride ? "plik" : "vnc";
        if (_lastSourceLabel != sourceLabel)
        {
            ResetSourceObservation();
        }
        TimeSpan? backoffOverride = null;
        RgbaImage? roiImage;

        if (useTestOverride)
        {
            _lastSourceLabel = "plik";
            // Nie zostawiaj sesji VNC bez obsługi podczas testu z pliku.
            var previousVnc = _vncSource;
            _vncSource = null;
            _sessionAwaitingFirstFrameSinceMs = null;
            _sessionConnectedAtMs = null;
            _qtGuard.StableSinceMs = null;
            await DisposeSourceAsync(previousVnc).ConfigureAwait(false);
            roiImage = await CaptureFromTestOverrideAsync(ct).ConfigureAwait(false);
        }
        else
        {
            _lastSourceLabel = "vnc";
            (roiImage, backoffOverride) = await CaptureFromVncAsync(ct).ConfigureAwait(false);
        }

        if (roiImage is not null)
        {
            await ProcessFrameAsync(roiImage, ct).ConfigureAwait(false);
        }

        PublishSnapshot();
        return backoffOverride;
    }

    // ---------------------------------------------------------------------------------------------
    // Źródło: plik testowy (test-ekran/<id>.png)
    // ---------------------------------------------------------------------------------------------

    private async Task<RgbaImage?> CaptureFromTestOverrideAsync(CancellationToken ct)
    {
        try
        {
            RgbaImage full = await _testOverride!.CaptureFullAsync(ct).ConfigureAwait(false);
            PublishPreview(full, "plik");
            _lastFullFrameAt = ObservationTime.Read(_clock);
            _lastFrameAt = _clock.UtcNow;
            _usingCachedFrame = false;
            _lastInfo = _testOverride.Info;

            // Bezwarunkowo (idempotentnie) zamyka ewentualny wiszący alert "brak łączności":
            // dotyczy też restartu nadzorcy (nowy MachineMonitor zaczyna z _offlineSince == null) i trybu pliku.
            _offlineSince = null;
            _alerts.OnMachineOnline(_machine.Id);

            RectInt clamped = _profile.StatusBarRoi.Clamp(full.Width, full.Height);
            if (clamped.IsEmpty)
            {
                ReportRoiOutOfBounds(full.Width, full.Height, "plik");
                return null;
            }

            UpdateIdleBounds(full.Width, full.Height, clamped, "plik");
            AcceptRoiInBounds();
            await CheckMainScreenIfDueAsync(full, ct).ConfigureAwait(false);
            return full.Crop(_fetchArea);
        }
        catch (ScreenSourceException ex)
        {
            // Plik istnieje (FileExists==true), ale chwilowo nie da się go odczytać (zapisywany/uszkodzony) —
            // traktujemy to jak brak odczytu W TYM cyklu; NIE ruszamy stanu połączenia VNC ani offline.
            ResetSourceObservation();
            _state = MachineConnectionState.Error;
            _lastError = "Nie można odczytać pliku testowego ekranu.";
            _logger.LogDebug(ex, "Plik testowego ekranu maszyny '{MachineId}' chwilowo niedostępny: {Error}", _machine.Id, ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Źródło: VNC
    // ---------------------------------------------------------------------------------------------

    private async Task<(RgbaImage? RoiImage, TimeSpan? BackoffOverride)> CaptureFromVncAsync(CancellationToken ct)
    {
        if (_vncSource is null && QtGuardHold() is { } hold)
        {
            _state = MachineConnectionState.Error;
            if (_inheritedQtHold)
            {
                // Nowy monitor (przebudowa przez nadzorcę) w trakcie wstrzymania — stan bezpiecznika przeżył przebudowę: karta maszyny pokazuje opis wstrzymania sprzed przebudowy, log — że ono trwa.
                _inheritedQtHold = false;
                _lastError ??= _qtGuard.HoldError;
                _logger.LogInformation(
                    "Maszyna '{MachineId}': bezpiecznik serwera VNC Qt nadal wstrzymuje próby połączenia po przebudowie monitora — " +
                    "następna próba za {Wait}.",
                    _machine.Id, FormatWait(TimeSpan.FromMilliseconds(_qtGuard.NextAttemptMs!.Value - _clock.MonotonicMs)));
            }

            _alerts.OnMachineOffline(_machine, _offlineSince ??= _clock.UtcNow);
            return (null, hold);
        }

        _vncSource ??= _vncFactory();

        if (!_vncSource.IsConnected)
        {
            try
            {
                await _vncSource.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (null, await HandleVncFailureAsync(ex).ConfigureAwait(false));
            }

            _forceFullFrame = true;
            _qtGuard.QtServer = QtEmbeddedVncServer.IsQtEmbedded(_vncSource.Info?.DesktopName);
            _qtGuard.QtInfo = _qtGuard.QtServer ? _vncSource.Info : null;
            if (_qtGuard.QtServer)
            {
                _lastInfo = _vncSource.Info;
            }

            long connectedAt = _clock.MonotonicMs;
            _sessionAwaitingFirstFrameSinceMs = connectedAt;
            _sessionConnectedAtMs = _qtGuard.QtServer && _machine.Mode == ConnectionMode.Persistent && _qtGuard.StableSinceMs is { } stable
                ? stable
                : connectedAt;
            _qtGuard.StableSinceMs = null;
        }

        try
        {
            // Dopóki ROI profilu leży poza ekranem, każdy cykl sprawdza rozmiar ponownie pełną klatką
            // (zamiast pytać serwer o przyrostową aktualizację nieprawidłowego obszaru).
            bool doFullFrame = _roiOutOfBounds
                || _machine.Mode == ConnectionMode.Polling
                || _forceFullFrame
                || _lastFullFrameAt is null
                || (_lastFullFrameAt.Value.Age(_clock)).TotalMilliseconds >= _timing.FullFrameIntervalMs
                || MainScreenCheckDue();

            _usingCachedFrame = false;
            RgbaImage? roiImage;
            if (doFullFrame)
            {
                RgbaImage full = await _vncSource.CaptureFullAsync(ct).ConfigureAwait(false);
                OnVncFrameReceived();
                PublishPreview(full, "vnc");
                _lastFullFrameAt = ObservationTime.Read(_clock);
                _forceFullFrame = false;

                RectInt clamped = _profile.StatusBarRoi.Clamp(full.Width, full.Height);
                if (clamped.IsEmpty)
                {
                    _lastFrameAt = _clock.UtcNow;
                    _lastInfo = _vncSource.Info;
                    _offlineSince = null;
                    _alerts.OnMachineOnline(_machine.Id);
                    ReportRoiOutOfBounds(full.Width, full.Height, "vnc");
                    return (null, null);
                }

                UpdateIdleBounds(full.Width, full.Height, clamped, "vnc");
                roiImage = full.Crop(_fetchArea);
                await CheckMainScreenIfDueAsync(full, ct).ConfigureAwait(false);
            }
            else if (_machine.Mode == ConnectionMode.Persistent)
            {
                // Żądanie przyrostowe z limitem bezczynności — serwer WinCE/OpenCnc na
                // niezmienionym ekranie potrafi nie odpowiadać w ogóle, dopóki coś się nie zmieni.
                // ROI to _fetchArea: sam StatusBarRoi, gdy obserwacja postoju jest wyłączona,
                // albo suma StatusBarRoi ∪ ProgressRoi, gdy jest aktywna — patrz UpdateIdleBounds.
                RgbaImage? fresh = await _vncSource.TryCaptureAsync(
                    _fetchArea,
                    incremental: true,
                    TimeSpan.FromMilliseconds(_timing.IdleUpdateTimeoutMs),
                    ct).ConfigureAwait(false);

                if (fresh is not null)
                {
                    roiImage = fresh;
                }
                else if (_lastRoiImage is not null)
                {
                    // Brak odpowiedzi w oknie oczekiwania = brak zmian na ekranie. Analizujemy ponownie
                    // ostatni znany obraz ROI — NIE pomijamy cyklu (inaczej LastSeenAt/epizod by nie
                    // dostały odświeżenia, a trwający błąd zamknąłby się po EpisodeCloseAfterMs).
                    roiImage = _lastRoiImage;
                    _usingCachedFrame = true;
                }
                else
                {
                    // Nie mamy jeszcze żadnego obrazu ROI (np. tuż po starcie) — wymuś pełny odczyt.
                    roiImage = await _vncSource.CaptureAsync(_fetchArea, incremental: false, ct).ConfigureAwait(false);
                }
            }
            else
            {
                roiImage = await _vncSource.CaptureAsync(_fetchArea, incremental: true, ct).ConfigureAwait(false);
            }

            AcceptRoiInBounds();
            if (!_usingCachedFrame) _lastFrameAt = _clock.UtcNow;
            OnVncFrameReceived();
            _backoffIndex = 0;
            _authFailures = 0;
            LogVncRestored();
            _lastInfo = _vncSource.Info;
            _offlineSince = null;
            _alerts.OnMachineOnline(_machine.Id);
            _lastRoiImage = roiImage;

            return (roiImage, null);
        }
        catch (RfbScreenResizedException resized)
        {
            _forceFullFrame = true;
            _lastInfo = _vncSource?.Info;
            _alerts.OnMachineOnline(_machine.Id);
            ReportRoiOutOfBounds(resized.Width, resized.Height, "vnc");
            return (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, await HandleVncFailureAsync(ex).ConfigureAwait(false));
        }
    }

    internal static readonly TimeSpan RepeatedFailureLogInterval = TimeSpan.FromMinutes(5);

    private void LogVncFailure(Exception ex, DateTimeOffset now)
    {
        _failureSeriesCount++;
        bool sameAsLogged = string.Equals(_failureLoggedText, _lastError, StringComparison.Ordinal);
        if (sameAsLogged && _failureLoggedAt is { } at && now - at < RepeatedFailureLogInterval)
        {
            _failuresSuppressed++;
            return;
        }

        if (sameAsLogged)
        {
            _logger.LogWarning("Błąd VNC maszyny '{MachineId}': {Error} (powtórzono {Count} razy od {Since:HH:mm})",
                _machine.Id, _lastError, _failuresSuppressed + 1, _failureLoggedAt!.Value.ToLocalTime());
        }
        else if (ex is RfbAuthenticationException)
        {
            // Brak hasła VNC / odrzucone hasło to BŁĄD KONFIGURACJI, nie awaria sieci/maszyny — logujemy BEZ
            // obiektu wyjątku (bez śladu stosu). RfbClient rozróżnia w treści „brak hasła” od „hasło odrzucone”.
            _logger.LogWarning("Błąd VNC maszyny '{MachineId}': {Error}", _machine.Id, _lastError);
        }
        else
        {
            // Pierwszy (albo zmieniony) błąd serii — ślad stosu zostaje, bez niego nie da się dojść, gdzie pękło.
            _logger.LogWarning(ex, "Błąd VNC maszyny '{MachineId}': {Error}", _machine.Id, _lastError);
        }

        _failureLoggedText = _lastError;
        _failureLoggedAt = now;
        _failuresSuppressed = 0;
    }

    /// <summary>Jeden wpis po udanym połączeniu, gdy wcześniej była seria błędów (para do <see cref="LogVncFailure"/>).</summary>
    private void LogVncRestored()
    {
        if (_failureSeriesCount == 0) return;
        _logger.LogInformation("Połączenie z maszyną '{MachineId}' przywrócone po {Count} nieudanych próbach.",
            _machine.Id, _failureSeriesCount);
        _failureSeriesCount = 0;
        _failuresSuppressed = 0;
        _failureLoggedText = null;
        _failureLoggedAt = null;
    }

    private void NoteFailureLoggedByQtGuard(DateTimeOffset now)
    {
        _failureSeriesCount++;
        _failureLoggedText = _lastError;
        _failureLoggedAt = now;
        _failuresSuppressed = 0;
    }

    /// <summary>Uzasadnienie odstępu po odrzuconym haśle (zdanie po „Kolejna próba za …”).</summary>
    private const string AuthRetryReason =
        "po odrzuceniu hasła program ponawia coraz rzadziej, żeby serwer nie zablokował adresu tego komputera. " +
        "Popraw hasło w Panelu → Ustawienia → Ten komputer.";

    private const string AuthRetryNote =
        "Po odrzuceniu hasła program ponawia coraz rzadziej, żeby serwer nie zablokował adresu tego komputera. " +
        "Popraw hasło w Panelu → Ustawienia → Ten komputer.";

    private async Task<TimeSpan> HandleVncFailureAsync(Exception ex)
    {
        DateTimeOffset now = _clock.UtcNow;

        _state = MachineConnectionState.Error;
        _lastError = DescribeError(ex);
        _offlineSince ??= now;

        QtFailure qt = ClassifyQtFailure(ex);
        bool newFailureText = !string.Equals(_failureLoggedText, _lastError, StringComparison.Ordinal);
        if (qt.Kind == QtFailureKind.None)
        {
            LogVncFailure(ex, now);
        }
        else
        {
            NoteFailureLoggedByQtGuard(now);
        }

        // Utrata łączności (typowo restart maszyny po błędzie) zamyka lokalny epizod czerwieni NATYCHMIAST,
        // bez czekania na histerezę _redGoneSince (patrz ProcessFrameAsync) — nie wiadomo, co się działo na
        // ekranie w czasie, gdy nie mieliśmy obrazu, więc kolejny odczyt po powrocie online ma zacząć od zera.
        if (_episodeActive)
        {
            _logger.LogInformation(
                "Maszyna '{MachineId}': epizod czerwieni zamknięty (powód: utrata łączności, najlepszy komunikat: '{ShortText}').",
                _machine.Id, _bestMapped?.ShortText ?? "-");
            _episodeActive = false;
            _redGoneSince = null;
        }

        ResetSourceObservation();
        _alerts.OnMachineOffline(_machine, _offlineSince.Value);

        IScreenSource? failed = _vncSource;
        _vncSource = null;
        await DisposeSourceAsync(failed).ConfigureAwait(false);

        int[] backoff = _timing.ReconnectBackoffMs;
        TimeSpan wait = backoff.Length > 0
            ? TimeSpan.FromMilliseconds(backoff[Math.Min(_backoffIndex, backoff.Length - 1)])
            : NormalInterval();
        _backoffIndex++;

        bool authFailure = ex is RfbAuthenticationException;
        if (authFailure)
        {
            _authFailures++;
            wait = AuthRetryDelay(_authFailures, backoff.Length > 0 ? TimeSpan.FromMilliseconds(backoff[^1]) : wait);
        }

        if (qt.Kind == QtFailureKind.None)
        {
            if (authFailure)
            {
                _lastError += $" Kolejna próba za {FormatWait(wait)} — {AuthRetryReason}";
            }

            return wait;
        }

        return ApplyQtGuard(qt, wait, authFailure, newFailureText && !authFailure ? ex : null);
    }

    internal static readonly TimeSpan[] QtGuardDelays = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    internal static readonly TimeSpan QtEarlyCloseWindow = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan[] QtTakeoverDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>Sesja stabilna co najmniej tyle przed przejęciem zaczyna odstępy przejęcia od nowa (od 1 min).</summary>
    internal static readonly TimeSpan QtTakeoverResetAfter = TimeSpan.FromMinutes(10);

    private TimeSpan ApplyQtGuard(QtFailure qt, TimeSpan wait, bool authFailure, Exception? stackTrace)
    {
        string error = _lastError!;
        if (qt.Kind == QtFailureKind.Takeover)
        {
            return ApplyQtTakeover(qt.SessionLengthMs, wait, error, authFailure, stackTrace);
        }

        TimeSpan guardWait = QtGuardDelays[Math.Min(_qtGuard.GuardStep, QtGuardDelays.Length - 1)];
        _qtGuard.GuardStep++;
        if (guardWait < wait) guardWait = wait;

        string reason;
        if (qt.Kind is QtFailureKind.EarlyClose or QtFailureKind.ProtocolError)
        {
            string trigger = qt.Kind == QtFailureKind.ProtocolError
                ? "serwer VNC Qt przysłał dane niezgodne z protokołem"
                : "serwer VNC Qt zamknął połączenie przed pierwszą klatką";
            _logger.LogWarning(stackTrace,
                "Maszyna '{MachineId}': {Trigger} — możliwa awaria HMI; następna próba za {Wait} ({Error}).",
                _machine.Id, trigger, FormatWait(guardWait), error);
            reason = $"{trigger}, możliwa awaria HMI; program ponawia rzadko, żeby nie łączyć się z HMI zaraz po jego ponownym starcie.";
        }
        else
        {
            _logger.LogWarning(stackTrace,
                "Maszyna '{MachineId}': bezpiecznik serwera VNC Qt (wcześniej zamknięcie przed pierwszą klatką albo błąd protokołu) — " +
                "kolejna nieudana próba; następna próba za {Wait} ({Error}).",
                _machine.Id, FormatWait(guardWait), error);
            reason = "bezpiecznik serwera VNC Qt (wcześniej możliwa awaria HMI).";
        }

        return HoldQtServer(guardWait, reason, authFailure, wait);
    }

    private enum QtFailureKind
    {
        None,

        EarlyClose,

        /// <summary>(b) Błąd protokołu (<see cref="RfbProtocolException"/>).</summary>
        ProtocolError,

        /// <summary>Kolejna nieudana próba przy aktywnym bezpieczniku (a)/(b).</summary>
        GuardActive,

        /// <summary>(c) Przejęcie stałej sesji, która dała klatki.</summary>
        Takeover,
    }

    private readonly record struct QtFailure(QtFailureKind Kind, long SessionLengthMs = 0);

    private QtFailure ClassifyQtFailure(Exception ex)
    {
        long? sessionSince = _sessionAwaitingFirstFrameSinceMs;
        long? sessionConnectedAt = _sessionConnectedAtMs;
        _sessionAwaitingFirstFrameSinceMs = null;
        _sessionConnectedAtMs = null;
        _qtGuard.StableSinceMs = null; // awaria przerywa ciągłość stabilnej obserwacji (także sprzed przebudowy monitora)
        if (!_qtGuard.QtServer)
        {
            return default;
        }

        if (ex is RfbProtocolException)
        {
            return new(QtFailureKind.ProtocolError);
        }

        if (ex is not RfbAuthenticationException && sessionSince is { } since
            && _clock.MonotonicMs - since <= QtEarlyCloseWindow.TotalMilliseconds)
        {
            return new(QtFailureKind.EarlyClose);
        }

        if (ex is not RfbAuthenticationException && _qtGuard.GuardStep == 0 && _machine.Mode == ConnectionMode.Persistent
            && sessionConnectedAt is { } connectedAt && sessionSince is null)
        {
            // Tylko stała sesja: w trybie polling każde odpytanie to osobne połączenie, którego monitor nie widzi (zna tylko
            // swój ConnectAsync sprzed wielu cykli) — nieudane odpytanie nie jest przejęciem.
            return new(QtFailureKind.Takeover, _clock.MonotonicMs - connectedAt);
        }

        return _qtGuard.GuardStep > 0 ? new(QtFailureKind.GuardActive) : default;
    }

    private TimeSpan HoldQtServer(TimeSpan holdWait, string reason, bool authFailure, TimeSpan wait)
    {
        _qtGuard.NextAttemptMs = _clock.MonotonicMs + (long)holdWait.TotalMilliseconds;
        _lastError += $" Kolejna próba za {FormatWait(holdWait)} — {reason}";
        if (authFailure)
        {
            _lastError += " " + AuthRetryNote;
        }

        _qtGuard.HoldError = _lastError; // opis wstrzymania dla następnego monitora tej maszyny (przebudowa w jego trakcie)
        return QtGuardHold() ?? wait;
    }

    private TimeSpan ApplyQtTakeover(long sessionLengthMs, TimeSpan wait, string error, bool authFailure, Exception? stackTrace)
    {
        if (sessionLengthMs >= QtTakeoverResetAfter.TotalMilliseconds) _qtGuard.TakeoverStep = 0;
        TimeSpan takeoverWait = QtTakeoverDelays[Math.Min(_qtGuard.TakeoverStep, QtTakeoverDelays.Length - 1)];
        _qtGuard.TakeoverStep++;
        if (takeoverWait < wait) takeoverWait = wait;

        _logger.LogWarning(stackTrace,
            "Maszyna '{MachineId}': inny klient VNC przejął {MachineName} (serwer Qt, jedno połączenie naraz) — sprawdź, czy RealVNC " +
            "nie łączy się z tą maszyną; następna próba za {Wait} ({Error}).",
            _machine.Id, _machine.Name, FormatWait(takeoverWait), error);
        return HoldQtServer(takeoverWait,
            "inny klient VNC przejął tę maszynę (serwer obsługuje jedno połączenie); sprawdź, czy RealVNC nie łączy się z tą maszyną.",
            authFailure, wait);
    }

    private TimeSpan? QtGuardHold()
    {
        if (_qtGuard.NextAttemptMs is not { } next) return null;
        long remainingMs = next - _clock.MonotonicMs;
        if (remainingMs <= 0) return null;

        int[] backoff = _timing.ReconnectBackoffMs;
        long refreshMs = Math.Max(1000, backoff.Length > 0 ? backoff.Max() : (long)NormalInterval().TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Min(remainingMs, refreshMs));
    }

    private void OnVncFrameReceived()
    {
        _sessionAwaitingFirstFrameSinceMs = null;
        _qtGuard.NextAttemptMs = null;
        _qtGuard.HoldError = null;
        if (_qtGuard.GuardStep == 0) return;
        _logger.LogInformation(
            "Maszyna '{MachineId}': serwer VNC Qt przysłał klatkę — bezpiecznik ponowień zdjęty po {Steps} dłuższych odstępach.",
            _machine.Id, _qtGuard.GuardStep);
        _qtGuard.GuardStep = 0;
    }

    /// <summary>Najdłuższy odstęp między próbami po odrzuconym haśle.</summary>
    internal static readonly TimeSpan AuthRetryMax = TimeSpan.FromMinutes(30);

    /// <summary>Odstęp po <paramref name="failures"/>. kolejnym odrzuceniu hasła: <paramref name="first"/> × 2^(n−1), najwyżej <see cref="AuthRetryMax"/>.</summary>
    internal static TimeSpan AuthRetryDelay(int failures, TimeSpan first)
    {
        if (first <= TimeSpan.Zero) first = TimeSpan.FromSeconds(30);
        double factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 30));
        return TimeSpan.FromMilliseconds(Math.Min(first.TotalMilliseconds * factor, AuthRetryMax.TotalMilliseconds));
    }

    private static string FormatWait(TimeSpan wait) =>
        wait.TotalMinutes >= 1 ? $"{Math.Round(wait.TotalMinutes):0} min" : $"{Math.Round(wait.TotalSeconds):0} s";

    // ---------------------------------------------------------------------------------------------
    // ROI profilu poza ekranem maszyny
    // ---------------------------------------------------------------------------------------------

    private void AcceptRoiInBounds()
    {
        _state = MachineConnectionState.Connected;
        _lastError = null;
        if (_roiOutOfBounds)
        {
            _roiOutOfBounds = false;
            _roiOutOfBoundsLogged = false;
        }
    }

    /// <summary>
    /// Obszar paska statusu profilu leży poza aktualnym rozmiarem ekranu maszyny — błąd konfiguracji,
    /// NIE utrata łączności: nie łączymy ponownie w pętli, nie zgłaszamy offline, logujemy raz (nie co
    /// cykl). Sprawdzenie powtarza się w kolejnych cyklach, więc zmiana rozmiaru
    /// ekranu (albo poprawka configu i restart) sama wróci do normy.
    /// </summary>
    private void ReportRoiOutOfBounds(int width, int height, string source)
    {
        AbandonErrorRead();
        _roiOutOfBounds = true;
        _state = MachineConnectionState.Error;
        _lastError =
            $"Obszar paska statusu (statusBarRoi) profilu '{_profile.Id}' leży poza ekranem maszyny ({width}×{height}) – popraw config.json";

        if (!_roiOutOfBoundsLogged)
        {
            _logger.LogError(
                "Maszyna '{MachineId}' ({Source}): obszar paska statusu (statusBarRoi) profilu '{Profile}' leży poza ekranem maszyny ({Width}x{Height}) – popraw config.json.",
                _machine.Id, source, _profile.Id, width, height);
            _roiOutOfBoundsLogged = true;
        }
    }


    /// <summary>
    /// Przelicza <see cref="_fetchArea"/> (i włącznik <see cref="_idleWatchEnabled"/>) wobec AKTUALNEGO
    /// rozmiaru ekranu maszyny — wołane przy KAŻDEJ pełnej klatce (VNC albo plik testowy), bo tylko wtedy
    /// znamy <paramref name="screenWidth"/>/<paramref name="screenHeight"/>. Gdy profil nie ma sekcji
    /// "idle" (<see cref="_idleProfile"/> jest <see langword="null"/>), po prostu ustawia
    /// <see cref="_fetchArea"/> na <paramref name="statusRoiClamped"/>. W przeciwnym razie: gdy <c>ProgressRoi</c> po
    /// przycięciu do rozmiaru ekranu okazuje się pusty (błąd konfiguracji — ROI CAŁKOWICIE poza ekranem),
    /// wyłącza obserwację postoju (jednorazowy log ostrzegawczy, jak <see cref="ReportRoiOutOfBounds"/>
    /// dla statusBarRoi) i również cofa <see cref="_fetchArea"/> do samego paska statusu — wykrywanie
    /// czerwieni na pasku statusu działa dalej bez zmian. Gdy ROI jest przynajmniej częściowo na ekranie
    /// (Clamp go tylko przycina, jak <paramref name="statusRoiClamped"/>), oblicza sumę obu obszarów.
    /// </summary>
    private void UpdateIdleBounds(int screenWidth, int screenHeight, RectInt statusRoiClamped, string source)
    {
        _statusRoiClamped = statusRoiClamped;

        // Pudełko stanu programu (profil z sekcją programState) leży obok paska statusu i jest pobierane
        // w każdym cyklu razem z nim. Liczy się tylko CAŁE pudełko — przycięte dałoby inny obraz niż wzorzec.
        _boxClamped = _programStateProfile is { } programState
            && programState.BoxRoi.Clamp(screenWidth, screenHeight) == programState.BoxRoi
                ? programState.BoxRoi
                : RectInt.Empty;
        RectInt baseArea = _boxClamped.IsEmpty ? statusRoiClamped : statusRoiClamped.Union(_boxClamped);

        if (_idleProfile is null)
        {
            _fetchArea = baseArea;
            _barAreaClamped = RectInt.Empty;
            return;
        }

        RectInt progressClamped = _idleProfile.ProgressRoi.Clamp(screenWidth, screenHeight);

        if (progressClamped.IsEmpty)
        {
            bool wasEnabled = _idleWatchEnabled;
            _idleWatchEnabled = false;
            _fetchArea = baseArea;
            _barAreaClamped = RectInt.Empty;
            _lastBar = null;

            if (wasEnabled)
            {
                // Wracamy z "aktywna" do "wyłączona" — resetujemy stan obserwacji, żeby ewentualne
                // potwierdzone 100% sprzed awarii konfiguracji nie "wisiało" bez odświeżania.
                _fullFilter!.Reset();
                _zeroFilter?.Reset();
                _idleCaptureFile = null;
                _lastProgressFraction = 0;
                _progressFullNow = false;
                _progressFullConfirmed = false;
                _progressFullSince = null;
                _progressZeroSince = null;
                _lastProgressPercent = null;
                _lastProgressDisplayAt = null;
                _lastProgressText = null;
                _lastProgressSource = null;
            }

            if (!_idleRoiOutOfBoundsLogged)
            {
                _logger.LogWarning(
                    "Maszyna '{MachineId}' ({Source}): obszar paska postępu (idle.progressRoi) profilu '{Profile}' leży poza ekranem maszyny ({Width}x{Height}) – obserwacja postoju dla tej maszyny jest wyłączona, popraw config.json.",
                    _machine.Id, source, _profile.Id, screenWidth, screenHeight);
                _idleRoiOutOfBoundsLogged = true;
            }

            return;
        }

        _idleWatchEnabled = true;
        _idleRoiOutOfBoundsLogged = false;
        _progressRoiClamped = progressClamped;
        _fetchArea = baseArea.Union(progressClamped);

        // Tor paska z otoczeniem musi być CAŁY na ekranie — inaczej pomiar nie ma sensu (wtedy tylko OCR).
        RectInt barArea = _barSource is { } bar ? ProgressBarReader.RequiredArea(bar) : RectInt.Empty;
        _barAreaClamped = !barArea.IsEmpty && barArea.Clamp(screenWidth, screenHeight) == barArea ? barArea : RectInt.Empty;
        if (_barAreaClamped.IsEmpty) _lastBar = null;
        else _fetchArea = _fetchArea.Union(_barAreaClamped);
    }

    /// <summary>
    /// Wycina pod-obszar <paramref name="roiInScreenCoords"/> (we współrzędnych pełnego ekranu, JUŻ
    /// przycięty do jego granic) z <paramref name="fetched"/> — obrazu całego <see cref="_fetchArea"/>.
    /// Przesunięcie liczone jest względem lewego-górnego rogu <see cref="_fetchArea"/>.
    /// </summary>
    private RgbaImage CropSub(RgbaImage fetched, RectInt roiInScreenCoords)
    {
        var relative = new RectInt(
            roiInScreenCoords.X - _fetchArea.X,
            roiInScreenCoords.Y - _fetchArea.Y,
            roiInScreenCoords.Width,
            roiInScreenCoords.Height);
        return fetched.Crop(relative);
    }

    private async Task DisposeSourceAsync(IScreenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Błąd (zignorowany) przy zwalnianiu źródła obrazu maszyny '{MachineId}'.", _machine.Id);
        }
    }

    private static string DescribeError(Exception ex) =>
        ex is RfbException or ScreenSourceException ? ex.Message : $"Błąd połączenia z maszyną: {ex.Message}";

    // ---------------------------------------------------------------------------------------------
    // Detekcja czerwieni + OCR + alerty
    // ---------------------------------------------------------------------------------------------

    private async Task ProcessFrameAsync(RgbaImage fetchedImage, CancellationToken ct)
    {
        bool observationAvailable = _profile.MainScreen is not { Enabled: true } || _mainScreenVisible == true;
        _alerts.SetScreenObservationAvailable(_machine.Id, observationAvailable);
        if (!observationAvailable)
        {
            InvalidateProgressRead();
            _redGoneSince = null;
            _fullFilter?.Reset();
            _zeroFilter?.Reset();
            _progressFullSince = null;
            _progressZeroSince = null;
            _progressFullNow = false;
            _progressFullConfirmed = false;
            _lastProgressPercent = null;
            _lastProgressDisplayAt = null;
            _lastProgressText = null;
            _lastProgressSource = null;
            _idleCaptureFile = null;

            if (_mainScreenVisible == false && !_alarmPageVisible && !StatusBarSurvivesOtherScreens)
            {
                AbandonErrorRead();
                _filter.Reset();
                _redNow = false;
                _redConfirmed = false;
                return;
            }
        }
        // Gdy obserwacja postoju jest wyłączona (profil bez sekcji "idle" albo ROI poza ekranem),
        // _fetchArea == obszar paska statusu, więc fetchedImage JEST JUŻ obrazem paska statusu
        // (bez dodatkowego, zbędnego wycinania). W przeciwnym razie wycinamy pod-obraz.
        RgbaImage roiImage = _fetchArea == _statusRoiClamped ? fetchedImage : CropSub(fetchedImage, _statusRoiClamped);

        // Pudełko stanu i pasek postępu mierzymy w każdym cyklu (tanie, bez OCR) — potrzebne i do alarmu
        // „program przerwany”, i do postoju (patrz ProcessIdleState).
        _programState = _boxClamped.IsEmpty ? null : ProgramStateDetector.AnalyzeBox(CropSub(fetchedImage, _boxClamped), _programStateProfile!);
        _lastBar = _idleWatchEnabled && !_barAreaClamped.IsEmpty
            ? ProgressBarReader.Read(CropSub(fetchedImage, _barAreaClamped), _barSource!)
            : null;
        UpdateProgramAbort(observationAvailable);

        RedDetectionResult result = _detector.Analyze(roiImage);
        bool confirmed = _filter.Push(result.IsRed);

        _lastRedFraction = result.RedFraction;
        _redNow = result.IsRed;
        _redConfirmed = confirmed;

        // Udział czerwieni w logu DBG TYLKO gdy stan filtru "2 z 3" faktycznie się zmienił względem
        // poprzedniego cyklu — logowanie co cykl (zwykle raz na sekundę, na 7 maszyn naraz) zaśmiecałoby
        // log bez żadnej nowej informacji.
        if (confirmed != _prevConfirmed)
        {
            _logger.LogDebug(
                "Maszyna '{MachineId}': filtr czerwieni {State} (udział czerwieni {Fraction:P2}).",
                _machine.Id, confirmed ? "potwierdził czerwień" : "nie potwierdza już czerwieni", result.RedFraction);
            _prevConfirmed = confirmed;
        }

        DateTimeOffset now = _clock.UtcNow;

        if (confirmed)
        {
            if (!_episodeActive)
            {
                _episodeActive = true;
                _episodeGeneration++;
                _ocrAttempts = 0;
                _ocrSatisfied = false;
                _lastMapped = null;
                _bestMapped = null;
                _lastOcrRoi = null;
                _lastOcrAt = null;
                _episodeTexts.Clear();
                _currentCaptureFile = _captures.Save(_machine.Id, roiImage, now, "blad");
                _episodeStartedAt = ObservationTime.Read(_clock);
                _suppressedOnlySince = null;
                _suppressedShortText = null;
                // Wyciszenie wymaga odczytu treści — bez silnika OCR nie ma czego czekać.
                _redDecision = _ocr.IsAvailable && _mapper.HasSuppressRules(_profile.Id)
                    ? RedAlarmDecision.Pending
                    : RedAlarmDecision.Raise;

                _logger.LogInformation(
                    "Maszyna '{MachineId}': epizod czerwieni otwarty (udział czerwieni {Fraction:P2}).",
                    _machine.Id, result.RedFraction);
            }

            // Czerwień jest AKURAT potwierdzona w tym cyklu — ewentualna wcześniejsza "przerwa" (patrz
            // _redGoneSince) przestaje być aktualna, epizod trwa dalej bez zmian (bez nowego zrzutu, bez
            // resetu _bestMapped/_ocrAttempts) — dokładnie zachowanie sprzed pierwszej klatki bez czerwieni.
            _redGoneSince = null;

            // (a) pierwsza próba, potem powtórki TYLKO gdy poprzedni odczyt był pusty,
            // do OcrMaxAttempts.
            bool firstAttempt = _ocrAttempts == 0;
            bool lastOcrWasEmpty = _lastMapped is null || _lastMapped.NormalizedText.Length == 0;
            bool shouldTryOcrByAttempts = !_ocrSatisfied && _ocrAttempts < _timing.OcrMaxAttempts && (firstAttempt || lastOcrWasEmpty);

            bool contentChanged = _lastOcrRoi is not null && FrameDiff.ChangedFraction(roiImage, _lastOcrRoi) > 0.02;
            bool retryIntervalElapsed = _lastOcrAt is null
                || (_lastOcrAt.Value.Age(_clock)).TotalMilliseconds >= _timing.OcrRetryIntervalMs;
            bool shouldTryOcrByChange = contentChanged && retryIntervalElapsed;

            // Pasek pokazuje komunikat wyciszony: co SuppressRecheckMs czytamy go ponownie, nawet gdy obraz
            // się nie zmienił — wyciszenie ma trwać tylko, dopóki OCR NAPRAWDĘ widzi ten komunikat.
            bool suppressRecheckDue = (_redDecision == RedAlarmDecision.Suppressed || _suppressedOnlySince is not null)
                && _lastOcrAt is { } lastSuppressedRead && lastSuppressedRead.Age(_clock).TotalMilliseconds >= SuppressRecheckMs;

            bool shouldTryOcr = shouldTryOcrByAttempts || shouldTryOcrByChange || suppressRecheckDue
                || (_timing.OcrMaxAttempts > 0 && lastOcrWasEmpty && _lastOcrAt is { } lastAttempt
                    && lastAttempt.Age(_clock).TotalMilliseconds >= 60000);

            TaskCompletionSource<MappedMessage>? firstReadResult = null;
            if (_errorRead is null && shouldTryOcr && result.IsRed && _ocr.IsAvailable)
            {
                _errorSourceGeneration = _sourceGeneration;
                _errorEpisodeGeneration = _episodeGeneration;
                _lastOcrRoi = roiImage;
                _lastOcrAt = ObservationTime.Read(_clock);
                _ocrAttempts++;
                _errorReadByChange = contentChanged;
                firstReadResult = new TaskCompletionSource<MappedMessage>();
                _firstReadResult = firstReadResult.Task;
                _errorRead = firstReadResult.Task; // Uchwyt pierwszej próby istnieje już przed jej startem.
            }
            _bestMapped ??= _mapper.Map(string.Empty, _profile.Id);
            bool handledFirstRead = false;
            try
            {
                // Kolor i jego czas zapisujemy PRZED synchroniczną częścią OCR. Rezerwacja wyniku jest
                // związana wyłącznie z próbą rozpoczynaną poniżej; bez kandydata Raised jest natychmiast.
                if (MayRaiseRedAlarm()) handledFirstRead = ConfirmRedAlarm();
            }
            finally
            {
                if (firstReadResult is not null)
                    _errorRead = ReadErrorMessageAsync(roiImage, result, ct, firstReadResult);
            }

            if (_errorRead is { IsCompleted: true })
            {
                MappedMessage mapped = await _errorRead.ConfigureAwait(false);
                _errorRead = null;
                if (_errorSourceGeneration != _sourceGeneration || _errorEpisodeGeneration != _episodeGeneration)
                    return; // wynik poprzedniego źródła/epizodu nigdy nie opisuje nowego błędu

                _lastMapped = mapped;
                _lastOcrText = mapped.RawText;
                // _lastOcrRoi zostaje obrazem, który NAPRAWDĘ przeczytano (ustawionym przy starcie odczytu), a nie
                // bieżącym: gdy pasek zmienił się w trakcie wolnego odczytu, następny cykl ma to zobaczyć jako
                // zmianę treści i przeczytać go ponownie — inaczej prawdziwy błąd, który zastąpił wyciszony
                // komunikat w trakcie odczytu, zostałby przeoczony.
                _lastOcrAt = ObservationTime.Read(_clock);

                if (mapped.NormalizedText.Length > 0)
                {
                    if (_episodeTexts.Add(mapped.NormalizedText))
                    {
                        _logger.LogInformation(
                            "Maszyna '{MachineId}': kolejny komunikat w tym samym epizodzie: '{Text}'.",
                            _machine.Id, mapped.NormalizedText);

                        _logger.LogInformation(
                            "Maszyna '{MachineId}': OCR surowy: '{Raw}' | z cyframi: '{Digits}' | znormalizowany: '{Norm}'.",
                            _machine.Id, mapped.RawText, TextNormalizer.NormalizeKeepDigits(mapped.RawText), mapped.NormalizedText);
                    }
                }
                else
                {
                    _logger.LogDebug("Maszyna '{MachineId}': OCR pusty.", _machine.Id);
                }

                if (mapped.IsBetterThan(_bestMapped))
                {
                    _bestMapped = mapped;
                    _logger.LogInformation(
                        "Maszyna '{MachineId}': lepszy komunikat w epizodzie: '{Text}' (reguła '{Rule}', priorytet {Priority}).",
                        _machine.Id, mapped.NormalizedText, mapped.RuleId ?? "-", mapped.Priority);
                }

                if (mapped.NormalizedText.Length > 0 && mapped.Matched && !mapped.Ignored)
                {
                    _ocrSatisfied = true;
                }

                DecideRedAlarm(mapped, _errorReadByChange);
            }

            // Niezależnie od tego, czy OCR w tym cyklu ruszał: potwierdzona czerwień ZAWSZE tworzy/odświeża
            // alert, z treścią NAJLEPSZEGO komunikatu widzianego w epizodzie (_bestMapped), nie ostatniego
            // odczytu. Gdy nigdy nie udało się uzyskać żadnej treści (np. OcrMaxAttempts=0, silnik ciągle
            // padał, albo brak silnika) — alert "Błąd (nie odczytano treści)" zamiast ciszy.
            if (_bestMapped is null)
            {
                _bestMapped = _mapper.Map(string.Empty, _profile.Id);
                _lastOcrText = _bestMapped.RawText;
            }

            if (MayRaiseRedAlarm())
            {
                _lastShortText = _bestMapped.ShortText;
                // Rozstrzygnięcie OCR nie jest nową obserwacją czerwieni. Pending ma LastRed sprzed
                // pracy OCR; zwykły alarm aktualizuje opis dotychczasową drogą.
                if (handledFirstRead)
                {
                    if (_errorRead is null) _alerts.FlushPendingFirstRead(_machine.Id);
                }
                else ConfirmRedAlarm();
            }
            else if (_abortConfirmed)
            {
                RaiseProgramAbort(fetchedImage, now);
            }
            else
            {
                // Karta i diagnostyka widzą, co wisi na pasku, choć to nie jest alarm.
                _lastShortText = _suppressedShortText ?? _lastMapped?.ShortText;
                if (_redDecision == RedAlarmDecision.Suppressed)
                {
                    // Dla stanu alertów wyciszony pasek to „brak czerwieni”: alert z wcześniejszego prawdziwego
                    // błędu zamknie Tick, a kolejny błąd po przerwie będzie nowym epizodem (z dźwiękiem).
                    _alerts.OnRedCleared(_machine.Id);
                }
            }
        }
        else if (observationAvailable)
        {
            // Bez czerwieni, ale z potwierdzonym „IDLE: Abort” w trakcie pracy: alarm jak błąd (ten sam tor co
            // czerwień — dźwięk, eskalacja, raport postojów). Inaczej — zwykły brak czerwieni.
            if (_abortConfirmed) RaiseProgramAbort(fetchedImage, now);
            else _alerts.OnRedCleared(_machine.Id);

            if (_episodeActive)
            {
                bool firstGoneFrame = _redGoneSince is null;
                _redGoneSince ??= ObservationTime.Read(_clock);

                if (firstGoneFrame && _diagnostics.CaptureBreaks)
                {
                    bool throttled = _lastBreakCaptureAt is { } lastCapture
                        && lastCapture.Age(_clock).TotalMilliseconds < 60000;
                    if (!throttled)
                    {
                        _captures.Save(_machine.Id, roiImage, now, "przerwa");
                        _lastBreakCaptureAt = ObservationTime.Read(_clock);
                    }
                }

                if ((_redGoneSince.Value.Age(_clock)).TotalMilliseconds >= _timing.EpisodeBreakAfterMs)
                {
                    _logger.LogInformation(
                        "Maszyna '{MachineId}': epizod czerwieni zamknięty (powód: przerwa ≥ {Ms} ms bez czerwieni, najlepszy komunikat: '{ShortText}').",
                        _machine.Id, _timing.EpisodeBreakAfterMs, _bestMapped?.ShortText ?? "-");
                    _episodeActive = false;
                    _redGoneSince = null;
                }
            }
        }
        if (_idleWatchEnabled && observationAvailable && (_errorRead is null || _errorRead.IsCompleted))
            await ProcessIdleState(fetchedImage, now, ct).ConfigureAwait(false);
    }

    /// <summary>Krótki opis alarmu „program przerwany” (pudełko stanu „IDLE: Abort” w trakcie pracy).</summary>
    internal const string ProgramAbortShortText = "Program przerwany (IDLE: Abort) — sprawdź narzędzie";

    /// <summary>
    /// Jedna próbka „program przerwany w trakcie pracy” na cykl (profil z sekcją <c>programState</c>).
    /// Dowód przerwania: pudełko stanu NIE jest zielone (nie RUN) i albo wzorzec pikseli napisu „IDLE: Abort”
    /// się zgadza, albo OCR ostatniej kontroli ekranu głównego widział w pudełku słowo „Abort”
    /// (<see cref="ProgramStateDetector.HasAbortWord"/>) — dwa niezależne tory. Alarm tylko w trakcie pracy:
    /// postęp 1–99 % (pasek, a gdy go nie widać — ostatni procent z OCR); „IDLE: Abort” przy 0 % to stan po
    /// skasowaniu pracy, który obsługuje zwykły postój „Stoi na 0 %”. Brak obserwacji (inny ekran, pudełko
    /// nieczytelne) ani nieznany postęp NIE są dowodem ani przerwania, ani wznowienia — filtr stoi w miejscu,
    /// jak przy czerwieni („zasłonięty ekran nie jest dowodem naprawy”).
    /// </summary>
    private void UpdateProgramAbort(bool observationAvailable)
    {
        if (_abortFilter is null || !observationAvailable || _programState is not { Known: true } state)
        {
            return;
        }

        int? percent = _lastBar is { Visible: true } bar ? bar.Percent : _lastProgressPercent;
        bool evidence = !state.Running && (state.AbortPattern || _abortWordSeen == true);
        if (evidence && percent is null)
        {
            return;
        }

        bool sample = evidence && percent is >= 1 and <= 99;
        bool confirmed = _abortFilter.Push(sample);
        if (confirmed != _abortConfirmed)
        {
            _logger.LogInformation(
                confirmed
                    ? "Maszyna '{MachineId}': program przerwany w trakcie pracy (IDLE: Abort przy {Percent} %; {State}) — alarm."
                    : "Maszyna '{MachineId}': koniec stanu „program przerwany” (postęp {Percent} %; {State}).",
                _machine.Id, percent, state.Description);
            _abortConfirmed = confirmed;
            if (!confirmed)
            {
                _abortCaptureFile = null;
                if (_lastShortText == ProgramAbortShortText) _lastShortText = null;
            }
        }
    }

    /// <summary>
    /// Zgłasza potwierdzone „IDLE: Abort” jak czerwony błąd (<see cref="AlertStateMachine.OnRedConfirmed"/>):
    /// klucz <c>rule:&lt;profil&gt;-program-abort</c> (<see cref="AlarmHelp.ProgramAbortRuleSuffix"/>), kategoria
    /// „narzedzie”, pomoc z <see cref="AlarmHelp"/>. Zrzut obszaru pobieranego (z pudełkiem stanu i paskiem) raz na epizod.
    /// </summary>
    private void RaiseProgramAbort(RgbaImage fetchedImage, DateTimeOffset now)
    {
        var message = new MappedMessage(
            _profile.Id + AlarmHelp.ProgramAbortRuleSuffix, ProgramAbortShortText, "narzedzie",
            "IDLE: Abort", "IDLE ABORT", Matched: true) { Priority = 90 };
        _abortCaptureFile ??= _captures.Save(_machine.Id, fetchedImage, now, "przerwany");
        _lastShortText = message.ShortText;
        _alerts.OnRedConfirmed(_machine, message, _abortCaptureFile);
    }

    /// <summary>
    /// Czy czerwień bieżącego epizodu wolno już zgłosić jako alarm. Czekanie na pierwszy odczyt treści
    /// (<see cref="RedAlarmDecision.Pending"/>) kończy się alarmem po <see cref="SuppressDecisionTimeoutMs"/>
    /// albo gdy silnik OCR przestał być dostępny.
    /// </summary>
    private bool MayRaiseRedAlarm()
    {
        if (_redDecision != RedAlarmDecision.Raise && !_ocr.IsAvailable)
        {
            // Także w stanie Suppressed: bez silnika OCR nie da się sprawdzić, czy na pasku nadal wisi
            // WYŁĄCZNIE wyciszony komunikat (np. zawieszony WinRT — SerializingOcrEngine.IsAvailable).
            _logger.LogInformation(
                "Maszyna '{MachineId}': silnik OCR niedostępny — czerwień zgłoszona jako alarm, wyciszenie wyłączone.",
                _machine.Id);
            _redDecision = RedAlarmDecision.Raise;
            _suppressedOnlySince = null;
        }
        else if (_redDecision == RedAlarmDecision.Pending
            && (_episodeStartedAt is not { } started || started.Age(_clock).TotalMilliseconds >= SuppressDecisionTimeoutMs))
        {
            _logger.LogInformation(
                "Maszyna '{MachineId}': brak rozstrzygającego odczytu treści w {Ms} ms — czerwień zgłoszona jako alarm.",
                _machine.Id, SuppressDecisionTimeoutMs);
            _redDecision = RedAlarmDecision.Raise;
        }
        else if (_redDecision == RedAlarmDecision.Raise && _ocr.IsAvailable
            && _suppressedOnlySince is { } since && since.Age(_clock).TotalMilliseconds >= SuppressReturnAfterMs)
        {
            _logger.LogInformation(
                "Maszyna '{MachineId}': od {Ms} ms pasek pokazuje wyłącznie komunikat bez alarmu — błąd uznany za usunięty, alarm wyciszony.",
                _machine.Id, SuppressReturnAfterMs);
            _redDecision = RedAlarmDecision.Suppressed;
            _suppressedOnlySince = null;
        }

        return _redDecision == RedAlarmDecision.Raise;
    }

    private void DecideRedAlarm(MappedMessage mapped, bool readAfterImageChange)
    {
        if (mapped.Suppressed)
        {
            _suppressedShortText = mapped.ShortText;
        }

        if (_redDecision == RedAlarmDecision.Raise)
        {
            // Alarm trwa. Liczymy tylko, od kiedy pasek pokazuje WYŁĄCZNIE komunikat wyciszony (powrót do
            // wyciszenia — MayRaiseRedAlarm, SuppressReturnAfterMs); każda inna treść zeruje ten licznik.
            if (mapped.Suppressed)
            {
                _suppressedOnlySince ??= ObservationTime.Read(_clock);
            }
            else if (mapped.NormalizedText.Length > 0 || readAfterImageChange)
            {
                _suppressedOnlySince = null;
            }

            return;
        }

        if (mapped.Suppressed)
        {
            if (_redDecision == RedAlarmDecision.Pending)
            {
                _logger.LogInformation(
                    "Maszyna '{MachineId}': czerwony pasek pokazuje komunikat bez alarmu ('{Rule}': '{Text}') — alarm wyciszony.",
                    _machine.Id, mapped.RuleId, mapped.NormalizedText);
            }

            _redDecision = RedAlarmDecision.Suppressed;
            return;
        }

        if (mapped.NormalizedText.Length > 0 || _redDecision == RedAlarmDecision.Pending || readAfterImageChange)
        {
            _logger.LogInformation(
                "Maszyna '{MachineId}': treść paska to nie komunikat bez alarmu ('{Text}') — czerwień zgłoszona jako alarm.",
                _machine.Id, mapped.NormalizedText);
            _redDecision = RedAlarmDecision.Raise;
        }
    }

    private bool ConfirmRedAlarm()
    {
        _lastShortText = _bestMapped!.ShortText;
        // Tylko placeholder przed pierwszym odczytem TEJ generacji. Retry po pustym wyniku,
        // brak OCR i max0 pozostają zwykłym alarmem. Profile wyciszające zachowują własny limit 10 s.
        if ((_ocr.IsAvailable || _firstReadResult?.IsCompleted == true) && _errorRead is { } firstRead
            && _ocrAttempts == 1 && _lastMapped is null
            && _errorSourceGeneration == _sourceGeneration && _errorEpisodeGeneration == _episodeGeneration
            && !_mapper.HasSuppressRules(_profile.Id))
            return _alerts.OnRedConfirmedWithFirstRead(_machine, _bestMapped, _currentCaptureFile, _firstReadResult ?? firstRead);
        _alerts.OnRedConfirmed(_machine, _bestMapped, _currentCaptureFile);
        return false;
    }

    private async Task<MappedMessage> ReadErrorMessageAsync(RgbaImage roi, RedDetectionResult detection,
        CancellationToken ct, TaskCompletionSource<MappedMessage> result)
    {
        try
        {
            MappedMessage mapped = _mapper.Map(await ReadErrorAsync(roi, detection, ct).ConfigureAwait(false), _profile.Id);
            result.SetResult(mapped);
            return mapped;
        }
        catch (Exception ex)
        {
            result.SetException(ex);
            throw;
        }
    }

    private Task<OcrResult> RecognizeErrorAsync(Gray8Image image, CancellationToken ct) =>
        _ocr is SerializingOcrEngine serial ? serial.RecognizeErrorAsync(image, ct) : _ocr.RecognizeAsync(image, ct);

    private async Task<string> ReadErrorAsync(RgbaImage roi, RedDetectionResult detection, CancellationToken ct)
    {
        try
        {
            var prepared = ImageOps.PrepareForOcr(roi, _profile.Red, _profile.OcrMarginPx, _profile.OcrScale);
            var result = await RecognizeErrorAsync(prepared, ct).ConfigureAwait(false);
            string text = TextTruncation.MarkIfCut(result.Text, detection.RedBounds, roi.Width);
            var firstMapped = _mapper.Map(text, _profile.Id);
            // A plausible OCR result is not necessarily correct. Retry any
            // unmatched result on the original image before settling for an unknown description.
            if (!result.Unavailable && !firstMapped.Matched)
            {
                var plain = ImageOps.PrepareTextForOcr(roi, _profile.OcrMarginPx, _profile.OcrScale, invert: false);
                var second = await RecognizeErrorAsync(plain, ct).ConfigureAwait(false);
                string secondText = TextTruncation.MarkIfCut(second.Text, detection.RedBounds, roi.Width);
                _logger.LogDebug("Maszyna {MachineId}: drugie podejście OCR (bez maski czerwieni).", _machine.Id);
                var secondMapped = _mapper.Map(secondText, _profile.Id);
                if (!second.Unavailable && (secondMapped.IsBetterThan(firstMapped)
                    || (firstMapped.AlarmNumber is null && !TextReadability.IsReadable(text)
                        && TextReadability.IsReadable(secondText)))) text = secondText;
            }

            MappedMessage current = _mapper.Map(text, _profile.Id);
            if (!result.Unavailable && (!current.Matched || current.Suppressed)
                && ImageOps.PrepareRedFieldTextForOcr(roi, _profile.Red, _profile.OcrMarginPx, _profile.OcrScale) is { } fieldImage)
            {
                OcrResult third = await RecognizeErrorAsync(fieldImage, ct).ConfigureAwait(false);
                string fieldText = TextTruncation.MarkIfCut(third.Text, detection.RedBounds, roi.Width);
                MappedMessage fieldMapped = _mapper.Map(fieldText, _profile.Id);
                bool take = current.Suppressed
                    ? fieldMapped.Matched && !fieldMapped.Ignored && !fieldMapped.Suppressed
                    : fieldMapped.Matched || (current.AlarmNumber is null && TextReadability.IsReadable(fieldText));
                _logger.LogDebug(
                    "Maszyna {MachineId}: trzecie podejście OCR (tekst na kolorowym polu): '{Text}' → {Rule}{Taken}.",
                    _machine.Id, fieldText, fieldMapped.RuleId ?? "-", take ? " (przyjęte)" : string.Empty);
                if (!third.Unavailable && take)
                {
                    // Przy wyciszonym odczycie dokładamy tekst pola do pełnego odczytu: MessageMapper sam wybierze
                    // regułę, a zwykła reguła przegrywa z wyciszeniem tylko wtedy, gdy nie pasuje nic innego.
                    text = current.Suppressed ? $"{text} {fieldText}" : fieldText;
                }
            }
            return text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return string.Empty; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Błąd silnika OCR dla maszyny '{MachineId}'.", _machine.Id);
            return string.Empty;
        }
    }

    private void ResetSourceObservation()
    {
        ResetOcrObservation();
        _filter.Reset();
        _fullFilter?.Reset();
        _zeroFilter?.Reset();
        _lastRoiImage = null;
        Volatile.Write(ref _previewFrame, null);
        _lastFullFrameAt = null;
        _forceFullFrame = true;
        _usingCachedFrame = false;
        _lastMainScreenCheckAt = null;
        _lastScreenResultAt = null;
        _lastScreenImage = null;
        _mainScreenUnknownStreak = 0;
        _mainScreenUnknownReason = null;
        _mainScreenVisible = null;
        _alarmPageVisible = false;
        _wrongScreenSince = null;
        _wrongScreenCaptureFile = null;
        _alerts.OnMainScreenUnknown(_machine.Id);
        _progressFullSince = null;
        _progressZeroSince = null;
        _progressFullNow = _progressFullConfirmed = false;
        _lastProgressPercent = null;
        _lastProgressDisplayAt = null;
        _lastProgressText = null;
        _lastProgressSource = null;
        _lastProgressFraction = 0;
        _idleCaptureFile = null;
        _lastBar = null;
        _programState = null;
        _abortWordSeen = null;
        _abortFilter?.Reset();
        _abortConfirmed = false;
        _abortCaptureFile = null;
        _redNow = _redConfirmed = _prevConfirmed = false;
        _lastRedFraction = 0;
        _episodeActive = false;
        _redGoneSince = null;
        _lastOcrRoi = null;
        _lastOcrText = _lastShortText = null;
        _alerts.SetScreenObservationAvailable(_machine.Id, false);
        // Zmiana źródła/połączenia nie jest odczytem „poniżej 100%”: licznik postoju startuje od nowa,
        // a istniejący alert postoju czeka na pierwszy rzeczywisty odczyt nowego źródła.
        _alerts.OnProgressUnknown(_machine.Id);
    }

    private void AbandonErrorRead()
    {
        _alerts.FlushPendingFirstRead(_machine.Id);
        // Zachowujemy uchwyt do zakończenia zadania, ale utracony obraz nie może już opisać nowego.
        // Nie zmieniamy generacji odczytu ekranu głównego, który ma rozpoznać powrót z menu.
        _errorSourceGeneration = -1;
    }

    private void ResetOcrObservation()
    {
        _alerts.FlushPendingFirstRead(_machine.Id);
        _sourceGeneration++;
        InvalidateProgressRead();
        // Nie porzucamy uchwytów działających zadań: najwyżej jedno na rodzaj odczytu.
        // Generacja uniemożliwia użycie ich wyników po zmianie źródła/połączenia.
    }

    private void InvalidateProgressRead()
    {
        _progressReadInvalidated = true;
        _numberReader?.Reset();
    }

    /// <summary>
    /// Detekcja „maszyna stoi bez pracy – dołóż prace" — wołane RAZ NA CYKL, gdy
    /// <see cref="_idleWatchEnabled"/>. Wycina obszar postępu z <paramref name="fetchedImage"/> (obszaru
    /// pobieranego) i analizuje go wybranym trybem (<see cref="ProgressMode.Bar"/> — kolor wypełnienia,
    /// synchronicznie; <see cref="ProgressMode.Number"/> — OCR liczby procent przez
    /// <see cref="ProgressNumberReader"/>, stąd ta metoda jest <c>async</c>), przepuszcza wynik przez
    /// filtr "2 z 3" i zgłasza go do <see cref="AlertStateMachine.OnProgressState"/> (to TAM mieszka próg
    /// <c>IdleAlertAfterMs</c> i cała logika epizodu — tu tylko śledzimy <see cref="_progressFullSince"/>
    /// na potrzeby migawki i zrzutu). Przy PIERWSZYM potwierdzeniu 100% w ramach bieżącego epizodu ustawia
    /// <see cref="_progressFullSince"/>; gdy potwierdzenie spadnie (postęp poniżej progu "100%"), zeruje
    /// je — maszyna ruszyła, epizod się skończył (dokładnie zgodnie z semantyką
    /// <see cref="AlertStateMachine.OnProgressState"/>). Zrzut ekranu „postoj" (CAŁEGO obszaru pobieranego,
    /// nie tylko sub-ROI postępu) zapisuje TYLKO RAZ na epizod, dopiero w pierwszym cyklu, w którym
    /// licznik przekroczy <c>IdleAlertAfterMs</c> (czyli dokładnie wtedy, gdy alert faktycznie powstaje) —
    /// nie przy każdym zwykłym zakończeniu pracy (maszyna staje na 100% na ~1 minutę przed każdą kolejną
    /// pracą), bo to dałoby setki zbędnych plików dziennie na 12 maszyn.
    /// </summary>
    private async Task ProcessIdleState(RgbaImage fetchedImage, DateTimeOffset now, CancellationToken ct)
    {
        RgbaImage progressImage = CropSub(fetchedImage, _progressRoiClamped);

        ProgressDetectionResult detection;
        ProgressDetectionResult? movingDisplay = null;
        bool awaitingProgress = false;
        bool fromBar = false;
        if (_lastBar is { Visible: true, Percent: int barPercent })
        {
            if (_progressRead is not null) InvalidateProgressRead();
            detection = new ProgressDetectionResult(barPercent / 100.0, barPercent >= _idleProfile!.FullAtPercent)
            {
                Percent = barPercent, RawText = _lastBar.Description,
            };
            fromBar = true;
        }
        else if (_idleProfile!.Mode == ProgressMode.Number)
        {
            if (_progressRead is null)
            {
                _progressGeneration = _sourceGeneration;
                _progressReadAt = ObservationTime.Read(_clock);
                _progressReadInvalidated = false;
                _progressReadImageChanged = false;
                _progressReadImage = progressImage;
                _progressRead = _numberReader!.ReadAsync(progressImage, ct);
            }
            bool withinReadDeadline = _progressReadAt.Age(_clock).TotalMilliseconds < _timing.MainScreenOcrGraceMs;
            if (_progressGeneration != _sourceGeneration || !withinReadDeadline)
                InvalidateProgressRead();
            if (!_progressReadImageChanged && !SameImage(progressImage, _progressReadImage))
            {
                // Moving digits are not a source/HMI boundary. Keep this fact sticky even for A-B-A:
                // the old crop cannot prove idle, but a fresh running sample can still feed the UI.
                _progressReadImageChanged = true;
                _numberReader!.Reset();
            }

            // Pending OCR (including a result discarded after another digit change) is not a
            // completed unreadable result. Bound the display independently of every new request.
            awaitingProgress = _ocr.IsAvailable && _progressGeneration == _sourceGeneration && withinReadDeadline;
            detection = new ProgressDetectionResult(0, false) { OcrUnavailable = !_ocr.IsAvailable };
            if (_progressRead.IsCompleted)
            {
                try
                {
                    var read = await _progressRead.ConfigureAwait(false);
                    if (!_progressReadInvalidated && _ocr.IsAvailable)
                    {
                        if (!_progressReadImageChanged)
                        {
                            detection = read;
                            awaitingProgress = false;
                        }
                        else
                        {
                            if (read.Percent is >= 0 and < 100 && !read.IsZero && !read.IsFull && !read.OcrUnavailable)
                                movingDisplay = read;
                            _numberReader!.Reset();
                        }
                    }
                    else _numberReader!.Reset();
                }
                finally { _progressRead = null; }
            }
            else if (!_progressReadInvalidated && !_progressReadImageChanged)
            {
                // A refresh in progress is not an unreadable result. Reuse only the exact same
                // crop, from this observation, until the original cache age reaches 60 + 15 s.
                detection = _numberReader!.GetResultWhileRefreshing(progressImage, _timing.MainScreenOcrGraceMs) ?? detection;
            }
        }
        else detection = _progressDetector!.Analyze(progressImage);

        // Nieczytelny odczyt (tryb number) nie jest próbką „poniżej 100%”: czyści filtr „2 z 3”
        // zamiast go zapełniać, więc kolejne rzeczywiste odczyty rozstrzygają same.
        bool readable = _idleProfile.Mode != ProgressMode.Number || detection.Percent.HasValue;
        bool running = _programState is { Known: true, Running: true };
        bool isZero = detection.IsZero && !running;
        bool fullConfirmed = false;
        bool zeroConfirmed = false;
        if (readable)
        {
            fullConfirmed = _fullFilter!.Push(detection.IsFull);
            zeroConfirmed = _zeroFilter?.Push(isZero) ?? false;
        }
        else
        {
            _fullFilter!.Reset();
            _zeroFilter?.Reset();
        }

        _lastProgressFraction = detection.FilledFraction;
        _progressFullNow = detection.IsFull;
        _progressFullConfirmed = fullConfirmed;
        // The snapshot feeds the preview, overlay and group receivers. Hold only their displayed
        // number during a bounded refresh; detection above/below stays unknown for idle accounting.
        if (detection.Percent.HasValue)
        {
            _lastProgressPercent = detection.Percent;
            _lastProgressText = detection.RawText;
            _lastProgressSource = fromBar ? "pasek" : "ocr";
            _lastProgressDisplayAt = ObservationTime.Read(_clock);
        }
        else if (movingDisplay is { } sampled)
        {
            _lastProgressPercent = sampled.Percent;
            _lastProgressText = sampled.RawText;
            _lastProgressSource = "ocr";
            // A slow completion must not buy another grace period for old pixels.
            _lastProgressDisplayAt = _progressReadAt;
        }
        else if (!awaitingProgress || _lastProgressDisplayAt is not { } displayedAt
            || displayedAt.Age(_clock).TotalMilliseconds >= _timing.MainScreenOcrGraceMs)
        {
            _lastProgressPercent = null;
            _lastProgressText = detection.RawText;
            _lastProgressSource = null;
            _lastProgressDisplayAt = null;
        }

        if (fullConfirmed) _progressFullSince ??= ObservationTime.Read(_clock);
        else _progressFullSince = null;

        if (zeroConfirmed) _progressZeroSince ??= ObservationTime.Read(_clock);
        else _progressZeroSince = null;

        // Zrzut "postoj" — raz na epizod, w pierwszym cyklu po przekroczeniu progu TEGO stanu (100 % ma
        // własny próg, 0 % własny), czyli dokładnie wtedy, gdy alert faktycznie powstaje.
        ObservationTime? standingSince = fullConfirmed ? _progressFullSince : zeroConfirmed ? _progressZeroSince : null;
        if (standingSince is null)
        {
            _idleCaptureFile = null;
        }
        else
        {
            var idleAlertAfter = TimeSpan.FromMilliseconds(
                fullConfirmed ? _timing.IdleAlertAfterMs : _timing.IdleAtZeroAfterMs);
            if (_idleCaptureFile is null && standingSince.Value.Age(_clock) >= idleAlertAfter)
            {
                _idleCaptureFile = SaveFullFrame(fetchedImage, now, "postoj");
            }
        }

        // Nieczytelny odczyt ani pojedyncze, jeszcze niepotwierdzone „100%” po resecie filtru nie dowodzą,
        // że maszyna ruszyła: licznik postoju startuje od nowa, lecz istniejący alert (np. przejęty od
        // poprzedniego obserwatora albo zachowany po uśpieniu) czeka na potwierdzony odczyt. Odczyt
        // poniżej 100% zamyka epizod od razu.
        bool awaitingFull = !fullConfirmed && detection.IsFull && _fullFilter.SampleCount < _fullFilter.Required;
        bool awaitingZero = !zeroConfirmed && isZero && _zeroFilter is not null
            && _zeroFilter.SampleCount < _zeroFilter.Required;
        if (!readable || awaitingFull || awaitingZero)
            _alerts.OnProgressUnknown(_machine.Id);
        else
            _alerts.OnProgressState(
                _machine,
                fullConfirmed ? ProgressState.Full : zeroConfirmed ? ProgressState.Zero : ProgressState.Running,
                _idleCaptureFile);
    }

    private string? SaveFullFrame(RgbaImage frame, DateTimeOffset now, string tag)
    {
        if (_lastFullCaptureAt.TryGetValue(tag, out ObservationTime last) && last.Age(_clock) < FullCaptureThrottle)
        {
            return null;
        }

        RgbaImage toSave = _diagnostics.CaptureBreaks ? frame : frame.Downscale(FullCaptureDownscale);
        string? path = _captures.Save(_machine.Id, toSave, now, tag);
        if (path is not null)
        {
            _lastFullCaptureAt[tag] = ObservationTime.Read(_clock);
        }

        return path;
    }

    // ---------------------------------------------------------------------------------------------
    // Obserwacja „czy HMI pokazuje ekran główny" (sekcja "mainScreen" profilu)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Czy w tym cyklu pora na sprawdzenie ekranu głównego — wymusza pobranie pełnej klatki (patrz <see cref="CaptureFromVncAsync"/>).</summary>
    private bool MainScreenCheckDue() =>
        _mainScreenWatchEnabled
        && (_screenRead is not null || _lastMainScreenCheckAt is null
            || (_lastMainScreenCheckAt.Value.Age(_clock)).TotalMilliseconds >= _timing.MainScreenCheckIntervalMs);

    private async Task CheckMainScreenIfDueAsync(RgbaImage fullFrame, CancellationToken ct)
    {
        if (!_mainScreenWatchEnabled) return;
        DateTimeOffset now = _clock.UtcNow;
        RgbaImage? context = AnchorContext(fullFrame);
        if (_screenRead is null && _lastScreenImage is not null && !SameContext(context, _lastScreenImage))
            _lastMainScreenCheckAt = null; // A requested full refresh may reveal a tab change early.
        if (!MainScreenCheckDue())
        {
            return;
        }

        if (_screenRead is null)
        {
            _screenGeneration = _sourceGeneration;
            _screenReadAt = ObservationTime.Read(_clock);
            _lastMainScreenCheckAt = _screenReadAt;
            _screenReadImage = context;
            _screenReadInvalidated = false;
            _screenReadStale = false;
            _screenRead = _mainScreenDetector!.DetectAsync(fullFrame.Clone(), ct);
        }

        bool anchorsChanged = !SameContext(context, _screenReadImage);
        _screenReadInvalidated |= _screenGeneration != _sourceGeneration || anchorsChanged;
        _screenReadStale |= _screenReadAt.Age(_clock).TotalMilliseconds >= _timing.MainScreenOcrGraceMs;
        if (!_screenRead.IsCompleted)
        {
            string? reason = _screenReadInvalidated ? (anchorsChanged ? "invalidated" : "reconnect")
                : _screenReadStale ? "grace"
                : !_ocr.IsAvailable ? "brak-ocr"
                : !SameContext(context, _lastScreenImage) ? "pending-po-zmianie"
                : _lastScreenResultAt is not { } lastResult
                    || lastResult.Age(_clock).TotalMilliseconds >= (long)_timing.MainScreenCheckIntervalMs + _timing.MainScreenOcrGraceMs
                    ? "pending"
                    : null;
            if (reason is not null) SetMainScreenUnknown(reason);
            return;
        }
        MainScreenDetectionResult result;
        try { result = await _screenRead.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = MainScreenDetectionResult.Unknown; }
        finally { _screenRead = null; }
        if (_screenReadInvalidated || _screenReadStale)
        {
            CountUnresolvedMainScreenCheck();
            SetMainScreenUnknown(_screenReadStale && !_screenReadInvalidated ? "grace"
                : _screenGeneration != _sourceGeneration ? "reconnect" : "invalidated");
            _lastMainScreenCheckAt = null;   // wymuszamy kolejny odczyt od razu, bez czekania na odstęp
            return;
        }
        if (!_ocr.IsAvailable) result = MainScreenDetectionResult.Unknown;
        _mainScreenVisible = result.MainScreenVisible;
        _alarmPageVisible = result.AlarmPageVisible;
        if (result.MainScreenVisible is not { } visible)
        {
            CountUnresolvedMainScreenCheck();
            SetMainScreenUnknown("ocr-bez-tekstu");
            return;
        }

        _mainScreenUnknownStreak = 0;
        _mainScreenUnknownReason = null;
        // Ten sam OCR pełnej klatki służy drugiemu, niezależnemu torowi wykrywania „IDLE: Abort” (obok
        // wzorca pikseli w ProgramStateDetector.AnalyzeBox) — bez dodatkowego wywołania silnika.
        if (_programStateProfile is { } programState)
            _abortWordSeen = ProgramStateDetector.HasAbortWord(result.Words, programState);
        if (result.LabelBounds is { IsEmpty: false } bounds)
        {
            RectInt learned = bounds.Inflate(2, 2).Clamp(fullFrame.Width, fullFrame.Height);
            if (_screenAnchorRoi != learned)
            {
                // Pas kotwic poznajemy dopiero z TEGO odczytu — zapamiętany obraz musi być już nowym,
                // węższym wycinkiem, inaczej następny cykl uznałby go za zmianę ekranu.
                _screenAnchorRoi = learned;
                context = AnchorContext(fullFrame);
            }
        }

        _mainScreenVisible = visible;
        _lastScreenImage = context;
        _lastScreenResultAt = _screenReadAt;

        if (visible)
        {
            if (_wrongScreenSince is not null)
            {
                _logger.LogInformation("Maszyna '{MachineId}': HMI znów pokazuje ekran główny (etykieta '{Label}').", _machine.Id, result.LabelText);
            }

            _wrongScreenSince = null;
            _wrongScreenCaptureFile = null;
        }
        else
        {
            if (_wrongScreenSince is null)
            {
                _wrongScreenSince = ObservationTime.Read(_clock);
                if (result.LabelText is { Length: > 0 } cover)
                {
                    _logger.LogInformation("Maszyna '{MachineId}': HMI pokazuje okno '{Label}' zamiast ekranu głównego.", _machine.Id, cover);
                }

                if (_timing.WrongScreenAlertAfterMs <= 0)
                {
                    _logger.LogInformation(
                        "Maszyna '{MachineId}': HMI pokazuje inny ekran niż główny (brak etykiety ekranu głównego) — przypomnienie od razu (timing.wrongScreenAlertAfterMs = 0).",
                        _machine.Id);
                }
                else
                {
                    _logger.LogInformation(
                        "Maszyna '{MachineId}': HMI pokazuje inny ekran niż główny (brak etykiety ekranu głównego) — przypomnienie po {AfterMs} ms, jeśli to potrwa.",
                        _machine.Id, _timing.WrongScreenAlertAfterMs);
                }
            }

            var alertAfter = TimeSpan.FromMilliseconds(_timing.WrongScreenAlertAfterMs);
            if (_wrongScreenCaptureFile is null && _wrongScreenSince.Value.Age(_clock) >= alertAfter)
            {
                _wrongScreenCaptureFile = SaveFullFrame(fullFrame, now, "ekran");
            }
        }

        _alerts.OnMainScreenState(_machine, visible, _wrongScreenCaptureFile);
    }

    private void CountUnresolvedMainScreenCheck()
    {
        _mainScreenUnknownStreak++;
        if (_mainScreenUnknownStreak == MainScreenUnknownWarnStreak)
        {
            _logger.LogWarning(
                "Maszyna '{MachineId}': {Count} kolejnych sprawdzeń ekranu głównego HMI bez rozstrzygnięcia (ostatni powód: {Reason}) — obserwacja ekranu jest niedostępna, alerty tej maszyny nie zamkną się same.",
                _machine.Id, _mainScreenUnknownStreak, _mainScreenUnknownReason ?? "-");
        }
    }

    private void SetMainScreenUnknown(string reason)
    {
        if (_mainScreenUnknownReason != reason)
        {
            _mainScreenUnknownReason = reason;
            _logger.LogDebug(
                "Maszyna '{MachineId}': sprawdzenie ekranu głównego HMI nierozstrzygnięte (powód: {Reason}) — pomijam ten cykl.",
                _machine.Id, reason);
        }

        _mainScreenVisible = null;
        _alarmPageVisible = false;
        _wrongScreenSince = null;
        _wrongScreenCaptureFile = null;
        _lastScreenImage = null;
        _lastScreenResultAt = null;
        _abortWordSeen = null;
        _alerts.OnMainScreenUnknown(_machine.Id);
    }

    private static bool SameImage(RgbaImage image, RgbaImage? previous) =>
        previous is not null && image.Width == previous.Width && image.Height == previous.Height
        && image.Pixels.AsSpan().SequenceEqual(previous.Pixels);

    /// <summary>Porównanie „pasa kotwic”; brak pasa po obu stronach = ekran uznajemy za niezmieniony.</summary>
    private static bool SameContext(RgbaImage? context, RgbaImage? previous) =>
        context is null ? previous is null : previous is not null && SameImage(context, previous);

    /// <summary>
    /// „Pas kotwic” bieżącej klatki — wycinki, po których poznajemy, że HMI nadal pokazuje ten sam ekran:
    /// <see cref="MainScreenWatchProfile.ContextRoi"/> z profilu, a gdy go nie podano — obszar, w którym
    /// OCR ostatnio naprawdę odczytał etykietę ekranu głównego (<see cref="_screenAnchorRoi"/>). Zwraca
    /// <see langword="null"/>, dopóki żadnego pasa nie znamy: wtedy zmiana obrazu NIE odrzuca trwającego
    /// odczytu (pierwszy odczyt na pracującej maszynie musi mieć
    /// szansę się skończyć). Pasek statusu i pole postępu są zawsze wycinane: mają własną obserwację, a
    /// ich zmiana nie jest zmianą ekranu.
    /// </summary>
    private RgbaImage? AnchorContext(RgbaImage frame)
    {
        IReadOnlyList<RectInt> areas = AnchorAreas();
        if (areas.Count == 0) return null;

        var clamped = new List<RectInt>(areas.Count);
        int width = 0, height = 0;
        foreach (RectInt area in areas)
        {
            RectInt clip = area.Clamp(frame.Width, frame.Height);
            if (clip.IsEmpty) continue;
            clamped.Add(clip);
            width = Math.Max(width, clip.Width);
            height += clip.Height;
        }

        if (clamped.Count == 0) return null;

        var context = new RgbaImage(width, height);
        int y = 0;
        foreach (RectInt clip in clamped)
        {
            context.Blit(frame.Crop(clip), 0, y);
            y += clip.Height;
        }

        Clear(_profile.StatusBarRoi);
        if (_idleProfile is not null) Clear(_idleProfile.ProgressRoi);
        // Ruchomy pasek postępu i zmiana RUN ↔ IDLE w pudełku stanu to nie zmiana ekranu HMI.
        if (!_barAreaClamped.IsEmpty) Clear(_barAreaClamped);
        if (!_boxClamped.IsEmpty) Clear(_boxClamped);
        return context;

        // Wycięcie żywego pola z UŁOŻONEGO pasa: przeliczamy prostokąt na współrzędne wyniku.
        void Clear(RectInt live)
        {
            int offset = 0;
            foreach (RectInt clip in clamped)
            {
                RectInt hit = clip.Intersect(live);
                if (!hit.IsEmpty)
                {
                    int x = hit.X - clip.X;
                    for (int row = hit.Y - clip.Y; row < hit.Y - clip.Y + hit.Height; row++)
                        context.Pixels.AsSpan((((offset + row) * context.Width) + x) * 4, hit.Width * 4).Clear();
                }

                offset += clip.Height;
            }
        }
    }

    /// <summary>Obszary pasa kotwic: z profilu, a gdy nie podano — wyuczony z ostatniego udanego odczytu.</summary>
    private IReadOnlyList<RectInt> AnchorAreas()
    {
        List<RectInt>? configured = _profile.MainScreen?.ContextRoi;
        if (configured is { Count: > 0 }) return configured;
        return _screenAnchorRoi is { } learned ? [learned] : [];
    }

    // ---------------------------------------------------------------------------------------------
    // Migawka
    // ---------------------------------------------------------------------------------------------

    private void PublishSnapshot()
    {
        MachineStatusSnapshot previous = Snapshot;
        MachineStatusSnapshot next = BuildSnapshot();
        Snapshot = next;

        if (previous.State != next.State
            || previous.UsingCachedFrame != next.UsingCachedFrame
            || previous.OcrAvailable != next.OcrAvailable
            || previous.RedNow != next.RedNow
            || previous.RedConfirmed != next.RedConfirmed
            || previous.LastError != next.LastError
            || previous.LastShortText != next.LastShortText
            || previous.Source != next.Source
            || previous.IdleWatchEnabled != next.IdleWatchEnabled
            || previous.ProgressFullConfirmed != next.ProgressFullConfirmed
            || previous.ProgressFullSince != next.ProgressFullSince
            || previous.ProgressZeroSince != next.ProgressZeroSince
            || previous.LastProgressPercent != next.LastProgressPercent
            || previous.LastProgressSource != next.LastProgressSource
            || previous.ProgramAborted != next.ProgramAborted
            || previous.MainScreenWatchEnabled != next.MainScreenWatchEnabled
            || previous.MainScreenVisible != next.MainScreenVisible
            || previous.MainScreenUnknownStreak != next.MainScreenUnknownStreak
            || previous.WrongScreenSince != next.WrongScreenSince)
        {
            StatusChanged?.Invoke(next);
        }
    }

    private MachineStatusSnapshot BuildSnapshot() => new()
    {
        MachineId = _machine.Id,
        Name = _machine.Name,
        Host = _machine.Host,
        Port = _machine.Port,
        Profile = _machine.Profile,
        State = _state,
        LastError = _lastError,
        LastFrameAt = _lastFrameAt,
        UsingCachedFrame = _usingCachedFrame,
        OcrAvailable = _ocr.IsAvailable,
        LastRedFraction = _lastRedFraction,
        RedNow = _redNow,
        RedConfirmed = _redConfirmed,
        LastOcrText = _lastOcrText,
        LastShortText = _lastShortText,
        Info = _lastInfo,
        OfflineSince = _offlineSince,
        Source = _lastSourceLabel,
        UpdatedAt = _clock.UtcNow,
        IdleWatchEnabled = _idleWatchEnabled,
        LastProgressFraction = _lastProgressFraction,
        ProgressFullNow = _progressFullNow,
        ProgressFullConfirmed = _progressFullConfirmed,
        ProgressFullSince = _progressFullSince?.Utc,
        ProgressZeroSince = _progressZeroSince?.Utc,
        LastProgressPercent = _lastProgressPercent,
        LastProgressText = _lastProgressText,
        LastProgressSource = _lastProgressSource,
        LastProgressBarText = _lastBar?.Description,
        ProgramStateText = _programState?.Description,
        ProgramAborted = _abortConfirmed,
        MainScreenWatchEnabled = _mainScreenWatchEnabled,
        MainScreenWatchRequired = _profile.MainScreen is { Enabled: true },
        MainScreenVisible = _mainScreenVisible,
        MainScreenUnknownStreak = _mainScreenUnknownStreak,
        MainScreenUnknownReason = _mainScreenUnknownReason,
        WrongScreenSince = _wrongScreenSince?.Utc,
        LastMainScreenCheckAt = _lastMainScreenCheckAt?.Utc,
    };
}
