using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.App.Tests;

public sealed class TestRaportPanelRefreshTests : IAsyncLifetime
{
    private readonly PanelTestHost _host = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static DateTimeOffset Local(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    private static DateTime[] RecentWorkingDays(int count)
    {
        var today = DateTime.Now.AddHours(-6).Date;
        return Enumerable.Range(1, 6).Select(i => today.AddDays(-i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d >= DateTime.Today.AddDays(-6))
            .Take(count).ToArray();
    }

    private static DowntimeEpisode Episode(string? machineId, string name, DateTimeOffset start, double minutes, string reason,
        string? category, bool open = false, Guid? id = null) =>
        new(machineId!, name, reason, start, start.AddMinutes(minutes), open ? null : start.AddMinutes(minutes),
            EpisodeId: id ?? Guid.NewGuid(), Category: category);

    /// <summary>Błędy BEZ zapisanej kategorii (rodzaj z klasyfikatora i słownika typu maszyny), postoje bez pracy, na 0 %.</summary>
    private static void Fill(AppOrchestrator orchestrator, DateTime[] days)
    {
        foreach (var day in days)
        {
            orchestrator.Downtime.Merge(Episode("aidite1", "Aidite 1", Local(day.AddHours(7)), 25, "Servo 6", null));
            orchestrator.Downtime.Merge(Episode("aidite1", "Aidite 1", Local(day.AddHours(8)), 40,
                Core.Alerts.AlertStateMachine.MachineIdleShortText, DowntimeCategories.Idle));
            orchestrator.Downtime.Merge(Episode("aidite2", "Aidite 2", Local(day.AddHours(15)), 18, "Bazowanie (Home)", null));
            orchestrator.Downtime.Merge(Episode("aidite3", "Aidite 3", Local(day.AddHours(22.5)), 12,
                Core.Alerts.AlertStateMachine.MachineIdleAtZeroShortText, null));
            orchestrator.Downtime.Merge(Episode("aidite4", "Aidite 4", Local(day.AddHours(11)), 70, "Tacka nie pobrana", "tacka"));
        }
    }

    private static IReadOnlyList<DowntimeSlice> Slices(DowntimeView view) =>
        (IReadOnlyList<DowntimeSlice>)typeof(DowntimeView).GetField("_slices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static object? Built(DowntimeView view) =>
        typeof(DowntimeView).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);

    private static void AllHistory(DowntimeView view) =>
        typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);

    private static Window Show(DowntimeView view)
    {
        var root = new Grid();
        root.Children.Add(view);
        var window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = 1220, Height = 850 });
        RealDowntimePanelTests.Settle(window);
        return window;
    }

    /// <summary>Ekran (karty, tabele, wiersze raportu, klucz treści wykresów) po wymuszonym przeliczeniu wykresów.</summary>
    private static string Screen(DowntimeView view, Window? window)
    {
        RealDowntimePanelTests.RefreshWithCharts(view);
        if (window is not null) RealDowntimePanelTests.Settle(window);
        return TestRaportPanelScenarioDumpTests.Screen(view);
    }

    /// <summary>Świeży widok na tym samym orkiestratorze z tym samym okresem, zmianą i przełącznikami — „liczenie od zera”.</summary>
    private static string FromScratch(AppOrchestrator orchestrator, DowntimeView like)
    {
        var fresh = new DowntimeView(orchestrator);
        fresh.FromDate.SelectedDate = like.FromDate.SelectedDate;
        fresh.ToDate.SelectedDate = like.ToDate.SelectedDate;
        fresh.ShiftFilter.SelectedIndex = like.ShiftFilter.SelectedIndex;
        fresh.IncludeIdleCharts.IsChecked = like.IncludeIdleCharts.IsChecked;
        fresh.LinePerMachine.IsChecked = like.LinePerMachine.IsChecked;
        return Screen(fresh, null);
    }

    private static void AssertSameAsFromScratch(AppOrchestrator orchestrator, DowntimeView view, Window window, string step)
    {
        string expected = FromScratch(orchestrator, view);
        string actual = Screen(view, window);
        Assert.True(expected == actual, $"krok „{step}”: ekran różni się od liczenia od zera\n--- od zera:\n{expected}\n--- widok:\n{actual}");
    }

    private static string[] Categories(DowntimeView view, string machineId) =>
        [.. Slices(view).Where(s => s.MachineId == machineId && !s.IsIdle).Select(s => s.Category).Distinct().Order()];

    [Fact]
    public void SlownikBledowZProfiluGrupy_PrzeliczaRaport_RodzajJakOdZera()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        Fill(orchestrator, RecentWorkingDays(2));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                Assert.Equal(["restart"], Categories(view, "aidite1"));
                object? built = Built(view);
                // Nowy profil grupy z pustym słownikiem (dziennik bez zmian) — rodzaj „nierozpoznany”, jak w raporcie liczonym od zera.
                var profile = GroupProfile.Capture(orchestrator.Config, new RuleSet(), new Dictionary<string, string>());
                orchestrator.Help.UseProfile(profile);
                view.Refresh();
                RealDowntimePanelTests.Settle(window);
                Assert.NotSame(built, Built(view));
                Assert.Equal([DowntimeCategories.Unrecognized], Categories(view, "aidite1"));
                AssertSameAsFromScratch(orchestrator, view, window, "pusty słownik");
                // Ten sam profil (ta sama instancja słownika) jeszcze raz — nic się nie zmieniło, raport bez przeliczenia.
                object? afterEmpty = Built(view);
                orchestrator.Help.UseProfile(profile);
                view.Refresh();
                Assert.Same(afterEmpty, Built(view));
                // Powrót do słownika z pliku — znów „restart”.
                orchestrator.Help.UseProfile(GroupProfile.Capture(orchestrator.Config,
                    ConfigLoader.LoadRules(Path.Combine(RepositoryRoot(), "config", "rules.example.json")), new Dictionary<string, string>()));
                view.Refresh();
                Assert.Equal(["restart"], Categories(view, "aidite1"));
                AssertSameAsFromScratch(orchestrator, view, window, "słownik przywrócony");
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void ProfilMaszynyZmienionyWKonfiguracji_PrzeliczaRaport_RodzajJakOdZera()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        Fill(orchestrator, RecentWorkingDays(2));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                Assert.Equal(["restart"], Categories(view, "aidite1"));
                var field = typeof(AppOrchestrator).GetField("_cfg", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var config = (AppConfig)field.GetValue(orchestrator)!;
                field.SetValue(orchestrator, config with
                {
                    Machines = [.. config.Machines.Select(m => m.Id == "aidite1" ? m with { Profile = "amm580" } : m)],
                });
                view.Refresh();
                RealDowntimePanelTests.Settle(window);
                Assert.Equal([DowntimeCategories.Unrecognized], Categories(view, "aidite1"));
                AssertSameAsFromScratch(orchestrator, view, window, "profil Aidite 1 = amm580");
                // Lista maszyn zmieniona W MIEJSCU (ta sama instancja konfiguracji, jak w VisualSmokeTest) — też wykryte.
                var current = (AppConfig)field.GetValue(orchestrator)!;
                int index = current.Machines.FindIndex(m => m.Id == "aidite1");
                current.Machines[index] = current.Machines[index] with { Profile = config.Machines[index].Profile };
                view.Refresh();
                Assert.Equal(["restart"], Categories(view, "aidite1"));
                AssertSameAsFromScratch(orchestrator, view, window, "lista maszyn zmieniona w miejscu");
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public async Task PrzelacznikiMaszynD3_EkranJakOdZera()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        Fill(orchestrator, RecentWorkingDays(2));
        DowntimeView? view = null;
        Window? window = null;
        string before = "";
        WpfTestHost.Run(() =>
        {
            view = new DowntimeView(orchestrator);
            window = Show(view);
            before = Screen(view, window);
            return 0;
        });
        try
        {
            await orchestrator.ApplySharedConfigAsync(null, null, new Dictionary<string, bool> { ["aidite3"] = false });
            Assert.False(orchestrator.Config.Machines.Single(m => m.Id == "aidite3").Enabled);
            WpfTestHost.Run(() =>
            {
                view!.Refresh();
                AssertSameAsFromScratch(orchestrator, view, window!, "D3: Aidite 3 wyłączona");
                Assert.NotEqual(before, Screen(view, window));
                return 0;
            });
        }
        finally { WpfTestHost.Run(() => { window?.Close(); return 0; }); }
    }

    [Fact]
    public void SortowanieUzytkownika_ZostajePoZmianieZmianyOkresuICalejHistorii()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(3);
        Fill(orchestrator, days);
        orchestrator.Downtime.Merge(Episode("aidite5", "Aidite 5", Local(DateTime.Today.AddDays(-20).AddHours(9)), 140, "Servo 6", null));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                var minutes = view.EpisodesGrid.Columns.First(c => c.SortMemberPath == "Minutes");
                var machine = view.EpisodesGrid.Columns.First(c => c.SortMemberPath == "Machine");
                minutes.SortDirection = ListSortDirection.Descending;
                view.EpisodesGrid.Items.SortDescriptions.Add(new SortDescription("Minutes", ListSortDirection.Descending));
                var share = view.SummaryGrid.Columns.First(c => c.SortMemberPath == "Hours");
                share.SortDirection = ListSortDirection.Ascending;
                view.SummaryGrid.Items.SortDescriptions.Add(new SortDescription("Hours", ListSortDirection.Ascending));
                RealDowntimePanelTests.Settle(window);
                void AssertSorted(string step)
                {
                    Assert.Equal(ListSortDirection.Descending, minutes.SortDirection);
                    Assert.Null(machine.SortDirection);
                    Assert.Equal(ListSortDirection.Ascending, share.SortDirection);
                    Assert.Equal([new SortDescription("Minutes", ListSortDirection.Descending)], view.EpisodesGrid.Items.SortDescriptions);
                    Assert.Equal([new SortDescription("Hours", ListSortDirection.Ascending)], view.SummaryGrid.Items.SortDescriptions);
                    double[] shown = [.. view.EpisodesGrid.Items.Cast<object>().Select(r => (double)r.GetType().GetProperty("Minutes")!.GetValue(r)!)];
                    Assert.True(shown.SequenceEqual(shown.OrderByDescending(m => m)), $"{step}: kolejność postojów nieposortowana");
                    double[] hours = [.. view.SummaryGrid.Items.Cast<object>().Select(r => (double)r.GetType().GetProperty("Hours")!.GetValue(r)!)];
                    Assert.True(hours.SequenceEqual(hours.OrderBy(m => m)), $"{step}: kolejność dni nieposortowana");
                }
                int all = view.EpisodesGrid.Items.Count;
                view.ShiftFilter.SelectedIndex = 1; // 06:00–14:00 — inne wiersze, nowe źródło tabel
                RealDowntimePanelTests.Settle(window);
                Assert.True(view.EpisodesGrid.Items.Count < all);
                AssertSorted("zmiana 06–14");
                view.ShiftFilter.SelectedIndex = 0;
                view.FromDate.SelectedDate = DateTime.Today.AddDays(-29);
                RealDowntimePanelTests.Settle(window);
                Assert.True(view.EpisodesGrid.Items.Count > all);
                AssertSorted("miesiąc");
                AllHistory(view);
                RealDowntimePanelTests.Settle(window);
                AssertSorted("cała historia");
                AssertSameAsFromScratchIgnoringViewOrder();
                void AssertSameAsFromScratchIgnoringViewOrder()
                {
                    // Treść (źródło tabel w kolejności raportu) jak od zera — sortowanie zmienia tylko widok.
                    string expected = FromScratch(orchestrator, view);
                    Assert.Equal(expected, Screen(view, window));
                }
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void PonownePokazanieZakladki_BezZmianDanych_NiczegoNiePodmienia_SortowanieZostaje()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        Fill(orchestrator, RecentWorkingDays(2));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                view.EpisodesGrid.Columns.First(c => c.SortMemberPath == "Minutes").SortDirection = ListSortDirection.Ascending;
                view.EpisodesGrid.Items.SortDescriptions.Add(new SortDescription("Minutes", ListSortDirection.Ascending));
                object? built = Built(view);
                object episodes = view.EpisodesGrid.ItemsSource, summary = view.SummaryGrid.ItemsSource;
                // Jak przełączenie zakładek Panelu: widok wypada z drzewa (Unloaded) i wraca (Loaded → Refresh).
                var root = (Grid)view.Parent;
                int loaded = 0;
                view.Loaded += (_, _) => loaded++;
                root.Children.Remove(view);
                RealDowntimePanelTests.Settle(window);
                root.Children.Add(view);
                RealDowntimePanelTests.Settle(window);
                Assert.Equal(1, loaded);
                Assert.Same(built, Built(view));
                Assert.Same(episodes, view.EpisodesGrid.ItemsSource);
                Assert.Same(summary, view.SummaryGrid.ItemsSource);
                Assert.Equal([new SortDescription("Minutes", ListSortDirection.Ascending)], view.EpisodesGrid.Items.SortDescriptions);
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void ZaznaczenieWiersza_ZostajePrzyTikuBezZmian_IPrzyZmianieSpozaOkresu()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        Fill(orchestrator, RecentWorkingDays(2));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                object selected = view.EpisodesGrid.Items[2]!;
                view.EpisodesGrid.SelectedItem = selected;
                for (int i = 0; i < 3; i++) { view.Refresh(); RealDowntimePanelTests.Settle(window); }
                Assert.Same(selected, view.EpisodesGrid.SelectedItem);
                orchestrator.Downtime.Merge(Episode("aidite6", "Aidite 6", Local(DateTime.Today.AddDays(-50).AddHours(9)), 25, "Servo 6", null));
                view.Refresh();
                RealDowntimePanelTests.Settle(window);
                Assert.Same(selected, view.EpisodesGrid.SelectedItem);
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void WirtualizacjaDniIZmian_SortowanieIZaznaczenieObejmujaNiezrealizowaneWiersze()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        foreach (var episode in PanelTestHost.SyntheticHall(DateTimeOffset.Now.AddDays(-91), DateTimeOffset.Now.AddHours(-1)).Where(e => e.MachineId == "aidite2"))
            orchestrator.Downtime.Merge(episode);
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                AllHistory(view);
                RealDowntimePanelTests.Settle(window);
                var grid = view.SummaryGrid;
                int rows = grid.Items.Count;
                Assert.True(rows > 150, $"wierszy {rows}");
                grid.Columns.First(c => c.SortMemberPath == "Minutes").SortDirection = ListSortDirection.Descending;
                grid.Items.SortDescriptions.Add(new SortDescription("Minutes", ListSortDirection.Descending));
                RealDowntimePanelTests.Settle(window);
                double[] minutes = [.. grid.Items.Cast<object>().Select(r => (double)r.GetType().GetProperty("Minutes")!.GetValue(r)!)];
                Assert.Equal(rows, minutes.Length);
                Assert.True(minutes.SequenceEqual(minutes.OrderByDescending(m => m)), "sortowanie obejmuje wszystkie wiersze, także niezrealizowane");
                Assert.True(Descendants<DataGridRow>(grid).Count() < 60, "wiersze są wirtualizowane");
                // Ostatni (niezrealizowany) wiersz: przewinięcie, zaznaczenie i kontener.
                object last = grid.Items[rows - 1]!;
                Assert.Null(grid.ItemContainerGenerator.ContainerFromItem(last));
                grid.SelectedItem = last;
                grid.ScrollIntoView(last);
                RealDowntimePanelTests.Settle(window);
                Assert.NotNull(grid.ItemContainerGenerator.ContainerFromItem(last));
                Assert.Same(last, grid.SelectedItem);
                // Tik bez zmian: zaznaczenie i sortowanie zostają.
                view.Refresh();
                RealDowntimePanelTests.Settle(window);
                Assert.Same(last, grid.SelectedItem);
                Assert.Equal([new SortDescription("Minutes", ListSortDirection.Descending)], grid.Items.SortDescriptions);
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void ObserwacjaWPrzyszlosciWGranicachDoby_BezPrzeliczaniaCoTik_EkranJakOdZera()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        Fill(orchestrator, RecentWorkingDays(1));
        var start = DateTimeOffset.Now.AddHours(-1);
        orchestrator.Downtime.Merge(new DowntimeEpisode("aidite7", "Aidite 7", "Servo 6", start, DateTimeOffset.Now.AddHours(20), null,
            EpisodeId: Guid.NewGuid(), Category: "restart"));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                object? built = Built(view);
                Assert.False((bool)built!.GetType().GetProperty("DependsOnNow")!.GetValue(built)!);
                for (int i = 0; i < 3; i++) { Thread.Sleep(5); view.Refresh(); }
                Assert.Same(built, Built(view));
                AssertSameAsFromScratch(orchestrator, view, window, "obserwacja 20 h w przyszłości");
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void KoniecTuzZaGranicaDoby_PoUplywieCzasu_PrzeliczaRazIPotemNie()
    {
        _host.DataDirectory();
        var clock = new PanelTestHost.ManualClock(DateTimeOffset.Now);
        var orchestrator = _host.Orchestrator(null, clock);
        Fill(orchestrator, RecentWorkingDays(1));
        var builtAt = clock.Now;
        var end = builtAt + DowntimeReport.FutureTolerance + TimeSpan.FromSeconds(2);
        // Chwila kolejnego tiku: koniec epizodu 200 ms przed „teraz + doba”.
        var later = end - DowntimeReport.FutureTolerance + TimeSpan.FromMilliseconds(200);
        var episode = new DowntimeEpisode("aidite7", "Aidite 7", "Servo 6", end.AddHours(-30), end, null, EpisodeId: Guid.NewGuid(), Category: "restart");
        orchestrator.Downtime.Merge(episode);
        // Gdy koniec wypada w weekendzie (sob 06:00 – pon 06:00), przycięcie nie zmienia wierszy — wtedy tylko ekran jak od zera.
        bool observable = !DowntimeReport.Build([episode], TimeZoneInfo.Local, null, builtAt)
            .SequenceEqual(DowntimeReport.Build([episode], TimeZoneInfo.Local, null, later));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                AllHistory(view);
                RealDowntimePanelTests.Settle(window);
                object? clipped = Built(view);
                Assert.True((bool)clipped!.GetType().GetProperty("DependsOnNow")!.GetValue(clipped)!, "warunek wstępny: koniec za „teraz + doba”");
                if (observable) Assert.True(Slices(view).Max(s => s.End) < end, "przycięcie w chwili liczenia");
                clock.Advance(later - builtAt);
                view.Refresh();
                RealDowntimePanelTests.Settle(window);
                object? full = Built(view);
                Assert.NotSame(clipped, full);
                Assert.False((bool)full!.GetType().GetProperty("DependsOnNow")!.GetValue(full)!);
                if (observable) Assert.Equal(end, Slices(view).Max(s => s.End));
                AssertSameAsFromScratch(orchestrator, view, window, "koniec już przed „teraz + doba”");
                view.Refresh();
                Assert.Same(full, Built(view));
            }
            finally { window.Close(); }
            return 0;
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }
}
