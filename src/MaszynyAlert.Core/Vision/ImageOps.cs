using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Operacje przygotowujące wycinek paska statusu do OCR oraz pomocnicze przeliczenia obrazu.
/// </summary>
public static class ImageOps
{
    /// <summary>
    /// Przygotowuje obraz ROI do OCR: maskuje czerwony tekst (czerwony piksel → czarny, reszta → biały),
    /// przycina do prostokąta czerwonych pikseli powiększonego o <paramref name="marginPx"/> z każdej strony
    /// (przycięty do granic obrazu; gdy nie ma czerwieni — bierze cały obraz, wypełniony bielą, z zachowaniem
    /// rozmiaru), a następnie powiększa ×<paramref name="scale"/> metodą najbliższego sąsiada.
    /// </summary>
    /// <param name="dilate">
    /// Gdy <see langword="true"/> (domyślnie), maska czerwieni jest dylatowana (3×3) przed skalowaniem —
    /// litery z antyaliasingiem bywają po progowaniu zbyt cienkie dla silnika OCR.
    /// </param>
    public static Gray8Image PrepareForOcr(RgbaImage roi, RedThresholds th, int marginPx = 20, int scale = 2, bool dilate = true)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ArgumentNullException.ThrowIfNull(th);

        int width = roi.Width;
        int height = roi.Height;
        int stride = roi.Stride;
        byte[] pixels = roi.Pixels;

        var mask = new bool[Math.Max(1, width * height)];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowStart + x * 4;
                var c = new Rgba(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                if (ColorMath.IsRed(c, th))
                {
                    mask[y * width + x] = true;
                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }
        }

        bool anyRed = minX != int.MaxValue;

