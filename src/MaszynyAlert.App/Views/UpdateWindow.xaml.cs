using System.ComponentModel;
using System.Windows;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.App.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateCoordinator _coordinator;
    private readonly UpdateViewModel _viewModel;
    private bool _shouldShowBefore;
    private bool _previewing;

    public UpdateWindow(UpdateService updates, AppOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(orchestrator);
        InitializeComponent();

        _coordinator = updates.Coordinator;
        _viewModel = new UpdateViewModel(
            updates.ApproveAndInstallAsync,
            updates.InstallNowAsync,
            updates.CheckNowAsync,
            _coordinator.Postpone,
            () => orchestrator.Config.Cluster.Enabled && orchestrator.Config.Cluster.Group is not null);
        _viewModel.CloseRequested += Hide;
        DataContext = _viewModel;

        Closing += OnClosing;
        IsVisibleChanged += (_, _) =>
        {
            // Podgląd z testu startu kończy się schowaniem okna — dalej okno pokazuje znów prawdziwy stan.
            if (!IsVisible && _previewing)
            {
                _previewing = false;
                Refresh(autoShow: false);
            }
        };
        _coordinator.StateChanged += () => Dispatcher.BeginInvoke(() => Refresh(autoShow: true));
        updates.Installer.ConsentPending += hint => Dispatcher.BeginInvoke(() =>
        {
            if (_previewing) return;
            _viewModel.SetHint(hint);
            if (!IsVisible) Show();
        });

        // Stan startowy bez pokazywania okna — samo pokaże się dopiero przy przejściu ShouldShowWindow na prawdę.
        Refresh(autoShow: false);
        _shouldShowBefore = false;
    }

    /// <summary>Model widoku (test startu sprawdza przez niego napis przycisku głównego).</summary>
    internal UpdateViewModel ViewModel => _viewModel;

    /// <summary>Menu zasobnika „Aktualizacja programu…”: pokazuje bieżący stan, także „program jest aktualny”.</summary>
    public void ShowFromMenu()
    {
        _previewing = false;
        Refresh(autoShow: false);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void ShowPreview(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _previewing = true;
        _viewModel.Apply(state);
        Show();
    }

    private void Refresh(bool autoShow)
    {
        if (_previewing) return;
        UpdateState state = _coordinator.State;
        _viewModel.Apply(state);
        bool shouldShow = state.ShouldShowWindow;
        if (autoShow && shouldShow && !_shouldShowBefore && !IsVisible)
        {
            Show();
        }
        _shouldShowBefore = shouldShow;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Okno jest tworzone raz i ponownie używane — zamknięcie tylko je chowa.
        e.Cancel = true;
        Hide();
    }
}
