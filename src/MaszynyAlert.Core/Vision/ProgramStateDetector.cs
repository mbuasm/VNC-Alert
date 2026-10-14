using System.Globalization;
using System.Text;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Wynik odczytu pudełka stanu programu (<see cref="ProgramStateDetector.AnalyzeBox"/>).
/// </summary>
/// <param name="Known">
/// <see langword="false"/> = stanu nie da się odczytać (wycinek innego rozmiaru niż
/// <see cref="ProgramStateProfile.BoxRoi"/>, pudełko poza klatką) — wtedy pozostałe pola nic nie znaczą
/// i wołający NIE może z nich wnioskować ani „pracuje”, ani „przerwany”.
/// </param>
/// <param name="Running">Stan RUN: udział zieleni w pudełku ≥ <see cref="ProgramStateProfile.RunMinFraction"/>.</param>
/// <param name="RunFraction">Zmierzony udział pikseli pudełka w oknie <see cref="ProgramStateProfile.RunFill"/> (0..1).</param>
/// <param name="AbortPattern">
/// Napis w pudełku zgadza się ze wzorcem „IDLE: Abort” (<see cref="ProgramStateProfile.AbortPattern"/>).
/// Liczone tylko wtedy, gdy stan NIE jest RUN i profil ma wzorzec; w pozostałych przypadkach
/// <see langword="false"/>.
/// </param>
/// <param name="AbortMismatch">
/// Najmniejsza liczba niezgodnych komórek wzorca (po wszystkich przesunięciach) albo <c>-1</c>, gdy wzorca
/// nie porównywano (RUN, brak wzorca w profilu, stan nieznany).
/// </param>
/// <param name="Description">Krótki opis po polsku do diagnostyki, np. „IDLE (zieleń 0 %), wzorzec Abort: zgodny (0 niezgodnych)”.</param>
public sealed record ProgramStateReading(
    bool Known, bool Running, double RunFraction, bool AbortPattern, int AbortMismatch, string Description)
{
    /// <summary>Stan nieznany z podanym powodem (do diagnostyki).</summary>
    public static ProgramStateReading Unknown(string reason) => new(false, false, 0.0, false, -1, reason);
}

public static class ProgramStateDetector
{
    /// <summary>
    /// Wycina <see cref="ProgramStateProfile.BoxRoi"/> z pełnej klatki i analizuje go
    /// (<see cref="AnalyzeBox"/>). Gdy pudełko nie mieści się w CAŁOŚCI w klatce (np. inna rozdzielczość
    /// ekranu niż zmierzona), wynik jest „nieznany” — przycięte pudełko dałoby fałszywy udział zieleni.
    /// </summary>
    public static ProgramStateReading AnalyzeFrame(RgbaImage frame, ProgramStateProfile p)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(p);

        RectInt box = p.BoxRoi;
        if (box.IsEmpty || box.Clamp(frame.Width, frame.Height) != box)
        {
            return ProgramStateReading.Unknown(
                $"stan programu nieznany: pudełko {Rect(box)} nie mieści się w klatce {frame.Width}×{frame.Height}");
        }

