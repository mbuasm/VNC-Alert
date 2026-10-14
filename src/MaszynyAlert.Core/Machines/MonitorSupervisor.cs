using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Machines;

public sealed class MonitorSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private AppConfig _cfg;
    private readonly Func<MachineConfig, string?> _passwordLookup;
    private IOcrEngine _ocr;
    private MessageMapper _mapper;
    private readonly AlertStateMachine _alerts;
    private readonly IClock _clock;
    private readonly ICaptureStore _captures;
    private readonly string? _testScreensDir;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<MonitorSupervisor> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, MachineMonitor> _monitors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MachineStatusSnapshot> _staticSnapshots = new(StringComparer.Ordinal);

    private readonly Dictionary<string, QtGuardState> _qtGuards = new(StringComparer.Ordinal);
    private List<Task> _runTasks = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Kontynuacja OSTATNIEGO zatrzymania — kompletna dopiero, gdy wszystkie monitory tej generacji
    /// faktycznie się zakończyły i ich CTS zostało zwolnione (nie tylko gdy minął "szybki" limit
    /// zwracany przez <see cref="StopAsync"/>). <see cref="StartAsync"/> czeka na nią przed utworzeniem
    /// nowego zestawu monitorów — inaczej dwa zestawy mogłyby przez chwilę żyć naraz (dwa połączenia do
    /// tej samej maszyny, dwa strumienie zdarzeń). Startuje jako ukończona (nigdy jeszcze
    /// nie zatrzymywano).
    /// </summary>
    private Task _stopped = Task.CompletedTask;

    public MonitorSupervisor(
        AppConfig cfg,
        Func<MachineConfig, string?> passwordLookup,
        IOcrEngine ocr,
        MessageMapper mapper,
        AlertStateMachine alerts,
        IClock clock,
        ICaptureStore captures,
        string? testScreensDir,
        ILoggerFactory? lf = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _passwordLookup = passwordLookup ?? throw new ArgumentNullException(nameof(passwordLookup));
        ArgumentNullException.ThrowIfNull(ocr);
        _ocr = ocr is SerializingOcrEngine ? ocr : new SerializingOcrEngine(ocr);
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _captures = captures ?? throw new ArgumentNullException(nameof(captures));
        _testScreensDir = testScreensDir;
        _loggerFactory = lf;
        _logger = lf?.CreateLogger<MonitorSupervisor>() ?? NullLogger<MonitorSupervisor>.Instance;
    }

    /// <summary>Czy nadzorca jest aktualnie uruchomiony (między <see cref="StartAsync"/> a <see cref="StopAsync"/>).</summary>
    public bool IsRunning { get; private set; }
    public bool MonitoringEnabled => _cfg.Enabled;
    public async Task ReconfigureAsync(AppConfig config, MessageMapper mapper, IOcrEngine ocr, CancellationToken ct)
    {
        ConfigLoader.Validate(config);
        await StopAsync().ConfigureAwait(false);
        await StoppedTask.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock) { _cfg = config; _mapper = mapper; _ocr = ocr; _staticSnapshots.Clear(); }
    }

    /// <summary>Zdarzenie wysyłane, gdy zmieniła się migawka którejkolwiek maszyny.</summary>
    public event Action? SnapshotsChanged;

    /// <summary>
    /// Aktualne migawki wszystkich maszyn z konfiguracji, w kolejności w niej podanej (także maszyny
    /// wyłączone — z <see cref="MachineConnectionState.Disabled"/>, i te z brakującym profilem — z <see cref="MachineConnectionState.Error"/>).
    /// </summary>
    public IReadOnlyList<MachineStatusSnapshot> Snapshots
    {
        get
        {
            lock (_lock)
            {
                var list = new List<MachineStatusSnapshot>(_cfg.Machines.Count);
                foreach (MachineConfig m in _cfg.Machines)
                {
                    if (_monitors.TryGetValue(m.Id, out MachineMonitor? monitor))
                    {
                        list.Add(monitor.Snapshot);
                    }
                    else if (_staticSnapshots.TryGetValue(m.Id, out MachineStatusSnapshot? snapshot))
                    {
                        list.Add(snapshot);
                    }
                }

                return list;
            }
        }
    }

    /// <summary>Zwraca obserwatora danej maszyny, albo <see langword="null"/>, gdy nie istnieje (nieznane id, maszyna bez profilu, albo nadzorca zatrzymany).</summary>
    public MachineMonitor? Get(string machineId)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        lock (_lock)
        {
            return _monitors.TryGetValue(machineId, out MachineMonitor? monitor) ? monitor : null;
        }
    }

    public bool AnyEnabledMachineIsReadyToConnect()
    {
        foreach (MachineConfig machine in _cfg.Machines)
        {
            if (!machine.Enabled)
            {
                continue;
            }

            if (!machine.RequiresPassword || !string.IsNullOrWhiteSpace(_passwordLookup(machine)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Task ukończony dopiero, gdy OSTATNIE zatrzymanie faktycznie dobiegło końca — wszystkie monitory tej
    /// generacji zakończyły pętlę i ich CTS zostało zwolnione. W przeciwieństwie do Taska zwracanego przez
    /// <see cref="StopAsync"/> (ograniczonego do <see cref="StopTimeout"/>) nie ma górnego limitu — czeka
    /// tyle, ile faktycznie potrzeba. Gdy nigdy nie zatrzymywano (albo nadzorca wciąż działa po ostatnim
    /// starcie), jest już ukończony.
    /// </summary>
    public Task StoppedTask
    {
        get
        {
            lock (_lock)
            {
                return _stopped;
            }
        }
    }

    /// <summary>
    /// Uruchamia <see cref="MachineMonitor"/> dla każdej maszyny z konfiguracji (włącznie z wyłączonymi —
    /// ich pętla tylko ustawia <see cref="MachineConnectionState.Disabled"/> i czeka). Maszyna wskazująca
    /// nieistniejący profil ekranu jest pomijana (loguje błąd, migawka na stałe <see cref="MachineConnectionState.Error"/>).
    /// Wywołanie, gdy nadzorca już działa, jest bezpiecznym no-opem. Gdy trwa jeszcze poprzednie
    /// zatrzymywanie (<see cref="StopAsync"/>), czeka na jego FAKTYCZNE zakończenie przed utworzeniem
    /// nowego zestawu monitorów — inaczej stary i nowy zestaw mogłyby przez chwilę żyć naraz (dwa
    /// połączenia do tej samej maszyny, dwa strumienie zdarzeń).
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (!_cfg.Enabled)
        {
            lock (_lock)
            {
                foreach (var m in _cfg.Machines)
                    _staticSnapshots[m.Id] = new MachineStatusSnapshot
                    {
                        MachineId = m.Id, Name = m.Name, Host = m.Host, Port = m.Port,
                        Profile = m.Profile, State = MachineConnectionState.Disabled,
                        LastError = "Monitorowanie wyłączone globalnie", Source = "-", UpdatedAt = _clock.UtcNow,
                    };
            }
            SnapshotsChanged?.Invoke();
            return;
        }
        Task pendingStop;
        lock (_lock)
        {
            if (IsRunning)
            {
                return;
            }

            pendingStop = _stopped;
        }

        await pendingStop.WaitAsync(ct).ConfigureAwait(false);

        lock (_lock)
        {
            if (IsRunning)
            {
                // Ktoś inny zdążył wystartować, gdy czekaliśmy na poprzednie zatrzymanie — no-op.
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _monitors.Clear();
            _staticSnapshots.Clear();
            var runTasks = new List<Task>(_cfg.Machines.Count);

            foreach (MachineConfig machine in _cfg.Machines)
            {
                if (!_cfg.Profiles.TryGetValue(machine.Profile, out ScreenProfile? profile))
                {
                    _logger.LogError(
                        "Maszyna '{MachineId}' ({Name}) wskazuje profil ekranu '{Profile}', którego nie ma w konfiguracji — pomijam tę maszynę.",
                        machine.Id, machine.Name, machine.Profile);

                    _staticSnapshots[machine.Id] = new MachineStatusSnapshot
                    {
                        MachineId = machine.Id,
                        Name = machine.Name,
                        Host = machine.Host,
                        Port = machine.Port,
                        Profile = machine.Profile,
                        State = MachineConnectionState.Error,
                        LastError = $"Brak profilu ekranu '{machine.Profile}' w konfiguracji.",
                        Source = "-",
                        UpdatedAt = _clock.UtcNow,
                    };
                    continue;
                }

                MachineConfig machineForFactory = machine;
                IScreenSource VncFactory() =>
                    new VncScreenSource(machineForFactory, _passwordLookup(machineForFactory), _cfg.Timing, _loggerFactory);

                FolderScreenSource? testOverride = _testScreensDir is not null
                    ? new FolderScreenSource(machine.Id, _testScreensDir)
                    : null;

                var monitor = new MachineMonitor(
                    machine,
                    profile,
                    VncFactory,
                    testOverride,
                    _ocr,
                    _mapper,
                    _alerts,
                    _clock,
                    _cfg.Timing,
                    _captures,
                    _loggerFactory?.CreateLogger<MachineMonitor>(),
                    delay: null,
                    diagnostics: _cfg.Diagnostics,
                    qtGuard: QtGuardFor(machine));

                monitor.StatusChanged += OnMonitorStatusChanged;
                _monitors[machine.Id] = monitor;
                runTasks.Add(Task.Run(() => monitor.RunAsync(cts.Token)));
            }

            _runTasks = runTasks;
            IsRunning = true;
        }
    }

    /// <summary>
    /// Zatrzymuje wszystkich obserwatorów: anuluje ich pętle i zwraca Task ograniczony do
    /// <see cref="StopTimeout"/> (żeby wywołujący — np. zamykanie aplikacji — nie wisiał w nieskończoność).
    /// Rzeczywiste sprzątanie (oczekiwanie na WSZYSTKIE zakończone pętle, odpięcie zdarzeń i dopiero potem
    /// zwolnienie CTS) toczy się dalej w tle niezależnie od tego limitu — patrz <see cref="StoppedTask"/>
    /// dla Taska bez górnego ograniczenia. Zwalnianie CTS przed faktycznym zakończeniem pętli groziłoby
    /// <see cref="ObjectDisposedException"/> w kodzie, który wciąż trzyma jego token.
    /// Po zakończeniu (rzeczywistym) <see cref="StartAsync"/> może zostać wywołane ponownie.
    /// </summary>
    public Task StopAsync()
    {
        CancellationTokenSource? cts;

        lock (_lock)
        {
            if (!IsRunning)
            {
                return _stopped;
            }

            cts = _cts!;
            List<Task> tasks = _runTasks;
            List<MachineMonitor> monitors = new(_monitors.Values);
            IsRunning = false;

            // CancelAsync kończy się dopiero po callbackach; Drain nie może zwolnić
            // CTS przed anulowaniem, także gdy lista pętli jest pusta.
            _stopped = DrainAsync(cts, tasks, monitors, cts.CancelAsync());
        }

        return Task.WhenAny(_stopped, Task.Delay(StopTimeout));
    }

    /// <summary>Czeka na faktyczne zakończenie wszystkich pętli tej generacji, odpina zdarzenia i DOPIERO WTEDY zwalnia CTS.</summary>
    private async Task DrainAsync(CancellationTokenSource cts, List<Task> tasks, List<MachineMonitor> monitors, Task cancellation)
    {
        try
        {
            await Task.WhenAll(tasks.Append(cancellation)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Pętle MachineMonitor same łapią własne wyjątki — to tylko siatka bezpieczeństwa, żeby
            // sprzątanie (odpięcie zdarzeń, dispose CTS) zawsze doszło do końca.
            _logger.LogDebug(ex, "Błąd (zignorowany) podczas oczekiwania na zakończenie monitorów przy zatrzymywaniu.");
        }

        foreach (MachineMonitor monitor in monitors)
        {
            monitor.StatusChanged -= OnMonitorStatusChanged;
        }

        cts.Dispose();

        lock (_lock)
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            if (ReferenceEquals(_runTasks, tasks))
            {
                _runTasks = new List<Task>();
            }
        }
    }

    public Task PruneCapturesAsync(TimeSpan? retentionOverride = null)
    {
        TimeSpan retention = retentionOverride ?? TimeSpan.FromDays(_cfg.Paths.CaptureRetentionDays);
        long maxBytes = _cfg.Paths.CapturesMaxMegabytes * 1024L * 1024L;
        string[] protectedFiles = _alerts.ActiveAlerts
            .Select(a => a.CaptureFile)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _captures.Prune(retention, maxBytes, protectedFiles);
        return Task.CompletedTask;
    }

    /// <summary>Liczba plików, zajęte miejsce i wiek najstarszego zrzutu — do licznika w Panelu administracyjnym.</summary>
    public CaptureFolderStats DescribeCaptures() => _captures.Describe();

    /// <summary>Twardy limit miejsca na zrzuty (MB) z bieżącej konfiguracji — pokazywany obok licznika.</summary>
    public int CapturesMaxMegabytes => _cfg.Paths.CapturesMaxMegabytes;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void OnMonitorStatusChanged(MachineStatusSnapshot snapshot) => SnapshotsChanged?.Invoke();

    private QtGuardState QtGuardFor(MachineConfig machine)
    {
        if (!_qtGuards.TryGetValue(machine.Id, out QtGuardState? state) || !state.Matches(machine))
        {
            state = new QtGuardState(machine.Host, machine.Port);
            _qtGuards[machine.Id] = state;
        }

        return state;
    }
}
