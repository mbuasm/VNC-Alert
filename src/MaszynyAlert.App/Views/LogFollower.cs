using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MaszynyAlert.App.Views;

internal sealed class LogFollower
{
    private readonly System.Windows.Controls.ListBox _list;
    private bool _following = true;

    public LogFollower(System.Windows.Controls.ListBox list)
    {
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    /// <summary>Czy log ma stać na najnowszym wpisie.</summary>
    public bool IsFollowing => _following;

    /// <summary>Przed odświeżeniem listy; <paramref name="resume"/> = pokazanie Panelu (śledzenie od nowa).</summary>
    public void BeforeRefresh(bool resume)
    {
        if (resume) _following = true;
    }

    /// <summary>Po odświeżeniu listy: przewinięcie na koniec w najbliższym przejściu układu (przed renderem).</summary>
    public void AfterRefresh()
    {
        if (_following) ScrollToEnd();
    }

    /// <summary>Przewija log do ostatniego wpisu (gdy lista jest w drzewie wizualnym).</summary>
    public void ScrollToEnd() => Viewer()?.ScrollToEnd();

    /// <summary>Wewnętrzny ScrollViewer listy (z szablonu ListBoxa); null, dopóki lista nie jest w drzewie wizualnym.</summary>
    public ScrollViewer? Viewer() => FindScrollViewer(_list);

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || !ReferenceEquals(viewer.TemplatedParent, _list)) return;

        // Zmieniła się sama pozycja (kółko, pasek, klawisze albo nasze przewinięcie na koniec): śledzimy, dopóki
        // lista stoi na dole. Lista przewija się po elementach (CanContentScroll), więc tolerancja to jeden wpis.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _following = viewer.ScrollableHeight <= 0 || viewer.VerticalOffset >= viewer.ScrollableHeight - 1;
        else if (_following && viewer.VerticalOffset < viewer.ScrollableHeight)
            viewer.ScrollToEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if ((child as ScrollViewer ?? FindScrollViewer(child)) is { } found) return found;
        }

        return null;
    }
}
