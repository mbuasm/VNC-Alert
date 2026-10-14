using System.Globalization;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public sealed class DowntimeReportScaleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DowntimeEpisode[] History(int days)
    {
        var result = new List<DowntimeEpisode>(2 * days);
        var day = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        for (int added = 0; added < days; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var errorStart = day.AddHours(8);
            var idleStart = day.AddHours(9);
            result.Add(new DowntimeEpisode("aidite1", "Aidite 1", "Servo 6", errorStart, errorStart.AddMinutes(10), errorStart.AddMinutes(10),
                EpisodeId: Guid.NewGuid(), Category: "restart"));
            result.Add(new DowntimeEpisode("aidite1", "Aidite 1", "Stoi bez pracy (100 %) – dołóż prace", idleStart, idleStart.AddMinutes(30),
                idleStart.AddMinutes(30), EpisodeId: Guid.NewGuid(), Category: DowntimeCategories.Idle));
            added++;
        }
        return result.ToArray();
    }

    private static (long Allocated, int Slices) AllocatedByBuild(DowntimeEpisode[] episodes)
    {
        var now = episodes.Max(e => e.End).AddHours(1);
        GC.KeepAlive(DowntimeReport.Build(episodes.Take(20), Utc, null, now)); // JIT przed pomiarem
        long before = GC.GetAllocatedBytesForCurrentThread();
        var slices = DowntimeReport.Build(episodes, Utc, null, now);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (allocated, slices.Count);
    }

    [Fact]
    public void Build_IdleAndErrorsOfOneMachine_AllocationsGrowLinearlyWithHistory()
    {
        var (small, smallSlices) = AllocatedByBuild(History(500));
        var (large, largeSlices) = AllocatedByBuild(History(2000));
        // Wynik się nie zmienia: każdy epizod to jeden wiersz (bez nakładania, w jednej zmianie).
        Assert.Equal(1000, smallSlices);
        Assert.Equal(4000, largeSlices);
        double ratio = (double)large / small;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"Build: 1000 epizodów {small / 1048576.0:0.0} MB, 4000 epizodów {large / 1048576.0:0.0} MB (×{ratio:0.0} przy ×4 historii)");
        // Liniowo: ×4 historii ≈ ×4 alokacji (z zapasem ×6).
        Assert.True(ratio < 6, text);
    }
}
