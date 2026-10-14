using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Cluster;

/// <summary>Stable application endpoint. Replacements close the previous socket before binding a new
/// one, discard its callbacks, and preserve episode state within the same group.</summary>
public sealed class ClusterRuntime : IClusterNode
{
    private readonly IClock _clock;
    private readonly AckMemory _legacyAcks;
    private readonly string _host, _version;
    private readonly Func<ClusterConfig, string, IPeerBus> _busFactory;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private IClusterNode _node;
    private string? _key;
    private bool _started;
    private IReadOnlyList<Alert> _local = [];
    private IReadOnlyList<MachineStatusSnapshot> _machines = [];
    private WatcherReadiness _readiness = new(false, false, []);
    // Episode state of the last group node (closures, acknowledgements, retired UUIDs). Kept while the
    // group is disabled or a switch fails, so re-enabling the same group does not revive closed episodes.
    private GroupRuntimeState? _retained;
    private Guid? _retainedGroup;
    public ClusterConfig Configuration { get; private set; }
    public bool NetworkActive { get; private set; }
    public string? Failure { get; private set; }
    public Guid? InterruptedGeneration { get; set; }
    public GroupClusterNode? GroupNode => _node as GroupClusterNode;
    public string LocalHost => _host;
    public string Version => _version;
    public bool StateFresh => GroupNode?.StateFresh ?? !NoWatcher;
    public bool IsWatcher => _node.IsWatcher;
    public string? WatcherHost => _node.WatcherHost;
    public bool NoWatcher => _node.NoWatcher;
    public IReadOnlyList<PeerInfo> Peers => _node.Peers;
    public IReadOnlyList<Alert> MergedAlerts => _node.MergedAlerts;
    public IReadOnlyList<MachineStatusSnapshot> RemoteMachines => _node.RemoteMachines;
    public event Action<bool>? RoleChanged;
    public event Action? PeersChanged;
    public event Action<IReadOnlyList<Alert>>? AlertsChanged;
    public event Action<AckRecord>? AckReceived;
    public event Action? SleepDetected;
    public event Action<IReadOnlyList<Alert>, IReadOnlyList<EpisodeClosure>>? EpisodeStateReceived;

    /// <summary>Wspólne instrukcje naprawy grupy; przeżywa podmianę transportu.</summary>
    public IInstructionSync? Instructions
    {
        get => _instructions;
        set { _instructions = value; if (GroupNode is { } group) group.Instructions = value; }
    }
    private IInstructionSync? _instructions;

    public ISharedConfigSync? SharedConfig
    {
        get => _sharedConfig;
        set { _sharedConfig = value; if (GroupNode is { } group) group.SharedConfig = value; }
    }
    private ISharedConfigSync? _sharedConfig;

    /// <summary>Wspólna konfiguracja zmieniła się przez sieć — ustawienia trzeba przeliczyć.</summary>
    public event Action? SharedConfigChanged;

    /// <summary>Rozgłasza własną zmianę wspólnej konfiguracji; poza grupą nie ma dokąd jej wysłać.</summary>
    public Task BroadcastSharedConfigAsync(SharedConfigDocument document, CancellationToken ct) =>
        GroupNode?.BroadcastSharedConfigAsync(document, ct) ?? Task.CompletedTask;

    /// <summary>Wspólny raport postojów grupy; przeżywa podmianę transportu.</summary>
    public IDowntimeSync? Downtime
    {
        get => _downtime;
        set { _downtime = value; if (GroupNode is { } group) group.Downtime = value; }
    }
    private IDowntimeSync? _downtime;

    public IObservationSync? Observation
    {
        get => _observation;
        set { _observation = value; if (GroupNode is { } group) group.Observation = value; }
    }
    private IObservationSync? _observation;

    /// <summary>Dziennik czasu obserwacji dostał przedziały z innego komputera grupy.</summary>
    public event Action? ObservationChanged;

    /// <summary>Instrukcje zmieniły się przez sieć — pomoc trzeba pokazać na nowo.</summary>
    public event Action? InstructionsChanged;

    /// <summary>Dziennik postojów dostał wpisy z innego komputera grupy.</summary>
    public event Action? DowntimeChanged;

    /// <summary>Rozgłasza własną zmianę instrukcji; poza grupą nie ma dokąd jej wysłać i nic się nie dzieje.</summary>
    public Task BroadcastInstructionAsync(InstructionEntry entry, CancellationToken ct) =>
        GroupNode?.BroadcastInstructionAsync(entry, ct) ?? Task.CompletedTask;

