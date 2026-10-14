using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Podstawowe przeliczenia kolorów używane przy wykrywaniu czerwonego tekstu błędu na pasku statusu HMI.
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// Przelicza kolor RGB (0..255 na kanał) na HSV: H w stopniach (0..360), S i V w zakresie 0..1.
    /// Standardowy algorytm (max/min kanałów).
    /// </summary>
    public static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;

        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;

        double h;
        if (delta <= 0.0)
        {
            h = 0.0;
        }
        else if (max == rf)
        {
            h = 60.0 * (((gf - bf) / delta) % 6.0);
        }
        else if (max == gf)
        {
            h = 60.0 * (((bf - rf) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((rf - gf) / delta) + 4.0);
        }

        if (h < 0.0)
        {
            h += 360.0;
        }

        double s = max <= 0.0 ? 0.0 : delta / max;
        double v = max;

        return (h, s, v);
    }

    /// <summary>
    /// Sprawdza, czy dany piksel spełnia progi koloru "czerwony" (błąd) z profilu ekranu.
    /// Zakres barwy jest liczony "przez zero": gdy <see cref="RedThresholds.HueMinDeg"/> &lt;= <see cref="RedThresholds.HueMaxDeg"/>,
    /// to zwykły zakres [HueMin, HueMax]; w przeciwnym razie (typowo dla czerwieni, np. 340..20) zakres
    /// obejmuje H &gt;= HueMin LUB H &lt;= HueMax. Piksele przezroczyste (A &lt; 128) nigdy nie są czerwone.
    /// </summary>
    public static bool IsRed(Rgba c, RedThresholds t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return InRangeCore(c, t.HueMinDeg, t.HueMaxDeg, t.MinSaturation, t.MinValue);
    }

    /// <summary>
    /// Sprawdza, czy dany piksel spełnia progi koloru opisane przez <paramref name="r"/> — ogólniejszy
    /// odpowiednik <see cref="IsRed"/>, używany przez wykrywanie koloru wypełnienia paska postępu
    /// (<c>ProgressBarDetector</c>), gdzie kolor NIE jest czerwienią (typowo zielony). Ten sam sens progów
    /// i ta sama zasada "przez zero" co w <see cref="IsRed"/>; piksele przezroczyste (A &lt; 128) nigdy
    /// nie pasują.
    /// </summary>
    public static bool InRange(Rgba c, HsvRange r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return InRangeCore(c, r.HueMinDeg, r.HueMaxDeg, r.MinSaturation, r.MinValue);
    }

    /// <summary>Wspólny rdzeń <see cref="IsRed"/> i <see cref="InRange"/> — patrz opis progów tam.</summary>
    private static bool InRangeCore(Rgba c, double hueMinDeg, double hueMaxDeg, double minSaturation, double minValue)
    {
        if (c.A < 128)
        {
            return false;
        }

        (double h, double s, double v) = RgbToHsv(c.R, c.G, c.B);

        bool hueMatch = hueMinDeg <= hueMaxDeg
            ? h >= hueMinDeg && h <= hueMaxDeg
            : h >= hueMinDeg || h <= hueMaxDeg;

        return hueMatch && s >= minSaturation && v >= minValue;
    }
}
