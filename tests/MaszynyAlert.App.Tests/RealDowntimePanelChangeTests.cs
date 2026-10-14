using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.History;
using Xunit.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class RealDowntimePanelChangeTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int Repetitions = 5;
    private static readonly TimeSpan UiLimit = TimeSpan.FromSeconds(1);
    private readonly PanelTestHost _host = new();
    private readonly StringBuilder _log = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private sealed record Tick(double CodeMs, double LayoutMs, long Allocated)
    {
        public double TotalMs => CodeMs + LayoutMs;
    }

    [PanelPerformanceFact]
    public void Panel_SyntetycznaHala12Miesiecy_PokazanieIZmianaDanychPonizejSekundy()
    {
        string data = _host.DataDirectory(clean: true);
        var now = DateTimeOffset.Now;
        using (var writer = new StreamWriter(Path.Combine(data, "downtime.jsonl"), false, new UTF8Encoding(false)))
            foreach (var episode in PanelTestHost.SyntheticHall(now.AddDays(-30.4 * 12), now.AddHours(-1)))
                writer.Write("\n" + JsonSerializer.Serialize(episode) + "\n");
        var orchestrator = _host.Orchestrator(null);
        Line($"hala 12 mies.: epizodów {orchestrator.Downtime.Snapshot().Count}");
        var workday = DateTime.Today.AddDays(-1);
        while (workday.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) workday = workday.AddDays(-1);
        var openStart = new DateTimeOffset(workday.AddHours(10), TimeZoneInfo.Local.GetUtcOffset(workday.AddHours(10)));
        var openId = Guid.NewGuid();
        var observed = openStart.AddMinutes(30);
        void Observe()
        {
            observed = observed.AddSeconds(1);
            orchestrator.Downtime.Merge(new DowntimeEpisode("aidite8", "Aidite 8", Core.Alerts.AlertStateMachine.MachineIdleShortText,
                openStart, observed, null, EpisodeId: openId, Category: DowntimeCategories.Idle));
        }
        Observe();

        var results = WpfTestHost.Run(() =>
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var view = new DowntimeView(orchestrator);
            Line(string.Create(CultureInfo.InvariantCulture,
                $"konstruktor DowntimeView (start programu): {watch.Elapsed.TotalMilliseconds:0} ms, alokacje {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0:0.0} MB"));
            allocated = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            var warm = new DowntimeView(orchestrator);
            Line(string.Create(CultureInfo.InvariantCulture,
                $"drugi konstruktor DowntimeView (bez JIT): {watch.Elapsed.TotalMilliseconds:0} ms, alokacje {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0:0.0} MB"));
            GC.KeepAlive(warm);
            var root = new Grid();
            root.Children.Add(view);
            allocated = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            var window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = 1220, Height = 850 });
            RealDowntimePanelTests.Settle(window);
            var shown = new Tick(watch.Elapsed.TotalMilliseconds, 0, GC.GetAllocatedBytesForCurrentThread() - allocated);
            Line(string.Create(CultureInfo.InvariantCulture,
                $"pierwsze pokazanie Panelu (liczenie tygodnia + układ WPF): {shown.CodeMs:0} ms, alokacje {shown.Allocated / 1048576.0:0.0} MB"));
            // Każdy tik po nowej obserwacji ma naprawdę przebudować tabelę (najgorszy przypadek), nie tylko policzyć raport.
            int rebinds = 0;
            var itemsSource = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
            itemsSource.AddValueChanged(view.EpisodesGrid, (_, _) => rebinds++);
            try
            {
                var weekSame = Time("tydzień — tik bez zmian", window, view.Refresh);
                var weekChange = Time("tydzień — tik po nowej obserwacji otwartego epizodu", window, () => { Observe(); view.Refresh(); });
                var weekCharts = Time("tydzień — tik po nowej obserwacji, z wykresami (co 5 s)", window,
                    () => { Observe(); RealDowntimePanelTests.RefreshWithCharts(view); });
                allocated = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [view, new RoutedEventArgs()]);
                double clickCode = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                RealDowntimePanelTests.Settle(window);
                var click = new Tick(clickCode, watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
                Line(string.Create(CultureInfo.InvariantCulture,
                    $"klik „Cała historia”: kod {click.CodeMs:0} ms, układ WPF {click.LayoutMs:0} ms, alokacje {click.Allocated / 1048576.0:0} MB; "
                    + $"wierszy tabeli postojów {view.EpisodesGrid.Items.Count}, dni i zmian {view.SummaryGrid.Items.Count}"));
                var allSame = Time("cała historia — tik bez zmian", window, view.Refresh);
                var allChange = Time("cała historia — tik po nowej obserwacji otwartego epizodu", window, () => { Observe(); view.Refresh(); });
                var allCharts = Time("cała historia — tik po nowej obserwacji, z wykresami (co 5 s)", window,
                    () => { Observe(); RealDowntimePanelTests.RefreshWithCharts(view); });
                // Sortowanie użytkownika (klik w „Minuty”) — przy każdej zmianie wierszy sortowanie wraca na nowe źródło.
                var minutes = view.EpisodesGrid.Columns.First(c => c.SortMemberPath == "Minutes");
                minutes.SortDirection = ListSortDirection.Descending;
                view.EpisodesGrid.Items.SortDescriptions.Add(new SortDescription("Minutes", ListSortDirection.Descending));
                RealDowntimePanelTests.Settle(window);
                var allSorted = Time("cała historia — tik po nowej obserwacji, tabela posortowana przez użytkownika", window,
                    () => { Observe(); view.Refresh(); });
                Line($"sortowanie po tikach: opisów {view.EpisodesGrid.Items.SortDescriptions.Count}, kierunek kolumny {minutes.SortDirection?.ToString() ?? "brak"}");
                Line($"przebudowy tabeli „Postoje maszyn” (pokazanie, 4 × {Repetitions} tików po zmianie, klik): {rebinds}");
                Assert.True(rebinds >= 4 * Repetitions, $"tiki po zmianie nie przebudowały tabeli ({rebinds}) — pomiar nie jest najgorszym przypadkiem\n{_log}");
                return new[] { shown, weekSame, weekChange, weekCharts, click, allSame, allChange, allCharts, allSorted };
            }
            finally
            {
                window.Close();
            }
        });
        output.WriteLine(_log.ToString());
        string[] names = ["pierwsze pokazanie", "tydzień bez zmian", "tydzień po zmianie", "tydzień po zmianie z wykresami", "klik „Cała historia”",
            "cała historia bez zmian", "cała historia po zmianie", "cała historia po zmianie z wykresami", "cała historia po zmianie, posortowana"];
        for (int i = 0; i < results.Length; i++)
            Assert.True(results[i].TotalMs < UiLimit.TotalMilliseconds, $"{names[i]}: {results[i].TotalMs:0} ms wątku UI ≥ 1 s\n{_log}");
    }

    /// <summary>Mediana z 5 powtórzeń: kod widoku i układ/rysowanie WPF po nim (wątek UI).</summary>
    private Tick Time(string name, Window window, Action action)
    {
        var code = new double[Repetitions];
        var layout = new double[Repetitions];
        var allocated = new long[Repetitions];
        for (int i = 0; i < Repetitions; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            action();
            code[i] = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            RealDowntimePanelTests.Settle(window);
            layout[i] = watch.Elapsed.TotalMilliseconds;
            allocated[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(code);
        Array.Sort(layout);
        Array.Sort(allocated);
        var tick = new Tick(code[Repetitions / 2], layout[Repetitions / 2], allocated[Repetitions / 2]);
        Line(string.Create(CultureInfo.InvariantCulture,
            $"{name}: kod {tick.CodeMs:0.0} ms (max {code[^1]:0.0}), układ WPF {tick.LayoutMs:0.0} ms (max {layout[^1]:0.0}), alokacje {tick.Allocated / 1048576.0:0.0} MB"));
        return tick;
    }

    private void Line(string text)
    {
        lock (_log) _log.AppendLine(text);
    }
}
