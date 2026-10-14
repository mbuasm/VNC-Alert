using System.Globalization;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using Xunit.Abstractions;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

public sealed class SampleReportFilesTests(ITestOutputHelper output)
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central European Standard Time" : "Europe/Warsaw");

    [Fact]
    public void PrzykladowyPdfIExcel_RozkladJakNaHali_PowstajaIMajaWarstweBledow()
    {
        var (episodes, machines, from, to) = SampleEpisodes();
        var now = new DateTimeOffset(to.AddDays(1).ToDateTime(new TimeOnly(12, 0)), Warsaw.GetUtcOffset(to.AddDays(1).ToDateTime(new TimeOnly(12, 0))));
        var slices = PanelSlices(episodes, from, to, zone: Warsaw, now: now);
        var data = DowntimeExportData.Create(slices, from, to, null, [], machines, Warsaw, now, "PRZYKLAD-PC",
            DowntimeExportData.ScopeTextFor(true, "Hala"));
        var kinds = data.Kinds;
        output.WriteLine($"wierszy: {data.Episodes.Count}, razem {data.Totals.All.Minutes:N1} min, błędy {kinds.Errors.All.Minutes:N1} min "
            + $"({kinds.Errors.All.Minutes / Math.Max(1, data.Totals.All.Minutes):P1}), bez pracy {kinds.IdleMinutes:N1} min");

        string dir = NewWorkDir();
        string pdfPath = Path.Combine(dir, "przyklad-raport.pdf");
        string xlsxPath = Path.Combine(dir, "przyklad-raport.xlsx");
        Assert.True(ReportFileWriter.SavePdf(data, pdfPath).Success);
        Assert.True(ReportFileWriter.SaveExcel(data, xlsxPath).Success);
        output.WriteLine("PDF: " + pdfPath);
        output.WriteLine("XLSX: " + xlsxPath);

        using var pdf = PdfProbe.Open(File.ReadAllBytes(pdfPath));
        var summary = new List<string>();
        for (int page = 1; page <= pdf.PageCount; page++)
        {
            var lines = pdf.Lines(page);
            summary.Add($"--- strona {page} ({lines.Count} linii)");
            summary.AddRange(lines.Take(14).Select(l => "  " + l.Text));
            summary.Add("  ...");
            summary.AddRange(lines.Skip(Math.Max(0, lines.Count - 3)).Select(l => "  " + l.Text));
        }
        File.WriteAllLines(Path.Combine(dir, "przyklad-strony.txt"), summary);
        foreach (var line in summary) output.WriteLine(line);

        string all = pdf.AllText();
        Assert.Contains("Według rodzaju postoju: Błędy maszyn", all, StringComparison.Ordinal);
        Assert.Contains("Błędy maszyn według nazwy", all, StringComparison.Ordinal);
        Assert.Contains("Błędy maszyn według maszyny", all, StringComparison.Ordinal);
        int errorsPage = Enumerable.Range(1, pdf.PageCount).First(p => pdf.Text(p).Contains("Wykresy — błędy maszyn", StringComparison.Ordinal));
        Assert.DoesNotContain("Stoi bez pracy", pdf.Text(errorsPage), StringComparison.Ordinal);
    }

    private static (DowntimeEpisode[] Episodes, MachineConfig[] Machines, DateOnly From, DateOnly To) SampleEpisodes()
    {
        var errors = new List<DowntimeEpisode>();
        string? csv = Environment.GetEnvironmentVariable("VNC_ALERT_SAMPLE_CSV");
        if (!string.IsNullOrWhiteSpace(csv) && File.Exists(csv))
            foreach (var line in File.ReadLines(csv).Skip(1))
            {
                var cells = line.Split(';').Select(c => c.Trim().Trim('"')).ToArray();
                if (cells.Length < 8) continue;
                var start = Local(cells[4]);
                var end = Local(cells[5]);
                if (end <= start) continue;
                errors.Add(new DowntimeEpisode(MachineId(cells[2]), cells[2], cells[3], start, end, end,
                    EpisodeId: Guid.NewGuid(), Category: "inny"));
            }
        if (errors.Count == 0)
            errors.AddRange(Random(7, 60).Where(e => e.End - e.StartedAt < TimeSpan.FromMinutes(45)).Select(e => e with { Category = "inny" }));

        var from = DateOnly.FromDateTime(errors.Min(e => e.StartedAt).UtcDateTime).AddDays(-1);
        var to = DateOnly.FromDateTime(errors.Max(e => e.End).UtcDateTime);
        var machineNames = errors.Select(e => (e.MachineId, e.MachineName)).Distinct().OrderBy(m => m.MachineName, StringComparer.CurrentCulture).ToArray();
        var machines = machineNames.Select(m => new MachineConfig { Id = m.MachineId, Name = m.MachineName, Host = "h", Profile = "aidite" }).ToArray();

        var random = new System.Random(9);
        var idle = new List<DowntimeEpisode>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            foreach (var (id, name) in machineNames)
            {
                var start = At(day, 7 + random.Next(0, 6), random.Next(0, 60));
                var end = start.AddMinutes(random.Next(120, 360));
                idle.Add(new DowntimeEpisode(id, name, AlertStateMachine.MachineIdleShortText, start, end, end,
                    EpisodeId: Guid.NewGuid(), Category: AlertStateMachine.IdleCategory));
                if (random.Next(3) == 0)
                {
                    var zeroStart = At(day, 15 + random.Next(0, 5), random.Next(0, 60));
                    var zeroEnd = zeroStart.AddMinutes(random.Next(20, 90));
                    idle.Add(new DowntimeEpisode(id, name, AlertStateMachine.MachineIdleAtZeroShortText, zeroStart, zeroEnd, zeroEnd,
                        EpisodeId: Guid.NewGuid(), Category: AlertStateMachine.IdleAtZeroCategory));
                }
            }
        }
        return ([.. errors, .. idle], machines, from, to);
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, Warsaw.GetUtcOffset(local));
    }

    private static DateTimeOffset Local(string text)
    {
        var local = DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return new DateTimeOffset(local, Warsaw.GetUtcOffset(local));
    }

    private static string MachineId(string name) => name.ToLowerInvariant().Replace(" ", "").Replace("(", "").Replace(")", "");
}
