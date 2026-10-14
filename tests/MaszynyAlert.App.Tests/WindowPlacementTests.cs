using System.Windows;
using MaszynyAlert.App.Views;

namespace MaszynyAlert.App.Tests;

public sealed class WindowPlacementTests
{
    private const double Margin = WindowPlacement.DefaultEdgeMargin;

    /// <summary>Typowy monitor główny 1920×1080 z paskiem zadań u dołu (48 px).</summary>
    private static readonly Rect PrimaryWorkArea = new(0, 0, 1920, 1032);

    /// <summary>
    /// Drugi monitor nad-i-w-lewo od głównego — cały w ujemnych współrzędnych, bo pulpit Windows ma początek
    /// w lewym-górnym narożniku monitora GŁÓWNEGO. To ten przypadek psuł <see cref="SystemParameters.WorkArea"/>,
    /// który zawsze zwraca monitor główny.
    /// </summary>
    private static readonly Rect SecondWorkArea = new(-1600, -900, 1600, 860);

    [Fact]
    public void ClampToWorkArea_ZostawiaOknoKtoreSieMiesci()
    {
        double top = WindowPlacement.ClampToWorkArea(400, 300, PrimaryWorkArea, Margin);

        Assert.Equal(400, top);
    }

    [Fact]
    public void ClampToWorkArea_PodnosiOknoRosnaceWDol()
    {
        // Okno przeciągnięte tuż nad pasek zadań (wysokość 340), a po rozwinięciu pomocy ma 600.
        double draggedTop = PrimaryWorkArea.Bottom - 340 - Margin;

        double top = WindowPlacement.ClampToWorkArea(draggedTop, 600, PrimaryWorkArea, Margin);

        Assert.Equal(PrimaryWorkArea.Bottom - 600 - Margin, top);
        Assert.True(top + 600 <= PrimaryWorkArea.Bottom - Margin);
    }

    [Fact]
    public void ClampToWorkArea_NieOpuszczaOknaKtoreJestWyzej()
    {
        // Okno w górnej połowie ekranu po rozwinięciu nadal się mieści — korekta nie może go przesuwać w dół.
        double top = WindowPlacement.ClampToWorkArea(100, 600, PrimaryWorkArea, Margin);

        Assert.Equal(100, top);
    }

    [Fact]
    public void ClampToWorkArea_SciagaOknoSponadGornejKrawedzi()
    {
        double top = WindowPlacement.ClampToWorkArea(-200, 300, PrimaryWorkArea, Margin);

        Assert.Equal(PrimaryWorkArea.Top + Margin, top);
    }

    [Fact]
    public void ClampToWorkArea_LiczyWzgledemMonitoraOUjemnychWspolrzednych()
    {
        double top = WindowPlacement.ClampToWorkArea(SecondWorkArea.Bottom - 100, 300, SecondWorkArea, Margin);

        Assert.Equal(SecondWorkArea.Bottom - 300 - Margin, top);
        Assert.True(top < 0, "Drugi monitor ma ujemne współrzędne — korekta musi je zachować, a nie zerować.");
    }

    [Fact]
    public void ClampToWorkArea_OknoWyzszeNizEkranPokazujeSwojaGore()
    {
        // Nie da się spełnić obu warunków naraz — widoczna zostaje góra okna, resztę przewija ScrollViewer.
        double top = WindowPlacement.ClampToWorkArea(500, PrimaryWorkArea.Height + 200, PrimaryWorkArea, Margin);

        Assert.Equal(PrimaryWorkArea.Top + Margin, top);
    }

    [Fact]
    public void ClampToWorkArea_NieustalonaPozycjaTrafiaDoDolnejKrawedzi()
    {
        // Window.Top jest NaN, dopóki okno nie zostało ustawione — wtedy trzymamy domyślny dolny narożnik.
        double top = WindowPlacement.ClampToWorkArea(double.NaN, 300, PrimaryWorkArea, Margin);

        Assert.Equal(PrimaryWorkArea.Bottom - 300 - Margin, top);
    }

    [Fact]
    public void MaxHeightFor_NaTypowymEkranieZostawiaWysokoscZProjektu()
    {
        Assert.Equal(620, WindowPlacement.MaxHeightFor(PrimaryWorkArea, 620, Margin));
    }

    [Fact]
    public void MaxHeightFor_NaNiskimEkranieObnizaWysokoscOMarginesy()
    {
        var low = new Rect(0, 0, 1024, 500);

        Assert.Equal(500 - (2 * Margin), WindowPlacement.MaxHeightFor(low, 620, Margin));
    }

    [Fact]
    public void MaxHeightFor_ZdegenerowanyObszarNieDajeZeraAniWartosciUjemnej()
    {
        Assert.Equal(620, WindowPlacement.MaxHeightFor(new Rect(0, 0, 1024, 20), 620, Margin));
    }
}
