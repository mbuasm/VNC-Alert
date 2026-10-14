using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class RealDowntimePanelFactAttribute : FactAttribute
{
    public const string DirectoryVariable = "VNCALERT_REAL_DOWNTIME_DIR";

    public RealDowntimePanelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DirectoryVariable)))
            Skip = $"Brak zmiennej {DirectoryVariable} (katalog z kopią downtime.jsonl) — pomiar Panelu na prawdziwych danych tylko ręcznie.";
    }
}

public sealed class PanelPerformanceFactAttribute : FactAttribute
{
    public const string Variable = "VNCALERT_PERF";

    public PanelPerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Pomiar wydajności Panelu tylko ręcznie: {Variable}=1.";
    }
}

public sealed class RealDowntimePanelTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int Repetitions = 5;
    private static readonly TimeSpan UiLimit = TimeSpan.FromSeconds(1);
    private readonly PanelTestHost _host = new();
    private readonly StringBuilder _log = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // IAsyncLifetime, nie IAsyncDisposable: xUnit 2.9 nie woła IAsyncDisposable na klasie testów.
    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private sealed record Tick(double CodeMs, double LayoutMs, long Allocated)
    {
        public double TotalMs => CodeMs + LayoutMs;
    }

    private sealed record PanelResult(Tick Week, Tick WeekWithCharts, Tick All, Tick AllWithCharts);

    [RealDowntimePanelFact]
    public void Panel_PrawdziweDane_CzasWatkuUi()
    {
        string source = Environment.GetEnvironmentVariable(RealDowntimePanelFactAttribute.DirectoryVariable)!;
        string data = _host.DataDirectory();
        File.Copy(Path.Combine(source, "downtime.jsonl"), Path.Combine(data, "downtime.jsonl"));
        if (File.Exists(Path.Combine(source, "observation.jsonl")))
            File.Copy(Path.Combine(source, "observation.jsonl"), Path.Combine(data, "state", "observation.jsonl"));
        var result = MeasurePanel("prawdziwe dane", source);
        output.WriteLine(_log.ToString());
        foreach (var tick in new[] { result.Week, result.WeekWithCharts, result.All, result.AllWithCharts })
            Assert.True(tick.TotalMs < UiLimit.TotalMilliseconds, _log.ToString());
    }

    [PanelPerformanceFact]
    public void Panel_SyntetycznaHala_CzasWatkuUi()
    {
        foreach (int months in new[] { 3, 6, 12 })
        {
            WriteSyntheticHall(months);
            MeasurePanel($"hala {months} mies. (syntetyczna)", null, diagnose: months == 12);
        }
        output.WriteLine(_log.ToString());
    }

    [PanelPerformanceFact]
    public void Panel_SyntetycznaHala12Miesiecy_TikPaneluPonizejSekundy()
    {
        WriteSyntheticHall(12);
        var result = MeasurePanel("hala 12 mies. (syntetyczna)", null);
        output.WriteLine(_log.ToString());
        Assert.True(result.Week.Allocated < 100L * 1024 * 1024, "Tik tygodnia alokuje ponad 100 MB:\n" + _log);
        Assert.True(result.All.TotalMs < UiLimit.TotalMilliseconds, "Tik „Cała historia” ponad 1 s wątku UI:\n" + _log);
        Assert.True(result.AllWithCharts.TotalMs < UiLimit.TotalMilliseconds, "Tik „Cała historia” z wykresami ponad 1 s wątku UI:\n" + _log);
    }

    private void WriteSyntheticHall(int months)
    {
        string data = _host.DataDirectory(clean: true);
        var now = DateTimeOffset.Now;
        using var writer = new StreamWriter(Path.Combine(data, "downtime.jsonl"), false, new UTF8Encoding(false));
        foreach (var episode in PanelTestHost.SyntheticHall(now.AddDays(-30.4 * months), now.AddHours(-1)))
            writer.Write("\n" + JsonSerializer.Serialize(episode) + "\n");
    }

    private PanelResult MeasurePanel(string label, string? source, bool diagnose = false)
    {
        var watch = Stopwatch.StartNew();
        var orchestrator = _host.Orchestrator(source);
        Line($"{label}: AppOrchestrator (wczytanie dzienników) {watch.Elapsed.TotalMilliseconds:0} ms; epizodów {orchestrator.Downtime.Snapshot().Count}");
        return WpfTestHost.Run(() =>
        {
            var created = Stopwatch.StartNew();
            var view = new DowntimeView(orchestrator);
            created.Stop();
            var root = new Grid();
            root.Children.Add(view);
            var window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = 1220, Height = 850 });
            try
            {
                Settle(window);
                Line($"{label}: konstruktor DowntimeView (InitializeComponent + Refresh tygodnia) {created.Elapsed.TotalMilliseconds:0} ms");
                // Drugi widok na tych samych danych (kod już skompilowany JIT) — sam koszt danych w konstruktorze.
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var warm = Stopwatch.StartNew();
                _ = new DowntimeView(orchestrator);
                Line(string.Create(CultureInfo.InvariantCulture,
                    $"{label}: drugi konstruktor DowntimeView (bez JIT) {warm.Elapsed.TotalMilliseconds:0} ms, alokacje {(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1048576.0:0} MB"));
                var week = Time($"{label}: tik Panelu (Refresh, bez wykresów)", window, () => view.Refresh());
                var weekCharts = Time($"{label}: tik Panelu z wykresami (co 5 s)", window, () => RefreshWithCharts(view));
                // Klik „Cała historia” (All_Click: FromDate = null, ToDate = null — każde przez Filter_Changed — i Refresh).
                long clickAllocated = GC.GetAllocatedBytesForCurrentThread();
                var click = Stopwatch.StartNew();
                typeof(DowntimeView).GetMethod("All_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [view, new RoutedEventArgs()]);
                double clickCode = click.Elapsed.TotalMilliseconds;
                click.Restart();
                Settle(window);
                Line(string.Create(CultureInfo.InvariantCulture,
                    $"{label}: klik „Cała historia”: kod {clickCode:0} ms, układ WPF {click.Elapsed.TotalMilliseconds:0} ms, alokacje {(GC.GetAllocatedBytesForCurrentThread() - clickAllocated) / 1048576.0:0} MB"));
                Assert.Null(view.FromDate.SelectedDate);
                Assert.Null(view.ToDate.SelectedDate);
                Line($"{label}: wierszy tabeli epizodów (cała historia): {view.EpisodesGrid.Items.Count}, wierszy podsumowania: {view.SummaryGrid.Items.Count}");
                var all = Time($"{label}: cała historia — tik Panelu (Refresh, bez wykresów)", window, () => view.Refresh());
                var allCharts = Time($"{label}: cała historia — tik z wykresami", window, () => RefreshWithCharts(view));
                if (diagnose)
                {
                    // Diagnostyka (tylko w tym widoku testu): ile układu to tabela „Dni i zmiany” bez wirtualizacji wierszy.
                    view.SummaryGrid.EnableRowVirtualization = true;
                    VirtualizingPanel.SetIsVirtualizing(view.SummaryGrid, true);
                    Settle(window);
                    Time($"{label}: DIAGNOSTYKA cała historia — tik, tabela „Dni i zmiany” z wirtualizacją wierszy", window, () => view.Refresh());
                    view.SummaryGrid.Visibility = Visibility.Collapsed;
                    view.EpisodesGrid.Visibility = Visibility.Collapsed;
                    Settle(window);
                    Time($"{label}: DIAGNOSTYKA cała historia — tik, obie tabele ukryte", window, () => view.Refresh());
                }
                return new PanelResult(week, weekCharts, all, allCharts);
            }
            finally
            {
                window.Close();
            }
        });
    }

    internal static void RefreshWithCharts(DowntimeView view)
    {
        typeof(DowntimeView).GetField("_chartsAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, DateTimeOffset.MinValue);
        view.Refresh();
    }

    /// <summary>Mediana z 5 powtórzeń: samo <paramref name="action"/> (kod widoku) i układ/rysowanie WPF po nim (wątek UI).</summary>
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
            Settle(window);
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

    internal static void Settle(Window window)
    {
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
    }

    private void Line(string text)
    {
        lock (_log) _log.AppendLine(text);
    }
}
