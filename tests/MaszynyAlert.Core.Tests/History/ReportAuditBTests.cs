using System.Globalization;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public sealed class ReportAuditBTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Now = At("2026-10-09T12:00:00+02:00");
    private static readonly Guid EpisodeId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    private static MachineConfig Machine(string name = "580", bool enabled = true) =>
        new() { Id = "m580", Name = name, Host = "127.0.0.1", Profile = "amm580", Enabled = enabled };
    private static DowntimeEpisode Episode(string start, string end, bool ongoing = false, string name = "580") =>
        new("m580", name, "Servo 6", At(start), At(end), ongoing ? null : At(end),
            EpisodeId: EpisodeId, Category: "restart");

    [Fact]
    public void MissingMachineIdInSavedJournal_ReportAndChartsMustNotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), "vnc-audit-B-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                MachineName = "580", Reason = "Servo 6",
                StartedAt = At("2026-10-05T08:00:00+02:00"),
                LastObservedAt = At("2026-10-05T08:30:00+02:00"),
                EndedAt = At("2026-10-05T08:30:00+02:00"), Category = "restart"
            }) + Environment.NewLine);
            var journal = new DowntimeJournal(path);
            var episodes = journal.Snapshot();
            Assert.Single(episodes);
            Assert.Null(episodes[0].MachineId);
            var slices = DowntimeReport.Build(episodes, Warsaw, now: Now);
            Assert.Equal(30, slices.Sum(s => s.Minutes), 6);
            var exception = Record.Exception(() => DowntimeCharts.Build(slices, new DowntimeChartOptions
            {
                From = new DateOnly(2026, 10, 5), To = new DateOnly(2026, 10, 9),
                Zone = Warsaw, GeneratedAt = Now, Machines = [new("m580", "580")], Observation = []
            }));
            Assert.Null(exception);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WholeHistoryWithoutDowntime_PanelAndExportKeepTheSamePastNoConnection()
    {
        ObservationInterval[] observation =
        [new("m580", ObservationState.NoConnection, At("2026-10-05T08:00:00+02:00"), At("2026-10-05T10:00:00+02:00"))];
        // Te same dane i null/null odpowiadają przyciskowi „Cała historia” w DowntimeView.
        var slices = DowntimeReport.Build([], Warsaw, now: Now);
        var panel = DowntimeCharts.Build(slices, new DowntimeChartOptions
        {
            From = null, To = null, Zone = Warsaw, GeneratedAt = Now,
            Machines = [new("m580", "580")], Observation = observation
        });
        var export = DowntimeExportData.Create(slices, null, null, null, observation,
            [Machine()], Warsaw, Now, "AUDIT-PC", "Dane lokalne tego komputera");
        Assert.Equal(TimeSpan.FromHours(2), export.NoConnection);
        double panelMinutes = panel.NoData!.Machines.Sum(m => m.NoConnection.TotalMinutes);
        Assert.Equal(export.NoConnection.TotalMinutes, panelMinutes, 6);
    }

    [Fact]
    public void RenamedMachine_ExportSummaryAndChartUseTheSameName()
    {
        var slices = DowntimeReport.Build([
            Episode("2026-10-05T08:00:00+02:00", "2026-10-05T08:30:00+02:00", name: "Dawna 580")
        ], Warsaw, now: Now);
        var data = DowntimeExportData.Create(slices, new(2026, 10, 5), new(2026, 10, 9), null, [],
            [Machine("Nowa 580")], Warsaw, Now, "AUDIT-PC", "Dane lokalne tego komputera");
        Assert.Equal(30, Assert.Single(data.Machines).Totals.All.Minutes, 6);
        Assert.Equal(Assert.Single(data.Charts.Machines.Categories), Assert.Single(data.Machines).Name);
    }

    [Theory]
    [InlineData("2026-10-25T00:00:00+02:00", "2026-10-26T00:00:00+01:00", 25)]
    [InlineData("2026-03-29T00:00:00+01:00", "2026-03-30T00:00:00+02:00", 23)]
    public void WarsawDstDay_ObservationUsesElapsedHours_AndWeekendIsExcluded(string start, string end, int hours)
    {
        var a = At(start); var b = At(end);
        ObservationInterval[] observation = [new("m580", ObservationState.Observed, a, b)];
        Assert.Equal(TimeSpan.FromHours(hours), ObservationReport.Summarize(observation, "m580", a, b).Observed);
        Assert.Equal(TimeSpan.Zero, ObservationReport.Summarize(observation, "m580", a, b,
            DowntimeCharts.CountedParts(Warsaw, null)).Observed);
        Assert.Empty(DowntimeReport.Build([Episode(start, end)], Warsaw, now: b));
    }

    [Theory]
    [InlineData("2026-10-19T06:00:00+02:00", "2026-10-26T06:00:00+01:00", 169)]
    [InlineData("2026-03-23T06:00:00+01:00", "2026-03-30T06:00:00+02:00", 167)]
    public void WarsawDstWeek_ElapsedDenominatorDiffersFromReportedWorkingWeek(string start, string end, int hours)
    {
        var a = At(start); var b = At(end);
        ObservationInterval[] observation = [new("m580", ObservationState.Observed, a, b)];
        Assert.Equal(TimeSpan.FromHours(hours), ObservationReport.Summarize(observation, "m580", a, b).Observed);
        Assert.Equal(TimeSpan.FromHours(120), ObservationReport.Summarize(observation, "m580", a, b,
            DowntimeCharts.CountedParts(Warsaw, null)).Observed);
        Assert.Equal(TimeSpan.FromHours(120), ReportCalendar.ReportedDuration(a, b, Warsaw));
        var slices = DowntimeReport.Build([Episode(start, end)], Warsaw, now: b);
        Assert.Equal(15, slices.Count);
        Assert.Equal(120 * 60, slices.Sum(s => s.Minutes), 6);
        Assert.Equal(120 * 60, DowntimeReport.Totals(slices).All.Minutes, 6);
        Assert.Equal(1, DowntimeReport.Totals(slices).All.Episodes);
    }

    [Fact]
    public void OpenEpisodeAcrossWeekend_SelectedFridayEndsAtSaturdaySix_AndTotalsAreConsistent()
    {
        var now = At("2026-10-26T08:00:00+01:00");
        var all = DowntimeReport.Build([
            Episode("2026-10-23T22:00:00+02:00", "2026-10-26T08:00:00+01:00", ongoing: true)
        ], Warsaw, now: now);
        Assert.Equal(600, all.Sum(s => s.Minutes), 6); // 8 h piątkowej nocy + 2 h poniedziałku; weekend 49 h odpada.
        var selected = all.Where(s => s.Day == new DateOnly(2026, 10, 23)).ToArray();
        var row = Assert.Single(selected);
        Assert.True(row.Ongoing);
        Assert.True(row.IsLong);
        Assert.Equal(At("2026-10-24T06:00:00+02:00"), row.End);
        var data = DowntimeExportData.Create(selected, new(2026, 10, 23), new(2026, 10, 23), null, [],
            [Machine()], Warsaw, now, "AUDIT-PC", "Dane lokalne tego komputera");
        Assert.Equal(480, data.Totals.All.Minutes, 6);
        Assert.Equal(480, data.Episodes.Sum(e => e.Minutes), 6);
        Assert.Equal(480, data.Machines.Sum(m => m.Totals.All.Minutes), 6);
        Assert.Equal(480, data.Charts.Machines.Totals.Sum(), 6);
        Assert.Equal(480, data.Charts.Errors.Total, 6);
        Assert.Equal(480, data.Charts.Days.Series[0].Points.Sum(p => p.Value), 6);
    }

    [Fact]
    public void MidnightBelongsToPreviousShiftDay_AndEnabledToggleKeepsHistoricalDowntime()
    {
        var slices = DowntimeReport.Build([
            Episode("2026-10-05T23:30:00+02:00", "2026-10-06T00:30:00+02:00")
        ], Warsaw, now: Now);
        var row = Assert.Single(slices);
        Assert.Equal(new DateOnly(2026, 10, 5), row.Day);
        Assert.Equal("22:00–06:00", row.Shift);
        var data = DowntimeExportData.Create(slices, new(2026, 10, 5), new(2026, 10, 9), null, [],
            [Machine(enabled: false)], Warsaw, Now, "AUDIT-PC", "Dane lokalne tego komputera");
        Assert.Equal(60, data.Totals.All.Minutes, 6);
        Assert.Equal("580", Assert.Single(data.Machines).Name);
        Assert.Equal("580", Assert.Single(data.Charts.Machines.Categories));
    }
}

