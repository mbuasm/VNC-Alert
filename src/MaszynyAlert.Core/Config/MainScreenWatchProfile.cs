using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Config;

public sealed record MainScreenWatchProfile
{
    public static IReadOnlyList<IReadOnlyList<string>> DefaultAnchorLabels { get; } =
    [
        ["Milling", "Status"],
        ["Milling", "Status:"],
        ["MillingStatus"],
    ];

    /// <summary>Włącznik funkcji dla tego profilu — <see langword="false"/> = jak brak sekcji <c>mainScreen</c> w ogóle.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Warianty etykiety, po której rozpoznajemy ekran główny — lista list słów (każdy wariant to kolejne
    /// słowa w jednej linii, dopasowywane rozmyto jak w <c>Vision.LabelAnchorLocator.FindAny</c>).
    /// Wystarczy, że KTÓRYKOLWIEK wariant zostanie znaleziony. Pusta lista w pliku = wartości domyślne
    /// (<see cref="DefaultAnchorLabels"/>) — patrz <see cref="EffectiveAnchorLabels"/>.
    /// </summary>
    public List<List<string>> AnchorLabels { get; init; } = new();

    /// <summary>
    /// Maksymalna odległość Levenshteina przy rozmytym dopasowaniu pojedynczego słowa etykiety (OCR
    /// bywa niedokładny, np. „Milllng”). Domyślnie 2.
    /// </summary>
    public int MaxDistance { get; init; } = 2;

    /// <summary>
    /// Czy przy braku etykiety na klatce w skali 1:1 spróbować jeszcze raz na klatce powiększonej ×2
    /// (mała czcionka HMI bywa nieczytelna dla OCR w oryginalnej skali — tak samo robi <c>probe.exe</c>).
    /// Kosztuje drugi OCR pełnej klatki, ale TYLKO gdy etykiety nie znaleziono, czyli zwykle właśnie
    /// wtedy, gdy HMI pokazuje inny ekran. Domyślnie włączone.
    /// </summary>
    public bool RetryScaled { get; init; } = true;

    public List<RectInt> ContextRoi { get; init; } = new();

    public bool StatusBarAlwaysVisible { get; init; }

    public List<List<string>> OtherScreenLabels { get; init; } = new();

    /// <summary>Warianty etykiety faktycznie używane: <see cref="AnchorLabels"/>, a gdy puste — <see cref="DefaultAnchorLabels"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> EffectiveAnchorLabels =>
        AnchorLabels.Count == 0 ? DefaultAnchorLabels : AnchorLabels;
}