    public ClusterRuntime(ClusterConfig cfg, string? key, IClock clock, AckMemory acks, string host, string version,
        Func<ClusterConfig, string, IPeerBus>? busFactory = null)
    {
        Configuration = cfg; _key = key; _clock = clock; _legacyAcks = acks; _host = host; _version = version;
        _busFactory = busFactory ?? ((c, k) => new UdpPeerBus(c, HmacSigner.FromBase64(k), clock, host));
        _node = CreateSolo(cfg); Attach(_node);
    }
    private IClusterNode CreateSolo(ClusterConfig cfg)
    {
        var local = cfg with { Enabled = false };
        var bus = new InMemoryPeerHub(new HmacSigner(HmacSigner.GenerateKey()), _clock).CreateBus(_host, local);
        return new ClusterNode(local, bus, _clock, _legacyAcks, _version);
    }
    private IClusterNode Create(ClusterConfig cfg, string? key)
    {
        if (!cfg.Enabled) return CreateSolo(cfg);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Brak lokalnego klucza grupy.");
        _ = HmacSigner.FromBase64(key);
        var bus = _busFactory(cfg, key);
        return cfg.Group is null ? new ClusterNode(cfg, bus, _clock, _legacyAcks, _version)
            : new GroupClusterNode(cfg, bus, _clock, _version);
    }
    private void Attach(IClusterNode node)
    {
        node.RoleChanged += v => { if (ReferenceEquals(_node, node)) RoleChanged?.Invoke(v); };
        node.PeersChanged += () => { if (ReferenceEquals(_node, node)) PeersChanged?.Invoke(); };
        node.AlertsChanged += a => { if (ReferenceEquals(_node, node)) AlertsChanged?.Invoke(a); };
        node.AckReceived += a => { if (ReferenceEquals(_node, node)) AckReceived?.Invoke(a); };
        node.SleepDetected += () => { if (ReferenceEquals(_node, node)) SleepDetected?.Invoke(); };
        if (node is GroupClusterNode group)
        {
            group.EpisodeStateReceived += (alerts, closed) =>
            { if (ReferenceEquals(_node, node)) EpisodeStateReceived?.Invoke(alerts, closed); };
            group.InstructionsChanged += () =>
            { if (ReferenceEquals(_node, node)) InstructionsChanged?.Invoke(); };
            group.SharedConfigChanged += () =>
            { if (ReferenceEquals(_node, node)) SharedConfigChanged?.Invoke(); };
            group.DowntimeChanged += () =>
            { if (ReferenceEquals(_node, node)) DowntimeChanged?.Invoke(); };
            group.ObservationChanged += () =>
            { if (ReferenceEquals(_node, node)) ObservationChanged?.Invoke(); };
        }
    }
    public void SetReadiness(WatcherReadiness readiness) { _readiness = readiness; GroupNode?.SetReadiness(readiness); }
    /// <summary>Startup could not use the committed group data (corrupted journal/generation); the panel shows why.</summary>
    public void MarkStartupFailure(string message) => Failure = message;
    public async Task StartAsync(CancellationToken ct)
    {
        await _operation.WaitAsync(ct);
        try
        {
            if (_started) return;
            _started = true;
            try { await Replace(Configuration, _key, null, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { await Fallback(ct); Failure = "Obserwacja lokalna, brak współpracy: " + SafeFailure(ex); }
        }
        finally { _operation.Release(); }
    }
    /// <summary>
    /// Czy przełączenie z <paramref name="previous"/> na <paramref name="next"/> wprowadza ten komputer do INNEJ grupy niż ta, w której
    /// był: włączona grupa v2 o innym <see cref="GroupIdentity.GroupId"/> — z pracy samodzielnej, ze starego trybu bez grupy albo
    /// z innej grupy (kreator „Utwórz grupę”, zaproszenie). Wtedy <see cref="SwitchAsync"/> zdejmuje z warstwy wspólnej przełączniki
    /// maszyn zapisane na tym komputerze poza tą grupą. Zmiana ustawień tej samej grupy (priorytet, interfejs, nowa
    /// generacja profilu, klucz), wyłączenie i ponowne włączenie tej samej grupy oraz wyjście z grupy — nie; restart programu
    /// i aktualizacja nie przechodzą przez <see cref="SwitchAsync"/> wcale.
    /// </summary>
    public static bool JoinsAnotherGroup(ClusterConfig previous, ClusterConfig next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        return next.Enabled && next.Group is { } group && group.GroupId != previous.Group?.GroupId;
    }

    /// <summary>
    /// Warstwa wspólna sprzed ostatniego udanego przełączenia do INNEJ grupy — do cofnięcia przez <see cref="RevertSwitchAsync"/>.
    /// <see cref="Document"/> = dokument sprzed zdjęcia przełączników; <see langword="null"/>, gdy przełączników wtedy nie było.
    /// </summary>
    private sealed record LayerBeforeJoin(SharedConfigDocument? Document);

    /// <summary>Stan warstwy sprzed ostatniego udanego dołączenia; <see langword="null"/>, gdy ostatnie przełączenie nie zmieniało grupy.</summary>
    private LayerBeforeJoin? _beforeJoin;

    /// <summary>
    /// Przełączniki maszyn zdjęte z warstwy wspólnej przy ostatnim udanym przełączeniu do innej grupy (do wpisu w logu), albo
    /// <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<string, SharedMachineSettings>? DetachedMachines => _beforeJoin?.Document?.Machines;

    /// <summary>
    /// Przełącza transport na <paramref name="cfg"/>. Przy przejściu do INNEJ grupy (<see cref="JoinsAnotherGroup"/>) zdejmuje z warstwy
    /// wspólnej przełączniki maszyn zapisane poza nią (<see cref="ISharedConfigSync.DetachMachines"/>: czas i autor zmiany bez zmian) —
    /// po zatrzymaniu węzła poprzedniej grupy, a przed startem nowego, więc nie trafią do nowej grupy ani pierwszym heartbeatem, ani
    /// odpowiedzią na cudzy. Nieudane przełączenie oddaje je przed powrotem do poprzedniej konfiguracji.
    /// </summary>
    public Task SwitchAsync(ClusterConfig cfg, string? key, CancellationToken ct) => SwitchCoreAsync(cfg, key, revert: false, ct);

    /// <summary>
    /// Cofa ostatnie <see cref="SwitchAsync"/>, gdy zmiany ustawień grupy nie udało się zatwierdzić (rollback w <c>AppOrchestrator</c>):
    /// wraca do <paramref name="previous"/> jak <see cref="SwitchAsync"/>, ale nie traktuje tego jako dołączenia — gdy tamto przełączenie
    /// weszło do innej grupy, warstwa dostaje z powrotem przełączniki maszyn sprzed niego (a gdy ich wtedy nie było, schodzą te, które
    /// zdążyły przyjść z tamtej grupy) — zanim ruszy węzeł poprzedniej grupy.
    /// </summary>
    public Task RevertSwitchAsync(ClusterConfig previous, string? previousKey, CancellationToken ct) =>
        SwitchCoreAsync(previous, previousKey, revert: true, ct);

    private async Task SwitchCoreAsync(ClusterConfig cfg, string? key, bool revert, CancellationToken ct)
    {
        await _operation.WaitAsync(ct);
        try
        {
            var previous = Configuration; var previousKey = _key;
            var state = Retain(previous.Group?.GroupId);
            var sync = _sharedConfig;
            bool joins = !revert && JoinsAnotherGroup(previous, cfg);
            LayerBeforeJoin? undo = revert ? _beforeJoin : null;
            // Co ta próba zmieniła w warstwie — do odwrócenia, gdy się nie uda.
            SharedConfigDocument? dropped = null;
            bool restored = false;
            Action? betweenNodes = sync is null ? null
                : joins ? () => dropped = sync.DetachMachines()
                : undo is { Document: { } beforeJoin } ? () => { sync.RestoreMachines(beforeJoin); restored = true; }
                : undo is not null ? () => dropped = sync.DetachMachines()
                : null;
            try
            {
                await Replace(cfg, key, previous.Group?.GroupId == cfg.Group?.GroupId ? state : null, ct, betweenNodes);
                Configuration = cfg; _key = key; Failure = null;
                if (cfg.Group?.GroupId != previous.Group?.GroupId) { _retained = null; _retainedGroup = null; }
                _beforeJoin = joins && sync is not null ? new LayerBeforeJoin(dropped) : null;
            }
            catch
            {
                // Wracamy do poprzedniej konfiguracji transportu: warstwa wspólna ma być taka, jak przy niej. Przełączniki zdjęte przy tej
                // próbie wracają, a oddane przy nieudanym cofnięciu znów schodzą (inaczej weszłyby do grupy, w której zostajemy).
                if (dropped is { } before) UndoLayerChange(() => sync!.RestoreMachines(before));
                if (restored) UndoLayerChange(() => sync!.DetachMachines());
                try { await Replace(previous, previousKey, state, CancellationToken.None); }
                catch { await Fallback(CancellationToken.None); Failure = "Nie udało się przywrócić UDP. Obserwacja lokalna, brak współpracy."; }
                throw;
            }
        }
        finally { _operation.Release(); }
    }

    /// <summary>
    /// Cofnięcie zmiany warstwy wspólnej po nieudanym przełączeniu nie może zasłonić pierwotnego błędu ani zatrzymać powrotu do poprzedniej
    /// konfiguracji; gdy się nie uda, Panel („Praca na kilku komputerach”) pokazuje, co sprawdzić.
    /// </summary>
    private void UndoLayerChange(Action undo)
    {
        try { undo(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Failure = "Nie przywrócono przełączników maszyn po nieudanej zmianie grupy — sprawdź Panel → Ustawienia → Konfiguracja → Maszyny.";
        }
    }
    /// <summary>Snapshot of the live group node, or the state retained from the last one of the same group.</summary>
    private GroupRuntimeState? Retain(Guid? group)
    {
        if (GroupNode is { } node) { _retained = node.CaptureState(); _retainedGroup = group; }
        return _retainedGroup == group ? _retained : null;
    }
    /// <param name="betweenNodes">
    /// Zmiana warstwy wspólnej przy zmianie grupy (<see cref="SwitchCoreAsync"/>): wołana, gdy stary węzeł już nic nie odbiera
    /// (<c>UdpPeerBus.DisposeAsync</c> czeka na koniec pętli odbioru), a nowy jeszcze nie ruszył — nie przeplata się z wymianą
    /// z żadną grupą.
    /// </param>
    private async Task Replace(ClusterConfig cfg, string? key, GroupRuntimeState? state, CancellationToken ct, Action? betweenNodes = null)
    {
        var old = _node;
        // Invalidate callbacks before disposing the transport, including callbacks queued during shutdown.
        _node = CreateSolo(cfg);
        await old.DisposeAsync();
        betweenNodes?.Invoke();
        var next = Create(cfg, key);
        try
        {
            if (next is GroupClusterNode group)
            {
                group.Instructions = _instructions;
                group.SharedConfig = _sharedConfig;
                group.Downtime = _downtime;
                group.Observation = _observation;
                group.SetReadiness(_readiness);
                group.RestoreState(state ?? new(_local, [], [], old.IsWatcher));
            }
            next.SetLocalState(_local, _machines);
            await next.StartAsync(ct);
        }
        catch { await next.DisposeAsync(); throw; }
        await _node.DisposeAsync();
        _node = next; Attach(next); NetworkActive = cfg.Enabled;
        // Local state published while the transport was being replaced went to the placeholder node.
        next.SetLocalState(_local, _machines);
        AlertsChanged?.Invoke(next.MergedAlerts); PeersChanged?.Invoke();
    }
    private async Task Fallback(CancellationToken ct)
    {
        await _node.DisposeAsync(); _node = CreateSolo(Configuration); Attach(_node);
        _node.SetLocalState(_local, _machines); await _node.StartAsync(ct); NetworkActive = false;
    }
    private static string SafeFailure(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException => "nie można otworzyć portu UDP.",
        ArgumentException or InvalidOperationException => "sprawdź klucz i interfejs sieci.",
        _ => "nie można uruchomić transportu."
    };
    public async Task TickAsync(CancellationToken ct)
    { await _operation.WaitAsync(ct); try { await _node.TickAsync(ct); } finally { _operation.Release(); } }
    public void SetLocalState(IReadOnlyList<Alert> alerts, IReadOnlyList<MachineStatusSnapshot> machines, IReadOnlyList<AlertKey>? recentlyClosed = null)
    { _local = alerts.ToArray(); _machines = machines.ToArray(); _node.SetLocalState(_local, _machines, recentlyClosed); }
    public void RecordClosure(Alert alert) => GroupNode?.RecordClosure(alert);
    public void ForceYield() => _node.ForceYield();
    public async Task SendAckAsync(AlertKey key, string user, CancellationToken ct)
    { await _operation.WaitAsync(ct); try { await _node.SendAckAsync(key, user, ct); } finally { _operation.Release(); } }
    public Task<string?> CheckConnectionAsync(TimeSpan timeout, CancellationToken ct) => GroupNode is { } group
        ? group.CheckConnectionAsync(timeout, ct) : Task.FromResult<string?>(null);
    public async Task StopAsync()
    { await _operation.WaitAsync(); try { _started = false; NetworkActive = false; await _node.DisposeAsync(); } finally { _operation.Release(); } }
    public async ValueTask DisposeAsync() => await StopAsync();
}
