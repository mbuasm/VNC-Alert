using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;

namespace MaszynyAlert.App.Views;

public partial class SharedConfigView : System.Windows.Controls.UserControl
{
    internal const double WideLayoutMinWidth = 900;

    private readonly SharedConfigViewModel _viewModel;
    private bool? _wide;

    /// <param name="messages">Dymek „Zapisano ustawienia” po udanym zapisie; <see langword="null"/> = <see cref="DesktopUserMessages"/>.</param>
    public SharedConfigView(SharedConfigViewModel viewModel, AppOrchestrator? orchestrator = null, IUserMessages? messages = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        IUserMessages notify = messages ?? DesktopUserMessages.Instance;
        _viewModel.Saved += title => notify.Notify(NotificationKind.Success, title);
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        ApplyResponsiveLayout(double.PositiveInfinity);

        if (orchestrator is null) return;

        // Wspólna konfiguracja może się zmienić bez udziału tego administratora (inny komputer grupy).
        // Zdarzenie przychodzi z wątku odbioru, więc wracamy na wątek okna; niezapisanych wpisów model
        // widoku nie nadpisuje — pokazuje zdanie „Odśwież, żeby zobaczyć nowe wartości”.
        // Subskrypcja jest na cały czas życia programu: widok i orkiestrator są singletonami, a
        // TabControl odłącza treść niewybranej zakładki (Unloaded), więc wypinanie się wtedy ze zdarzenia
        // zostawiałoby zakładkę głuchą po pierwszym przełączeniu.
        orchestrator.ConfigChanged += () => Dispatcher.BeginInvoke(new Action(() => _viewModel.RefreshFromOutside(changedElsewhere: true)));
    }

    /// <summary>Szeroko: szczegóły w prawej kolumnie, komunikat obok przycisków; wąsko: jedno pod drugim.</summary>
    private void ApplyResponsiveLayout(double width)
    {
        bool wide = width >= WideLayoutMinWidth;
        if (_wide == wide) return;
        _wide = wide;

        DetailColumn.Width = wide ? new System.Windows.GridLength(300) : new System.Windows.GridLength(0);
        System.Windows.Controls.Grid.SetColumn(DetailPanel, wide ? 1 : 0);
        System.Windows.Controls.Grid.SetRow(DetailPanel, wide ? 0 : 2);
        System.Windows.Controls.Grid.SetRowSpan(DetailPanel, wide ? 3 : 1);
        DetailPanel.Margin = wide ? new System.Windows.Thickness(14, 0, 0, 0) : new System.Windows.Thickness(0, 8, 0, 0);
        DetailPanel.MaxHeight = wide ? double.PositiveInfinity : 200;

        FooterButtonsColumn.Width = wide ? System.Windows.GridLength.Auto : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(MessageText, wide ? 1 : 0);
        System.Windows.Controls.Grid.SetRow(MessageText, wide ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(MessageText, wide ? 1 : 2);
        MessageText.Margin = wide ? new System.Windows.Thickness(4, 0, 0, 4) : new System.Windows.Thickness(0, 2, 0, 0);
    }

    /// <summary>
    /// Kliknięcie w wiersz albo fokus w jego polu (także z klawiatury, Tab) zaznacza to ustawienie — panel
    /// szczegółów pokazuje wtedy jego opis, klucz i błąd.
    /// </summary>
    private void Row_Activated(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: SharedConfigField field })
            _viewModel.SelectedField = field;
    }

    /// <summary>Odświeżenie przy pokazaniu zakładki (Panel administracyjny); niezapisane wpisy zostają bez komunikatu.</summary>
    public void RefreshOnShow() => _viewModel.RefreshFromOutside(changedElsewhere: false);
}
