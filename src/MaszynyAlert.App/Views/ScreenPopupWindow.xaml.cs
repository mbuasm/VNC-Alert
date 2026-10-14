using System.ComponentModel;
using System.Windows;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Views;

public partial class ScreenPopupWindow : Window
{
    private readonly AppOrchestrator _orchestrator;
    private readonly WavSoundPlayer _sound;
    private readonly ScreenPopupViewModel _viewModel;

    /// <summary>Klucze widoczne przy OSTATNIM odświeżeniu — do wykrycia "nowego" klucza.</summary>
    private HashSet<AlertKey> _knownKeys = new();

    /// <summary>Tryb przypięty (menu zasobnika) — okno widoczne także przy pustej liście, dopóki operator go nie zamknie.</summary>
    private bool _pinned;

    public ScreenPopupWindow(AppOrchestrator orchestrator, WavSoundPlayer sound)
    {
        InitializeComponent();

        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        WindowEffects.Attach(this, orchestrator.Config.Ui.AlertOpacity);
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));

        _viewModel = new ScreenPopupViewModel(key => _orchestrator.DismissWrongScreenAlertAsync(key));
        DataContext = _viewModel;

        Closing += OnClosing;
        // Wspólne pozycjonowanie (WindowPlacement) liczy obszar roboczy monitora, na którym stoi okno —
        // przy dwóch monitorach pop-up nie wyjeżdża poza ekran.
        Loaded += (_, _) => WindowPlacement.PositionTopRight(this);
        SizeChanged += (_, _) => WindowPlacement.PositionTopRight(this);

        _orchestrator.AlertsChanged += OnOrchestratorAlertsChanged;

        RefreshFromOrchestrator();
    }

    /// <summary>Pokazuje pop-up "przypięty" (menu zasobnika "Ekran HMI – włącz okno podglądu"): także przy pustej liście.</summary>
    public void ShowPinned()
    {
        _pinned = true;
        RefreshFromOrchestrator();
    }

    private void OnOrchestratorAlertsChanged() => Dispatcher.BeginInvoke(RefreshFromOrchestrator);

    private void RefreshFromOrchestrator()
    {
        IReadOnlyList<Alert> current = _orchestrator.WrongScreenAlerts;

        var currentKeys = new HashSet<AlertKey>(current.Select(a => a.Key));
        bool hasNewKey = currentKeys.Any(k => !_knownKeys.Contains(k));
        bool newKeyWithSound = current.Any(a => a.Sound && !_knownKeys.Contains(a.Key));
        if (_knownKeys.Count > 0 && current.Count == 0) _pinned = false;
        _knownKeys = currentKeys;

        _viewModel.Refresh(current);

        if (current.Count == 0 && !_pinned)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (hasNewKey && newKeyWithSound)
        {
            // Raz, przy pojawieniu się — nie w pętli ani nie co minutę jak alarm błędu (patrz AlertWindow).
            _sound.PlayOnce();
        }
    }

    /// <summary>"✕" w nagłówku: zamyka (ACK) WSZYSTKIE pokazane powiadomienia i kończy tryb przypięty — okno schowa się po odświeżeniu.</summary>
    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = false;
        foreach (AlertKey key in _knownKeys.ToList())
        {
            await _orchestrator.DismissWrongScreenAlertAsync(key);
        }

        RefreshFromOrchestrator();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Okno jest tworzone raz i ponownie używane — zamknięcie tylko je chowa.
        e.Cancel = true;
        _pinned = false;
        Hide();
    }
}
