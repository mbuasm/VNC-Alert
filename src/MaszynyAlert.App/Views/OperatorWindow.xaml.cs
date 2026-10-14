using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.Views;

public partial class OperatorWindow : Window
{
    private readonly AppOrchestrator _orchestrator;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _machineSignature = "";
    private string _alertSignature = "";

    /// <summary>
    /// Kolory zdania o zasięgu „✕ Skasuj” — szary jak inne objaśnienia, bursztynowy dla ostrzeżenia. To klucze
    /// pędzli motywu (App.xaml), nie stałe kolory, żeby zdanie przełączało się razem z trybem ciemnym.
    /// </summary>
    private const string ScopeBrushKey = "TextSecondaryBrush";
    private const string ScopeWarningBrushKey = "WarningTextBrush";

    private readonly ThemeService _theme;

    private string? _highlightedMachineId;
    public event Action? OpenAdmin;
    public OperatorWindow(AppOrchestrator orchestrator, BuildIdentity build, ThemeService theme)
    {
        InitializeComponent();
        _orchestrator = orchestrator;
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        // Przełączenie zawsze idzie z wątku UI (przycisk, menu zasobnika), więc napis odświeża się od razu.
        _theme.ThemeChanged += _ => RefreshThemeButton();
        RefreshThemeButton();
        _orchestrator.AlertsChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Refresh(); }));
        VersionLabel.Text = "VNC Alert • wersja " + build.Version.Split('+')[0];
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _timer.Start(); } else { _timer.Stop(); _highlightedMachineId = null; } };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }
    /// <summary>
    /// Pokazuje Podgląd z wyróżnioną kartą maszyny <paramref name="machineId"/> — kliknięcie kafelka nakładki
    /// statusu (<see cref="OverlayWindow.MachineChosen"/>). Karta jest przewijana do widoku, bo przy siedmiu
    /// maszynach żądana potrafi być poza pierwszym ekranem.
    /// </summary>
    public void ShowForMachine(string machineId)
    {
        ArgumentException.ThrowIfNullOrEmpty(machineId);

        _highlightedMachineId = machineId;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Refresh();
        BringHighlightedIntoView();
    }

    /// <summary>Przewija listę kart do maszyny wskazanej z nakładki (po odświeżeniu, gdy kontenery już są).</summary>
    private void BringHighlightedIntoView()
    {
        if (_highlightedMachineId is null || MachineCards.ItemsSource is not MachineCardViewModel[] cards) return;
        int index = Array.FindIndex(cards, c => c.Id == _highlightedMachineId);
        if (index < 0) return;
        MachineCards.UpdateLayout();
        (MachineCards.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement)?.BringIntoView();
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var alerts = _orchestrator.VisibleAlerts.Where(a => a.Status != AlertStatus.Closed).ToArray();
        var rows = MachineCardViewModel.ForEnabledMachines(_orchestrator, now)
            .Select(c => c.Id == _highlightedMachineId ? c with { Highlighted = true } : c).ToArray();
        string signature = string.Join("|", rows.Select(r => r.ToString()));
        if (signature != _machineSignature) { MachineCards.ItemsSource = rows; _machineSignature = signature; }
        int problems = rows.Count(r => r.Tone != MachineCardTone.Ok);
        bool observing = _orchestrator.Supervisor.MonitoringEnabled && !_orchestrator.Cluster.NoWatcher
            && (!_orchestrator.Cluster.IsWatcher || _orchestrator.Supervisor.IsRunning);
        Summary.Text = !observing ? "Monitorowanie nie działa" : problems == 0 ? "Maszyny pod obserwacją" : $"{problems} z {rows.Length} maszyn wymaga uwagi";
        SummaryDetail.Text = !observing ? "Sprawdź ustawienia oraz diagnostykę w Panelu administracyjnym."
            : "Czerwony komunikat oznacza błąd. Bursztynowy przypomina o czynności przy maszynie.";
        // Potwierdzenie usuwa zadanie operatora, ale stan maszyny nadal pokazuje trwający błąd.
        var errors = alerts.Where(AlertVisibility.IsVisibleInAlertWindow).ToArray();
        string alertSignature = string.Join("|", errors.Select(a => $"{a.Key}:{a.StartedAt}:{a.ShortText}:{a.Status}:{a.AcknowledgedAt}:{_orchestrator.Help.For(a)}"));
        if (_alertSignature != alertSignature)
        {
            AlertCards.ItemsSource = errors.Select(a => new AlertItemViewModel(a, key => _orchestrator.AcknowledgeAsync(key), _orchestrator.Help.For)).ToArray();
            _alertSignature = alertSignature;
        }
        NoAlerts.Visibility = errors.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshIdleSection();
    }

    private void RefreshIdleSection()
    {
        var idle = _orchestrator.IdleAlerts;
        IdleCards.ItemsSource = idle
            .Select(a => new IdleAlertItemViewModel(a, key => _orchestrator.DismissIdleAlertAsync(key),
                _orchestrator.DismissButtonHint))
            .ToArray();
        NoIdle.Visibility = idle.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Zasięg skasowania pokazujemy tylko wtedy, gdy jest co kasować — inaczej byłoby to zdanie o niczym.
        IdleScope.Text = _orchestrator.DismissScopeNote;
        IdleScope.SetResourceReference(ForegroundProperty, _orchestrator.IsDismissScopeWarning ? ScopeWarningBrushKey : ScopeBrushKey);
        IdleScope.Visibility = idle.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Admin_Click(object sender, RoutedEventArgs e) => OpenAdmin?.Invoke();

    /// <summary>Napis przycisku mówi, DO CZEGO przełącza: w jasnym „Tryb ciemny”, w ciemnym „Tryb jasny”.</summary>
    internal string ThemeButtonText => _theme.IsDark ? "Tryb jasny" : "Tryb ciemny";

    private void RefreshThemeButton() => ThemeButton.Content = ThemeButtonText;

    private void Theme_Click(object sender, RoutedEventArgs e) => _theme.Toggle();
    private void Screen_Click(object sender, RoutedEventArgs e)
    {
        string id = (string)((System.Windows.Controls.Button)sender).Tag;
        var machine = _orchestrator.Config.Machines.First(m => m.Id == id);
        new ScreenPreviewWindow(_orchestrator.Preview, id, machine.Name) { Owner = this }.Show();
    }
}
