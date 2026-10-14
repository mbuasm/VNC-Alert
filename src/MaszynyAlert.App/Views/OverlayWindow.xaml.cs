using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.App.Views;

public partial class OverlayWindow : Window
{
    private readonly AppOrchestrator _orchestrator;
    private readonly OverlaySettingsStore _store;
    private readonly ILogger<OverlayWindow>? _logger;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private OverlaySettings _settings;
    private string _signature = "";
    private bool _clickThrough;
    private OverlayLayout? _appliedLayout;
    private (int Rows, int Columns) _appliedGrid;
    private readonly List<(OverlayLayout Layout, System.Windows.Controls.MenuItem Item)> _layoutItems = [];

    /// <summary>Operator kliknął kafelek maszyny o tym identyfikatorze — Podgląd ma się otworzyć na niej.</summary>
    public event Action<string>? MachineChosen;

    public OverlayWindow(AppOrchestrator orchestrator, OverlaySettingsStore store, ILogger<OverlayWindow>? logger = null)
    {
        InitializeComponent();

        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        _settings = _store.Load();

        OverlayConfig overlay = orchestrator.Config.Ui.Overlay;
        Zoom.ScaleX = overlay.Scale;
        Zoom.ScaleY = overlay.Scale;
        WindowEffects.Attach(this, overlay.Opacity);
        ApplyLayout();
        BuildLayoutMenu();
        ApplyLayoutPickerVisibility();

        Closing += OnClosing;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _timer.Tick += (_, _) => Refresh();
        _orchestrator.AlertsChanged += OnStateChanged;
        _orchestrator.MachinesChanged += OnStateChanged;
        _orchestrator.ConfigChanged += OnConfigChanged;

        Refresh();
    }

    private void OnConfigChanged() => Dispatcher.BeginInvoke(new Action(ApplyConfiguration));

    /// <summary>
    /// Bierze ustawienia nakładki z bieżącej konfiguracji (powiększenie, przezroczystość, widoczność i
    /// przepuszczanie kliknięć) i stosuje je do już otwartego okna. Wołane na wątku UI:
    /// warstwa z Panelu może je zmienić w trakcie pracy programu.
    /// Świadomy wybór operatora nadal wygrywa z <c>config.json</c> — patrz <see cref="OverlayEnabled"/>
    /// i <see cref="OverlaySettings.ResolveEnabled"/>.
    /// </summary>
    public void ApplyConfiguration()
    {
        OverlayConfig overlay = Configured;
        OverlaySettings normalized = _settings.Normalize(overlay);
        if (!ReferenceEquals(normalized, _settings)) SaveSettings(normalized);
        Zoom.ScaleX = overlay.Scale;
        Zoom.ScaleY = overlay.Scale;
        WindowEffects.SetIdleOpacity(this, overlay.Opacity);
        ApplyLayout();
        ApplyVisibility();
        ApplyClickThrough();
    }

    /// <summary>
    /// Układ kafelków obowiązujący w tej chwili: wybór operatora z menu zasobnika wygrywa z
    /// <c>ui.overlay.layout</c>, dopóki administrator tego ustawienia nie zmieni — te same zasady co
    /// <see cref="OverlayEnabled"/> (<see cref="OverlaySettings.ResolveLayout"/>).
    /// </summary>
    public OverlayLayout Layout => _settings.ResolveLayout(Configured);

    /// <summary>Zmienia układ kafelków i zapamiętuje ten wybór operatora w profilu użytkownika.</summary>
    public void SetLayout(OverlayLayout layout)
    {
        SaveSettings(_settings.WithLayout(layout, Configured));
        ApplyLayout();
    }

    private void BuildLayoutMenu()
    {
        foreach (OverlayLayout layout in OverlayLayouts.All)
        {
            var item = new System.Windows.Controls.MenuItem { Header = OverlayLayouts.Label(layout), Tag = OverlayLayouts.Name(layout) };
            item.Click += (_, _) => SetLayout(layout);
            LayoutMenu.Items.Add(item);
            _layoutItems.Add((layout, item));
        }

        LayoutMenu.Opened += (_, _) => RefreshLayoutMenuChecks();
    }

    /// <summary>Ptaszek przy bieżącym układzie — odświeżany przy każdym otwarciu menu (układ może przyjść z Panelu).</summary>
    internal void RefreshLayoutMenuChecks()
    {
        OverlayLayout current = Layout;
        foreach ((OverlayLayout layout, System.Windows.Controls.MenuItem item) in _layoutItems)
        {
            item.IsChecked = layout == current;
        }
    }

