using System.Windows;
using MaszynyAlert.App.Views;

namespace MaszynyAlert.App.Tests;

public sealed class PopupStackPlacementTests
{
    private const double Margin = WindowPlacement.DefaultEdgeMargin;
    private const double Gap = WindowPlacement.DefaultStackGap;

    private static readonly Rect WorkArea = new(0, 0, 2752, 1104);

    private static readonly Size IdleSize = new(420, 231);

    private static Rect ScreenPopup => new(WorkArea.Right - 460 - Margin, WorkArea.Top + Margin, 460, 200);

    private static bool Overlaps(Rect a, Rect b)
    {
        Rect common = Rect.Intersect(a, b);
        return !common.IsEmpty && common.Width > 0.5 && common.Height > 0.5;
    }

    [Fact]
    public void BezInnychOkien_PostojWPrawymDolnymRogu()
    {
        Point p = WindowPlacement.IdlePopupPosition(WorkArea, IdleSize, screenPopup: null, alertWindow: null);

        Assert.Equal(WorkArea.Right - IdleSize.Width - Margin, p.X);
        Assert.Equal(WorkArea.Bottom - IdleSize.Height - Margin, p.Y);
    }

    [Fact]
    public void ZOknemAlertu_PostojNadNim()
    {
        var alert = new Rect(WorkArea.Right - 460 - Margin, WorkArea.Bottom - 400 - Margin, 460, 400);

        Point p = WindowPlacement.IdlePopupPosition(WorkArea, IdleSize, screenPopup: null, alertWindow: alert);

        Assert.Equal(alert.Top - Gap - IdleSize.Height, p.Y);
        Assert.Equal(WorkArea.Right - IdleSize.Width - Margin, p.X);
    }

    [Fact]
    public void WysokieOknoAlertuIEkranHmi_PostojNieNachodziNaZadneZNich()
    {
        var alert = new Rect(WorkArea.Right - 460 - Margin, WorkArea.Bottom - 700 - Margin, 460, 700);

        Point p = WindowPlacement.IdlePopupPosition(WorkArea, IdleSize, ScreenPopup, alert);
        var idle = new Rect(p, IdleSize);

        Assert.False(Overlaps(idle, ScreenPopup), $"Postój {idle} nachodzi na okno ekranu HMI {ScreenPopup}.");
        Assert.False(Overlaps(idle, alert), $"Postój {idle} nachodzi na okno alertu {alert}.");
        Assert.True(idle.Left >= WorkArea.Left + Margin && idle.Bottom <= WorkArea.Bottom - Margin + 0.5);
    }

    [Fact]
    public void EkranHmiBezOknaAlertu_PostojWPrawejKolumniePodSpodem()
    {
        Point p = WindowPlacement.IdlePopupPosition(WorkArea, IdleSize, ScreenPopup, alertWindow: null);
        var idle = new Rect(p, IdleSize);

        Assert.False(Overlaps(idle, ScreenPopup));
        Assert.Equal(WorkArea.Right - IdleSize.Width - Margin, p.X);
    }

    [Fact]
    public void BrakMiejscaWPrawejKolumnie_PostojObokZWidocznymNaglowkiem()
    {
        // Niski ekran 1024×768: między oknem HMI a oknem alertu nie zmieści się postój.
        var low = new Rect(0, 0, 1024, 728);
        var screen = new Rect(low.Right - 460 - Margin, low.Top + Margin, 460, 200);
        var alert = new Rect(low.Right - 460 - Margin, low.Bottom - 480 - Margin, 460, 480);

        Point p = WindowPlacement.IdlePopupPosition(low, IdleSize, screen, alert);
        var idle = new Rect(p, IdleSize);

        Assert.False(Overlaps(idle, screen), $"Postój {idle} nachodzi na okno ekranu HMI {screen}.");
        Assert.False(Overlaps(idle, alert), $"Postój {idle} nachodzi na okno alertu {alert}.");
        Assert.True(idle.Top >= low.Top + Margin - 0.5 && idle.Left >= low.Left + Margin - 0.5);
    }
}
