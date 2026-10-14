using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Views;

public partial class StatusWindow : Window
{
    private readonly StatusWindowViewModel _viewModel;
    private readonly AppOrchestrator _orchestrator;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;
    private readonly DowntimeView _report;
    private readonly MultiComputerViewModel _multiComputer;
    private readonly SharedConfigView _sharedConfig;
    private readonly LogFollower _log;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    public StatusWindow(StatusWindowViewModel viewModel, MultiComputerView multiComputerView,
        SharedConfigView sharedConfigView, SetupView setupView, AppOrchestrator orchestrator, IClock clock)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        DataContext = _viewModel;
        _report = new DowntimeView(orchestrator);
        ReportHost.Content = _report;
        InstructionsHost.Content = new AlarmInstructionsView(orchestrator.Help, () => orchestrator.InstructionsSyncStatus);
        MultiComputerHost.Content = multiComputerView ?? throw new ArgumentNullException(nameof(multiComputerView));
        _multiComputer = (MultiComputerViewModel)multiComputerView.DataContext;
        _sharedConfig = sharedConfigView ?? throw new ArgumentNullException(nameof(sharedConfigView));
        SharedConfigHost.Content = _sharedConfig;
        LocalSetupHost.Content = setupView ?? throw new ArgumentNullException(nameof(setupView));
        PreviewMouseDown += (_, _) => _lastActivity = DateTimeOffset.UtcNow;
        PreviewKeyDown += (_, _) => _lastActivity = DateTimeOffset.UtcNow;
        PreviewMouseWheel += (_, _) => _lastActivity = DateTimeOffset.UtcNow;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (DateTimeOffset.UtcNow - _lastActivity > TimeSpan.FromMinutes(5)) { Hide(); return; }
            // Po zakładkach chodzimy PO REFERENCJI, nie po numerze: dołożenie kolejnej zakładki nie może
            // przestawić odświeżania raportu, kreatora grupy ani podglądu technicznego.
            if (AdminTabs.SelectedItem == ReportTab) _report.Refresh();
            else if (AdminTabs.SelectedItem == MultiComputerTab) _multiComputer.Refresh();
            else if (AdminTabs.SelectedItem == TechnicalTab) RefreshTechnical();
        };
        TechnicalPage.Attach(TechnicalScroll);
        _log = new LogFollower(LogList);

        // Zdarzenie wybrania zakładki przychodzi też z wewnętrznego TabControl Ustawień (bąbelkowanie),
        // więc jeden odbiornik wystarczy na obie listy zakładek. Bąbelkują tak samo listy rozwijane i
        // listy w widokach (Selector.SelectionChanged), stąd sprawdzenie źródła — inaczej wybór rogu
        // nakładki w zakładce Konfiguracja liczyłby się jako jej ponowne pokazanie.
        AdminTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, AdminTabs) || ReferenceEquals(e.OriginalSource, SettingsTabs))
                RefreshSharedConfigIfShown();
        };
        IsVisibleChanged += OnIsVisibleChanged;
        Closing += OnClosing;
    }

    /// <summary>Zakładka „Konfiguracja” odświeża wartości przy pokazaniu (niezapisanych wpisów nie nadpisuje).</summary>
    private void RefreshSharedConfigIfShown()
    {
        if (AdminTabs.SelectedItem == SettingsTab && SettingsTabs.SelectedItem == SharedConfigTab)
            _sharedConfig.RefreshOnShow();
    }

    /// <summary>Wybiera zakładkę Ustawienia → Konfiguracja (zrzut ekranu w teście startu).</summary>
    internal void ShowSharedConfigTab()
    {
        AdminTabs.SelectedItem = SettingsTab;
        SettingsTabs.SelectedItem = SharedConfigTab;
    }

    /// <summary>Wybiera zakładkę „Podgląd techniczny” (zrzut i sprawdzenie układu sekcji w teście startu).</summary>
    internal void ShowTechnicalTab() => AdminTabs.SelectedItem = TechnicalTab;

    /// <summary>Wraca na pierwszą zakładkę Panelu (po zrzucie ekranu w teście startu).</summary>
    internal void ShowReportTab() => AdminTabs.SelectedItem = ReportTab;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _lastActivity = DateTimeOffset.UtcNow;
            _report.Refresh();
            _multiComputer.Refresh();
            RefreshTechnical(followLog: true);
            RefreshSharedConfigIfShown();
            _timer.Start();
        }
        else
        {
            foreach (Window owned in OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            _timer.Stop();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Odświeża Podgląd techniczny; log trzyma się najnowszego wpisu (<see cref="LogFollower"/>).
    /// <paramref name="followLog"/> = pokazanie Panelu: śledzenie logu od nowa.
    /// </summary>
    internal void RefreshTechnical(bool followLog = false)
    {
        _log.BeforeRefresh(followLog);
        _viewModel.Refresh(_clock.UtcNow);
        _log.AfterRefresh();
    }

    /// <summary>Przewija log do ostatniego wpisu (także dla sprawdzenia w teście startu).</summary>
    internal void ScrollLogToEnd() => _log.ScrollToEnd();

    /// <summary>Wewnętrzny ScrollViewer listy logu (z szablonu ListBoxa).</summary>
    internal ScrollViewer? FindLogScroll() => _log.Viewer();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshTechnical();
    private void Lock_Click(object sender, RoutedEventArgs e) => Hide();

    private void ShowScreenButton_Click(object sender, RoutedEventArgs e)
    {
        StatusWindowViewModel.MachinePickerItem? selected = _viewModel.SelectedMachine;
        if (selected is null)
        {
            System.Windows.MessageBox.Show(
                "Wybierz maszynę z listy.", "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = new ScreenPreviewWindow(_orchestrator.Preview, selected.Id, selected.Name) { Owner = this };
        preview.Show();
    }

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string logsDir = AppPaths.Resolve(_orchestrator.Config.Paths.LogsDir);
        try
        {
            Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logsDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            System.Windows.MessageBox.Show(
                $"Nie udało się otworzyć folderu logów:\n{logsDir}\n\n{ex.Message}",
                "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
