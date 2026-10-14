namespace MaszynyAlert.Core.Config;

/// <summary>
/// Progi HSV określające, kiedy piksel jest uznawany za dany kolor — ten sam sens co
/// <see cref="RedThresholds"/> (używane dziś tylko do wykrywania czerwieni błędu), ale ogólniejszy:
/// służy do rozpoznawania KOLORU WYPEŁNIENIA paska postępu (<see cref="IdleWatchProfile.ProgressFill"/>),
/// który zwykle nie jest czerwony (typowo zielony/niebieski). Zakres barwy (Hue) jest liczony
/// "przez zero" tak samo jak w <see cref="RedThresholds"/>: gdy <see cref="HueMinDeg"/> &lt;=
/// <see cref="HueMaxDeg"/>, to zwykły zakres [HueMin, HueMax]; w przeciwnym razie zakres obejmuje
/// H &gt;= HueMin LUB H &lt;= HueMax.
/// </summary>
public sealed record HsvRange
{
    public double HueMinDeg { get; init; } = 80;

    /// <summary>Górna granica zakresu barwy w stopniach — patrz <see cref="HueMinDeg"/>.</summary>
    public double HueMaxDeg { get; init; } = 160;

    /// <summary>Minimalne nasycenie (0..1), poniżej którego kolor jest uznawany za wyblakły/szary.</summary>
    public double MinSaturation { get; init; } = 0.35;

    /// <summary>Minimalna jasność/wartość (0..1), poniżej której kolor jest uznawany za zbyt ciemny.</summary>
    public double MinValue { get; init; } = 0.35;
}
