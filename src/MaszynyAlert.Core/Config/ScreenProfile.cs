using System.Text.Json.Serialization;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Config;

/// <summary>
/// Progi HSV określające, kiedy piksel jest uznawany za "czerwony" (błąd). Zakres barwy (Hue) jest
/// liczony "przez zero" — piksel jest czerwony, gdy (H &gt;= HueMinDeg lub H &lt;= HueMaxDeg) oraz
/// S &gt;= MinSaturation oraz V &gt;= MinValue.
/// </summary>
public sealed record RedThresholds
{
    /// <summary>Dolna granica zakresu barwy w stopniach (góra koła barw, blisko 360°).</summary>
    public double HueMinDeg { get; init; } = 340;

    /// <summary>Górna granica zakresu barwy w stopniach (dół koła barw, blisko 0°).</summary>
    public double HueMaxDeg { get; init; } = 20;

    /// <summary>Minimalne nasycenie (0..1), poniżej którego kolor jest uznawany za wyblakły/szary.</summary>
    public double MinSaturation { get; init; } = 0.55;

    /// <summary>Minimalna jasność/wartość (0..1), poniżej której kolor jest uznawany za zbyt ciemny.</summary>
    public double MinValue { get; init; } = 0.35;
}

/// <summary>
/// Profil ekranu — opisuje wygląd HMI jednego typu maszyny (położenie paska statusu, progi koloru
/// czerwieni, ustawienia OCR). Kilka maszyn o tym samym układzie ekranu współdzieli jeden profil.
/// </summary>
public sealed record ScreenProfile
{
    public string Id { get; init; } = "";

    /// <summary>Opis dla administratora, np. nazwa układu HMI ("jasny motyw, pasek u góry").</summary>
    public string? Description { get; init; }

    /// <summary>Obszar (w pikselach pełnego ekranu) paska statusu, w którym szukamy czerwonego tekstu.</summary>
    public RectInt StatusBarRoi { get; init; } = RectInt.Empty;

    /// <summary>Progi HSV wykrywania czerwieni dla tego profilu.</summary>
    public RedThresholds Red { get; init; } = new();

    /// <summary>Minimalny udział czerwonych pikseli w ROI, żeby uznać stan za błąd (np. 0.005 = 0,5%).</summary>
    public double MinRedFraction { get; init; } = 0.005;

    /// <summary>Margines (w pikselach) dodawany dookoła wykrytego tekstu przed przekazaniem do OCR.</summary>
    public int OcrMarginPx { get; init; } = 20;

    public int OcrScale { get; init; } = 2;

    /// <summary>Czy HMI tej maszyny ma ciemny motyw (wpływa m.in. na dobór progów/diagnostykę).</summary>
    public bool DarkTheme { get; init; } = false;

    public IdleWatchProfile? Idle { get; init; }

    public MainScreenWatchProfile? MainScreen { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProgramStateProfile? ProgramState { get; init; }
}
