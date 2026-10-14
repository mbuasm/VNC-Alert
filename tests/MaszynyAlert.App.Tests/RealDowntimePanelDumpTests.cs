using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.App.Tests;

/// <summary>Zrzut ekranu Panelu do porównania dwóch wersji programu — tylko ręcznie (<c>VNCALERT_PANEL_DUMP_OUT</c> = katalog wyników).</summary>
public sealed class PanelDumpFactAttribute : FactAttribute
{
    public const string Variable = "VNCALERT_PANEL_DUMP_OUT";

    public PanelDumpFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Zrzut Panelu do porównania wersji tylko ręcznie: {Variable}=<katalog>.";
    }
}

public sealed class RealDowntimePanelDumpTests : IAsyncLifetime
{
    private readonly List<PanelTestHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
    }

    [PanelDumpFact]
    public void Panel_ZrzutEkranu_PrawdziweDaneISyntetycznaHala()
    {
        string output = Environment.GetEnvironmentVariable(PanelDumpFactAttribute.Variable)!;
        string label = Environment.GetEnvironmentVariable("VNCALERT_REAL_DOWNTIME_LABEL") is { Length: > 0 } l ? l : "wynik";
        Directory.CreateDirectory(output);
        if (Environment.GetEnvironmentVariable(RealDowntimePanelFactAttribute.DirectoryVariable) is { Length: > 0 } source)
        {
            var host = new PanelTestHost();
            _hosts.Add(host);
            string data = host.DataDirectory();
            File.Copy(Path.Combine(source, "downtime.jsonl"), Path.Combine(data, "downtime.jsonl"));
            if (File.Exists(Path.Combine(source, "observation.jsonl")))
                File.Copy(Path.Combine(source, "observation.jsonl"), Path.Combine(data, "state", "observation.jsonl"));
            File.WriteAllText(Path.Combine(output, $"{label}-panel-prawdziwe.txt"), Dump(host.Orchestrator(source)), new UTF8Encoding(false));
        }
        {
            var host = new PanelTestHost();
            _hosts.Add(host);
            string data = host.DataDirectory();
            var midnight = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
            var ids = new Random(20261012);
            using (var writer = new StreamWriter(Path.Combine(data, "downtime.jsonl"), false, new UTF8Encoding(false)))
                foreach (var episode in PanelTestHost.SyntheticHall(midnight.AddDays(-365), midnight))
                {
                    var bytes = new byte[16];
                    ids.NextBytes(bytes);
                    writer.Write("\n" + JsonSerializer.Serialize(episode with { EpisodeId = new Guid(bytes) }) + "\n");
                }
            // Ten sam plik dla porównania eksportu (RealDowntimeDataTests z VNCALERT_REAL_DOWNTIME_DIR = katalog z tym plikiem).
            string hall = Path.Combine(output, "hala-" + label);
            Directory.CreateDirectory(hall);
            File.Copy(Path.Combine(data, "downtime.jsonl"), Path.Combine(hall, "downtime.jsonl"), overwrite: true);
            File.WriteAllText(Path.Combine(output, $"{label}-panel-hala.txt"), Dump(host.Orchestrator(null)), new UTF8Encoding(false));
        }
    }

    private static string Dump(Services.AppOrchestrator orchestrator) => WpfTestHost.Run(() =>
    {
        var view = new DowntimeView(orchestrator);
        var root = new Grid();
        root.Children.Add(view);
        var window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = 1220, Height = 850 });
        var text = new StringBuilder();
        try
        {
            RealDowntimePanelTests.Settle(window);
            void Snap(string name)
            {
                RealDowntimePanelTests.RefreshWithCharts(view);
                RealDowntimePanelTests.Settle(window);
                text.Append("=== ").Append(name).Append('\n').Append(Screen(view));
            }
            Snap("tydzień (domyślny okres)");
            for (int shift = 1; shift <= 3; shift++)
            {
                view.ShiftFilter.SelectedIndex = shift;
                Snap("tydzień, zmiana " + shift);
            }
            view.ShiftFilter.SelectedIndex = 0;
            view.FromDate.SelectedDate = DateTime.Today.AddDays(-29);
            Snap("miesiąc");
            typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
            Snap("cała historia");
            view.IncludeIdleCharts.IsChecked = false;
            Snap("cała historia, wykresy bez postojów bez pracy");
            view.LinePerMachine.IsChecked = true;
            Snap("cała historia, wykresy bez postojów bez pracy, linia dla każdej maszyny");
        }
        finally { window.Close(); }
        return text.ToString();
    });

    private static string Canonical(DowntimeSlice s) => string.Join('|',
        s.Day.ToString("O", CultureInfo.InvariantCulture), s.Shift, s.Machine ?? "<null>", s.Reason,
        s.Start.ToString("O", CultureInfo.InvariantCulture), s.End.ToString("O", CultureInfo.InvariantCulture),
        s.Interrupted, s.Ongoing, s.IsLong, s.Category,
        s.MachineId ?? "<null>", s.EpisodeId);

    private static string Screen(DowntimeView view)
    {
        var text = new StringBuilder();
        foreach (var block in new[]
                 {
                     view.TotalTime, view.TotalTimeWithoutLong, view.TotalTimeByKind, view.TotalMinutes, view.TotalMinutesWithoutLong,
                     view.TotalMinutesByKind, view.EpisodeCount, view.EpisodeCountLong, view.EpisodeCountByKind, view.EmptyText, view.ReportWarning,
                     view.NoDataText, view.MachinesChartTitle, view.MachinesChartSubtitle, view.KindsChartTitle, view.KindsChartSubtitle,
                     view.ErrorsChartTitle, view.ErrorsChartSubtitle, view.ErrorMachinesChartTitle, view.ErrorMachinesChartSubtitle,
                     view.DaysChartTitle, view.DaysChartSubtitle, view.DaysChartNote,
                 })
            text.Append(block.Name).Append(" = ").Append(block.Text).Append('\n');
        text.Append("EmptyText.Visibility = ").Append(view.EmptyText.Visibility).Append('\n');
        // Chwila wygenerowania to zegar, nie treść raportu.
        text.Append("ChartsHeader = ").Append(Regex.Replace(view.ChartsHeader.Text, @"Wygenerowano: [0-9.: ]+", "Wygenerowano: <chwila>")).Append('\n');
        text.Append("ContentKey =\n").Append(typeof(DowntimeView).GetField("_chartsContentKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)).Append('\n');
        foreach (object row in view.SummaryGrid.ItemsSource?.Cast<object>() ?? []) text.Append("dzień: ").Append(row).Append('\n');
        foreach (object row in view.EpisodesGrid.ItemsSource?.Cast<object>() ?? []) text.Append("postój: ").Append(row).Append('\n');
        var slices = (IReadOnlyList<DowntimeSlice>)typeof(DowntimeView).GetField("_slices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        foreach (var slice in slices) text.Append("wiersz: ").Append(Canonical(slice)).Append('\n');
        return text.ToString();
    }
}
