using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

public readonly record struct RedField(RectInt Bounds, Rgba Color, int FieldPixels, double TextContrast)
{
    /// <summary>Czy w polu jest coś do przeczytania (<see cref="TextContrast"/> ≥ <see cref="RedFieldLocator.MinTextContrast"/>).</summary>
    public bool HasText => TextContrast >= RedFieldLocator.MinTextContrast;
}

public static class RedFieldLocator
{
    public const int MinFieldHeight = 8;

    /// <summary>Najmniejsza szerokość obwiedni pola w pikselach wycinka — pojedyncza czerwona litera jest węższa.</summary>
    public const int MinFieldWidth = 16;

    public const double MinFillRatio = 0.6;

    public const double MinTextContrast = 64;

    /// <summary>
    /// Ile wysokości wyższego pola musi się pokrywać w pionie, żeby dwa pola były w TYM SAMYM wierszu
    /// (<see cref="GroupIntoLines"/>): „STOPPED” i komunikat obok siebie — tak (te same wiersze pikseli); czerwone
    /// pole 20 px obok żółtej plakietki „EDIT” 48 px — nie (pokrycie 20 z 48).
    /// </summary>
    public const double SameLineOverlap = 0.6;

    /// <summary>
    /// Znajduje pola (patrz opis klasy) na wycinku <paramref name="roi"/> w oknie barwy <paramref name="red"/>.
    /// Wynik jest uporządkowany od góry, a przy tej samej górnej krawędzi od lewej. Pusty wycinek → pusta lista.
    /// Pola bez napisu (<see cref="RedField.HasText"/> = <see langword="false"/>) też są zwracane — o tym, czy je
    /// czytać, decyduje wołający.
    /// </summary>
    public static IReadOnlyList<RedField> Find(RgbaImage roi, RedThresholds red)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ArgumentNullException.ThrowIfNull(red);

        int width = roi.Width;
        int height = roi.Height;
        if (width == 0 || height == 0)
        {
            return [];
        }

        byte[] pixels = roi.Pixels;
        int stride = roi.Stride;
        int count = width * height;

        // 0 = piksel spoza okna barwy; -1 = w oknie, jeszcze bez etykiety; >0 = numer obszaru.
        var labels = new int[count];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowStart + (x * 4);
                var c = new Rgba(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                if (ColorMath.IsRed(c, red))
                {
                    labels[(y * width) + x] = -1;
                }
            }
        }

        var candidates = new List<(int Label, RectInt Bounds, int Pixels)>();
        var stack = new int[count];
        int next = 0;
        for (int start = 0; start < count; start++)
        {
            if (labels[start] != -1)
            {
                continue;
            }

            next++;
            int top = 0;
            stack[top++] = start;
            labels[start] = next;
            int area = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            while (top > 0)
            {
                int p = stack[--top];
                int px = p % width;
                int py = p / width;
                area++;
                minX = Math.Min(minX, px);
                maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py);
                maxY = Math.Max(maxY, py);

                // Sąsiedztwo 4: po skosie pole i litera mogłyby się „skleić” przez róg piksela.
                if (px > 0 && labels[p - 1] == -1)
                {
                    labels[p - 1] = next;
                    stack[top++] = p - 1;
                }

                if (px < width - 1 && labels[p + 1] == -1)
                {
                    labels[p + 1] = next;
                    stack[top++] = p + 1;
                }

                if (py > 0 && labels[p - width] == -1)
                {
                    labels[p - width] = next;
                    stack[top++] = p - width;
                }

                if (py < height - 1 && labels[p + width] == -1)
                {
                    labels[p + width] = next;
                    stack[top++] = p + width;
                }
            }

            var bounds = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            if (bounds.Width >= MinFieldWidth
                && bounds.Height >= MinFieldHeight
                && area >= MinFillRatio * bounds.Width * bounds.Height)
            {
                candidates.Add((next, bounds, area));
            }
        }

        var fields = new List<RedField>(candidates.Count);
        foreach ((int label, RectInt bounds, int area) in candidates)
        {
            Rgba color = MedianColor(roi, labels, bounds, label);
            fields.Add(new RedField(bounds, color, area, MaxDistance(roi, bounds, color)));
        }

        fields.Sort((a, b) => a.Bounds.Y != b.Bounds.Y ? a.Bounds.Y.CompareTo(b.Bounds.Y) : a.Bounds.X.CompareTo(b.Bounds.X));
        return fields;
    }

    public static IReadOnlyList<IReadOnlyList<RedField>> GroupIntoLines(IEnumerable<RedField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var lines = new List<List<RedField>>();
        IEnumerable<RedField> ordered = fields
            .OrderBy(f => f.Bounds.Y)
            .ThenBy(f => f.Bounds.X);
        foreach (RedField field in ordered)
        {
            List<RedField>? line = lines.FirstOrDefault(l => l.TrueForAll(other => SameLine(field.Bounds, other.Bounds)));
            if (line is null)
            {
                lines.Add([field]);
            }
            else
            {
                line.Add(field);
            }
        }

        return lines
            .Select(l => (IReadOnlyList<RedField>)l.OrderBy(f => f.Bounds.X).ToList())
            .ToList();
    }

    /// <summary>Odległość euklidesowa kolorów w RGB (0..≈442), bez kanału alfa.</summary>
    internal static double Distance(byte r, byte g, byte b, Rgba color)
    {
        int dr = r - color.R;
        int dg = g - color.G;
        int db = b - color.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    private static bool SameLine(RectInt a, RectInt b)
    {
        int overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return overlap > 0 && overlap >= SameLineOverlap * Math.Max(a.Height, b.Height);
    }

    /// <summary>Mediana każdego kanału pikseli obszaru <paramref name="label"/> (histogramy 256 przedziałów).</summary>
    private static Rgba MedianColor(RgbaImage roi, int[] labels, RectInt bounds, int label)
    {
        var histR = new int[256];
        var histG = new int[256];
        var histB = new int[256];
        int total = 0;
        byte[] pixels = roi.Pixels;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
        {
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                if (labels[(y * roi.Width) + x] != label)
                {
                    continue;
                }

                int i = (y * roi.Stride) + (x * 4);
                histR[pixels[i]]++;
                histG[pixels[i + 1]]++;
                histB[pixels[i + 2]]++;
                total++;
            }
        }

        return new Rgba(Median(histR, total), Median(histG, total), Median(histB, total));
    }

    private static byte Median(int[] histogram, int total)
    {
        int half = (total + 1) / 2;
        int seen = 0;
        for (int v = 0; v < histogram.Length; v++)
        {
            seen += histogram[v];
            if (seen >= half)
            {
                return (byte)v;
            }
        }

        return 255;
    }

    private static double MaxDistance(RgbaImage roi, RectInt bounds, Rgba color)
    {
        double max = 0;
        byte[] pixels = roi.Pixels;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
        {
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                int i = (y * roi.Stride) + (x * 4);
                max = Math.Max(max, Distance(pixels[i], pixels[i + 1], pixels[i + 2], color));
            }
        }

        return max;
    }
}
