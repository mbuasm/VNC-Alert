using System.Globalization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

public static class ProgressBarReader
{
    public const int RoundedEndColumns = 3;

    /// <summary>
    /// Wymagany udział zgodnych pikseli (≥ 98 %) przy warunkach (a) i (b) — nie 100 %, żeby pojedynczy
    /// zakłócony piksel (kompresja, zrzut okna) nie wyłączał paska; kursor i okna i tak łapie warunek (c).
    /// </summary>
    public const double MinAgreement = 0.98;

    public const double SurroundMaxSaturation = 0.15;

    /// <summary>Ile kolumn rdzenia może być mieszaniną wypełnienia i tła (granica wypełnienia) — patrz warunek (c).</summary>
    public const int MaxMixedColumns = 1;

    /// <summary>
    /// Obszar pełnego ekranu, który trzeba wyciąć dla <see cref="Read"/>: <see cref="ProgressBarSource.TrackRoi"/>
    /// rozszerzony w PIONIE o <see cref="ProgressBarSource.SurroundRows"/> wierszy z obu stron (w poziomie bez
    /// zmian). Pusty tor → <see cref="RectInt.Empty"/>. Obszar nie jest tu przycinany do ekranu: wycinek
    /// przycięty przez <see cref="RgbaImage.Crop"/> będzie miał inne wymiary i <see cref="Read"/> uzna tor za
    /// niewidoczny.
    /// </summary>
    public static RectInt RequiredArea(ProgressBarSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        RectInt track = source.TrackRoi;
        if (track.IsEmpty)
        {
            return RectInt.Empty;
        }

        int rows = Math.Max(0, source.SurroundRows);
        return new RectInt(track.X, track.Y - rows, track.Width, track.Height + (2 * rows));
    }

    /// <summary>
    /// Odczytuje pasek z wycinka <paramref name="area"/>, który ma być DOKŁADNIE obszarem
    /// <see cref="RequiredArea"/> (przyciętym do ekranu — gdy wymiary się nie zgadzają, wynik jest
    /// „tor niewidoczny” z opisem, a nie wyjątek). Wyłączone źródło (<see cref="ProgressBarSource.Enabled"/> =
    /// <see langword="false"/>) też daje „niewidoczny”. Warunki widoczności i model procentu — patrz opis klasy.
    /// </summary>
    public static ProgressBarReading Read(RgbaImage area, ProgressBarSource source)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(source);
        HsvRange fill = source.Fill
            ?? throw new ArgumentException("Źródło paska postępu nie ma progów barwy wypełnienia (fill).", nameof(source));

        RectInt track = source.TrackRoi;
        int width = Math.Max(0, track.Width);
        int height = track.Height;

        if (!source.Enabled)
        {
            return NotVisible(width, "pasek: wyłączony w konfiguracji (progressBar.enabled = false)");
        }

        if (track.IsEmpty || width <= 2 * RoundedEndColumns)
        {
            return NotVisible(width, Why($"obszar toru nieustawiony albo węższy niż {(2 * RoundedEndColumns) + 1} kolumn"));
        }

        RectInt required = RequiredArea(source);
        if (area.Width != required.Width || area.Height != required.Height)
        {
            return NotVisible(width, Why(
                $"wycinek {area.Width}×{area.Height} zamiast {required.Width}×{required.Height} — tor poza ekranem albo zły obszar"));
        }

        byte[] pixels = area.Pixels;
        int stride = area.Stride;
        int rows = Math.Max(0, source.SurroundRows);
        int coreStart = RoundedEndColumns;
        int coreEnd = width - RoundedEndColumns;

        // (a) Otoczenie: obwódka i panel nad torem oraz pod nim — ciemne i szare.
        if (rows > 0)
        {
            double above = DarkFraction(pixels, stride, 0, rows, coreStart, coreEnd, source.SurroundMaxValue);
            if (above < MinAgreement)
            {
                return NotVisible(width, Why($"tło nad torem {Measured(above)} zamiast ≥ {Pct(MinAgreement)}"));
            }

            double below = DarkFraction(pixels, stride, rows + height, (2 * rows) + height, coreStart, coreEnd, source.SurroundMaxValue);
            if (below < MinAgreement)
            {
                return NotVisible(width, Why($"tło pod torem {Measured(below)} zamiast ≥ {Pct(MinAgreement)}"));
            }
        }

        // Klasyfikacja pikseli toru: wypełnienie albo tło toru (albo nic z tych dwóch).
        var fillCount = new int[width];
        var backgroundCount = new int[width];
        for (int y = rows; y < rows + height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowStart + (x * 4);
                var c = new Rgba(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                if (c.A < 128)
                {
                    continue;
                }

                if (ColorMath.InRange(c, fill))
                {
                    fillCount[x]++;
                    continue;
                }

                (_, double s, double v) = ColorMath.RgbToHsv(c.R, c.G, c.B);
                if (s <= source.TrackMaxSaturation && v >= source.TrackMinValue)
                {
                    backgroundCount[x]++;
                }
            }
        }

        // (b) W rdzeniu toru tylko wypełnienie i tło toru.
        long known = 0;
        for (int x = coreStart; x < coreEnd; x++)
        {
            known += fillCount[x] + backgroundCount[x];
        }

