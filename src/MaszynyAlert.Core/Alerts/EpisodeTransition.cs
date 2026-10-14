namespace MaszynyAlert.Core.Alerts;

/// <summary>Ordering of transitions within one v2 episode. A fresh observation is not a transition.</summary>
internal static class EpisodeTransition
{
    public static bool IsNewer(Alert incoming, Alert current)
    {
        var comparison = At(incoming).CompareTo(At(current));
        return comparison > 0 || comparison == 0 && Rank(incoming) > Rank(current);
    }

    internal static DateTimeOffset At(Alert alert) => new[] { alert.StartedAt,
        alert.AcknowledgedAt ?? DateTimeOffset.MinValue, alert.EscalatedAt ?? DateTimeOffset.MinValue,
        alert.ReconciledAt ?? DateTimeOffset.MinValue }.Max();

    // On a timestamp tie keep an escalation ringing; only a later ACK can silence it.
    private static int Rank(Alert alert) => alert.Status == AlertStatus.Active && alert.ReconciledAt == At(alert)
        ? 3 : alert.Status switch
    {
        AlertStatus.Escalated => 2, AlertStatus.Acknowledged => 1, _ => 0
    };
}
