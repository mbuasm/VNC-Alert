using System.Text.Json.Serialization;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Config;

public sealed record IdleWatchProfile
{
    /// <summary>Włącznik funkcji dla tego profilu — <see langword="false"/> = jak brak sekcji <c>idle</c> w ogóle.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Sposób odczytu postępu — <see cref="ProgressMode.Number"/> (domyślny, OCR liczby procent) albo
    /// <see cref="ProgressMode.Bar"/> (v2, kolor wypełnienia paska, bez OCR).
    /// </summary>
    public ProgressMode Mode { get; init; } = ProgressMode.Number;

    /// <summary>
    /// Obszar (w pikselach pełnego ekranu) z postępem bieżącej pracy — w trybie <see cref="ProgressMode.Number"/>
    /// sama liczba procent (np. „100%”), z niewielkim zapasem; w trybie <see cref="ProgressMode.Bar"/> pasek postępu.
    /// </summary>
    public RectInt ProgressRoi { get; init; } = RectInt.Empty;

    // --- Tryb number (OCR liczby procent) ----------------------------------------------------------

    /// <summary>
    /// Od jakiej odczytanej wartości procentu uznajemy pracę za „skończoną (100%)” — domyślnie równo 100,
    /// bo w trybie liczbowym (w przeciwieństwie do paska z antyaliasingiem) OCR albo odczyta dokładną
    /// wartość, albo nic (wtedy i tak <c>Percent</c> jest <see langword="null"/>, patrz <see cref="Vision.ProgressNumberReader"/>).
    /// </summary>
    public int FullAtPercent { get; init; } = 100;

    public int OcrScale { get; init; } = 3;

    /// <summary>Biały margines (w pikselach) dodawany dookoła wycinka z liczbą procent przed OCR.</summary>
    public int OcrMarginPx { get; init; } = 8;

    /// <summary>
    /// Czy przed OCR odwrócić jasność wycinka (jasny tekst na ciemnym tle → czarny tekst na białym, czego
    /// oczekuje silnik OCR) — <see langword="null"/> (domyślnie) = zgodnie z <see cref="ScreenProfile.DarkTheme"/>
    /// profilu, w którym jest ta sekcja (jawna wartość pozwala nadpisać to per-profilowe domyślne zachowanie,
    /// gdyby akurat obszar liczby procent miał inny motyw niż reszta ekranu).
    /// </summary>
    public bool? InvertForOcr { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProgressBarSource? ProgressBar { get; init; }

    // --- Tryb bar (v2, kolor wypełnienia, bez zmian) ------------------------------------------------

    /// <summary>Progi HSV koloru wypełnienia paska postępu (typowo zielony, DO DOSTROJENIA) — tylko tryb <see cref="ProgressMode.Bar"/>.</summary>
    public HsvRange ProgressFill { get; init; } = new();

    /// <summary>
    /// Próg wypełnienia paska postępu (0..1), od którego uznajemy pasek za „na 100%” — nie równo 1.0,
    /// bo antyaliasing krawędzi i ewentualny nadrukowany napis „100%” nigdy nie dają idealnych 100%
    /// wypełnionych kolumn. Tylko tryb <see cref="ProgressMode.Bar"/>.
    /// </summary>
    public double FullAtFraction { get; init; } = 0.97;
}
