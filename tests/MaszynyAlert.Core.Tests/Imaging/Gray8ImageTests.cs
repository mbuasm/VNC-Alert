using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Tests.Imaging;

public sealed class Gray8ImageTests
{
    [Fact]
    public void Konstruktor_GdyTablicaMaZlyRozmiar_RzucaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new Gray8Image(3, 3, new byte[8]));
    }

    [Fact]
    public void SetIGet_ZapisujaIOdczytujaJasnosc()
    {
        var img = new Gray8Image(4, 3);
        img.Set(3, 2, 200);
        Assert.Equal(200, img.Get(3, 2));
        Assert.Equal(0, img.Get(0, 0));
    }

    [Fact]
    public void Get_PozaObrazem_RzucaArgumentOutOfRange()
    {
        var img = new Gray8Image(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Get(2, 0));
    }

    [Fact]
    public void ToRgba_KopiujeJasnoscDoTrzechKanalowIUstawiaPelnaNieprzezroczystosc()
    {
        var gray = new Gray8Image(2, 2, [0, 64, 128, 255]);
        RgbaImage rgba = gray.ToRgba();

        Assert.Equal(2, rgba.Width);
        Assert.Equal(2, rgba.Height);
        Assert.Equal(new Rgba(0, 0, 0, 255), rgba.GetPixel(0, 0));
        Assert.Equal(new Rgba(64, 64, 64, 255), rgba.GetPixel(1, 0));
        Assert.Equal(new Rgba(128, 128, 128, 255), rgba.GetPixel(0, 1));
        Assert.Equal(new Rgba(255, 255, 255, 255), rgba.GetPixel(1, 1));
    }
}