        RectInt cropRect = anyRed
            ? new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1).Inflate(marginPx, marginPx).Clamp(width, height)
            : new RectInt(0, 0, width, height);

        if (dilate && anyRed)
        {
            mask = Dilate3x3(mask, width, height);
        }

        int cw = Math.Max(1, cropRect.Width);
        int ch = Math.Max(1, cropRect.Height);
        var gray = new Gray8Image(cw, ch);

        for (int y = 0; y < ch; y++)
        {
            int sy = cropRect.Y + y;
            for (int x = 0; x < cw; x++)
            {
                int sx = cropRect.X + x;
                bool isRedPixel = anyRed && sx >= 0 && sx < width && sy >= 0 && sy < height && mask[sy * width + sx];
                gray.Set(x, y, isRedPixel ? (byte)0 : (byte)255);
            }
        }

        return scale <= 1 ? gray : ScaleNearest(gray, scale);
    }

    /// <summary>
    /// Przygotowuje wycinek z LICZBĄ (np. procent postępu, tryb <c>ProgressMode.Number</c>) do OCR:
    /// skala szarości → (gdy <paramref name="invert"/>) odwrócenie jasności (jasny tekst na ciemnym tle
    /// → ciemny na jasnym, czego oczekuje silnik OCR) → rozciągnięcie kontrastu (zakres [min..max] pikseli
    /// rozciągnięty do pełnego [0..255] — mała czcionka procentu na jednolitym tle bywa niskokontrastowa,
    /// a bez rozciągnięcia krawędzie cyfr są zbyt "miękkie" dla OCR; gdy min==max, obraz jest jednolity —
    /// bez zmian, bo nie ma czego rozciągać) → biały margines <paramref name="marginPx"/> z każdej strony
    /// (silniki OCR, w tym Windows.Media.Ocr, gorzej rozpoznają tekst przyklejony do krawędzi obrazu) →
    /// powiększenie ×<paramref name="scale"/> najbliższym sąsiadem (patrz <see cref="ScaleNearest"/>).
    /// Wycinek o zerowym rozmiarze (np. ROI przycięte do zera przez <c>Clamp</c>) NIE rzuca wyjątku —
    /// zwraca mały biały obraz (<paramref name="marginPx"/>*2 × <paramref name="marginPx"/>*2), na którym
    /// OCR po prostu nic nie odczyta (odczyt "nieczytelny", nie awaria).
    /// </summary>
    public static Gray8Image PrepareTextForOcr(RgbaImage roi, int marginPx, int scale, bool invert)
    {
        ArgumentNullException.ThrowIfNull(roi);

        if (roi.Width == 0 || roi.Height == 0)
        {
            int emptySize = marginPx * 2;
            var empty = new Gray8Image(emptySize, emptySize);
            Array.Fill(empty.Pixels, (byte)255);
            return empty;
        }

        Gray8Image gray = ToGray(roi);
        byte[] px = gray.Pixels;

        if (invert)
        {
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = (byte)(255 - px[i]);
            }
        }

        byte min = 255;
        byte max = 0;
        foreach (byte v in px)
        {
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }
        }

        if (max > min)
        {
            double factor = 255.0 / (max - min);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = (byte)Math.Clamp(Math.Round((px[i] - min) * factor), 0, 255);
            }
        }

        var margined = new Gray8Image(gray.Width + (2 * marginPx), gray.Height + (2 * marginPx));
        Array.Fill(margined.Pixels, (byte)255);
        for (int y = 0; y < gray.Height; y++)
        {
            for (int x = 0; x < gray.Width; x++)
            {
                margined.Set(x + marginPx, y + marginPx, gray.Get(x, y));
            }
        }

        return scale <= 1 ? margined : ScaleNearest(margined, scale);
    }

    public static Gray8Image? PrepareRedFieldTextForOcr(RgbaImage roi, RedThresholds red, int marginPx, int scale)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ArgumentNullException.ThrowIfNull(red);

        var textFields = RedFieldLocator.Find(roi, red).Where(f => f.HasText).ToList();
        if (textFields.Count == 0)
        {
            return null;
        }

        int margin = Math.Max(0, marginPx);
        IReadOnlyList<IReadOnlyList<RedField>> lines = RedFieldLocator.GroupIntoLines(textFields);
        var spans = lines.Select(LineSpan).ToList();
        int width = spans.Max(s => s.Width) + (2 * margin);
        int height = spans.Sum(s => s.Height) + ((spans.Count + 1) * margin);
        var gray = new Gray8Image(width, height);
        Array.Fill(gray.Pixels, (byte)255);

        byte[] pixels = roi.Pixels;
        int top = margin;
        for (int l = 0; l < lines.Count; l++)
        {
            RectInt span = spans[l];
            foreach (RedField field in lines[l])
            {
                for (int y = field.Bounds.Y; y < field.Bounds.Bottom; y++)
                {
                    int dstRow = (top + y - span.Y) * width;
                    for (int x = field.Bounds.X; x < field.Bounds.Right; x++)
                    {
                        int i = (y * roi.Stride) + (x * 4);
                        double distance = RedFieldLocator.Distance(pixels[i], pixels[i + 1], pixels[i + 2], field.Color);
                        double ink = Math.Min(1.0, distance / field.TextContrast);
                        gray.Pixels[dstRow + margin + x - span.X] = (byte)Math.Clamp(Math.Round(255 * (1.0 - ink)), 0, 255);
                    }
                }
            }

            top += span.Height + margin;
        }

        return scale <= 1 ? gray : ScaleNearest(gray, scale);
    }

    /// <summary>Obwiednia wszystkich pól jednego wiersza (patrz <see cref="PrepareRedFieldTextForOcr"/>).</summary>
    private static RectInt LineSpan(IReadOnlyList<RedField> line)
    {
        RectInt span = RectInt.Empty;
        foreach (RedField field in line)
        {
            span = span.Union(field.Bounds);
        }

        return span;
    }

    /// <summary>Konwertuje obraz RGBA do skali szarości (luma = 0,299 R + 0,587 G + 0,114 B).</summary>
    public static Gray8Image ToGray(RgbaImage img)
    {
        ArgumentNullException.ThrowIfNull(img);

        var gray = new Gray8Image(img.Width, img.Height);
        byte[] pixels = img.Pixels;
        int stride = img.Stride;

        for (int y = 0; y < img.Height; y++)
        {
            int rowStart = y * stride;
            int grayRow = y * img.Width;
            for (int x = 0; x < img.Width; x++)
            {
                int i = rowStart + x * 4;
                double luma = (0.299 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.114 * pixels[i + 2]);
                gray.Pixels[grayRow + x] = (byte)Math.Clamp(Math.Round(luma), 0, 255);
            }
        }

        return gray;
    }

    /// <summary>Udział czerwonych pikseli (0..1) całego obrazu <paramref name="img"/> wg progów <paramref name="th"/>.</summary>
    public static double RedFraction(RgbaImage img, RedThresholds th)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(th);

        int total = img.Width * img.Height;
        if (total == 0)
        {
            return 0.0;
        }

        byte[] pixels = img.Pixels;
        int stride = img.Stride;
        int red = 0;

        for (int y = 0; y < img.Height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < img.Width; x++)
            {
                int i = rowStart + x * 4;
                var c = new Rgba(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                if (ColorMath.IsRed(c, th))
                {
                    red++;
                }
            }
        }

        return (double)red / total;
    }

    private static bool[] Dilate3x3(bool[] mask, int width, int height)
    {
        var result = new bool[mask.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool set = false;
                for (int dy = -1; dy <= 1 && !set; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    int rowBase = ny * width;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        if (mask[rowBase + nx])
                        {
                            set = true;
                            break;
                        }
                    }
                }

                result[(y * width) + x] = set;
            }
        }

        return result;
    }

    private static Gray8Image ScaleNearest(Gray8Image src, int scale)
    {
        int w = src.Width * scale;
        int h = src.Height * scale;
        var result = new Gray8Image(w, h);

        for (int y = 0; y < h; y++)
        {
            int sy = y / scale;
            for (int x = 0; x < w; x++)
            {
                int sx = x / scale;
                result.Set(x, y, src.Get(sx, sy));
            }
        }

        return result;
    }
}
