using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Cluster;

/// <summary>
/// Spina komunikację klastra w jedną całość: <see cref="IPeerBus"/> + <see cref="PeerTable"/> +
/// <see cref="WatcherRoleController"/> + <see cref="StateMerger"/> + <see cref="AckMemory"/>. Używany zarówno
/// przez <c>MaszynyAlert.App</c> (właściwy program), jak i przez <c>MaszynyAlert.Probe</c> (<c>bus-test</c>).
/// Wątkowo bezpieczna: dostęp do stanu jest chroniony blokadą, a zdarzenia są emitowane POZA nią.
/// </summary>
/// <remarks>
/// Gdy <c>ClusterConfig.Enabled</c> jest <see langword="false"/> — tryb pojedynczego komputera: ten węzeł jest
/// obserwatorem od razu, na stałe, magistrala (<see cref="IPeerBus"/>) w ogóle nie jest uruchamiana ani używana,
/// a <see cref="TickAsync"/> tylko przelicza <see cref="MergedAlerts"/> z lokalnego stanu.
/// </remarks>
public sealed class ClusterNode : IClusterNode
{
    /// <summary>Górny limit wpisów <c>HeartbeatMessage.RecentAcks</c>.</summary>
    private const int MaxRecentAcksInHeartbeat = 20;

    private readonly ClusterConfig _cfg;
    private readonly IPeerBus _bus;
    private readonly IClock _clock;
    private readonly AckMemory _acks;
    private readonly string _version;
    private readonly ILogger _logger;

    private readonly PeerTable _peerTable;
    private readonly WatcherRoleController _role;

    private readonly object _lock = new();
    private readonly Dictionary<string, RemoteState> _remoteStates = new(StringComparer.Ordinal);

    /// <summary>Ostatni zaakceptowany (Seq, InstanceId) każdego nadawcy STATE — ochrona przed powtórką/odtworzeniem.</summary>
    private readonly Dictionary<string, (string InstanceId, long LastSeq)> _lastStateSeqByHost = new(StringComparer.Ordinal);

    private IReadOnlyList<Alert> _localAlerts = [];
    private IReadOnlyList<MachineStatusSnapshot> _localMachines = [];
    private IReadOnlyList<AlertKey> _localRecentlyClosed = [];
    private IReadOnlyList<Alert> _mergedAlerts = [];
    private IReadOnlyList<MachineStatusSnapshot> _remoteMachines = [];

    private bool _isWatcher;
    private string? _watcherHost;
    private bool _noWatcher;
    private bool _started;

    public ClusterNode(ClusterConfig cfg, IPeerBus bus, IClock clock, AckMemory acks, string version, ILogger? logger = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        // Never silently send a configured
        // v2 group over the legacy protocol, which cannot enforce its identity or episode boundaries.
        if (_cfg.Enabled && _cfg.Group is not null)
            throw new NotSupportedException("Ta wersja przygotowuje konfigurację grupy, ale nie obsługuje jeszcze protokołu v2. Współpraca nie została uruchomiona.");
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _acks = acks ?? throw new ArgumentNullException(nameof(acks));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _logger = logger ?? NullLogger.Instance;

        _peerTable = new PeerTable(clock, cfg, _logger);
        _role = new WatcherRoleController(clock, cfg, bus.LocalHost, _logger);

        if (!_cfg.Enabled)
        {
            // Tryb pojedynczego komputera: zawsze obserwator, bez klastra.
            _isWatcher = true;
            _watcherHost = bus.LocalHost;
            _noWatcher = false;
        }
    }

    /// <summary>Czy TEN komputer jest teraz obserwatorem.</summary>
    public bool IsWatcher
    {
        get { lock (_lock) { return _isWatcher; } }
    }

    /// <summary>Host aktualnego obserwatora klastra, albo <see langword="null"/>, gdy nikt żywy nim nie jest.</summary>
    public string? WatcherHost
    {
        get { lock (_lock) { return _watcherHost; } }
    }

    /// <summary>Czy nikt żywy (łącznie z tym komputerem) nie pełni roli obserwatora — UI pokazuje "brak obserwatora".</summary>
    public bool NoWatcher
    {
        get { lock (_lock) { return _noWatcher; } }
    }

