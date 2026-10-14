namespace MaszynyAlert.Core.Cluster;

public sealed class WatcherOverlapTracker
{
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Przerwa między odczytami dłuższa niż ta (zwykle przychodzą co sekundę) = program nie pracował (uśpienie, zawieszenie)
    /// — liczenie od nowa, żeby skok zegara monotonicznego nie „odliczył” 60 s od razu. Wykryte uśpienie i przełączenie
    /// grupy zerują też jawnie (<see cref="Reset"/>).
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private long? _since;
    private long? _lastUpdate;
    private IReadOnlyList<string> _confirmed = [];

    /// <summary>Obserwatorzy z ostatniego odczytu, gdy nakładanie się jest potwierdzone; inaczej pusta lista.</summary>
    public IReadOnlyList<string> Confirmed { get { lock (_gate) return _confirmed; } }

    /// <param name="watchers">Obserwatorzy widziani teraz, łącznie z tym komputerem.</param>
    /// <param name="monotonicMs">Czas z <see cref="Abstractions.IClock.MonotonicMs"/>.</param>
    public IReadOnlyList<string> Update(IReadOnlyList<string> watchers, long monotonicMs)
    {
        ArgumentNullException.ThrowIfNull(watchers);
        lock (_gate)
        {
            if (_lastUpdate is { } last && monotonicMs - last > (long)MaxGap.TotalMilliseconds) _since = null;
            _lastUpdate = monotonicMs;
            if (watchers.Count <= 1)
            {
                _since = null;
                _confirmed = [];
            }
            else
            {
                _since ??= monotonicMs;
                _confirmed = monotonicMs - _since.Value >= (long)ConfirmAfter.TotalMilliseconds ? watchers.ToArray() : [];
            }
            return _confirmed;
        }
    }

    /// <summary>Liczenie od nowa — po wykrytym uśpieniu i po przełączeniu grupy.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _since = null;
            _lastUpdate = null;
            _confirmed = [];
        }
    }
}
