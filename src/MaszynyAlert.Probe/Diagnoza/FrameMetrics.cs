using System.Security.Cryptography;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>Udział czerwieni policzony progami <c>red</c> profilu <see cref="Profil"/> w obszarze <c>statusBarRoi</c> profilu <see cref="Roi"/>.</summary>
public sealed record RedCell(string Profil, string Roi, double Udzial, bool PowyzejProgu);

public sealed record FrameMeasurement(
    int Width,
    int Height,
    double JasnoscSrednia,
    double UdzialCiemnych,
    string KlasaCzerni,
    IReadOnlyList<RedCell> Czerwien,
    IReadOnlyDictionary<string, double> CzerwienCalaKlatka,
    IReadOnlyDictionary<string, RectInt?> CzerwienBbox,
    IReadOnlyList<double> Odcienie,
    double UdzialBieli,
    double UdzialCzerni,
    string Motyw,
    string Hash)
{
    /// <summary>Prawda dla klatki czarnej albo prawie czarnej (test „ekran gaśnie po połączeniu”).</summary>
    public bool IsBlack => KlasaCzerni is FrameMetrics.KlasaCzarna or FrameMetrics.KlasaPrawieCzarna;

    /// <summary>
    /// Prawda, gdy na klatce jest JAKAKOLWIEK czerwień wg progów któregokolwiek profilu (co najmniej 0,05 % całej klatki
    /// albo próg w którymkolwiek ROI) — taka klatka jest zbierana częściej (co 5 s), bo to kandydat na alarm maszyny bez listy błędów.
    /// </summary>
    public bool JakakolwiekCzerwien => Czerwien.Any(c => c.PowyzejProgu) || CzerwienCalaKlatka.Values.Any(f => f >= FrameMetrics.AnyRedFraction);
}

public static class FrameMetrics
{
    public const string KlasaCzarna = "czarna";
    public const string KlasaPrawieCzarna = "prawie czarna";
    public const string KlasaNormalna = "normalna";

    public const string MotywJasny = "jasny";
    public const string MotywCiemny = "ciemny";
    public const string MotywNieokreslony = "nieokreślony";

    /// <summary>Piksel ciemny: każdy kanał poniżej tej wartości.</summary>
    public const int DarkLevel = 16;

    /// <summary>Udział ciemnych pikseli, od którego klatka jest „czarna” (ekran zgaszony; pojedyncze diody/kursor nie przeszkadzają).</summary>
    public const double BlackFraction = 0.98;

    /// <summary>Udział ciemnych pikseli, od którego klatka jest „prawie czarna” (wygaszacz z jednym napisem, ciemny motyw bez treści).</summary>
    public const double NearBlackFraction = 0.90;

    /// <summary>Udział czerwieni w całej klatce, od którego klatka liczy się jako „z czerwienią” (patrz <see cref="FrameMeasurement.JakakolwiekCzerwien"/>).</summary>
    public const double AnyRedFraction = 0.0005;

    private const int HueBuckets = 12;

    /// <summary>Mierzy klatkę względem wszystkich profili z konfiguracji (macierz profil × ROI liczona dla każdej pary).</summary>
    public static FrameMeasurement Measure(RgbaImage frame, IReadOnlyDictionary<string, ScreenProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(profiles);

        int total = frame.Width * frame.Height;
        byte[] px = frame.Pixels;
        long brightnessSum = 0;
        int dark = 0;
        int white = 0;
        int black = 0;
        var hue = new int[HueBuckets];
        int saturated = 0;

        for (int i = 0; i < px.Length; i += 4)
        {
            byte r = px[i];
            byte g = px[i + 1];
            byte b = px[i + 2];
            int max = Math.Max(r, Math.Max(g, b));
            brightnessSum += max;
            if (max < DarkLevel)
            {
                dark++;
            }

            (double h, double s, double v) = ColorMath.RgbToHsv(r, g, b);
            if (v >= 0.85 && s <= 0.15)
            {
                white++;
            }
            else if (v < 0.12)
            {
                black++;
            }

            if (s >= 0.35 && v >= 0.35)
            {
                int bucket = (int)(h / (360.0 / HueBuckets));
                if (bucket >= HueBuckets)
                {
                    bucket = HueBuckets - 1;
                }

                hue[bucket]++;
                saturated++;
            }
        }

        double darkFraction = total == 0 ? 1.0 : dark / (double)total;
        string klasa = darkFraction >= BlackFraction ? KlasaCzarna : darkFraction >= NearBlackFraction ? KlasaPrawieCzarna : KlasaNormalna;
        double whiteShare = total == 0 ? 0 : white / (double)total;
        double blackShare = total == 0 ? 0 : black / (double)total;
        string motyw = klasa != KlasaNormalna || Math.Abs(whiteShare - blackShare) < 0.05
            ? MotywNieokreslony
            : whiteShare > blackShare ? MotywJasny : MotywCiemny;

        double[] odcienie = hue.Select(c => saturated == 0 ? 0.0 : Math.Round(c / (double)saturated, 4)).ToArray();

        var fullRed = profiles.ToDictionary(
            p => p.Key,
            p => RedTextDetector.Analyze(frame, p.Value.Red, p.Value.MinRedFraction),
            StringComparer.Ordinal);

        return new FrameMeasurement(
            frame.Width,
            frame.Height,
            Math.Round(total == 0 ? 0 : brightnessSum / (double)total, 1),
            Math.Round(darkFraction, 4),
            klasa,
            RedMatrix(frame, profiles),
            fullRed.ToDictionary(p => p.Key, p => Math.Round(p.Value.RedFraction, 5), StringComparer.Ordinal),
            fullRed.ToDictionary(p => p.Key, p => p.Value.RedBounds, StringComparer.Ordinal),
            odcienie,
            Math.Round(whiteShare, 4),
            Math.Round(blackShare, 4),
            motyw,
            Hash(frame));
    }

    /// <summary>
    /// Macierz profil × ROI: dla KAŻDEGO profilu progi <c>red</c> liczone w <c>statusBarRoi</c> KAŻDEGO profilu.
    /// Obszar poza klatką (po przycięciu pusty) daje udział 0 i „poniżej progu” — raport i tak pokaże rozmiar ekranu,
    /// więc widać, że profil nie pasuje.
    /// </summary>
    public static IReadOnlyList<RedCell> RedMatrix(RgbaImage frame, IReadOnlyDictionary<string, ScreenProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(profiles);

        var cells = new List<RedCell>();
        foreach ((string roiId, ScreenProfile roiProfile) in profiles)
        {
            RectInt roi = roiProfile.StatusBarRoi.Clamp(frame.Width, frame.Height);
            RgbaImage? crop = roi.IsEmpty ? null : frame.Crop(roi);
            foreach ((string profileId, ScreenProfile profile) in profiles)
            {
                double fraction = crop is null ? 0.0 : RedTextDetector.Analyze(crop, profile.Red, profile.MinRedFraction).RedFraction;
                cells.Add(new RedCell(profileId, roiId, Math.Round(fraction, 5), fraction >= profile.MinRedFraction && crop is not null));
            }
        }

        return cells;
    }

    /// <summary>Skrót klatki (SHA-256 pikseli, 16 znaków hex) — równe skróty = ten sam obraz co do piksela.</summary>
    public static string Hash(RgbaImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Convert.ToHexString(SHA256.HashData(frame.Pixels))[..16].ToLowerInvariant();
    }
}
