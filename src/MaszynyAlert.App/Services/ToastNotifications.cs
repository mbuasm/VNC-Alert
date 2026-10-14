using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MaszynyAlert.App.Views;

namespace MaszynyAlert.App.Services;

public static class ToastNotifications
{
    internal static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(2);

    /// <summary>Obecnie pokazywany dymek (<see langword="null"/>, gdy żadnego nie ma).</summary>
    public static ToastWindow? Current { get; private set; }

    /// <summary>Pokazuje dymek; poprzedni (jeśli stoi) znika od razu.</summary>
    public static ToastWindow Show(NotificationKind kind, string title, string? detail = null, string? revealPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Current?.Dismiss(immediately: true);
        Window? anchor = ActiveWindow();
        var toast = new ToastWindow(kind, title, detail, revealPath, DisplayDuration);
        Current = toast;
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, toast)) Current = null;
        };

        // Pierwsze ustawienie tylko wybiera monitor (obszar roboczy liczony z uchwytu dymka); właściwa pozycja — po zmierzeniu.
        Rect start = anchor is not null ? AnchorRect(anchor) : SystemParameters.WorkArea;
        toast.Left = start.Left;
        toast.Top = start.Top;
        toast.Loaded += (_, _) => Place(toast, anchor);
        toast.Show();
        return toast;
    }

    /// <summary>
    /// Wykonuje <paramref name="action"/>, gdy obecny dymek zniknie (od razu, gdy żadnego nie ma). Samodzielny edytor ustawień
    /// (<c>--setup</c>) kończy proces razem z oknem — bez tego dymek „Zapisano ustawienia” znikałby w chwili pojawienia się.
    /// </summary>
    public static void WhenClosed(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Current is not { } toast)
        {
            action();
            return;
        }

        toast.Closed += (_, _) => action();
    }

    /// <summary>Aktywne, widoczne okno programu (Panel, Ustawienia) — kotwica dymka; inny dymek się nie liczy.</summary>
    private static Window? ActiveWindow() =>
        System.Windows.Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible && w is not ToastWindow && w.WindowState != WindowState.Minimized);

    /// <summary>
    /// WIDOCZNY prostokąt okna kotwicy w pikselach niezależnych od DPI — dymek chowa się dokładnie pod jego dolną krawędzią. Windows 11
    /// dolicza do <c>Left</c>/<c>Top</c>/<c>ActualHeight</c> niewidoczną ramkę do zmiany rozmiaru (ok. 7 px), więc granice bierzemy z DWM
    /// (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>). Gdy DWM ich nie poda: okno zmaksymalizowane = obszar roboczy jego monitora (w <c>Left</c>/<c>Top</c>
    /// ma położenie sprzed maksymalizacji), inne — <c>Left</c>/<c>Top</c> i rozmiar.
    /// </summary>
    private static Rect AnchorRect(Window anchor)
    {
        nint handle = new WindowInteropHelper(anchor).Handle;
        if (handle != 0 && DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out NativeRect device, Marshal.SizeOf<NativeRect>()) == 0
            && device.Right > device.Left && device.Bottom > device.Top
            && PresentationSource.FromVisual(anchor)?.CompositionTarget?.TransformFromDevice is { } fromDevice)
        {
            return new Rect(fromDevice.Transform(new System.Windows.Point(device.Left, device.Top)),
                fromDevice.Transform(new System.Windows.Point(device.Right, device.Bottom)));
        }

        return anchor.WindowState == WindowState.Maximized
            ? WindowPlacement.WorkAreaFor(anchor)
            : new Rect(anchor.Left, anchor.Top, anchor.ActualWidth, anchor.ActualHeight);
    }

    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeRect value, int size);

    private static void Place(ToastWindow toast, Window? anchor)
    {
        Rect workArea = anchor is not null ? WindowPlacement.WorkAreaFor(anchor) : WindowPlacement.WorkAreaFor(toast);
        Rect? owner = anchor is not null && anchor.IsVisible ? AnchorRect(anchor) : null;
        System.Windows.Point position = WindowPlacement.ToastPosition(workArea, new System.Windows.Size(toast.ActualWidth, toast.ActualHeight), owner);
        toast.Left = position.X;
        toast.Top = position.Y;
    }
}
