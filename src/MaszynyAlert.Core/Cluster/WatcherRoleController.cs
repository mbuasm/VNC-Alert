using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Cluster;

public sealed class WatcherRoleController
{
    private readonly IClock _clock;
    private readonly ClusterConfig _cfg;
    private readonly string _selfHost;
    private readonly ILogger _logger;

    private long? _candidateSinceMs;
    private long? _lastEvaluateMonotonicMs;
    private bool _lastIsWatcher;
    private int _forceYieldPending;

    public WatcherRoleController(IClock clock, ClusterConfig cfg, string selfHost, ILogger? logger = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _selfHost = string.IsNullOrWhiteSpace(selfHost)
            ? throw new ArgumentException("Nazwa lokalnego komputera nie może być pusta.", nameof(selfHost))
            : selfHost;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Wymusza, żeby najbliższe <see cref="Evaluate"/> zachowało się tak, jakby wykryło uśpienie komputera
    /// (natychmiastowe ustąpienie z roli obserwatora, histereza liczona od nowa). Aplikacja wywołuje to po
    /// systemowym zdarzeniu wybudzenia (<c>PowerModeChanged.Resume</c>) — stare połączenia VNC po wybudzeniu
    /// mogą być martwe, więc trzeba je nawiązać ponownie.
    /// </summary>
    public void ForceYield() => Interlocked.Exchange(ref _forceYieldPending, 1);

    public RoleDecision Evaluate(IReadOnlyList<PeerInfo> alivePeers, PeerInfo self)
    {
        ArgumentNullException.ThrowIfNull(alivePeers);
        ArgumentNullException.ThrowIfNull(self);

        long nowMs = _clock.MonotonicMs;
        bool forceYield = Interlocked.Exchange(ref _forceYieldPending, 0) == 1;
        bool sleepDetected = forceYield;

        if (!sleepDetected && _lastEvaluateMonotonicMs is { } lastMs)
        {
            long delta = nowMs - lastMs;
            if (delta > _cfg.SleepJumpThresholdMs)
            {
                sleepDetected = true;
                _logger.LogInformation(
                    "Wykryto skok zegara monotonicznego o {DeltaMs} ms (próg {ThresholdMs} ms) — komputer był prawdopodobnie " +
                    "uśpiony; oddaję rolę obserwatora i łączę się z maszynami od nowa.",
                    delta, _cfg.SleepJumpThresholdMs);
            }
        }

        _lastEvaluateMonotonicMs = nowMs;

        bool isWatcher;
        if (sleepDetected)
        {
            _candidateSinceMs = null;
            isWatcher = false;
        }
        else
        {
            var candidates = new List<PeerInfo>(alivePeers.Count + 1);
            candidates.AddRange(alivePeers);
            candidates.Add(self);
            string? candidateHost = Election.ChooseWatcher(candidates);

            if (!string.Equals(candidateHost, _selfHost, StringComparison.Ordinal))
            {
                // Kandydatem jest ktoś inny (albo lepszy peer się pojawił) — ustępujemy natychmiast.
                _candidateSinceMs = null;
                isWatcher = false;
            }
            else
            {
                _candidateSinceMs ??= nowMs;
                isWatcher = (nowMs - _candidateSinceMs.Value) >= _cfg.TakeoverDelayMs;
            }
        }

        string? watcherHost;
        if (isWatcher)
        {
            watcherHost = _selfHost;
        }
        else
        {
            List<PeerInfo> declaredWatchers = alivePeers.Where(p => p.Role == PeerRole.Watcher).ToList();
            watcherHost = declaredWatchers.Count == 0 ? null : Election.ChooseWatcher(declaredWatchers);
        }

        if (isWatcher != _lastIsWatcher)
        {
            _logger.LogInformation(isWatcher
                ? "Ten komputer przejmuje rolę obserwatora."
                : "Ten komputer przestaje być obserwatorem.");
        }

        _lastIsWatcher = isWatcher;
        return new RoleDecision(isWatcher, watcherHost, watcherHost is null, sleepDetected);
    }
}
