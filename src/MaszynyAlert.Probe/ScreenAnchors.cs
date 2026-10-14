using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Probe;

public sealed record MachineAnchors
{
    /// <summary>Obszar liczby procent postępu ("Milling Status"), albo <see langword="null"/>, gdy etykieta nie została znaleziona przez OCR.</summary>
    public RectInt? ProgressRoi { get; init; }

    /// <summary>Surowy tekst OCR odczytany z <see cref="ProgressRoi"/> przy weryfikacji kotwicy (diagnostyka) — <see langword="null"/>, gdy <see cref="ProgressRoi"/> jest <see langword="null"/>.</summary>
    public string? ProgressRawText { get; init; }

    /// <summary>Procent odczytany z <see cref="ProgressRawText"/> przez <see cref="PercentParser"/>, albo <see langword="null"/>, gdy nieczytelny.</summary>
    public int? ProgressPercent { get; init; }

    public bool ProgressRoiIsGuess { get; init; }

    /// <summary>Obszar nazwy pliku pracy ("File Name"), albo <see langword="null"/>, gdy etykieta nie została znaleziona.</summary>
    public RectInt? FileNameRoi { get; init; }

    /// <summary>Surowy tekst OCR odczytany z <see cref="FileNameRoi"/> przy weryfikacji kotwicy — <see langword="null"/>, gdy <see cref="FileNameRoi"/> jest <see langword="null"/>.</summary>
    public string? FileNameRawText { get; init; }

    /// <summary>
    /// Obszar okienka alertów do obserwacji w <c>watch</c> — wynik heurystyki
    /// (<see cref="ScreenAnchors.FindAlertWindowHeuristic"/>), gdy się powiodła, inaczej
    /// <c>ScreenProfile.StatusBarRoi</c> (patrz <see cref="AlertWindowIsHeuristic"/>). NIGDY <see langword="null"/>
    /// — może być <see cref="RectInt.Empty"/>, gdy profil też nie ma sensownego <c>StatusBarRoi</c>.
    /// </summary>
    public RectInt AlertWindowRoi { get; init; } = RectInt.Empty;

    /// <summary>Prawda, gdy <see cref="AlertWindowRoi"/> pochodzi z heurystyki "Tool"/"Manual", a nie z profilu (diagnostyka/raport).</summary>
    public bool AlertWindowIsHeuristic { get; init; }

    /// <summary>Kotwica pusta (nic nie znaleziono, OCR niedostępny albo jeszcze nie liczona) — używa tylko <c>StatusBarRoi</c> profilu.</summary>
    public static MachineAnchors Fallback(RectInt statusBarRoi) => new() { AlertWindowRoi = statusBarRoi, AlertWindowIsHeuristic = false };
}

public static class ScreenAnchors
{
    private static readonly IReadOnlyList<IReadOnlyList<string>> MillingStatusVariants =
    [
        ["Milling", "Status"],
        ["Milling", "Status:"],
        ["MillingStatus"],
        ["Progress:"],
        ["Progress"],
    ];

    private static readonly IReadOnlyList<IReadOnlyList<string>> FileNameVariants =
    [
        ["File", "Name"],
        ["File", "Name:"],
        ["FileName"],
    ];

