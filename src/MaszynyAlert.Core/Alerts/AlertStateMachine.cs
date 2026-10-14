using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Czysta logika cyklu życia alertów, sterowana zegarem <see cref="IClock"/> — działa u obserwatora (watchera).
/// Nowy epizod → Active → (Acknowledged) → ewentualnie Escalated (gdy błąd trwa po potwierdzeniu) → Closed
/// (gdy czerwień/brak łączności znika na dłużej niż skonfigurowany próg). Wątkowo bezpieczna (lock na całości);
/// zdarzenia <see cref="Changed"/> są emitowane POZA blokadą, po zebraniu pełnej listy z danego wywołania.
/// </summary>
public sealed class AlertStateMachine
{
    private bool _groupProtocol;

    public void UseGroupProtocol(bool enabled) { lock (_lock) _groupProtocol = enabled; }

    /// <summary>Apply v2 transitions to locally owned UUIDs or their explicit replacements. Network observations never
    /// advance local detection timers; absent episodes are not inferred to be closed.</summary>
    public void ReconcileGroupState(IReadOnlyList<Alert> episodes, IReadOnlyList<EpisodeClosure> closures)
    {
        var events = new List<AlertEvent>();
        lock (_lock)
        {
            if (!_groupProtocol) return;
            foreach (var source in episodes)
            {
                var target = _alerts.Values.FirstOrDefault(a => a.MachineId == source.MachineId && a.Kind == source.Kind
                    && (a.EpisodeId == source.EpisodeId || source.SupersededEpisodeIds.Contains(a.EpisodeId)));
                if (target is null) continue;
                bool replaced = target.EpisodeId != source.EpisodeId;
                bool newer = EpisodeTransition.IsNewer(source, target)
                    || source.ReconciledAt > (target.ReconciledAt ?? DateTimeOffset.MinValue);
                bool metadataChanged = source.SupersededEpisodeIds.Except(target.SupersededEpisodeIds).Any();
                if (!replaced && !newer && !metadataChanged) continue;
                var transition = replaced || newer ? source : target;
                // Rebind only an existing observation. Its timers, local key/description and last
                // actual read stay local; receiving a UUID does not manufacture a fresh frame.
                var updated = target with { EpisodeId = source.EpisodeId,
                    StartedAt = replaced ? source.StartedAt : target.StartedAt,
                    SupersededEpisodeIds = source.SupersededEpisodeIds.Concat(target.SupersededEpisodeIds)
                        .Where(id => id != source.EpisodeId).Distinct().ToArray(),
                    ReconciledAt = new[] { source.ReconciledAt, target.ReconciledAt }.Max(),
                    Status = transition.Status, AcknowledgedAt = transition.AcknowledgedAt,
                    AckedByHost = transition.AckedByHost, AckedByUser = transition.AckedByUser,
                    EscalatedAt = transition.EscalatedAt };
                _alerts[target.Key] = updated;
                if (updated.AcknowledgedAt is { } at)
                    _acknowledgedMs[target.Key] = _clock.MonotonicMs - (long)Math.Max(0, (_clock.UtcNow - at).TotalMilliseconds);
                else _acknowledgedMs.Remove(target.Key);
                events.Add(new(updated.Status switch { AlertStatus.Acknowledged => AlertEventKind.Acknowledged,
                    AlertStatus.Escalated => AlertEventKind.Escalated, _ => AlertEventKind.Updated }, updated));
            }
            foreach (var closure in closures)
            {
                var target = _alerts.Values.FirstOrDefault(a => a.EpisodeId == closure.EpisodeId
                    && a.MachineId == closure.MachineId && a.Kind == closure.Kind);
                if (target is null) continue;
                CloseUnclosedErrorLocked(target, closure.At, events);
                switch (target.Kind)
                {
                    case AlertKind.Error:
                        _redClearedSince.Remove(target.MachineId);
                        _episodeBrokenByOffline.Remove(target.MachineId);
                        break;
                    case AlertKind.MachineIdle: ForgetIdleCounters(target.MachineId); break;
                    case AlertKind.WrongScreen: _wrongScreenSince.Remove(target.MachineId); break;
                    case AlertKind.Offline: _offlineMachines.Remove(target.MachineId); break;
                }
            }
        }
        Publish(events);
    }

    /// <summary>Restore observation ownership before starting monitors. Imported timestamps are never
    /// advanced to pretend a fresh read; errors wait for actual screen observation before closing.</summary>
    public void RestoreObservation(IEnumerable<Alert> episodes, bool preserveAcknowledgements = true)
    {
        var events = new List<AlertEvent>();
        lock (_lock)
        {
            foreach (string machineId in _pendingFirstReads.Keys.ToArray()) FlushFirstReadLocked(machineId, events);
            _alerts.Clear(); _acks.Clear(); _episodeAliases.Clear(); _observedMs.Clear();
            _acknowledgedMs.Clear(); _offlineMachines.Clear(); _lastRedSeen.Clear();
            _redClearedSince.Clear(); _episodeBrokenByOffline.Clear(); _fullSince.Clear(); _zeroSince.Clear();
            _wrongScreenSince.Clear(); _unobservedMachines.Clear(); _observationResumedAt.Clear();
            _unobservedSince.Clear(); _unobservedSinceMs.Clear(); _rejoinable.Clear();
            foreach (var source in episodes.Where(a => a.Status != AlertStatus.Closed))
            {
                var alert = preserveAcknowledgements ? source : source with
                {
                    EpisodeId = Guid.NewGuid(), Status = AlertStatus.Active,
                    SupersededEpisodeIds = [], ReconciledAt = null,
                    AcknowledgedAt = null, AckedByHost = null, AckedByUser = null, EscalatedAt = null
                };
                _alerts[alert.Key] = alert;
                _observedMs[alert.Key] = _clock.MonotonicMs;
                if (alert.AcknowledgedAt is { } at)
                    _acknowledgedMs[alert.Key] = _clock.MonotonicMs - (long)Math.Max(0, (_clock.UtcNow - at).TotalMilliseconds);
                foreach (var key in alert.EpisodeKeys.Append(alert.Key)) _episodeAliases[key] = alert.Key;
                // Restored episodes wait for actual observation: an error for a frame, an idle alert
                // for a readable progress value. Neither is closed or re-raised on network data alone.
                if (alert.Kind is AlertKind.Error or AlertKind.WrongScreen or AlertKind.MachineIdle)
                    _unobservedMachines.Add(alert.MachineId);
                if (alert.Kind == AlertKind.MachineIdle)
                    IdleCounter(alert.Category)[alert.MachineId] = new(alert.StartedAt, _clock.MonotonicMs);
                if (alert.Kind == AlertKind.WrongScreen) _wrongScreenSince[alert.MachineId] = new(alert.StartedAt, _clock.MonotonicMs);
            }
        }
        Publish(events);
    }
    private const int MaxRecentHistory = 200;
    private const string RuleDiscriminatorPrefix = "rule:";
    private const string AlarmDiscriminatorPrefix = "alarm:";
    private const string EmptyTextDiscriminator = "text:empty";

    /// <summary>Treść alertu <see cref="AlertKind.MachineIdle"/> w odmianie „stoi na 100 %” — jednakowa dla wszystkich maszyn.</summary>
    public const string MachineIdleShortText = "Stoi bez pracy (100 %) – dołóż prace";

    public const string MachineIdleAtZeroShortText = "Stoi na 0 % – wciśnij Start";

    /// <summary>Kategoria alertu postoju „stoi na 100 % – dołóż prace”.</summary>
    public const string IdleCategory = "postoj";

    /// <summary>Kategoria alertu postoju „stoi na 0 % – wciśnij Start”: ten sam rodzaj i klucz, inny powód.</summary>
    public const string IdleAtZeroCategory = "postoj-0";

    /// <summary>Treść alertu <see cref="AlertKind.WrongScreen"/> — jednakowa dla wszystkich maszyn.</summary>
    public const string WrongScreenShortText = "Włącz okno podglądu stanu maszyny (z alertami i %) – HMI pokazuje inny ekran";

    private readonly Dictionary<AlertKey, long> _observedMs = new();
    private readonly Dictionary<AlertKey, long> _acknowledgedMs = new();

    private DateTimeOffset Observe(AlertKey key, DateTimeOffset utc)
    {
        _observedMs[key] = _clock.MonotonicMs;
        return utc;
    }

    private DateTimeOffset Observe(AlertKey key, ObservationTime observation)
    {
        _observedMs[key] = observation.Milliseconds;
        return observation.Utc;
    }

    private bool AcknowledgementDue(Alert alert, TimeSpan delay)
    {
        if (alert.Status != AlertStatus.Acknowledged || alert.AcknowledgedAt is null) return false;
        // Zapamiętany ACK zachowuje wiek z momentu odbioru, także po przejęciu obserwacji.
        if (!_acknowledgedMs.TryGetValue(alert.Key, out long at))
            _acknowledgedMs[alert.Key] = at = _acks.RecordedAtMs(alert.Key) ?? _clock.MonotonicMs;
        return _clock.MonotonicMs - at >= delay.TotalMilliseconds;
    }

    private readonly IClock _clock;
    private TimingConfig _timing;
    public void ConfigureTiming(TimingConfig timing) { lock (_lock) _timing = timing; }
    private readonly AckMemory _acks;
    // Wszystkie kody widziane w otwartym epizodzie, także przed zmianą opisu.
    private readonly Dictionary<AlertKey, AlertKey> _episodeAliases = new();
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>Aktualnie niezamknięte alerty, wg klucza.</summary>
    private readonly Dictionary<AlertKey, Alert> _alerts = new();

    /// <summary>Ostatnie zamknięte alerty, najnowsze pierwsze (maks. <see cref="MaxRecentHistory"/>).</summary>
    private readonly List<Alert> _recentHistory = new();

    /// <summary>Maszyny zgłoszone jako offline (<see cref="OnMachineOffline"/>) — czekają na przekroczenie progu.</summary>
    private readonly Dictionary<string, (MachineConfig Machine, ObservationTime OfflineSince)> _offlineMachines = new();

