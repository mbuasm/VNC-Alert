namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Dźwięk na początku epizodu i co zadany odstęp, sterowany czasem monotonicznym. Odstęp jest czytany
/// przy każdym sprawdzeniu, więc zmiana konfiguracji po zastosowaniu ustawień grupy działa bez nowego okna.
/// </summary>
public sealed class AlertSoundSchedule(Func<long> repeatMs)
{
    public AlertSoundSchedule(long repeatMs) : this(() => repeatMs) { }

    private HashSet<(string Machine, DateTimeOffset Started, DateTimeOffset? Escalated)> _ringing = [];
    private long? _lastPlayedMs;

    public bool HasRingingAlerts => _ringing.Count > 0;

    public bool Update(IEnumerable<Alert> alerts, long nowMs)
    {
        var ringing = alerts
            .Where(a => AlertVisibility.IsVisibleInAlertWindow(a) && a.Sound)
            .Select(a => (a.MachineId, a.StartedAt, a.EscalatedAt))
            .ToHashSet();
        bool newEpisode = ringing.Except(_ringing).Any();
        _ringing = ringing;
        if (ringing.Count == 0)
        {
            _lastPlayedMs = null;
            return false;
        }

        if (newEpisode || _lastPlayedMs is null || nowMs - _lastPlayedMs.Value >= repeatMs())
        {
            _lastPlayedMs = nowMs;
            return true;
        }

        return false;
    }
}