    /// <summary>
    /// Wszyscy znani peerzy — zawiera też WIERSZ TEGO komputera (Host = <c>bus.LocalHost</c>, bieżąca rola,
    /// <c>LastSeen</c> = teraz), żeby okno statusu mogło pokazać tabelę komputerów z własnym wierszem.
    /// </summary>
    public IReadOnlyList<PeerInfo> Peers
    {
        get
        {
            lock (_lock)
            {
                List<PeerInfo> list = _peerTable.All
                    .Where(p => !string.Equals(p.Host, _bus.LocalHost, StringComparison.Ordinal))
                    .ToList();
                list.Add(BuildSelfPeerInfo());
                return list;
            }
        }
    }

    /// <summary>STATE od wszystkich żywych obserwatorów, scalone (<see cref="StateMerger"/>), plus stan lokalny, gdy ten komputer jest obserwatorem.</summary>
    public IReadOnlyList<Alert> MergedAlerts
    {
        get { lock (_lock) { return _mergedAlerts; } }
    }

    /// <summary>Maszyny ze stanu aktualnego obserwatora (<see cref="WatcherHost"/>) — lokalne, gdy obserwatorem jest ten komputer, inaczej z ostatniego odebranego STATE.</summary>
    public IReadOnlyList<MachineStatusSnapshot> RemoteMachines
    {
        get { lock (_lock) { return _remoteMachines; } }
    }

    /// <summary>Zmiana roli tego komputera (parametr = nowy <see cref="IsWatcher"/>).</summary>
    public event Action<bool>? RoleChanged;

    /// <summary>Zmiana w tabeli znanych komputerów (nowy host albo zmiana roli któregoś z nich).</summary>
    public event Action? PeersChanged;

    /// <summary>Zmienił się scalony zestaw alertów (<see cref="MergedAlerts"/>).</summary>
    public event Action<IReadOnlyList<Alert>>? AlertsChanged;

    /// <summary>Odebrano (lub lokalnie zapisano — patrz <see cref="SendAckAsync"/>) potwierdzenie alertu.</summary>
    public event Action<AckRecord>? AckReceived;

    /// <summary>W tym cyklu wykryto uśpienie komputera (patrz <see cref="WatcherRoleController"/>).</summary>
    public event Action? SleepDetected;

    /// <summary>Uruchamia magistralę (o ile klaster jest włączony) i zaczyna nasłuchiwać wiadomości. Idempotentne.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        if (!_cfg.Enabled)
        {
            return;
        }

