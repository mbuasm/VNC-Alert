using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class FrameDiffTests
{
    private static RgbaImage Solid(Rgba color, int w = 10, int h = 10)
    {
        var img = new RgbaImage(w, h);
        img.Fill(color);
        return img;
    }

    [Fact]
    public void ChangedFraction_IdentycznychObrazow_ZwracaZero()
    {
        // Dlaczego: nieruchome okienko statusu (ten sam komunikat) nie może wywoływać kolejnych OCR-ów.
        var color = new Rgba(30, 60, 200);

        Assert.Equal(0.0, FrameDiff.ChangedFraction(Solid(color), Solid(color)));
    }

    [Fact]
    public void ChangedFraction_GdyZmienilaSieCwiartkaObrazu_ZwracaJednaCzwarta()
    {
        RgbaImage a = Solid(new Rgba(30, 60, 200));
        RgbaImage b = Solid(new Rgba(30, 60, 200));
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                b.SetPixel(x, y, new Rgba(220, 30, 30)); // 25 ze 100 pikseli
            }
        }

        Assert.Equal(0.25, FrameDiff.ChangedFraction(a, b), 6);
    }

    [Theory]
    [InlineData(24, 0.0)]    // różnica DOKŁADNIE równa tolerancji jeszcze się nie liczy (warunek to „>”)
    [InlineData(25, 1.0)]    // o jeden więcej — już się liczy
    public void ChangedFraction_RoznicyNaGranicyTolerancji_LiczyDopieroPowyzejNiej(int delta, double oczekiwany)
    {
        // Dlaczego: tolerancja jest po to, żeby drgania jasności/kompresji VNC nie udawały „nowego
        // komunikatu”; granica musi być jednoznaczna, inaczej próg 2 % zmienionych pikseli byłby loterią.
        RgbaImage a = Solid(new Rgba(100, 100, 100));
        RgbaImage b = Solid(new Rgba((byte)(100 + delta), 100, 100));

        Assert.Equal(oczekiwany, FrameDiff.ChangedFraction(a, b), 6);
    }

    [Fact]
    public void ChangedFraction_ZWlasnaTolerancja_UzywaJejZamiastDomyslnej()
    {
        RgbaImage a = Solid(new Rgba(100, 100, 100));
        RgbaImage b = Solid(new Rgba(101, 100, 100));

        Assert.Equal(0.0, FrameDiff.ChangedFraction(a, b));                        // domyślne 24
        Assert.Equal(1.0, FrameDiff.ChangedFraction(a, b, perChannelTolerance: 0));
    }

    [Fact]
    public void ChangedFraction_ObrazowORoznychRozmiarach_ZwracaJeden()
    {
        // Dlaczego: zmiana rozmiaru ROI (np. po zmianie rozdzielczości ekranu maszyny) to „zupełnie inny
        // obraz” — próba wyrównywania/przycinania dawałaby ciche, nieprzewidywalne wyniki.
        Assert.Equal(1.0, FrameDiff.ChangedFraction(Solid(new Rgba(1, 2, 3), 10, 10), Solid(new Rgba(1, 2, 3), 10, 11)));
        Assert.Equal(1.0, FrameDiff.ChangedFraction(Solid(new Rgba(1, 2, 3), 10, 10), Solid(new Rgba(1, 2, 3), 9, 10)));
    }

    [Fact]
    public void ChangedFraction_DwochObrazowPustych_ZwracaZero()
    {
        // Dlaczego: brak pikseli to nie jest „zmiana” — inaczej pusty ROI (błąd konfiguracji) wyzwalałby
        // OCR w każdym cyklu.
        Assert.Equal(0.0, FrameDiff.ChangedFraction(new RgbaImage(0, 0), new RgbaImage(0, 0)));
    }

    [Fact]
    public void ChangedFraction_RoznicyWylacznieWKanaleAlfa_ZwracaZero()
    {
        // Dlaczego: zachowanie utrwalone — porównywane są tylko kanały R/G/B (przezroczystość w zrzutach
        // ekranu maszyn nie występuje, a jej uwzględnianie dawałoby fałszywe „zmiany treści”).
        RgbaImage a = Solid(new Rgba(30, 60, 200, 255));
        RgbaImage b = Solid(new Rgba(30, 60, 200, 0));

        Assert.Equal(0.0, FrameDiff.ChangedFraction(a, b));
    }

    [Fact]
    public void ChangedFraction_Null_RzucaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FrameDiff.ChangedFraction(null!, Solid(new Rgba(1, 2, 3))));
        Assert.Throws<ArgumentNullException>(() => FrameDiff.ChangedFraction(Solid(new Rgba(1, 2, 3)), null!));
    }
}
