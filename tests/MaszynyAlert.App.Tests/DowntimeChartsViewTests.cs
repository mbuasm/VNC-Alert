using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.App.Views.Charts;
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

public sealed class DowntimeChartsViewTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "r2-charts-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _disposablesSync = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables) await d.DisposeAsync();
        foreach (var d in _disposablesSync) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── Orkiestrator i dane ─────────────────────────────────────────────────────────────────────

    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public DateTimeOffset Now => DateTimeOffset.Now;
        public long MonotonicMs => Environment.TickCount64;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }

    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private AppOrchestrator Orchestrator(int machines)
    {
        string root = RepositoryRoot();
        var example = ConfigLoader.LoadAppConfig(System.IO.Path.Combine(root, "config", "config.example.json"));
        var rules = ConfigLoader.LoadRules(System.IO.Path.Combine(root, "config", "rules.example.json"));
        var sample = example.Machines.First(m => m.Enabled);
        string history = System.IO.Path.Combine(_dir, "data", "history.csv");
        var cfg = example with
        {
            Machines = Enumerable.Range(1, machines).Select(i => sample with
            {
                Id = "m" + i, Name = "Maszyna " + i, Host = "127.0.0.1", Port = ClosedPort(), Enabled = true,
            }).ToList(),
            Paths = example.Paths with
            {
                HistoryFile = history, LogsDir = System.IO.Path.Combine(_dir, "logs"),
                CapturesDir = System.IO.Path.Combine(_dir, "captures"), TestScreensDir = System.IO.Path.Combine(_dir, "test-ekran"),
            },
        };
        Directory.CreateDirectory(System.IO.Path.Combine(_dir, "test-ekran"));
        var clock = new RealClock();
        var secrets = new InMemorySecretStore();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, cfg.Timing, acks);
        var supervisor = new MonitorSupervisor(cfg, _ => "haslo", new NullOcrEngine(), new MessageMapper(rules), alerts, clock,
            new FileCaptureStore(System.IO.Path.Combine(_dir, "captures")), System.IO.Path.Combine(_dir, "test-ekran"));
        var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, acks, "TEST-PC", "test");
        var power = new PowerModeWatcher();
        var orchestrator = new AppOrchestrator(runtime, supervisor, alerts, new HistoryWriter(history, clock), power, clock,
            new StartupConfig(cfg, cfg),
            new SharedConfigSync(new SharedConfigStore(System.IO.Path.Combine(_dir, "shared-config", "shared-config.json")), "TEST-PC"),
            new NullOcrEngine(), NullLogger<AppOrchestrator>.Instance,
            new AlarmHelpService(rules, System.IO.Path.Combine(_dir, "alarm-instructions.json"), false, clock: clock), secrets);
        _disposables.Add(supervisor); _disposables.Add(runtime); _disposablesSync.Add(power);
        return orchestrator;
    }

    /// <summary>Ostatnie dni robocze PRZED dzisiejszym dniem zmian, najwyżej <paramref name="count"/> (wszystkie w domyślnym zakresie widoku: dziś − 6 … dziś).</summary>
    private static DateOnly[] RecentWorkingDays(int count)
    {
        var today = DateOnly.FromDateTime(DateTime.Now.AddHours(-6));
        return Enumerable.Range(1, 6).Select(i => today.AddDays(-i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d >= DateOnly.FromDateTime(DateTime.Today.AddDays(-6)))
            .Take(count).ToArray();
    }

    private static DowntimeEpisode Episode(string machineId, DateOnly day, int startHour, double minutes, string reason = "Servo 6",
        string? category = "inny")
    {
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(startHour, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(startHour, 0))));
        var end = start.AddMinutes(minutes);
        return new DowntimeEpisode(machineId, "Maszyna " + machineId[1..], reason, start, end, end, EpisodeId: Guid.NewGuid(), Category: category);
    }

    /// <summary>Raport z postojami każdej maszyny (różne czasy), 8 nazw błędów (w tym „Tacka nie pobrana”), jeden długi postój.</summary>
    private static void FillTypicalReport(AppOrchestrator orchestrator, int machines)
    {
        var days = RecentWorkingDays(3);
        Assert.NotEmpty(days);
        string[] reasons = ["Servo 6", "Bazowanie (Home)", "Tacka nie pobrana", "Tacka nie odłożona", "Ciśnienie powietrza", "Drzwi otwarte", "Narzędzie", "Wrzeciono"];
        for (int i = 1; i <= machines; i++)
            orchestrator.Downtime.Merge(Episode("m" + i, days[i % days.Length], 8, 4 + i * 3, reasons[i % reasons.Length]));
        orchestrator.Downtime.Merge(Episode("m1", days[0], 10, 95, "Wrzeciono"));
        orchestrator.Downtime.Merge(Episode("m1", days[^1], 15, 5, "Tekst sprzed słownika", category: null));
    }

    // ── Okno jak Panel ──────────────────────────────────────────────────────────────────────────

    private sealed class ReportHarness : IDisposable
    {
        private readonly Dictionary<object, object>? _shadowed;
        private readonly List<object> _added = [];

        public ReportHarness(AppOrchestrator orchestrator, double width, double height, AppTheme theme)
        {
            if (theme == AppTheme.Dark)
            {
                ResourceDictionary resources = System.Windows.Application.Current.Resources;
                _shadowed = [];
                foreach (string key in ThemePalette.Keys)
                {
                    if (resources.Keys.Cast<object>().Contains(key)) _shadowed[key] = resources[key];
                    else _added.Add(key);
                }
                ThemePalette.ApplyTo(resources, theme);
            }
            View = new DowntimeView(orchestrator);
            var outer = new TabControl { Margin = new Thickness(16) };
            var report = new TabItem { Header = "Raport postojów", Content = View };
            outer.Items.Add(report);
            foreach (string header in new[] { "Instrukcje naprawy", "Praca na kilku komputerach", "Ustawienia", "Podgląd techniczny" })
                outer.Items.Add(new TabItem { Header = header, Content = new TextBlock() });
            outer.SelectedItem = report;
            var root = new DockPanel();
            var band = new Border { Height = 92 };
            DockPanel.SetDock(band, Dock.Top);
            root.Children.Add(band);
            root.Children.Add(outer);
            Window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = width, Height = height });
            Settle();
            // Jak w SharedConfigViewLayoutTests: obszar klienta o żądanym rozmiarze niezależnie od ekranu (runner CI).
            double frameWidth = Window.ActualWidth - root.ActualWidth;
            double frameHeight = Window.ActualHeight - root.ActualHeight;
            root.Width = width - frameWidth;
            root.Height = height - frameHeight;
            Settle();
        }

        public Window Window { get; }
        public DowntimeView View { get; }
        public ScrollViewer Scroll => (ScrollViewer)View.Content;
        public ChartCanvas[] Charts => [View.MachinesChart, View.KindsChart, View.ErrorsChart, View.ErrorMachinesChart, View.DaysChart];

        public void Settle()
        {
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            Window.UpdateLayout();
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        }

        /// <summary>Wykresy przeliczane od razu (bez czekania 5 s): cofnięcie chwili ostatniego przeliczenia.</summary>
        public void RefreshNow()
        {
            typeof(DowntimeView).GetField("_chartsAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(View, DateTimeOffset.MinValue);
            View.Refresh();
            Settle();
        }

        public void Dispose()
        {
            Window.Close();
            if (_shadowed is null) return;
            ResourceDictionary resources = System.Windows.Application.Current.Resources;
            foreach ((object key, object value) in _shadowed) resources[key] = value;
            foreach (object key in _added) resources.Remove(key);
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        }
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

    private static Color ColorOf(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Rect Bounds(Canvas canvas, FrameworkElement element)
    {
        double left = Canvas.GetLeft(element), top = Canvas.GetTop(element);
        return new Rect(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top, element.ActualWidth, element.ActualHeight);
    }

    /// <summary>Napisy i kształty na płótnie wykresu leżą w jego granicach, a napisy nie zachodzą na siebie.</summary>
    private static List<string> LayoutProblems(ChartCanvas chart, string label)
    {
        var problems = new List<string>();
        var placed = chart.Children.OfType<FrameworkElement>().Where(e => e is TextBlock or System.Windows.Shapes.Rectangle or Ellipse).ToArray();
        foreach (var element in placed)
        {
            Rect r = Bounds(chart, element);
            if (r.Left < -0.5 || r.Top < -0.5 || r.Right > chart.ActualWidth + 0.5 || r.Bottom > chart.ActualHeight + 0.5)
                problems.Add($"{label}: {Describe(element)} {r.Left:F1},{r.Top:F1}–{r.Right:F1},{r.Bottom:F1} poza płótnem {chart.ActualWidth:F1}×{chart.ActualHeight:F1}");
        }
        var texts = placed.OfType<TextBlock>().ToArray();
        for (int i = 0; i < texts.Length; i++)
            for (int j = i + 1; j < texts.Length; j++)
            {
                Rect a = Bounds(chart, texts[i]), b = Bounds(chart, texts[j]);
                a.Inflate(-1, -1);
                b.Inflate(-1, -1);
                if (a.IntersectsWith(b))
                    problems.Add($"{label}: napisy zachodzą na siebie: „{texts[i].Text}” {Bounds(chart, texts[i])} i „{texts[j].Text}” {Bounds(chart, texts[j])}");
            }
        return problems;
    }

    private static string Describe(FrameworkElement element) => element is TextBlock t ? $"„{t.Text}”" : element.GetType().Name;

    private static void ApplyDark(Action body)
    {
        ResourceDictionary resources = System.Windows.Application.Current.Resources;
        var shadowed = new Dictionary<object, object>();
        var added = new List<object>();
        foreach (string key in ThemePalette.Keys)
        {
            if (resources.Keys.Cast<object>().Contains(key)) shadowed[key] = resources[key];
            else added.Add(key);
        }
        try
        {
            ThemePalette.ApplyTo(resources, AppTheme.Dark);
            body();
        }
        finally
        {
            foreach ((object key, object value) in shadowed) resources[key] = value;
            foreach (object key in added) resources.Remove(key);
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        }
    }

    // ── Jasny i ciemny motyw: render bez wyjątku, kolory z palety, brak jasnych powierzchni ──

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ChartsRenderInBothThemes_SeriesAndTextColorsFollowTheTheme(AppTheme theme)
    {
        var orchestrator = Orchestrator(5);
        FillTypicalReport(orchestrator, 5);
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, theme);
            panel.RefreshNow();
            IReadOnlyDictionary<string, Color> palette = ThemePalette.ColorsFor(theme);
            bool dark = theme == AppTheme.Dark;
            var log = new StringBuilder();
            foreach (ChartCanvas chart in panel.Charts)
            {
                Assert.True(chart.Children.Count > 5, $"{chart.Name}: pusty wykres ({chart.Children.Count} elementów)");
                Assert.True(chart.ActualHeight >= ChartCanvas.MinChartHeight, $"{chart.Name}: {chart.ActualHeight}");
                foreach (TextBlock text in chart.Children.OfType<TextBlock>())
                {
                    Color color = ((SolidColorBrush)text.Foreground).Color;
                    Assert.True(color == palette["TextPrimaryBrush"] || color == palette["TextSecondaryBrush"],
                        $"{chart.Name}: napis „{text.Text}” w kolorze {color} spoza motywu");
                }
                log.AppendLine(CultureInfo.InvariantCulture, $"{theme} {chart.Name}: {chart.ActualWidth:F0}×{chart.ActualHeight:F0}, elementów {chart.Children.Count}");
            }
            Color first = ColorOf(ChartPalette.Slot(0).For(dark));
            Assert.Contains(panel.View.MachinesChart.Children.OfType<System.Windows.Shapes.Rectangle>(),
                r => r.Fill is SolidColorBrush { Color: var c } && c == first && r.Width > 2);
            Color other = ColorOf(ChartPalette.Slot(0).For(!dark));
            if (first != other)
                Assert.DoesNotContain(panel.View.MachinesChart.Children.OfType<System.Windows.Shapes.Rectangle>(),
                    r => r.Fill is SolidColorBrush { Color: var c } && c == other);
            Assert.Contains(panel.View.ErrorsChart.Children.OfType<System.Windows.Shapes.Path>(),
                p => p.Stroke is SolidColorBrush { Color: var c } && c == palette["CardBackgroundBrush"]);
            return log.ToString();
        });
        output.WriteLine(report);
    }

    [Fact]
    public void DarkTheme_NoLightSurfacesInTheReportWithCharts()
    {
        var orchestrator = Orchestrator(4);
        FillTypicalReport(orchestrator, 4);
        WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Dark);
            panel.RefreshNow();
            // Ta sama reguła, co smoke `sprawdz` (VisualSmokeTest.AssertNoLightSurfaces): przewinięcie do wykresów, żeby
            // ScrollViewer zrealizował całe drzewo.
            panel.Scroll.ScrollToEnd();
            panel.Settle();
            var check = typeof(App).Assembly.GetType("MaszynyAlert.App.Services.VisualSmokeTest")!
                .GetMethod("AssertNoLightSurfaces", BindingFlags.NonPublic | BindingFlags.Static)!;
            try { check.Invoke(null, [panel.Window, ThemePalette.ColorsFor(AppTheme.Dark), "Raport postojów z wykresami"]); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
            return 0;
        });
    }

    /// <summary>Przełączenie motywu przy otwartym widoku rysuje wykresy od nowa — kolory serii z wariantu ciemnego i z powrotem.</summary>
    [Fact]
    public void ThemeSwitchWhileOpen_RedrawsWithTheOtherSeriesColors()
    {
        var orchestrator = Orchestrator(3);
        FillTypicalReport(orchestrator, 3);
        WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Light);
            panel.RefreshNow();
            UIElement[] before = panel.Charts.Select(c => c.Children[0]).ToArray();
            Color light = ColorOf(ChartPalette.Slot(0).LightArgb), dark = ColorOf(ChartPalette.Slot(0).DarkArgb);
            bool Has(Color color) => panel.View.MachinesChart.Children.OfType<System.Windows.Shapes.Rectangle>()
                .Any(r => r.Fill is SolidColorBrush { Color: var c } && c == color && r.Width > 2);
            Assert.True(Has(light));

            ApplyDark(() =>
            {
                panel.Settle();
                for (int i = 0; i < before.Length; i++)
                    Assert.NotSame(before[i], panel.Charts[i].Children[0]);
                Assert.True(Has(dark), "po przełączeniu na ciemny słupki nadal w kolorze jasnym");
                Assert.False(Has(light));
            });
            panel.Settle();
            Assert.True(Has(light), "po powrocie do jasnego słupki nie wróciły do koloru jasnego");
            return 0;
        });
    }

    // ── Układ: sekcja w ScrollViewer, 1024×768, 700 px, 1220×850 ─────────────────────

    [Theory]
    [InlineData(1024, 768, AppTheme.Light)]
    [InlineData(1024, 768, AppTheme.Dark)]
    [InlineData(1220, 850, AppTheme.Light)]
    [InlineData(700, 850, AppTheme.Light)]
    public void ChartsSectionIsInTheScrollViewer_ReadableAtPanelSizes(int width, int height, AppTheme theme)
    {
        var orchestrator = Orchestrator(9);
        FillTypicalReport(orchestrator, 9);
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, width, height, theme);
            panel.RefreshNow();
            ScrollViewer scroll = panel.Scroll;
            var log = new StringBuilder();
            var failures = new List<string>();
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            var content = (FrameworkElement)scroll.Content;
            double gridBottom = panel.View.EpisodesGrid.TransformToAncestor(content).Transform(new Point(0, panel.View.EpisodesGrid.ActualHeight)).Y;
            foreach (ChartCanvas chart in panel.Charts)
            {
                Assert.True(Descendants<ChartCanvas>(scroll).Contains(chart), $"{chart.Name} poza ScrollViewer");
                Rect r = chart.TransformToAncestor(content).TransformBounds(new Rect(0, 0, chart.ActualWidth, chart.ActualHeight));
                log.AppendLine(CultureInfo.InvariantCulture, $"{width}×{height} {theme} {chart.Name}: {r.Left:F0},{r.Top:F0} {r.Width:F0}×{r.Height:F0} (widok {scroll.ViewportWidth:F0}×{scroll.ViewportHeight:F0})");
                if (r.Top < gridBottom) failures.Add($"{chart.Name} nad tabelą postojów ({r.Top:F0} < {gridBottom:F0})");
                if (chart.ActualHeight < ChartCanvas.MinChartHeight - 0.5) failures.Add($"{chart.Name}: wysokość {chart.ActualHeight:F1} < 220");
                if (chart.ActualHeight > scroll.ViewportHeight) failures.Add($"{chart.Name}: wyższy niż widok ({chart.ActualHeight:F0} > {scroll.ViewportHeight:F0}) — nie da się zobaczyć całego");
                if (r.Right > scroll.ViewportWidth + 0.5) failures.Add($"{chart.Name}: prawa krawędź {r.Right:F0} > widok {scroll.ViewportWidth:F0}");
                if (chart.ActualWidth < 400) failures.Add($"{chart.Name}: szerokość {chart.ActualWidth:F0} < 400");
                failures.AddRange(LayoutProblems(chart, $"{width}×{height} {chart.Name}"));
            }
            Assert.True(scroll.ExtentHeight >= panel.View.DaysChart.TransformToAncestor(content).Transform(new Point(0, panel.View.DaysChart.ActualHeight)).Y);
            Assert.Equal(Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility);
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures) + "\n\n" + log);
            return log.ToString();
        });
        output.WriteLine(report);
    }

    // ── Słupkowy: wysokość dla 1 / 9 / 12 maszyn ────────────────────────────────────────────

    [Theory]
    [InlineData(1, 220)]
    [InlineData(5, 228)]
    [InlineData(9, 348)]
    [InlineData(11, 408)]
    [InlineData(12, 438)]
    public void BarChartHeightGrowsWithMachines_LabelsDoNotOverlap(int machines, double expected)
    {
        var orchestrator = Orchestrator(machines);
        FillTypicalReport(orchestrator, machines);
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1024, 768, AppTheme.Light);
            panel.RefreshNow();
            BarChartView bar = panel.View.MachinesChart;
            Assert.Equal(expected, BarChartView.HeightFor(bar.Data));
            Assert.Equal(expected, bar.ActualHeight, 1);
            Assert.Equal(machines, bar.Data!.Categories.Count);
            var labels = bar.Children.OfType<TextBlock>().Where(t => bar.Data.Categories.Contains(t.Text)).ToArray();
            Assert.Equal(machines, labels.Length);
            var problems = LayoutProblems(bar, $"{machines} maszyn");
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
            return string.Create(CultureInfo.InvariantCulture, $"{machines} maszyn: wysokość {bar.ActualHeight:F0} DIP, szerokość {bar.ActualWidth:F0}");
        });
        output.WriteLine(report);
    }

    // ── Kołowy: legenda 8 pozycji ────────────────────────────────────────────────────────────

    private static PieChartData PieWithEightSlices()
    {
        var day = new DateOnly(2026, 9, 15);
        DowntimeSlice Row(string reason, double minutes, string category = "inny") =>
            new(day, "06:00–14:00", "A", reason, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(minutes), false, false)
            { Category = category, MachineId = "a", EpisodeId = Guid.NewGuid() };
        var rows = Enumerable.Range(0, 10).Select(i => Row(i == 0 ? "Tacka nie pobrana" : $"Bardzo długa nazwa błędu z OCR numer {i} — sprawdź wrzeciono i zacisk", 120 - i * 9))
            .Append(Row("Stary tekst", 7, "nierozpoznany")).ToArray();
        var pie = DowntimeCharts.Build(rows, new DowntimeChartOptions { From = day, To = day, Zone = TimeZoneInfo.Utc,
            GeneratedAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero) }).Errors;
        Assert.Equal(8, pie.Slices.Count);
        return pie;
    }

    private static T Host<T>(T chart, double width, Action<T> setData) where T : ChartCanvas
    {
        var card = new Border { Style = (Style)System.Windows.Application.Current.Resources["CardStyle"], Child = chart, Width = width };
        var window = WpfTestHost.OffScreen(new Window { Content = card, SizeToContent = SizeToContent.Height, Width = width + 40 });
        setData(chart);
        window.Show();
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        return chart;
    }

    [Theory]
    [InlineData(560)]
    [InlineData(860)]
    [InlineData(1100)]
    public void PieLegendWithEightEntriesFits_AndDoesNotOverlapTheCircle(double width)
    {
        string report = WpfTestHost.Run(() =>
        {
            var data = PieWithEightSlices();
            var pie = Host(new PieChartView(), width, p => p.Data = data);
            try
            {
                var problems = LayoutProblems(pie, $"kołowy {width}");
                var shapes = pie.Children.OfType<System.Windows.Shapes.Path>().ToArray();
                Assert.Equal(8, shapes.Length);
                double circleRight = shapes.Max(s => s.Data.Bounds.Right);
                var labels = pie.Children.OfType<TextBlock>().ToArray();
                foreach (var slice in data.Slices)
                    Assert.Contains(labels, t => t.Text == slice.Label);
                Assert.Contains(labels, t => t.Text == "Razem");
                foreach (TextBlock text in labels)
                    if (Canvas.GetLeft(text) < circleRight) problems.Add($"napis „{text.Text}” ({Canvas.GetLeft(text):F0}) na kole (do {circleRight:F0})");
                if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
                return string.Create(CultureInfo.InvariantCulture, $"{width}: płótno {pie.ActualWidth:F0}×{pie.ActualHeight:F0}, koło do {circleRight:F0}");
            }
            finally { Window.GetWindow(pie)!.Close(); }
        });
        output.WriteLine(report);
    }

    // ── Liniowy: 1 dzień i 60 dni ────────────────────────────────────────────────────────────

    private static LineChartData Line(int workingDays, Func<int, double> minutes, bool perMachine = false)
    {
        var from = new DateOnly(2026, 6, 1); // poniedziałek
        var days = new List<DateOnly>();
        for (var d = from; days.Count < workingDays; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
        var rows = days.Select((d, i) => new DowntimeSlice(d, "06:00–14:00", "A", "Servo 6", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(minutes(i)), false, false) { MachineId = "a", EpisodeId = Guid.NewGuid() })
            .Where(r => r.Minutes > 0).ToArray();
        var line = DowntimeCharts.Build(rows, new DowntimeChartOptions
        {
            From = from, To = days[^1], Zone = TimeZoneInfo.Utc, LinePerMachine = perMachine,
            GeneratedAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        }).Days;
        Assert.Equal(workingDays, line.Days.Count);
        return line;
    }

    [Theory]
    [InlineData(560)]
    [InlineData(860)]
    public void LineChart_OneDay_MarkersWithoutLine(double width)
    {
        WpfTestHost.Run(() =>
        {
            var view = Host(new LineChartView(), width, l => l.Data = Line(1, _ => 12));
            try
            {
                Assert.Empty(view.Children.OfType<Polyline>());
                Assert.Equal(3, view.Children.OfType<Ellipse>().Count()); // trzy serie sumy (wszystkie, bez długich, błędy), po jednym punkcie
                Assert.Contains(view.Children.OfType<TextBlock>(), t => t.Text == "01.06");
                var problems = LayoutProblems(view, "liniowy 1 dzień");
                if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
            }
            finally { Window.GetWindow(view)!.Close(); }
            return 0;
        });
    }

    [Theory]
    [InlineData(560)]
    [InlineData(860)]
    public void LineChart_SixtyDays_PolylineWithoutMarkers_DayLabelsDoNotOverlap(double width)
    {
        string report = WpfTestHost.Run(() =>
        {
            var view = Host(new LineChartView(), width, l => l.Data = Line(60, i => 5 + i % 7 * 4));
            try
            {
                var lines = view.Children.OfType<Polyline>().ToArray();
                Assert.Equal(3, lines.Length);
                Assert.All(lines, p => Assert.Equal(60, p.Points.Count));
                Assert.Empty(view.Children.OfType<Ellipse>());
                Assert.True(lines[0].Points.Zip(lines[0].Points.Skip(1)).All(p => p.First.X < p.Second.X));
                var problems = LayoutProblems(view, $"liniowy 60 dni, {width}");
                if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
                int dayLabels = view.Children.OfType<TextBlock>().Count(t => t.Text.Length == 5 && t.Text[2] == '.');
                return string.Create(CultureInfo.InvariantCulture, $"{width}: podpisów dni {dayLabels} z 60");
            }
            finally { Window.GetWindow(view)!.Close(); }
        });
        output.WriteLine(report);
    }

    /// <summary>
    /// Podpis maksimum pierwszej serii stoi 14 DIP nad punktem. Gdy maksimum wypada na początku osi i równa się górze skali
    /// (NiceAxis: 100 → oś do 100), podpis trafia w wiersz legendy w lewym górnym rogu.
    /// </summary>
    [Fact]
    public void LineChart_MaximumLabelDoesNotOverlapTheLegend()
    {
        WpfTestHost.Run(() =>
        {
            var view = Host(new LineChartView(), 860, l => l.Data = Line(5, i => i == 0 ? 100 : 20));
            try
            {
                Assert.Contains(view.Children.OfType<TextBlock>(), t => t.Text == "100,0 min");
                var problems = LayoutProblems(view, "liniowy, maksimum przy legendzie");
                if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
            }
            finally { Window.GetWindow(view)!.Close(); }
            return 0;
        });
    }

    // ── Rysowanie tylko po zmianie ContentKey; zmiana filtra — od razu ───────────────────

    [Fact]
    public void RefreshRedrawsOnlyWhenContentKeyChanges_FilterChangeRedrawsImmediately()
    {
        var orchestrator = Orchestrator(3);
        FillTypicalReport(orchestrator, 3);
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Light);
            panel.RefreshNow();
            var draws = new int[3];
            var last = panel.Charts.Select(c => c.Children[0]).ToArray();
            void Count()
            {
                for (int i = 0; i < 3; i++)
                    if (!ReferenceEquals(last[i], panel.Charts[i].Children[0])) { draws[i]++; last[i] = panel.Charts[i].Children[0]; }
            }

            // 10 odświeżeń Panelu (co sekundę) bez zmian — i 10 wymuszonych przeliczeń modelu: ten sam klucz, nic się nie rysuje.
            for (int i = 0; i < 10; i++) { panel.View.Refresh(); panel.Settle(); Count(); }
            for (int i = 0; i < 10; i++) { panel.RefreshNow(); Count(); }
            Assert.Equal([0, 0, 0], draws);
            string header = panel.View.ChartsHeader.Text;
            Assert.Contains("Wygenerowano:", header);

            // Nowy postój: po przeliczeniu rysują się słupki, kołowy i liniowy.
            orchestrator.Downtime.Merge(Episode("m2", RecentWorkingDays(1)[0], 12, 30, "Nowy błąd"));
            panel.RefreshNow();
            Count();
            Assert.Equal([1, 1, 1], draws);

            // Zmiana filtra zmiany: od razu (bez czekania 5 s), bez cofania _chartsAt.
            panel.View.ShiftFilter.SelectedIndex = 2;
            panel.Settle();
            Count();
            Assert.Equal([2, 2, 2], draws);
            Assert.Contains("Zmiana: 14:00–22:00", panel.View.ChartsHeader.Text);
            return string.Join(", ", draws);
        });
        output.WriteLine(report);
    }

    // ── Osobno dla każdej maszyny ────────────────────────────────────────────────────────

    [Fact]
    public void LinePerMachineCheckbox_SwitchesBetweenSumAndMachineLines()
    {
        var orchestrator = Orchestrator(4);
        FillTypicalReport(orchestrator, 4);
        WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Light);
            panel.RefreshNow();
            Assert.Equal([DowntimeCharts.AllName, DowntimeCharts.WithoutLongName, DowntimeCharts.ErrorsName], panel.View.DaysChart.Data!.Series.Select(s => s.Name));

            panel.View.LinePerMachine.IsChecked = true;
            panel.Settle();
            Assert.True(panel.View.DaysChart.Data!.PerMachine);
            Assert.Equal(4, panel.View.DaysChart.Data.Series.Count);
            Assert.All(panel.View.DaysChart.Data.Series, s => Assert.StartsWith("Maszyna ", s.Name));
            Assert.Contains("Każda linia", panel.View.DaysChartSubtitle.Text);

            panel.View.LinePerMachine.IsChecked = false;
            panel.Settle();
            Assert.Equal(3, panel.View.DaysChart.Data!.Series.Count);
            return 0;
        });
    }


    private static DowntimeEpisode IdleEpisode(string machineId, DateOnly day, int startHour, double minutes, bool atZero = false) =>
        Episode(machineId, day, startHour, minutes,
            atZero ? AlertStateMachine.MachineIdleAtZeroShortText : AlertStateMachine.MachineIdleShortText,
            atZero ? AlertStateMachine.IdleAtZeroCategory : AlertStateMachine.IdleCategory);

    [Fact]
    public void IncludeIdleCheckbox_DefaultOn_SwitchingOffChangesContentKeyAndShowsOnlyErrors()
    {
        var orchestrator = Orchestrator(3);
        FillTypicalReport(orchestrator, 3);
        var day = RecentWorkingDays(1)[0];
        orchestrator.Downtime.Merge(IdleEpisode("m2", day, 6, 300));
        orchestrator.Downtime.Merge(IdleEpisode("m3", day, 13, 40, atZero: true));
        WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Light);
            panel.RefreshNow();
            Assert.True(panel.View.IncludeIdleCharts.IsChecked);
            string keyOn = (string)typeof(DowntimeView).GetField("_chartsContentKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel.View)!;
            Assert.Equal([DowntimeCharts.ErrorsName, DowntimeCharts.IdleName, DowntimeCharts.IdleAtZeroName],
                panel.View.MachinesChart.Data!.Series.Select(s => s.Name));
            Assert.Equal(3, panel.View.KindsChart.Data!.Slices.Count);
            Assert.DoesNotContain(panel.View.ErrorsChart.Data!.Slices, s => s.Label.Contains("Stoi", StringComparison.Ordinal));
            Assert.Contains("błędy maszyn", panel.View.TotalTimeByKind.Text, StringComparison.Ordinal);
            Assert.Contains("W tym błędów maszyn: ", panel.View.EpisodeCountByKind.Text, StringComparison.Ordinal);

            // Wyłączenie: od razu (bez czekania 5 s), nowy klucz treści, słupki i linia tylko z błędów, rodzaje bez zmian.
            panel.View.IncludeIdleCharts.IsChecked = false;
            panel.Settle();
            string keyOff = (string)typeof(DowntimeView).GetField("_chartsContentKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel.View)!;
            Assert.NotEqual(keyOn, keyOff);
            Assert.Equal([DowntimeCharts.ErrorsName], panel.View.MachinesChart.Data!.Series.Select(s => s.Name));
            Assert.Equal([DowntimeCharts.ErrorsName, DowntimeCharts.ErrorsWithoutLongName], panel.View.DaysChart.Data!.Series.Select(s => s.Name));
            Assert.Equal(3, panel.View.KindsChart.Data!.Slices.Count);
            Assert.Contains("ukryte", panel.View.MachinesChartSubtitle.Text, StringComparison.Ordinal);

            panel.View.IncludeIdleCharts.IsChecked = true;
            panel.Settle();
            Assert.Equal(keyOn, (string)typeof(DowntimeView).GetField("_chartsContentKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel.View)!);
            return 0;
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ChartsSnapshot_RenderedToPng_ForHumanReview(AppTheme theme)
    {
        var orchestrator = Orchestrator(5);
        FillTypicalReport(orchestrator, 5);
        var days = RecentWorkingDays(3);
        for (int i = 1; i <= 5; i++)
            foreach (var day in days)
                orchestrator.Downtime.Merge(IdleEpisode("m" + i, day, 6 + i % 3, 150 + i * 20));
        orchestrator.Downtime.Merge(IdleEpisode("m2", days[0], 14, 35, atZero: true));
        string path = WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, theme);
            panel.RefreshNow();
            var content = (FrameworkElement)panel.Scroll.Content;
            var charts = (FrameworkElement)panel.View.MachinesChart.Parent;
            double top = charts.TransformToAncestor(content).Transform(new Point(0, 0)).Y - 60;
            double height = Math.Min(content.ActualHeight - top, 2400);
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                    Viewbox = new Rect(0, top, content.ActualWidth, height), ViewboxUnits = BrushMappingMode.Absolute },
                    null, new Rect(0, 0, content.ActualWidth, height));
            }
            bitmap.Render(visual);
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "r2-zrzuty");
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, $"raport-wykresy-{theme.ToString().ToLowerInvariant()}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
            return file;
        });
        Assert.True(new FileInfo(path).Length > 10_000, path);
        output.WriteLine(path);
    }

    [Fact]
    public void OnlyIdleDowntime_ErrorChartsSayNoErrors_KindsChartHasIdleOnly()
    {
        var orchestrator = Orchestrator(2);
        var day = RecentWorkingDays(1)[0];
        orchestrator.Downtime.Merge(IdleEpisode("m1", day, 7, 120));
        orchestrator.Downtime.Merge(IdleEpisode("m2", day, 9, 30, atZero: true));
        WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Light);
            panel.RefreshNow();
            string Texts(ChartCanvas chart) => string.Join(" | ", chart.Children.OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("nie ma błędów maszyn", Texts(panel.View.ErrorsChart));
            Assert.Contains("nie ma błędów maszyn", Texts(panel.View.ErrorMachinesChart));
            Assert.Empty(panel.View.ErrorMachinesChart.Children.OfType<System.Windows.Shapes.Rectangle>());
            Assert.Equal(2, panel.View.KindsChart.Data!.Slices.Count);
            Assert.NotEmpty(panel.View.MachinesChart.Children.OfType<System.Windows.Shapes.Rectangle>());
            // Kreskowanie długiego postoju bez pracy (120 min > 1 h): legenda kreskowania i co najmniej jeden pasek wzoru.
            Assert.Contains(BarChartView.LongLegendText, Texts(panel.View.MachinesChart));
            Assert.Contains(panel.View.MachinesChart.Children.OfType<System.Windows.Shapes.Rectangle>(), r => r.Fill is DrawingBrush);
            return 0;
        });
    }

    // ── Pusty raport, zły zakres, adnotacja „brak danych” ────────────────────────────────

    [Fact]
    public void EmptyReport_ShowsThreeNoDowntimeTexts_ReversedRangeSaysInvalid()
    {
        var orchestrator = Orchestrator(2);
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Light);
            panel.RefreshNow();
            string Texts(ChartCanvas chart) => string.Join(" | ", chart.Children.OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("nie ma postojów", Texts(panel.View.MachinesChart));
            Assert.Contains("nie ma postojów", Texts(panel.View.ErrorsChart));
            Assert.Empty(panel.View.MachinesChart.Children.OfType<System.Windows.Shapes.Rectangle>());
            string line = Texts(panel.View.DaysChart);

            panel.View.FromDate.SelectedDate = DateTime.Today;
            panel.View.ToDate.SelectedDate = DateTime.Today.AddDays(-3);
            panel.Settle();
            Assert.Contains("zakres dat jest niepoprawny", panel.View.ChartsHeader.Text);
            Assert.Contains("Brak dni roboczych", Texts(panel.View.DaysChart));
            Assert.Equal(Visibility.Visible, panel.View.ReportWarningBox.Visibility);
            return "liniowy przy pustym raporcie: " + line;
        });
        output.WriteLine(report);
    }

    [Fact]
    public void NoDataAnnotation_ShowsNoConnectionTimeUnderTheBarChart()
    {
        var orchestrator = Orchestrator(2);
        FillTypicalReport(orchestrator, 2);
        var day = RecentWorkingDays(1)[0];
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(9, 0))));
        orchestrator.Observation.Apply(
        [
            new ObservationInterval("m1", ObservationState.Observed, start.AddDays(-20), start.AddDays(-20).AddHours(1)),
            new ObservationInterval("m2", ObservationState.NoConnection, start, start.AddMinutes(150)),
        ]);
        WpfTestHost.Run(() =>
        {
            using var panel = new ReportHarness(orchestrator, 1220, 850, AppTheme.Light);
            panel.RefreshNow();
            Assert.Equal(Visibility.Visible, panel.View.NoDataText.Visibility);
            Assert.Contains("Brak danych - brak połączenia z maszyną (poza sumami postojów): Maszyna 2 — 2 godz. 30 min.", panel.View.NoDataText.Text);
            return 0;
        });
    }
}