        double knownFraction = (double)known / ((long)(coreEnd - coreStart) * height);
        if (knownFraction < MinAgreement)
        {
            return NotVisible(width, Why(
                $"w torze tylko {Measured(knownFraction)} pikseli to wypełnienie albo tło toru, wymagane ≥ {Pct(MinAgreement)}"));
        }

        // (e) Zaokrąglone końce toru na swoim miejscu (przesunięcie o 1 px w poziomie wpuszcza w nie obwódkę).
        for (int k = 0; k < 2 * RoundedEndColumns; k++)
        {
            int x = k < RoundedEndColumns ? k : width - (2 * RoundedEndColumns) + k;
            double endFraction = (double)(fillCount[x] + backgroundCount[x]) / height;
            if (endFraction < source.ColumnFillMinFraction)
            {
                return NotVisible(width, Why(
                    $"koniec toru nie na miejscu: kolumna {x + 1}/{width} ma {Measured(endFraction)} pikseli wypełnienia albo tła, wymagane ≥ {Pct(source.ColumnFillMinFraction)}"));
            }
        }

        // (c) Kolumny rdzenia czyste: w całości wypełnienie albo w całości tło.
        int mixed = 0;
        for (int x = coreStart; x < coreEnd; x++)
        {
            if (fillCount[x] != height && backgroundCount[x] != height)
            {
                mixed++;
            }
        }

        if (mixed > MaxMixedColumns)
        {
            return NotVisible(width, Why($"{mixed} kolumn toru to mieszanina wypełnienia i tła, dopuszczalna {MaxMixedColumns}"));
        }

        // (d) Wypełnione kolumny (cały tor) tworzą ciągły ciąg od lewej krawędzi.
        int filled = 0;
        int prefix = 0;
        bool inPrefix = true;
        for (int x = 0; x < width; x++)
        {
            bool isFilled = (double)fillCount[x] / height >= source.ColumnFillMinFraction;
            if (isFilled)
            {
                filled++;
            }

            if (inPrefix && isFilled)
            {
                prefix++;
            }
            else
            {
                inPrefix = false;
            }
        }

        if (prefix != filled)
        {
            return NotVisible(width, Why($"wypełnienie nie jest ciągłe od lewej: {filled} kol., z tego ciągłych od lewej {prefix}"));
        }

        int percent = PercentFromColumns(filled, width, source.ColumnsPerPercent, source.ColumnsOffset);
        return new ProgressBarReading(true, filled, width, percent, $"pasek {filled}/{width} kol. → {percent} %");
    }

    public static int PercentFromColumns(int filledColumns, int trackWidth, double columnsPerPercent, double columnsOffset)
    {
        if (trackWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackWidth), trackWidth, "Szerokość toru paska musi być dodatnia.");
        }

        if (filledColumns <= 0)
        {
            return 0;
        }

        if (filledColumns >= trackWidth)
        {
            return 100;
        }

        double perPercent = double.IsFinite(columnsPerPercent) && columnsPerPercent > 0 ? columnsPerPercent : trackWidth / 100.0;
        double offset = double.IsFinite(columnsOffset) ? columnsOffset : 0;
        double rounded = Math.Round((filledColumns + offset) / perPercent, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(rounded, 1, 99);
    }

    /// <summary>Udział pikseli ciemnych i szarych (otoczenie toru) w wierszach [<paramref name="y0"/>, <paramref name="y1"/>) i kolumnach [<paramref name="x0"/>, <paramref name="x1"/>).</summary>
    private static double DarkFraction(byte[] pixels, int stride, int y0, int y1, int x0, int x1, double maxValue)
    {
        int dark = 0;
        int total = 0;
        for (int y = y0; y < y1; y++)
        {
            int rowStart = y * stride;
            for (int x = x0; x < x1; x++)
            {
                int i = rowStart + (x * 4);
                total++;
                if (pixels[i + 3] < 128)
                {
                    continue;
                }

                (_, double s, double v) = ColorMath.RgbToHsv(pixels[i], pixels[i + 1], pixels[i + 2]);
                if (v <= maxValue && s <= SurroundMaxSaturation)
                {
                    dark++;
                }
            }
        }

        return total == 0 ? 1.0 : (double)dark / total;
    }

    private static ProgressBarReading NotVisible(int trackWidth, string description) =>
        new(false, 0, trackWidth, null, description);

    private static string Why(string reason) => $"pasek: tor niewidoczny ({reason})";

    /// <summary>Próg 0..1 jako procent po polsku z przecinkiem, np. „98 %”, „50 %”.</summary>
    private static string Pct(double fraction) =>
        (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " %";

    /// <summary>
    /// ZMIERZONY udział 0..1 jako procent po polsku z jednym miejscem po przecinku, np. „94,8 %” — obcięty
    /// w dół, a nie zaokrąglony, żeby opis nigdy nie pokazał „98,0 % zamiast ≥ 98 %” dla 97,96 %.
    /// </summary>
    private static string Measured(double fraction) =>
        (Math.Floor((fraction * 1000) + 1e-9) / 10).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " %";
}