        return AnalyzeBox(frame.Crop(box), p);
    }

    /// <summary>
    /// Analizuje wycinek DOKŁADNIE obszaru <see cref="ProgramStateProfile.BoxRoi"/> (inny rozmiar = stan
    /// nieznany): liczy udział zieleni RUN, a gdy to nie RUN i profil ma wzorzec — porównuje biały rdzeń
    /// napisu ze wzorcem „IDLE: Abort” (patrz <see cref="Config.AbortPattern"/>).
    /// </summary>
    public static ProgramStateReading AnalyzeBox(RgbaImage boxImage, ProgramStateProfile p)
    {
        ArgumentNullException.ThrowIfNull(boxImage);
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(p.RunFill);

        RectInt box = p.BoxRoi;
        if (box.IsEmpty || boxImage.Width != box.Width || boxImage.Height != box.Height)
        {
            return ProgramStateReading.Unknown(
                $"stan programu nieznany: wycinek {boxImage.Width}×{boxImage.Height} zamiast pudełka {box.Width}×{box.Height}");
        }

        byte[] pixels = boxImage.Pixels;
        int total = boxImage.Width * boxImage.Height;
        int green = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (ColorMath.InRange(new Rgba(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]), p.RunFill))
            {
                green++;
            }
        }

        double fraction = total == 0 ? 0.0 : (double)green / total;
        bool running = fraction >= p.RunMinFraction;
        string greenText = $"zieleń {Percent(fraction)}";

        if (running)
        {
            return new ProgramStateReading(true, true, fraction, false, -1, $"RUN ({greenText})");
        }

        if (p.AbortPattern is not { } pattern)
        {
            return new ProgramStateReading(true, false, fraction, false, -1, $"IDLE ({greenText}), bez wzorca Abort w profilu");
        }

        int mismatch = PatternMismatch(boxImage, box, pattern);
        bool abort = mismatch >= 0 && mismatch <= pattern.MaxMismatch;
        string patternText = mismatch < 0
            ? "wzorzec Abort: nieprawidłowy (brak wierszy)"
            : abort
                ? $"wzorzec Abort: zgodny ({mismatch} niezgodnych)"
                : $"wzorzec Abort: niezgodny ({mismatch} niezgodnych, próg {pattern.MaxMismatch})";
        return new ProgramStateReading(true, false, fraction, abort, mismatch, $"IDLE ({greenText}), {patternText}");
    }

    public static bool HasAbortWord(IReadOnlyList<OcrWord> words, ProgramStateProfile p)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(p);

        if (p.BoxRoi.IsEmpty || p.AbortWords is null || words.Count == 0)
        {
            return false;
        }

        var targets = new List<string>(p.AbortWords.Count);
        foreach (string target in p.AbortWords)
        {
            string normalized = NormalizeWord(target);
            if (normalized.Length > 0)
            {
                targets.Add(normalized);
            }
        }

        if (targets.Count == 0)
        {
            return false;
        }

        foreach (OcrWord word in words)
        {
            if (word is null || string.IsNullOrEmpty(word.Text))
            {
                continue;
            }

            RectInt b = word.Bounds;
            if (!p.BoxRoi.Contains(b.X + (b.Width / 2), b.Y + (b.Height / 2)))
            {
                continue;
            }

            foreach (string piece in SplitPieces(word.Text))
            {
                string normalized = NormalizeWord(piece);
                if (normalized.Length == 0)
                {
                    continue;
                }

                foreach (string target in targets)
                {
                    if (Math.Abs(normalized.Length - target.Length) <= 1
                        && Levenshtein.Distance(normalized, target) <= p.AbortMaxDistance)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizacja słowa do porównania z <see cref="ProgramStateProfile.AbortWords"/>: polskie znaki
    /// diakrytyczne → ASCII (ą→a, ć→c, ę→e, ł→l, ń→n, ó→o, ś→s, ź/ż→z), małe litery, a potem zostają
    /// WYŁĄCZNIE litery a–z (cyfry, interpunkcja i inne znaki wypadają). <see langword="null"/> → "".
    /// </summary>
    public static string NormalizeWord(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw) switch
            {
                'ą' => 'a',
                'ć' => 'c',
                'ę' => 'e',
                'ł' => 'l',
                'ń' => 'n',
                'ó' => 'o',
                'ś' => 's',
                'ź' or 'ż' => 'z',
                var other => other,
            };
            if (c is >= 'a' and <= 'z')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Dzieli tekst słowa OCR na kawałki na każdym znaku, który nie jest literą ani cyfrą.</summary>
    private static IEnumerable<string> SplitPieces(string text)
    {
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || !char.IsLetterOrDigit(text[i]))
            {
                if (i > start)
                {
                    yield return text[start..i];
                }

                start = i + 1;
            }
        }
    }

    /// <summary>
    /// Najmniejsza liczba niezgodnych komórek wzorca po wszystkich przesunięciach −MaxShift..MaxShift
    /// (w poziomie i w pionie) albo <c>-1</c>, gdy wzorzec nie ma wierszy. Komórka wzorca, która po
    /// przesunięciu wypada poza pudełko, jest porównywana z „nie-białym” pikselem. Znak inny niż
    /// <c>'#'</c> liczy się jak <c>'.'</c> (poprawność zapisu pilnuje walidacja konfiguracji).
    /// </summary>
    private static int PatternMismatch(RgbaImage boxImage, RectInt box, AbortPattern pattern)
    {
        IReadOnlyList<string>? rows = pattern.Rows;
        if (rows is null || rows.Count == 0)
        {
            return -1;
        }

        int width = boxImage.Width;
        int height = boxImage.Height;
        byte[] pixels = boxImage.Pixels;

        // Maska białego rdzenia całego pudełka — liczona raz, porównania przesunięć czytają już tylko ją.
        var white = new bool[width * height];
        for (int i = 0, p = 0; p < white.Length; i += 4, p++)
        {
            byte min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            white[p] = pixels[i + 3] >= 128 && min >= pattern.WhiteMinChannel;
        }

        int originX = pattern.X - box.X;
        int originY = pattern.Y - box.Y;
        int maxShift = Math.Max(0, pattern.MaxShift);
        int best = int.MaxValue;
        for (int dy = -maxShift; dy <= maxShift; dy++)
        {
            for (int dx = -maxShift; dx <= maxShift; dx++)
            {
                int mismatch = 0;
                for (int r = 0; r < rows.Count && mismatch < best; r++)
                {
                    string row = rows[r] ?? string.Empty;
                    int y = originY + r + dy;
                    bool rowInside = y >= 0 && y < height;
                    for (int c = 0; c < row.Length; c++)
                    {
                        int x = originX + c + dx;
                        bool isWhite = rowInside && x >= 0 && x < width && white[(y * width) + x];
                        if (isWhite != (row[c] == '#'))
                        {
                            mismatch++;
                        }
                    }
                }

                best = Math.Min(best, mismatch);
            }
        }

        return best;
    }

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    private static string Rect(RectInt r) => $"{r.X},{r.Y},{r.Width}×{r.Height}";
}