        await _bus.StartAsync(ct).ConfigureAwait(false);
        _bus.Received += OnReceived;
    }

    /// <summary>Zatrzymuje odbieranie wiadomości (nie zamyka magistrali — do tego <see cref="DisposeAsync"/>).</summary>
    public Task StopAsync()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return Task.CompletedTask;
            }

            _started = false;
        }

        if (_cfg.Enabled)
        {
            _bus.Received -= OnReceived;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Wywoływana co ok. 1 s przez hosta (App/Probe). Rozgłasza heartbeat, ewentualnie STATE (gdy jest się
    /// obserwatorem), przelicza rolę i porządkuje tablicę peerów/zdalnych stanów. W trybie pojedynczego
    /// komputera (<c>ClusterConfig.Enabled == false</c>) tylko przelicza <see cref="MergedAlerts"/> z lokalnego stanu.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!_cfg.Enabled)
        {
            // Bez UDP nadal wykrywamy Resume/skok zegara, aby odtworzyć połączenia VNC.
            var localSelf = new PeerInfo(_bus.LocalHost, _cfg.Priority, PeerRole.Watcher, _version,
                _clock.UtcNow, 0, _bus.LocalHost, 0, 0);
            if (_role.Evaluate([], localSelf).SleepDetected) SleepDetected?.Invoke();
            RecomputeMerged();
            return;
        }

        DateTimeOffset now = _clock.UtcNow;

        IReadOnlyList<Alert> localAlertsSnapshot;
        IReadOnlyList<MachineStatusSnapshot> localMachinesSnapshot;
        IReadOnlyList<AlertKey> localRecentlyClosedSnapshot;
        bool wasWatcher;
        string? believedWatcher;
        lock (_lock)
        {
            localAlertsSnapshot = _localAlerts;
            localMachinesSnapshot = _localMachines;
            localRecentlyClosedSnapshot = _localRecentlyClosed;
            wasWatcher = _isWatcher;
            believedWatcher = _watcherHost;
        }

        int machinesConnected = localMachinesSnapshot.Count(m => m.State == MachineConnectionState.Connected);
        int machinesTotal = localMachinesSnapshot.Count;

        var heartbeat = new HeartbeatMessage
        {
            Sender = _bus.LocalHost,
            Version = _version,
            SentAt = now,
            Priority = _cfg.Priority,
            Role = wasWatcher ? PeerRole.Watcher : PeerRole.Ui,
            BelievedWatcher = believedWatcher,
            MachinesConnected = machinesConnected,
            MachinesTotal = machinesTotal,
            RecentAcks = _acks.Recent(TimeSpan.FromSeconds(60))
                .OrderByDescending(a => a.At)
                .Take(MaxRecentAcksInHeartbeat)
                .ToList(),
        };

        await TrySendAsync(heartbeat, "heartbeatu", ct).ConfigureAwait(false);

        if (wasWatcher)
        {
            var state = new StateMessage
            {
                Sender = _bus.LocalHost,
                Version = _version,
                SentAt = now,
                Alerts = localAlertsSnapshot,
                Machines = localMachinesSnapshot,
                RecentlyClosed = localRecentlyClosedSnapshot,
            };

            await TrySendAsync(state, "stanu (STATE)", ct).ConfigureAwait(false);
        }

        var self = new PeerInfo(
            _bus.LocalHost, _cfg.Priority, heartbeat.Role, _version, now, 0, believedWatcher, machinesConnected, machinesTotal);
        IReadOnlyList<PeerInfo> aliveOthers = _peerTable.Alive()
            .Where(p => !string.Equals(p.Host, _bus.LocalHost, StringComparison.Ordinal))
            .ToList();

        RoleDecision decision = _role.Evaluate(aliveOthers, self);

        bool roleChanged;
        lock (_lock)
        {
            roleChanged = _isWatcher != decision.IsWatcher;
            _isWatcher = decision.IsWatcher;
            _watcherHost = decision.WatcherHost;
            _noWatcher = decision.NoWatcher;
        }

        if (roleChanged)
        {
            RoleChanged?.Invoke(decision.IsWatcher);
        }

        if (decision.SleepDetected)
        {
            SleepDetected?.Invoke();
        }

        _peerTable.Prune();

        // Usuwamy zapamiętany STATE hosta, gdy: (a) przestał być żywy, ALBO (b) jest żywy, ale ostatni znany
        // heartbeat mówi, że przestał być obserwatorem (np. ustąpił roli) — bez tego jego stare alerty
        // "widmo" zostawałyby scalane na zawsze, mimo że host już ich nie rozgłasza.
        HashSet<string> aliveHostsNow = _peerTable.Alive().Select(p => p.Host).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, PeerInfo> knownPeersNow = _peerTable.All.ToDictionary(p => p.Host, StringComparer.Ordinal);
        lock (_lock)
        {
            List<string>? toRemove = null;
            foreach (string host in _remoteStates.Keys)
            {
                bool isAlive = aliveHostsNow.Contains(host);
                bool knownAsNonWatcher = knownPeersNow.TryGetValue(host, out PeerInfo? info) && info.Role != PeerRole.Watcher;
                if (!isAlive || knownAsNonWatcher)
                {
                    (toRemove ??= new List<string>()).Add(host);
                }
            }

            if (toRemove is not null)
            {
                foreach (string host in toRemove)
                {
                    _remoteStates.Remove(host);
                    _lastStateSeqByHost.Remove(host);
                }
            }
        }

        RecomputeMerged();
    }

    /// <summary>
    /// Zastępuje lokalny stan (alerty i maszyny widziane przez tego obserwatora) — App wywołuje po każdym cyklu
    /// monitorowania. Kopiuje listy (migawka).
    /// </summary>
    /// <param name="recentlyClosed">
    /// Klucze epizodów zamkniętych w ostatnich ~10 minutach (patrz <c>AlertStateMachine.RecentlyClosedKeys</c>) —
    /// rozgłaszane w kolejnym <c>StateMessage</c>, żeby pozostałe komputery zapomniały ACK tych kluczy. Opcjonalny — pominięcie (<see langword="null"/>) po prostu nic nie rozgłasza.
    /// </param>
    public void SetLocalState(
        IReadOnlyList<Alert> alerts, IReadOnlyList<MachineStatusSnapshot> machines, IReadOnlyList<AlertKey>? recentlyClosed = null)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(machines);

        lock (_lock)
        {
            _localAlerts = alerts.ToList();
            _localMachines = machines.ToList();
            _localRecentlyClosed = recentlyClosed?.ToList() ?? [];
        }

        RecomputeMerged();
    }

    /// <summary>
    /// Potwierdza alert ("Idę sprawdzić") i rozgłasza to po sieci. Local-first: zapis do <see cref="AckMemory"/>
    /// i zdarzenie <see cref="AckReceived"/> następują ZAWSZE lokalnie, niezależnie od tego, czy wysyłka po
    /// sieci się powiedzie (błąd sieci jest tylko logowany).
    /// </summary>
    public async Task SendAckAsync(AlertKey key, string user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        DateTimeOffset now = _clock.UtcNow;
        var record = new AckRecord(key, _bus.LocalHost, user, now);

        _acks.Record(record);
        AckReceived?.Invoke(record);
        RecomputeMerged();

        if (!_cfg.Enabled)
        {
            return;
        }

        try
        {
            var msg = new AckMessage
            {
                Sender = _bus.LocalHost,
                Version = _version,
                SentAt = now,
                Key = key,
                User = user,
                AckedAt = now,
            };
            await _bus.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Nie udało się rozgłosić potwierdzenia alertu \"{Key}\" po sieci — zapisane tylko lokalnie na tym komputerze.", key);
        }
    }

    public void ForceYield() => _role.ForceYield();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _bus.DisposeAsync().ConfigureAwait(false);
    }

    private async Task TrySendAsync(PeerMessage msg, string opisPl, CancellationToken ct)
    {
        try
        {
            await _bus.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Nie udało się rozgłosić {Opis}.", opisPl);
        }
    }

    private void OnReceived(PeerMessage msg)
    {
        if (msg.Group is not null) return;
        switch (msg)
        {
            case HeartbeatMessage hb:
                HandleHeartbeat(hb);
                break;
            case StateMessage state:
                HandleState(state);
                break;
            case AckMessage ack:
                HandleAck(ack);
                break;
        }
    }

    private void HandleHeartbeat(HeartbeatMessage hb)
    {
        bool changed;
        lock (_lock)
        {
            PeerInfo? before = _peerTable.All.FirstOrDefault(p => string.Equals(p.Host, hb.Sender, StringComparison.Ordinal));
            if (!_peerTable.Observe(hb))
            {
                return;
            }

            changed = before is null || before.Role != hb.Role;
        }

        if (changed)
        {
            PeersChanged?.Invoke();
        }

        // Retransmisja ACK-ów przez heartbeat — zgubiony pojedynczy AckMessage nie
        // zostawia obserwatora dzwoniącego bez końca, bo w ciągu ~1 s ACK i tak dojedzie w kolejnym heartbeacie.
        // Emitujemy AckReceived/przeliczamy tylko dla NOWYCH potwierdzeń, żeby App nie dostawało duplikatów co sekundę.
        bool anyNewAck = false;
        foreach (AckRecord ack in hb.RecentAcks)
        {
            AckRecord? existing = _acks.Get(ack.Key);
            if (existing is null || existing.At < ack.At)
            {
                _acks.Record(ack);
                AckReceived?.Invoke(ack);
                anyNewAck = true;
            }
        }

        if (anyNewAck)
        {
            RecomputeMerged();
        }
    }

    private void HandleState(StateMessage state)
    {
        if (_peerTable.IsRetiredInstance(state.Sender, state.InstanceId)) return;
        bool isDuplicate;
        lock (_lock)
        {
            // Ochrona przed powtórką/odtworzeniem STATE, per nadawca: pakiet jest
            // powtórką tylko wtedy, gdy pochodzi z TEJ SAMEJ instancji procesu nadawcy (patrz PeerMessage.InstanceId)
            // i ma Seq nie większy niż ostatnio zaakceptowany — restart nadawcy (inny InstanceId) zawsze przechodzi.
            isDuplicate = _lastStateSeqByHost.TryGetValue(state.Sender, out var lastSeen)
                && string.Equals(lastSeen.InstanceId, state.InstanceId, StringComparison.Ordinal)
                && state.Seq <= lastSeen.LastSeq;

            if (!isDuplicate)
            {
                _lastStateSeqByHost[state.Sender] = (state.InstanceId, state.Seq);

                bool senderDeclaresWatcher = _peerTable.All.Any(p =>
                    string.Equals(p.Host, state.Sender, StringComparison.Ordinal) && p.Role == PeerRole.Watcher);
                if (!senderDeclaresWatcher)
                {
                    _logger.LogDebug(
                        "Odebrano STATE od \"{Sender}\", który nie deklaruje roli obserwatora w swoim ostatnim heartbeacie — akceptuję mimo to.",
                        state.Sender);
                }

                _remoteStates[state.Sender] = new RemoteState(state.Alerts, state.Machines, _clock.MonotonicMs);
            }
        }

        if (isDuplicate)
        {
            return;
        }

        // Zasilenie AckMemory ze STATE — nowy obserwator, który przejął rolę, nie widząc
        // wcześniej AckMessage (był wyłączony/uśpiony), dowiaduje się o istniejących potwierdzeniach stąd.
        foreach (Alert alert in state.Alerts)
        {
            if (alert.Kind == AlertKind.Error) _acks.LinkEpisode(alert.Key, alert.EpisodeKeys);
            if (alert.Status is AlertStatus.Acknowledged or AlertStatus.Escalated
                && alert.AcknowledgedAt is { } ackedAt && alert.AckedByHost is { } ackedHost)
            {
                _acks.Record(new AckRecord(alert.Key, ackedHost, alert.AckedByUser ?? string.Empty, ackedAt));
            }
        }

        // Zapominanie ACK zamkniętych epizodów — tylko dla kluczy, które w TYM SAMYM
        // STATE nie są jednocześnie zgłoszone jako aktywne (epizod mógł się zamknąć i natychmiast otworzyć od nowa).
        if (state.RecentlyClosed.Count > 0)
        {
            HashSet<AlertKey> activeKeysInState = state.Alerts.SelectMany(a => a.EpisodeKeys.Append(a.Key)).ToHashSet();
            foreach (AlertKey key in state.RecentlyClosed)
            {
                if (!activeKeysInState.Contains(key))
                {
                    _acks.Retire(key, state.SentAt);
                }
            }
        }

        RecomputeMerged();
    }

    private void HandleAck(AckMessage ack)
    {
        var record = new AckRecord(ack.Key, ack.Sender, ack.User, ack.AckedAt);
        _acks.Record(record);
        AckReceived?.Invoke(record);
        RecomputeMerged();
    }

    private void RecomputeMerged()
    {
        List<Alert>? changedTo = null;

        lock (_lock)
        {
            HashSet<string> aliveHosts = _cfg.Enabled
                ? _peerTable.Alive().Select(p => p.Host).ToHashSet(StringComparer.Ordinal)
                : [];

            // Wiek maks. zapamiętanego STATE — nawet gdyby TickAsync nie zdążył
            // jeszcze usunąć wpisu po tym, jak nadawca przestał być obserwatorem, przeterminowany STATE i tak
            // nie wejdzie do scalenia (RecomputeMerged bywa wołane częściej niż raz na sekundę: SetLocalState,
            // HandleState, HandleAck, SendAckAsync).
            long nowMono = _clock.MonotonicMs;
            long maxRemoteStateAgeMs = Math.Max(5000, _cfg.StateIntervalMs * 5);

            var statesToMerge = new List<IReadOnlyList<Alert>>();
            foreach (KeyValuePair<string, RemoteState> entry in _remoteStates)
            {
                if (aliveHosts.Contains(entry.Key) && (nowMono - entry.Value.ReceivedAtMonotonicMs) <= maxRemoteStateAgeMs)
                {
                    statesToMerge.Add(entry.Value.Alerts);
                }
            }

            if (_isWatcher)
            {
                statesToMerge.Add(_localAlerts);
            }

            IReadOnlyList<Alert> merged = StateMerger.Merge(statesToMerge, _acks);
            if (!AlertListsEqual(merged, _mergedAlerts))
            {
                _mergedAlerts = merged;
                changedTo = merged.ToList();
            }

            _remoteMachines = _watcherHost switch
            {
                null => [],
                var host when string.Equals(host, _bus.LocalHost, StringComparison.Ordinal) => _localMachines,
                var host when _remoteStates.TryGetValue(host, out RemoteState remote) => remote.Machines,
                _ => _remoteMachines, // brak jeszcze STATE od nowo wybranego obserwatora — zachowaj poprzednie
            };
        }

        if (changedTo is not null)
        {
            AlertsChanged?.Invoke(changedTo);
        }
    }

    private PeerInfo BuildSelfPeerInfo() => new(
        _bus.LocalHost,
        _cfg.Priority,
        _isWatcher ? PeerRole.Watcher : PeerRole.Ui,
        _version,
        _clock.UtcNow,
        0,
        _watcherHost,
        _localMachines.Count(m => m.State == MachineConnectionState.Connected),
        _localMachines.Count);

    private static bool AlertListsEqual(IReadOnlyList<Alert> a, IReadOnlyList<Alert> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Key != b[i].Key || a[i].Status != b[i].Status || a[i].ShortText != b[i].ShortText)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct RemoteState(IReadOnlyList<Alert> Alerts, IReadOnlyList<MachineStatusSnapshot> Machines, long ReceivedAtMonotonicMs);
}
