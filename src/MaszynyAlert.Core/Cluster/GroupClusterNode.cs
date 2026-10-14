using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Cluster;

/// <summary>Protocol v2: explicit group boundary, episode-scoped ACK/closures and retained alarms.
/// Transport authenticates datagrams; this class validates semantic compatibility and replay.</summary>
public sealed class GroupClusterNode : IClusterNode
{
    private readonly ClusterConfig _cfg;
    private readonly IPeerBus _bus;
    private readonly IClock _clock;
    private readonly string _version;
    private readonly object _gate = new();
    private readonly Dictionary<string, Member> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Alert> _known = new();
    private readonly Dictionary<Guid, (EpisodeClosure Closure, long Received)> _closed = new();
    private readonly Dictionary<Guid, (AckRecord Ack, long Received)> _acks = new();
    private readonly Dictionary<Guid, TaskCompletionSource<string>> _probes = new();
    /// <summary>
    /// Nasze prośby o PEŁNE okno postojów: <c>RequestId</c> → kiedy wysłana (zegar monotoniczny) i heartbeat którego partnera
    /// ją wywołał. Pełne okno partnera po starcie albo odzyskaniu łączności kończy wyłącznie odpowiedź na jedną z nich — patrz
    /// <see cref="EndsFullDowntimeWindow"/>. Pamięć ograniczona: <see cref="MaxRememberedFullDowntimeRequests"/> wpisów,
    /// <see cref="FullDowntimeRequestLifetimeMs"/>, a wpisy wywołane przez partnera usuniętego z tabeli znikają razem z nim.
    /// </summary>
    private readonly Dictionary<Guid, (long SentAt, string TriggeredBy)> _fullDowntimeRequests = new();
    private IInstructionSync? _instructions;
    private ISharedConfigSync? _sharedConfig;
    private IDowntimeSync? _downtime;
    private IObservationSync? _observation;
    private IReadOnlyList<Alert> _local = [];
    private IReadOnlyList<MachineStatusSnapshot> _machines = [];
    private IReadOnlyList<Alert> _merged = [];
    private WatcherReadiness _readiness = new(false, false, []);
    private bool _started, _watcher;
    private string? _watcherHost;
    private long? _candidateSince, _lastTick;
    private int _yield;
    private string? _transportError;
    private string? _roleChangeReason;
    private long StateLimit => Math.Max(5000L, 5L * _cfg.StateIntervalMs);
    public GroupIdentity Identity => _cfg.Group!;

    public GroupClusterNode(ClusterConfig cfg, IPeerBus bus, IClock clock, string version)
    {
        cfg.Group?.Validate();
        if (!cfg.Enabled || cfg.Group is null) throw new ArgumentException("Wymagana włączona grupa v2.");
        _cfg = cfg; _bus = bus; _clock = clock; _version = version;
    }

    /// <summary>
    /// Wspólne instrukcje naprawy grupy. Ustawiane przez warstwę aplikacji;
    /// bez niego węzeł nie wysyła ani nie przyjmuje instrukcji.
    /// </summary>
    public IInstructionSync? Instructions
    {
        get { lock (_gate) return _instructions; }
        set { lock (_gate) _instructions = value; }
    }

    /// <summary>Ile razy uzgadnianie instrukcji dopisało tu zmiany od innego komputera (diagnostyka i testy).</summary>
    public event Action? InstructionsChanged;

    public ISharedConfigSync? SharedConfig
    {
        get { lock (_gate) return _sharedConfig; }
        set { lock (_gate) _sharedConfig = value; }
    }

    /// <summary>Warstwa przyszła z innego komputera i została zapisana — konfigurację trzeba przeliczyć.</summary>
    public event Action? SharedConfigChanged;

    /// <summary>
    /// Wspólny raport postojów grupy. Ustawiane przez warstwę aplikacji; bez
    /// niego węzeł nie prosi o postoje ani nie odpowiada na cudze prośby.
    /// </summary>
    public IDowntimeSync? Downtime
    {
        get { lock (_gate) return _downtime; }
        set { lock (_gate) _downtime = value; }
    }

    /// <summary>Uzupełnianie dopisało tu postoje z innego komputera (Panel odświeża wtedy raport).</summary>
    public event Action? DowntimeChanged;

    public IObservationSync? Observation
    {
        get { lock (_gate) return _observation; }
        set { lock (_gate) _observation = value; }
    }

    /// <summary>Uzupełnianie dopisało tu przedziały obserwacji z innego komputera.</summary>
    public event Action? ObservationChanged;

