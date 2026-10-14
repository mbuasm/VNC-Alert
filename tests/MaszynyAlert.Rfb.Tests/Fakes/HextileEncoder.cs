using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>
/// Prosty koder Hextile (RFC 6143 §7.7.4) dla fałszywego serwera. Dla każdego kafla 16×16 wybiera:
/// <list type="bullet">
/// <item>1 kolor → <c>BackgroundSpecified</c> (2),</item>
/// <item>2 kolory → <c>BackgroundSpecified|ForegroundSpecified|AnySubrects</c> (14),</item>
/// <item>3–4 kolory przy włączonym <c>HextileUseColouredSubrects</c> → <c>BackgroundSpecified|AnySubrects|SubrectsColoured</c> (26),</item>
/// <item>w pozostałych przypadkach → <c>Raw</c> (1).</item>
/// </list>
/// W trybie „sticky background” kolejny kafel o tym samym tle nie ustawia bitu 2 (i nie wysyła
/// piksela tła) — poprawny obraz zobaczy tylko klient, który pamięta tło z poprzedniego kafla.
/// Po kaflu Raw tło jest zawsze podawane jawnie, żeby ominąć niejednoznaczność specyfikacji.
/// </summary>
internal sealed class HextileEncoder
{
    private const byte Raw = 1;
    private const byte BackgroundSpecified = 2;
    private const byte ForegroundSpecified = 4;
    private const byte AnySubrects = 8;
    private const byte SubrectsColoured = 16;
    private const int TileSize = 16;

    private readonly PixelEncoder _pixels;
    private readonly bool _sticky;
    private readonly bool _colouredSubrects;

    public HextileEncoder(PixelEncoder pixels, bool stickyBackground, bool colouredSubrects)
    {
        _pixels = pixels;
        _sticky = stickyBackground;
        _colouredSubrects = colouredSubrects;
    }

    public void Encode(RgbaImage image, RectInt rect, Stream output, Action<byte> onSubencoding)
    {
        Rgba? lastBackground = null;

        for (int ty = rect.Y; ty < rect.Bottom; ty += TileSize)
        {
            int tileH = Math.Min(TileSize, rect.Bottom - ty);
            for (int tx = rect.X; tx < rect.Right; tx += TileSize)
            {
                int tileW = Math.Min(TileSize, rect.Right - tx);
                EncodeTile(image, tx, ty, tileW, tileH, output, ref lastBackground, onSubencoding);
            }
        }
    }

    private void EncodeTile(
        RgbaImage image,
        int tx,
        int ty,
        int tileW,
        int tileH,
        Stream output,
        ref Rgba? lastBackground,
        Action<byte> onSubencoding)
    {
        var counts = new Dictionary<Rgba, int>();
        for (int y = 0; y < tileH; y++)
        {
            for (int x = 0; x < tileW; x++)
            {
                Rgba c = image.GetPixel(tx + x, ty + y);
                counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
            }
        }

        List<Rgba> ordered = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        Rgba background = ordered[0];

        if (ordered.Count == 1)
        {
            byte sub = 0;
            if (!_sticky || lastBackground != background)
            {
                sub |= BackgroundSpecified;
            }

            output.WriteByte(sub);
            onSubencoding(sub);
            if ((sub & BackgroundSpecified) != 0)
            {
                _pixels.WritePixel(background, output);
            }

            lastBackground = background;
            return;
        }

        if (ordered.Count == 2)
        {
            Rgba foreground = ordered[1];
            List<Subrect> subrects = CollectRuns(image, tx, ty, tileW, tileH, c => c == foreground);
            if (subrects.Count <= 255)
            {
                byte sub = ForegroundSpecified | AnySubrects;
                if (!_sticky || lastBackground != background)
                {
                    sub |= BackgroundSpecified;
                }

                output.WriteByte(sub);
                onSubencoding(sub);
                if ((sub & BackgroundSpecified) != 0)
                {
                    _pixels.WritePixel(background, output);
                }

                _pixels.WritePixel(foreground, output);
                output.WriteByte((byte)subrects.Count);
                foreach (Subrect s in subrects)
                {
                    WriteSubrectPosition(s, output);
                }

                lastBackground = background;
                return;
            }
        }

        if (_colouredSubrects && ordered.Count is >= 3 and <= 4)
        {
            List<Subrect> subrects = CollectRuns(image, tx, ty, tileW, tileH, c => c != background);
            if (subrects.Count <= 255)
            {
                byte sub = AnySubrects | SubrectsColoured;
                if (!_sticky || lastBackground != background)
                {
                    sub |= BackgroundSpecified;
                }

                output.WriteByte(sub);
                onSubencoding(sub);
                if ((sub & BackgroundSpecified) != 0)
                {
                    _pixels.WritePixel(background, output);
                }

                output.WriteByte((byte)subrects.Count);
                foreach (Subrect s in subrects)
                {
                    _pixels.WritePixel(s.Color, output);
                    WriteSubrectPosition(s, output);
                }

                lastBackground = background;
                return;
            }
        }

        output.WriteByte(Raw);
        onSubencoding(Raw);
        _pixels.WriteRawRect(image, new RectInt(tx, ty, tileW, tileH), output);

        // Kafel Raw nie ustala tła — następny kafel poda je jawnie (unikamy niejednoznaczności RFC).
        lastBackground = null;
    }

    private static void WriteSubrectPosition(Subrect s, Stream output)
    {
        output.WriteByte((byte)(((s.X & 0x0F) << 4) | (s.Y & 0x0F)));
        output.WriteByte((byte)((((s.Width - 1) & 0x0F) << 4) | ((s.Height - 1) & 0x0F)));
    }

    /// <summary>Zbiera poziome ciągi pikseli spełniających predykat jako podprostokąty 1 piksela wysokości.</summary>
    private static List<Subrect> CollectRuns(RgbaImage image, int tx, int ty, int tileW, int tileH, Func<Rgba, bool> isSubrectPixel)
    {
        var result = new List<Subrect>();
        for (int y = 0; y < tileH; y++)
        {
            int x = 0;
            while (x < tileW)
            {
                Rgba c = image.GetPixel(tx + x, ty + y);
                if (!isSubrectPixel(c))
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < tileW && image.GetPixel(tx + x, ty + y) == c)
                {
                    x++;
                }

                result.Add(new Subrect(start, y, x - start, 1, c));
            }
        }

        return result;
    }

    private readonly record struct Subrect(int X, int Y, int Width, int Height, Rgba Color);
}