    /// <summary>
    /// Liczy kotwice dla jednej klatki: pole postępu (etykieta "Milling Status"), pole nazwy pliku
    /// (etykieta "File Name") i okienko alertów (heurystyka "Tool"/"Manual", patrz
    /// <see cref="FindAlertWindowHeuristic"/>). Gdy etykieta nie zostanie znaleziona na OCR klatki w skali 1:1
    /// (typowe dla małej czcionki), próbuje ponownie na klatce powiększonej ×2 i dzieli wynikowe współrzędne
    /// przez 2. Weryfikuje znalezione pole postępu/nazwy pliku, wycinając
    /// je z <paramref name="full"/> i odczytując OCR jeszcze raz (<see cref="ImageOps.PrepareTextForOcr"/>) —
    /// diagnostyka trafności kotwicy, wynik w <see cref="MachineAnchors.ProgressRawText"/>/<see cref="MachineAnchors.ProgressPercent"/>.
    /// Gdy <paramref name="ocr"/> jest niedostępny (<c>IOcrEngine.IsAvailable</c> == <see langword="false"/>),
    /// od razu zwraca <see cref="MachineAnchors.Fallback"/> bez wołania OCR.
    /// </summary>
    public static async Task<MachineAnchors> ComputeAsync(
        RgbaImage full, IOcrEngine ocr, ScreenProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(profile);

        if (!ocr.IsAvailable)
        {
            return MachineAnchors.Fallback(profile.StatusBarRoi);
        }

        OcrResult ocrScale1 = await ocr.RecognizeAsync(ImageOps.ToGray(full), ct).ConfigureAwait(false);

        AnchoredField? milling = LabelAnchorLocator.FindAny(ocrScale1, MillingStatusVariants, full.Width, full.Height);
        AnchoredField? fileName = FindFileName(ocrScale1, full.Width, full.Height);

        if (milling is null || fileName is null)
        {
            RgbaImage scaled = full.Scale(2);
            OcrResult ocrScale2 = await ocr.RecognizeAsync(ImageOps.ToGray(scaled), ct).ConfigureAwait(false);
            milling ??= Halve(LabelAnchorLocator.FindAny(ocrScale2, MillingStatusVariants, scaled.Width, scaled.Height));
            fileName ??= Halve(FindFileName(ocrScale2, scaled.Width, scaled.Height));
        }

        RectInt? alertWindow = FindAlertWindowHeuristic(ocrScale1, full.Width, full.Height);

        (RectInt? progressRoi, string? progressText, int? progressPercent) =
            await VerifyNumberFieldAsync(full, milling?.Value, profile, ocr, ct).ConfigureAwait(false);

        // Kotwica bez odczytanej wartości to tylko "wartość pewnie pojawi się tu obok" — patrz
        // MachineAnchors.ProgressRoiIsGuess. Wołający ma o tym wiedzieć, zanim poda taki obszar
        // operatorowi do wklejenia albo użyje go zamiast zmierzonego ROI z profilu.
        bool progressIsGuess = progressRoi is not null && string.IsNullOrWhiteSpace(milling?.ValueText);
        (RectInt? fileNameRoi, string? fileNameText, _) =
            await VerifyNumberFieldAsync(full, fileName?.Value, profile, ocr, ct, parsePercent: false).ConfigureAwait(false);

        return new MachineAnchors
        {
            ProgressRoi = progressRoi,
            ProgressRawText = progressText,
            ProgressPercent = progressPercent,
            ProgressRoiIsGuess = progressIsGuess,
            FileNameRoi = fileNameRoi,
            FileNameRawText = fileNameText,
            AlertWindowRoi = alertWindow ?? profile.StatusBarRoi,
            AlertWindowIsHeuristic = alertWindow is not null,
        };
    }

    private static AnchoredField? FindFileName(OcrResult ocr, int width, int height) =>
        LabelAnchorLocator.FindAny(ocr, FileNameVariants, width, height)
        ?? JobFileLocator.Find(ocr, width, height);

    private static async Task<(RectInt? Roi, string? Text, int? Percent)> VerifyNumberFieldAsync(
        RgbaImage full, RectInt? roi, ScreenProfile profile, IOcrEngine ocr, CancellationToken ct, bool parsePercent = true)
    {
        if (roi is not { IsEmpty: false } r)
        {
            return (null, null, null);
        }

        RgbaImage crop = full.Crop(r.Clamp(full.Width, full.Height));
        Gray8Image forOcr = ImageOps.PrepareTextForOcr(crop, marginPx: 8, scale: 3, invert: profile.DarkTheme);
        OcrResult verify = await ocr.RecognizeAsync(forOcr, ct).ConfigureAwait(false);

        int? percent = null;
        if (parsePercent && PercentParser.TryParse(verify.Text, out int pct))
        {
            percent = pct;
        }

        return (r, verify.Text, percent);
    }

    /// <summary>Przelicza wynik znaleziony na klatce powiększonej ×2 z powrotem na współrzędne oryginalnej klatki (dzieli przez 2).</summary>
    private static AnchoredField? Halve(AnchoredField? f)
    {
        if (f is null)
        {
            return null;
        }

        return new AnchoredField(Halve(f.Label), Halve(f.Value), f.LabelText, f.ValueText);
    }

