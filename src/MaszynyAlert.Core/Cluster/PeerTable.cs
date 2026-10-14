using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Cluster;

public sealed class PeerTable
{
    private static readonly TimeSpan PruneAfter = TimeSpan.FromMinutes(10);
    private const long RestartSeqDropThreshold = 1000;

    private readonly IClock _clock;
    private readonly ClusterConfig _cfg;
    private readonly ILogger _logger;
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Host, string Instance), DateTimeOffset> _retiredInstances = new();
    // Pakiet z zegarem nadawcy na górnej granicy tolerancji może być ważny przez 2 × tolerancja.
    private TimeSpan ReplayWindow => TimeSpan.FromSeconds(Math.Max(60, 2d * _cfg.ClockSkewToleranceSec));
    private readonly object _lock = new();

    public PeerTable(IClock clock, ClusterConfig cfg, ILogger? logger = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Zapisuje odebrany heartbeat. Zwraca <see langword="false"/>, gdy pakiet wygląda na powtórzony/odtworzony:
    /// ten sam host jest już znany, a jego <c>Seq</c> nie jest większy niż ostatnio zapamiętany i ostatni
    /// heartbeat od niego widziano w oknie co najmniej 2 × tolerancja zegara (minimum 60 s).
    /// Nowy <see cref="PeerMessage.InstanceId"/> resetuje numerację; poprzednia instancja jest blokowana
    /// na to samo okno, żeby jej opóźniony pakiet nie cofnął restartu. Spadek Seq o więcej niż 1000
    /// oznacza restart tylko dla starszych nadawców bez InstanceId.
    /// </summary>
    public bool Observe(HeartbeatMessage hb)
    {
        ArgumentNullException.ThrowIfNull(hb);
        DateTimeOffset now = _clock.UtcNow;

        lock (_lock)
        {
            if (IsRetiredInstance(hb.Sender, hb.InstanceId)) return false;
            if (_peers.TryGetValue(hb.Sender, out PeerInfo? existing))
            {
                bool instanceChanged = !string.Equals(existing.InstanceId, hb.InstanceId, StringComparison.Ordinal);
                if (instanceChanged)
                {
                    if (!string.IsNullOrEmpty(existing.InstanceId))
                        _retiredInstances[(hb.Sender, existing.InstanceId)] = now + ReplayWindow;
                    _logger.LogInformation(
                        "Komputer \"{Host}\" wygląda na zrestartowany (nowy identyfikator instancji) — resetuję numerację pakietów.",
                        hb.Sender);
                }
                else if (now - existing.LastSeen <= ReplayWindow && hb.Seq <= existing.LastSeq)
                {
                    long drop = existing.LastSeq - hb.Seq;
                    if (!string.IsNullOrEmpty(hb.InstanceId) || drop <= RestartSeqDropThreshold)
                    {
                        return false;
                    }

                    _logger.LogInformation(
                        "Komputer \"{Host}\" wygląda na zrestartowany (numer sekwencyjny spadł z {Old} na {New}) — traktuję jako nowy start.",
                        hb.Sender, existing.LastSeq, hb.Seq);
                }
            }

            _peers[hb.Sender] = new PeerInfo(
                hb.Sender, hb.Priority, hb.Role, hb.Version, now, hb.Seq, hb.BelievedWatcher, hb.MachinesConnected,
                hb.MachinesTotal, hb.InstanceId);
            return true;
        }
    }

    internal bool IsRetiredInstance(string host, string instance)
    {
        lock (_lock)
            return _retiredInstances.TryGetValue((host, instance), out var until) && _clock.UtcNow <= until;
    }

    /// <summary>Wszyscy znani komputerowie (migawka listy w chwili odczytu).</summary>
    public IReadOnlyList<PeerInfo> All
    {
        get
        {
            lock (_lock)
            {
                return _peers.Values.ToList();
            }
        }
    }

    /// <summary>Komputery uznawane za "żywe" — heartbeat odebrany w oknie <c>ClusterConfig.PeerTimeoutMs</c>.</summary>
    public IReadOnlyList<PeerInfo> Alive()
    {
        DateTimeOffset now = _clock.UtcNow;
        TimeSpan timeout = TimeSpan.FromMilliseconds(_cfg.PeerTimeoutMs);
        lock (_lock)
        {
            return _peers.Values.Where(p => p.IsAlive(now, timeout)).ToList();
        }
    }

    /// <summary>Usuwa jeden host z tablicy (np. po jawnym zamknięciu programu na tym komputerze).</summary>
    public void Forget(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_lock)
        {
            _peers.Remove(host);
        }
    }

    /// <summary>Usuwa hosty po 10 minutach lub po dłuższym oknie anty-replay.</summary>
    public void Prune()
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset threshold = now - (ReplayWindow > PruneAfter ? ReplayWindow : PruneAfter);
        lock (_lock)
        {
            foreach (var key in _retiredInstances.Where(p => p.Value < now).Select(p => p.Key).ToList())
                _retiredInstances.Remove(key);
            List<string>? stale = null;
            foreach (KeyValuePair<string, PeerInfo> entry in _peers)
            {
                if (entry.Value.LastSeen < threshold)
                {
                    (stale ??= new List<string>()).Add(entry.Key);
                }
            }

            if (stale is not null)
            {
                foreach (string host in stale)
                {
                    _peers.Remove(host);
                }
            }
        }
    }
}
