using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.App.Tests;

public sealed class DowntimeViewRefreshTests : IAsyncLifetime
{
    private readonly PanelTestHost _host = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // IAsyncLifetime, nie IAsyncDisposable: xUnit 2.9 nie woła IAsyncDisposable na klasie testów.
    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    // ── Dane ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ostatnie dni robocze przed dzisiejszym dniem zmian, w domyślnym okresie widoku (dziś − 6 … dziś).</summary>
    private static DateTime[] RecentWorkingDays(int count)
    {
        var today = DateTime.Now.AddHours(-6).Date;
        return Enumerable.Range(1, 6).Select(i => today.AddDays(-i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d >= DateTime.Today.AddDays(-6))
            .Take(count).ToArray();
    }

    private static DateTimeOffset Local(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    private static DowntimeEpisode Episode(string? machineId, string name, DateTimeOffset start, double minutes, string reason, string? category,
        bool open = false, Guid? id = null) =>
        new(machineId!, name, reason, start, start.AddMinutes(minutes), open ? null : start.AddMinutes(minutes),
            EpisodeId: id ?? Guid.NewGuid(), Category: category);

    private static DowntimeEpisode Idle(string machineId, DateTimeOffset start, double minutes, bool open = false, Guid? id = null) =>
        Episode(machineId, "Aidite " + machineId[^1], start, minutes, Core.Alerts.AlertStateMachine.MachineIdleShortText,
            DowntimeCategories.Idle, open, id);

    private static DowntimeEpisode Error(string machineId, DateTimeOffset start, double minutes, string reason = "Servo 6") =>
        Episode(machineId, "Aidite " + machineId[^1], start, minutes, reason, "restart");

    /// <summary>Dwa dni robocze: błędy, postoje bez pracy nakładające się na błąd tej samej maszyny, długi postój, postój na 0 %.</summary>
    private static void Fill(AppOrchestrator orchestrator, DateTime[] days)
    {
        foreach (var (day, index) in days.Take(2).Select((d, i) => (d, i)))
        {
            orchestrator.Downtime.Merge(Idle("aidite1", Local(day.AddHours(7)), 95));
            orchestrator.Downtime.Merge(Error("aidite1", Local(day.AddHours(7.5)), 20));
            orchestrator.Downtime.Merge(Error("aidite2", Local(day.AddHours(13.9)), 12 + index, "Tacka nie pobrana"));
            orchestrator.Downtime.Merge(Episode("aidite3", "Aidite 3", Local(day.AddHours(21.5)), 50,
                Core.Alerts.AlertStateMachine.MachineIdleAtZeroShortText, DowntimeCategories.IdleAtZero));
            orchestrator.Downtime.Merge(Idle("aidite4", Local(day.AddHours(10)), 30 + index * 7));
        }
    }

    // ── Odczyt ekranu ─────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<DowntimeSlice> Slices(DowntimeView view) =>
        (IReadOnlyList<DowntimeSlice>)typeof(DowntimeView).GetField("_slices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static object? Built(DowntimeView view) =>
        typeof(DowntimeView).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);

    private static string Canonical(DowntimeSlice s) => string.Join('|',
        s.Day.ToString("O", CultureInfo.InvariantCulture), s.Shift, s.Machine ?? "<null>", s.Reason,
        s.Start.ToString("O", CultureInfo.InvariantCulture), s.End.ToString("O", CultureInfo.InvariantCulture),
        s.Interrupted, s.Ongoing, s.IsLong, s.Category,
        s.MachineId ?? "<null>", s.EpisodeId);

    /// <summary>Wszystko, co widok pokazuje i eksportuje: karty sum, ostrzeżenie, obie tabele (kolejność źródła) i wiersze raportu.</summary>
    private static string Screen(DowntimeView view)
    {
        var text = new StringBuilder();
        foreach (var block in new[]
                 {
                     view.TotalTime, view.TotalTimeWithoutLong, view.TotalTimeByKind, view.TotalMinutes, view.TotalMinutesWithoutLong,
                     view.TotalMinutesByKind, view.EpisodeCount, view.EpisodeCountLong, view.EpisodeCountByKind, view.EmptyText, view.ReportWarning,
                 })
            text.Append(block.Name).Append(" = ").Append(block.Text).Append('\n');
        text.Append("EmptyText.Visibility = ").Append(view.EmptyText.Visibility).Append('\n');
        foreach (object row in view.SummaryGrid.ItemsSource?.Cast<object>() ?? []) text.Append("dzień: ").Append(row).Append('\n');
        foreach (object row in view.EpisodesGrid.ItemsSource?.Cast<object>() ?? []) text.Append("postój: ").Append(row).Append('\n');
        foreach (var slice in Slices(view)) text.Append("wiersz: ").Append(Canonical(slice)).Append('\n');
        return text.ToString();
    }

    /// <summary>Świeży widok na tym samym orkiestratorze, z tym samym okresem i zmianą — „liczenie od zera”.</summary>
    private static string FromScratch(AppOrchestrator orchestrator, DowntimeView like)
    {
        var fresh = new DowntimeView(orchestrator);
        fresh.FromDate.SelectedDate = like.FromDate.SelectedDate;
        fresh.ToDate.SelectedDate = like.ToDate.SelectedDate;
        fresh.ShiftFilter.SelectedIndex = like.ShiftFilter.SelectedIndex;
        fresh.Refresh();
        return Screen(fresh);
    }

    private static Window Show(DowntimeView view)
    {
        var root = new Grid();
        root.Children.Add(view);
        var window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = 1220, Height = 850 });
        RealDowntimePanelTests.Settle(window);
        return window;
    }

