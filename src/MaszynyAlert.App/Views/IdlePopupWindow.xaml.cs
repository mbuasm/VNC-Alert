using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.Views;

public partial class IdlePopupWindow : Window
{
    private readonly AppOrchestrator _orchestrator;
    private readonly AlertWindow _alertWindow;
    private readonly ScreenPopupWindow _screenPopup;
    private readonly IdlePopupViewModel _viewModel;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly MaszynyAlert.Windows.WavSoundPlayer _ping = new(
        System.IO.Path.Combine(MaszynyAlert.Windows.AppPaths.ExeDir, "sounds", "idle-ping.wav"));

    /// <summary>Klucze alertów postoju widoczne przy OSTATNIM odświeżeniu — do wykrycia "nowego" klucza (patrz <see cref="RefreshFromOrchestrator"/>).</summary>
    private HashSet<AlertKey> _knownKeys = new();

    /// <summary>Tryb przypięty (<see cref="ShowPinned"/>) — pop-up widoczny bez auto-ukrywania, dopóki operator sam go nie zamknie.</summary>
    private bool _pinned;

    public IdlePopupWindow(AppOrchestrator orchestrator, AlertWindow alertWindow, ScreenPopupWindow screenPopup)
    {
        InitializeComponent();

        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        WindowEffects.Attach(this, orchestrator.Config.Ui.AlertOpacity);
        System.Windows.Application.Current.Exit += (_, _) => _ping.Dispose();
        _alertWindow = alertWindow ?? throw new ArgumentNullException(nameof(alertWindow));

        _viewModel = new IdlePopupViewModel(key => _orchestrator.DismissIdleAlertAsync(key));
        DataContext = _viewModel;

        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += OnAutoHideTimerTick;

        Closing += OnClosing;
        Loaded += (_, _) => RepositionSelf();
        SizeChanged += (_, _) => RepositionSelf();
        _alertWindow.PlacementChanged += RepositionSelf;
        _screenPopup = screenPopup ?? throw new ArgumentNullException(nameof(screenPopup));
        _screenPopup.IsVisibleChanged += (_, _) => RepositionSelf();
        _screenPopup.SizeChanged += (_, _) => RepositionSelf();
        _screenPopup.LocationChanged += (_, _) => RepositionSelf();

        _orchestrator.AlertsChanged += OnOrchestratorAlertsChanged;

        RefreshFromOrchestrator();
    }

    /// <summary>
    /// Pokazuje pop-up "przypięty" (menu zasobnika "Maszyny bez pracy – dołóż prace"): bez limitu czasu, z bieżącą
    /// listą alertów postoju (a gdy pusta — tekst "Brak maszyn stojących bez pracy"). Kolejny "✕" w nagłówku
    /// chowa okno i kończy tryb przypięty (patrz <see cref="CloseButton_Click"/>).
    /// </summary>
    public void ShowPinned()
    {
        _pinned = true;
        _autoHideTimer.Stop();
        RefreshFromOrchestrator();
    }

    private void OnOrchestratorAlertsChanged() => Dispatcher.BeginInvoke(RefreshFromOrchestrator);

    /// <summary>
    /// Przelicza treść z <see cref="AppOrchestrator.IdleAlerts"/> i decyduje o pokazaniu/schowaniu okna:
    /// w trybie przypiętym okno jest zawsze widoczne (nawet przy pustej liście); w trybie zwykłym pojawienie
    /// się NOWEGO klucza (nieznanego przy poprzednim odświeżeniu) pokazuje okno i (re)startuje zegar
    /// auto-ukrycia (<see cref="Core.Config.TimingConfig.IdlePopupSeconds"/>; 0 = bez zegara), a pusta lista chowa
    /// okno od razu. Klucz, który zniknął z listy (skasowany/zamknięty), jest automatycznie "zapominany" —
    /// nadpisujemy <see cref="_knownKeys"/> pełnym bieżącym zestawem przy każdym wywołaniu.
    /// Przejście z niepustej listy do pustej kończy przypięcie i chowa okno.
    /// </summary>
    private void RefreshFromOrchestrator()
    {
        IReadOnlyList<Alert> current = _orchestrator.IdleAlerts;

        var currentKeys = new HashSet<AlertKey>(current.Select(a => a.Key));
        bool hasNewKey = currentKeys.Any(k => !_knownKeys.Contains(k));
        bool listBecameEmpty = _knownKeys.Count > 0 && current.Count == 0;
        _knownKeys = currentKeys;

        _viewModel.Refresh(current, _orchestrator.DismissScopeNote, _orchestrator.IsDismissScopeWarning,
            _orchestrator.DismissButtonHint);
        if (hasNewKey && !_orchestrator.VisibleAlerts.Any(a => a.Sound && a.Status is AlertStatus.Active or AlertStatus.Escalated))
            _ping.PlayOnce();

        // Po skasowaniu/ustąpieniu ostatniej pozycji kończymy także ręczne przypięcie.
        // Pusty widok otwarty świadomie z menu nadal można obejrzeć i zamknąć krzyżykiem.
        if (listBecameEmpty) _pinned = false;

        if (_pinned)
        {
            // Tryb przypięty: zawsze widoczny, nigdy nie chowamy automatycznie (nawet przy pustej liście —
            // wtedy szablon w XAML pokazuje tekst zastępczy zamiast listy).
            if (!IsVisible)
            {
                Show();
            }

            return;
        }

        if (current.Count == 0)
        {
            _autoHideTimer.Stop();
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (hasNewKey)
        {
            if (!IsVisible)
            {
                Show();
            }

            RestartAutoHideTimer();
        }
        // W przeciwnym razie: lista niepusta, ale bez nowego klucza — treść jest już odświeżona wyżej, licznik
        // czasu NIE jest restartowany (pop-up sam zniknie, jeśli już wcześniej się pokazał; operator wciąż
        // widzi problem w oknie statusu / menu zasobnika "Maszyny bez pracy – dołóż prace").
    }

    private void RestartAutoHideTimer()
    {
        _autoHideTimer.Stop();
        int seconds = _orchestrator.Config.Timing.IdlePopupSeconds;
        if (seconds <= 0)
        {
            return;
        }

        _autoHideTimer.Interval = TimeSpan.FromSeconds(seconds);
        _autoHideTimer.Start();
    }

    private void OnAutoHideTimerTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        if (!_pinned)
        {
            Hide();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _autoHideTimer.Stop();
        _pinned = false;
        Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Okno jest tworzone raz i ponownie używane (jak AlertWindow/StatusWindow) — zamknięcie tylko je chowa.
        e.Cancel = true;
        _autoHideTimer.Stop();
        _pinned = false;
        Hide();
    }

    private void RepositionSelf()
    {
        // Obszar roboczy z monitora okna alertu, gdy jest widoczne — oba okna zostają razem przy dwóch monitorach.
        Rect workArea = WindowPlacement.WorkAreaFor(_alertWindow.IsVisible ? _alertWindow : this);
        Point position = WindowPlacement.IdlePopupPosition(workArea, new Size(ActualWidth, ActualHeight),
            BoundsIfVisible(_screenPopup), BoundsIfVisible(_alertWindow));
        Left = position.X;
        Top = position.Y;
    }

    private static Rect? BoundsIfVisible(Window window) =>
        window.IsVisible ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : null;
}
