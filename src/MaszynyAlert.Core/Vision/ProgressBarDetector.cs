using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

public sealed class ProgressBarDetector
{
    /// <summary>
    /// Próg wypełnienia POJEDYNCZEJ KOLUMNY (0..1) w <see cref="FilledFraction"/>: kolumna liczy się jako
    /// „wypełniona”, gdy co najmniej tyle jej pikseli pasuje do koloru wypełnienia. Liczymy kolumnami
    /// (zamiast całym obszarem naraz), bo nad paskiem postępu bywa nadrukowany tekst procentowy ("95%"),
    /// który psułby liczenie pikselami — kolumna z tekstem, ale też z wypełnieniem, nadal ma większość
    /// pikseli koloru wypełnienia i zalicza się jako wypełniona.
    /// </summary>
    private const double ColumnFillMinFraction = 0.3;

    private readonly IdleWatchProfile _profile;

    public ProgressBarDetector(IdleWatchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    /// <summary>
    /// Analizuje jeden cykl: <paramref name="progressRoiImage"/> to wycinek paska postępu bieżącej pracy
    /// (już wycięty do <c>IdleWatchProfile.ProgressRoi</c>). Obraz „pusty” (szerokość lub wysokość 0 — np.
    /// ROI całkowicie ucięty przez <c>Clamp</c>) daje zachowawczo <c>FilledFraction=0</c>, <c>IsFull=false</c>
    /// (brak alertu), niezależnie od progów profilu.
    /// </summary>
    public ProgressDetectionResult Analyze(RgbaImage progressRoiImage)
    {
        ArgumentNullException.ThrowIfNull(progressRoiImage);

        bool empty = progressRoiImage.Width == 0 || progressRoiImage.Height == 0;
        double fraction = empty ? 0.0 : FilledFraction(progressRoiImage, _profile.ProgressFill);
        bool isFull = !empty && fraction >= _profile.FullAtFraction;

        return new ProgressDetectionResult(fraction, isFull);
    }

    /// <summary>
    /// Udział KOLUMN paska postępu wypełnionych kolorem <paramref name="fill"/> (0..1) — patrz
    /// <see cref="ColumnFillMinFraction"/>. Obraz o zerowej szerokości/wysokości daje 0.
    /// </summary>
    public static double FilledFraction(RgbaImage img, HsvRange fill)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(fill);

        int width = img.Width;
        int height = img.Height;
        if (width == 0 || height == 0)
        {
            return 0.0;
        }

        byte[] pixels = img.Pixels;
        int stride = img.Stride;
        int filledColumns = 0;

        for (int x = 0; x < width; x++)
        {
            int colOffset = x * 4;
            int matched = 0;
            for (int y = 0; y < height; y++)
            {
                int i = (y * stride) + colOffset;
                var c = new Rgba(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                if (ColorMath.InRange(c, fill))
                {
                    matched++;
                }
            }

            if ((double)matched / height >= ColumnFillMinFraction)
            {
                filledColumns++;
            }
        }

        return (double)filledColumns / width;
    }
}
