namespace MaszynyAlert.Core.History;

/// <summary>Wspólny początek „Całej historii” dla wykresów Panelu i eksportu — obejmuje także czas bez postojów.</summary>
internal static class ReportHistoryRange
{
    public static DateOnly? FirstDay(IReadOnlyList<DowntimeSlice> slices,
        IReadOnlyList<ObservationInterval>? observations, TimeZoneInfo zone)
    {
        DateOnly? first = slices.Count > 0 ? slices.Min(s => s.Day) : null;
        if (observations is not null && ObservationReport.FirstEntryAt(observations) is { } observed)
        {
            var day = ShiftDay(observed, zone);
            if (first is null || day < first.Value) first = day;
        }
        return first;
    }

    public static DateOnly ShiftDay(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(ReportCalendar.AddWall(ReportCalendar.LocalWall(instant, zone),
            TimeSpan.FromHours(-ReportCalendar.ShiftDayStartHour)) ?? DateTime.MinValue);
}