    // ── Testy ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotBuildTheReport_FirstShowDoes()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(2);
        Assert.NotEmpty(days);
        Fill(orchestrator, days);
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            // Start programu (StatusWindow tworzy widok przy starcie): ani raportu, ani wierszy tabel.
            Assert.Null(Built(view));
            Assert.Null(view.EpisodesGrid.ItemsSource);
            Assert.Null(view.SummaryGrid.ItemsSource);
            Assert.Equal(string.Empty, view.TotalTime.Text);
            // Domyślny okres bez zmian: dziś − 6 … dziś.
            Assert.Equal(DateTime.Today.AddDays(-6), view.FromDate.SelectedDate);
            Assert.Equal(DateTime.Today, view.ToDate.SelectedDate);

            var window = Show(view);
            try
            {
                Assert.NotNull(Built(view));
                Assert.True(view.EpisodesGrid.Items.Count > 0);
                Assert.NotEqual(string.Empty, view.TotalTime.Text);
                Assert.Equal(FromScratch(orchestrator, view), Screen(view));
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void Tick_WithoutChange_KeepsReportAndTablesInstances()
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
                object? built = Built(view);
                var slices = Slices(view);
                object episodes = view.EpisodesGrid.ItemsSource, summary = view.SummaryGrid.ItemsSource;
                string screen = Screen(view);
                for (int i = 0; i < 5; i++)
                {
                    view.Refresh(); // tik Panelu, dane bez zmian
                    RealDowntimePanelTests.Settle(window);
                }
                Assert.Same(built, Built(view));
                Assert.Same(slices, Slices(view));
                Assert.Same(episodes, view.EpisodesGrid.ItemsSource);
                Assert.Same(summary, view.SummaryGrid.ItemsSource);
                Assert.Equal(screen, Screen(view));
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void Tick_AfterDataChange_ShowsNewRows_AndKeepsUserSortOfBothTables()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(2);
        Fill(orchestrator, days);
        string result = WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                int before = view.EpisodesGrid.Items.Count;
                var minutes = view.EpisodesGrid.Columns.First(c => c.SortMemberPath == "Minutes");
                minutes.SortDirection = ListSortDirection.Descending;
                view.EpisodesGrid.Items.SortDescriptions.Add(new SortDescription("Minutes", ListSortDirection.Descending));
                var dayMinutes = view.SummaryGrid.Columns.First(c => c.SortMemberPath == "Minutes");
                dayMinutes.SortDirection = ListSortDirection.Ascending;
                view.SummaryGrid.Items.SortDescriptions.Add(new SortDescription("Minutes", ListSortDirection.Ascending));
                RealDowntimePanelTests.Settle(window);

                orchestrator.Downtime.Merge(Error("aidite5", Local(days[0].AddHours(9)), 240, "Narzędzie nie wróciło"));
                view.Refresh();
                RealDowntimePanelTests.Settle(window);
                Assert.Equal(before + 1, view.EpisodesGrid.Items.Count);
                Assert.Equal(FromScratch(orchestrator, view), Screen(view));
                Assert.Equal(ListSortDirection.Descending, minutes.SortDirection);
                Assert.Equal(ListSortDirection.Ascending, dayMinutes.SortDirection);
                Assert.Equal(new[] { new SortDescription("Minutes", ListSortDirection.Descending) }, view.EpisodesGrid.Items.SortDescriptions);
                Assert.Equal(new[] { new SortDescription("Minutes", ListSortDirection.Ascending) }, view.SummaryGrid.Items.SortDescriptions);
                double[] shown = [.. view.EpisodesGrid.Items.Cast<object>().Select(r => (double)r.GetType().GetProperty("Minutes")!.GetValue(r)!)];
                Assert.Equal(shown.OrderByDescending(m => m), shown);
                double[] dayRows = [.. view.SummaryGrid.Items.Cast<object>().Select(r => (double)r.GetType().GetProperty("Minutes")!.GetValue(r)!)];
                Assert.Equal(dayRows.OrderBy(m => m), dayRows);
                Assert.Equal(240, shown[0], 6);
                return $"wierszy {before} → {view.EpisodesGrid.Items.Count}, sortowanie zachowane";
            }
            finally { window.Close(); }
        });
        Assert.Contains("sortowanie zachowane", result);
    }

    [Fact]
    public void Tick_ChangeOutsideTheShownPeriod_RebuildsButKeepsTables()
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
                object? built = Built(view);
                object episodes = view.EpisodesGrid.ItemsSource;
                string screen = Screen(view);
                // Postój sprzed miesiąca — poza domyślnym okresem tygodnia.
                orchestrator.Downtime.Merge(Error("aidite6", Local(DateTime.Today.AddDays(-35).AddHours(9)), 25));
                view.Refresh();
                RealDowntimePanelTests.Settle(window);
                Assert.NotSame(built, Built(view)); // dziennik się zmienił — raport policzony od nowa
                Assert.Same(episodes, view.EpisodesGrid.ItemsSource); // ...ale pokazywane wiersze te same
                Assert.Equal(screen, Screen(view));
                Assert.Equal(FromScratch(orchestrator, view), Screen(view));
            }
            finally { window.Close(); }
            return 0;
        });
    }

    /// <summary>
    /// Długi ciąg kroków na jednym widoku (jak Panel otwarty przez kilka minut) — po każdym ekran = świeży widok liczony od zera.
    /// </summary>
    [Fact]
    public void EveryStep_SameScreenAsComputingFromScratch()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(3);
        Fill(orchestrator, days);
        var openId = Guid.NewGuid();
        var openStart = Local(days[0].AddHours(15));
        var steps = new List<string>();
        DowntimeView? view = null;
        Window? window = null;
        void Check(string step)
        {
            view!.Refresh();
            RealDowntimePanelTests.Settle(window!);
            string expected = FromScratch(orchestrator, view);
            Assert.True(expected == Screen(view), $"krok „{step}”: ekran różni się od liczenia od zera\n--- od zera:\n{expected}\n--- widok:\n{Screen(view)}");
            steps.Add($"{step}: {view.EpisodesGrid.Items.Count} wierszy, {view.TotalMinutes.Text} min");
        }
        WpfTestHost.Run(() =>
        {
            view = new DowntimeView(orchestrator);
            window = Show(view);
            Check("start");
            orchestrator.Downtime.Merge(Idle("aidite2", openStart, 10, open: true, id: openId));
            Check("otwarty postój");
            orchestrator.Downtime.Merge(Idle("aidite2", openStart, 35, open: true, id: openId));
            Check("otwarty postój trwa dalej");
            orchestrator.Downtime.Merge(Error("aidite2", openStart.AddMinutes(12), 6, "Bazowanie (Home)"));
            Check("błąd w trakcie postoju bez pracy");
            Assert.True(orchestrator.Downtime.MergeClosure(openId, openStart.AddMinutes(80)));
            Check("zamknięcie");
            orchestrator.Downtime.Merge(Episode(null, "Stara maszyna", Local(days[1].AddHours(8)), 15, "Servo 6", null));
            Check("wpis bez MachineId");
            view.ShiftFilter.SelectedIndex = 2;
            Check("zmiana 14:00–22:00");
            view.ShiftFilter.SelectedIndex = 0;
            typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
            Check("cała historia");
            orchestrator.Downtime.Merge(Error("aidite7", Local(DateTime.Today.AddDays(-40).AddHours(11)), 33));
            Check("postój sprzed 40 dni");
            return 0;
        });
        WpfTestHost.Run(() => { window?.Close(); return 0; });
        Assert.Equal(9, steps.Count);
    }

    [Fact]
    public void Tick_EpisodeEndingBeyondNowPlusOneDay_RebuildsEveryTick_LikeBefore()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(1);
        Fill(orchestrator, days);
        orchestrator.Downtime.Merge(Idle("aidite6", Local(days[0].AddHours(8)), 60 * 24 * 30));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                object? first = Built(view);
                Thread.Sleep(20);
                view.Refresh();
                Assert.NotSame(first, Built(view));
                Assert.True((bool)first!.GetType().GetProperty("DependsOnNow")!.GetValue(first)!);
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void AllHistoryClick_RebindsEachTableOnce()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(2);
        Fill(orchestrator, days);
        orchestrator.Downtime.Merge(Error("aidite7", Local(DateTime.Today.AddDays(-40).AddHours(11)), 33));
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                int episodes = 0, summary = 0;
                var descriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
                EventHandler countEpisodes = (_, _) => episodes++, countSummary = (_, _) => summary++;
                descriptor.AddValueChanged(view.EpisodesGrid, countEpisodes);
                descriptor.AddValueChanged(view.SummaryGrid, countSummary);
                typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
                RealDowntimePanelTests.Settle(window);
                descriptor.RemoveValueChanged(view.EpisodesGrid, countEpisodes);
                descriptor.RemoveValueChanged(view.SummaryGrid, countSummary);
                Assert.Null(view.FromDate.SelectedDate);
                Assert.Null(view.ToDate.SelectedDate);
                Assert.Equal((1, 1), (episodes, summary));
                Assert.Equal(FromScratch(orchestrator, view), Screen(view));
            }
            finally { window.Close(); }
            return 0;
        });
    }

    [Fact]
    public void SummaryGrid_VirtualizesRows_LikeTheEpisodesTable()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        // Trzy miesiące dni z postojami: ~65 dni roboczych × (1 + 3 zmiany) wierszy w tabeli „Dni i zmiany”.
        foreach (var episode in PanelTestHost.SyntheticHall(DateTimeOffset.Now.AddDays(-91), DateTimeOffset.Now.AddHours(-1)).Where(e => e.MachineId == "aidite1"))
            orchestrator.Downtime.Merge(episode);
        WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var window = Show(view);
            try
            {
                typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
                RealDowntimePanelTests.Settle(window);
                Assert.True(view.SummaryGrid.EnableRowVirtualization);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(view.SummaryGrid));
                int rows = view.SummaryGrid.Items.Count;
                int realized = Descendants<DataGridRow>(view.SummaryGrid).Count();
                Assert.True(rows > 150, $"wierszy {rows}");
                // Widać ~8 wierszy (MaxHeight 270) plus bufor wirtualizacji; bez wirtualizacji — wszystkie.
                Assert.True(realized < 60, $"zrealizowanych wierszy {realized} z {rows}");
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
}