    /// <summary>
    /// Ostatni znany stan czerwieni per maszyna. Ustawiane wyłącznie przez <see cref="OnRedCleared"/> —
    /// samo zamknięcie epizodu liczone jest w <see cref="Tick"/> na podstawie <c>Alert.LastSeenAt</c>.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset?> _lastRedSeen = new();

    private readonly Dictionary<string, ObservationTime> _redClearedSince = new();

    private readonly Dictionary<string, OfflineGap> _episodeBrokenByOffline = new(StringComparer.Ordinal);

    /// <summary>Przerwa w łączności od ostatniego potwierdzenia czerwieni (ms zegara monotonicznego).</summary>
    private readonly record struct OfflineGap(long StartMs, long? EndMs);

    /// <summary>
    /// Od kiedy dana maszyna nieprzerwanie pokazuje pasek postępu na 100% (<see cref="OnProgressState"/>).
    /// Brak wpisu = pasek nie jest (albo nigdy nie był w tym epizodzie) na 100%. Zerowane natychmiast, gdy
    /// pasek spadnie poniżej 100% (maszyna ruszyła — epizod postoju się skończył) albo gdy maszyna przejdzie
    /// offline (<see cref="OnMachineOffline"/>) — utrata łączności zeruje licznik 2 minut, bo nie wiemy, co
    /// się działo z paskiem w tym czasie.
    /// </summary>
    private readonly Dictionary<string, ObservationTime> _fullSince = new();

    /// <summary>
    /// „Postęp stoi na 0 % nieprzerwanie od” — licznik bliźniaczy do <see cref="_fullSince"/>. Osobny, bo
    /// każdy z tych stanów ma własny próg (<c>TimingConfig.IdleAtZeroAfterMs</c> obok <c>IdleAlertAfterMs</c>)
    /// i własną treść alertu. Maszyna jest najwyżej w jednym z nich, więc wejście w jeden zeruje drugi.
    /// </summary>
    private readonly Dictionary<string, ObservationTime> _zeroSince = new();

    /// <summary>Licznik odpowiadający kategorii alertu postoju (<see cref="IdleAtZeroCategory"/> albo <see cref="IdleCategory"/>).</summary>
    private Dictionary<string, ObservationTime> IdleCounter(string? category) =>
        category == IdleAtZeroCategory ? _zeroSince : _fullSince;

    /// <summary>Zapomina OBA liczniki postoju maszyny (100 % i 0 %) — patrz <see cref="_zeroSince"/>.</summary>
    private void ForgetIdleCounters(string machineId)
    {
        _fullSince.Remove(machineId);
        _zeroSince.Remove(machineId);
    }

    /// <summary>
    /// Od kiedy HMI danej maszyny nieprzerwanie pokazuje INNY ekran niż główny (<see cref="OnMainScreenState"/>
    /// z <c>mainScreenVisible=false</c>). Brak wpisu = ekran główny widoczny (albo nigdy nie sprawdzano).
    /// Zerowane, gdy ekran główny wróci albo maszyna przejdzie offline (<see cref="OnMachineOffline"/> —
    /// restart maszyny zwykle przywraca ekran główny, a i tak nie widzimy wtedy obrazu).
    /// </summary>
    private readonly Dictionary<string, ObservationTime> _wrongScreenSince = new();

    // Brak widoczności HMI nie potwierdza końca błędu. Nie zmieniamy LastSeenAt,
    // bo oznacza ono rzeczywisty odczyt, a po powrocie wymagamy pełnego czasu bez czerwieni.
    private readonly HashSet<string> _unobservedMachines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _observationResumedAt = new();

    private readonly Dictionary<string, DateTimeOffset> _unobservedSince = new(StringComparer.Ordinal);

    /// <summary>
    /// Ten sam moment co <see cref="_unobservedSince"/>, ale na zegarze monotonicznym — rozstrzyga w <see cref="Tick"/>,
    /// czy OSTATNI rzeczywisty odczyt przed utratą widoczności ekranu pokazywał błąd (<see cref="ErrorStillSeenLocked"/>).
    /// Brak wpisu przy maszynie nieobserwowanej (epizod odtworzony przez <see cref="RestoreObservation"/>) = liczymy do „teraz”.
    /// </summary>
    private readonly Dictionary<string, long> _unobservedSinceMs = new(StringComparer.Ordinal);

    private readonly Dictionary<AlertKey, RetiredEpisode> _rejoinable = new();

    // Placeholder sprzed OCR nie rozstrzyga tożsamości zamkniętego błędu. Ten stan nie jest
    // alertem: nie trafia do UI, ACK, grupy ani dziennika przed odczytem pierwszej treści.
    private readonly Dictionary<string, PendingFirstRead> _pendingFirstReads = new();
    private sealed record PendingFirstRead(MachineConfig Machine, MappedMessage Fallback, string? CaptureFile,
        Task<MappedMessage> Read, AlertKey CandidateKey, Guid CandidateId, ObservationTime RejoinSince,
        ObservationTime FirstRed, ObservationTime LastRed);

    /// <summary>Zapamiętany, zamknięty epizod błędu — tyle, ile trzeba, żeby go wznowić bez zrywania ciągłości.</summary>
    private sealed record RetiredEpisode(
        Guid EpisodeId, DateTimeOffset StartedAt, AlertStatus Status, DateTimeOffset? AcknowledgedAt, long? AcknowledgedMs,
        string? AckedByHost, string? AckedByUser, string? CaptureFile, ObservationTime ClosedAt,
        DateTimeOffset? EscalatedAt, IReadOnlyList<Guid> SupersededEpisodeIds, DateTimeOffset? ReconciledAt);

    /// <summary>Najwięcej poprzedników w <see cref="Alert.SupersededEpisodeIds"/> — limit, który węzeł grupy jeszcze przyjmuje.</summary>
    private const int MaxSupersededEpisodeIds = 128;

    public void SetScreenObservationAvailable(string machineId, bool available)
    {
        lock (_lock)
        {
            if (!available)
            {
                if (_unobservedMachines.Add(machineId))
                {
                    _unobservedSince[machineId] = _clock.UtcNow;
                    _unobservedSinceMs[machineId] = _clock.MonotonicMs;
                }
                _redClearedSince.Remove(machineId);
                ForgetIdleCounters(machineId);
            }
            else if (_unobservedMachines.Remove(machineId))
            {
                _observationResumedAt[machineId] = _clock.MonotonicMs;
                _unobservedSince.Remove(machineId);
            }
        }
    }

    public AlertStateMachine(IClock clock, TimingConfig timing, AckMemory acks, ILogger? logger = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _acks = acks ?? throw new ArgumentNullException(nameof(acks));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Zdarzenia stanu alertów — do UI (dźwięk/okno), historii i wysyłki po sieci.</summary>
    public event Action<AlertEvent>? Changed;

    public Alert OnRedConfirmed(MachineConfig machine, MappedMessage message, string? captureFile)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(message);
        var events = new List<AlertEvent>();
        Alert result;
        lock (_lock)
        {
            if (_pendingFirstReads.TryGetValue(machine.Id, out var pending))
            {
                var observed = ObservationTime.Read(_clock);
                _lastRedSeen[machine.Id] = observed.Utc;
                result = ResolveFirstReadLocked(pending with { LastRed = observed }, message, events);
            }
            else result = OnRedConfirmedLocked(machine, message, captureFile, events);
        }
        Publish(events);
        return result;
    }

