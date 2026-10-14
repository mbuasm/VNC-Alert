using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class ReportCalendarTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);

    [Theory]
    [InlineData("2026-09-18T21:59:00+02:00", true)]
    [InlineData("2026-09-18T22:00:00+02:00", true)]
    [InlineData("2026-09-19T05:59:59+02:00", true)]
    [InlineData("2026-09-19T06:00:00+02:00", false)]
    [InlineData("2026-09-20T12:00:00+02:00", false)]
    [InlineData("2026-09-21T05:59:59+02:00", false)]
    [InlineData("2026-09-21T06:00:00+02:00", true)]
    [InlineData("2026-09-16T03:00:00+02:00", true)]
    public void IsReportedTime_GraniceWeekenduWCzasieLokalnym(string instant, bool expected)
    {
        Assert.Equal(expected, ReportCalendar.IsReportedTime(At(instant), TestZones.Warsaw));
    }

    [Theory]
    [InlineData("2026-09-19T05:59:59Z", true)]
    [InlineData("2026-09-19T06:00:00Z", false)]
    [InlineData("2026-09-21T06:00:00Z", true)]
    public void IsReportedTime_WStrefieUtc(string instant, bool expected)
    {
        Assert.Equal(expected, ReportCalendar.IsReportedTime(At(instant), TimeZoneInfo.Utc));
    }

    [Fact]
    public void IsReportedTime_GranicaLiczySieWCzasieLokalnymANieWUtc()
    {
        // Sobota 06:00 w Warszawie (CEST) to 04:00 UTC — ta sama chwila w UTC jest jeszcze piątkową nocą.
        var instant = At("2026-09-19T04:30:00Z");
        Assert.False(ReportCalendar.IsReportedTime(instant, TestZones.Warsaw));
        Assert.True(ReportCalendar.IsReportedTime(instant, TimeZoneInfo.Utc));
    }

    public static TheoryData<string, string, string[]> Przedzialy => new()
    {
        { "2026-09-18T22:00:00+02:00", "2026-09-19T08:00:00+02:00", ["2026-09-18T22:00:00+02:00", "2026-09-19T06:00:00+02:00"] },
        { "2026-09-19T10:00:00+02:00", "2026-09-21T07:00:00+02:00", ["2026-09-21T06:00:00+02:00", "2026-09-21T07:00:00+02:00"] },
        { "2026-09-18T20:00:00+02:00", "2026-09-21T08:00:00+02:00",
            ["2026-09-18T20:00:00+02:00", "2026-09-19T06:00:00+02:00", "2026-09-21T06:00:00+02:00", "2026-09-21T08:00:00+02:00"] },
        { "2026-09-19T06:00:00+02:00", "2026-09-21T06:00:00+02:00", [] },
        { "2026-09-16T08:00:00+02:00", "2026-09-16T08:00:00+02:00", [] },
        { "2026-09-16T09:00:00+02:00", "2026-09-16T08:00:00+02:00", [] },
        { "2026-09-15T08:00:00+02:00", "2026-09-17T08:00:00+02:00", ["2026-09-15T08:00:00+02:00", "2026-09-17T08:00:00+02:00"] },
        // Przez dwa weekendy: trzy kawałki.
        { "2026-09-18T12:00:00+02:00", "2026-09-29T12:00:00+02:00",
            ["2026-09-18T12:00:00+02:00", "2026-09-19T06:00:00+02:00", "2026-09-21T06:00:00+02:00", "2026-09-26T06:00:00+02:00",
             "2026-09-28T06:00:00+02:00", "2026-09-29T12:00:00+02:00"] },
    };

    [Theory]
    [MemberData(nameof(Przedzialy))]
    public void ReportedIntervals_PrzycinaNaGranicachWeekendu(string start, string end, string[] expected)
    {
        var parts = ReportCalendar.ReportedIntervals(At(start), At(end), TestZones.Warsaw);

        var flat = parts.SelectMany(p => new[] { p.Start, p.End }).ToArray();
        Assert.Equal(expected.Select(At), flat);
        // Kolejność rosnąca, kawałki niepuste i rozłączne.
        Assert.All(parts, p => Assert.True(p.Start < p.End));
        for (int i = 1; i < parts.Count; i++) Assert.True(parts[i - 1].End <= parts[i].Start);
    }

    [Fact]
    public void ReportedIntervals_TeSameReguływStrefieUtc()
    {
        var parts = ReportCalendar.ReportedIntervals(At("2026-09-18T20:00:00Z"), At("2026-09-21T08:00:00Z"), TimeZoneInfo.Utc);
        Assert.Equal(
            [(At("2026-09-18T20:00:00Z"), At("2026-09-19T06:00:00Z")), (At("2026-09-21T06:00:00Z"), At("2026-09-21T08:00:00Z"))],
            parts);
    }


    [Theory]
    [InlineData("2026-03-27T12:00:00+01:00", "2026-03-30T12:00:00+02:00", "2026-03-30T06:00:00+02:00")]
    [InlineData("2026-10-23T12:00:00+02:00", "2026-10-26T12:00:00+01:00", "2026-10-26T06:00:00+01:00")]
    public void ReportedDuration_PrzezWeekendZeZmianaCzasu_LiczyPrawdziwyCzas(string start, string end, string mondaySix)
    {
        // pt 12:00 – sob 06:00 = 18 h, pon 06:00 – 12:00 = 6 h; weekend (ze zmianą czasu) poza raportem.
        var zone = TestZones.Warsaw;
        Assert.Equal(TimeSpan.FromHours(24), ReportCalendar.ReportedDuration(At(start), At(end), zone));
        var parts = ReportCalendar.ReportedIntervals(At(start), At(end), zone);
        Assert.Equal(2, parts.Count);
        Assert.Equal(At(mondaySix), parts[1].Start);
        Assert.Equal(TimeSpan.FromHours(18), parts[0].End - parts[0].Start);
    }

    [Fact]
    public void ReportedDuration_PustyIOdwroconyPrzedzial_Zero()
    {
        var t = At("2026-09-16T08:00:00+02:00");
        Assert.Equal(TimeSpan.Zero, ReportCalendar.ReportedDuration(t, t, TestZones.Warsaw));
        Assert.Equal(TimeSpan.Zero, ReportCalendar.ReportedDuration(t, t.AddHours(-1), TestZones.Warsaw));
    }

    [Fact]
    public void ReportedDuration_ZmianaCzasuWDzienRoboczy_LiczyPrawdziwyCzas()
    {
        var zone = TestZones.WednesdayDst(2);
        Assert.Equal(TimeSpan.FromHours(47),
            ReportCalendar.ReportedDuration(At("2026-03-24T12:00:00+01:00"), At("2026-03-26T12:00:00+02:00"), zone));
    }

    // --- Strefa sztuczna z przeskokiem zegara na granicy zmiany w dzień roboczy ---

    public static TheoryData<int, string, string> PrzeskokiWDzienRoboczy => new()
    {
        { 6, "2026-03-24T12:00:00+01:00", "2026-03-27T12:00:00+02:00" },
        { 14, "2026-03-24T12:00:00+01:00", "2026-03-27T12:00:00+02:00" },
        { 6, "2026-10-27T12:00:00+02:00", "2026-10-30T12:00:00+01:00" },
        { 14, "2026-10-27T12:00:00+02:00", "2026-10-30T12:00:00+01:00" },
    };

    [Theory]
    [MemberData(nameof(PrzeskokiWDzienRoboczy))]
    public async Task PrzeskokZegaraWDzienRoboczy_BezWyjatkuIBezPetli(int hour, string start, string end)
    {
        var zone = TestZones.WednesdayDst(hour);
        TimeSpan? duration = null;
        IReadOnlyList<DowntimeSlice>? rows = null;
        var work = Task.Run(() =>
        {
            duration = ReportCalendar.ReportedDuration(At(start), At(end), zone);
            rows = DowntimeReport.Build(
                [new DowntimeEpisode("a", "A", "Błąd", At(start), At(end), At(end), EpisodeId: Guid.NewGuid())], zone);
        });
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == work, "Pętla w ReportCalendar/Build przy przeskoku zegara.");
        await work;
        // Cały przedział leży w dniach roboczych: czas raportowany = prawdziwy upływ czasu.
        Assert.Equal(At(end) - At(start), duration);
        Assert.Equal((At(end) - At(start)).TotalMinutes, rows!.Sum(r => r.Minutes), 6);
        Assert.All(rows!, r => Assert.True(r.Start < r.End));
    }

    private static void AssertConsistent(TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to)
    {
        var parts = ReportCalendar.ReportedIntervals(from, to, zone);
        var mismatches = new List<string>();
        for (var t = from; t < to; t = t.AddMinutes(10))
        {
            bool inPart = parts.Any(p => p.Start <= t && t < p.End);
            if (inPart != ReportCalendar.IsReportedTime(t, zone))
                mismatches.Add($"{t:u} (lokalnie {TimeZoneInfo.ConvertTime(t, zone):yyyy-MM-dd HH:mm zzz}): IsReportedTime={!inPart}, ReportedIntervals={inPart}");
        }
        Assert.True(mismatches.Count == 0, "IsReportedTime i ReportedIntervals różnią się:\n" + string.Join("\n", mismatches.Take(10)));
    }

    public static TheoryData<string> StrefyBezPrzeskokuNaGranicyWeekendu => new() { "utc", "warszawa", "sroda-6", "sroda-14" };

    private static TimeZoneInfo ZoneByKey(string key) => key switch
    {
        "utc" => TimeZoneInfo.Utc,
        "warszawa" => TestZones.Warsaw,
        "sroda-6" => TestZones.WednesdayDst(6),
        "sroda-14" => TestZones.WednesdayDst(14),
        "sobota-6" => TestZones.SaturdayDst(6),
        "poniedzialek-6" => TestZones.MondayDst(6),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Theory]
    [MemberData(nameof(StrefyBezPrzeskokuNaGranicyWeekendu))]
    public void IsReportedTimeZgodneZReportedIntervals_WiosnaIJesien(string key)
    {
        var zone = ZoneByKey(key);
        AssertConsistent(zone, At("2026-03-20T00:00:00Z"), At("2026-04-03T00:00:00Z"));
        AssertConsistent(zone, At("2026-10-20T00:00:00Z"), At("2026-11-03T00:00:00Z"));
    }

    [Theory]
    [InlineData("sobota-6")]
    [InlineData("poniedzialek-6")]
    public void IsReportedTimeZgodneZReportedIntervals_PrzeskokNaGranicyWeekendu(string key)
    {
        // Strefa sztuczna: zegar skacze/cofa się DOKŁADNIE na granicy weekendu (sob/pon 06:00). Godzina niejednoznaczna
        // po cofnięciu (06:00–07:00 dwa razy) musi być rozstrzygnięta tak samo w obu funkcjach.
        var zone = ZoneByKey(key);
        AssertConsistent(zone, At("2026-03-20T00:00:00Z"), At("2026-04-03T00:00:00Z"));
        AssertConsistent(zone, At("2026-10-20T00:00:00Z"), At("2026-11-03T00:00:00Z"));
    }
}
