using System.Windows;

namespace MaszynyAlert.App.Services;

/// <summary>Rodzaj powiadomienia (<see cref="IUserMessages.Notify"/>): kolor i znak w kółku.</summary>
public enum NotificationKind
{
    /// <summary>Zielone kółko z „✓” — zapisano.</summary>
    Success,

    /// <summary>Bursztynowe kółko z „!” — zapisano, ale z zastrzeżeniem (np. diagnostyka z ostrzeżeniami).</summary>
    Warning,

    /// <summary>Czerwone kółko z „✕” — nie zapisano.</summary>
    Error,
}

public interface IUserMessages
{
    /// <summary>Pokazuje komunikat; parametry jak w <see cref="System.Windows.MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/>.</summary>
    MessageBoxResult Show(string text, string caption, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None);

    void Notify(NotificationKind kind, string title, string? detail = null, string? revealPath = null);
}

/// <summary>
/// Komunikaty programu: <see cref="Show"/> to zwykły <see cref="System.Windows.MessageBox"/>
/// (wywołanie dwuargumentowe <c>MessageBox.Show(text, caption)</c> to OK
/// i brak ikony); <see cref="Notify"/> to dymek <see cref="ToastNotifications"/>.
/// </summary>
public sealed class DesktopUserMessages : IUserMessages
{
    public static DesktopUserMessages Instance { get; } = new();

    public MessageBoxResult Show(string text, string caption, MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None) =>
        System.Windows.MessageBox.Show(text, caption, button, icon);

    public void Notify(NotificationKind kind, string title, string? detail = null, string? revealPath = null) =>
        ToastNotifications.Show(kind, title, detail, revealPath);
}
