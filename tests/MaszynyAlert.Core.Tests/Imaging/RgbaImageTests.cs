using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Tests.Imaging;

public sealed class RgbaImageTests
{
    private static RgbaImage Gradient(int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                img.SetPixel(x, y, new Rgba((byte)(x * 7 % 256), (byte)(y * 11 % 256), (byte)((x + y) % 256), 255));
            }
        }

        return img;
    }

    [Fact]
    public void Konstruktor_DlaPodanegoRozmiaru_UstawiaStrideIWielkoscTablicy()
    {
        var img = new RgbaImage(37, 23);
        Assert.Equal(37 * 4, img.Stride);
        Assert.Equal(37 * 23 * 4, img.Pixels.Length);
    }

    [Fact]
    public void Konstruktor_GdyTablicaMaZlyRozmiar_RzucaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new RgbaImage(4, 4, new byte[10]));
    }

    [Fact]
    public void Konstruktor_GdyRozmiarUjemny_RzucaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new RgbaImage(-1, 5));
    }

    [Fact]
    public void SetPixel_IGetPixel_ZapisujaIOdczytujaTenSamKolor()
    {
        var img = new RgbaImage(3, 2);
        var color = new Rgba(1, 2, 3, 4);
        img.SetPixel(2, 1, color);
        Assert.Equal(color, img.GetPixel(2, 1));
        Assert.Equal(new Rgba(0, 0, 0, 0), img.GetPixel(0, 0));
    }

    [Fact]
    public void GetPixel_PozaObrazem_RzucaArgumentOutOfRange()
    {
        var img = new RgbaImage(3, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.GetPixel(3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.GetPixel(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.SetPixel(-1, 0, default));
    }

    [Fact]
    public void Fill_WypelniaWszystkiePikseleJednymKolorem()
    {
        var img = new RgbaImage(5, 4);
        var color = new Rgba(10, 20, 30, 40);
        img.Fill(color);
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                Assert.Equal(color, img.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Crop_DlaProstokataWewnatrzObrazu_ZwracaWlasciwyFragment()
    {
        RgbaImage src = Gradient(10, 8);
        RgbaImage cropped = src.Crop(new RectInt(2, 3, 4, 2));

        Assert.Equal(4, cropped.Width);
        Assert.Equal(2, cropped.Height);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(src.GetPixel(x + 2, y + 3), cropped.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Crop_GdyProstokatWystajePozaObraz_JestPrzycinany()
    {
        RgbaImage src = Gradient(10, 8);
        RgbaImage cropped = src.Crop(new RectInt(8, 6, 100, 100));
        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(src.GetPixel(8, 6), cropped.GetPixel(0, 0));
    }

    [Fact]
    public void Crop_GdyProstokatCalkowiciePozaObrazem_RzucaArgumentException()
    {
        RgbaImage src = Gradient(10, 8);
        Assert.Throws<ArgumentException>(() => src.Crop(new RectInt(50, 50, 10, 10)));
    }

    [Fact]
    public void Crop_GdyProstokatPusty_RzucaArgumentException()
    {
        RgbaImage src = Gradient(10, 8);
        Assert.Throws<ArgumentException>(() => src.Crop(new RectInt(0, 0, 0, 5)));
    }

    [Fact]
    public void Clone_ZwracaNiezaleznaKopie()
    {
        RgbaImage src = Gradient(4, 4);
        RgbaImage copy = src.Clone();
        copy.SetPixel(0, 0, new Rgba(9, 9, 9, 9));

        Assert.NotSame(src.Pixels, copy.Pixels);
        Assert.NotEqual(src.GetPixel(0, 0), copy.GetPixel(0, 0));
    }

    [Fact]
    public void Scale_PowiekszaObrazMetodaNajblizszegoSasiada()
    {
        var src = new RgbaImage(2, 2);
        src.SetPixel(0, 0, new Rgba(1, 1, 1));
        src.SetPixel(1, 0, new Rgba(2, 2, 2));
        src.SetPixel(0, 1, new Rgba(3, 3, 3));
        src.SetPixel(1, 1, new Rgba(4, 4, 4));

        RgbaImage scaled = src.Scale(3);

        Assert.Equal(6, scaled.Width);
        Assert.Equal(6, scaled.Height);
        Assert.Equal(new Rgba(1, 1, 1), scaled.GetPixel(2, 2));
        Assert.Equal(new Rgba(2, 2, 2), scaled.GetPixel(3, 0));
        Assert.Equal(new Rgba(4, 4, 4), scaled.GetPixel(5, 5));
    }

    [Fact]
    public void Scale_WspolczynnikiemJeden_ZwracaKopie()
    {
        RgbaImage src = Gradient(3, 3);
        RgbaImage scaled = src.Scale(1);
        Assert.NotSame(src.Pixels, scaled.Pixels);
        Assert.Equal(src.Pixels, scaled.Pixels);
    }

    [Fact]
    public void Scale_ZeroLubUjemny_RzucaArgumentException()
    {
        RgbaImage src = Gradient(3, 3);
        Assert.Throws<ArgumentException>(() => src.Scale(0));
        Assert.Throws<ArgumentException>(() => src.Scale(-2));
    }

    [Fact]
    public void Blit_KopiujeObrazNaWskazanaPozycje()
    {
        var dst = new RgbaImage(6, 6);
        dst.Fill(new Rgba(0, 0, 0, 255));
        var src = new RgbaImage(2, 2);
        src.Fill(new Rgba(255, 0, 0, 255));

        dst.Blit(src, 4, 4);

        Assert.Equal(new Rgba(255, 0, 0, 255), dst.GetPixel(4, 4));
        Assert.Equal(new Rgba(255, 0, 0, 255), dst.GetPixel(5, 5));
        Assert.Equal(new Rgba(0, 0, 0, 255), dst.GetPixel(3, 3));
    }

    [Fact]
    public void Blit_GdyZrodloWystajePozaCel_KopiujeTylkoCzescWspolna()
    {
        var dst = new RgbaImage(4, 4);
        dst.Fill(new Rgba(0, 0, 0, 255));
        var src = new RgbaImage(4, 4);
        src.Fill(new Rgba(1, 2, 3, 255));

        dst.Blit(src, 3, 3);
        Assert.Equal(new Rgba(1, 2, 3, 255), dst.GetPixel(3, 3));
        Assert.Equal(new Rgba(0, 0, 0, 255), dst.GetPixel(2, 3));

        dst.Blit(src, -3, -3);
        Assert.Equal(new Rgba(1, 2, 3, 255), dst.GetPixel(0, 0));
        Assert.Equal(new Rgba(0, 0, 0, 255), dst.GetPixel(1, 1));
    }

    [Fact]
    public void Blit_GdyCalkowiciePozaCelem_NicNieZmienia()
    {
        var dst = new RgbaImage(4, 4);
        dst.Fill(new Rgba(7, 7, 7, 255));
        var src = new RgbaImage(2, 2);
        src.Fill(new Rgba(1, 1, 1, 255));

        dst.Blit(src, 10, 10);
        Assert.Equal(new Rgba(7, 7, 7, 255), dst.GetPixel(3, 3));
    }

    // --- Downscale: zapis pełnych klatek do captures w skali 1:2 ---

    [Fact]
    public void Downscale_PolowiWymiaryIUsredniaBlokDwaNaDwa()
    {
        var img = new RgbaImage(4, 2);
        img.SetPixel(0, 0, new Rgba(0, 0, 0, 255));
        img.SetPixel(1, 0, new Rgba(100, 0, 0, 255));
        img.SetPixel(0, 1, new Rgba(0, 100, 0, 255));
        img.SetPixel(1, 1, new Rgba(100, 100, 0, 255));
        img.SetPixel(2, 0, new Rgba(200, 200, 200, 255));
        img.SetPixel(3, 0, new Rgba(200, 200, 200, 255));
        img.SetPixel(2, 1, new Rgba(200, 200, 200, 255));
        img.SetPixel(3, 1, new Rgba(200, 200, 200, 255));

        RgbaImage small = img.Downscale(2);

        Assert.Equal(2, small.Width);
        Assert.Equal(1, small.Height);
        Assert.Equal(new Rgba(50, 50, 0, 255), small.GetPixel(0, 0));
        Assert.Equal(new Rgba(200, 200, 200, 255), small.GetPixel(1, 0));
    }

    [Fact]
    public void Downscale_PomijaNiepelneBlokiPrzyKrawedzi()
    {
        var img = new RgbaImage(5, 3);
        img.Fill(new Rgba(10, 20, 30, 255));

        RgbaImage small = img.Downscale(2);

        Assert.Equal(2, small.Width);
        Assert.Equal(1, small.Height);
    }

    [Fact]
    public void Downscale_ObrazMniejszyNizBlok_WracaBezZmian()
    {
        var img = new RgbaImage(1, 1);
        img.Fill(new Rgba(9, 9, 9, 255));

        RgbaImage same = img.Downscale(2);

        Assert.Equal(1, same.Width);
        Assert.Equal(1, same.Height);
        Assert.Equal(new Rgba(9, 9, 9, 255), same.GetPixel(0, 0));
    }

    [Fact]
    public void Downscale_WspolczynnikJedenAlboNiedodatni()
    {
        var img = new RgbaImage(2, 2);
        img.Fill(new Rgba(5, 6, 7, 255));

        Assert.Equal(2, img.Downscale(1).Width);
        Assert.Throws<ArgumentException>(() => img.Downscale(0));
        Assert.Throws<ArgumentException>(() => img.Downscale(-2));
    }
}
