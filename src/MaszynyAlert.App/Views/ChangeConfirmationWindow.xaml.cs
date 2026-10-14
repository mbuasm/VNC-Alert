using System.Windows;
using MaszynyAlert.App.ViewModels;

namespace MaszynyAlert.App.Views;

public partial class ChangeConfirmationWindow : Window
{
    public ChangeConfirmationWindow(IReadOnlyList<SettingChange> changes)
    {
        InitializeComponent();
        ChangeList.ItemsSource = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>
/// Pytanie „zapisać?” w programie: okno modalne nad aktywnym oknem programu. Zamknięcie krzyżykiem = Anuluj.
/// </summary>
public sealed class DialogSettingsChangeConfirmation : ISettingsChangeConfirmation
{
    public bool Confirm(IReadOnlyList<SettingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var dialog = new ChangeConfirmationWindow(changes);
        Window? owner = System.Windows.Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible && !ReferenceEquals(w, dialog));
        if (owner is not null) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }
}
