using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Wykrywa czerwony tekst błędu na pasku statusu HMI maszyny, licząc udział pikseli spełniających
/// progi koloru z profilu ekranu. Celowo działa bezpośrednio na tablicy bajtów obrazu (bez
/// <see cref="RgbaImage.GetPixel"/> per piksel), bo jest wywoływane co sekundę dla każdej z 12 maszyn.
/// </summary>
public sealed class RedTextDetector
{
    private readonly ScreenProfile _profile;

    public RedTextDetector(ScreenProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    /// <summary>Analizuje obraz, który jest już wycięty do obszaru paska statusu (ROI).</summary>
    public RedDetectionResult Analyze(RgbaImage roiImage) => Analyze(roiImage, _profile.Red, _profile.MinRedFraction);

    /// <summary>Wycina obszar paska statusu (<see cref="ScreenProfile.StatusBarRoi"/>, przycięty do granic klatki) i analizuje go.</summary>
    public RedDetectionResult AnalyzeFullFrame(RgbaImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        RectInt roi = _profile.StatusBarRoi.Clamp(frame.Width, frame.Height);
        if (roi.IsEmpty)
        {
            return new RedDetectionResult(0, 0, 0.0, false, null);
        }

        RgbaImage cropped = frame.Crop(roi);
        return Analyze(cropped);
    }

    /// <summary>
    /// Rdzeń detekcji: liczy czerwone piksele obrazu <paramref name="img"/> wg progów <paramref name="th"/>
    /// i porównuje udział do <paramref name="minFraction"/>.
    /// </summary>
    public static RedDetectionResult Analyze(RgbaImage img, RedThresholds th, double minFraction)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(th);

        int width = img.Width;
        int height = img.Height;
        int total = width * height;
        byte[] pixels = img.Pixels;
        int stride = img.Stride;

        int redCount = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowStart + x * 4;
                var c = new Rgba(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                if (ColorMath.IsRed(c, th))
                {
                    redCount++;
                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }
        }

        double fraction = total == 0 ? 0.0 : (double)redCount / total;
        bool isRed = fraction >= minFraction;
        RectInt? bounds = redCount == 0 ? null : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);

        return new RedDetectionResult(redCount, total, fraction, isRed, bounds);
    }
}
