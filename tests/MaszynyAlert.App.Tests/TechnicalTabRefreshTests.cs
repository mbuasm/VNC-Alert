using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Machines;
using Xunit.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class TechnicalTabRefreshTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 20, 53, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData("góra")]
    [InlineData("środek")]
    [InlineData("dół")]
    public void OdswiezenieNieRuszaUkladuZakladki(string position)
    {
        string report = WpfTestHost.Run(() =>
        {
            using var tab = new TechnicalTabHarness();
            tab.ShowTechnical();
            tab.Refresh(resumeLog: true);
            tab.Settle();

            ScrollViewer outer = tab.Outer;
            outer.ScrollToVerticalOffset(position switch
            {
                "góra" => 0,
                "środek" => outer.ScrollableHeight / 2,
                _ => outer.ScrollableHeight,
            });
            tab.Settle();
            if (position != "góra" && outer.VerticalOffset <= 0)
                throw new InvalidOperationException("Zakładka nie przewija się — test nie sprawdza pozycji w środku ani na dole.");

            var log = new StringBuilder();
            Dictionary<string, string> before = tab.Measure();
            log.AppendLine($"[{position}] przed: " + Format(before));
            for (int tick = 1; tick <= 6; tick++)
            {
                tab.Refresh(resumeLog: false);
                WpfTestHost.Pump(DispatcherPriority.Render);
                tab.Window.UpdateLayout();
                Dictionary<string, string> rendered = tab.Measure();
                tab.Settle();
                Dictionary<string, string> settled = tab.Measure();
                log.AppendLine($"[{position}] tyknięcie {tick}, render: " + Format(Differences(before, rendered)));
                log.AppendLine($"[{position}] tyknięcie {tick}, koniec: " + Format(Differences(before, settled)));

                string jump = Format(Differences(rendered, settled));
                if (jump.Length > 0)
                    throw new InvalidOperationException($"[{position}] tyknięcie {tick}: klatka po odświeżeniu różni się od stanu końcowego (render → koniec): {jump}\n{log}");
                // Treść logu zmienia się celowo (nowy wpis); jego pozycja i reszta układu — nie.
                string moved = Format(Differences(before, settled).Where(d => d.Key is not (TechnicalTabHarness.LogFirstVisible or TechnicalTabHarness.LogLast)));
                if (moved.Length > 0)
                    throw new InvalidOperationException($"[{position}] tyknięcie {tick}: odświeżenie zmieniło układ zakładki (przed → koniec): {moved}\n{log}");
                if (settled[TechnicalTabHarness.LogLast] != tab.Data.LogLines[^1])
                    throw new InvalidOperationException($"[{position}] tyknięcie {tick}: log nie stoi na najnowszym wpisie.\n{log}");
            }

            return log.ToString();
        });
        output.WriteLine(report);
    }

    /// <summary>
    /// Fokus zostawiony w liście po wcześniejszym kliknięciu administratora (linia logu, linia historii, przycisk alertu,
    /// komórka tabeli). Gdy odświeżenie usuwało zaznaczony kontener, fokus spadał na całą listę, a jej
    /// <c>RequestBringIntoView</c> przewijało stronę — bez kółka i kliknięcia, przy samym tyknięciu zegara.
    /// </summary>
    [Theory]
    [InlineData("linia logu")]
    [InlineData("linia historii")]
    [InlineData("przycisk alertu")]
    [InlineData("komórka Maszyny")]
    public void FokusZostawionyWLiscieNiePrzewijaStronyPrzyTyknieciu(string focus)
    {
        string report = WpfTestHost.Run(() =>
        {
            using var tab = new TechnicalTabHarness();
            tab.ShowTechnical();
            tab.Refresh(resumeLog: true);
            tab.Settle();
            tab.Outer.ScrollToVerticalOffset(tab.Outer.ScrollableHeight / 2);
            tab.Settle();

            UIElement target = focus switch
            {
                "linia logu" => TechnicalTabHarness.Descendants<ListBoxItem>((DependencyObject)tab.Outer.FindName("LogList")).Skip(2).First(),
                "linia historii" => TechnicalTabHarness.Descendants<ListBoxItem>(tab.Find<System.Windows.Controls.ListBox>("Historia (ostatnie 50)")).Last(),
                "przycisk alertu" => TechnicalTabHarness.Descendants<System.Windows.Controls.Button>(tab.Find<ItemsControl>("Alerty aktywne")).First(),
                _ => TechnicalTabHarness.Descendants<DataGridCell>(tab.Find<DataGrid>("Maszyny")).Skip(3 * 12).First(),
            };
            if (!target.Focus())
                throw new InvalidOperationException($"Nie udało się ustawić fokusu ({focus}) — test nic nie sprawdza.");
            tab.Settle();

            var log = new StringBuilder();
            double start = tab.Outer.VerticalOffset;
            for (int tick = 1; tick <= 8; tick++)
            {
                tab.Refresh(resumeLog: false);
                WpfTestHost.Pump(DispatcherPriority.Render);
                tab.Window.UpdateLayout();
                double rendered = tab.Outer.VerticalOffset;
                tab.Settle();
                double settled = tab.Outer.VerticalOffset;
                log.AppendLine($"[{focus}] tyknięcie {tick}: zakładka {start:F2} → render {rendered:F2} → koniec {settled:F2}, fokus: {System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
                if (Math.Abs(rendered - start) > 0.01 || Math.Abs(settled - start) > 0.01)
                    throw new InvalidOperationException($"[{focus}] tyknięcie {tick}: odświeżenie przewinęło zakładkę ({start:F2} → render {rendered:F2} → koniec {settled:F2}).\n{log}");
            }

            return log.ToString();
        });
        output.WriteLine(report);
    }

    [Fact]
    public void FokusZKlawiaturyNadalPokazujeElement()
    {
        WpfTestHost.Run(() =>
        {
            using var tab = new TechnicalTabHarness();
            tab.ShowTechnical();
            tab.Refresh(resumeLog: true);
            tab.Settle();

            tab.Outer.ScrollToVerticalOffset(0);
            tab.Settle();
            var line = TechnicalTabHarness.Descendants<ListBoxItem>((DependencyObject)tab.Outer.FindName("LogList")).Last();
            if (!line.Focus()) throw new InvalidOperationException("Nie udało się ustawić fokusu na linii logu.");
            tab.Settle();
            if (tab.Outer.VerticalOffset <= 0)
                throw new InvalidOperationException("Fokus na linii logu poniżej widoku nie przewinął strony do niej.");

            tab.Outer.ScrollToVerticalOffset(tab.Outer.ScrollableHeight);
            tab.Settle();
            double bottom = tab.Outer.VerticalOffset;
            var picker = tab.Find<System.Windows.Controls.ComboBox>("Maszyny");
            if (!picker.Focus()) throw new InvalidOperationException("Nie udało się ustawić fokusu na polu wyboru maszyny.");
            tab.Settle();
            if (tab.Outer.VerticalOffset >= bottom)
                throw new InvalidOperationException("Fokus na polu wyboru maszyny powyżej widoku nie przewinął strony do niego.");
            return 0;
        });
    }

    [Fact]
    public void OdswiezenieZostawiaZaznaczenieWierszaIWybranaMaszyne()
    {
        WpfTestHost.Run(() =>
        {
            using var tab = new TechnicalTabHarness();
            tab.ShowTechnical();
            tab.Refresh(resumeLog: true);
            tab.Settle();
            var grid = tab.Find<DataGrid>("Maszyny");
            grid.SelectedIndex = 3;
            object selectedRow = grid.SelectedItem;
            object? selectedMachine = tab.Data.SelectedMachine;
            for (int tick = 1; tick <= 3; tick++)
            {
                tab.Refresh(resumeLog: false);
                tab.Settle();
            }

            if (!ReferenceEquals(grid.SelectedItem, selectedRow) || grid.SelectedIndex != 3)
                throw new InvalidOperationException($"Odświeżenie zgubiło zaznaczony wiersz tabeli „Maszyny” (indeks {grid.SelectedIndex}).");
            if (!ReferenceEquals(tab.Data.SelectedMachine, selectedMachine))
                throw new InvalidOperationException("Odświeżenie zmieniło maszynę wybraną w „Pokaż ekran maszyny”.");
        });
    }

    [Fact]
    public void LogPoWejsciuNaZakladkeStoiNaNajnowszymWpisie()
    {
        string report = WpfTestHost.Run(() =>
        {
            using var tab = new TechnicalTabHarness();
            // Panel otwiera się na „Raporcie postojów”: OnIsVisibleChanged odświeża podgląd ze śledzeniem logu,
            // a lista logu nie jest jeszcze w drzewie wizualnym.
            tab.Refresh(resumeLog: true);
            tab.Settle();
            tab.ShowTechnical();
            tab.Settle();

            var log = new StringBuilder();
            for (int tick = 1; tick <= 2; tick++)
            {
                tab.Refresh(resumeLog: false);
                tab.Settle();
                ScrollViewer viewer = tab.LogViewer;
                log.AppendLine($"tyknięcie {tick}: log {viewer.VerticalOffset:F0} z {viewer.ScrollableHeight:F0}, pierwszy widoczny „{tab.Measure()[TechnicalTabHarness.LogFirstVisible]}”");
                if (viewer.ScrollableHeight <= 0)
                    throw new InvalidOperationException("Test nic nie sprawdza — 50 wpisów logu mieści się bez przewijania.");
                if (viewer.VerticalOffset < viewer.ScrollableHeight - 1)
                    throw new InvalidOperationException($"Po wejściu na zakładkę log nie stoi na najnowszym wpisie (tyknięcie {tick}: {viewer.VerticalOffset:F0} z {viewer.ScrollableHeight:F0}).\n{log}");
            }

            tab.LogViewer.ScrollToVerticalOffset(5);
            tab.Settle();
            tab.Refresh(resumeLog: false);
            tab.Settle();
            if (Math.Abs(tab.LogViewer.VerticalOffset - 5) > 0.5)
                throw new InvalidOperationException($"Log przewinięty w górę uciekł z pozycji 5 na {tab.LogViewer.VerticalOffset:F0} przy odświeżeniu.");

            // Powrót na dół wznawia śledzenie.
            tab.LogViewer.ScrollToEnd();
            tab.Settle();
            tab.Refresh(resumeLog: false);
            tab.Settle();
            if (tab.LogViewer.VerticalOffset < tab.LogViewer.ScrollableHeight - 1 || tab.Measure()[TechnicalTabHarness.LogLast] != tab.Data.LogLines[^1])
                throw new InvalidOperationException("Po powrocie na dół log nie wznowił śledzenia najnowszego wpisu.");
            return log.ToString();
        });
        output.WriteLine(report);
    }

    private static IEnumerable<KeyValuePair<string, string>> Differences(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Keys.Union(b.Keys)
            .Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .Select(k => new KeyValuePair<string, string>(k, $"{a.GetValueOrDefault(k) ?? "—"} → {b.GetValueOrDefault(k) ?? "—"}"));

    private static string Format(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("; ", values.Select(v => $"{v.Key} = {v.Value}"));

    /// <summary>
    /// Zakładka „Podgląd techniczny” z prawdziwego StatusWindow.xaml (bez code-behind okna: zdarzenia Click usunięte)
    /// w oknie poza ekranem, obok zakładki „Raport postojów”, na której Panel się otwiera.
    /// </summary>
    private sealed class TechnicalTabHarness : IDisposable
    {
        public const string LogFirstVisible = "log: pierwszy widoczny wpis";
        public const string LogLast = "log: ostatni widoczny wpis";

        private readonly TabControl _tabs;
        private readonly TabItem _technical;
        private readonly LogFollower _follower;
        private int _tick;

        public TechnicalTabHarness()
        {
            Outer = LoadTechnicalTab();
            Data = new TabData();
            _technical = new TabItem { Header = "Podgląd techniczny", Content = Outer };
            _tabs = new TabControl { Margin = new Thickness(16) };
            _tabs.Items.Add(new TabItem { Header = "Raport postojów", Content = new TextBlock { Text = "raport" } });
            _tabs.Items.Add(_technical);
            _tabs.SelectedIndex = 0;
            var root = new DockPanel { DataContext = Data };
            var band = new Border { Height = 92 };
            DockPanel.SetDock(band, Dock.Top);
            root.Children.Add(band);
            root.Children.Add(_tabs);
            Window = WpfTestHost.OffScreen(new Window { Content = root, Width = 1220, Height = 850, WindowStyle = WindowStyle.None });
            _follower = new LogFollower((System.Windows.Controls.ListBox)Outer.FindName("LogList"));
            TechnicalPage.Attach(Outer);
            Window.Show();
            Settle();
        }

        public Window Window { get; }
        public ScrollViewer Outer { get; }
        public TabData Data { get; }

        public ScrollViewer LogViewer => _follower.Viewer() ?? throw new InvalidOperationException("Lista logu nie ma ScrollViewer (zakładka niewidoczna?).");

        public void ShowTechnical() => _tabs.SelectedItem = _technical;

        /// <summary>To samo, co <c>StatusWindow.RefreshTechnical</c> przy tyknięciu zegara (albo pokazaniu Panelu).</summary>
        public void Refresh(bool resumeLog)
        {
            _follower.BeforeRefresh(resumeLog);
            Data.Tick(_tick++);
            _follower.AfterRefresh();
        }

        public void Settle()
        {
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            Window.UpdateLayout();
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        }

        public T Find<T>(string section) where T : DependencyObject =>
            Descendants<T>(Sections.Single(s => SectionName(s) == section)).First();

        private IEnumerable<FrameworkElement> Sections => ((StackPanel)Outer.Content).Children.OfType<FrameworkElement>();

        /// <summary>Wszystko, co przy odświeżeniu może przesunąć treść zakładki, w DIP z dokładnością 0,01.</summary>
        public Dictionary<string, string> Measure()
        {
            var m = new Dictionary<string, string>
            {
                ["zakładka: przewinięcie"] = F(Outer.VerticalOffset),
                ["zakładka: wysokość treści"] = F(Outer.ExtentHeight),
            };
            var stack = (StackPanel)Outer.Content;
            foreach (FrameworkElement section in Sections)
            {
                string name = SectionName(section);
                m[$"{name}: y"] = F(section.TransformToAncestor(stack).Transform(new System.Windows.Point(0, 0)).Y);
                m[$"{name}: wysokość"] = F(section.ActualHeight);
                foreach (DataGrid grid in Descendants<DataGrid>(section))
                    m[$"{name}: tabela"] = F(grid.ActualHeight);
                int i = 0;
                foreach (ScrollViewer inner in Descendants<ScrollViewer>(section))
                {
                    m[$"{name}: lista {i}: przewinięcie"] = F(inner.VerticalOffset);
                    m[$"{name}: lista {i}: paski"] = $"{inner.ComputedVerticalScrollBarVisibility}/{inner.ComputedHorizontalScrollBarVisibility}";
                    i++;
                }
            }

            if (_follower.Viewer() is { } logViewer && Data.LogLines.Count > 0)
            {
                int first = Math.Clamp((int)Math.Round(logViewer.VerticalOffset), 0, Data.LogLines.Count - 1);
                int last = Math.Clamp(first + (int)Math.Floor(logViewer.ViewportHeight) - 1, 0, Data.LogLines.Count - 1);
                m[LogFirstVisible] = Data.LogLines[first];
                m[LogLast] = Data.LogLines[last];
            }

            return m;
        }

        public void Dispose() => Window.Close();

        private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

        private static string SectionName(FrameworkElement section) =>
            section is HeaderedContentControl { Header: string header } ? header : "Nagłówek";

        public static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) yield return match;
                foreach (T nested in Descendants<T>(child)) yield return nested;
            }
        }

        private static ScrollViewer LoadTechnicalTab()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XDocument window = XDocument.Load(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "StatusWindow.xaml"));
            XElement tab = window.Descendants().Single(e => e.Name.LocalName == "TabItem" && (string?)e.Attribute(x + "Name") == "TechnicalTab");
            XElement content = new(tab.Elements().Single());
            content.Descendants().Attributes("Click").Remove();
            return (ScrollViewer)XamlReader.Parse(content.ToString());
        }
    }

    private sealed class TabData
    {
        private int _logCount;

        public TabData()
        {
            MachinePicker.Add(new StatusWindowViewModel.MachinePickerItem("m0", "Aidite 1"));
            MachinePicker.Add(new StatusWindowViewModel.MachinePickerItem("m1", "Aidite 2"));
            SelectedMachine = MachinePicker[1];
            foreach (AlertItemViewModel alert in Alerts(0)) ActiveAlerts.Add(alert);
            for (int i = 0; i < 50; i++) History.Add($"{Start.AddMinutes(-i - 1):HH:mm:ss}  Aidite {i % 7 + 1} — Brak łączności z maszyną");
            foreach (string line in History) HistoryLines.Add(line);
            for (int i = 0; i < 60; i++) AddLogLine();
        }

        public string RoleText => "Obserwacja lokalna, brak współpracy: sprawdź klucz i interfejs sieci.";
        public bool IsRoleWarning => true;
        public string VersionText => "Wersja 0.4.79+0123456789abcdef0123456789abcdef01234567 (commit 0123456789ab)";
        public string ModeText => "Tryb: zainstalowana";
        public string DataDirText => "Dane: C:\\ProgramData\\MaszynyAlert";
        public string OcrText => "Język OCR: en-US";
        public bool IsOcrWarning => false;
        public CapturesData Captures { get; } = new();
        public bool IsClusterEnabled => true;
        public ObservableCollection<MachineRowViewModel> Machines { get; } = new();
        public ObservableCollection<PeerRowViewModel> Peers { get; } = new();
        public ObservableCollection<AlertItemViewModel> ActiveAlerts { get; } = new();
        public ObservableCollection<IdleAlertItemViewModel> IdleAlerts { get; } = new();
        public ObservableCollection<ScreenAlertItemViewModel> WrongScreenAlerts { get; } = new();
        public string DismissScopeNote => "Bez grupy komputerów skasowanie dotyczy tylko tego komputera.";
        public bool IsDismissScopeWarning => false;
        public ObservableCollection<string> HistoryLines { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();
        public ObservableCollection<StatusWindowViewModel.MachinePickerItem> MachinePicker { get; } = new();
        public StatusWindowViewModel.MachinePickerItem? SelectedMachine { get; set; }

        private List<string> Log { get; } = new();
        private List<string> History { get; } = new();

        /// <summary>
        /// Sześć alertów (lista przewija się w swoim limicie wysokości): pierwszy na zmianę potwierdzony i dzwoniący,
        /// ostatni co trzecie tyknięcie zastąpiony alertem innej maszyny.
        /// </summary>
        private static IEnumerable<AlertItemViewModel> Alerts(int tick) => Enumerable.Range(0, 6).Select(i =>
        {
            string id = i == 5 && tick % 3 == 2 ? "m9" : "m" + i;
            bool acknowledged = i != 0 || tick % 2 == 0;
            var alert = new Alert
            {
                Key = new AlertKey(id, "connection"), MachineId = id, MachineName = $"Maszyna {id}",
                ShortText = "Brak łączności z maszyną", Category = "polaczenie", StartedAt = Start.AddMinutes(-7),
                Status = acknowledged ? AlertStatus.Acknowledged : AlertStatus.Active,
                AckedByHost = acknowledged ? "PC-1" : null, AckedByUser = acknowledged ? "operator" : null,
                AcknowledgedAt = acknowledged ? Start.AddMinutes(-3) : null,
            };
            return new AlertItemViewModel(alert, _ => Task.CompletedTask, _ => "Sprawdź zasilanie i kabel sieciowy maszyny.");
        });

        public void Tick(int tick)
        {
            DateTimeOffset now = Start.AddSeconds(tick);
            AddLogLine();
            StatusWindowViewModel.SyncMachines(Machines, Enumerable.Range(0, 12).Select(i => new MachineRowViewModel(Snapshot(i, now))));
            StatusWindowViewModel.SyncPeers(Peers, [new PeerRowViewModel(
                new PeerInfo("PC-1", 100, PeerRole.Watcher, "0.4.79+01234567", now, tick, "PC-1", 1, 12), now, TimeSpan.FromSeconds(15), isThisComputer: true)]);
            StatusWindowViewModel.SyncIdleAlerts(IdleAlerts, []);
            StatusWindowViewModel.SyncScreenAlerts(WrongScreenAlerts, []);
            History.Insert(0, $"{now:HH:mm:ss}  Maszyna m{tick % 12} — Brak łączności z maszyną");
            StatusWindowViewModel.SyncActiveAlerts(ActiveAlerts, Alerts(tick));
            StatusWindowViewModel.SyncLines(HistoryLines, History.Take(50));
            StatusWindowViewModel.SyncLines(LogLines, Log.TakeLast(50));
        }

        private void AddLogLine()
        {
            DateTimeOffset at = Start.AddSeconds(_logCount);
            Log.Add($"{at:HH:mm:ss} [WRN] Nie udało się połączyć z 192.168.1.{100 + _logCount % 9}:9100 w ciągu 10 s — wpis {_logCount}");
            _logCount++;
        }

        private static MachineStatusSnapshot Snapshot(int i, DateTimeOffset now) => new()
        {
            MachineId = "m" + i,
            Name = i < 7 ? $"Aidite {i + 1}" : i < 9 ? $"520 ({i - 6})" : i == 9 ? "580" : i == 10 ? "Premile" : "Cameo 1",
            Host = $"192.168.1.{100 + i}", Port = i < 7 ? 9100 : 5900, Profile = i < 7 ? "opencnc" : i < 9 ? "aidite5x" : "amm580",
            State = i == 0 ? MachineConnectionState.Connected : i < 9 ? MachineConnectionState.Error : MachineConnectionState.Disabled,
            Source = i < 9 ? "vnc" : "-",
            LastFrameAt = i == 0 ? now : null,
            LastRedFraction = i == 0 ? (now.Second % 7) / 100.0 : 0,
            LastError = i is > 0 and < 9 ? $"Nie udało się połączyć z 192.168.1.{100 + i}:9100 w ciągu 10 s" : null,
        };
    }

    private sealed class CapturesData
    {
        public string SummaryText => "Zrzuty: brak plików (limit 200 MB).";
        public bool IsAtLimit => false;
        public string ActionText => "";
        public object? DeleteOlderThanDayCommand => null;
    }
}
