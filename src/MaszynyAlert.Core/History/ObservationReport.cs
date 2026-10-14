namespace MaszynyAlert.Core.History;

public sealed record ObservationSummary(TimeSpan Observed, TimeSpan NoConnection, TimeSpan ScreenHidden, DateTimeOffset? FirstEntryAt);

public static class ObservationReport
{
    /// <summary>
    /// Rozłączne przedziały jednej maszyny po rozstrzygnięciu konfliktów stanów, posortowane po początku —
    /// podstawa sum w <see cref="Summarize"/> (i np. osi czasu na wykresie).
    /// </summary>
    public static IReadOnlyList<ObservationInterval> Resolve(IEnumerable<ObservationInterval> intervals, string machineId)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(machineId);
        var mine = intervals.Where(i => i is not null && i.IsValid() && i.MachineId == machineId).ToArray();
        List<ObservationSpan> SetOf(ObservationState state) =>
            IntervalMath.Union(mine.Where(i => i.State == state).Select(i => new ObservationSpan(i.Start, i.End)));
        var observed = SetOf(ObservationState.Observed);
        var hidden = IntervalMath.Subtract(SetOf(ObservationState.ScreenHidden), observed);
        var noConnection = IntervalMath.Subtract(IntervalMath.Subtract(SetOf(ObservationState.NoConnection), observed), hidden);
        return observed.Select(s => new ObservationInterval(machineId, ObservationState.Observed, s.Start, s.End))
            .Concat(hidden.Select(s => new ObservationInterval(machineId, ObservationState.ScreenHidden, s.Start, s.End)))
            .Concat(noConnection.Select(s => new ObservationInterval(machineId, ObservationState.NoConnection, s.Start, s.End)))
            .OrderBy(i => i.Start).ToArray();
    }

    public static ObservationSummary Summarize(IEnumerable<ObservationInterval> intervals, string machineId,
        DateTimeOffset from, DateTimeOffset to,
        Func<DateTimeOffset, DateTimeOffset, IEnumerable<(DateTimeOffset Start, DateTimeOffset End)>>? countedParts = null)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var all = intervals as IReadOnlyCollection<ObservationInterval> ?? intervals.ToArray();
        var resolved = Resolve(all, machineId);
        TimeSpan Sum(ObservationState state)
        {
            var spans = IntervalMath.Clip(resolved.Where(i => i.State == state).Select(i => new ObservationSpan(i.Start, i.End)), from, to);
            if (countedParts is not null)
                // Filtr dostaje już przycięty przedział, a jego wynik przycinamy jeszcze raz — obca funkcja
                // nie może wyprowadzić czasu poza zakres ani poza przedział.
                spans = spans.SelectMany(s => IntervalMath.Clip(
                    countedParts(s.Start, s.End).Select(p => new ObservationSpan(p.Start, p.End)), s.Start, s.End));
            return IntervalMath.Union(spans).Aggregate(TimeSpan.Zero, (total, s) => total + s.Length);
        }
        return new ObservationSummary(Sum(ObservationState.Observed), Sum(ObservationState.NoConnection),
            Sum(ObservationState.ScreenHidden), FirstEntryAt(all, machineId));
    }

    /// <summary>
    /// Początek pierwszego wpisu w dzienniku — dla maszyny <paramref name="machineId"/> albo, gdy
    /// <see langword="null"/>, dla dowolnej. Przed tą chwilą raport nie ma mianownika.
    /// </summary>
    public static DateTimeOffset? FirstEntryAt(IEnumerable<ObservationInterval> intervals, string? machineId = null)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        return intervals.Where(i => i is not null && i.IsValid() && i.End > i.Start
                && (machineId is null || i.MachineId == machineId))
            .Select(i => (DateTimeOffset?)i.Start).Min();
    }
}
