using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MaszynyAlert.App.Views;

internal static class WheelRouting
{
    /// <summary>Czy wewnętrzna lista może przewinąć się w kierunku kółka (<paramref name="delta"/> &gt; 0 = w górę).</summary>
    public static bool InnerCanScroll(double offset, double scrollableHeight, int delta) =>
        scrollableHeight > 0 && (delta > 0 ? offset > 0 : offset < scrollableHeight);

    /// <summary>
    /// Podpina do strony (<paramref name="page"/>) przekierowanie kółka: najbliższy wewnętrzny
    /// <see cref="ScrollViewer"/> pod kursorem dostaje kółko, gdy może się przewinąć, inaczej przewija się strona.
    /// </summary>
    public static void Attach(ScrollViewer page)
    {
        ArgumentNullException.ThrowIfNull(page);
        page.PreviewMouseWheel += (_, e) =>
        {
            ScrollViewer? inner = InnerScrollViewer(e.OriginalSource as DependencyObject, page);
            if (inner is not null && InnerCanScroll(inner.VerticalOffset, inner.ScrollableHeight, e.Delta)) return;
            page.ScrollToVerticalOffset(page.VerticalOffset - e.Delta);
            e.Handled = true;
        };
    }

    private static ScrollViewer? InnerScrollViewer(DependencyObject? source, ScrollViewer page)
    {
        for (DependencyObject? node = source; node is not null && !ReferenceEquals(node, page); node = Parent(node))
        {
            if (node is ScrollViewer viewer) return viewer;
        }

        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
