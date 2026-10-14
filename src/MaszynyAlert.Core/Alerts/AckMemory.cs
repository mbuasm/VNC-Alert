using MaszynyAlert.Core.Abstractions;

namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Pamięć potwierdzeń (ACK) alertów, niezależna od tego, który komputer aktualnie jest obserwatorem —
/// dzięki niej nowy obserwator (po przejęciu roli) nie wznawia alertów, które ktoś już potwierdził.
/// Wątkowo bezpieczna: używana zarówno z wątku UI, jak i z wątku odbierającego pakiety sieciowe.
/// </summary>
public sealed class AckMemory
{
    public void Clear()
    {
        lock (_lock)
        {
            _acks.Clear(); _receivedMs.Clear(); _aliases.Clear(); _closedThrough.Clear(); _rejoinUntilMs.Clear();
        }
    }
    private readonly IClock _clock;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<AlertKey, AckRecord> _acks = new();
    private readonly Dictionary<AlertKey, long> _receivedMs = new();
    private readonly object _lock = new();
    private readonly Dictionary<AlertKey, AlertKey> _aliases = new();
    private readonly Dictionary<AlertKey, ObservationTime> _closedThrough = new();

    /// <summary>
    /// Potwierdzenia epizodów ZAMKNIĘTYCH, ale jeszcze „do wygaszenia” — patrz
    /// <see cref="Retire(AlertKey, DateTimeOffset, TimeSpan)"/> i <c>TimingConfig.SameMessageRejoinMs</c>.
    /// Wartość to chwila (czas monotoniczny), po której wpis przestaje obowiązywać.
    /// </summary>
    private readonly Dictionary<AlertKey, long> _rejoinUntilMs = new();

