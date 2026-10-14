namespace MaszynyAlert.Core.History;

public static class ReportCalendar
{
    /// <summary>Godzina lokalna, o której zaczyna się dzień zmian (pierwsza zmiana 06:00–14:00).</summary>
    public const int ShiftDayStartHour = 6;

    /// <summary>
    /// Czy chwila <paramref name="instant"/> należy do czasu raportowanego (nie leży w weekendzie od soboty 06:00
    /// do poniedziałku 06:00 czasu lokalnego strefy <paramref name="zone"/>). Granice: sobota 06:00 już NIE jest
    /// raportowana, poniedziałek 06:00 już JEST.
    /// </summary>
    public static bool IsReportedTime(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return !IsWeekendShiftDay(ShiftDay(instant, zone));
    }

    /// <summary>
    /// Części przedziału <c>[<paramref name="start"/>; <paramref name="end"/>)</c>, które należą do czasu
    /// raportowanego, w kolejności rosnącej. Przedział przecinający granicę weekendu jest przycinany na granicy,
    /// nie odrzucany w całości. Pusty lub odwrócony przedział daje pustą listę.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ReportedIntervals(
        DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        var cursor = start;
        while (cursor < end)
        {
            var shiftDay = ShiftDay(cursor, zone);
            bool reported = !IsWeekendShiftDay(shiftDay);
            // Następna granica: z czasu raportowanego — najbliższa sobota 06:00, z weekendu — najbliższy
            // poniedziałek 06:00 (oba liczone od dnia zmian, który zaczyna się o 06:00).
            var target = reported ? DayOfWeek.Saturday : DayOfWeek.Monday;
            int days = ((int)target - (int)shiftDay.DayOfWeek + 7) % 7;
            if (days == 0) days = 7;
            var boundary = NextInstant(AddWall(shiftDay.Date, TimeSpan.FromDays(days) + TimeSpan.FromHours(ShiftDayStartHour)),
                zone, cursor);
            var until = end < boundary ? end : boundary;
            if (reported) result.Add((cursor, until));
            cursor = until;
        }
        return result;
    }

    /// <summary>Łączny czas raportowany w przedziale <c>[<paramref name="start"/>; <paramref name="end"/>)</c>.</summary>
    public static TimeSpan ReportedDuration(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone) =>
        ReportedIntervals(start, end, zone).Aggregate(TimeSpan.Zero, (sum, part) => sum + (part.End - part.Start));

    /// <summary>
    /// Zegar ścienny cofnięty o <see cref="ShiftDayStartHour"/> godzin: jego data to „dzień zmian” (nocna zmiana
    /// 22:00–06:00 należy do dnia, w którym się zaczęła — tak samo jak w <see cref="DowntimeReport.Build"/>).
    /// </summary>
    private static DateTime ShiftDay(DateTimeOffset instant, TimeZoneInfo zone) =>
        AddWall(LocalWall(instant, zone), TimeSpan.FromHours(-ShiftDayStartHour)) ?? DateTime.MinValue;

    internal static DateTime LocalWall(DateTimeOffset instant, TimeZoneInfo zone)
    {
        long ticks = instant.UtcTicks + zone.GetUtcOffset(instant).Ticks;
        return new DateTime(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Zegar ścienny <paramref name="wall"/> przesunięty o <paramref name="delta"/>, albo <see langword="null"/>, gdy wynik
    /// wychodzi poza zakres <see cref="DateTime"/> (granica „za końcem kalendarza” — wołający traktuje ją jak brak granicy).
    /// </summary>
    internal static DateTime? AddWall(DateTime wall, TimeSpan delta)
    {
        long ticks = wall.Ticks + delta.Ticks;
        return ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            ? null : new DateTime(ticks, DateTimeKind.Unspecified);
    }

    private static bool IsWeekendShiftDay(DateTime shiftDay) =>
        shiftDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Krok, o który przesuwa się godzina nieistniejąca (przeskok zegara do przodu), i limit takich kroków (50 h).</summary>
    private static readonly TimeSpan InvalidTimeStep = TimeSpan.FromMinutes(15);
    private const int MaxInvalidTimeSteps = 200;

    internal static DateTimeOffset NextInstant(DateTime? wallClock, TimeZoneInfo zone, DateTimeOffset after)
    {
        if (wallClock is null) return DateTimeOffset.MaxValue;
        var local = DateTime.SpecifyKind(wallClock.Value, DateTimeKind.Unspecified);
        for (int i = 0; i < MaxInvalidTimeSteps && zone.IsInvalidTime(local); i++)
        {
            if (AddWall(local, InvalidTimeStep) is not { } next) return DateTimeOffset.MaxValue;
            local = next;
        }
        IEnumerable<TimeSpan> offsets = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).OrderByDescending(o => o)
            : [zone.GetUtcOffset(local)];
        foreach (var offset in offsets)
        {
            long utcTicks = local.Ticks - offset.Ticks;
            if (utcTicks < DateTime.MinValue.Ticks) continue;
            if (utcTicks > DateTime.MaxValue.Ticks) return DateTimeOffset.MaxValue;
            var instant = new DateTimeOffset(local, offset);
            if (instant > after) return instant;
        }
        return after > DateTimeOffset.MaxValue.AddHours(-1) ? DateTimeOffset.MaxValue : after.AddHours(1);
    }
}