    private static RectInt Halve(RectInt r) =>
        new(r.X / 2, r.Y / 2, Math.Max(1, r.Width / 2), Math.Max(1, r.Height / 2));

    public static RectInt? FindAlertWindowHeuristic(OcrResult ocr, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(ocr);

        var byLine = ocr.Words.GroupBy(w => w.LineIndex).OrderBy(g => g.Key);
        foreach (var group in byLine)
        {
            List<OcrWord> words = group.OrderBy(w => w.Bounds.X).ToList();
            OcrWord? tool = FindFuzzyWord(words, "Tool");
            OcrWord? manual = FindFuzzyWord(words, "Manual");
            if (tool is null || manual is null || manual.Bounds.X <= tool.Bounds.X)
            {
                continue;
            }

            int h = tool.Bounds.Height;
            if (h <= 0)
            {
                continue;
            }

            int xStart = tool.Bounds.X - 20;
            int xEnd = manual.Bounds.Right + (manual.Bounds.Right - tool.Bounds.X);
            int yTop = tool.Bounds.Y - (int)Math.Round(7.0 * h);
            int yBottom = tool.Bounds.Y - (int)Math.Round(2.5 * h);

            RectInt candidate = new RectInt(xStart, yTop, xEnd - xStart, yBottom - yTop).Clamp(frameWidth, frameHeight);
            if (!candidate.IsEmpty)
            {
                return candidate;
            }
        }

        return null;
    }

    private static OcrWord? FindFuzzyWord(List<OcrWord> words, string target)
    {
        foreach (OcrWord w in words)
        {
            string stripped = w.Text.Trim().TrimEnd(':', '.', ',');
            if (Levenshtein.Distance(stripped.ToUpperInvariant(), target.ToUpperInvariant()) <= 1)
            {
                return w;
            }
        }

        return null;
    }

    /// <summary>Formatuje jedną wartość ROI jako fragment JSON do wklejenia w config.json, np. <c>"progressRoi": { "x": 10, "y": 20, "width": 30, "height": 12 }</c>.</summary>
    public static string FormatRoiJsonSnippet(string propertyName, RectInt roi) =>
        $"\"{propertyName}\": {{ \"x\": {roi.X}, \"y\": {roi.Y}, \"width\": {roi.Width}, \"height\": {roi.Height} }}";

    /// <summary>Formatuje ROI jako "x,y,w,h" — wspólny format do raportu i tabeli podsumowującej.</summary>
    public static string FormatRoi(RectInt r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    /// <summary>
    /// Format kolumny "pole postępu" tabeli podsumowującej z wyniku kotwiczenia:
    /// "x,y,w,h (odczyt N %)" / "x,y,w,h (OCR nie odczytał liczby)" / "nie znaleziono". Nie zwraca "brak
    /// OCR" — o tym decyduje wołający (patrz <c>Commands/OcrCommand.cs</c>), bo ta metoda dostaje gotowy
    /// <see cref="MachineAnchors"/>, a nie wie, czy w ogóle próbowano liczyć (OCR niedostępny → wołający
    /// nie woła <see cref="ComputeAsync"/> w ogóle).
    /// </summary>
    public static string DescribeProgressField(MachineAnchors anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);

        if (anchors.ProgressRoi is not { } roi)
        {
            return "nie znaleziono";
        }

        if (anchors.ProgressRoiIsGuess)
        {
            return $"{FormatRoi(roi)} (zgadnięte obok etykiety)";
        }

        return anchors.ProgressPercent is { } pct
            ? $"{FormatRoi(roi)} (odczyt {pct} %)"
            : $"{FormatRoi(roi)} (OCR nie odczytał liczby)";
    }

    /// <summary>Format kolumny "nazwa pliku" tabeli podsumowującej — patrz <see cref="DescribeProgressField"/>.</summary>
    public static string DescribeFileNameField(MachineAnchors anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        return anchors.FileNameRoi is { } roi ? FormatRoi(roi) : "nie znaleziono";
    }
}
