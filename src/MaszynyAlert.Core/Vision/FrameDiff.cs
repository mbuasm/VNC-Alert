using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

public static class FrameDiff
{
    /// <summary>
    /// Udział pikseli (0..1), w których którykolwiek kanał R/G/B różni się między <paramref name="a"/> i
    /// <paramref name="b"/> o więcej niż <paramref name="perChannelTolerance"/> (kanał A jest ignorowany —
    /// przezroczystość nie występuje w zrzutach ekranu maszyn). Różne rozmiary obrazów → <c>1.0</c>
    /// (traktujemy to jak „całkowicie inny obraz”, bez próby wyrównania/przycięcia). Oba obrazy PUSTE
    /// (0 pikseli) → <c>0.0</c> (nie ma czego porównać — to nie jest „zmiana”).
    /// </summary>
    public static double ChangedFraction(RgbaImage a, RgbaImage b, int perChannelTolerance = 24)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Width != b.Width || a.Height != b.Height)
        {
            return 1.0;
        }

        int pixelCount = a.Width * a.Height;
        if (pixelCount == 0)
        {
            return 0.0;
        }

        byte[] pa = a.Pixels;
        byte[] pb = b.Pixels;
        int changed = 0;

        for (int i = 0; i < pa.Length; i += 4)
        {
            int dr = pa[i] - pb[i];
            int dg = pa[i + 1] - pb[i + 1];
            int db = pa[i + 2] - pb[i + 2];
            if (Math.Abs(dr) > perChannelTolerance || Math.Abs(dg) > perChannelTolerance || Math.Abs(db) > perChannelTolerance)
            {
                changed++;
            }
        }

        return (double)changed / pixelCount;
    }
}