    public AckMemory(IClock clock, TimeSpan ttl)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ttl = ttl;
    }

    /// <summary>Zapamiętuje potwierdzenie — nadpisuje istniejący wpis tylko wtedy, gdy nowy jest równie świeży lub nowszy.</summary>
    public void Record(AckRecord ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        lock (_lock)
        {
            if (_closedThrough.TryGetValue(ack.Key, out var closed) && ack.At <= closed.Utc) return;
            if (_aliases.TryGetValue(ack.Key, out var canonical)) ack = ack with { Key = canonical };
            if (_closedThrough.TryGetValue(ack.Key, out closed) && ack.At <= closed.Utc) return;
            if (_acks.TryGetValue(ack.Key, out AckRecord? existing) && existing.At > ack.At)
            {
                return;
            }

            if (!_acks.TryGetValue(ack.Key, out var previous) || previous.At != ack.At)
                _receivedMs[ack.Key] = _clock.MonotonicMs - (long)Math.Max(0, (_clock.UtcNow - ack.At).TotalMilliseconds);
            _acks[ack.Key] = ack;
            _rejoinUntilMs.Remove(ack.Key);   // epizod znów żyje — wpis nie jest już „do wygaszenia”
        }
    }

    // Wyłącznie dla przycisku na lokalnym, istniejącym alercie. Korekta UTC nie może
    // unieważnić świadomego potwierdzenia operatora; bariery dla zdalnych ACK zostają.
    internal void RecordLocal(AckRecord ack)
    {
        lock (_lock)
        {
            if (_aliases.TryGetValue(ack.Key, out var canonical)) ack = ack with { Key = canonical };
            _acks[ack.Key] = ack;
            _receivedMs[ack.Key] = _clock.MonotonicMs;
            _rejoinUntilMs.Remove(ack.Key);
        }
    }

    /// <summary>
    /// Zwraca zapamiętane potwierdzenie dla klucza, albo <see langword="null"/>, gdy go nie ma. Wpis
    /// epizodu ZAMKNIĘTEGO, zachowany tylko na wypadek dołączenia tego samego komunikatu
    /// (<see cref="Retire(AlertKey, DateTimeOffset, TimeSpan)"/>), nie jest tu widoczny — nowy epizod
    /// nigdy nie dziedziczy potwierdzenia „po cichu”. Dołączenie prowadzi
    /// <c>AlertStateMachine</c>, jawną ścieżką, z zachowaniem <c>StartedAt</c> i identyfikatora epizodu.
    /// </summary>
    public AckRecord? Get(AlertKey key)
    {
        lock (_lock)
        {
            if (_aliases.TryGetValue(key, out var canonical)) key = canonical;
            if (Retired(key)) return null;
            return _acks.TryGetValue(key, out AckRecord? ack) ? ack : null;
        }
    }

    /// <summary>
    /// Epizod znów żyje (dołączył do niego ten sam komunikat) — wpis przestaje być „do wygaszenia”,
    /// zachowując swój pierwotny czas odbioru, żeby eskalacja liczyła się od prawdziwego potwierdzenia.
    /// </summary>
    internal void Revive(AlertKey key)
    {
        lock (_lock)
        {
            if (_aliases.TryGetValue(key, out var canonical)) key = canonical;
            if (!Retired(key)) _rejoinUntilMs.Remove(key);
        }
    }

    private bool Retired(AlertKey key)
    {
        if (!_rejoinUntilMs.TryGetValue(key, out long until)) return false;
        if (_clock.MonotonicMs < until) return true;
        _rejoinUntilMs.Remove(key);
        _acks.Remove(key);
        _receivedMs.Remove(key);
        return false;
    }

    internal long? RecordedAtMs(AlertKey key)
    {
        lock (_lock)
        {
            if (_aliases.TryGetValue(key, out var canonical)) key = canonical;
            return _receivedMs.TryGetValue(key, out long at) ? at : null;
        }
    }

    /// <summary>Czy dany epizod ma zapamiętane potwierdzenie.</summary>
    public bool IsAcked(AlertKey key)
    {
        lock (_lock)
        {
            return !Retired(key) && _acks.ContainsKey(key);
        }
    }

    /// <summary>
    /// Zapomina potwierdzenie danego klucza — wołane przy zamknięciu epizodu (patrz
    /// <c>AlertStateMachine.Tick</c>/<c>OnMachineOnline</c>), żeby kolejne wystąpienie TEGO SAMEGO błędu (ten
    /// sam <see cref="AlertKey"/>) nie powstało od razu jako potwierdzone, bez dźwięku, przez pozostały czas
    /// TTL. Bezpieczne, gdy klucz nie jest zapamiętany — wtedy nic nie robi.
    /// </summary>
    public void Forget(AlertKey key)
    {
        lock (_lock)
        {
            _acks.Remove(key);
            _receivedMs.Remove(key);
            _rejoinUntilMs.Remove(key);
        }
    }

    /// <summary>Kończy epizod i odrzuca późniejsze retransmisje jego potwierdzeń.</summary>
    public void Retire(AlertKey key, DateTimeOffset closedAt) => Retire(key, closedAt, TimeSpan.Zero);

    public void Retire(AlertKey key, DateTimeOffset closedAt, TimeSpan rejoinWindow)
    {
        lock (_lock)
        {
            if (_aliases.TryGetValue(key, out var canonical)) key = canonical;
            var keys = _aliases.Where(p => p.Value == key).Select(p => p.Key).Append(key).Distinct().ToArray();
            bool keepForRejoin = rejoinWindow > TimeSpan.Zero;
            foreach (var alias in keys)
            {
                if (keepForRejoin && alias == key && _acks.ContainsKey(alias))
                {
                    _rejoinUntilMs[alias] = _clock.MonotonicMs + (long)rejoinWindow.TotalMilliseconds;
                }
                else
                {
                    _acks.Remove(alias);
                    _receivedMs.Remove(alias);
                    _rejoinUntilMs.Remove(alias);
                }

                _aliases.Remove(alias);
                if (!_closedThrough.TryGetValue(alias, out var previous) || closedAt > previous.Utc)
                    _closedThrough[alias] = new ObservationTime(closedAt, _clock.MonotonicMs);
            }
        }
    }

    /// <summary>Zmiana kodu zachowuje jedną pamięć ACK; aliasy żyją tylko do końca epizodu.</summary>
    public void LinkEpisode(AlertKey current, IEnumerable<AlertKey> keys)
    {
        lock (_lock)
        {
            foreach (var key in keys.Append(current).Distinct().ToArray())
            {
                var previous = _aliases.GetValueOrDefault(key, key);
                var ack = _acks.GetValueOrDefault(previous);
                long received = _receivedMs.GetValueOrDefault(previous, _clock.MonotonicMs);
                foreach (var alias in _aliases.Where(p => p.Value == previous).Select(p => p.Key).ToArray())
                    _aliases[alias] = current;
                _aliases[key] = current;
                if (previous != current) { _acks.Remove(previous); _receivedMs.Remove(previous); }
                if (ack is not null)
                {
                    Record(ack with { Key = current });
                    if (_acks.TryGetValue(current, out var recorded) && recorded.At == ack.At)
                        _receivedMs[current] = received;
                }
            }
        }
    }

    public IReadOnlyList<AlertKey> EpisodeKeys(AlertKey key)
    {
        lock (_lock)
        {
            if (_aliases.TryGetValue(key, out var current)) key = current;
            return _aliases.Where(p => p.Value == key).Select(p => p.Key).Append(key).Distinct().ToArray();
        }
    }

    public bool IsRetired(AckRecord ack)
    {
        lock (_lock) return _closedThrough.TryGetValue(ack.Key, out var closed) && ack.At <= closed.Utc;
    }

    /// <summary>Usuwa wpisy starsze niż skonfigurowane TTL (według <see cref="IClock.MonotonicMs"/>).</summary>
    public void Prune()
    {
        long threshold = _clock.MonotonicMs - (long)_ttl.TotalMilliseconds;
        lock (_lock)
        {
            foreach (var key in _closedThrough.Where(p => p.Value.Milliseconds < threshold).Select(p => p.Key).ToArray())
                _closedThrough.Remove(key);
            foreach (var key in _rejoinUntilMs.Where(p => p.Value <= _clock.MonotonicMs).Select(p => p.Key).ToArray())
            {
                _rejoinUntilMs.Remove(key);
                _acks.Remove(key);
                _receivedMs.Remove(key);
            }

            List<AlertKey>? expired = null;
            foreach (KeyValuePair<AlertKey, AckRecord> entry in _acks)
            {
                if (_receivedMs.GetValueOrDefault(entry.Key, _clock.MonotonicMs) < threshold)
                {
                    (expired ??= new List<AlertKey>()).Add(entry.Key);
                }
            }

            if (expired is not null)
            {
                foreach (AlertKey key in expired)
                {
                    _acks.Remove(key);
                    _receivedMs.Remove(key);
                }
            }
        }
    }

    /// <summary>Wszystkie aktualnie zapamiętane potwierdzenia (migawka).</summary>
    public IReadOnlyCollection<AckRecord> All
    {
        get
        {
            lock (_lock)
            {
                return _acks.Where(p => !_rejoinUntilMs.ContainsKey(p.Key)).Select(p => p.Value).ToArray();
            }
        }
    }

    /// <summary>
    /// Potwierdzenia z ostatniego okna czasu monotonicznego (z początkowym wiekiem zdalnego ACK).
    /// do dołączenia w <c>HeartbeatMessage.RecentAcks</c>, żeby zgubiony pojedynczy pakiet ACK nie kazał
    /// obserwatorowi dzwonić bez końca. Wywołujący sam ogranicza liczbę wpisów w pakiecie.
    /// </summary>
    public IReadOnlyList<AckRecord> Recent(TimeSpan window)
    {
        long threshold = _clock.MonotonicMs - (long)window.TotalMilliseconds;
        lock (_lock)
        {
            return _acks.Values
                .Where(a => !_rejoinUntilMs.ContainsKey(a.Key)
                    && _receivedMs.GetValueOrDefault(a.Key, _clock.MonotonicMs) >= threshold)
                .ToList();
        }
    }
}
