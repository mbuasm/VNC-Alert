using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>
/// Generator SYNTETYCZNYCH obrazów paska statusu HMI. „Tekst” to ciąg prostokątnych pseudo-glifów
/// 6×10 px z odstępami, rysowanych deterministycznie na podstawie ziarna (<c>seed</c>), o zadanej
/// pokrywalności ROI. Wokół każdego zapalonego piksela dorysowywana jest 1-pikselowa obwódka
/// w kolorze pośrednim tło↔tekst — imitacja antyaliasingu prawdziwego renderowania czcionki.
/// </summary>
public static class SyntheticScreens
{
    /// <summary>Szerokość pojedynczego pseudo-glifu w pikselach.</summary>
    public const int GlyphWidth = 6;

    /// <summary>Wysokość pojedynczego pseudo-glifu w pikselach.</summary>
    public const int GlyphHeight = 10;

    /// <summary>Odstęp między pseudo-glifami w pikselach.</summary>
    public const int GlyphGap = 3;

    /// <summary>Typowe kolory używane przez próbki i testy.</summary>
    public static readonly Rgba LightBackground = new(235, 235, 235);
    public static readonly Rgba Light2Background = new(245, 245, 245);
    public static readonly Rgba DarkBackground = new(30, 30, 40);
    public static readonly Rgba WhiteText = new(255, 255, 255);
    public static readonly Rgba BlackText = new(20, 20, 20);
    public static readonly Rgba YellowText = new(240, 200, 0);
    public static readonly Rgba AmberText = new(250, 180, 20);
    public static readonly Rgba RedText = new(220, 30, 30);
    public static readonly Rgba DarkRedText = new(180, 20, 20);

    /// <summary>
    /// Rysuje pasek statusu <paramref name="w"/>×<paramref name="h"/> w kolorze <paramref name="background"/>
    /// z „tekstem” w kolorze <paramref name="textColor"/>. Liczba zapalonych pikseli tekstu jest DOKŁADNIE
    /// równa <c>round(coverage * w * h)</c> (o ile mieści się na pasku), dzięki czemu udział czerwieni
    /// w teście jest przewidywalny. <paramref name="seed"/> = <see langword="null"/> albo pusty → pusty pasek.
    /// </summary>
    public static RgbaImage StatusBar(int w, int h, Rgba background, Rgba textColor, string? seed, double coverage = 0.02)
    {
        var img = new RgbaImage(w, h);
        img.Fill(background);

        if (string.IsNullOrEmpty(seed) || coverage <= 0)
        {
            return img;
        }

        int targetInk = (int)Math.Round(coverage * w * h);
        if (targetInk <= 0)
        {
            return img;
        }

        List<(int X, int Y)> ink = InkPixels(w, h, seed, targetInk);
        if (ink.Count == 0)
        {
            return img;
        }

        var inkSet = new HashSet<(int, int)>(ink);
        Rgba edge = Blend(background, textColor, 0.5);

        // Najpierw obwódka (antyaliasing), potem właściwe piksele tekstu — obwódka nie może ich zamazać.
        foreach ((int x, int y) in ink)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || inkSet.Contains((nx, ny)))
                    {
                        continue;
                    }

                    img.SetPixel(nx, ny, edge);
                }
            }
        }

        foreach ((int x, int y) in ink)
        {
            img.SetPixel(x, y, textColor);
        }

        return img;
    }

    /// <summary>
    /// Osadza obraz paska <paramref name="bar"/> w „pełnej klatce” o rozmiarze wynikającym z ROI profilu
    /// (szerokość = prawa krawędź ROI, wysokość = dolna krawędź ROI + 40 px zapasu poniżej, żeby zawsze
    /// istniał obszar POZA ROI). Reszta klatki jest wypełniona kolorem <paramref name="background"/>.
    /// </summary>
    public static RgbaImage FullFrame(ScreenProfile profile, RgbaImage bar, Rgba? background = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(bar);

        RectInt roi = profile.StatusBarRoi;
        int width = Math.Max(roi.Right, roi.X + bar.Width);
        int height = Math.Max(roi.Bottom, roi.Y + bar.Height) + 40;

        var frame = new RgbaImage(width, height);
        frame.Fill(background ?? (profile.DarkTheme ? DarkBackground : LightBackground));
        frame.Blit(bar, roi.X, roi.Y);
        return frame;
    }

    /// <summary>Mieszanie dwóch kolorów (0 = <paramref name="a"/>, 1 = <paramref name="b"/>).</summary>
    public static Rgba Blend(Rgba a, Rgba b, double t) => new(
        (byte)Math.Round((a.R * (1 - t)) + (b.R * t)),
        (byte)Math.Round((a.G * (1 - t)) + (b.G * t)),
        (byte)Math.Round((a.B * (1 - t)) + (b.B * t)),
        (byte)Math.Round((a.A * (1 - t)) + (b.A * t)));

    /// <summary>
    /// Wylicza deterministyczną listę pikseli „atramentu”: pseudo-glify 6×10 układane w rzędzie
    /// (z zawijaniem do kolejnych wierszy), w każdym glifie ~60 % pikseli zapalonych wg ziarna.
    /// Zwraca dokładnie <paramref name="targetInk"/> pikseli (albo tyle, ile się zmieściło).
    /// </summary>
    private static List<(int X, int Y)> InkPixels(int w, int h, string seed, int targetInk)
    {
        var result = new List<(int X, int Y)>(targetInk);

        int rowHeight = GlyphHeight + 4;
        int rows = Math.Max(1, (h - 4) / rowHeight);
        int columns = Math.Max(1, (w - 8) / (GlyphWidth + GlyphGap));
        uint state = Fnv1a(seed);

        for (int row = 0; row < rows && result.Count < targetInk; row++)
        {
            int glyphY = 2 + (row * rowHeight) + 2;
            if (glyphY + GlyphHeight > h)
            {
                break;
            }

            for (int col = 0; col < columns && result.Count < targetInk; col++)
            {
                int glyphX = 4 + (col * (GlyphWidth + GlyphGap));
                if (glyphX + GlyphWidth > w)
                {
                    break;
                }

                for (int gy = 0; gy < GlyphHeight && result.Count < targetInk; gy++)
                {
                    for (int gx = 0; gx < GlyphWidth && result.Count < targetInk; gx++)
                    {
                        state = NextRandom(state);
                        // ~62 % wypełnienia prostokąta glifu — na tyle gęsto, żeby „litera” była spójna.
                        if (state % 100 < 62)
                        {
                            result.Add((glyphX + gx, glyphY + gy));
                        }
                    }
                }
            }
        }

        return result;
    }

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash == 0 ? 1u : hash;
    }

    /// <summary>Prosty, w pełni deterministyczny generator xorshift32 (nie zależy od implementacji Random w BCL).</summary>
    private static uint NextRandom(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