    /// <summary>
    /// Potwierdzona czerwień, gdy rzeczywiście trwa PIERWSZA próba odczytu bieżącej generacji obrazu.
    /// Tylko powrót niedawno zamkniętego, znanego błędu czeka na tę próbę; bez kandydata alarm powstaje
    /// od razu. Pusty/nieudany wynik kończy oczekiwanie. Zwykły brak OCR używa OnRedConfirmed.
    /// Zadanie jest wyłącznie odczytywane po zakończeniu; nie publikuje zdarzeń z kontynuacji OCR.
    /// </summary>
    /// <returns>True, jeśli tę obserwację obsłużył prywatny stan pierwszego odczytu.</returns>
    public bool OnRedConfirmedWithFirstRead(MachineConfig machine, MappedMessage fallback, string? captureFile,
        Task<MappedMessage> firstRead)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(firstRead);
        var events = new List<AlertEvent>();
        bool handledFirstRead;
        lock (_lock)
        {
            var observed = ObservationTime.Read(_clock);
            _lastRedSeen[machine.Id] = observed.Utc;
            if (_pendingFirstReads.TryGetValue(machine.Id, out var previous) && !ReferenceEquals(previous.Read, firstRead))
                FlushFirstReadLocked(machine.Id, events);

            if (_pendingFirstReads.TryGetValue(machine.Id, out var pending))
            {
                pending = pending with { LastRed = observed };
                _pendingFirstReads[machine.Id] = pending;
            }
            else if (FindUnclosedError(machine.Id) is null && CanRejoinLocked(machine.Id))
            {
                // Ostatni zamknięty epizod, nie pierwszy pasujący wpis słownika (np. starszy text:empty).
                var candidate = _rejoinable.Where(p => p.Key.MachineId == machine.Id)
                    .OrderByDescending(p => p.Value.ClosedAt.Milliseconds).FirstOrDefault();
                if (candidate.Value is { } retired && candidate.Key.Discriminator != EmptyTextDiscriminator
                    && retired.ClosedAt.Age(_clock).TotalMilliseconds < _timing.SameMessageRejoinMs)
                {
                    var since = _redClearedSince.TryGetValue(machine.Id, out var cleared)
                        && cleared.Milliseconds < retired.ClosedAt.Milliseconds ? cleared : retired.ClosedAt;
                    pending = new(machine, fallback, captureFile, firstRead, candidate.Key, retired.EpisodeId,
                        since, observed, observed);
                    _pendingFirstReads[machine.Id] = pending;
                }
            }

            handledFirstRead = pending is not null;
            if (pending is null)
                OnRedConfirmedLocked(machine, fallback, captureFile, events);
            else
            {
                ForgetIdleCounters(machine.Id);
                CloseIdleAlertLocked(machine.Id, observed.Utc, events);
                if (firstRead.IsCompleted || !CanResolveAsRejoinLocked(pending))
                    ResolveFirstReadLocked(pending, FirstReadResult(pending), events);
            }
        }
        Publish(events);
        return handledFirstRead;
    }

    /// <summary>Rozlicza już widzianą czerwień przed porzuceniem odczytu/obserwacji, bez nowej klatki.</summary>
    public void FlushPendingFirstRead(string machineId)
    {
        var events = new List<AlertEvent>();
        lock (_lock) FlushFirstReadLocked(machineId, events);
        Publish(events);
    }

    private void FlushFirstReadLocked(string machineId, List<AlertEvent> events)
    {
        if (_pendingFirstReads.TryGetValue(machineId, out var pending))
            ResolveFirstReadLocked(pending, FirstReadResult(pending), events);
    }

    private static MappedMessage FirstReadResult(PendingFirstRead pending) =>
        pending.Read.IsCompletedSuccessfully ? pending.Read.Result : pending.Fallback;

    private bool CanResolveAsRejoinLocked(PendingFirstRead pending) =>
        pending.RejoinSince.Age(_clock).TotalMilliseconds < _timing.SameMessageRejoinMs
        && CanRejoinLocked(pending.Machine.Id)
        && _rejoinable.TryGetValue(pending.CandidateKey, out var retired) && retired.EpisodeId == pending.CandidateId;

    private Alert ResolveFirstReadLocked(PendingFirstRead pending, MappedMessage message, List<AlertEvent> events)
    {
        _pendingFirstReads.Remove(pending.Machine.Id);
        // Tak samo jak wybór _bestMapped w monitorze: komunikat Ignored nigdy nie opisuje alarmu.
        if (!message.IsBetterThan(pending.Fallback)) message = pending.Fallback;
        AlertKey key = AlertKey.ForMessage(pending.Machine.Id, message);
        string? fullText = NormalizeFullText(message.RawText);
        DateTimeOffset now = _clock.UtcNow;
        Alert? rejoined = key.Discriminator != EmptyTextDiscriminator && CanResolveAsRejoinLocked(pending)
            ? RejoinRetiredEpisodeLocked(pending.Machine, message, key, fullText, pending.CaptureFile, now, events, pending.LastRed)
            : null;
        Alert result = rejoined ?? CreateNewKeyAlert(pending.Machine, message, key, fullText,
            pending.CaptureFile, now, events, pending.FirstRed, pending.LastRed);
        _redClearedSince.Remove(pending.Machine.Id);
        _episodeBrokenByOffline.Remove(pending.Machine.Id);
        return result;
    }

    private Alert OnRedConfirmedLocked(MachineConfig machine, MappedMessage message, string? captureFile,
        List<AlertEvent> events)
    {
        Alert result;
        DateTimeOffset now = _clock.UtcNow;
        AlertKey key = AlertKey.ForMessage(machine.Id, message);
        string? fullText = NormalizeFullText(message.RawText);

        bool contentless = !message.Matched && message.NormalizedText.Length == 0 && message.AlarmNumber is null;
        bool holdForFirstRead = contentless && CanRejoinLocked(machine.Id);

        _lastRedSeen[machine.Id] = now;

        if (_alerts.TryGetValue(key, out Alert? sameKey) && sameKey.Status != AlertStatus.Closed)
        {
            // TEN SAM klucz po przerwie: dopóki mieści się w oknie łączenia, pasek HMI tylko mrugnął
            // — zostaje jeden epizod, bez dźwięku i bez wpisu „nowy”.
            if (IsEpisodeBrokenLocked(machine.Id) && !CanRejoinLocked(machine.Id))
            {
                CloseUnclosedErrorLocked(sameKey, now, events);
                result = CreateNewKeyAlert(machine, message, key, fullText, captureFile, now, events);
            }
            else result = UpdateSameKey(sameKey, machine, message, fullText, captureFile, now, events);
        }
        else
        {
            Alert? other = FindUnclosedError(machine.Id, key);
            if (other is null)
            {
                Alert? rejoined = RejoinRetiredEpisodeLocked(machine, message, key, fullText, captureFile, now, events);
                result = rejoined ?? CreateNewKeyAlert(machine, message, key, fullText, captureFile, now, events);
                if (rejoined is null) holdForFirstRead = false;
            }
            else if (IsEpisodeBrokenLocked(machine.Id) && !holdForFirstRead)
            {
                // Epizod był przerwany (czerwień zniknęła na ≥ EpisodeBreakAfterMs albo maszyna była
                // offline) — to NOWY błąd, nie kolejny komunikat tego samego epizodu: stary zamykamy
                // natychmiast (jak Tick), nowy dostaje pełny "Raised" (z dźwiękiem), niezależnie od IsBetterThan.
                CloseUnclosedErrorLocked(other, now, events);
                result = CreateNewKeyAlert(machine, message, key, fullText, captureFile, now, events);
            }
            else if (message.IsBetterThan(AsMappedMessage(other)))
            {
                result = SwapKey(other, key, machine, message, fullText, captureFile, now, events);
            }
            else
            {
                // Ten sam epizod (czerwień nieprzerwana), ale nowy komunikat NIE jest lepszy od
                // starego (patrz MappedMessage.IsBetterThan) — ignorujemy jego treść, odświeżamy
                // tylko LastSeenAt starego alertu. Jedno okienko statusu = jeden błąd.
                Alert refreshed = other with { LastSeenAt = Observe(other.Key, now) };
                _alerts[other.Key] = refreshed;
                result = refreshed;
            }
        }

        _episodeAliases[key] = result.Key;
        if (!result.EpisodeKeys.Contains(key))
        {
            _acks.LinkEpisode(result.Key, [key]);
            result = result with { EpisodeKeys = _acks.EpisodeKeys(result.Key) };
            _alerts[result.Key] = result;
        }

        // Czerwień jest AKURAT potwierdzona — ewentualne wcześniejsze "przerwanie" epizodu tej
        // maszyny przestaje być aktualne (patrz doc <see cref="_redClearedSince"/>), aż coś je znów
        // ustawi (OnRedCleared/OnMachineOffline). Wyjątek: odczyt BEZ TREŚCI niczego nie rozstrzyga,
        // więc przerwa zostaje zapamiętana do pierwszego prawdziwego odczytu (patrz holdForFirstRead).
        if (!holdForFirstRead)
        {
            _redClearedSince.Remove(machine.Id);
            _episodeBrokenByOffline.Remove(machine.Id);
        }

        // Czerwony błąd ma priorytet nad alertem postoju (patrz szerszy komentarz w
        // OnProgressState): przy KAŻDYM potwierdzeniu/odświeżeniu czerwieni — niezależnie od tego,
        // czy to nowy epizod, aktualizacja tekstu, czy tylko odświeżenie LastSeenAt — maszyna stoi
        // (albo za chwilę stanie) z powodu błędu, nie z braku pracy. Zamykamy to TU, w tej samej
        // sekundzie, w której powstaje/odświeża się alert błędu, a nie dopiero przy najbliższym
        // cyklu OnProgressState — inaczej pop-up/lista "maszyny bez pracy" pokazywałyby przez
        // chwilę maszynę, która w istocie ma czerwony błąd na ekranie.
        ForgetIdleCounters(machine.Id);
        CloseIdleAlertLocked(machine.Id, now, events);
        return result;
    }

    /// <summary>
    /// Czerwień nie jest widoczna w tym cyklu odczytu. Samo w sobie nic nie zamyka — zamknięcie epizodu
    /// liczy <see cref="Tick"/> na podstawie czasu, jaki upłynął od ostatniego <see cref="OnRedConfirmed"/>.
    /// Zapamiętuje też (przy PIERWSZYM zniknięciu po ostatnim potwierdzeniu) w <see cref="_redClearedSince"/>,
    /// od kiedy czerwieni nie ma — gdy wróci z INNYM dopasowaniem po przerwie ≥
    /// <see cref="TimingConfig.EpisodeBreakAfterMs"/>, <see cref="OnRedConfirmed"/> potraktuje to jako NOWY
    /// błąd (np. po restarcie); krótsza przerwa (okienko HMI mruga między komunikatami w pętli) to nadal
    /// ten sam epizod, czyli jeden alert.
    /// </summary>
    public void OnRedCleared(string machineId)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        var events = new List<AlertEvent>();
        lock (_lock)
        {
            FlushFirstReadLocked(machineId, events);
            _lastRedSeen[machineId] = null;
            _redClearedSince.TryAdd(machineId, ObservationTime.Read(_clock));
        }
        Publish(events);
    }

    /// <summary>
    /// Zgłasza brak łączności z maszyną od podanego momentu — alert powstanie dopiero po przekroczeniu progu
    /// (w <see cref="Tick"/>). Wywoływane per cykl monitorowania, dopóki maszyna jest offline — gdy istnieje
    /// już (niezamknięty) alert "brak łączności" dla tej maszyny, odświeża jego <c>LastSeenAt</c>, żeby
    /// <see cref="Tick"/> go nie zamknął jako przeterminowany. Dodatkowo zeruje licznik
    /// „100% nieprzerwanie od" (<see cref="_fullSince"/>) — utrata łączności zeruje licznik 2 minut, bo nie
    /// wiemy, co się działo z paskiem postępu w czasie, gdy nie mieliśmy obrazu. Oznacza też epizod błędu
    /// tej maszyny jako PRZERWANY (<see cref="_episodeBrokenByOffline"/>) — od razu, bez czekania na
    /// <see cref="TimingConfig.EpisodeBreakAfterMs"/>: utrata łączności to typowo restart maszyny po błędzie.
    /// </summary>
    public void OnMachineOffline(MachineConfig machine, DateTimeOffset offlineSince)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var events = new List<AlertEvent>();
        lock (_lock)
        {
            FlushFirstReadLocked(machine.Id, events);
            if (!_offlineMachines.ContainsKey(machine.Id))
                _offlineMachines[machine.Id] = (machine, new ObservationTime(offlineSince,
                    _clock.MonotonicMs - (long)Math.Max(0, (_clock.UtcNow - offlineSince).TotalMilliseconds)));
            ForgetIdleCounters(machine.Id);
            // Pierwsze zgłoszenie przerwy zapamiętuje jej początek; kolejne (co cykl, ten sam offlineSince) i nowa
            // przerwa po krótkim powrocie przed potwierdzeniem czerwieni liczą się od pierwszego początku.
            if (!_episodeBrokenByOffline.TryGetValue(machine.Id, out OfflineGap gap))
                _episodeBrokenByOffline[machine.Id] = new OfflineGap(_offlineMachines[machine.Id].OfflineSince.Milliseconds, null);
            else if (gap.EndMs is not null)
                _episodeBrokenByOffline[machine.Id] = gap with { EndMs = null };
            _unobservedMachines.Remove(machine.Id);
            _observationResumedAt.Remove(machine.Id);

            // Bez obrazu nie wiemy, jaki ekran pokazuje HMI — a restart maszyny (typowa przyczyna
            // utraty łączności) i tak przywraca ekran główny. Licznik „inny ekran od” startuje od zera
            // po powrocie, a otwarte przypomnienie „włącz okno podglądu” zamykamy od razu.
            _wrongScreenSince.Remove(machine.Id);
            CloseAlertOfKindLocked(AlertKey.WrongScreen(machine.Id), _clock.UtcNow, events);

            AlertKey key = AlertKey.Offline(machine.Id);
            if (_alerts.TryGetValue(key, out Alert? existing) && existing.Status != AlertStatus.Closed)
            {
                _alerts[key] = existing with { LastSeenAt = Observe(key, _clock.UtcNow) };
            }
        }

        Publish(events);
    }

    public void OnMachineDisabled(string machineId)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        var events = new List<AlertEvent>();
        lock (_lock)
        {
            FlushFirstReadLocked(machineId, events);
            foreach (Alert alert in _alerts.Values.Where(a => a.MachineId == machineId && a.Status != AlertStatus.Closed).ToList())
                CloseUnclosedErrorLocked(alert, _clock.UtcNow, events);
            // Celowe wyłączenie kończy obserwację, także dla błędów zamkniętych jeszcze przed wyłączeniem.
            foreach (AlertKey key in _rejoinable.Keys.Where(k => k.MachineId == machineId).ToArray())
                _rejoinable.Remove(key);
            _offlineMachines.Remove(machineId);
            _lastRedSeen.Remove(machineId);
            _redClearedSince.Remove(machineId);
            _episodeBrokenByOffline.Remove(machineId);
            ForgetIdleCounters(machineId);
            _wrongScreenSince.Remove(machineId);
            _unobservedMachines.Remove(machineId);
            _observationResumedAt.Remove(machineId);
            _unobservedSince.Remove(machineId);
            _unobservedSinceMs.Remove(machineId);
        }

        Publish(events);
    }

    /// <summary>Nieznany ekran przerywa licznik braku etykiety, bez potwierdzania naprawy.</summary>
    public void OnMainScreenUnknown(string machineId)
    {
        lock (_lock)
        {
            _wrongScreenSince.Remove(machineId);
        }
        SetScreenObservationAvailable(machineId, false);
    }

    /// <summary>
    /// Zgłasza wynik sprawdzenia, czy HMI maszyny pokazuje ekran główny (okno stanu maszyny z alertami i
    /// procentem) — wołane przez <c>Machines.MachineMonitor</c> raz na <c>TimingConfig.MainScreenCheckIntervalMs</c>,
    /// TYLKO z rozstrzygniętym wynikiem (awaria OCR = brak wywołania, patrz <c>Vision.MainScreenDetector</c>).
    /// Semantyka jak <see cref="OnProgressState"/>: <paramref name="mainScreenVisible"/> = <see langword="true"/>
    /// zamyka NATYCHMIAST ewentualny otwarty alert <see cref="AlertKind.WrongScreen"/> (operator wrócił na
    /// ekran główny — jednoznaczny sygnał końca) i zeruje licznik; <see langword="false"/> uruchamia licznik
    /// <see cref="_wrongScreenSince"/>, a gdy inny ekran trwa nieprzerwanie ≥ <c>TimingConfig.WrongScreenAlertAfterMs</c>
    /// — tworzy alert (zdarzenie Raised; Updated, gdy <see cref="AckMemory"/> zna klucz) ze <c>StartedAt</c> =
    /// początek niewłaściwego ekranu, <c>Sound</c> = <c>TimingConfig.WrongScreenAlertSound</c>. Po „✕” (Acknowledge)
    /// alert wraca jako Escalated po <c>TimingConfig.WrongScreenRemindAfterMs</c>, jeśli ekran nadal jest
    /// niewłaściwy (patrz <see cref="Tick"/>) — program jest wtedy ślepy na błędy tej maszyny.
    /// </summary>
    public void OnMainScreenState(MachineConfig machine, bool mainScreenVisible, string? captureFile = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        SetScreenObservationAvailable(machine.Id, mainScreenVisible);

        var events = new List<AlertEvent>();
        lock (_lock)
        {
            DateTimeOffset now = _clock.UtcNow;
            AlertKey key = AlertKey.WrongScreen(machine.Id);

            if (mainScreenVisible)
            {
                _wrongScreenSince.Remove(machine.Id);
                CloseAlertOfKindLocked(key, now, events);
            }
            else
            {
                if (!_wrongScreenSince.TryGetValue(machine.Id, out ObservationTime since))
                {
                    since = ObservationTime.Read(_clock);
                    _wrongScreenSince[machine.Id] = since;
                }

                if (_alerts.TryGetValue(key, out Alert? current) && current.Status != AlertStatus.Closed)
                {
                    _alerts[key] = current with
                    {
                        LastSeenAt = Observe(key, now),
                        MachineName = machine.Name,
                        CaptureFile = current.CaptureFile ?? captureFile,
                    };
                }
                else if (since.Age(_clock) >= TimeSpan.FromMilliseconds(_timing.WrongScreenAlertAfterMs))
                {
                    AckRecord? ack = _groupProtocol ? null : _acks.Get(key);
                    Alert alert = new()
                    {
                        Key = key,
                        MachineId = machine.Id,
                        MachineName = machine.Name,
                        Kind = AlertKind.WrongScreen,
                        ShortText = WrongScreenShortText,
                        FullText = null,
                        Category = "ekran",
                        StartedAt = since.Utc,
                        LastSeenAt = Observe(key, now),
                        Status = ack is not null ? AlertStatus.Acknowledged : AlertStatus.Active,
                        AcknowledgedAt = ack?.At,
                        AckedByHost = ack?.Host,
                        AckedByUser = ack?.User,
                        CaptureFile = captureFile,
                        Sound = _timing.WrongScreenAlertSound,
                    };
                    _alerts[key] = alert;
                    events.Add(new AlertEvent(ack is not null ? AlertEventKind.Updated : AlertEventKind.Raised, alert));
                }
            }
        }

        Publish(events);
    }

    /// <summary>Maszyna wróciła online — zamyka ewentualny alert "brak łączności" i czyści zapamiętany stan offline.</summary>
    public void OnMachineOnline(string machineId)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        var events = new List<AlertEvent>();
        lock (_lock)
        {
            _offlineMachines.Remove(machineId);
            if (_episodeBrokenByOffline.TryGetValue(machineId, out OfflineGap gap) && gap.EndMs is null)
                _episodeBrokenByOffline[machineId] = gap with { EndMs = _clock.MonotonicMs };

            AlertKey key = AlertKey.Offline(machineId);
            if (_alerts.TryGetValue(key, out Alert? existing) && existing.Status != AlertStatus.Closed)
            {
                DateTimeOffset now = _clock.UtcNow;
                Alert closed = existing with { Status = AlertStatus.Closed, ClosedAt = now };
                _alerts.Remove(key);
                AddToHistory(closed);
                _acks.Forget(key); // koniec epizodu — ACK ważne tylko "do zamknięcia lub 2h"
                events.Add(new AlertEvent(AlertEventKind.Closed, closed));
            }
        }

        Publish(events);
    }

    public void OnProgressState(MachineConfig machine, ProgressState state, string? captureFile = null)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var events = new List<AlertEvent>();
        lock (_lock)
        {
            DateTimeOffset now = _clock.UtcNow;
            AlertKey key = AlertKey.Idle(machine.Id);

            if (HasUnclosedError(machine.Id))
            {
                // Czerwony błąd ma priorytet nad alertem postoju: gdy maszyna ma otwarty, niezamknięty
                // alert Error, stoi (albo pokazuje 0%) Z POWODU TEGO BŁĘDU, nie z braku pracy — a nawet
                // gdyby akurat pokazywała 100% (np. błąd pojawił się chwilę po zakończeniu pracy), alert
                // "dołóż prace" byłby mylący: operator ma iść naprawić błąd, a nie szukać kolejnej pracy
                // do zafrezowania. Dlatego podczas otwartego błędu licznik "100% nieprzerwanie od" NIE
                // biegnie (usuwamy _fullSince) i nie tworzymy/odświeżamy alertu postoju, a ewentualny już
                // istniejący alert postoju zamykamy natychmiast — niezależnie od aktualnego atFull. Gdy
                // błąd się zamknie (Tick, po EpisodeCloseAfterMs bez czerwieni), _fullSince zostaje puste,
                // więc kolejne OnProgressState(atFull: true) zacznie liczyć 2 minuty od zera — dokładnie
                // jak przy zwykłym, świeżym epizodzie postoju (patrz też OnRedConfirmed, który zamyka
                // alert postoju od razu przy powstaniu/odświeżeniu błędu, żeby nie czekać na ten cykl).
                // Oba liczniki postoju (100 % i 0 %) zerują się tak samo: przy otwartym błędzie 0 % znaczy
                // „stoi przez ten błąd”, a nie „nikt nie wcisnął Start”.
                ForgetIdleCounters(machine.Id);
                CloseIdleAlertLocked(machine.Id, now, events);
            }
            else if (state == ProgressState.Running)
            {
                ForgetIdleCounters(machine.Id);
                CloseIdleAlertLocked(machine.Id, now, events);
            }
            else
            {
                bool atZero = state == ProgressState.Zero;
                string category = atZero ? IdleAtZeroCategory : IdleCategory;
                Dictionary<string, ObservationTime> since = IdleCounter(category);
                Dictionary<string, ObservationTime> opposite = IdleCounter(atZero ? IdleCategory : IdleAtZeroCategory);
                TimeSpan alertAfter = TimeSpan.FromMilliseconds(
                    atZero ? _timing.IdleAtZeroAfterMs : _timing.IdleAlertAfterMs);

                opposite.Remove(machine.Id);
                if (!since.TryGetValue(machine.Id, out ObservationTime standingSince))
                {
                    standingSince = ObservationTime.Read(_clock);
                    since[machine.Id] = standingSince;
                }

                if (_alerts.TryGetValue(key, out Alert? current) && current.Status != AlertStatus.Closed)
                {
                    if (current.Category == category)
                    {
                        _alerts[key] = current with
                        {
                            LastSeenAt = Observe(key, now),
                            MachineName = machine.Name,
                            CaptureFile = current.CaptureFile ?? captureFile,
                        };
                    }
                    else
                    {
                        // 100 % → 0 % (albo odwrotnie): powód otwartego postoju przestał być prawdziwy.
                        // Zamykamy epizod i odliczamy próg nowego stanu od zera — patrz komentarz metody.
                        CloseIdleAlertLocked(machine.Id, now, events);
                    }
                }
                else if (standingSince.Age(_clock) >= alertAfter)
                {
                    // Dokładnie jak CreateNewKeyAlert dla błędów: gdy AckMemory już zna ten klucz (poprzedni
                    // epizod postoju potwierdzono, a maszyna wciąż stoi), nie dzwonimy (Raised) — tylko Updated.
                    AckRecord? ack = _groupProtocol ? null : _acks.Get(key);
                    Alert alert = new()
                    {
                        Key = key,
                        MachineId = machine.Id,
                        MachineName = machine.Name,
                        Kind = AlertKind.MachineIdle,
                        ShortText = atZero ? MachineIdleAtZeroShortText : MachineIdleShortText,
                        FullText = null,
                        Category = category,
                        StartedAt = standingSince.Utc,
                        LastSeenAt = Observe(key, now),
                        Status = ack is not null ? AlertStatus.Acknowledged : AlertStatus.Active,
                        AcknowledgedAt = ack?.At,
                        AckedByHost = ack?.Host,
                        AckedByUser = ack?.User,
                        CaptureFile = captureFile,
                        Sound = false,
                    };
                    _alerts[key] = alert;
                    events.Add(new AlertEvent(ack is not null ? AlertEventKind.Updated : AlertEventKind.Raised, alert));
                }

                // Gdy nic z powyższego (postęp stoi, ale krócej niż próg, i nie ma jeszcze alertu) — nic nie
                // robimy: czekamy na upływ progu w kolejnych cyklach.
            }
        }

        Publish(events);
    }

    /// <summary>
    /// Zgodność wsteczna: stara sygnatura „na 100 % / poniżej 100 %”, bez rozróżnienia 0 % (używana przez
    /// testy i ścieżki, które nie mają odczytu procentu — patrz <see cref="OnProgressState(MachineConfig, ProgressState, string?)"/>).
    /// </summary>
    public void OnProgressState(MachineConfig machine, bool atFull, string? captureFile = null) =>
        OnProgressState(machine, atFull ? ProgressState.Full : ProgressState.Running, captureFile);

    /// <summary>
    /// Brak wiarygodnego odczytu postępu (nieczytelny/niedostępny OCR, oczekujący pierwszy odczyt po
    /// zmianie źródła albo zbyt mało próbek filtru „2 z 3”). Zeruje OBA liczniki postoju („100 %
    /// nieprzerwanie od” i „0 % nieprzerwanie od”),
    /// ale NIE zamyka istniejącego alertu postoju — nieczytelny odczyt nie dowodzi, że maszyna ruszyła
    /// (zamknięcie to jawne <see cref="OnProgressState"/> z <c>atFull=false</c> albo brak odświeżenia
    /// przez <c>EpisodeCloseAfterMs</c> w <see cref="Tick"/>). Dzięki temu alert przejęty od
    /// poprzedniego obserwatora albo zachowany po uśpieniu nie znika i nie wraca jako nowy pop-up
    /// tylko dlatego, że nowy monitor nie ma jeszcze pierwszego rzeczywistego odczytu.
    /// </summary>
    public void OnProgressUnknown(string machineId)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        lock (_lock)
        {
            ForgetIdleCounters(machineId);
        }
    }

    /// <summary>
    /// Potwierdza alert ("Idę sprawdzić"). Active/Escalated → Acknowledged (emituje <see cref="AlertEventKind.Acknowledged"/>).
    /// Już Acknowledged → tylko aktualizuje dane ACK, jeśli <paramref name="at"/> jest wcześniejsze (pierwszy, kto potwierdził
    /// wygrywa w wyświetlaniu) — bez zdarzenia. Brak alertu albo Closed → tylko zapisuje do <see cref="AckMemory"/> (żeby nowy
    /// obserwator po przejęciu roli wiedział, że epizod już potwierdzono) i zwraca <see langword="false"/>.
    /// </summary>
    public bool Acknowledge(AlertKey key, string host, string user, DateTimeOffset at) =>
        AcknowledgeCore(key, host, user, at, local: false);

    /// <summary>Świadome potwierdzenie bieżącego alertu na tym komputerze, odporne na cofnięcie UTC.</summary>
    public bool AcknowledgeLocal(AlertKey key, string host, string user) =>
        AcknowledgeCore(key, host, user, _clock.UtcNow, local: true);

    private bool AcknowledgeCore(AlertKey key, string host, string user, DateTimeOffset at, bool local, Guid? episodeId = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(user);

        bool result;
        AlertEvent? emitted = null;
        lock (_lock)
        {
            // Resolve UUID and mutate under the same lock: a concurrent new episode may reuse
            // the old key, but must never receive its delayed confirmation.
            if (episodeId is { } episode)
            {
                var target = _alerts.Values.FirstOrDefault(a => a.EpisodeId == episode
                    && a.MachineId == key.MachineId && a.Status != AlertStatus.Closed);
                if (target is null) return false;
                key = target.Key;
            }
            else if (_groupProtocol && !local) return false;
            var incoming = new AckRecord(key, host, user, at);
            if (!local && episodeId is null && _acks.IsRetired(incoming)) return false;
            if (_episodeAliases.TryGetValue(key, out var currentKey)) key = currentKey;
            if (_alerts.TryGetValue(key, out Alert? existing) && existing.Status != AlertStatus.Closed)
            {
                if (existing.ReconciledAt is { } boundary)
                {
                    if (!local && at <= boundary) return false;
                    if (local && at <= boundary) at = boundary.AddTicks(1);
                }
                if (!local && (at < existing.StartedAt || (existing.EscalatedAt is { } escalated && at < escalated))) return false;
                if (episodeId is not null && existing.EscalatedAt is { } groupEscalated && at <= groupEscalated) return false;
                if (existing.Status is AlertStatus.Active or AlertStatus.Escalated)
                {
                    Alert acked = existing with
                    {
                        Status = AlertStatus.Acknowledged,
                        AcknowledgedAt = at,
                        AckedByHost = host,
                        AckedByUser = user,
                    };
                    _acknowledgedMs[key] = _clock.MonotonicMs;
                    _alerts[key] = acked;
                    if (local) _acks.RecordLocal(new AckRecord(key, host, user, at));
                    else _acks.Record(new AckRecord(key, host, user, at));
                    emitted = new AlertEvent(AlertEventKind.Acknowledged, acked);
                    result = true;
                }
                else
                {
                    // Już Acknowledged: pierwszy, kto faktycznie potwierdził (najwcześniejszy `at`), wygrywa.
                    if (existing.AcknowledgedAt is null || at < existing.AcknowledgedAt)
                    {
                        _alerts[key] = existing with
                        {
                            AcknowledgedAt = at,
                            AckedByHost = host,
                            AckedByUser = user,
                        };
                    }

                    if (local) _acks.RecordLocal(new AckRecord(key, host, user, at));
                    else _acks.Record(new AckRecord(key, host, user, at));
                    result = true;
                }
            }
            else
            {
                // Brak alertu albo już zamknięty — zapamiętujemy mimo to, żeby nowy obserwator go nie wznowił.
                if (!local) _acks.Record(new AckRecord(key, host, user, at));
                result = false;
            }
        }

        if (emitted is not null)
        {
            Changed?.Invoke(emitted);
        }

        return result;
    }

    /// <summary>Zastosowanie zdalnego potwierdzenia (odebranego z sieci) — deleguje do <see cref="Acknowledge"/>.</summary>
    public void ApplyRemoteAck(AckRecord ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        AcknowledgeCore(ack.Key, ack.Host, ack.User, ack.At, local: false, ack.EpisodeId);
    }

    public IReadOnlyList<AlertEvent> Tick()
    {
        var events = new List<AlertEvent>();
        lock (_lock)
        {
            DateTimeOffset now = _clock.UtcNow;
            var closeAfter = TimeSpan.FromMilliseconds(_timing.EpisodeCloseAfterMs);
            var escalateAfter = TimeSpan.FromMilliseconds(_timing.EscalationAfterAckMs);
            var offlineAfter = TimeSpan.FromMilliseconds(_timing.OfflineAlertAfterMs);

            // Próg zamknięcia Offline z braku odświeżenia MUSI mieć spory zapas ponad najdłuższy odstęp między
            // kolejnymi próbami połączenia (TimingConfig.ReconnectBackoffMs, domyślnie do 30 s) — inaczej, skoro
            // OnMachineOffline jest wołane raz na PRÓBĘ POŁĄCZENIA (rzadziej niż ciągły odczyt ROI ~co 1 s, na
            // którym opiera się odświeżanie Error), alert Offline otwierałby się i natychmiast zamykał w kółko,
            // gdy tylko backoff osiągnie EpisodeCloseAfterMs (domyślnie oba to akurat 30 s).
            long maxBackoffMs = _timing.ReconnectBackoffMs is { Length: > 0 } backoffs ? backoffs.Max() : 0;
            var offlineStaleAfter = TimeSpan.FromMilliseconds(Math.Max(_timing.EpisodeCloseAfterMs, (maxBackoffMs * 2) + 5000));

            // WrongScreen jest odświeżany tylko raz na MainScreenCheckIntervalMs (OCR pełnej klatki, domyślnie
            // 20 s) — ten sam zapas co dla Offline, żeby alert nie zamykał się między dwoma sprawdzeniami.
            var wrongScreenStaleAfter = TimeSpan.FromMilliseconds(
                Math.Max(_timing.EpisodeCloseAfterMs, ((long)_timing.MainScreenCheckIntervalMs * 2) + 5000));
            var remindAfter = TimeSpan.FromMilliseconds(_timing.WrongScreenRemindAfterMs);

            // OCR może skończyć się, gdy następna klatka już nie nadejdzie. Nie awaitujemy pod zamkiem
            // i nie dopisujemy czasu braku obrazu do LastSeenAt. Istniejące granice nadal obowiązują.
            foreach (var pending in _pendingFirstReads.Values.ToArray())
                if (pending.Read.IsCompleted || !CanResolveAsRejoinLocked(pending) || pending.LastRed.Age(_clock) >= closeAfter)
                    ResolveFirstReadLocked(pending, FirstReadResult(pending), events);

            if (_timing.SameMessageRejoinMs > 0)
            {
                foreach (AlertKey stale in _rejoinable
                    .Where(p => p.Value.ClosedAt.Age(_clock).TotalMilliseconds >= _timing.SameMessageRejoinMs)
                    .Select(p => p.Key).ToArray())
                {
                    _rejoinable.Remove(stale);
                }
            }
            else
            {
                _rejoinable.Clear();
            }

            foreach (Alert alert in _alerts.Values.ToList())
            {
                if (alert.Status == AlertStatus.Closed)
                {
                    continue;
                }

                // Zamknięcie po braku odświeżenia LastSeenAt dotyczy obu rodzajów alertu: Error (czerwień
                // znikła, próg closeAfter) i Offline (próg offlineStaleAfter, patrz wyżej).
                TimeSpan staleAfter = alert.Kind switch
                {
                    AlertKind.Offline => offlineStaleAfter,
                    AlertKind.WrongScreen => wrongScreenStaleAfter,
                    _ => closeAfter,
                };
                long lastObserved = _observedMs.GetValueOrDefault(alert.Key, _clock.MonotonicMs);
                if ((alert.Kind is AlertKind.Error or AlertKind.MachineIdle)
                    && _observationResumedAt.TryGetValue(alert.MachineId, out long resumedAt))
                    lastObserved = Math.Max(lastObserved, resumedAt);

                // Bez obserwacji ekranu (inny ekran HMI, nierozstrzygnięty OCR, dopiero co przejęta
                // obserwacja) błąd, przypomnienie o ekranie i postój czekają na rzeczywisty odczyt.
                bool observationMissing = _unobservedMachines.Contains(alert.MachineId)
                    && alert.Kind is AlertKind.Error or AlertKind.WrongScreen or AlertKind.MachineIdle;
                if (!observationMissing && _clock.MonotonicMs - lastObserved >= staleAfter.TotalMilliseconds)
                {
                    Alert closed = alert with { Status = AlertStatus.Closed, ClosedAt = now };
                    _alerts.Remove(alert.Key);
                    RetireEpisode(alert, now); // koniec epizodu — ACK ważne tylko "do zamknięcia lub 2h"
                    AddToHistory(closed);
                    if (alert.Kind == AlertKind.Offline)
                    {
                        _offlineMachines.Remove(alert.MachineId);
                    }
                    else if (alert.Kind == AlertKind.MachineIdle)
                    {
                        ForgetIdleCounters(alert.MachineId);
                    }
                    else if (alert.Kind == AlertKind.WrongScreen)
                    {
                        _wrongScreenSince.Remove(alert.MachineId);
                    }

                    events.Add(new AlertEvent(AlertEventKind.Closed, closed));
                    continue;
                }

                // Eskalacja dotyczy błędów (Error, po EscalationAfterAckMs; 0 = wyłączona) i przypomnienia o
                // niewłaściwym ekranie HMI (WrongScreen, po WrongScreenRemindAfterMs; 0 = wyłączone) — alert
                // "brak łączności" i "dołóż prace" celowo nigdy nie wracają.
                bool escalationDue = alert.Kind switch
                {
                    AlertKind.Error => _timing.EscalationAfterAckMs > 0 && AcknowledgementDue(alert, escalateAfter)
                        && ErrorStillSeenLocked(alert),
                    AlertKind.WrongScreen => _timing.WrongScreenRemindAfterMs > 0 && AcknowledgementDue(alert, remindAfter),
                    _ => false,
                };
                if (escalationDue)
                {
                    Alert escalated = alert with { Status = AlertStatus.Escalated, EscalatedAt = now };
                    _alerts[alert.Key] = escalated;
                    double sinceAck = alert.AcknowledgedAt is { } ackedAt
                        ? Math.Max(0, (now - ackedAt).TotalMinutes) : 0;
                    string reason = Combine($"eskalacja po {sinceAck:0.#} min od potwierdzenia",
                        UnobservedNoteLocked(alert.MachineId))!;
                    _logger.LogInformation(
                        "Maszyna '{MachineId}': alarm '{ShortText}' wraca — powód: {Reason}.",
                        alert.MachineId, alert.ShortText, reason);
                    events.Add(new AlertEvent(AlertEventKind.Escalated, escalated) { Reason = reason });
                }
            }

            foreach (KeyValuePair<string, (MachineConfig Machine, ObservationTime OfflineSince)> entry in _offlineMachines.ToList())
            {
                string machineId = entry.Key;
                MachineConfig machine = entry.Value.Machine;
                ObservationTime offlineSince = entry.Value.OfflineSince;

                AlertKey offlineKey = AlertKey.Offline(machineId);
                if (_alerts.TryGetValue(offlineKey, out Alert? existingOffline) && existingOffline.Status != AlertStatus.Closed)
                {
                    continue;
                }

                if (offlineSince.Age(_clock) < offlineAfter)
                {
                    continue;
                }

                AckRecord? ack = _groupProtocol ? null : _acks.Get(offlineKey);
                Alert offlineAlert = new()
                {
                    Key = offlineKey,
                    MachineId = machineId,
                    MachineName = machine.Name,
                    Kind = AlertKind.Offline,
                    ShortText = "Brak łączności z maszyną",
                    FullText = null,
                    Category = "offline",
                    StartedAt = offlineSince.Utc,
                    LastSeenAt = Observe(offlineKey, now),
                    Status = ack is not null ? AlertStatus.Acknowledged : AlertStatus.Active,
                    AcknowledgedAt = ack?.At,
                    AckedByHost = ack?.Host,
                    AckedByUser = ack?.User,
                    CaptureFile = null,
                    Sound = _timing.OfflineAlertSound,
                };
                _alerts[offlineKey] = offlineAlert;
                events.Add(new AlertEvent(ack is not null ? AlertEventKind.Updated : AlertEventKind.Raised, offlineAlert));
            }

            _acks.Prune();
        }

        Publish(events);
        return events;
    }

    /// <summary>Niezamknięte alerty, posortowane: najpierw dzwoniące (Active/Escalated), potem wg <c>StartedAt</c> rosnąco.</summary>
    public IReadOnlyList<Alert> ActiveAlerts
    {
        get
        {
            lock (_lock)
            {
                return OrderForDisplay(_alerts.Values.Where(a => a.Status != AlertStatus.Closed));
            }
        }
    }

    /// <summary>Ostatnie zamknięte alerty, najnowsze pierwsze (maks. 200).</summary>
    public IReadOnlyList<Alert> RecentHistory
    {
        get
        {
            lock (_lock)
            {
                return _recentHistory.ToList();
            }
        }
    }

    /// <summary>
    /// Klucze epizodów zamkniętych (<see cref="AlertStatus.Closed"/>) w ciągu ostatniego <paramref name="window"/>
    /// — do rozgłoszenia w <c>StateMessage.RecentlyClosed</c> (patrz <c>ClusterNode.SetLocalState</c>), żeby
    /// pozostałe komputery zapomniały ACK tych kluczy i kolejne wystąpienie tego samego błędu znów zadzwoniło.
    /// </summary>
    public IReadOnlyList<AlertKey> RecentlyClosedKeys(TimeSpan window)
    {
        lock (_lock)
        {
            DateTimeOffset threshold = _clock.UtcNow - window;
            return _recentHistory
                .Where(a => a.ClosedAt is { } closedAt && closedAt >= threshold)
                .Select(a => a.Key)
                .ToList();
        }
    }

    /// <summary>Zwraca alert (aktywny albo z ostatniej historii) o danym kluczu, jeśli istnieje.</summary>
    public Alert? Get(AlertKey key)
    {
        lock (_lock)
        {
            if (_alerts.TryGetValue(key, out Alert? active))
            {
                return active;
            }

            return _recentHistory.FirstOrDefault(a => a.Key == key);
        }
    }

    private void Publish(List<AlertEvent> events)
    {
        foreach (AlertEvent e in events)
        {
            Changed?.Invoke(e);
        }
    }

    private void AddToHistory(Alert closedAlert)
    {
        _observedMs.Remove(closedAlert.Key);
        _acknowledgedMs.Remove(closedAlert.Key);
        _recentHistory.Insert(0, closedAlert);
        if (_recentHistory.Count > MaxRecentHistory)
        {
            _recentHistory.RemoveRange(MaxRecentHistory, _recentHistory.Count - MaxRecentHistory);
        }
    }

    /// <summary>
    /// Szuka niezamkniętego alertu <see cref="AlertKind.Error"/> danej maszyny, opcjonalnie pomijając
    /// <paramref name="excludeKey"/> (używane przez <see cref="OnRedConfirmed"/>, żeby nie znaleźć "siebie
    /// samego" przy sprawdzaniu, czy błąd o innym kluczu już trwa). Wywołanie bez <paramref name="excludeKey"/>
    /// (patrz <see cref="HasUnclosedError"/>) sprawdza po prostu "czy ta maszyna ma jakikolwiek otwarty błąd".
    /// </summary>
    private Alert? FindUnclosedError(string machineId, AlertKey? excludeKey = null)
    {
        foreach (Alert alert in _alerts.Values)
        {
            if (alert.MachineId == machineId && alert.Kind == AlertKind.Error
                && alert.Status != AlertStatus.Closed && (excludeKey is null || alert.Key != excludeKey.Value))
            {
                return alert;
            }
        }

        return null;
    }

    /// <summary>
    /// Czy dana maszyna ma teraz jakikolwiek niezamknięty alert <see cref="AlertKind.Error"/> — decyduje
    /// o priorytecie błędu nad alertem postoju (<see cref="OnProgressState"/>, <see cref="OnRedConfirmed"/>):
    /// patrz uzasadnienie w komentarzu <see cref="OnProgressState"/>.
    /// </summary>
    private bool HasUnclosedError(string machineId) =>
        _pendingFirstReads.ContainsKey(machineId) || FindUnclosedError(machineId) is not null;

    /// <summary>
    /// Czy epizod błędu maszyny jest przerwany: maszyna była offline od
    /// ostatniego potwierdzenia, albo czerwień jest nieprzerwanie niewidoczna od co najmniej
    /// <see cref="TimingConfig.EpisodeBreakAfterMs"/> (patrz <see cref="_redClearedSince"/>).
    /// </summary>
    private bool IsEpisodeBrokenLocked(string machineId)
    {
        if (_episodeBrokenByOffline.ContainsKey(machineId))
        {
            return true;
        }

        return _redClearedSince.TryGetValue(machineId, out ObservationTime since)
            && (since.Age(_clock)).TotalMilliseconds >= _timing.EpisodeBreakAfterMs;
    }

    private bool ErrorStillSeenLocked(Alert alert)
    {
        long now = _clock.MonotonicMs;
        long seen = _observedMs.GetValueOrDefault(alert.Key, now);
        long reference = _unobservedMachines.Contains(alert.MachineId)
            && _unobservedSinceMs.TryGetValue(alert.MachineId, out long lost) ? Math.Min(lost, now) : now;
        return reference - seen < _timing.EpisodeBreakAfterMs;
    }

    private bool CanRejoinLocked(string machineId)
    {
        if (_timing.SameMessageRejoinMs <= 0) return false;
        if (IsLongOfflineGapLocked(machineId)) return false;
        return !_redClearedSince.TryGetValue(machineId, out ObservationTime since)
            || since.Age(_clock).TotalMilliseconds < _timing.SameMessageRejoinMs;
    }

    /// <summary>
    /// Czy od ostatniego potwierdzenia czerwieni maszyna straciła łączność na co najmniej
    /// <see cref="TimingConfig.EpisodeBreakAfterMs"/> — ta sama granica co dla zniknięcia czerwieni. Przerwa jeszcze
    /// trwająca liczy się do teraz. Brak przerwy = <see langword="false"/>.
    /// </summary>
    private bool IsLongOfflineGapLocked(string machineId)
    {
        if (!_episodeBrokenByOffline.TryGetValue(machineId, out OfflineGap gap)) return false;
        long end = gap.EndMs ?? _clock.MonotonicMs;
        return end - gap.StartMs >= _timing.EpisodeBreakAfterMs;
    }

    private Alert? RejoinRetiredEpisodeLocked(
        MachineConfig machine, MappedMessage message, AlertKey key, string? fullText,
        string? captureFile, DateTimeOffset now, List<AlertEvent> events, ObservationTime? lastRed = null)
    {
        if (!_rejoinable.TryGetValue(key, out RetiredEpisode? retired)) return null;
        if (retired.ClosedAt.Age(_clock).TotalMilliseconds >= _timing.SameMessageRejoinMs)
        {
            _rejoinable.Remove(key);
            return null;
        }

        if (!CanRejoinLocked(machine.Id)) return null;

        double breakSeconds = RedBreakSecondsLocked(machine.Id, retired.ClosedAt);
        bool newIdentity = _groupProtocol;
        Alert alert = new()
        {
            EpisodeId = newIdentity ? Guid.NewGuid() : retired.EpisodeId,
            SupersededEpisodeIds = newIdentity
                ? retired.SupersededEpisodeIds.Prepend(retired.EpisodeId).Distinct().Take(MaxSupersededEpisodeIds).ToArray()
                : [],
            ReconciledAt = newIdentity ? retired.ReconciledAt : null,
            Key = key,
            EpisodeKeys = _acks.EpisodeKeys(key),
            MachineId = machine.Id,
            MachineName = machine.Name,
            Kind = AlertKind.Error,
            ShortText = message.ShortText,
            FullText = fullText,
            Category = message.Category,
            Priority = message.Priority,
            StartedAt = retired.StartedAt,
            LastSeenAt = Observe(key, lastRed ?? ObservationTime.Read(_clock)),
            Status = retired.Status == AlertStatus.Closed ? AlertStatus.Active : retired.Status,
            AcknowledgedAt = retired.AcknowledgedAt,
            AckedByHost = retired.AckedByHost,
            AckedByUser = retired.AckedByUser,
            EscalatedAt = retired.EscalatedAt,
            CaptureFile = retired.CaptureFile ?? captureFile,
            Sound = true,
        };

        _rejoinable.Remove(key);
        _alerts[key] = alert;
        _acks.Revive(key);
        _acks.LinkEpisode(key, alert.EpisodeKeys);
        foreach (var alias in alert.EpisodeKeys) _episodeAliases[alias] = key;
        _episodeAliases[key] = key;
        if (retired.AcknowledgedMs is { } at)
            _acknowledgedMs[key] = at;

        string reason = $"ten sam komunikat wrócił po przerwie {breakSeconds:0.#} s — epizod połączony, bez nowego dźwięku"
            + (newIdentity ? " (grupa: nowy identyfikator epizodu, poprzedni zastąpiony)" : "");
        _logger.LogInformation(
            "Maszyna '{MachineId}': {Reason} (alert '{ShortText}', status {Status}).",
            machine.Id, reason, alert.ShortText, alert.Status);
        events.Add(new AlertEvent(AlertEventKind.Updated, alert) { Reason = reason });
        return alert;
    }

    private string? RaiseReasonLocked(string machineId, AlertKey key)
    {
        string? cause = null;
        if (_rejoinable.TryGetValue(key, out RetiredEpisode? sameKey))
        {
            cause = $"przerwa {RedBreakSecondsLocked(machineId, sameKey.ClosedAt):0.#} s";
        }
        else
        {
            foreach (KeyValuePair<AlertKey, RetiredEpisode> entry in _rejoinable)
            {
                if (!string.Equals(entry.Key.MachineId, machineId, StringComparison.Ordinal)) continue;
                cause = $"inny klucz po przerwie {RedBreakSecondsLocked(machineId, entry.Value.ClosedAt):0.#} s "
                    + $"(poprzednio {entry.Key.Discriminator})";
                break;
            }
        }

        return Combine(cause, UnobservedNoteLocked(machineId));
    }

    /// <summary>Jak długo (w sekundach) nie było czerwieni: od pierwszego zniknięcia, a gdy nieznane — od zamknięcia epizodu.</summary>
    private double RedBreakSecondsLocked(string machineId, ObservationTime closedAt) =>
        (_redClearedSince.TryGetValue(machineId, out ObservationTime since) ? since.Age(_clock) : closedAt.Age(_clock))
            .TotalSeconds;

    /// <summary>Dopisek o tym, że alert nie mógł się zamknąć, bo obserwacja ekranu HMI jest nierozstrzygnięta.</summary>
    private string? UnobservedNoteLocked(string machineId) =>
        _unobservedMachines.Contains(machineId) && _unobservedSince.TryGetValue(machineId, out DateTimeOffset since)
            ? $"nierozstrzygnięty ekran blokował zamknięcie od {since.ToLocalTime():HH:mm}"
            : null;

    private static string? Combine(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "; " + second;

    /// <summary>
    /// Odtwarza z alertu <see cref="Alert"/> (który nie niesie oryginalnego <see cref="MappedMessage"/>)
    /// wystarczająco dużo, żeby porównać go przez <see cref="MappedMessage.IsBetterThan"/> z NOWYM
    /// komunikatem w <see cref="OnRedConfirmed"/>: dopasowanie reguły ⇔ dyskryminator zaczyna się od
    /// <c>"rule:"</c>, <see cref="MappedMessage.Priority"/> z <see cref="Alert.Priority"/>, "niepustość"
    /// tekstu ⇔ dyskryminator inny niż <c>"text:empty"</c>, a <see cref="MappedMessage.AlarmNumber"/> ⇔
    /// dyskryminator zaczynający się od <c>"alarm:"</c> (numer po dwukropku — patrz <see cref="AlertKey"/>).
    /// Wynikowy <see cref="MappedMessage.RuleId"/> i <see cref="MappedMessage.NormalizedText"/> są tylko
    /// atrapami (nieużywanymi przez <c>IsBetterThan</c>) — ta metoda służy WYŁĄCZNIE do tego porównania,
    /// nie do niczego innego.
    /// </summary>
    private static MappedMessage AsMappedMessage(Alert alert)
    {
        string discriminator = alert.Key.Discriminator;
        bool matched = IsRuleDiscriminator(discriminator);
        bool emptyText = discriminator == EmptyTextDiscriminator;
        string? alarmNumber = discriminator.StartsWith(AlarmDiscriminatorPrefix, StringComparison.Ordinal)
            ? discriminator[AlarmDiscriminatorPrefix.Length..]
            : null;
        return new MappedMessage(
            RuleId: matched ? "(stara-reguła)" : null,
            ShortText: alert.ShortText,
            Category: alert.Category,
            RawText: alert.FullText ?? string.Empty,
            NormalizedText: emptyText ? string.Empty : "x",
            Matched: matched)
        {
            Priority = alert.Priority,
            AlarmNumber = alarmNumber,
        };
    }

    /// <summary>
    /// Zamyka od razu (jak przeterminowanie w <see cref="Tick"/>) niezamknięty alert błędu innej maszyny/
    /// klucza niż ten właśnie potwierdzany — wołane z <see cref="OnRedConfirmed"/>, gdy epizod był
    /// przerwany (<see cref="IsEpisodeBrokenLocked"/>) i stary błąd jest w istocie już nieaktualny (maszyna
    /// pokazuje NOWY błąd po restarcie).
    /// </summary>
    private void CloseUnclosedErrorLocked(Alert alert, DateTimeOffset now, List<AlertEvent> events)
    {
        Alert closed = alert with { Status = AlertStatus.Closed, ClosedAt = now };
        _alerts.Remove(alert.Key);
        RetireEpisode(alert, now); // koniec epizodu — ACK ważne tylko "do zamknięcia lub 2h"
        AddToHistory(closed);
        events.Add(new AlertEvent(AlertEventKind.Closed, closed));
    }

    /// <summary>
    /// Zamyka niezamknięty alert <see cref="AlertKind.MachineIdle"/> danej maszyny, jeśli istnieje — dokładnie
    /// tą samą procedurą co pozostałe ścieżki auto-zamknięcia (<see cref="AlertStatus.Closed"/>, historia,
    /// wygaszenie ACK przez <see cref="RetireEpisode"/>, zdarzenie <see cref="AlertEventKind.Closed"/> dopisane do <paramref name="events"/>,
    /// publikowane poza blokadą przez wywołującego). Wspólna dla trzech miejsc: maszyna ruszyła
    /// (<c>!atFull</c>), maszyna ma otwarty błąd (<see cref="OnProgressState"/>) i powstanie/odświeżenie
    /// błędu (<see cref="OnRedConfirmed"/>). Zwraca <see langword="true"/>, jeśli faktycznie coś zamknęła.
    /// </summary>
    private bool CloseIdleAlertLocked(string machineId, DateTimeOffset now, List<AlertEvent> events) =>
        CloseAlertOfKindLocked(AlertKey.Idle(machineId), now, events);

    /// <summary>
    /// Zamyka niezamknięty alert o kluczu <paramref name="key"/> (postój albo niewłaściwy ekran HMI — alerty
    /// „jednego klucza na maszynę”), jeśli istnieje — tą samą procedurą co auto-zamknięcie w <see cref="Tick"/>.
    /// Zwraca <see langword="true"/>, jeśli faktycznie coś zamknęła.
    /// </summary>
    private bool CloseAlertOfKindLocked(AlertKey key, DateTimeOffset now, List<AlertEvent> events)
    {
        if (_alerts.TryGetValue(key, out Alert? existing) && existing.Status != AlertStatus.Closed)
        {
            Alert closed = existing with { Status = AlertStatus.Closed, ClosedAt = now };
            _alerts.Remove(key);
            RetireEpisode(existing, now);
            AddToHistory(closed);
            events.Add(new AlertEvent(AlertEventKind.Closed, closed));
            return true;
        }

        return false;
    }

    private Alert UpdateSameKey(
        Alert existing, MachineConfig machine, MappedMessage message, string? fullText,
        string? captureFile, DateTimeOffset now, List<AlertEvent> events)
    {
        bool textChanged = existing.ShortText != message.ShortText || existing.FullText != fullText;
        Alert updated = existing with
        {
            MachineName = machine.Name,
            ShortText = message.ShortText,
            FullText = fullText,
            Category = message.Category,
            Priority = message.Priority,
            LastSeenAt = Observe(existing.Key, now),
            CaptureFile = existing.CaptureFile ?? captureFile,
        };
        _alerts[updated.Key] = updated;
        if (textChanged)
        {
            events.Add(new AlertEvent(AlertEventKind.Updated, updated));
        }

        return updated;
    }

    private void RetireEpisode(Alert alert, DateTimeOffset now)
    {
        TimeSpan rejoin = alert.Kind == AlertKind.Error && _timing.SameMessageRejoinMs > 0
            ? TimeSpan.FromMilliseconds(_timing.SameMessageRejoinMs)
            : TimeSpan.Zero;

        // Zachowaj faktycznie używany zegar ACK, zanim AddToHistory usunie aktywny licznik.
        long? acknowledgedMs = _acknowledgedMs.TryGetValue(alert.Key, out long at)
            ? at : _acks.RecordedAtMs(alert.Key);
        _acks.Retire(alert.Key, now, rejoin);
        foreach (var alias in _episodeAliases.Where(p => p.Value == alert.Key).Select(p => p.Key).ToArray())
        {
            if (alias != alert.Key) _acks.Retire(alias, now);
            _episodeAliases.Remove(alias);
        }

        if (rejoin > TimeSpan.Zero)
        {
            _rejoinable[alert.Key] = new RetiredEpisode(
                alert.EpisodeId, alert.StartedAt, alert.Status, alert.AcknowledgedAt, acknowledgedMs,
                alert.AckedByHost, alert.AckedByUser, alert.CaptureFile,
                new ObservationTime(now, _clock.MonotonicMs), alert.EscalatedAt,
                alert.SupersededEpisodeIds, alert.ReconciledAt);
        }
    }

    private Alert SwapKey(
        Alert other, AlertKey newKey, MachineConfig machine, MappedMessage message, string? fullText,
        string? captureFile, DateTimeOffset now, List<AlertEvent> events)
    {
        // Stary klucz "znika" bez zdarzenia Closed — to nadal ten sam epizod, tylko lepiej rozpoznany.
        _alerts.Remove(other.Key);
        _observedMs.Remove(other.Key);
        if (_acknowledgedMs.Remove(other.Key, out long acknowledgedMs))
            _acknowledgedMs[newKey] = acknowledgedMs;
        foreach (var alias in _episodeAliases.Where(p => p.Value == other.Key).Select(p => p.Key).ToArray())
            _episodeAliases[alias] = newKey;
        _episodeAliases[other.Key] = newKey;
        _episodeAliases[newKey] = newKey;
        _acks.LinkEpisode(newKey, other.EpisodeKeys.Append(other.Key));

        Alert swapped = new()
        {
            EpisodeId = other.EpisodeId,
            SupersededEpisodeIds = other.SupersededEpisodeIds,
            ReconciledAt = other.ReconciledAt,
            Key = newKey,
            EpisodeKeys = _acks.EpisodeKeys(newKey),
            MachineId = machine.Id,
            MachineName = machine.Name,
            Kind = AlertKind.Error,
            ShortText = message.ShortText,
            FullText = fullText,
            Category = message.Category,
            Priority = message.Priority,
            StartedAt = other.StartedAt,
            LastSeenAt = Observe(newKey, now),
            Status = other.Status,
            AcknowledgedAt = other.AcknowledgedAt,
            AckedByHost = other.AckedByHost,
            AckedByUser = other.AckedByUser,
            EscalatedAt = other.EscalatedAt,
            ClosedAt = null,
            CaptureFile = other.CaptureFile ?? captureFile,
            Sound = true,
        };
        _alerts[newKey] = swapped;
        events.Add(new AlertEvent(AlertEventKind.Updated, swapped));
        return swapped;
    }

    private Alert CreateNewKeyAlert(
        MachineConfig machine, MappedMessage message, AlertKey key, string? fullText,
        string? captureFile, DateTimeOffset now, List<AlertEvent> events,
        ObservationTime? firstRed = null, ObservationTime? lastRed = null)
    {
        AckRecord? ack = _groupProtocol ? null : _acks.Get(key);
        Alert alert = new()
        {
            Key = key,
            EpisodeKeys = _acks.EpisodeKeys(key),
            MachineId = machine.Id,
            MachineName = machine.Name,
            Kind = AlertKind.Error,
            ShortText = message.ShortText,
            FullText = fullText,
            Category = message.Category,
            Priority = message.Priority,
            StartedAt = firstRed?.Utc ?? (ack is not null && ack.At < now ? ack.At : now),
            LastSeenAt = Observe(key, lastRed ?? ObservationTime.Read(_clock)),
            Status = ack is not null ? AlertStatus.Acknowledged : AlertStatus.Active,
            AcknowledgedAt = ack?.At,
            AckedByHost = ack?.Host,
            AckedByUser = ack?.User,
            CaptureFile = captureFile,
            Sound = true,
        };
        _acks.LinkEpisode(key, alert.EpisodeKeys);
        foreach (var alias in alert.EpisodeKeys) _episodeAliases[alias] = key;
        _episodeAliases[key] = key;
        _alerts[key] = alert;
        AlertEventKind kind = ack is not null ? AlertEventKind.Updated : AlertEventKind.Raised;
        string? reason = RaiseReasonLocked(machine.Id, key);
        _rejoinable.Remove(key);   // ten epizod jest nowy — stary wpis nie ma już czego wznawiać
        if (kind == AlertEventKind.Raised && reason is not null)
        {
            _logger.LogInformation(
                "Maszyna '{MachineId}': nowy alarm '{ShortText}' mimo niedawnego alertu tej maszyny — powód: {Reason}.",
                machine.Id, alert.ShortText, reason);
        }

        // Gdy AckMemory już zna ten klucz, nie dzwonimy (Raised) — tylko Updated, żeby UI dodało wpis bez dźwięku.
        events.Add(new AlertEvent(kind, alert) { Reason = reason });
        return alert;
    }

    private static string? NormalizeFullText(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return null;
        }

        string trimmed = rawText.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsRuleDiscriminator(string discriminator) =>
        discriminator.StartsWith(RuleDiscriminatorPrefix, StringComparison.Ordinal);

    private static List<Alert> OrderForDisplay(IEnumerable<Alert> alerts) =>
        alerts
            .OrderBy(a => a.Status is AlertStatus.Active or AlertStatus.Escalated ? 0 : 1)
            .ThenBy(a => a.StartedAt)
            .ThenBy(a => a.Key.MachineId, StringComparer.Ordinal)
            .ThenBy(a => a.Key.Discriminator, StringComparer.Ordinal)
            .ToList();
}
