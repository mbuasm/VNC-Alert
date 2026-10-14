using System.ComponentModel;
using System.Windows;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Views;

public partial class SetupWindow : Window
{
    private readonly SetupView _view;
    private readonly bool _standalone;

    /// <param name="messages">Komunikaty widoku; <see langword="null"/> = <see cref="DesktopUserMessages"/> (patrz <see cref="SetupView"/>).</param>
    public SetupWindow(SetupWindowViewModel viewModel, AppConfig cfg, ISecretStore secrets, AppOrchestrator? orchestrator,
        ISettingsChangeConfirmation confirmation, IUserMessages? messages = null)
    {
        InitializeComponent();

        _standalone = orchestrator is null;
        _view = new SetupView(viewModel, cfg, secrets, orchestrator, includeSecrets: false, confirmation, messages);
        _view.Finished += Finish;
        Content = _view;

        Closing += OnClosing;
    }

    /// <summary>Czy pole klucza sieciowego jest widoczne (tryb jednego komputera je chowa) — sprawdzane w teście startu.</summary>
    public bool NetworkKeyGroupVisible => _view.NetworkKeyGroupVisible;

    /// <summary>Odświeża pola przed pokazaniem okna — checkbox autostartu pokazuje FAKTYCZNY stan wpisu.</summary>
    public void LoadCurrentValues() => _view.LoadCurrentValues();

    public void LoadCurrentValues(bool defaultAutostartOn) => _view.LoadCurrentValues(defaultAutostartOn);

    private void Finish()
    {
        Hide();
        // Samodzielny edytor (--setup) kończy proces razem z oknem — zamykamy je dopiero, gdy zniknie dymek „Zapisano ustawienia”.
        if (_standalone) ToastNotifications.WhenClosed(Close);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_standalone)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
