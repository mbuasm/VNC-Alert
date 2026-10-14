using System.Windows;
using System.Windows.Controls;

namespace MaszynyAlert.App.Views;

/// <summary>
/// Zachowanie strony „Podgląd techniczny” (przewijanej jako całość, z listami w środku) — podpinane w
/// <see cref="StatusWindow"/> i w teście układu zakładki (TechnicalTabRefreshTests), żeby test sprawdzał to samo okablowanie.
/// </summary>
internal static class TechnicalPage
{
    public static void Attach(ScrollViewer page)
    {
        ArgumentNullException.ThrowIfNull(page);
        WheelRouting.Attach(page);
        if (page.Content is not UIElement content)
            throw new InvalidOperationException("Strona Podglądu technicznego nie ma treści (stosu sekcji).");
        content.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
        {
            // ComboBox to też ItemsControl, ale fokus na nim (Tab) ma pokazać pole wyboru.
            if (e.TargetRect.IsEmpty && e.TargetObject is (ItemsControl and not System.Windows.Controls.ComboBox) or ScrollViewer)
                e.Handled = true;
        }));
    }
}
