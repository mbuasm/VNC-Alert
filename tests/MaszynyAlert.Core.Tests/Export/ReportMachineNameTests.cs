using System.Globalization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.Export;

public sealed class ReportMachineNameTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Now = At("2026-10-09T12:00:00+02:00");
    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    private static MachineConfig Machine(string name = "580", bool enabled = true) =>
        new() { Id = "m580", Name = name, Host = "127.0.0.1", Profile = "amm580", Enabled = enabled };
    private static DowntimeEpisode Episode(string start, string end, string name) =>
        new("m580", name, "Servo 6", At(start), At(end), At(end),
            EpisodeId: Guid.NewGuid(), Category: "restart");
    private static DowntimeExportData Export(IReadOnlyList<DowntimeSlice> slices, params MachineConfig[] machines) =>
        DowntimeExportData.Create(slices, new(2026, 10, 5), new(2026, 10, 9), null,
            [new("m580", ObservationState.NoConnection, At("2026-10-05T10:00:00+02:00"), At("2026-10-05T10:30:00+02:00"))],
            machines, Warsaw, Now, "AUDIT-PC", "Dane lokalne tego komputera", linePerMachine: true);
    private static void AssertAggregatedName(DowntimeExportData data, string expected)
    {
        Assert.Equal(expected, Assert.Single(data.Machines).Name);
        Assert.Equal(expected, Assert.Single(data.Charts.Machines.Categories));
        Assert.Equal(expected, Assert.Single(data.Charts.ErrorMachines.Categories));
        Assert.Equal(expected, Assert.Single(data.Charts.Days.Series).Name);
        Assert.Equal(expected, Assert.Single(data.Charts.NoData!.Machines).Machine);
    }

    [Fact]
    public void RenamedMachine_ExportSummaryAndChartUseTheSameName()
    {
        var slices = DowntimeReport.Build([
            Episode("2026-10-05T08:00:00+02:00", "2026-10-05T08:30:00+02:00", "Dawna 580")
        ], Warsaw, now: Now);
        var data = DowntimeExportData.Create(slices, new(2026, 10, 5), new(2026, 10, 9), null, [],
            [Machine("Nowa 580")], Warsaw, Now, "AUDIT-PC", "Dane lokalne tego komputera");
        Assert.Equal(30, Assert.Single(data.Machines).Totals.All.Minutes, 6);
        Assert.Equal(Assert.Single(data.Charts.Machines.Categories), Assert.Single(data.Machines).Name);
    }

    [Fact]
    public void CurrentEnabledName_IsSharedByAllAggregations_ButDetailsKeepHistory()
    {
        var slices = DowntimeReport.Build([
            Episode("2026-10-05T08:00:00+02:00", "2026-10-05T08:30:00+02:00", "Dawna 580")
        ], Warsaw, now: Now);
        var data = Export(slices, Machine("Nowa 580"));
        AssertAggregatedName(data, "Nowa 580");
        Assert.Equal("Dawna 580", Assert.Single(slices).Machine);
        Assert.Equal("Dawna 580", Assert.Single(data.Episodes).Machine);
        Assert.Equal(30, data.Totals.All.Minutes, 6);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingOrDisabledMachine_UsesLatestHistoricalName_IndependentOfRowOrder(bool disabled, bool reverse)
    {
        var slices = DowntimeReport.Build([
            Episode("2026-10-05T08:00:00+02:00", "2026-10-05T08:10:00+02:00", "Dawna 580"),
            Episode("2026-10-06T08:00:00+02:00", "2026-10-06T08:20:00+02:00", "Ostatnia 580")
        ], Warsaw, now: Now).ToArray();
        if (reverse) Array.Reverse(slices);
        var data = Export(slices, disabled ? [Machine("Wyłączona nazwa", enabled: false)] : []);
        AssertAggregatedName(data, "Ostatnia 580");
        Assert.Equal(new[] { "Dawna 580", "Ostatnia 580" }, data.Episodes.Select(e => e.Machine));
        Assert.Equal(30, data.Totals.All.Minutes, 6);
    }

    [Fact]
    public void FilteredHistory_DoesNotBorrowNameFromOutsideSelectedRows()
    {
        var slices = DowntimeReport.Build([
            Episode("2026-10-05T08:00:00+02:00", "2026-10-05T08:10:00+02:00", "Dawna 580"),
            Episode("2026-10-06T08:00:00+02:00", "2026-10-06T08:20:00+02:00", "Ostatnia 580")
        ], Warsaw, now: Now).Where(s => s.Day == new DateOnly(2026, 10, 5)).ToArray();
        AssertAggregatedName(Export(slices), "Dawna 580");
    }

    [Fact]
    public void SameDisplayName_DoesNotMergeDifferentMachineIds()
    {
        var slices = DowntimeReport.Build([
            Episode("2026-10-05T08:00:00+02:00", "2026-10-05T08:10:00+02:00", "Dawna 580"),
            Episode("2026-10-05T08:00:00+02:00", "2026-10-05T08:20:00+02:00", "Inna maszyna") with { MachineId = "other" }
        ], Warsaw, now: Now);
        var data = Export(slices, Machine("Wspólna nazwa"), Machine("Wspólna nazwa") with { Id = "other" });
        Assert.Equal(2, data.Machines.Count);
        Assert.All(data.Machines, m => Assert.Equal("Wspólna nazwa", m.Name));
        Assert.Equal(new[] { 10d, 20d }, data.Machines.Select(m => m.Totals.All.Minutes).Order());
        Assert.Equal(2, data.Charts.Machines.Categories.Count);
        Assert.All(data.Charts.Machines.Categories, name => Assert.Equal("Wspólna nazwa", name));
    }
}