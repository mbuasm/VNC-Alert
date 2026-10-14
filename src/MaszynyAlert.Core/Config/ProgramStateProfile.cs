using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Config;

public sealed record ProgramStateProfile
{
    /// <summary>Włącznik funkcji dla tego profilu — <see langword="false"/> = jak brak sekcji <c>programState</c> w ogóle.</summary>
    public bool Enabled { get; init; } = true;

    public RectInt BoxRoi { get; init; } = RectInt.Empty;

    public HsvRange RunFill { get; init; } = new() { HueMinDeg = 100, HueMaxDeg = 160, MinSaturation = 0.5, MinValue = 0.4 };

    /// <summary>
    /// Udział pikseli pudełka w <see cref="RunFill"/> (0..1], od którego uznajemy stan za RUN. Domyślnie
    /// 0,5 — w połowie między zmierzonymi 0,89–0,90 (RUN) a 0,00 (IDLE).
    /// </summary>
    public double RunMinFraction { get; init; } = 0.5;

    /// <summary>
    /// Słowa, których odczyt OCR w pudełku stanu oznacza przerwany program. Porównanie po normalizacji
    /// (polskie znaki → ASCII, małe litery, tylko litery a–z; cyfry wypadają) z tolerancją
    /// <see cref="AbortMaxDistance"/> — szczegóły w <see cref="Vision.ProgramStateDetector.HasAbortWord"/>.
    /// </summary>
    public IReadOnlyList<string> AbortWords { get; init; } = ["abort"];

    /// <summary>
    /// Największa odległość edycyjna (Levenshteina) słowa OCR od <see cref="AbortWords"/> po normalizacji,
    /// dozwolone 0..2. Domyślnie 1: „Ab0rt”, „Abołt”, „Abort.” pasują, a „Normal”, „Stop”, „IDLE”, „RUN”
    /// i „Continue” nie.
    /// </summary>
    public int AbortMaxDistance { get; init; } = 1;

    /// <summary>
    /// Wzorzec pikseli napisu „IDLE: Abort” — drugi, niezależny od OCR sposób rozpoznania przerwanego
    /// programu. <see langword="null"/> = bez wzorca (tylko OCR).
    /// </summary>
    public AbortPattern? AbortPattern { get; init; }
}

public sealed record AbortPattern
{
    /// <summary>Lewa krawędź wzorca we współrzędnych PEŁNEGO EKRANU (wzorzec musi leżeć w <see cref="ProgramStateProfile.BoxRoi"/>).</summary>
    public int X { get; init; }

    /// <summary>Górna krawędź wzorca we współrzędnych PEŁNEGO EKRANU (wzorzec musi leżeć w <see cref="ProgramStateProfile.BoxRoi"/>).</summary>
    public int Y { get; init; }

    /// <summary>
    /// Wiersze maski, od góry: wszystkie tej samej długości, tylko znaki <c>'#'</c> i <c>'.'</c>. Szerokość
    /// wzorca = długość wiersza, wysokość = liczba wierszy.
    /// </summary>
    public IReadOnlyList<string> Rows { get; init; } = [];

    public int WhiteMinChannel { get; init; } = 150;

    /// <summary>
    /// Największa liczba niezgodnych komórek wzorca (przy najlepszym przesunięciu), przy której napis
    /// uznajemy za zgodny. Domyślnie 20 — zmierzone: 0 dla „IDLE: Abort”, co najmniej 244 dla innych napisów.
    /// </summary>
    public int MaxMismatch { get; init; } = 20;

    /// <summary>
    /// Tolerancja przesunięcia w pikselach (w poziomie i w pionie, dozwolone 0..3): porównanie sprawdza
    /// wszystkie przesunięcia od −MaxShift do +MaxShift i bierze najmniejszą liczbę niezgodności.
    /// Domyślnie 1 — na zmierzonych klatkach napis stoi zawsze w tym samym miejscu, zapas jest na
    /// wypadek innego zaokrąglenia przy wyśrodkowaniu.
    /// </summary>
    public int MaxShift { get; init; } = 1;
}
