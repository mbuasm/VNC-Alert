using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public sealed class DowntimeMissingMachineIdTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-05T08:00:00+02:00", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T12:00:00+02:00", CultureInfo.InvariantCulture);
    private static readonly DateOnly From = new(2026, 10, 5);
    private static readonly DateOnly To = new(2026, 10, 9);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SavedJournal_ChartsKeepPositiveMinutesForMissingId(string? id)
    {
        var slices = FromJournal(Episode(id, "580", 0, 30, 1));
        Assert.Equal(id, Assert.Single(slices).MachineId);
        Assert.Equal(30, DowntimeReport.Totals(slices).All.Minutes, 6);
        AssertCharts(slices, ("580", 30, 30));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SavedJournal_ExportKeepsAllPerMachineTotalsForMissingId(string? id)
    {
        // Ponad godzinę: także wariant bez długich i licznik epizodów nie mogą zgubić tej maszyny.
        var slices = FromJournal(Episode(id, "580", 0, 75, 1), Episode(id, "580", 120, 150, 2));
        var data = Export(slices);
        AssertExport(slices, data, ("580", 105, 105));
        var machine = Assert.Single(data.Machines);
        Assert.Equal(2, machine.Totals.All.Episodes);
        Assert.Equal(30, machine.Totals.WithoutLong.Minutes, 6);
        Assert.Equal(30, machine.Kinds.Errors.WithoutLong.Minutes, 6);
        Assert.False(machine.ObservationKnown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DifferentNamesWithoutId_OverlappingErrorsStaySeparate(string? id)
    {
        var slices = FromJournal(Episode(id, "580", 0, 30, 1), Episode(id, "Premile", 15, 45, 2));
        Assert.Equal(60, DowntimeReport.Totals(slices).All.Minutes, 6);
        Assert.Equal(2, DowntimeReport.Totals(slices).All.Episodes);
        AssertCharts(slices, ("580", 30, 30), ("Premile", 30, 30));
        AssertExport(slices, Export(slices), ("580", 30, 30), ("Premile", 30, 30));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingId_ErrorOnlySubtractsFromIdleOfSameNamedMachine(string? id)
    {
        var slices = FromJournal(Episode(id, "580", 10, 20, 1),
            Episode(id, "580", 0, 30, 2, DowntimeCategories.Idle),
            Episode(id, "Premile", 0, 30, 3, DowntimeCategories.Idle));
        Assert.Equal(60, DowntimeReport.Totals(slices).All.Minutes, 6);
        Assert.Equal(10, DowntimeReport.ByKind(slices).Errors.All.Minutes, 6);
        Assert.Equal(50, DowntimeReport.ByKind(slices).Idle.All.Minutes, 6);
        AssertCharts(slices, ("580", 30, 10), ("Premile", 30, 0));
        AssertExport(slices, Export(slices), ("580", 30, 10), ("Premile", 30, 0));
    }

    [Fact]
    public void SameNameWithNullAndEmptyId_MergesAndAppliesErrorPrecedence()
    {
        var slices = FromJournal(Episode(null, "580", 0, 20, 1), Episode("", "580", 10, 30, 2),
            Episode("", "580", 0, 60, 3, DowntimeCategories.Idle));
        Assert.Equal(60, DowntimeReport.Totals(slices).All.Minutes, 6);
        Assert.Equal(2, DowntimeReport.Totals(slices).All.Episodes);
        AssertCharts(slices, ("580", 60, 30));
        AssertExport(slices, Export(slices), ("580", 60, 30));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Totals_DoNotIdentifyDifferentUnnamedIdsByEpisodeIdAlone(string? id)
    {
        // Same identyfikatory starych epizodów nie zastępują tożsamości maszyny w sumach raportu.
        var slices = DowntimeReport.Build([Episode(id, "580", 0, 30, 1), Episode(id, "Premile", 60, 90, 1)],
            Warsaw, now: Now);
        Assert.Equal(2, DowntimeReport.Totals(slices).All.Episodes);
        Assert.Equal(2, DowntimeReport.ByKind(slices).Errors.All.Episodes);
        AssertExport(slices, Export(slices), ("580", 30, 30), ("Premile", 30, 30));
    }

    [Fact]
    public void ValidId_RenameStillMergesById_AndSameNameWithoutIdStaysSeparate()
    {
        var slices = FromJournal(Episode("m580", "Dawna 580", 0, 30, 1), Episode("m580", "580", 15, 45, 2),
            Episode("", "580", 0, 30, 3));
        Assert.Equal(75, DowntimeReport.Totals(slices).All.Minutes, 6);
        Assert.Equal(2, DowntimeReport.Totals(slices).All.Episodes);
        Assert.Equal(45, slices.Where(s => s.MachineId == "m580").Sum(s => s.Minutes), 6);
        var data = Export(slices);
        Assert.Equal(2, data.Machines.Count);
        Assert.Equal(75, data.Machines.Sum(m => m.Totals.All.Minutes), 6);
        Assert.Equal(75, data.Charts.Machines.Totals.Sum(), 6);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void RealIdMatchingFallbackText_DoesNotMergeOrSubtractDifferentMachine(string? missingId, bool idle)
    {
        // U+0001 jest legalne w rzeczywistym ID. Nie wolno rezerwować prefiksu przez zmianę schematu.
        var real = Episode("\u0001Premile", "580", 0, idle ? 10 : 30, 1);
        Assert.True(real.IsValid());
        var missing = Episode(missingId, "Premile", idle ? 0 : 15, idle ? 30 : 45, 2,
            idle ? DowntimeCategories.Idle : "restart");
        var slices = FromJournal(real, missing);
        Assert.Equal(idle ? 40 : 60, DowntimeReport.Totals(slices).All.Minutes, 6);
        Assert.Equal(2, DowntimeReport.Totals(slices).All.Episodes);
        AssertCharts(slices, ("580", idle ? 10 : 30, idle ? 10 : 30), ("Premile", 30, idle ? 0 : 30));
        AssertExport(slices, Export(slices), ("580", idle ? 10 : 30, idle ? 10 : 30), ("Premile", 30, idle ? 0 : 30));
    }

    [Fact]
    public void RealIdMatchingFallbackText_StillMatchesObservationAndNoData()
    {
        const string realId = "\u0001Premile";
        var slices = FromJournal(Episode(realId, "580", 0, 30, 1), Episode("", "Premile", 60, 90, 2));
        ObservationInterval[] observations = [new(realId, ObservationState.NoConnection, Start.AddHours(2), Start.AddHours(3))];
        MachineConfig[] machines = [new() { Id = realId, Name = "580", Host = "127.0.0.1", Profile = "amm580" }];
        var panel = DowntimeCharts.Build(slices, new DowntimeChartOptions
        {
            From = From, To = To, Zone = Warsaw, GeneratedAt = Now, Observation = observations,
            Machines = [new(realId, "580")], LinePerMachine = true
        });
        Assert.Equal(60, Assert.Single(panel.NoData!.Machines).NoConnection.TotalMinutes, 6);
        Assert.Equal(new[] { 30.0, 30.0 }, panel.Machines.Totals);
        var data = DowntimeExportData.Create(slices, From, To, null, observations, machines,
            Warsaw, Now, "AUDIT-PC", "Dane lokalne tego komputera", linePerMachine: true);
        Assert.Equal(60, data.NoConnection.TotalMinutes, 6);
        AssertExport(slices, data, ("580", 30, 30), ("Premile", 30, 30));
        var real = Assert.Single(data.Machines, m => m.Name == "580");
        Assert.Equal(realId, real.MachineId);
        Assert.True(real.ObservationKnown);
        Assert.Equal(60, real.NoConnection.TotalMinutes, 6);
        var missing = Assert.Single(data.Machines, m => m.Name == "Premile");
        Assert.False(missing.ObservationKnown);
        Assert.Equal(TimeSpan.Zero, missing.NoConnection);
    }
    private static DowntimeEpisode Episode(string? id, string name, int from, int to, int number, string category = "restart") =>
        new(id!, name, category == DowntimeCategories.Idle ? "Stoi bez pracy (100 %)" : "Servo 6",
            Start.AddMinutes(from), Start.AddMinutes(to), Start.AddMinutes(to),
            EpisodeId: Guid.Parse($"10000000-0000-0000-0000-{number:D12}"), Category: category);

    private static IReadOnlyList<DowntimeSlice> FromJournal(params DowntimeEpisode[] episodes)
    {
        string path = Path.Combine(Path.GetTempPath(), "vnc-B01-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            // Null pomijany: odtwarza dodatni starszy wpis bez pola MachineId, a nie wyłącznie obiekt w pamięci.
            var json = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            File.WriteAllLines(path, episodes.Select(e => JsonSerializer.Serialize(e, json)));
            var loaded = new DowntimeJournal(path).Snapshot();
            Assert.Equal(episodes.Length, loaded.Count);
            return DowntimeReport.Build(loaded, Warsaw, now: Now);
        }
        finally { File.Delete(path); }
    }

    private static DowntimeExportData Export(IReadOnlyList<DowntimeSlice> slices) =>
        DowntimeExportData.Create(slices, From, To, null, [],
            Array.Empty<MachineConfig>(), Warsaw, Now, "AUDIT-PC", "Dane lokalne tego komputera", linePerMachine: true);

    private static void AssertCharts(IReadOnlyList<DowntimeSlice> slices,
        params (string Name, double Minutes, double ErrorMinutes)[] expected)
    {
        var chart = DowntimeCharts.Build(slices, new DowntimeChartOptions
        {
            From = From, To = To, Zone = Warsaw, GeneratedAt = Now, Observation = [], LinePerMachine = true
        });
        Assert.Equal(expected.Length, chart.Machines.Categories.Count);
        foreach (var machine in expected)
        {
            int i = chart.Machines.Categories.ToList().IndexOf(machine.Name);
            Assert.True(i >= 0, machine.Name);
            Assert.Equal(machine.Minutes, chart.Machines.Totals[i], 6);
            int e = chart.ErrorMachines.Categories.ToList().IndexOf(machine.Name);
            Assert.True(e >= 0, machine.Name);
            Assert.Equal(machine.ErrorMinutes, chart.ErrorMachines.Totals[e], 6);
            Assert.Equal(machine.Minutes,
                Assert.Single(chart.Days.Series, s => s.Name == machine.Name).Points.Sum(p => p.Value), 6);
        }
        Assert.Equal(expected.Sum(m => m.Minutes), chart.Kinds.Total, 6);
        Assert.Equal(expected.Sum(m => m.ErrorMinutes), chart.Errors.Total, 6);
    }

    private static void AssertExport(IReadOnlyList<DowntimeSlice> slices, DowntimeExportData data,
        params (string Name, double Minutes, double ErrorMinutes)[] expected)
    {
        Assert.Equal(expected.Sum(m => m.Minutes), data.Totals.All.Minutes, 6);
        Assert.Equal(data.Totals.All.Minutes, data.Episodes.Sum(e => e.Minutes), 6);
        Assert.Equal(data.Totals.All.Minutes, data.Machines.Sum(m => m.Totals.All.Minutes), 6);
        Assert.Equal(data.Totals.WithoutLong.Minutes, data.Machines.Sum(m => m.Totals.WithoutLong.Minutes), 6);
        Assert.Equal(data.Totals.All.Episodes, data.Machines.Sum(m => m.Totals.All.Episodes));
        Assert.Equal(data.Totals.All.Minutes, data.Errors.Sum(e => e.Totals.All.Minutes), 6);
        Assert.Equal(data.Totals.All.Minutes, data.Days.Sum(d => d.Totals.All.Minutes), 6);
        Assert.Equal(expected.Length, data.Machines.Count);
        foreach (var machine in expected)
        {
            var row = Assert.Single(data.Machines, m => m.Name == machine.Name);
            Assert.Equal(machine.Minutes, row.Totals.All.Minutes, 6);
            Assert.Equal(machine.ErrorMinutes, row.Kinds.Errors.All.Minutes, 6);
            Assert.Equal(machine.Minutes - machine.ErrorMinutes, row.Kinds.IdleMinutes, 6);
        }
        AssertCharts(slices, expected);
    }
}
