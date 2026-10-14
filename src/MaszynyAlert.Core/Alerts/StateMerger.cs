namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Scala listy alertów odebrane od (potencjalnie kilku) obserwatorów w jedną spójną listę — używane przez
/// każdy komputer do wyliczenia tego, co pokazać w oknie alertu. Chwilowe dwa obserwatory (np. partycja sieci)
/// są nieszkodliwe: ten sam klucz od dwóch nadawców po prostu się deduplikuje.
/// </summary>
public static class StateMerger
{
    /// <summary>
    /// Deduplikuje po <see cref="Alert.Key"/>: przy konflikcie wygrywa wyższy status w rankingu
    /// Closed(0) &lt; Active(1) &lt; {Acknowledged, Escalated}(2) — Acknowledged i Escalated są sobie równe w
    /// randze. Między
    /// Acknowledged i Escalated wygrywa ten z NOWSZYM znacznikiem ostatniego przejścia
    /// (<c>max(AcknowledgedAt, EscalatedAt)</c>); w pozostałych przypadkach równej rangi wygrywa nowszy
    /// <c>LastSeenAt</c>. <c>StartedAt</c> wyniku to minimum ze wszystkich wystąpień klucza. Jeśli
    /// <paramref name="acks"/> zna dany klucz, a wynikowy alert jest Active — zostaje oznaczony jako
    /// Acknowledged danymi z <see cref="AckMemory"/>, ale TYLKO gdy potwierdzenie nie jest przestarzałe
    /// (<c>ack.At &gt;= alert.StartedAt</c>, żeby nie potwierdzać nowego epizodu ACK-iem sprzed jego startu)
    /// i nie jest starsze niż ewentualna eskalacja (<c>alert.EscalatedAt is null || ack.At &gt; EscalatedAt</c>) —
    /// alerty już Acknowledged/Escalated nie są nadpisywane z <see cref="AckMemory"/>, bo mają świeższe własne dane.
    /// Zamknięte alerty są pomijane w wyniku. Wynik jest posortowany tak samo jak
    /// <see cref="AlertStateMachine.ActiveAlerts"/>.
    /// </summary>
    public static IReadOnlyList<Alert> Merge(IEnumerable<IReadOnlyList<Alert>> statesFromWatchers, AckMemory acks)
    {
        ArgumentNullException.ThrowIfNull(statesFromWatchers);
        ArgumentNullException.ThrowIfNull(acks);

        var best = new Dictionary<AlertKey, Alert>();
        var minStarted = new Dictionary<AlertKey, DateTimeOffset>();

        foreach (IReadOnlyList<Alert> state in statesFromWatchers)
        {
            if (state is null)
            {
                continue;
            }

            foreach (Alert alert in state)
            {
                if (minStarted.TryGetValue(alert.Key, out DateTimeOffset currentMin))
                {
                    if (alert.StartedAt < currentMin)
                    {
                        minStarted[alert.Key] = alert.StartedAt;
                    }
                }
                else
                {
                    minStarted[alert.Key] = alert.StartedAt;
                }

                if (!best.TryGetValue(alert.Key, out Alert? current))
                {
                    best[alert.Key] = alert;
                    continue;
                }

                int currentRank = StatusRank(current.Status);
                int candidateRank = StatusRank(alert.Status);
                if (candidateRank > currentRank)
                {
                    best[alert.Key] = alert;
                }
                else if (candidateRank == currentRank)
                {
                    bool candidateWins = currentRank == AckedOrEscalatedRank
                        ? LastTransition(alert) > LastTransition(current)
                        : alert.LastSeenAt > current.LastSeenAt;

                    if (candidateWins)
                    {
                        best[alert.Key] = alert;
                    }
                }
            }
        }

        var result = new List<Alert>(best.Count);
        foreach (KeyValuePair<AlertKey, Alert> entry in best)
        {
            Alert alert = entry.Value;
            if (alert.StartedAt != minStarted[entry.Key])
            {
                alert = alert with { StartedAt = minStarted[entry.Key] };
            }

            if (alert.Status == AlertStatus.Closed)
            {
                continue;
            }

            if (alert.Status == AlertStatus.Active)
            {
                AckRecord? ack = acks.Get(alert.Key);
                if (ack is not null && ack.At >= alert.StartedAt && (alert.EscalatedAt is null || ack.At > alert.EscalatedAt))
                {
                    alert = alert with
                    {
                        Status = AlertStatus.Acknowledged,
                        AcknowledgedAt = ack.At,
                        AckedByHost = ack.Host,
                        AckedByUser = ack.User,
                    };
                }
            }

            result.Add(alert);
        }

        return result
            .OrderBy(a => a.Status is AlertStatus.Active or AlertStatus.Escalated ? 0 : 1)
            .ThenBy(a => a.StartedAt)
            .ThenBy(a => a.Key.MachineId, StringComparer.Ordinal)
            .ThenBy(a => a.Key.Discriminator, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Wspólna ranga Acknowledged/Escalated w <see cref="StatusRank"/> — patrz doc <see cref="Merge"/>.</summary>
    private const int AckedOrEscalatedRank = 2;

    private static int StatusRank(AlertStatus status) => status switch
    {
        AlertStatus.Closed => 0,
        AlertStatus.Active => 1,
        AlertStatus.Acknowledged or AlertStatus.Escalated => AckedOrEscalatedRank,
        _ => -1,
    };

    /// <summary>Znacznik czasu ostatniego przejścia stanu potwierdzonego/eskalowanego alertu — <c>max(AcknowledgedAt, EscalatedAt)</c>.</summary>
    private static DateTimeOffset LastTransition(Alert alert)
    {
        DateTimeOffset acked = alert.AcknowledgedAt ?? DateTimeOffset.MinValue;
        DateTimeOffset escalated = alert.EscalatedAt ?? DateTimeOffset.MinValue;
        return acked > escalated ? acked : escalated;
    }
}
