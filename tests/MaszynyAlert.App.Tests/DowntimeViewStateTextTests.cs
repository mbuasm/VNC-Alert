using System.Reflection;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.App.Tests;

public sealed class DowntimeViewStateTextTests
{
    private static string StateText(DowntimeSlice slice)
    {
        MethodInfo method = typeof(DowntimeView).GetMethod("StateText", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Brak DowntimeView.StateText.");
        return (string)method.Invoke(null, [slice])!;
    }

    private static DowntimeSlice Slice(bool interrupted, bool ongoing, bool isLong)
    {
        var start = DateTimeOffset.Parse("2026-09-15T08:00:00Z");
        return new DowntimeSlice(new DateOnly(2026, 9, 15), "06:00–14:00", "Aidite 1", "Servo 6", start, start.AddMinutes(10),
            interrupted, ongoing) { IsLong = isLong };
    }

    [Theory]
    [InlineData(false, false, false, "Zakończony")]
    [InlineData(false, true, false, "Otwarty epizod")]
    [InlineData(true, false, false, "Przerwana obserwacja")]
    [InlineData(true, true, false, "Przerwana obserwacja")]
    [InlineData(false, false, true, "Zakończony, długi postój (> 1 h)")]
    [InlineData(false, true, true, "Otwarty epizod, długi postój (> 1 h)")]
    [InlineData(true, false, true, "Przerwana obserwacja, długi postój (> 1 h)")]
    public void StanZOznaczeniemDlugiegoPostoju(bool interrupted, bool ongoing, bool isLong, string expected)
    {
        Assert.Equal(expected, StateText(Slice(interrupted, ongoing, isLong)));
    }

    [Fact]
    public void StanDlugiegoPostoju_ZRaportu()
    {
        // Pełna droga: epizod 61 minut z Build → wiersz z oznaczeniem; 60 minut → bez.
        var start = DateTimeOffset.Parse("2026-09-15T08:00:00Z");
        var rows = DowntimeReport.Build(
        [
            new DowntimeEpisode("a", "A", "Błąd", start, start.AddMinutes(61), start.AddMinutes(61), EpisodeId: Guid.NewGuid()),
            new DowntimeEpisode("b", "B", "Błąd", start, start.AddMinutes(60), start.AddMinutes(60), EpisodeId: Guid.NewGuid()),
        ], TimeZoneInfo.Utc);

        Assert.All(rows.Where(r => r.MachineId == "a"), r => Assert.Equal("Zakończony, długi postój (> 1 h)", StateText(r)));
        Assert.All(rows.Where(r => r.MachineId == "b"), r => Assert.Equal("Zakończony", StateText(r)));
    }
}

public sealed class DowntimeViewStateTextExportEquivalenceTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void StanWEksporcie_TakSamJakWPanelu(bool interrupted, bool ongoing, bool isLong)
    {
        var method = typeof(DowntimeView).GetMethod("StateText", BindingFlags.NonPublic | BindingFlags.Static)!;
        var start = DateTimeOffset.Parse("2026-09-29T08:00:00Z");
        var slice = new DowntimeSlice(new DateOnly(2026, 9, 29), "06:00–14:00", "Aidite 1", "Tacka nie pobrana", start, start.AddMinutes(10),
            interrupted, ongoing) { IsLong = isLong };

        Assert.Equal((string)method.Invoke(null, [slice])!, Core.Export.ReportPresentationNames.StateText(slice));
    }
}
