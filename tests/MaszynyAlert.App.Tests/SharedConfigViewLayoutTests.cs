using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Config;
using Xunit.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class SharedConfigViewLayoutTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Light, true)]
    [InlineData(AppTheme.Dark, false)]
    [InlineData(AppTheme.Dark, true)]
    public void EverySectionFitsWithoutScrollingAt1220x850(AppTheme theme, bool inGroup)
    {
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(1220, 850, theme, inGroup);
            var log = new StringBuilder();
            var failures = new List<string>();
            log.AppendLine(panel.Breakdown());
            foreach (SharedConfigSection section in panel.ViewModel.Sections)
            {
                panel.ViewModel.SelectedSection = section;
                foreach (SharedConfigField field in section.Fields)
                {
                    panel.ViewModel.SelectedField = field;
                    panel.Settle();
                    ScrollViewer rows = panel.RowsViewer;
                    log.AppendLine(CultureInfo.InvariantCulture, $"{theme}/{(inGroup ? "grupa" : "sam")} [{section.Title}] {field.Key}: widok {rows.ViewportHeight:F1}, treść {rows.ExtentHeight:F1}, pasek {rows.ComputedVerticalScrollBarVisibility}");
                    if (rows.ScrollableHeight > 0.5 || rows.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                        failures.Add($"[{section.Title}] zaznaczone {field.Key}: wiersze {rows.ExtentHeight:F1} DIP > miejsce {rows.ViewportHeight:F1} DIP");
                }
            }

            if (failures.Count > 0)
                throw new InvalidOperationException($"{theme}, {(inGroup ? "w grupie" : "jeden komputer")}: sekcja nie mieści się bez przewijania na 1220×850:\n{string.Join("\n", failures)}\n\n{log}");
            return log.ToString();
        });
        output.WriteLine(report);
    }

    [Theory]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Dark, true)]
    public void MachinesSectionWithTwelveMachinesFitsWithoutScrollingAt1220x850(AppTheme theme, bool inGroup)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        var machines = ConfigLoader.LoadAppConfig(Path.Combine(dir!.FullName, "config", "config.example.json")).Machines;
        Assert.Equal(12, machines.Count);

        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(1220, 850, theme, inGroup, machines);
            SharedConfigSection section = panel.ViewModel.Sections.Single(s => s.Title == SharedConfigViewModel.MachinesSectionTitle);
            Assert.Equal(12, section.Fields.Count);
            panel.ViewModel.SelectedSection = section;
            panel.ViewModel.SelectedField = section.Fields[^1];
            panel.Settle();
            ScrollViewer rows = panel.RowsViewer;
            string log = $"{theme}/{(inGroup ? "grupa" : "sam")} [Maszyny]: widok {rows.ViewportHeight:F1}, treść {rows.ExtentHeight:F1}, pasek {rows.ComputedVerticalScrollBarVisibility}";
            if (rows.ScrollableHeight > 0.5 || rows.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                throw new InvalidOperationException("Sekcja „Maszyny” nie mieści się bez przewijania na 1220×850: " + log);
            // Wiersz maszyny: pole wyboru z nazwą, pole w kolumnie wartości ukryte (żaden TextBox/ComboBox nie jest widoczny).
            FrameworkElement row = panel.RowOf(section.Fields[0]);
            Assert.Contains(Descendants<CheckBox>(row), c => c.Visibility == Visibility.Visible);
            Assert.DoesNotContain(Descendants<TextBox>(row), t => t.Visibility == Visibility.Visible);
            return log;
        });
        output.WriteLine(report);
    }

    /// <summary>Błąd walidacji w panelu szczegółów (najdłuższy komunikat) i zdanie po zapisie też nie wymuszają przewijania.</summary>
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ConnectionSectionWithAValidationErrorStillFits(AppTheme theme)
    {
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(1220, 850, theme, inGroup: true);
            panel.ViewModel.Fields.Single(f => f.Key == "timing.idleUpdateTimeoutMs").Text = "0.099";
            panel.ViewModel.SaveCommand.Execute(null);
            panel.Settle();

            Assert.Equal("Połączenie z maszyną", panel.ViewModel.SelectedSection!.Title);
            Assert.True(panel.ViewModel.SelectedField!.HasError);
            ScrollViewer rows = panel.RowsViewer;
            string line = $"{theme}: widok {rows.ViewportHeight:F1}, treść {rows.ExtentHeight:F1}, pasek {rows.ComputedVerticalScrollBarVisibility}";
            if (rows.ScrollableHeight > 0.5)
                throw new InvalidOperationException("Sekcja z błędem w panelu szczegółów nie mieści się bez przewijania: " + line);
            return line;
        });
        output.WriteLine(report);
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void At700PixelsNoRowIsCutOffHorizontally(AppTheme theme)
    {
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(700, 850, theme, inGroup: true);
            var log = new StringBuilder();
            var failures = new List<string>();
            foreach (SharedConfigSection section in panel.ViewModel.Sections)
            {
                panel.ViewModel.SelectedSection = section;
                panel.Settle();
                ScrollViewer rows = panel.RowsViewer;
                Assert.Equal(ScrollBarVisibility.Disabled, rows.HorizontalScrollBarVisibility);
                foreach (SharedConfigField field in section.Fields)
                {
                    FrameworkElement row = panel.RowOf(field);
                    foreach (FrameworkElement part in Descendants<FrameworkElement>(row)
                                 .Where(e => e is TextBox or CheckBox or ComboBox || e is TextBlock { Text: var t } && t == field.Unit && t.Length > 0)
                                 .Where(e => e.IsVisible))
                    {
                        Rect bounds = part.TransformToAncestor(rows).TransformBounds(new Rect(0, 0, part.ActualWidth, part.ActualHeight));
                        if (bounds.Right > rows.ViewportWidth + 0.5 || bounds.Left < -0.5)
                            failures.Add($"[{section.Title}] {field.Key}: {part.GetType().Name} {bounds.Left:F1}–{bounds.Right:F1} poza widokiem 0–{rows.ViewportWidth:F1}");
                    }
                }
                log.AppendLine(CultureInfo.InvariantCulture, $"[{section.Title}] widok wierszy {rows.ViewportWidth:F1} DIP, przewijanie pionowe {rows.ComputedVerticalScrollBarVisibility}");
            }

            // Cała zakładka mieści się w szerokości hosta (żaden element nie wystaje poza prawą krawędź).
            foreach (FrameworkElement element in new FrameworkElement[] { panel.View.DetailPanel, panel.View.SectionList, panel.View.MessageText })
            {
                Rect bounds = element.TransformToAncestor(panel.View).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (bounds.Right > panel.View.ActualWidth + 0.5)
                    failures.Add($"{element.Name}: prawa krawędź {bounds.Right:F1} > szerokość zakładki {panel.View.ActualWidth:F1}");
            }

            if (failures.Count > 0) throw new InvalidOperationException("Przy 700 px:\n" + string.Join("\n", failures) + "\n\n" + log);
            return log.ToString();
        });
        output.WriteLine(report);
    }

    /// <summary>Tryb ciemny: zaznaczony wiersz i panel szczegółów biorą kolory z palety motywu (DynamicResource).</summary>
    [Fact]
    public void DarkThemeColorsReachSelectedRowAndDetailPanel()
    {
        WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(1220, 850, AppTheme.Dark, inGroup: false);
            IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);
            panel.ViewModel.SelectedSection = panel.ViewModel.Sections[2];
            panel.ViewModel.SelectedField = panel.ViewModel.SelectedSection.Fields[3];
            panel.Settle();

            Assert.Equal(dark["SurfaceAltBrush"], ((SolidColorBrush)panel.View.DetailPanel.Background).Color);
            var row = (Border)panel.RowOf(panel.ViewModel.SelectedField!);
            Assert.Equal(dark["AccentSoftBrush"], ((SolidColorBrush)row.Background).Color);
            return 0;
        });
    }

    [Fact]
    public void ViewUsesOnlyDynamicResourceBrushes()
    {
        string xaml = System.IO.File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "SharedConfigView.xaml"));
        Assert.DoesNotMatch(@"(Background|Foreground|BorderBrush)=""(?!\{DynamicResource|Transparent)", xaml);
        Assert.DoesNotMatch(@"Value=""#", xaml);
    }


    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void LongMessageNextToTheButtonsStillLeavesRoomForTheRows(AppTheme theme)
    {
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(1220, 850, theme, inGroup: true);
            panel.ViewModel.SelectedSection = panel.ViewModel.Sections.Single(s => s.Title == "Połączenie z maszyną");
            panel.ViewModel.Message = "Nie zapisano wspólnej konfiguracji: The process cannot access the file "
                + @"'C:\ProgramData\MaszynyAlert\shared-config\shared-config.json.lock' because it is being used by another process.";
            panel.Settle();

            ScrollViewer rows = panel.RowsViewer;
            Rect message = panel.View.MessageText.TransformToAncestor(panel.View)
                .TransformBounds(new Rect(0, 0, panel.View.MessageText.ActualWidth, panel.View.MessageText.ActualHeight));
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{theme}: wiersze {rows.ExtentHeight:F1} / miejsce {rows.ViewportHeight:F1}, komunikat {message.Left:F0}–{message.Right:F0} × {message.Height:F1} (zakładka {panel.View.ActualWidth:F0})");
            if (rows.ScrollableHeight > 0.5) throw new InvalidOperationException("Długi komunikat wymusza przewijanie wierszy: " + line);
            if (message.Right > panel.View.ActualWidth + 0.5) throw new InvalidOperationException("Komunikat wychodzi poza zakładkę: " + line);
            if (!panel.View.MessageText.IsVisible) throw new InvalidOperationException("Komunikat niewidoczny: " + line);
            return line;
        });
        output.WriteLine(report);
    }

    [Fact]
    public void ResizingSwitchesBetweenWideAndNarrowLayout()
    {
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(1220, 850, AppTheme.Light, inGroup: true);
            var log = new System.Text.StringBuilder();
            foreach (double width in new double[] { 1220, 700, 1220, 700 })
            {
                panel.Window.Width = width;
                panel.Settle();
                bool wide = panel.View.ActualWidth >= SharedConfigView.WideLayoutMinWidth;
                int column = Grid.GetColumn(panel.View.DetailPanel);
                int row = Grid.GetRow(panel.View.DetailPanel);
                log.AppendLine(CultureInfo.InvariantCulture, $"okno {width}: zakładka {panel.View.ActualWidth:F0}, szczegóły kolumna {column} wiersz {row}, komunikat kolumna {Grid.GetColumn(panel.View.MessageText)}");
                if (width == 1220 && !wide) throw new InvalidOperationException("Przy 1220 px zakładka węższa niż próg układu szerokiego.\n" + log);
                if (wide != (column == 1 && row == 0) || wide != (Grid.GetColumn(panel.View.MessageText) == 1))
                    throw new InvalidOperationException("Układ nie odpowiada szerokości zakładki.\n" + log);
                if (!wide && panel.View.DetailPanel.TransformToAncestor(panel.View).Transform(new System.Windows.Point(panel.View.DetailPanel.ActualWidth, 0)).X > panel.View.ActualWidth + 0.5)
                    throw new InvalidOperationException("Wąsko panel szczegółów wychodzi poza zakładkę.\n" + log);
            }
            return log.ToString();
        });
        output.WriteLine(report);
    }

    [Fact]
    public void Info_1024x768()
    {
        string report = WpfTestHost.Run(() =>
        {
            using var panel = new ConfigHarness(1024, 768, AppTheme.Light, inGroup: true);
            panel.ViewModel.SelectedSection = panel.ViewModel.Sections.Single(s => s.Title == "Połączenie z maszyną");
            panel.Settle();
            ScrollViewer rows = panel.RowsViewer;
            return string.Create(CultureInfo.InvariantCulture,
                $"1024×768: zakładka {panel.View.ActualWidth:F0} DIP ({(panel.View.ActualWidth >= SharedConfigView.WideLayoutMinWidth ? "szeroko" : "wąsko")}), wiersze {rows.ExtentHeight:F1} / miejsce {rows.ViewportHeight:F1}, pasek {rows.ComputedVerticalScrollBarVisibility}");
        });
        output.WriteLine(report);
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

    private sealed class ConfigHarness : IDisposable
    {
        private readonly Dictionary<object, object>? _shadowed;
        private readonly List<object> _added = [];

        public ConfigHarness(double width, double height, AppTheme theme, bool inGroup, IReadOnlyList<MachineConfig>? machines = null)
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
            var config = new AppConfig { Enabled = false, Machines = machines?.ToList() ?? [] };
            ViewModel = new SharedConfigViewModel(
                () => new SharedConfigViewInput(config, config,
                    new SharedConfigDocument { Timing = new TimingConfig(), EditedAt = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), EditedBy = "AIDITEKOMP1" },
                    inGroup ? "Konfiguracja jest wspólna dla grupy; zgodna z 3 z 3 komputerami." : "", null, inGroup,
                    @"C:\ProgramData\MaszynyAlert\shared-config\shared-config.json"),
                (_, _, _) => Task.CompletedTask);
            if (inGroup) ViewModel.Message = "Zapisano. W grupie komputerów zmiana rozejdzie się sama; poza grupą działa na tym komputerze.";
            View = new SharedConfigView(ViewModel);

            var inner = new TabControl { Margin = new Thickness(0, 8, 0, 0) };
            inner.Items.Add(new TabItem { Header = "Konfiguracja", Content = View });
            inner.Items.Add(new TabItem { Header = "Ten komputer", Content = new TextBlock() });
            var outer = new TabControl { Margin = new Thickness(16) };
            foreach (string header in new[] { "Raport postojów", "Instrukcje naprawy", "Praca na kilku komputerach" })
                outer.Items.Add(new TabItem { Header = header, Content = new TextBlock() });
            var settings = new TabItem { Header = "Ustawienia", Content = inner };
            outer.Items.Add(settings);
            outer.Items.Add(new TabItem { Header = "Podgląd techniczny", Content = new TextBlock() });
            outer.SelectedItem = settings;

            var root = new DockPanel();
            var band = new Border { Height = 92 };
            DockPanel.SetDock(band, Dock.Top);
            root.Children.Add(band);
            root.Children.Add(outer);
            Window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = width, Height = height });
            Settle();
            double frameWidth = Window.ActualWidth - root.ActualWidth;
            double frameHeight = Window.ActualHeight - root.ActualHeight;
            root.Width = width - frameWidth;
            root.Height = height - frameHeight;
            Settle();
        }

        public Window Window { get; }
        public SharedConfigView View { get; }
        public SharedConfigViewModel ViewModel { get; }

        public ScrollViewer RowsViewer => (ScrollViewer)View.SettingRows.Parent;

        public FrameworkElement RowOf(SharedConfigField field)
        {
            var presenter = (ContentPresenter)View.SettingRows.ItemContainerGenerator.ContainerFromItem(field);
            return (FrameworkElement)presenter.ContentTemplate.FindName("Row", presenter);
        }

        /// <summary>Gdzie idzie wysokość zakładki (DIP) — do raportu przy niepowodzeniu.</summary>
        public string Breakdown()
        {
            var grid = (Grid)View.Content;
            var right = (Grid)RowsViewer.Parent;
            FrameworkElement Child(Panel panel, int row) => panel.Children.OfType<FrameworkElement>().First(e => Grid.GetRow(e) == row && Grid.GetColumn(e) == 0);
            FrameworkElement firstRow = RowOf(ViewModel.SelectedSection!.Fields[0]);
            return string.Create(CultureInfo.InvariantCulture,
                $"okno {Window.ActualHeight:F1}, zakładka Konfiguracja {View.ActualHeight:F1}: nagłówek {Child(grid, 0).ActualHeight:F1}, "
                + $"środek {Child(grid, 1).ActualHeight:F1} (tytuł sekcji {Child(right, 0).ActualHeight:F1}, wiersze {RowsViewer.ActualHeight:F1}, "
                + $"szczegóły {View.DetailPanel.ActualHeight:F1}), przyciski i komunikaty {Child(grid, 2).ActualHeight:F1}; jeden wiersz {firstRow.ActualHeight:F1} (+2 margines)");
        }

        public void Settle()
        {
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            Window.UpdateLayout();
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
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
}
