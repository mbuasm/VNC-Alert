using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Views;

internal static class WindowPlacement
{
    /// <summary>Domyślny margines od krawędzi obszaru roboczego, w pikselach niezależnych od DPI.</summary>
    public const double DefaultEdgeMargin = 12;

    /// <summary>Domyślny odstęp między oknem a oknem pod nim przy układaniu "nad innym oknem" (<see cref="PositionAbove"/>).</summary>
    public const double DefaultStackGap = 8;

    /// <summary>
    /// Obszar roboczy monitora, na którym stoi <paramref name="window"/>, w pikselach niezależnych od DPI
    /// (<c>Window.Left</c>/<c>Top</c> są w tych samych jednostkach). Monitor jest ustalany z uchwytu okna
    /// (<see cref="System.Windows.Forms.Screen.FromHandle"/>), a współrzędne urządzenia przeliczane przez
    /// <see cref="CompositionTarget.TransformFromDevice"/> — bez tego przy skalowaniu innym niż 100 % okno
    /// lądowałoby w złym miejscu. Dopóki okno nie ma jeszcze uchwytu (przed <c>Loaded</c>), zwracany jest
    /// obszar roboczy monitora głównego.
    /// </summary>
    public static Rect WorkAreaFor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return SystemParameters.WorkArea;
        }

        System.Drawing.Rectangle device = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        if (device.Width <= 0 || device.Height <= 0)
        {
            return SystemParameters.WorkArea;
        }

        Matrix fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        System.Windows.Point topLeft = fromDevice.Transform(new System.Windows.Point(device.Left, device.Top));
        System.Windows.Point bottomRight = fromDevice.Transform(new System.Windows.Point(device.Right, device.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// Pionowa pozycja okna skorygowana do obszaru roboczego: wynik spełnia
    /// <c>top + height ≤ workArea.Bottom - margin</c> oraz <c>top ≥ workArea.Top + margin</c>, a w granicach
    /// tego przedziału zostawia <paramref name="top"/> bez zmian (okno podniesione, nie przyklejone do rogu).
    /// Czysta funkcja — testowana w <c>WindowPlacementTests</c>.
    /// </summary>
    /// <param name="top">Żądana pozycja górnej krawędzi (może być <see cref="double.NaN"/> dla okna bez pozycji).</param>
    /// <param name="height">Wysokość okna po zmianie rozmiaru (<c>ActualHeight</c>).</param>
    /// <param name="workArea">Obszar roboczy monitora — patrz <see cref="WorkAreaFor"/>.</param>
    /// <param name="margin">Margines od krawędzi obszaru roboczego.</param>
    public static double ClampToWorkArea(double top, double height, Rect workArea, double margin = DefaultEdgeMargin)
    {
        double minTop = workArea.Top + margin;
        double maxTop = workArea.Bottom - height - margin;
        if (maxTop <= minTop)
        {
            // Okno wyższe niż obszar roboczy (bardzo niski ekran) — obu warunków spełnić nie można, więc
            // pokazujemy GÓRĘ okna z nagłówkiem i treścią pierwszego alertu; resztę przewija ScrollViewer.
            return minTop;
        }

        return double.IsFinite(top) ? Math.Clamp(top, minTop, maxTop) : maxTop;
    }

    /// <summary>
    /// Górne ograniczenie wysokości okna: mniejsza z wartości „wysokość z projektu okna”
    /// (<paramref name="designMaxHeight"/>) i „obszar roboczy bez marginesów”. Dzięki temu na niskim ekranie
    /// okno alertu nie jest wyższe od ekranu, a nadmiar treści przewija <c>ScrollViewer</c>. Zdegenerowany
    /// obszar roboczy (węższy niż same marginesy) zostawia wysokość z projektu — lepsza niż zwinięte do zera
    /// okno alertu. Czysta funkcja — testowana w <c>WindowPlacementTests</c>.
    /// </summary>
    public static double MaxHeightFor(Rect workArea, double designMaxHeight, double margin = DefaultEdgeMargin)
    {
        double available = workArea.Height - (2 * margin);
        return available <= 0 ? designMaxHeight : Math.Min(designMaxHeight, available);
    }

    /// <summary>
    /// Ustawia <paramref name="window"/> w prawym-dolnym rogu obszaru roboczego monitora, na którym stoi, z
    /// marginesem <paramref name="margin"/>. Wymaga, żeby <see cref="FrameworkElement.ActualWidth"/>/<c>ActualHeight</c>
    /// były już policzone (wołać po <c>Loaded</c>/<c>SizeChanged</c>, nie w konstruktorze).
    /// </summary>
    public static void PositionBottomRight(Window window, double margin = DefaultEdgeMargin)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect workArea = WorkAreaFor(window);
        window.Left = workArea.Right - window.ActualWidth - margin;
        window.Top = ClampToWorkArea(workArea.Bottom - window.ActualHeight - margin, window.ActualHeight, workArea, margin);
    }

    /// <summary>
    /// Ustawia <paramref name="window"/> w prawym-GÓRNYM rogu obszaru roboczego monitora, na którym stoi
    /// (prawy-dolny zajmuje pop-up „dołóż prace”, patrz <see cref="IdlePopupWindow"/>).
    /// </summary>
    public static void PositionTopRight(Window window, double margin = DefaultEdgeMargin)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect workArea = WorkAreaFor(window);
        window.Left = workArea.Right - window.ActualWidth - margin;
        window.Top = ClampToWorkArea(workArea.Top + margin, window.ActualHeight, workArea, margin);
    }

    /// <summary>
    /// Ustawia <paramref name="window"/> tuż NAD oknem <paramref name="other"/> (np. pop-up postoju nad oknem
    /// alertu, gdy oba są widoczne) — pionowo: <c>Top = other.Top - window.ActualHeight - gap</c> skorygowane
    /// do obszaru roboczego; poziomo: wyrównanie do tej samej prawej krawędzi obszaru roboczego, którą używa
    /// <see cref="PositionBottomRight"/> (tak oba okna "trzymają" wspólną prawą krawędź niezależnie od różnicy
    /// szerokości między nimi). Obszar roboczy jest brany z monitora okna <paramref name="other"/>, żeby przy
    /// dwóch monitorach oba okna zostały razem.
    /// </summary>
    public static void PositionAbove(Window window, Window other, double gap = DefaultStackGap, double edgeMargin = DefaultEdgeMargin)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(other);

        Rect workArea = WorkAreaFor(other);
        window.Left = workArea.Right - window.ActualWidth - edgeMargin;
        window.Top = ClampToWorkArea(other.Top - window.ActualHeight - gap, window.ActualHeight, workArea, edgeMargin);
    }

    public static Point IdlePopupPosition(Rect workArea, Size size, Rect? screenPopup, Rect? alertWindow,
        double gap = DefaultStackGap, double margin = DefaultEdgeMargin)
    {
        double right = workArea.Right - size.Width - margin;
        // Okno alertu przeciągnięte przez operatora poza prawą kolumnę nie wyznacza już miejsca postoju.
        if (alertWindow is { } moved && moved.Right <= right) alertWindow = null;
        double maxTop = alertWindow is { } alert ? alert.Top - size.Height - gap : workArea.Bottom - size.Height - margin;
        double minTop = screenPopup is { } screen ? screen.Bottom + gap : workArea.Top + margin;
        if (maxTop >= minTop)
        {
            return new Point(right, ClampToWorkArea(maxTop, size.Height, workArea, margin));
        }

        // Brak miejsca w kolumnie — obok, na lewo od węższego z okien kolumny, przy dolnej krawędzi.
        double columnLeft = Math.Min(screenPopup?.Left ?? double.PositiveInfinity, alertWindow?.Left ?? double.PositiveInfinity);
        double left = Math.Max(workArea.Left + margin, columnLeft - size.Width - gap);
        return new Point(left, ClampToWorkArea(workArea.Bottom - size.Height - margin, size.Height, workArea, margin));
    }

    public static Point ToastPosition(Rect workArea, Size size, Rect? anchor, double margin = DefaultEdgeMargin)
    {
        if (anchor is { Width: > 0, Height: > 0 } owner && owner.IntersectsWith(workArea))
        {
            double left = ClampToWorkArea(owner.Left + ((owner.Width - size.Width) / 2), size.Width, FlipToHorizontal(workArea), margin);
            return new Point(left, ClampToWorkArea(owner.Bottom - size.Height, size.Height, workArea, margin: 0));
        }

        return new Point(workArea.Right - size.Width - margin, ClampToWorkArea(workArea.Bottom - size.Height, size.Height, workArea, margin: 0));
    }

    public static void PositionInCorner(Window window, OverlayPosition position, double margin = DefaultEdgeMargin)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect workArea = WorkAreaFor(window);
        bool right = position is OverlayPosition.TopRight or OverlayPosition.BottomRight;
        bool bottom = position is OverlayPosition.BottomLeft or OverlayPosition.BottomRight;
        window.Left = right ? workArea.Right - window.ActualWidth - margin : workArea.Left + margin;
        window.Top = ClampToWorkArea(
            bottom ? workArea.Bottom - window.ActualHeight - margin : workArea.Top + margin,
            window.ActualHeight,
            workArea,
            margin);
    }

    /// <summary>
    /// Wciąga okno z powrotem na obszar roboczy w OBU osiach — dla okna, którego położenie pochodzi z pliku
    /// zapisanego przy innym układzie monitorów (nakładka statusu przeciągnięta na drugi ekran, który potem
    /// odłączono). Poziomo liczona jest ta sama korekta co pionowo, więc okno szersze niż ekran zostaje przy
    /// lewej krawędzi.
    /// </summary>
    public static void ClampToVisibleArea(Window window, double margin = DefaultEdgeMargin)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect workArea = WorkAreaFor(window);
        window.Left = ClampToWorkArea(window.Left, window.ActualWidth, FlipToHorizontal(workArea), margin);
        window.Top = ClampToWorkArea(window.Top, window.ActualHeight, workArea, margin);
    }

    /// <summary>
    /// Obszar roboczy „położony na bok”: to, co dla <see cref="ClampToWorkArea"/> jest górą i dołem, jest tu
    /// lewą i prawą krawędzią. Dzięki temu obie osie korygujemy jedną, przetestowaną funkcją.
    /// </summary>
    private static Rect FlipToHorizontal(Rect workArea) =>
        new(workArea.Top, workArea.Left, workArea.Height, workArea.Width);

    /// <summary>
    /// Koryguje TYLKO pionową pozycję <paramref name="window"/> (patrz <see cref="ClampToWorkArea"/>),
    /// zostawiając <see cref="Window.Left"/> bez zmian. Dla okna, które operator przeciągnął w wybrane miejsce
    /// i które potem urosło w dół (rozwinięcie „Jak rozwiązać ten problem?” w oknie alertu) — okno ma się
    /// podnieść, a nie wrócić do rogu.
    /// </summary>
    public static void ClampVertically(Window window, double margin = DefaultEdgeMargin)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Top = ClampToWorkArea(window.Top, window.ActualHeight, WorkAreaFor(window), margin);
    }
}
