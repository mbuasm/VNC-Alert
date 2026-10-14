using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Config;

public sealed record ProgressBarSource
{
    public bool Enabled { get; init; } = true;

    public RectInt TrackRoi { get; init; } = RectInt.Empty;

    public HsvRange Fill { get; init; } = new();

    public double TrackMaxSaturation { get; init; } = 0.10;

    /// <summary>
    /// Najniższa jasność (0..1) piksela NIEWYPEŁNIONEGO toru. Tło toru ma pionowy gradient V 0,45–0,89,
    /// a obwódka i panel wokół niego są ciemne (V ≤ 0,32) — próg 0,40 rozdziela jedno od drugiego, więc
    /// przesunięcie obrazu o 1 piksel w pionie wpuszcza obwódkę do toru i tor przestaje być „widoczny”.
    /// </summary>
    public double TrackMinValue { get; init; } = 0.40;

    public double SurroundMaxValue { get; init; } = 0.35;

    public int SurroundRows { get; init; } = 5;

    public double ColumnFillMinFraction { get; init; } = 0.5;

    public double ColumnsPerPercent { get; init; } = 0;

    public double ColumnsOffset { get; init; } = 0;
}
