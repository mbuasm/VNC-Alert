using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.App.Tests;

/// <summary>Zrzut scenariusza Panelu do porównania dwóch wersji — tylko ręcznie (<c>VNCALERT_TESTRAPORT_PANEL_OUT</c> = katalog).</summary>
public sealed class TestRaportPanelDumpFactAttribute : FactAttribute
{
    public const string Variable = "VNCALERT_TESTRAPORT_PANEL_OUT";

    public TestRaportPanelDumpFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Zrzut scenariusza Panelu do porównania wersji tylko ręcznie: {Variable}=<katalog>.";
    }
}

public sealed class TestRaportPanelScenarioDumpTests : IAsyncLifetime
{
    private readonly PanelTestHost _host = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static DateTimeOffset Local(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    private static Guid Id(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static DowntimeEpisode Episode(Random random, string? machineId, string name, DateTimeOffset start, double minutes, string reason,
        string? category, bool open = false, Guid? id = null, double? observedMinutes = null) =>
        new(machineId!, name, reason, start, start.AddMinutes(observedMinutes ?? minutes), open ? null : start.AddMinutes(minutes),
            EpisodeId: id ?? Id(random), Category: category);

    /// <summary>Ostatni dzień roboczy przed dzisiejszym dniem zmian (wtedy krok „otwarty postój” jest w domyślnym okresie).</summary>
    private static DateTime LastWorkingDay()
    {
        var day = DateTime.Now.AddHours(-6).Date.AddDays(-1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
        return day;
    }

    /// <summary>Historia 45 dni: błędy z kategorią i bez (klasyfikator), postoje bez pracy pod błędem, na 0 %, długie, nocne.</summary>
    private static void Fill(AppOrchestrator orchestrator, Random random)
    {
        string idle = Core.Alerts.AlertStateMachine.MachineIdleShortText, idleZero = Core.Alerts.AlertStateMachine.MachineIdleAtZeroShortText;
        for (int back = 45; back >= 1; back--)
        {
            var day = DateTime.Today.AddDays(-back);
            orchestrator.Downtime.Merge(Episode(random, "aidite1", "Aidite 1", Local(day.AddHours(7)), 95, idle, DowntimeCategories.Idle));
            orchestrator.Downtime.Merge(Episode(random, "aidite1", "Aidite 1", Local(day.AddHours(7.5)), 20, "Servo 6", null)); // klasyfikator
            orchestrator.Downtime.Merge(Episode(random, "aidite2", "Aidite 2", Local(day.AddHours(13.9)), 12 + back % 5, "Tacka nie pobrana", "tacka"));
            orchestrator.Downtime.Merge(Episode(random, "aidite3", "Aidite 3", Local(day.AddHours(21.5)), 50, idleZero, null)); // rodzaj z tekstu
            if (back % 3 == 0)
                orchestrator.Downtime.Merge(Episode(random, "aidite4", "Aidite 4", Local(day.AddHours(5.75)), 75, "Bazowanie (Home)", null)); // przez 06:00, długi
            if (back % 4 == 0)
                orchestrator.Downtime.Merge(Episode(random, "aidite5", "Aidite 5", Local(day.AddHours(10)), 30, idle, DowntimeCategories.Idle));
        }
    }

    private static string Canonical(DowntimeSlice s) => string.Join('|',
        s.Day.ToString("O", CultureInfo.InvariantCulture), s.Shift, s.Machine ?? "<null>", s.Reason ?? "<null>",
        s.Start.ToString("O", CultureInfo.InvariantCulture), s.End.ToString("O", CultureInfo.InvariantCulture),
        s.Interrupted, s.Ongoing, s.IsLong, s.Category,
        s.MachineId ?? "<null>", s.EpisodeId);

    /// <summary>Wszystko, co widać i co idzie do eksportu (bez chwili wygenerowania wykresów).</summary>
    internal static string Screen(DowntimeView view)
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
        text.Append("ChartsHeader = ").Append(Regex.Replace(view.ChartsHeader.Text, @"Wygenerowano: [0-9.: ]+", "Wygenerowano: <chwila>")).Append('\n');
        text.Append("ContentKey =\n").Append(typeof(DowntimeView).GetField("_chartsContentKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)).Append('\n');
        foreach (object row in view.SummaryGrid.ItemsSource?.Cast<object>() ?? []) text.Append("dzień: ").Append(row).Append('\n');
        foreach (object row in view.EpisodesGrid.ItemsSource?.Cast<object>() ?? []) text.Append("postój: ").Append(row).Append('\n');
        var slices = (IReadOnlyList<DowntimeSlice>)typeof(DowntimeView).GetField("_slices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        foreach (var slice in slices) text.Append("wiersz: ").Append(Canonical(slice)).Append('\n');
        return text.ToString();
    }

    [TestRaportPanelDumpFact]
    public async Task Panel_ScenariuszZmianWejscia_ZrzutPoKazdymKroku()
    {
        string output = Environment.GetEnvironmentVariable(TestRaportPanelDumpFactAttribute.Variable)!;
        string label = Environment.GetEnvironmentVariable("VNCALERT_TESTRAPORT_LABEL") is { Length: > 0 } l ? l : "wynik";
        Directory.CreateDirectory(output);
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var random = new Random(1_210_202_610);
        Fill(orchestrator, random);
        var text = new StringBuilder();
        DowntimeView? view = null;
        Window? window = null;
        void Snap(string step)
        {
            RealDowntimePanelTests.RefreshWithCharts(view!);
            RealDowntimePanelTests.Settle(window!);
            text.Append("=== ").Append(step).Append('\n').Append(Screen(view!));
        }
        void Tick(string step)
        {
            view!.Refresh(); // tik Panelu (wykresy według własnego zegara co 5 s)
            RealDowntimePanelTests.Settle(window!);
            text.Append("=== ").Append(step).Append(" (sam tik)\n").Append(Screen(view!));
        }
        var workDay = LastWorkingDay();
        var openId = Id(random);
        var openStart = Local(workDay.AddHours(10));
        string idle = Core.Alerts.AlertStateMachine.MachineIdleShortText;
        WpfTestHost.Run(() =>
        {
            view = new DowntimeView(orchestrator);
            var root = new Grid();
            root.Children.Add(view);
            window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = 1220, Height = 850 });
            RealDowntimePanelTests.Settle(window);
            Snap("pokazanie");
            for (int i = 0; i < 3; i++) Tick("tik bez zmian " + i);
            orchestrator.Downtime.Merge(Episode(random, "aidite6", "Aidite 6", openStart, 20, idle, DowntimeCategories.Idle, open: true, id: openId));
            Tick("nowy otwarty postój");
            orchestrator.Downtime.Merge(Episode(random, "aidite6", "Aidite 6", openStart, 35, idle, DowntimeCategories.Idle, open: true, id: openId));
            Tick("otwarty postój trwa");
            orchestrator.Downtime.Merge(Episode(random, "aidite6", "Aidite 6", openStart.AddMinutes(12), 6, "Servo 6", null));
            Tick("błąd w trakcie postoju bez pracy");
            orchestrator.Downtime.MergeClosure(openId, openStart.AddMinutes(50));
            Tick("zamknięcie");
            orchestrator.Downtime.Apply(
            [
                Episode(random, "aidite7", "Aidite 7", Local(workDay.AddHours(15)), 44, "Tacka nie pobrana", "tacka"),
                Episode(random, "aidite2", "Aidite 2", Local(workDay.AddHours(23.5)), 61, idle, DowntimeCategories.Idle),
            ]);
            Tick("porcja z sieci");
            orchestrator.Downtime.Merge(Episode(random, null, "Stara maszyna", Local(workDay.AddHours(8)), 15, "Servo 6", null));
            Tick("wpis bez MachineId");
            orchestrator.Downtime.Merge(Episode(random, "aidite7", "Aidite 7", Local(DateTime.Today.AddDays(-40).AddHours(11)), 33, "Servo 6", null));
            Tick("epizod spoza okresu");
            for (int shift = 1; shift <= 3; shift++)
            {
                view!.ShiftFilter.SelectedIndex = shift;
                Snap("zmiana " + shift);
            }
            view!.ShiftFilter.SelectedIndex = 0;
            view.FromDate.SelectedDate = DateTime.Today.AddDays(-29);
            Snap("miesiąc");
            typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
            Snap("cała historia");
            Tick("cała historia");
            return 0;
        });
        try
        {
            await orchestrator.ApplySharedConfigAsync(null, null, new Dictionary<string, bool> { ["aidite3"] = false });
            WpfTestHost.Run(() => { Tick("D3: Aidite 3 wyłączona"); Snap("D3: Aidite 3 wyłączona"); return 0; });
            orchestrator.Help.UseProfile(GroupProfile.Capture(orchestrator.Config, new RuleSet(), new Dictionary<string, string>()));
            WpfTestHost.Run(() => { Tick("pusty słownik"); Snap("pusty słownik"); return 0; });
            orchestrator.Help.UseProfile(GroupProfile.Capture(orchestrator.Config,
                ConfigLoader.LoadRules(Path.Combine(RepositoryRoot(), "config", "rules.example.json")), new Dictionary<string, string>()));
            WpfTestHost.Run(() => { Tick("słownik przywrócony"); Snap("słownik przywrócony"); return 0; });
            var field = typeof(AppOrchestrator).GetField("_cfg", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var config = (AppConfig)field.GetValue(orchestrator)!;
            field.SetValue(orchestrator, config with
            {
                Machines = [.. config.Machines.Select(m => m.Id == "aidite1" ? m with { Profile = "amm580" } : m)],
            });
            WpfTestHost.Run(() => { Tick("profil Aidite 1 = amm580"); Snap("profil Aidite 1 = amm580"); return 0; });
            WpfTestHost.Run(() =>
            {
                view!.IncludeIdleCharts.IsChecked = false;
                Snap("wykresy bez postojów bez pracy");
                view.LinePerMachine.IsChecked = true;
                Snap("linia dla każdej maszyny");
                Tick("koniec");
                return 0;
            });
        }
        finally
        {
            WpfTestHost.Run(() => { window?.Close(); return 0; });
        }
        File.WriteAllText(Path.Combine(output, $"{label}-panel-scenariusz.txt"), text.ToString(), new UTF8Encoding(false));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }
}
