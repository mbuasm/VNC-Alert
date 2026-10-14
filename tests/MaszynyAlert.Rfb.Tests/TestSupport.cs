using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Wspólne narzędzia testów: limity czasu, fabryka opcji klienta.</summary>
internal static class TestSupport
{
    /// <summary>Twardy limit czasu pojedynczego testu — żaden test nie może wisieć.</summary>
    public static CancellationTokenSource NewCts() => new(TimeSpan.FromSeconds(10));

    /// <summary>Krótki limit oczekiwania na zdarzenia po stronie fałszywego serwera.</summary>
    public static TimeSpan ShortWait => TimeSpan.FromSeconds(5);

    public static RfbClientOptions ClientOptions(FakeRfbServer server, Action<RfbClientOptions>? configure = null)
    {
        var options = new RfbClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ReadTimeout = TimeSpan.FromSeconds(5),
        };

        configure?.Invoke(options);
        return options;
    }
}

/// <summary>Generatory obrazów testowych („ekranów maszyny”).</summary>
internal static class TestScreens
{
    /// <summary>Gradient/wzór o dużej liczbie kolorów — dobry do testów Raw i kafli Hextile w trybie Raw.</summary>
    public static RgbaImage Gradient(int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y, new Rgba((byte)((x * 5) + (y * 3)), (byte)(x * 7), (byte)(y * 11)));
            }
        }

        return image;
    }

    /// <summary>
    /// Wzór z kolorów odwzorowywalnych dokładnie w RGB565 (kanały to floor(i*255/31) i floor(j*255/63)),
    /// z wtrąconymi kolorami czystymi (czarny, biały, R/G/B) w lewym górnym rogu.
    /// </summary>
    public static RgbaImage Rgb565Friendly(int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x % 32) * 255 / 31);
                byte g = (byte)((y % 64) * 255 / 63);
                byte b = (byte)(((x + y) % 32) * 255 / 31);
                image.SetPixel(x, y, new Rgba(r, g, b));
            }
        }

        Rgba[] pure =
        [
            new Rgba(0, 0, 0), new Rgba(255, 255, 255), new Rgba(255, 0, 0),
            new Rgba(0, 255, 0), new Rgba(0, 0, 255), new Rgba(255, 255, 0),
        ];
        for (int i = 0; i < pure.Length && i < width; i++)
        {
            image.SetPixel(i, 0, pure[i]);
        }

        return image;
    }

    public static RgbaImage Rgb555Friendly(int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x % 32) * 255 / 31);
                byte g = (byte)((y % 32) * 255 / 31);
                byte b = (byte)(((x + y) % 32) * 255 / 31);
                image.SetPixel(x, y, new Rgba(r, g, b));
            }
        }

        Rgba[] pure =
        [
            new Rgba(0, 0, 0), new Rgba(255, 255, 255), new Rgba(255, 0, 0),
            new Rgba(0, 255, 0), new Rgba(0, 0, 255), new Rgba(255, 255, 0),
        ];
        for (int i = 0; i < pure.Length && i < width; i++)
        {
            image.SetPixel(i, 0, pure[i]);
        }

        return image;
    }

    /// <summary>
    /// Ekran 40×40 (kafle brzegowe 8 pikseli) zaprojektowany tak, by koder Hextile użył wszystkich
    /// wariantów: kafel Raw (dużo kolorów), kafel jednokolorowy (samo tło) i kafle tło+pierwszy plan
    /// z podprostokątami.
    /// </summary>
    public static RgbaImage HextileMixed()
    {
        var background = new Rgba(20, 40, 60);
        var foreground = new Rgba(220, 30, 30);
        var image = new RgbaImage(40, 40);
        image.Fill(background);

        // Kafel (0,0): gradient → koder wybierze Raw.
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                image.SetPixel(x, y, new Rgba((byte)(x * 16), (byte)(y * 16), (byte)((x + y) * 8)));
            }
        }

        // Kafel (16,0): pozostaje jednokolorowy (samo tło).

        // Kafel (32,0), szerokość 8: tło + podprostokąty pierwszego planu.
        for (int y = 2; y < 10; y++)
        {
            for (int x = 33; x < 38; x++)
            {
                image.SetPixel(x, y, foreground);
            }
        }

        // Kafel (0,16): pojedyncze piksele pierwszego planu (wiele krótkich podprostokątów).
        for (int y = 16; y < 32; y += 2)
        {
            for (int x = 0; x < 16; x += 2)
            {
                image.SetPixel(x, y, foreground);
            }
        }

        // Kafel (16,16): pasek pierwszego planu.
        for (int y = 20; y < 28; y++)
        {
            for (int x = 18; x < 30; x++)
            {
                image.SetPixel(x, y, foreground);
            }
        }

        // Dolny pas kafli (y = 32..39): tło + mały prostokąt.
        for (int y = 34; y < 38; y++)
        {
            for (int x = 4; x < 12; x++)
            {
                image.SetPixel(x, y, foreground);
            }
        }

        return image;
    }

    /// <summary>Ekran, którego prawa połowa jest dokładną kopią lewej — do testu CopyRect.</summary>
    public static RgbaImage MirroredHalves(int width, int height)
    {
        var image = new RgbaImage(width, height);
        int half = width / 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < half; x++)
            {
                var color = new Rgba((byte)(x * 4), (byte)(y * 6), (byte)((x * y) % 256));
                image.SetPixel(x, y, color);
                image.SetPixel(half + x, y, color);
            }
        }

        return image;
    }

    /// <summary>Ekran zbudowany wyłącznie z kolorów podanej palety (tryb 8 bpp indeksowany).</summary>
    public static RgbaImage FromPalette(int width, int height, IReadOnlyList<Rgba> palette)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y, palette[((x / 3) + (y / 2)) % palette.Count]);
            }
        }

        return image;
    }
}

/// <summary>Asercje porównujące obrazy piksel po pikselu z czytelnym komunikatem o pierwszej różnicy.</summary>
internal static class ImageAssert
{
    public static void Equal(RgbaImage expected, RgbaImage actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba e = expected.GetPixel(x, y);
                Rgba a = actual.GetPixel(x, y);
                if (e != a)
                {
                    Assert.Fail($"Piksel ({x},{y}) różni się: oczekiwano {e}, otrzymano {a}.");
                }
            }
        }
    }

    public static void Close(RgbaImage expected, RgbaImage actual, int tolerance)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba e = expected.GetPixel(x, y);
                Rgba a = actual.GetPixel(x, y);
                if (Math.Abs(e.R - a.R) > tolerance || Math.Abs(e.G - a.G) > tolerance ||
                    Math.Abs(e.B - a.B) > tolerance || e.A != a.A)
                {
                    Assert.Fail($"Piksel ({x},{y}) poza tolerancją {tolerance}: oczekiwano {e}, otrzymano {a}.");
                }
            }
        }
    }

    /// <summary>Porównuje obraz z wycinkiem <paramref name="roi"/> obrazu źródłowego.</summary>
    public static void EqualToRegion(RgbaImage source, RectInt roi, RgbaImage actual) =>
        Equal(source.Crop(roi), actual);
}
