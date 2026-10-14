namespace MaszynyAlert.Core.Tests.TestUtil;

public static class TestZones
{
    /// <summary>
    /// Wiosną zegar skacze z <paramref name="hour"/>:00 na <paramref name="hour"/>+1:00 w dniu <paramref name="spring"/>,
    /// jesienią cofa się z <paramref name="hour"/>+1:00 (czas letni) na <paramref name="hour"/>:00 w dniu <paramref name="autumn"/>.
    /// </summary>
    public static TimeZoneInfo Custom(string id, DateOnly spring, DateOnly autumn, int hour)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, hour, 0, 0), spring.Month, spring.Day),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, hour + 1, 0, 0), autumn.Month, autumn.Day));
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(1), id, id + " std", id + " dst", [rule]);
    }

    public static TimeZoneInfo WednesdayDst(int hour) =>
        Custom($"test-sroda-{hour}", new DateOnly(2026, 3, 25), new DateOnly(2026, 10, 28), hour);

    public static TimeZoneInfo SaturdayDst(int hour) =>
        Custom($"test-sobota-{hour}", new DateOnly(2026, 3, 28), new DateOnly(2026, 10, 31), hour);

    public static TimeZoneInfo MondayDst(int hour) =>
        Custom($"test-poniedzialek-{hour}", new DateOnly(2026, 3, 30), new DateOnly(2026, 10, 26), hour);

    /// <summary>
    /// Strefa z dowolnym przeskokiem: wiosną w dniu <paramref name="spring"/> o <paramref name="springAt"/> (czas standardowy)
    /// zegar skacze o <paramref name="delta"/> do przodu, jesienią w dniu <paramref name="autumn"/> o <paramref name="autumnAt"/>
    /// (czas letni) cofa się o <paramref name="delta"/>. Czas standardowy = UTC + <paramref name="baseOffset"/>.
    /// </summary>
    public static TimeZoneInfo Transitions(string id, TimeSpan baseOffset, TimeSpan delta,
        DateOnly spring, TimeOnly springAt, DateOnly autumn, TimeOnly autumnAt)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), delta,
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1).Add(springAt.ToTimeSpan()), spring.Month, spring.Day),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1).Add(autumnAt.ToTimeSpan()), autumn.Month, autumn.Day));
        return TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id + " std", id + " dst", [rule]);
    }

    public static TimeZoneInfo Warsaw => TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
}
