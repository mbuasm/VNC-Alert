using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class ImageOpsTests
{
    private static readonly RedThresholds Thresholds = new();

    /// <summary>Obraz 40x20 w kolorze tła z czerwonym kwadratem 4x4 zaczynającym się w (10,5).</summary>
    private static RgbaImage RedBlock()
    {
        var img = new RgbaImage(40, 20);
        img.Fill(new Rgba(200, 200, 200));
        for (int y = 5; y < 9; y++)
        {
            for (int x = 10; x < 14; x++)
            {
                img.SetPixel(x, y, new Rgba(220, 30, 30));
            }
        }

        return img;
    }

    [Fact]
    public void PrepareForOcr_ZwracaObrazOWymiarachBboxPowiekszonegoOMarginesIPrzeskalowanego()
    {
        Gray8Image gray = ImageOps.PrepareForOcr(RedBlock(), Thresholds, marginPx: 2, scale: 2);

        // bbox 4x4 + 2*2 marginesu = 8x8, po przycięciu do obrazu bez zmian, razy skala 2.
        Assert.Equal(16, gray.Width);
        Assert.Equal(16, gray.Height);
    }

    [Fact]
    public void PrepareForOcr_ZamieniaCzerwonyTekstNaCzarnyNaBialymTle()
    {
        Gray8Image gray = ImageOps.PrepareForOcr(RedBlock(), Thresholds, marginPx: 2, scale: 1);

        // Środek czerwonego kwadratu w układzie wycinka: (10-8, 5-3) = (2,2).
        Assert.Equal(0, gray.Get(3, 3));
        // Róg wycinka (2 px marginesu od bboxu) — tło.
        Assert.Equal(255, gray.Get(0, 0));
    }

    [Fact]
    public void PrepareForOcr_DylatacjaPoszerzaMaskeOJedenPiksel()
    {
        Gray8Image gray = ImageOps.PrepareForOcr(RedBlock(), Thresholds, marginPx: 2, scale: 1);

        // Piksel (9,4) w oryginale sąsiaduje z czerwienią — po dylatacji ma być czarny.
        // W układzie wycinka (offset 8,3) to (1,1).
        Assert.Equal(0, gray.Get(1, 1));
        // Piksel (8,3) jest dwa piksele od czerwieni — pozostaje biały.
        Assert.Equal(255, gray.Get(0, 0));
    }

    [Fact]
    public void PrepareForOcr_ZWylaczonaDylatacja_NieRozszerzaMaski()
    {
        Gray8Image gray = ImageOps.PrepareForOcr(RedBlock(), Thresholds, marginPx: 2, scale: 1, dilate: false);

        Assert.Equal(255, gray.Get(1, 1));
        Assert.Equal(0, gray.Get(2, 2));
    }

    [Fact]
    public void PrepareForOcr_GdyBrakCzerwieni_ZwracaCalyBialyObrazWRozmiarzeCalegoWejscia()
    {
        var img = new RgbaImage(20, 10);
        img.Fill(new Rgba(240, 240, 240));

        Gray8Image gray = ImageOps.PrepareForOcr(img, Thresholds, marginPx: 5, scale: 2);

        Assert.Equal(40, gray.Width);
        Assert.Equal(20, gray.Height);
        Assert.All(gray.Pixels, p => Assert.Equal(255, p));
    }

    [Fact]
    public void PrepareForOcr_GdyMarginesWychodziPozaObraz_JestPrzycinanyDoGranic()
    {
        var img = new RgbaImage(20, 10);
        img.Fill(new Rgba(240, 240, 240));
        img.SetPixel(0, 0, new Rgba(220, 30, 30));

        Gray8Image gray = ImageOps.PrepareForOcr(img, Thresholds, marginPx: 50, scale: 1);

        // bbox (0,0,1,1) + 50 marginesu → po przycięciu cały obraz 20x10.
        Assert.Equal(20, gray.Width);
        Assert.Equal(10, gray.Height);
        Assert.Equal(0, gray.Get(0, 0));
    }

    [Fact]
    public void PrepareForOcr_ZeSkalaJeden_NiePowiekszaObrazu()
    {
        Gray8Image gray = ImageOps.PrepareForOcr(RedBlock(), Thresholds, marginPx: 0, scale: 1);

        Assert.Equal(4, gray.Width);
        Assert.Equal(4, gray.Height);
    }

    [Fact]
    public void PrepareForOcr_ZeSkalaTrzy_PowielaKazdyPikselDziewieciokrotnie()
    {
        Gray8Image gray = ImageOps.PrepareForOcr(RedBlock(), Thresholds, marginPx: 0, scale: 3);

        Assert.Equal(12, gray.Width);
        Assert.Equal(12, gray.Height);
        Assert.Equal(0, gray.Get(0, 0));
        Assert.Equal(0, gray.Get(11, 11));
    }

    [Theory]
    [InlineData(255, 0, 0, 76)]
    [InlineData(0, 255, 0, 150)]
    [InlineData(0, 0, 255, 29)]
    [InlineData(255, 255, 255, 255)]
    [InlineData(0, 0, 0, 0)]
    public void ToGray_LiczyJasnoscWgWagLuminancji(byte r, byte g, byte b, byte expected)
    {
        var img = new RgbaImage(1, 1);
        img.SetPixel(0, 0, new Rgba(r, g, b));

        Gray8Image gray = ImageOps.ToGray(img);

        Assert.Equal(1, gray.Width);
        Assert.Equal(expected, gray.Get(0, 0));
    }

    [Fact]
    public void ToGray_ZachowujeWymiaryObrazu()
    {
        Gray8Image gray = ImageOps.ToGray(new RgbaImage(7, 3));
        Assert.Equal(7, gray.Width);
        Assert.Equal(3, gray.Height);
    }

    [Fact]
    public void RedFraction_LiczyUdzialCzerwonychPikseli()
    {
        var img = new RgbaImage(10, 10);
        img.Fill(new Rgba(255, 255, 255));
        for (int i = 0; i < 25; i++)
        {
            img.SetPixel(i % 10, i / 10, new Rgba(220, 30, 30));
        }

        Assert.Equal(0.25, ImageOps.RedFraction(img, Thresholds), 9);
    }

    [Fact]
    public void RedFraction_DlaObrazuBezCzerwieni_ZwracaZero()
    {
        var img = new RgbaImage(5, 5);
        img.Fill(new Rgba(255, 255, 255));

        Assert.Equal(0.0, ImageOps.RedFraction(img, Thresholds));
    }
}