    public bool IsWatcher { get { lock (_gate) return _watcher; } }
    public string? WatcherHost { get { lock (_gate) return _watcherHost; } }
    public bool NoWatcher => WatcherHost is null;
    public string? TransportError { get { lock (_gate) return _transportError; } }
    public WatcherReadiness Readiness { get { lock (_gate) return _readiness; } }
    public void SetReadiness(WatcherReadiness value) { lock (_gate) _readiness = value; }
    public GroupRuntimeState CaptureState()
    {
        lock (_gate) return new(_known.Values.ToArray(), _closed.Values.Select(p => p.Closure).ToArray(),
            _acks.Values.Select(p => p.Ack).ToArray(), _watcher);
    }
    public void RestoreState(GroupRuntimeState state)
    {
        lock (_gate)
        {
            foreach (var c in state.Closed) Close(c);
            foreach (var a in state.Alerts) Merge(a);
            foreach (var a in state.Acks) RememberAck(a);
            _watcher = state.WasWatcher && _readiness.CanObserve;
            _watcherHost = _watcher ? _bus.LocalHost : null;
        }
        Publish();
    }
    public bool StateFresh
    {
        get { lock (_gate) return _watcher || (_watcherHost is { } host && _members.TryGetValue(host, out var m) && Fresh(m)); }
    }
    public long? StateAgeMs
    {
        get { lock (_gate) return _watcher ? 0 : _watcherHost is { } host && _members.TryGetValue(host, out var m)
            && m.StateAt is { } at ? Math.Max(0, _clock.MonotonicMs - at) : null; }
    }
    public IReadOnlyList<PeerInfo> Peers
    {
        get { lock (_gate) return _members.Values.Select(m => m.Info).Append(new PeerInfo(_bus.LocalHost,
            _cfg.Priority, _watcher ? PeerRole.Watcher : PeerRole.Ui, _version, _clock.UtcNow, 0,
            _watcherHost, _machines.Count(m => m.State == MachineConnectionState.Connected), _machines.Count)
            { Readiness = _readiness, InstructionsHash = _instructions?.Hash,
              SharedConfigHash = _sharedConfig?.Hash }).ToArray(); }
    }
    public IReadOnlyList<Alert> MergedAlerts { get { lock (_gate) return _merged; } }
    public IReadOnlyList<MachineStatusSnapshot> RemoteMachines
    {
        get { lock (_gate) return _watcher ? _machines : _watcherHost is { } host && _members.TryGetValue(host, out var m)
            ? m.Machines : []; }
    }
    public event Action<bool>? RoleChanged;
    public event Action? PeersChanged;
    public event Action<IReadOnlyList<Alert>>? AlertsChanged;
    public event Action<AckRecord>? AckReceived;
    public event Action? SleepDetected;
    public event Action<IReadOnlyList<Alert>, IReadOnlyList<EpisodeClosure>>? EpisodeStateReceived;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        _bus.Received += Receive;
        try { await _bus.StartAsync(ct).ConfigureAwait(false); _started = true; }
        catch { _bus.Received -= Receive; throw; }
    }
    public Task StopAsync()
    {
        _started = false; _bus.Received -= Receive;
        lock (_gate) { foreach (var p in _probes.Values) p.TrySetCanceled(); _probes.Clear(); }
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync() { await StopAsync(); await _bus.DisposeAsync(); }
    public void ForceYield() => Interlocked.Exchange(ref _yield, 1);

    public async Task TickAsync(CancellationToken ct)
    {
        HeartbeatMessage hb;
        StateMessage? state;
        bool changed, sleep;
        lock (_gate)
        {
            long now = _clock.MonotonicMs;
            sleep = Interlocked.Exchange(ref _yield, 0) != 0 || (_lastTick is { } last && now - last > _cfg.SleepJumpThresholdMs);
            _lastTick = now;
            var eligible = _members.Values.Where(m => Alive(m) && m.Info.Compatibility == GroupCompatibility.Compatible
                && m.Info.Readiness?.CanObserve == true && (m.Info.Role != PeerRole.Watcher || Fresh(m))).ToArray();
            string? candidate = eligible.Select(m => (m.Info.Host, m.Info.Priority))
                .Concat(_readiness.CanObserve ? [(_bus.LocalHost, _cfg.Priority)] : [])
                .OrderBy(p => p.Item2).ThenBy(p => p.Item1, StringComparer.Ordinal).Select(p => p.Item1).FirstOrDefault();
            bool next = false;
            if (!sleep && candidate == _bus.LocalHost)
            {
                _candidateSince ??= now;
                next = _watcher || now - _candidateSince >= _cfg.TakeoverDelayMs;
            }
            else
            {
                _candidateSince = null;
                // Keep existing local observation until a prepared replacement actually supplies data.
                next = !sleep && _watcher && _readiness.CanObserve && candidate is { } c
                    && (!_members.TryGetValue(c, out var replacement) || replacement.Info.Role != PeerRole.Watcher
                        || !Fresh(replacement) || !ReplacementObserves(replacement)
                        || LocalEpisodes().Any(a => !replacement.Alerts.Any(remote => remote.EpisodeId == a.EpisodeId
                            || remote.SupersededEpisodeIds.Contains(a.EpisodeId))));
            }
            changed = next != _watcher;
            if (changed) _roleChangeReason = next ? TakeoverReason() : YieldReason(sleep, candidate);
            _watcher = next;
            _watcherHost = next ? _bus.LocalHost : eligible.Where(m => m.Info.Role == PeerRole.Watcher)
                .OrderBy(m => m.Info.Priority).ThenBy(m => m.Info.Host, StringComparer.Ordinal).Select(m => m.Info.Host).FirstOrDefault();
            foreach (var id in _closed.Where(p => now - p.Value.Received > 2 * 60 * 60 * 1000L).Select(p => p.Key).ToArray()) _closed.Remove(id);
            foreach (var id in _acks.Where(p => now - p.Value.Received > 2 * 60 * 60 * 1000L).Select(p => p.Key).ToArray()) _acks.Remove(id);
            foreach (var host in _members.Where(p => now - p.Value.HeartbeatAt > 2 * 60 * 60 * 1000L).Select(p => p.Key).ToArray())
            {
                _members.Remove(host);
                ForgetFullDowntimeRequestsTriggeredBy(host);
            }
            hb = new HeartbeatMessage { Sender = _bus.LocalHost, Version = _version, Group = Identity,
                Priority = _cfg.Priority, Role = next ? PeerRole.Watcher : PeerRole.Ui, BelievedWatcher = _watcherHost,
                InstructionsHash = _instructions?.Hash, InstructionsEditedAt = _instructions?.LatestEditedAt,
                SharedConfigHash = _sharedConfig?.Hash, SharedConfigEditedAt = _sharedConfig?.EditedAt,
                Readiness = _readiness, MachinesTotal = _machines.Count,
                MachinesConnected = _machines.Count(m => m.State == MachineConnectionState.Connected),
                RecentAcks = _acks.Values.Where(p => now - p.Received < 60000).OrderByDescending(p => p.Received)
                    .Take(128).Select(p => p.Ack).ToArray() };
            state = next ? new StateMessage { Sender = _bus.LocalHost, Version = _version, Group = Identity,
                // Receivers accept at most 128 alerts; unacknowledged and newest first so a UDP trim drops the least urgent.
                Alerts = LocalEpisodes().OrderByDescending(a => a.Status is AlertStatus.Active or AlertStatus.Escalated)
                    .ThenByDescending(a => a.StartedAt).Take(128).Select(a => a with { CaptureFile = null }).ToArray(), Machines = _machines,
                ClosedEpisodes = _closed.Values.OrderByDescending(p => p.Received).Take(256).Select(p => p.Closure).ToArray() } : null;
        }
        if (changed) RoleChanged?.Invoke(IsWatcher);
        if (sleep) SleepDetected?.Invoke();
        await Send(hb, ct).ConfigureAwait(false);
        if (state is not null) await Send(state, ct).ConfigureAwait(false);
        Publish(); PeersChanged?.Invoke();
    }

    public string? LastRoleChangeReason { get { lock (_gate) return _roleChangeReason; } }

    private string TakeoverReason()
    {
        var incompatible = _members.Values.Where(m => Alive(m) && !GroupPeerStatus.Cooperates(m.Info))
            .OrderBy(m => m.Info.Host, StringComparer.Ordinal)
            .Select(m => $"{m.Info.Host} — {GroupPeerStatus.IncompatibilityReason(m.Info, _version)}").ToArray();
        return incompatible.Length == 0
            ? "brak zgodnego obserwatora z pierwszeństwem przed tym komputerem"
            : "brak zgodnego obserwatora z pierwszeństwem przed tym komputerem; widoczne, ale niezgodne (ich obserwacji "
              + "ten komputer nie uznaje): " + string.Join(", ", incompatible);
    }

    private string YieldReason(bool sleep, string? candidate) =>
        sleep ? "przerwa w pracy programu (uśpienie albo ręczne ustąpienie) — ponowny wybór obserwatora"
        : !_readiness.CanObserve ? "ten komputer nie jest gotowy do obserwacji maszyn"
        : candidate is { } host ? $"ustąpienie komputerowi {host}, który ma pierwszeństwo i już obserwuje"
        : "brak gotowego obserwatora";

    public void SetLocalState(IReadOnlyList<Alert> alerts, IReadOnlyList<MachineStatusSnapshot> machines,
        IReadOnlyList<AlertKey>? recentlyClosed = null)
    {
        IReadOnlyList<Alert> reconciled;
        lock (_gate)
        {
            _local = alerts.ToArray(); _machines = machines.ToArray();
            foreach (var a in alerts) Merge(a);
            reconciled = LocalEpisodes();
        }
        EpisodeStateReceived?.Invoke(reconciled, []);
        Publish();
    }
    private IReadOnlyList<Alert> LocalEpisodes() => _known.Values.Where(a => _local.Any(local =>
        local.EpisodeId == a.EpisodeId || a.SupersededEpisodeIds.Contains(local.EpisodeId))).ToArray();
    public void RecordClosure(Alert alert)
    {
        lock (_gate) Close(new(alert.EpisodeId, alert.MachineId, alert.Kind, alert.ClosedAt ?? _clock.UtcNow));
        Publish();
    }

    /// <summary>Only recently received, compatible observations establish continuity at takeover.</summary>
    public IReadOnlyList<Alert> TakeoverEpisodes()
    {
        lock (_gate)
        {
            long maximum = _cfg.PeerTimeoutMs + (long)_cfg.TakeoverDelayMs + StateLimit;
            var ids = _members.Values.Where(m => m.Info.Compatibility == GroupCompatibility.Compatible
                && m.StateAt is { } at && _clock.MonotonicMs - at <= maximum)
                .SelectMany(m => m.Alerts).Select(a => a.EpisodeId).ToHashSet();
            return _merged.Where(a => ids.Contains(a.EpisodeId)).Select(a => a with { CaptureFile = null }).ToArray();
        }
    }

    public async Task SendAckAsync(AlertKey key, string user, CancellationToken ct)
    {
        AckRecord? ack;
        lock (_gate)
        {
            var a = _merged.FirstOrDefault(a => a.Key == key || a.EpisodeKeys.Contains(key));
            if (a is null) return;
            var at = a.ReconciledAt is { } boundary && boundary >= _clock.UtcNow ? boundary.AddTicks(1) : _clock.UtcNow;
            ack = new(a.Key, _bus.LocalHost, user, at) { EpisodeId = a.EpisodeId };
            RememberAck(ack);
        }
        AckReceived?.Invoke(ack); Publish();
        await Send(new AckMessage { Sender = _bus.LocalHost, Version = _version, Group = Identity,
            Key = ack.Key, EpisodeId = ack.EpisodeId, User = user, AckedAt = ack.At }, ct).ConfigureAwait(false);
    }

    public async Task<string?> CheckConnectionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { if (_probes.Count >= 4) throw new InvalidOperationException("Test połączenia już trwa."); _probes[id] = completion; }
        try
        {
            await Send(new GroupProbeMessage { Sender = _bus.LocalHost, Version = _version, Group = Identity, RequestId = id }, ct);
            try { return await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return null; }
        }
        finally { lock (_gate) _probes.Remove(id); }
    }

    private void Receive(PeerMessage message)
    {
        if (!_started || message.Sender == _bus.LocalHost || message.Group is null
            || message.Group.GroupId != Identity.GroupId || message.Group.KeyId != Identity.KeyId
            || !Guid.TryParse(message.InstanceId, out _) || string.IsNullOrWhiteSpace(message.Sender)
            || message.Sender.Length > 100 || message.Seq < 1) return;
        var compatibility = Identity.Compare(message.Group);
        var receivedAcks = new List<AckRecord>();
        IReadOnlyList<Alert>? receivedEpisodes = null;
        IReadOnlyList<EpisodeClosure> receivedClosures = [];
        GroupProbeMessage? reply = null;
        // Instrukcje stosujemy i wysyłamy POZA zamkiem: zapis pliku nie może wstrzymywać odbioru alertów.
        PeerMessage? instructionRequest = null;
        InstructionsSyncRequest? answerTo = null;
        List<InstructionEntry>? instructionsToApply = null;
        PeerMessage? sharedConfigToSend = null;
        SharedConfigDocument? sharedConfigToApply = null;
        // To samo dla wspólnego raportu postojów: zapis dziennika idzie poza zamkiem.
        PeerMessage? downtimeRequest = null;
        DowntimeSyncRequest? downtimeAnswerTo = null;
        List<DowntimeEpisode>? downtimeToApply = null;
        PeerMessage? observationRequest = null;
        ObservationSyncRequest? observationAnswerTo = null;
        List<ObservationInterval>? observationToApply = null;
        lock (_gate)
        {
            long now = _clock.MonotonicMs;
            if (!_members.TryGetValue(message.Sender, out var member))
            {
                if (message is not HeartbeatMessage || _members.Count >= 64) return;
                member = new Member(); _members.Add(message.Sender, member);
            }
            foreach (var retired in member.Retired.Where(p => now - p.Value > Math.Max(600000L, 2L * _cfg.ClockSkewToleranceSec * 1000)).Select(p => p.Key).ToArray()) member.Retired.Remove(retired);
            if (member.Retired.ContainsKey(message.InstanceId)) return;
            if (member.Instance != message.InstanceId)
            {
                if (message is not HeartbeatMessage) return;
                if (member.Instance is { } old) member.Retired[old] = now;
                member.Instance = message.InstanceId; member.Sequences.Clear(); member.StateAt = null;
                member.Alerts = []; member.Machines = [];
                // Komputer właśnie wystartował:
                // pytamy go o postoje od razu, nie po upływie odstępu z poprzedniego uruchomienia.
                member.DowntimeAskedAt = null; member.DowntimeAnsweredAt = null; member.DowntimeRepliedAt = null;
                member.DowntimeAwaitingSince = now;
                member.ResetObservationSync();
            }
            // Porcje obserwacji mogą przyjść w innej kolejności. Ich osobne, ograniczone okno
            // aktualizujemy dopiero po sprawdzeniu adresata i rozmiaru w obsłudze odpowiedzi.
            // Stan/ACK i pozostałe komunikaty nadal wymagają ściśle rosnącego Seq per typ.
            if (message is not ObservationSyncReply)
            {
                var type = message.GetType();
                if (member.Sequences.TryGetValue(type, out long sequence) && message.Seq <= sequence) return;
                member.Sequences[type] = message.Seq;
            }
            if (message is HeartbeatMessage hb)
            {
                if (hb.Readiness is null || hb.RecentAcks is null || hb.RecentAcks.Count > 128) return;
                // Przywrócenie łączności może odsłonić starszą lukę mimo świeżego lokalnego wpisu.
                // Dopiero zaakceptowany heartbeat otwiera pełne okno; znaczniki wysyłania
                // zostają, aby reconnect nie obchodził limitów wysyłania.
                if (!Alive(member) && member.DowntimeAnsweredAt is not null)
                {
                    member.DowntimeAnsweredAt = null;
                    member.DowntimeAwaitingSince = now;
                }
                member.HeartbeatAt = now;
                member.Info = new PeerInfo(hb.Sender, hb.Priority, hb.Role, hb.Version, _clock.UtcNow, hb.Seq,
                    hb.BelievedWatcher, hb.MachinesConnected, hb.MachinesTotal, hb.InstanceId)
                    { Readiness = hb.Readiness, Compatibility = compatibility, InstructionsHash = hb.InstructionsHash,
                      SharedConfigHash = hb.SharedConfigHash, DowntimeSyncedAt = member.DowntimeAnsweredAtUtc };
                if (compatibility == GroupCompatibility.Compatible)
                {
                    foreach (var a in hb.RecentAcks) if (RememberAck(a)) receivedAcks.Add(a);
                    member.InstructionsHash = hb.InstructionsHash;
                    member.InstructionsEditedAt = hb.InstructionsEditedAt;
                    instructionRequest = InstructionRequestFor(member, now);
                    member.SharedConfigHash = hb.SharedConfigHash;
                    member.SharedConfigEditedAt = hb.SharedConfigEditedAt;
                    sharedConfigToSend = SharedConfigToSendFor(member, now);
                    downtimeRequest = DowntimeRequestFor(member, hb.Sender, now);
                    observationRequest = ObservationRequestFor(member, hb.Sender, now);
                }
            }
            else if (compatibility == GroupCompatibility.Compatible && Alive(member)
                     && member.Info.Compatibility == GroupCompatibility.Compatible)
            {
                switch (message)
                {
                    case StateMessage state when member.Info.Role == PeerRole.Watcher && member.Info.Readiness?.CanObserve == true:
                        if (state.Alerts is null || state.Machines is null || state.ClosedEpisodes is null
                            || state.Alerts.Count > 128 || state.Machines.Count > 128 || state.ClosedEpisodes.Count > 256
                            || state.Alerts.Any(a => !Valid(a)) || !Consistent(state.Alerts)) return;
                        member.StateAt = now; member.Alerts = state.Alerts; member.Machines = state.Machines;
                        // Closures arrive before Merge, so an as-yet unknown UUID must also be
                        // checked against identities carried by this packet (including predecessors).
                        foreach (var closure in state.ClosedEpisodes)
                            if (ClosureMatches(closure, state.Alerts)) Close(closure);
                        foreach (var a in state.Alerts) Merge(a with { CaptureFile = null });
                        // Deliver the reconciled transitions as well as tombstones to the local
                        // observer. Missing entries in STATE never imply that an alarm was repaired.
                        var ids = state.Alerts.Select(a => a.EpisodeId).ToHashSet();
                        receivedEpisodes = _known.Values.Where(a => ids.Contains(a.EpisodeId)
                            || a.SupersededEpisodeIds.Any(ids.Contains)).ToArray();
                        receivedClosures = state.ClosedEpisodes.Where(c => c is not null).Select(c => c.EpisodeId)
                            .Concat(ids).Distinct().Where(id => _closed.ContainsKey(id))
                            .Select(id => _closed[id].Closure).ToArray();
                        break;
                    case AckMessage ack when ack.EpisodeId is { } episode:
                        var record = new AckRecord(ack.Key, ack.Sender, ack.User, ack.AckedAt) { EpisodeId = episode };
                        if (RememberAck(record)) receivedAcks.Add(record);
                        break;
                    case InstructionChangeMessage change when _instructions is not null:
                        if (change.Entry is null || !change.Entry.IsValid()) return;
                        instructionsToApply = [change.Entry];
                        break;
                    case InstructionsSyncRequest request when _instructions is not null && request.RequestId != Guid.Empty:
                        // Odpowiadamy najwyżej raz na kilka sekund dla jednego komputera: powtórzona prośba
                        // (zgubiony pakiet, restart partnera) nie może zamienić się w lawinę pakietów.
                        if (member.InstructionsRepliedAt is { } repliedAt && now - repliedAt < InstructionReplyIntervalMs) return;
                        member.InstructionsRepliedAt = now;
                        answerTo = request;
                        break;
                    case InstructionsSyncReply answer when _instructions is not null
                        && answer.ReplyTo == _bus.LocalHost && answer.Entries is not null:
                        if (answer.Entries.Count > MaxInstructionsPerMessage) return;
                        instructionsToApply = answer.Entries.Where(e => e is not null && e.IsValid()).ToList();
                        break;
                    case SharedConfigMessage shared when _sharedConfig is not null:
                        if (shared.Document is null || !shared.Document.IsValid()) return;
                        sharedConfigToApply = shared.Document;
                        break;
                    case DowntimeSyncRequest downtimeAsk when _downtime is not null && downtimeAsk.RequestId != Guid.Empty:
                        // Jedna seria odpowiedzi na proszącego na 60 s:
                        // powtórzona prośba po zgubionym pakiecie nie może zamienić się w lawinę porcji,
                        // a seria i tak niesie do 50 porcji, więc reszta poczeka najwyżej jeden cykl.
                        if (member.DowntimeRepliedAt is { } downtimeRepliedAt
                            && now - downtimeRepliedAt < DowntimeReplyIntervalMs) return;
                        member.DowntimeRepliedAt = now;
                        downtimeAnswerTo = downtimeAsk;
                        break;
                    case DowntimeSyncReply downtimeAnswer when _downtime is not null
                        && downtimeAnswer.ReplyTo == _bus.LocalHost && downtimeAnswer.Episodes is not null:
                        if (downtimeAnswer.Episodes.Count > MaxDowntimeEpisodesPerMessage) return;
                        downtimeToApply = downtimeAnswer.Episodes.Where(e => e is not null && e.IsValid()).ToList();
                        if (member.DowntimeAnsweredAt is not null || EndsFullDowntimeWindow(member, downtimeAnswer.RequestId, now))
                        {
                            member.DowntimeAnsweredAt = now;
                            member.DowntimeAnsweredAtUtc = _clock.UtcNow;
                            member.Info = member.Info with { DowntimeSyncedAt = member.DowntimeAnsweredAtUtc };
                        }
                        // Dalszy ciąg bierzemy od razu, nie czekając na odstęp: partner sam powiedział,
                        // że ma jeszcze coś po tym znaczniku.
                        if (downtimeAnswer.More && downtimeAnswer.Cursor is { } cursor)
                        {
                            member.DowntimeAskedAt = now;
                            downtimeRequest = new DowntimeSyncRequest { Sender = _bus.LocalHost, Version = _version,
                                Group = Identity, RequestId = Guid.NewGuid(), Since = cursor };
                        }
                        break;
                    case ObservationSyncRequest observationAsk when _observation is not null && observationAsk.RequestId != Guid.Empty
                        && observationAsk.To == _bus.LocalHost:
                        // Jak przy postojach: jedna seria odpowiedzi na proszącego na 60 s.
                        if (member.ObservationRepliedAt is { } observationRepliedAt
                            && now - observationRepliedAt < ObservationReplyIntervalMs) return;
                        member.ObservationRepliedAt = now;
                        observationAnswerTo = observationAsk;
                        break;
                    case ObservationSyncReply observationAnswer when _observation is not null
                        && observationAnswer.ReplyTo == _bus.LocalHost && observationAnswer.Intervals is not null:
                        if (observationAnswer.Intervals.Count > MaxObservationIntervalsPerMessage
                            || !member.RememberObservationReply(observationAnswer.Seq)) return;
                        // Koniec przedziału po „teraz” (z tolerancją zegarów) to cudzy zły zegar — taki wpis
                        // wyprzedzałby obserwację i nie ma prawa trafić do dziennika.
                        var notAfter = _clock.UtcNow + TimeSpan.FromSeconds(Math.Max(0, _cfg.ClockSkewToleranceSec));
                        observationToApply = observationAnswer.Intervals.Where(i => i is not null && i.IsValid(notAfter)).ToList();
                        member.ObservationAnsweredAt = now;
                        // Przedziały przyjmujemy zawsze (suma jest idempotentna), ale postęp serii — tylko z odpowiedzi
                        // na NASZĄ ostatnią prośbę: spóźniona porcja starej serii nie może ogłosić kompletu.
                        // Najwyższy Seq tej prośby jest najnowszą porcją wysłaną przez partnera.
                        // Starsze More dostarcza dane, ale nie cofa kursora ani nie otwiera zamkniętej serii.
                        if (observationAnswer.RequestId != member.ObservationRequestId
                            || observationAnswer.Seq <= member.ObservationProgressSeq) break;
                        member.ObservationProgressSeq = observationAnswer.Seq;
                        if (observationAnswer.More && observationAnswer.Cursor is { } observationCursor)
                            member.ObservationCursor = observationCursor;
                        else
                        {
                            member.ObservationCursor = null;
                            member.ObservationSyncedUpTo = member.ObservationSeriesStartedUtc ?? member.ObservationSyncedUpTo;
                        }
                        break;
                    case GroupProbeMessage probe when probe.RequestId != Guid.Empty:
                        if (probe.ReplyTo == _bus.LocalHost && _probes.TryGetValue(probe.RequestId, out var pending)) pending.TrySetResult(probe.Sender);
                        else if (probe.ReplyTo is null) reply = new GroupProbeMessage { Sender = _bus.LocalHost,
                            Version = _version, Group = Identity, RequestId = probe.RequestId, ReplyTo = probe.Sender };
                        break;
                }
            }
        }
        foreach (var ack in receivedAcks) AckReceived?.Invoke(ack);
        if (receivedEpisodes is not null) EpisodeStateReceived?.Invoke(receivedEpisodes, receivedClosures);
        if (reply is not null) _ = Send(reply, CancellationToken.None);
        if (instructionsToApply is { Count: > 0 } entries && Instructions is { } sync)
        {
            // Nieudany zapis pliku instrukcji (zajęty przez edytor, brak prawa) nie może przerwać odbioru
            // alertów. Zbiory i tak się nie zgadzają, więc uzgadnianie powtórzy się przy kolejnym odcisku.
            try { if (sync.Apply(entries)) InstructionsChanged?.Invoke(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        if (sharedConfigToApply is { } document && SharedConfig is { } configSync)
        {
            // Jak przy instrukcjach: nieudany zapis pliku warstwy (zajęty przez Panel, brak prawa) nie może
            // przerwać odbioru alertów. Odciski i tak się nie zgadzają, więc uzgadnianie się powtórzy.
            try { if (configSync.Apply(document)) SharedConfigChanged?.Invoke(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        if (sharedConfigToSend is not null) _ = Send(sharedConfigToSend, CancellationToken.None);
        if (downtimeToApply is { Count: > 0 } episodes && Downtime is { } journal)
        {
            // Jak przy instrukcjach: nieudany zapis dziennika nie może przerwać odbioru alertów.
            // Luka zostaje, a kolejna prośba (po odstępie) spróbuje jeszcze raz.
            try { if (journal.Apply(episodes)) DowntimeChanged?.Invoke(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        if (observationToApply is { Count: > 0 } intervals && Observation is { } observationJournal)
        {
            // Jak przy postojach: nieudany zapis dziennika nie może przerwać odbioru alertów.
            try { if (observationJournal.Apply(intervals)) ObservationChanged?.Invoke(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        if (instructionRequest is not null) _ = Send(instructionRequest, CancellationToken.None);
        if (downtimeRequest is not null) _ = Send(downtimeRequest, CancellationToken.None);
        if (observationRequest is not null) _ = Send(observationRequest, CancellationToken.None);
        if (answerTo is not null && Instructions is { } source)
            foreach (var chunk in MessageCodec.Chunk(source.AllEntries(),
                         (entries, more) => new InstructionsSyncReply
                         {
                             Sender = _bus.LocalHost, Version = _version, Group = Identity,
                             RequestId = answerTo.RequestId, ReplyTo = answerTo.Sender,
                             Entries = entries.ToArray(), More = more,
                         }, MaxInstructionsPerMessage))
                _ = Send(chunk, CancellationToken.None);
        if (downtimeAnswerTo is not null && Downtime is { } downtimeSource)
            foreach (var chunk in DowntimeReplies(downtimeAnswerTo, downtimeSource))
                _ = Send(chunk, CancellationToken.None);
        if (observationAnswerTo is not null && Observation is { } observationSource)
            foreach (var chunk in ObservationReplies(observationAnswerTo, observationSource))
                _ = Send(chunk, CancellationToken.None);
        Publish(); PeersChanged?.Invoke();
    }

    /// <summary>Najwyżej tyle wpisów w jednej odpowiedzi — reszta idzie kolejną porcją.</summary>
    private const int MaxInstructionsPerMessage = 256;
    /// <summary>
    /// Jak często wolno powtórzyć prośbę do tego samego komputera. Pierwsza prośba bywa z natury
    /// stracona: partner dopiero nas poznaje (nie zna jeszcze naszego heartbeatu i odrzuca wszystko
    /// inne), więc odstęp musi być krótki — sama prośba to około 250 bajtów, a ruch i tak ustaje,
    /// gdy odciski się zrównają.
    /// </summary>
    private const long InstructionRequestIntervalMs = 10_000;
    private const long InstructionReplyIntervalMs = 5_000;

    /// <summary>
    /// Czy poprosić partnera o jego instrukcje. Pytamy tylko wtedy, gdy
    /// odciski się różnią, a partner ma zmianę nie starszą niż nasza — dzięki temu w parze komputerów
    /// pyta zawsze ten z uboższym zbiorem, a po scaleniu odciski się zrównują i ruch ustaje.
    /// </summary>
    private InstructionsSyncRequest? InstructionRequestFor(Member member, long now)
    {
        if (_instructions is not { } sync || member.InstructionsHash is not { } theirs) return null;
        if (string.Equals(theirs, sync.Hash, StringComparison.OrdinalIgnoreCase))
        { member.InstructionsAskedAt = null; member.InstructionsAskedAbout = null; return null; }
        // Nowy odcisk u partnera to nowa sytuacja: pytamy od razu, nie czekając na upływ odstępu.
        if (!string.Equals(member.InstructionsAskedAbout, theirs, StringComparison.OrdinalIgnoreCase))
        { member.InstructionsAskedAt = null; member.InstructionsAskedAbout = theirs; }
        if (sync.LatestEditedAt is { } mine
            && (member.InstructionsEditedAt is not { } partner || partner < mine)) return null;
        if (member.InstructionsAskedAt is { } asked && now - asked < InstructionRequestIntervalMs) return null;
        member.InstructionsAskedAt = now;
        return new InstructionsSyncRequest { Sender = _bus.LocalHost, Version = _version, Group = Identity,
            RequestId = Guid.NewGuid() };
    }

    private const long SharedConfigSendIntervalMs = 10_000;

    /// <summary>
    /// Czy wysłać partnerowi naszą wspólną konfigurację. Wysyłamy, gdy odciski się różnią, a nasz dokument
    /// NIE jest starszy od jego: wtedy w parze komputerów wysyła zawsze ten z nowszą zmianą (przy równym
    /// czasie wysyłają obaj, a rozstrzyga deterministyczne <see cref="SharedConfigDocument.Wins"/> u odbiorcy),
    /// po scaleniu odciski się zrównują i ruch ustaje. Partner bez żadnego dokumentu (np. włączony po raz
    /// pierwszy albo wyłączony w czasie zmiany) też dostaje nasz — inaczej nigdy by się nie dowiedział.
    /// </summary>
    private SharedConfigMessage? SharedConfigToSendFor(Member member, long now)
    {
        if (_sharedConfig is not { } sync || sync.Current is not { } mine) return null;
        string ours = mine.Hash;
        string theirs = member.SharedConfigHash ?? string.Empty;
        if (string.Equals(theirs, ours, StringComparison.OrdinalIgnoreCase))
        { member.SharedConfigSentAt = null; member.SharedConfigSentAbout = null; return null; }
        // Partner ma nowszą zmianę — nic nie robimy, on wyśle swoją.
        if (member.SharedConfigEditedAt is { } partner && partner > mine.EditedAt) return null;
        // Nowy odcisk u partnera to nowa sytuacja: wysyłamy od razu, nie czekając na upływ odstępu.
        if (!string.Equals(member.SharedConfigSentAbout, theirs, StringComparison.OrdinalIgnoreCase))
        { member.SharedConfigSentAt = null; member.SharedConfigSentAbout = theirs; }
        if (member.SharedConfigSentAt is { } sent && now - sent < SharedConfigSendIntervalMs) return null;
        member.SharedConfigSentAt = now;
        return new SharedConfigMessage { Sender = _bus.LocalHost, Version = _version, Group = Identity, Document = mine };
    }

    public Task BroadcastSharedConfigAsync(SharedConfigDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsValid()) throw new ArgumentException("Nieprawidłowa wspólna konfiguracja.", nameof(document));
        return Send(new SharedConfigMessage { Sender = _bus.LocalHost, Version = _version,
            Group = Identity, Document = document }, ct);
    }

    /// <summary>Najwyżej tyle wpisów postoju w jednej porcji odpowiedzi (walidacja odbiorcy).</summary>
    private const int MaxDowntimeEpisodesPerMessage = 256;

    /// <summary>Najwyżej tyle porcji w jednej serii odpowiedzi.</summary>
    private const int MaxDowntimeChunksPerSeries = 50;

    /// <summary>Jedna seria odpowiedzi na proszącego na tyle milisekund.</summary>
    private const long DowntimeReplyIntervalMs = 60_000;

    private const long DowntimeFirstRequestIntervalMs = 10_000;

    /// <summary>
    /// Czy poprosić partnera o brakujące postoje. Pytamy każdego partnera:
    /// inaczej niż przy instrukcjach nie ma tu odcisku, po którym dałoby się poznać, że zbiory są równe,
    /// a epizod sprzed naszego włączenia widział tylko ówczesny obserwator — i nie wiadomo z góry który
    /// komputer nim był. Ruch jest za to znikomy: po pierwszej odpowiedzi na prośbę o pełne okno pytamy co kilka minut, a okno
    /// <see cref="DowntimeSince"/> ogranicza odpowiedź do świeżej końcówki dziennika. <paramref name="host"/> — partner, którego
    /// heartbeat wywołał prośbę (tylko do sprzątania pamięci próśb po jego usunięciu z tabeli).
    /// </summary>
    private DowntimeSyncRequest? DowntimeRequestFor(Member member, string host, long now)
    {
        if (_downtime is not { } sync) return null;
        long interval = member.DowntimeAnsweredAt is null
            ? DowntimeFirstRequestIntervalMs
            : Math.Max(DowntimeFirstRequestIntervalMs, _cfg.DowntimeSyncIntervalMs);
        if (member.DowntimeAskedAt is { } asked && now - asked < interval) return null;
        member.DowntimeAskedAt = now;
        var request = new DowntimeSyncRequest { Sender = _bus.LocalHost, Version = _version, Group = Identity,
            RequestId = Guid.NewGuid(), Since = DowntimeSince(sync, member.DowntimeAnsweredAt is null, out bool fullWindow) };
        if (fullWindow) RememberFullDowntimeRequest(request.RequestId, host, now);
        return request;
    }

    /// <summary>
    /// Do pierwszej odpowiedzi partnera na prośbę o pełne okno (po starcie, także po odzyskaniu łączności) pytamy o całe
    /// skonfigurowane okno: świeży lokalny wpis nie dowodzi braku starszych luk. Potem doba przed naszą najpóźniejszą obserwacją,
    /// nie dalej niż <c>cluster.downtimeSyncWindowDays</c>. Pusty dziennik nadal pyta o całe okno; sama odpowiedź nie dowodzi
    /// kompletu. <paramref name="fullWindow"/> mówi, że prośba obejmuje całe okno (także ogon przycięty do okna) — odpowiedź na
    /// nią może zakończyć pełne okno partnera (<see cref="EndsFullDowntimeWindow"/>).
    /// </summary>
    private DateTimeOffset DowntimeSince(IDowntimeSync sync, bool awaitingFirstAnswer, out bool fullWindow)
    {
        var window = _clock.UtcNow - TimeSpan.FromDays(Math.Max(1, _cfg.DowntimeSyncWindowDays));
        fullWindow = true;
        if (awaitingFirstAnswer || sync.LatestObservedAt is not { } latest) return window;
        var since = latest - TimeSpan.FromDays(1);
        if (since <= window) return window;
        fullWindow = false;
        return since;
    }

    internal const int MaxRememberedFullDowntimeRequests = 256;

    /// <summary>
    /// Jak długo pamiętamy prośbę o pełne okno: odpowiedź przyjęta przez transport może do nas dotrzeć najpóźniej po dwóch
    /// tolerancjach czasu pakietu (prośba i odpowiedź mają każda <c>|SentAt − teraz| ≤ cluster.clockSkewToleranceSec</c>),
    /// ale nie krócej niż 10 min — ta sama granica, co dla wycofanych instancji partnera.
    /// </summary>
    private long FullDowntimeRequestLifetimeMs => Math.Max(600_000L, 2L * _cfg.ClockSkewToleranceSec * 1000);

    /// <summary>
    /// Odpowiedź bez <c>RequestId</c> (<see cref="Guid.Empty"/>) kończy pełne okno dopiero po tylu milisekundach oczekiwania na nie
    /// (dwa limity serii odpowiadającego, <see cref="DowntimeReplyIntervalMs"/>). Żadna wersja programu takiej
    /// nie wysyła — odpowiadający zawsze odsyła <c>RequestId</c> prośby. Gdyby jednak przyszła, nie da się jej przypisać do prośby:
    /// wpisy z niej scalamy, prośby o pełne okno idą dalej co 10 s, a po tym czasie kolejna taka odpowiedź kończy pełne okno i wracamy
    /// do zwykłego ogona. Bez tej granicy partner, który nie odsyła identyfikatora, dostawałby prośbę o pełne okno co 10 s bez końca.
    /// </summary>
    private const long UncorrelatedDowntimeReplyGraceMs = 2 * DowntimeReplyIntervalMs;

    /// <summary>Liczba zapamiętanych próśb o pełne okno (diagnostyka testów ograniczenia pamięci).</summary>
    internal int RememberedFullDowntimeRequests { get { lock (_gate) return _fullDowntimeRequests.Count; } }

    private void RememberFullDowntimeRequest(Guid requestId, string host, long now)
    {
        ForgetExpiredFullDowntimeRequests(now);
        if (_fullDowntimeRequests.Count >= MaxRememberedFullDowntimeRequests)
            _fullDowntimeRequests.Remove(_fullDowntimeRequests.MinBy(p => p.Value.SentAt).Key);
        _fullDowntimeRequests[requestId] = (now, host);
    }

    private void ForgetExpiredFullDowntimeRequests(long now)
    {
        long lifetime = FullDowntimeRequestLifetimeMs;
        foreach (var id in _fullDowntimeRequests.Where(p => now - p.Value.SentAt > lifetime).Select(p => p.Key).ToArray())
            _fullDowntimeRequests.Remove(id);
    }

    private void ForgetFullDowntimeRequestsTriggeredBy(string host)
    {
        foreach (var id in _fullDowntimeRequests.Where(p => p.Value.TriggeredBy == host).Select(p => p.Key).ToArray())
            _fullDowntimeRequests.Remove(id);
    }

    /// <summary>
    /// Czy odpowiedź partnera, który czeka na pełne okno, je kończy. Tak — wyłącznie odpowiedź na NASZĄ prośbę o pełne okno, i to
    /// dowolną: prośba nie ma adresata, więc prośba wywołana heartbeatem innego komputera też niesie całe okno. Odpowiedź na prośbę
    /// ogona albo dalszego ciągu (także spóźniona, sprzed przerwy) i odpowiedź na prośbę, której już nie pamiętamy, nie kończą go —
    /// następna prośba o pełne okno pójdzie po 10 s. Odpowiedź bez identyfikatora — patrz <see cref="UncorrelatedDowntimeReplyGraceMs"/>.
    /// </summary>
    private bool EndsFullDowntimeWindow(Member member, Guid requestId, long now)
    {
        if (requestId == Guid.Empty) return now - member.DowntimeAwaitingSince >= UncorrelatedDowntimeReplyGraceMs;
        ForgetExpiredFullDowntimeRequests(now);
        return _fullDowntimeRequests.ContainsKey(requestId);
    }

    /// <summary>
    /// Porcje odpowiedzi na prośbę o postoje, uporządkowane rosnąco po <c>(LastObservedAt, EpisodeId)</c>.
    /// <see cref="DowntimeSyncReply.More"/> na ostatniej porcji znaczy „mam jeszcze coś po tym znaczniku” —
    /// proszący wraca wtedy z kolejną prośbą od <see cref="DowntimeSyncReply.Cursor"/>.
    /// </summary>
    private IReadOnlyList<DowntimeSyncReply> DowntimeReplies(DowntimeSyncRequest request, IDowntimeSync source)
    {
        int limit = MaxDowntimeEpisodesPerMessage * MaxDowntimeChunksPerSeries;
        IReadOnlyList<DowntimeEpisode> episodes;
        try { episodes = source.EpisodesSince(request.Since, limit); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        DowntimeSyncReply[] chunks;
        try
        {
            chunks = [.. MessageCodec.Chunk(episodes, (items, more) => new DowntimeSyncReply
            {
                Sender = _bus.LocalHost, Version = _version, Group = Identity,
                RequestId = request.RequestId, ReplyTo = request.Sender,
                Episodes = [.. items], More = more,
                Cursor = items.Count > 0 ? items[^1].LastObservedAt : null,
            }, MaxDowntimeEpisodesPerMessage).Take(MaxDowntimeChunksPerSeries)];
        }
        // Pojedynczy wpis ponad limit datagramu: dziennik jest wtedy uszkodzony albo obcy. Milczymy —
        // proszący powtórzy prośbę, a alerty mają iść dalej.
        catch (InvalidDataException) { return []; }
        // Ucięliśmy serię (za dużo wpisów albo za dużo porcji): ostatnia wysłana porcja musi o tym
        // powiedzieć, inaczej proszący uznałby, że to już całość i zostałby z luką do następnego cyklu.
        if (chunks.Length > 0 && (episodes.Count >= limit || chunks.Length == MaxDowntimeChunksPerSeries))
            chunks[^1] = chunks[^1] with { More = true };
        return chunks;
    }

    /// <summary>Najwyżej tyle przedziałów obserwacji w jednej porcji odpowiedzi (walidacja odbiorcy).</summary>
    private const int MaxObservationIntervalsPerMessage = 256;

    /// <summary>Najwyżej tyle porcji w jednej serii odpowiedzi o przedziały obserwacji (jak przy postojach).</summary>
    private const int MaxObservationChunksPerSeries = 50;

    // Ponad pięć pełnych serii po 50 porcji, najwyżej 256 numerów na partnera (partnerów <= 64).
    // Liczymy przyjęte porcje, nie różnicę Seq: licznik nadawcy jest wspólny dla typów i odbiorców.
    private const int MaxRememberedObservationReplies = 256;

    /// <summary>Jedna seria odpowiedzi o przedziały obserwacji na proszącego na tyle milisekund.</summary>
    private const long ObservationReplyIntervalMs = 60_000;

    /// <summary>
    /// Ile pierwszych próśb idzie w krótkim odstępie (<see cref="DowntimeFirstRequestIntervalMs"/>) do komputera,
    /// który jeszcze nie odpowiedział. Starsza wersja programu nie zna tego komunikatu i nigdy nie odpowie
    /// — bez limitu dostawałaby prośbę co 10 s i co minutę wpisywała do logu ostrzeżenie o odrzuconym pakiecie.
    /// Potem prośby idą co <c>cluster.downtimeSyncIntervalMs</c>.
    /// </summary>
    private const int ObservationQuickRequests = 6;

    /// <summary>
    /// Po porcji z <see cref="ObservationSyncReply.More"/> dalszy ciąg prosimy dopiero po upływie limitu serii
    /// u partnera (<see cref="ObservationReplyIntervalMs"/> plus zapas) — wcześniejsza prośba i tak zostałaby pominięta.
    /// </summary>
    private const long ObservationContinuationDelayMs = 65_000;

    /// <summary>
    /// Zapas wstecz od ostatniej pełnej serii przy kolejnych prośbach: otwarte przedziały partnera rosną, a zgubiona
    /// porcja UDP z ostatniej doby wraca przy następnej prośbie (jak doba w <see cref="DowntimeSince"/>).
    /// </summary>
    private static readonly TimeSpan ObservationResyncMargin = TimeSpan.FromDays(1);

    private ObservationSyncRequest? ObservationRequestFor(Member member, string host, long now)
    {
        if (_observation is null) return null;
        long interval = member.ObservationCursor is not null ? ObservationContinuationDelayMs
            : member.ObservationAnsweredAt is null && member.ObservationUnanswered < ObservationQuickRequests
                ? DowntimeFirstRequestIntervalMs
                : Math.Max(DowntimeFirstRequestIntervalMs, _cfg.DowntimeSyncIntervalMs);
        if (member.ObservationAskedAt is { } asked && now - asked < interval) return null;
        member.ObservationAskedAt = now;
        if (member.ObservationAnsweredAt is null) member.ObservationUnanswered++;
        var window = _clock.UtcNow - TimeSpan.FromDays(Math.Max(1, _cfg.DowntimeSyncWindowDays));
        DateTimeOffset since;
        if (member.ObservationCursor is { } cursor) since = cursor;
        else
        {
            // Nowa seria: jej początek staje się „zsynchronizowane do”, gdy przyjdzie ostatnia porcja bez More.
            member.ObservationSeriesStartedUtc = _clock.UtcNow;
            since = member.ObservationSyncedUpTo is { } synced ? synced - ObservationResyncMargin : window;
        }
        member.ObservationRequestId = Guid.NewGuid();
        member.ObservationProgressSeq = 0;
        return new ObservationSyncRequest { Sender = _bus.LocalHost, Version = _version, Group = Identity,
            RequestId = member.ObservationRequestId, To = host, Since = since < window ? window : since };
    }

    /// <summary>
    /// Porcje odpowiedzi na prośbę o przedziały obserwacji — wzorzec <see cref="DowntimeReplies"/>. Rozmiar porcji
    /// liczy <see cref="MessageCodec.Chunk"/> z samej listy przedziałów (limit datagramu 60 000 B).
    /// </summary>
    private IReadOnlyList<ObservationSyncReply> ObservationReplies(ObservationSyncRequest request, IObservationSync source)
    {
        int limit = MaxObservationIntervalsPerMessage * MaxObservationChunksPerSeries;
        IReadOnlyList<ObservationInterval> intervals;
        try { intervals = source.IntervalsSince(request.Since, limit); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        ObservationSyncReply[] chunks;
        try
        {
            chunks = [.. MessageCodec.Chunk(intervals, (items, more) => new ObservationSyncReply
            {
                Sender = _bus.LocalHost, Version = _version, Group = Identity,
                RequestId = request.RequestId, ReplyTo = request.Sender,
                Intervals = [.. items], More = more,
                Cursor = items.Count > 0 ? items[^1].End : null,
            }, MaxObservationIntervalsPerMessage).Take(MaxObservationChunksPerSeries)];
        }
        // Pojedynczy wpis ponad limit datagramu — dziennik uszkodzony albo obcy. Milczymy, jak przy postojach.
        catch (InvalidDataException) { return []; }
        // Ucięta seria: ostatnia wysłana porcja musi o tym powiedzieć (proszący wróci od jej znacznika).
        if (chunks.Length > 0 && (intervals.Count >= limit || chunks.Length == MaxObservationChunksPerSeries))
            chunks[^1] = chunks[^1] with { More = true };
        return chunks;
    }

    /// <summary>Rozgłasza zmianę instrukcji zapisaną w panelu tego komputera.</summary>
    public Task BroadcastInstructionAsync(InstructionEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsValid()) throw new ArgumentException("Nieprawidłowy wpis instrukcji.", nameof(entry));
        return Send(new InstructionChangeMessage { Sender = _bus.LocalHost, Version = _version,
            Group = Identity, Entry = entry }, ct);
    }

    private bool RememberAck(AckRecord ack)
    {
        if (ack is null || ack.EpisodeId is not { } id || id == Guid.Empty || _closed.ContainsKey(id)
            || ack.Key.MachineId is null || ack.Host is null || ack.User is null
            || (_clock.UtcNow - ack.At).Duration().TotalSeconds > _cfg.ClockSkewToleranceSec
            || (_acks.TryGetValue(id, out var old) && old.Ack.At >= ack.At)) return false;
        if (_acks.Count >= 512 && !_acks.ContainsKey(id)) return false;
        _acks[id] = (ack, _clock.MonotonicMs);
        if (_known.TryGetValue(id, out var a)) _known[id] = ApplyAck(a);
        return true;
    }
    private Alert ApplyAck(Alert a)
    {
        if (_acks.TryGetValue(a.EpisodeId, out var entry) && entry.Ack.Key.MachineId == a.MachineId
            && (a.ReconciledAt is null || entry.Ack.At > a.ReconciledAt)
            && entry.Ack.At >= a.StartedAt && (a.EscalatedAt is null || entry.Ack.At > a.EscalatedAt)
            && (a.AcknowledgedAt is null || entry.Ack.At > a.AcknowledgedAt))
            return a with { Status = AlertStatus.Acknowledged, AcknowledgedAt = entry.Ack.At,
                AckedByHost = entry.Ack.Host, AckedByUser = entry.Ack.User };
        return a;
    }
    private void Merge(Alert a)
    {
        if (!Valid(a) || _closed.ContainsKey(a.EpisodeId)) return;
        if (_known.Values.Any(current => current.SupersededEpisodeIds.Contains(a.EpisodeId))) return;
        // A predecessor must belong to the same machine and kind; a claim over a foreign episode is ignored so that
        // the foreign episode stays updatable and is never closed under somebody else's identity.
        if (a.SupersededEpisodeIds.Any(id => _known.TryGetValue(id, out var known) && (known.MachineId != a.MachineId || known.Kind != a.Kind)
                || _closed.TryGetValue(id, out var closed) && (closed.Closure.MachineId != a.MachineId || closed.Closure.Kind != a.Kind))) return;
        if (a.Status == AlertStatus.Closed) { Close(new(a.EpisodeId, a.MachineId, a.Kind, a.ClosedAt ?? _clock.UtcNow)); return; }
        if (_known.TryGetValue(a.EpisodeId, out var old))
        {
            if (a.MachineId != old.MachineId || a.Kind != old.Kind) return;
            if (a.SupersededEpisodeIds.Concat(old.SupersededEpisodeIds).Distinct().Count() > 128) return;
            var latest = EpisodeTransition.IsNewer(a, old) ? a : old;
            var text = a.Priority > old.Priority || a.Priority == old.Priority && a.LastSeenAt >= old.LastSeenAt ? a : old;
            a = latest with { Key = text.Key, ShortText = text.ShortText, FullText = text.FullText, Priority = text.Priority,
                Category = text.Category, StartedAt = old.StartedAt,
                SupersededEpisodeIds = a.SupersededEpisodeIds.Concat(old.SupersededEpisodeIds).Distinct().ToArray(),
                ReconciledAt = new[] { a.ReconciledAt, old.ReconciledAt }.Max(),
                LastSeenAt = a.LastSeenAt > old.LastSeenAt ? a.LastSeenAt : old.LastSeenAt,
                EpisodeKeys = a.EpisodeKeys.Concat(old.EpisodeKeys).Append(a.Key).Append(old.Key).Distinct().Take(128).ToArray() };
        }
        else if (_known.Count >= 512) return;
        if (a.ReconciledAt is { } reconciled && a.Status == AlertStatus.Acknowledged
            && (a.AcknowledgedAt is null || a.AcknowledgedAt <= reconciled))
            a = a with { Status = AlertStatus.Active, AcknowledgedAt = null, AckedByHost = null, AckedByUser = null };
        a = ApplyAck(a);
        // Different UUIDs are ambiguous occurrences, not continuity inferred from OCR. Select a
        // stable identity, retain the retirement set and require a fresh ACK for the combined view.
        foreach (var other in _known.Values.Where(other => other.EpisodeId != a.EpisodeId
                     && other.MachineId == a.MachineId && other.Kind == a.Kind).ToArray())
        {
            if (a.SupersededEpisodeIds.Contains(other.EpisodeId)) continue;
            var winner = a.StartedAt > other.StartedAt || a.StartedAt == other.StartedAt
                && a.EpisodeId.CompareTo(other.EpisodeId) > 0 ? a : other;
            var loser = winner.EpisodeId == a.EpisodeId ? other : a;
            var retired = winner.SupersededEpisodeIds.Concat(loser.SupersededEpisodeIds)
                .Append(loser.EpisodeId).Distinct().ToArray();
            if (retired.Length > 128) return; // keep the ringing state rather than truncate identity evidence
            var boundary = new[] { _clock.UtcNow, EpisodeTransition.At(a), EpisodeTransition.At(other) }.Max();
            a = winner with { SupersededEpisodeIds = retired, ReconciledAt = boundary,
                Status = AlertStatus.Active, AcknowledgedAt = null, AckedByHost = null,
                AckedByUser = null, EscalatedAt = null };
            _acks.Remove(a.EpisodeId);
        }
        foreach (var retired in a.SupersededEpisodeIds)
            Close(new(retired, a.MachineId, a.Kind, a.ReconciledAt ?? _clock.UtcNow));
        _known[a.EpisodeId] = a;
    }
    private void Close(EpisodeClosure closure)
    {
        if (closure is null || closure.EpisodeId == Guid.Empty || string.IsNullOrWhiteSpace(closure.MachineId)
            || !Enum.IsDefined(closure.Kind)) return;
        if (!ClosureMatches(closure, _known.Values)) return;
        if (_known.TryGetValue(closure.EpisodeId, out var a) && (a.MachineId != closure.MachineId || a.Kind != closure.Kind)) return;
        if (_closed.ContainsKey(closure.EpisodeId)) return; // retransmission never renews the retention window
        if (_closed.Count >= 2048) return;
        _closed[closure.EpisodeId] = (closure, _clock.MonotonicMs);
        _known.Remove(closure.EpisodeId); _acks.Remove(closure.EpisodeId);
        if (a is not null)
            foreach (var retired in a.SupersededEpisodeIds)
                Close(new(retired, closure.MachineId, closure.Kind, closure.At));
    }

    private static bool ClosureMatches(EpisodeClosure? closure, IEnumerable<Alert> alerts) => closure is not null
        && !alerts.Any(a => (a.EpisodeId == closure.EpisodeId || a.SupersededEpisodeIds.Contains(closure.EpisodeId))
            && (a.MachineId != closure.MachineId || a.Kind != closure.Kind));
    private void Publish()
    {
        IReadOnlyList<Alert> snapshot;
        lock (_gate)
        {
            // Ambiguous partition episodes are presented once per machine/kind. An unacknowledged
            // occurrence wins over a different, acknowledged UUID; old ACK cannot silence new detection.
            snapshot = _known.Values.GroupBy(a => (a.MachineId, a.Kind)).Select(g => g
                .OrderByDescending(a => a.Status is AlertStatus.Active or AlertStatus.Escalated)
                .ThenByDescending(a => a.LastSeenAt).ThenBy(a => a.StartedAt).First())
                .OrderBy(a => a.MachineId, StringComparer.Ordinal).ThenBy(a => a.Kind).ToArray();
            _merged = snapshot;
        }
        AlertsChanged?.Invoke(snapshot);
    }
    /// <summary>The successor must actually read machines: at least one live connection, and every
    /// machine this node currently reads must already be read by it, so the hand-over leaves no gap.
    /// A successor that cannot reach some machine keeps this node observing; parallel VNC is allowed.</summary>
    private bool ReplacementObserves(Member replacement)
    {
        var remote = replacement.Machines.Where(m => m.State == MachineConnectionState.Connected)
            .Select(m => m.MachineId).ToHashSet(StringComparer.Ordinal);
        return remote.Count > 0 && _machines.Where(m => m.State == MachineConnectionState.Connected)
            .All(m => remote.Contains(m.MachineId));
    }

    private bool Alive(Member m) => _clock.MonotonicMs - m.HeartbeatAt < _cfg.PeerTimeoutMs;
    private bool Fresh(Member m) => Alive(m) && m.StateAt is { } at && _clock.MonotonicMs - at <= StateLimit;
    private static bool Valid(Alert a) => a is not null && a.EpisodeId != Guid.Empty && !string.IsNullOrWhiteSpace(a.MachineId)
        && a.Key.MachineId == a.MachineId && a.Key.Discriminator is not null && a.EpisodeKeys is not null
        && a.EpisodeKeys.Count <= 128 && a.ShortText is not null && a.Category is not null
        && a.SupersededEpisodeIds is not null && a.SupersededEpisodeIds.Count <= 128
        && a.SupersededEpisodeIds.All(id => id != Guid.Empty && id != a.EpisodeId)
        && a.SupersededEpisodeIds.Distinct().Count() == a.SupersededEpisodeIds.Count
        && Enum.IsDefined(a.Kind) && Enum.IsDefined(a.Status);
    /// <summary>A STATE packet is rejected as a whole when identities contradict each other: a repeated UUID,
    /// or a UUID that is both active and retired (this covers two alerts superseding one another).</summary>
    private static bool Consistent(IReadOnlyList<Alert> alerts)
    {
        var ids = new HashSet<Guid>();
        foreach (var a in alerts) if (!ids.Add(a.EpisodeId)) return false;
        return !alerts.Any(a => a.SupersededEpisodeIds.Any(ids.Contains));
    }
    private async Task Send(PeerMessage message, CancellationToken ct)
    {
        try { await _bus.SendAsync(message, ct).ConfigureAwait(false); lock (_gate) _transportError = null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { lock (_gate) _transportError = "Błąd transportu UDP — potwierdzenia mogą działać tylko lokalnie."; }
    }
    private sealed class Member
    {
        public PeerInfo Info = new("", 100, PeerRole.Ui, "", default, 0, null, 0, 0);
        public long HeartbeatAt;
        public long? StateAt;
        public string? Instance;
        public Dictionary<string, long> Retired = new(StringComparer.Ordinal);
        public Dictionary<Type, long> Sequences = new();
        public IReadOnlyList<Alert> Alerts = [];
        public IReadOnlyList<MachineStatusSnapshot> Machines = [];
        public string? InstructionsHash;
        public DateTimeOffset? InstructionsEditedAt;
        public long? InstructionsAskedAt;
        public string? InstructionsAskedAbout;
        public long? InstructionsRepliedAt;
        /// <summary>Odcisk wspólnej konfiguracji tego komputera z ostatniego heartbeatu.</summary>
        public string? SharedConfigHash;
        /// <summary>Czas jego ostatniej zmiany wspólnej konfiguracji — po nim decydujemy, kto wysyła.</summary>
        public DateTimeOffset? SharedConfigEditedAt;
        /// <summary>Kiedy ostatnio wysłaliśmy mu naszą wspólną konfigurację (zegar monotoniczny).</summary>
        public long? SharedConfigSentAt;
        /// <summary>O jakim JEGO odcisku była ostatnia wysyłka — nowy odcisk znosi odstęp.</summary>
        public string? SharedConfigSentAbout;
        /// <summary>Kiedy ostatnio poprosiliśmy ten komputer o brakujące postoje (zegar monotoniczny).</summary>
        public long? DowntimeAskedAt;
        /// <summary>
        /// Kiedy odpowiedział na naszą prośbę o pełne okno od ostatniego nawiązania łączności, a potem — kiedy ostatnio odpowiedział;
        /// dopóki puste, pytamy o pełne okno w krótkim odstępie.
        /// </summary>
        public long? DowntimeAnsweredAt;
        /// <summary>To samo, ale zegarem ściennym — do pokazania w Panelu administracyjnym.</summary>
        public DateTimeOffset? DowntimeAnsweredAtUtc;
        /// <summary>Od kiedy czekamy na jego odpowiedź na pełne okno (zegar monotoniczny; start, nowa instancja, odzyskanie łączności).</summary>
        public long DowntimeAwaitingSince;
        /// <summary>Kiedy ostatnio wysłaliśmy mu serię odpowiedzi (limit jednej serii na 60 s).</summary>
        public long? DowntimeRepliedAt;
        /// <summary>Kiedy ostatnio poprosiliśmy go o przedziały obserwacji (zegar monotoniczny).</summary>
        public long? ObservationAskedAt;
        /// <summary>Kiedy ostatnio przysłał porcję przedziałów; dopóki puste — pytamy krótko, najwyżej kilka razy.</summary>
        public long? ObservationAnsweredAt;
        /// <summary>Ile próśb poszło do niego bez żadnej odpowiedzi (starsza wersja nie odpowiada wcale).</summary>
        public int ObservationUnanswered;
        /// <summary>Identyfikator naszej ostatniej prośby do niego — tylko odpowiedź na nią przesuwa serię.</summary>
        public Guid ObservationRequestId;
        /// <summary>Najwyższy Seq, który przesunął postęp naszej ostatniej prośby.</summary>
        public long ObservationProgressSeq;
        private readonly SortedSet<long> _observationReplySequences = [];
        private long _observationReplyFloor;
        /// <summary>Znacznik dalszego ciągu z ostatniej porcji z More; <see langword="null"/> — seria domknięta.</summary>
        public DateTimeOffset? ObservationCursor;
        /// <summary>Zegar ścienny wysłania pierwszej prośby bieżącej serii.</summary>
        public DateTimeOffset? ObservationSeriesStartedUtc;
        /// <summary>Do kiedy mamy od niego komplet przedziałów (początek ostatniej domkniętej serii).</summary>
        public DateTimeOffset? ObservationSyncedUpTo;
        /// <summary>Kiedy ostatnio wysłaliśmy mu serię odpowiedzi o przedziały (limit jednej serii na 60 s).</summary>
        public long? ObservationRepliedAt;

        public bool RememberObservationReply(long sequence)
        {
            if (sequence <= _observationReplyFloor || !_observationReplySequences.Add(sequence)) return false;
            if (_observationReplySequences.Count > MaxRememberedObservationReplies)
            {
                // Zawsze wyrzucamy najmniejszy numer, również jeśli to właśnie przyjęta spóźniona porcja.
                // Granica tylko rośnie, więc wyrzucenie nie otwiera ponownie możliwości replay.
                _observationReplyFloor = _observationReplySequences.Min;
                _observationReplySequences.Remove(_observationReplyFloor);
            }
            return true;
        }

        /// <summary>Partner wystartował od nowa — pytamy go od razu i o całe okno.</summary>
        public void ResetObservationSync()
        {
            ObservationAskedAt = null; ObservationAnsweredAt = null; ObservationUnanswered = 0; ObservationRequestId = Guid.Empty;
            ObservationCursor = null; ObservationSeriesStartedUtc = null; ObservationSyncedUpTo = null;
            ObservationRepliedAt = null;
            ObservationProgressSeq = 0; _observationReplySequences.Clear(); _observationReplyFloor = 0;
        }
    }
}

public sealed record GroupRuntimeState(IReadOnlyList<Alert> Alerts, IReadOnlyList<EpisodeClosure> Closed,
    IReadOnlyList<AckRecord> Acks, bool WasWatcher);
