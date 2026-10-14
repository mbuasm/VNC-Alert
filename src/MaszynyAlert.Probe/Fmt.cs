using System.Globalization;

namespace MaszynyAlert.Probe;

/// <summary>Wspólne formatowanie liczb w komunikatach PL (przecinek jako separator dziesiętny, niezależnie od ustawień systemu).</summary>
internal static class Fmt
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>Udział (0..1) jako procent z jednym miejscem po przecinku, np. "1,2 %".</summary>
    public static string Pct1(double fraction) => $"{(fraction * 100).ToString("0.0", Pl)} %";

    /// <summary>Udział (0..1) jako procent z dwoma miejscami po przecinku, np. "0,12 %".</summary>
    public static string Pct2(double fraction) => $"{(fraction * 100).ToString("0.00", Pl)} %";

    /// <summary>Czas w milisekundach, np. "123 ms".</summary>
    public static string Ms(long milliseconds) => $"{milliseconds} ms";
}
