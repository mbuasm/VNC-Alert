using System.Globalization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public sealed class DowntimeWholeHistoryRangeTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Now = At("2026-10-09T12:00:00+02:00");
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly DateOnly Friday = new(2026, 10, 9);
    private static readonly MachineConfig[] Machines = [new() { Id = "m580", Name = "580", Host = "127.0.0.1", Profile = "amm580" }];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeHistory_IncludesObservationBeforeFirstDowntime_InPanelAndExport(bool hasDowntime)
    {
        ObservationInterval[] observation =
        [
            new("m580", ObservationState.NoConnection, At("2026-10-05T08:00:00+02:00"), At("2026-10-05T10:00:00+02:00")),
            new("m580", ObservationState.ScreenHidden, At("2026-10-06T09:00:00+02:00"), At("2026-10-06T09:30:00+02:00"))
        ];
        var (panel, export) = Build(hasDowntime ? [Episode("2026-10-07T08:00:00+02:00", 30)] : [], observation);
        Assert.Equal(Monday, export.From);
        Assert.Equal(Monday, panel.Days.Days[0]);
        Assert.Equal(Friday, export.To);
        Assert.Equal(5, panel.Header.WorkingDays);
        AssertNoData(panel, export, 120, 30);
        Assert.Equal(hasDowntime ? 30 : 0, export.Totals.All.Minutes, 6);
        Assert.Equal(export.Totals.All.Minutes, panel.Machines.Totals.Sum(), 6);
    }

    [Fact]
    public void WholeHistory_EmptyDataUsesCurrentShiftDayAndUnknownObservation()
    {
        var (panel, export) = Build([], []);
        Assert.Equal(Friday, export.From);
        Assert.Equal(Friday, export.To);
        Assert.Equal(Friday, Assert.Single(panel.Days.Days));
        Assert.False(export.HasObservationJournal);
        AssertNoData(panel, export, 0, 0);
        Assert.Empty(export.Episodes);
    }

    [Fact]
    public void ExplicitRangeAndShift_AreNotExpandedByOlderObservations()
    {
        ObservationInterval[] observation =
        [
            new("m580", ObservationState.NoConnection, At("2026-10-05T08:00:00+02:00"), At("2026-10-05T10:00:00+02:00")),
            new("m580", ObservationState.NoConnection, At("2026-10-07T13:00:00+02:00"), At("2026-10-07T15:00:00+02:00"))
        ];
        var from = new DateOnly(2026, 10, 7);
        var to = new DateOnly(2026, 10, 8);
        var (panel, export) = Build([Episode("2026-10-07T13:00:00+02:00", 120)], observation, from, to, "06:00–14:00");
        Assert.Equal(from, export.From);
        Assert.Equal(to, export.To);
        Assert.Equal(new[] { from, to }, panel.Days.Days);
        AssertNoData(panel, export, 60, 0);
        Assert.Equal(60, export.Totals.All.Minutes, 6);
        Assert.Equal(60, panel.Machines.Totals.Sum(), 6);
    }

    [Fact]
    public void WholeHistory_ObservationBeforeSixBelongsToPreviousShiftDay()
    {
        ObservationInterval[] observation =
        [new("m580", ObservationState.NoConnection, At("2026-10-06T05:00:00+02:00"), At("2026-10-06T06:00:00+02:00"))];
        var (panel, export) = Build([], observation);
        Assert.Equal(Monday, export.From);
        Assert.Equal(Monday, panel.Days.Days[0]);
        AssertNoData(panel, export, 60, 0);
    }

    [Fact]
    public void WholeHistory_OlderDowntimeStillPrecedesObservation_AndKnownFromIsPreserved()
    {
        var first = At("2026-10-07T08:00:00+02:00");
        ObservationInterval[] observation = [new("m580", ObservationState.NoConnection, first, first.AddHours(1))];
        var (panel, export) = Build([Episode("2026-10-05T08:00:00+02:00", 30)], observation);
        Assert.Equal(Monday, export.From);
        Assert.Equal(Monday, panel.Days.Days[0]);
        Assert.Equal(first, export.ObservationKnownFrom);
        AssertNoData(panel, export, 60, 0);
    }

    [Fact]
    public void WholeHistory_InvalidObservationDoesNotExtendRange()
    {
        var first = At("2026-09-01T08:00:00+02:00");
        ObservationInterval[] observation = [new("m580", ObservationState.NoConnection, first, first.AddMinutes(-1))];
        var (panel, export) = Build([], observation);
        Assert.Equal(Friday, export.From);
        Assert.Equal(Friday, Assert.Single(panel.Days.Days));
        AssertNoData(panel, export, 0, 0);
    }

    [Fact]
    public void WholeHistory_WarsawDstWeekendRemainsOutsideNoDataTotals()
    {
        var now = At("2026-10-26T08:00:00+01:00");
        ObservationInterval[] observation =
        [new("m580", ObservationState.NoConnection, At("2026-10-23T22:00:00+02:00"), now)];
        var (panel, export) = Build([], observation, now: now);
        Assert.Equal(new DateOnly(2026, 10, 23), export.From);
        Assert.Equal(new[] { new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 26) }, panel.Days.Days);
        // Weekend 49 h jest nadal wykluczony; raport liczy 8 h piątkowej nocy i 2 h poniedziałku.
        AssertNoData(panel, export, 600, 0);
    }

    private static (DowntimeChartSet Panel, DowntimeExportData Export) Build(DowntimeEpisode[] episodes,
        ObservationInterval[] observation, DateOnly? from = null, DateOnly? to = null, string? shift = null,
        DateTimeOffset? now = null)
    {
        var generated = now ?? Now;
        var slices = DowntimeReport.Build(episodes, Warsaw, now: generated)
            .Where(s => (from is null || s.Day >= from) && (to is null || s.Day <= to) && (shift is null || s.Shift == shift))
            .ToArray();
        var panel = DowntimeCharts.Build(slices, new DowntimeChartOptions
        {
            From = from, To = to, Shift = shift, Zone = Warsaw, GeneratedAt = generated,
            Machines = [new("m580", "580")], Observation = observation
        });
        var export = DowntimeExportData.Create(slices, from, to, shift, observation, Machines, Warsaw,
            generated, "AUDIT-PC", "Dane lokalne tego komputera");
        return (panel, export);
    }

    private static void AssertNoData(DowntimeChartSet panel, DowntimeExportData export, double noConnection, double screenHidden)
    {
        Assert.Equal(noConnection, export.NoConnection.TotalMinutes, 6);
        Assert.Equal(noConnection, panel.NoData!.Machines.Sum(m => m.NoConnection.TotalMinutes), 6);
        Assert.Equal(screenHidden, export.ScreenHidden.TotalMinutes, 6);
        Assert.Equal(screenHidden, panel.NoData.Machines.Sum(m => m.ScreenHidden.TotalMinutes), 6);
        Assert.Equal(export.NoConnection, export.Charts.NoData!.Machines.Aggregate(TimeSpan.Zero, (s, m) => s + m.NoConnection));
    }

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static DowntimeEpisode Episode(string start, int minutes)
    {
        var s = At(start);
        return new("m580", "580", "Servo 6", s, s.AddMinutes(minutes), s.AddMinutes(minutes),
            EpisodeId: Guid.Parse("20000000-0000-0000-0000-000000000001"), Category: "restart");
    }
}
