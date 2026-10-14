using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MaszynyAlert.App.Views;

internal static class TaskbarIcon
{
    private static readonly ConditionalWeakTable<Window, object> AttachedWindows = new();
    private static ImageSource? _appIcon;

    /// <summary>Podpina odświeżanie pod każde okno programu (raz na proces — w konstruktorze <see cref="App"/>).</summary>
    public static void RegisterForAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Attach(window);
            }));

    /// <summary>
    /// Okno z przyciskiem na pasku zadań odświeża ikonę po każdym pokazaniu; okno bez przycisku (dymek, alert, nakładka) — nie.
    /// Wołane z <c>Loaded</c>, czyli już po pierwszym pokazaniu — stąd od razu jedno odświeżenie.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.ShowInTaskbar || AttachedWindows.TryGetValue(window, out _)) return;
        AttachedWindows.Add(window, new object());
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) ScheduleRefresh(window);
        };
        ScheduleRefresh(window);
    }

    /// <summary>Czy okno ma podpięte odświeżanie (testy).</summary>
    internal static bool IsAttached(Window window) => AttachedWindows.TryGetValue(window, out _);

    /// <summary>
    /// Ustawia ikonę okna jeszcze raz: WPF tworzy przy tym nowe uchwyty i wysyła <c>WM_SETICON</c>, a pasek zadań odświeża przycisk
    /// (ten sam uchwyt drugi raz niczego by nie zmienił). Okno bez własnej ikony dostaje ikonę programu — tę samą, którą WPF brał z EXE.
    /// </summary>
    internal static void Refresh(Window window)
    {
        if (!window.IsVisible) return;
        ImageSource icon = window.Icon ?? AppIcon();
        window.Icon = null;
        window.Icon = icon;
    }

    private static void ScheduleRefresh(Window window) =>
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Refresh(window)));

    private static ImageSource AppIcon()
    {
        if (_appIcon is null)
        {
            BitmapFrame frame = BitmapFrame.Create(new Uri("pack://application:,,,/MaszynyAlert.App;component/Assets/VncAlert.ico"));
            frame.Freeze();
            _appIcon = frame;
        }

        return _appIcon;
    }
}