    /// <summary>
    /// Przycisk układu jest tylko po to, żeby go kliknąć — przy przenikaniu kliknięć nakładka nie łapie myszy, więc
    /// przycisk znika (i wraca po wyłączeniu przenikania w menu zasobnika albo w Panelu).
    /// </summary>
    private void ApplyLayoutPickerVisibility()
    {
        LayoutButton.Visibility = ClickThrough ? Visibility.Collapsed : Visibility.Visible;
        DragHandle.Visibility = ClickThrough ? Visibility.Hidden : Visibility.Visible;
        HideButton.Visibility = ClickThrough ? Visibility.Hidden : Visibility.Visible;
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLayoutMenuChecks();
        LayoutMenu.PlacementTarget = LayoutButton;
        LayoutMenu.IsOpen = true;
    }

    /// <summary>
    /// Buduje panel kafelków dla bieżącego <see cref="Layout"/>: <c>UniformGrid</c> ze stałą liczbą wierszy
    /// (układy poziome) albo kolumn (pionowe), drugi wymiar liczy sam z liczby kafelków — dokładnie jak
    /// <see cref="OverlayLayouts.Shape"/>. Podmiana szablonu panelu generuje kontenery od nowa, więc robimy ją
    /// tylko wtedy, gdy układ naprawdę się zmienił (inaczej miganie czerwonego kafelka startowałoby od zera).
    /// </summary>
    internal void ApplyLayout()
    {
        OverlayLayout layout = Layout;
        int tiles = Tiles.Items.Count;
        int maxColumns = OverlayLayouts.MaxColumns(WindowPlacement.WorkAreaFor(this).Width, Zoom.ScaleX);
        (int rows, int columns) = OverlayLayouts.FitGrid(layout, tiles, maxColumns);
        if (_appliedLayout == layout && _appliedGrid == (rows, columns))
        {
            return;
        }

        _appliedGrid = (rows, columns);
        var grid = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid));
        grid.SetValue(System.Windows.Controls.Primitives.UniformGrid.RowsProperty, rows);
        grid.SetValue(System.Windows.Controls.Primitives.UniformGrid.ColumnsProperty, columns);
        Tiles.ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(grid);
        _appliedLayout = layout;
        _logger?.LogInformation(
            "Nakładka statusu: układ kafelków {Uklad} (ui.overlay.layout={Configured}, wybór operatora={Chosen} dla ustawienia {ChosenFor}).",
            OverlayLayouts.Name(layout),
            OverlayLayouts.Name(Configured.Layout),
            _settings.Layout is { } chosen ? OverlayLayouts.Name(chosen) : "brak",
            _settings.LayoutDefault is { } chosenFor ? OverlayLayouts.Name(chosenFor) : "brak");
    }

    /// <summary>
    /// Czy nakładka ma być widoczna. Wybór operatora (menu zasobnika, krzyżyk) wygrywa z <c>config.json</c>,
    /// ale traci ważność, gdy administrator zmieni <c>ui.overlay.enabled</c> — patrz
    /// <see cref="OverlaySettings.ResolveEnabled"/>.
    /// </summary>
    public bool OverlayEnabled => _settings.ResolveEnabled(Configured);

    public bool ClickThrough => _settings.ResolveClickThrough(Configured);

    /// <summary>Ustawienia nakładki ze stanowiska (<c>ui.overlay</c> w <c>config.json</c>).</summary>
    private OverlayConfig Configured => _orchestrator.Config.Ui.Overlay;

    /// <summary>
    /// Pokazuje albo chowa nakładkę i zapamiętuje ten <b>świadomy</b> wybór operatora w profilu użytkownika
    /// (menu zasobnika „Nakładka statusu”, krzyżyk na samej nakładce). Start programu i zatrzymanie programu
    /// wyboru nie zapisują — patrz <see cref="ApplyVisibility"/> i <see cref="OnClosing"/>.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        SaveSettings(_settings.WithEnabled(enabled, Configured));
        ApplyVisibility();
    }

    /// <summary>Włącza albo wyłącza przepuszczanie kliknięć i zapamiętuje ten wybór w profilu użytkownika.</summary>
    public void SetClickThrough(bool clickThrough)
    {
        SaveSettings(_settings.WithClickThrough(clickThrough, Configured));
        ApplyClickThrough();
    }

    /// <summary>Pokazuje nakładkę, jeśli operator jej nie wyłączył (wołane przy starcie programu).</summary>
    public void ApplyVisibility()
    {
        if (OverlayEnabled)
        {
            Refresh();
            if (!IsVisible)
            {
                Show();
            }

            ApplyClickThrough();
            _timer.Start();
            LogVisibility(shown: true);
            return;
        }

        HideOverlay();
        LogVisibility(shown: false);
    }

    /// <summary>Chowa nakładkę i zatrzymuje jej zegar, NIE zapisując niczego w profilu użytkownika.</summary>
    private void HideOverlay()
    {
        _timer.Stop();
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// Wpis do logu, dlaczego nakładka jest (albo nie jest) na ekranie.
    /// </summary>
    private void LogVisibility(bool shown) => _logger?.LogInformation(
        "Nakładka statusu {Stan}: ui.overlay.enabled={Configured}, wybór operatora={Chosen} (dla ustawienia {ChosenFor}) z pliku {Path}.",
        shown ? "włączona" : "wyłączona",
        Configured.Enabled,
        _settings.Enabled?.ToString() ?? "brak",
        _settings.EnabledDefault?.ToString() ?? "brak",
        _store.Path);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        ApplyClickThrough();
    }

    /// <summary>
    /// Nakładka ma <c>SizeToContent</c>, więc jej szerokość jest znana dopiero po zmierzeniu kafelków, a
    /// zmienia się przy każdej zmianie liczby maszyn.
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RestorePlacement();

    private void OnStateChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (IsVisible)
        {
            Refresh();
        }
    }));

    /// <summary>
    /// Przelicza kafelki. Sygnatura chroni przed podmianą listy przy każdym tyknięciu zegara — inaczej
    /// animacja migania czerwonego kafelka startowałaby od nowa co dwie sekundy.
    /// </summary>
    private void Refresh()
    {
        MachineCardViewModel[] cards = MachineCardViewModel.ForEnabledMachines(_orchestrator, DateTimeOffset.UtcNow);
        string signature = string.Join("|", cards.Select(c => c.ToString()));
        if (signature == _signature)
        {
            return;
        }

        Tiles.ItemsSource = cards;
        _signature = signature;
        ApplyLayout();
    }

    private void RestorePlacement()
    {
        if (_settings.HasPlacement)
        {
            Left = _settings.Left!.Value;
            Top = _settings.Top!.Value;
            WindowPlacement.ClampToVisibleArea(this);
            return;
        }

        WindowPlacement.PositionInCorner(this, _orchestrator.Config.Ui.Overlay.Position);
    }

    private void ApplyClickThrough()
    {
        bool wanted = ClickThrough;
        ApplyLayoutPickerVisibility();
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || wanted == _clickThrough)
        {
            return;
        }

        try
        {
            MaszynyAlert.Windows.WindowClickThrough.Apply(handle, wanted);
            _clickThrough = wanted;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            // Nakładka działa dalej, tylko nadal łapie kliknięcia — to stan domyślny, więc nie ma czym straszyć
            // operatora okienkiem.
            _logger?.LogWarning(ex, "Nie udało się zmienić przepuszczania kliknięć nakładki statusu.");
        }
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        WindowPlacement.ClampToVisibleArea(this);
        SaveSettings(_settings with { Left = Left, Top = Top });
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.Button)sender).Tag is string machineId && !string.IsNullOrEmpty(machineId))
        {
            MachineChosen?.Invoke(machineId);
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => SetEnabled(false);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Okno jest singletonem jak pozostałe okna programu — zamknięcie znaczy „schowaj”. Wyboru operatora
        // NIE zapisujemy: przy zatrzymaniu programu („vncalert killapp”, wylogowanie albo zamknięcie
        // Windows) WPF sam zamyka wszystkie okna. Krzyżyk na nakładce ma własną obsługę
        // (HideButton_Click) i nadal zapamiętuje świadomy wybór operatora.
        e.Cancel = true;
        HideOverlay();
    }

    private void SaveSettings(OverlaySettings settings)
    {
        _settings = settings;
        if (!_store.Save(settings))
        {
            _logger?.LogWarning("Nie udało się zapisać ustawień nakładki statusu w pliku {Path}.", _store.Path);
        }
    }
}
