using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Views;

public partial class AlertWindow : Window
{
    /// <summary>
    /// Wysokość okna z projektu (<c>MaxHeight</c> w AlertWindow.xaml). Na niskim ekranie jest obniżana do
    /// obszaru roboczego w <see cref="OnLoaded"/> — patrz <see cref="WindowPlacement.MaxHeightFor"/>.
    /// </summary>
    private const double DesignMaxHeight = 620;

    private readonly AppOrchestrator _orchestrator;
    private readonly WavSoundPlayer _sound;
    /// <summary>Skonfigurowana przezroczystość czytana z bieżącej konfiguracji orkiestratora (zmienia się po zastosowaniu ustawień grupy).</summary>
    private double ConfiguredOpacity => _orchestrator.Config.Ui.AlertOpacity;
    private readonly AlertWindowViewModel _viewModel;
    private readonly DispatcherTimer _soundTimer;

    /// <summary>Terminy dźwięku i tożsamość epizodów, niezależne od zmian kodu OCR.</summary>
    private readonly AlertSoundSchedule _soundSchedule;
    private readonly IClock _clock;

    private bool _userMoved;

    /// <summary>
    /// Wywoływane po każdej zmianie położenia/widoczności tego okna (Show/Hide/zmiana rozmiaru/przeciągnięcie) —
    /// pozwala <see cref="IdlePopupWindow"/> ustawić się NAD tym oknem, gdy jest ono widoczne, albo wrócić do
    /// własnego rogu, gdy nie jest.
    /// </summary>
    public event Action? PlacementChanged;

    public AlertWindow(AppOrchestrator orchestrator, WavSoundPlayer sound, IClock clock)
    {
        InitializeComponent();

        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));
        _clock = clock;
        _soundSchedule = new AlertSoundSchedule(() => _orchestrator.Config.Timing.AlertSoundRepeatMs);
        Opacity = ConfiguredOpacity;

        _viewModel = new AlertWindowViewModel(key => _orchestrator.AcknowledgeAsync(key), _orchestrator.Help.For);
        DataContext = _viewModel;

        _soundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _soundTimer.Tick += OnSoundTimerTick;

        Closing += OnClosing;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _orchestrator.AlertsChanged += OnOrchestratorAlertsChanged;

        RefreshFromOrchestrator();
    }

    private void OnOrchestratorAlertsChanged() => Dispatcher.BeginInvoke(RefreshFromOrchestrator);

    private void RefreshFromOrchestrator()
    {
        if (_viewModel.Refresh(_orchestrator.VisibleAlerts))
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(AlertsScroll.ScrollToTop));

        UpdateSound();

        if (_viewModel.HasVisibleAlerts)
        {
            if (!IsVisible)
            {
                if (!_userMoved)
                {
                    WindowPlacement.PositionBottomRight(this);
                }

                // Show() bez Activate() — ShowActivated="False" w XAML gwarantuje, że okno nie kradnie
                // fokusu operatorowi. Topmost zostaje.
                Show();
            }
        }
        else if (IsVisible)
        {
            Hide();
            _userMoved = false;
        }

        PlacementChanged?.Invoke();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MaxHeight = WindowPlacement.MaxHeightFor(WindowPlacement.WorkAreaFor(this), DesignMaxHeight);

        if (!_userMoved)
        {
            WindowPlacement.PositionBottomRight(this);
        }

        PlacementChanged?.Invoke();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_userMoved)
        {
            WindowPlacement.ClampVertically(this);
        }
        else
        {
            WindowPlacement.PositionBottomRight(this);
        }

        PlacementChanged?.Invoke();
    }

    /// <summary>
    /// Nagłówek (czerwony pasek) jako uchwyt przeciągania. Po zakończeniu przesunięcia blokuje ponowne
    /// przyciąganie okna do rogu (<see cref="_userMoved"/>) i powiadamia <see cref="PlacementChanged"/>,
    /// żeby pop-up postoju od razu przeliczył swoją pozycję.
    /// </summary>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
            _userMoved = true;
            PlacementChanged?.Invoke();
        }
        catch (InvalidOperationException)
        {
            // DragMove wywołane poza gestem lewego przycisku myszy (np. programowo) — nic nie robimy.
        }
    }

    internal void TestUserMove(double left, double top)
    {
        Left = left;
        Top = top;
        _userMoved = true;
        PlacementChanged?.Invoke();
    }

    /// <summary>
    /// Po rozwinięciu „Jak rozwiązać ten problem?” okno najpierw rośnie w dół i podnosi się nad dolną
    /// krawędź ekranu (<see cref="OnSizeChanged"/>), ale gdy instrukcja jest długa albo alertów jest kilka,
    /// treść i tak nie mieści się w <c>ScrollViewer</c>. Wtedy przewijamy do rozwiniętej instrukcji, żeby
    /// operator nie musiał jej szukać ręcznie. Wołane z niskim priorytetem, bo
    /// w chwili zdarzenia <c>Expanded</c> nowy układ nie jest jeszcze policzony.
    /// </summary>
    private void Solution_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement expander)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (expander.IsLoaded)
            {
                // Rozwinięty Expander wyższy niż widok przewijania zostanie wyrównany GÓRĄ (reguła
                // "minimal scroll" WPF), czyli od początku instrukcji — a nie od jej końca.
                expander.BringIntoView();
            }
        }));
    }

    /// <summary>Pełna nieprzezroczystość po najechaniu myszą — łatwiej przeczytać treść alertu.</summary>
    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => WindowEffects.Fade(this, 1.0);

    /// <summary>Powrót do skonfigurowanej przezroczystości (<see cref="UiConfig.AlertOpacity"/>) po zjechaniu myszą.</summary>
    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => WindowEffects.Fade(this, ConfiguredOpacity);

    /// <summary>
    /// Steruje powtarzanym dźwiękiem (patrz opis klasy): brak dzwoniących alertów → stop i zatrzymany zegar;
    /// nowy dzwoniący epizod (albo pierwszy po ciszy) → dźwięk od razu i zegar powtarzania od nowa; ten sam
    /// zestaw dzwoniących co poprzednio → nic (zegar sam odtworzy dźwięk po upływie odstępu).
    /// </summary>
    private void UpdateSound()
    {
        bool wasRinging = _soundSchedule.HasRingingAlerts;
        bool play = _soundSchedule.Update(_orchestrator.VisibleAlerts, _clock.MonotonicMs);
        if (!_soundSchedule.HasRingingAlerts)
        {
            _soundTimer.Stop();
            if (wasRinging)
            {
                _sound.Stop();
            }
            return;
        }

        if (play)
        {
            _sound.PlayOnce();
        }
        _soundTimer.Start();
    }

    private void OnSoundTimerTick(object? sender, EventArgs e) => UpdateSound();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
