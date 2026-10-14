using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Wynik jednego sprawdzenia, czy HMI pokazuje ekran główny (patrz <see cref="MainScreenDetector"/>).
/// </summary>
/// <param name="MainScreenVisible">
/// <see langword="true"/> — etykieta ekranu głównego znaleziona (HMI pokazuje okno stanu maszyny);
/// <see langword="false"/> — OCR odczytał ekran, ale etykiety na nim nie ma (inny ekran, np. „Production
/// info”); <see langword="null"/> — NIE WIADOMO: silnik OCR rzucił wyjątkiem albo nie zwrócił żadnego
/// słowa z całej klatki (awaria/chwilowa niedostępność OCR, nie dowód na inny ekran) — wołający NIE
/// powinien traktować tego jako „inny ekran”.
/// </param>
/// <param name="LabelText">Tekst etykiety odczytany przez OCR, gdy znaleziono (diagnostyka).</param>
/// <param name="ScaledRetry">Czy etykietę znaleziono dopiero na klatce powiększonej ×2 (diagnostyka — sugeruje małą czcionkę HMI).</param>
public sealed record MainScreenDetectionResult(bool? MainScreenVisible, string? LabelText, bool ScaledRetry)
{
    /// <summary>Okno listy alarmów zasłania postęp, ale pozostawia pasek błędu widoczny.</summary>
    public bool AlarmPageVisible { get; init; }

    /// <summary>
    /// Prostokąt (we współrzędnych PEŁNEJ klatki 1:1, także gdy etykietę znaleziono dopiero na klatce
    /// powiększonej ×2), w którym OCR faktycznie odczytał etykietę kotwiczącą — albo <see langword="null"/>,
    /// gdy etykiety nie znaleziono. <c>Machines.MachineMonitor</c> używa go jako domyślnego „pasa kotwic”
    /// przy sprawdzaniu, czy HMI nie zmieniło ekranu w trakcie trwającego odczytu OCR (patrz
    /// <see cref="Config.MainScreenWatchProfile.ContextRoi"/>).
    /// </summary>
    public RectInt? LabelBounds { get; init; }

    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();

    /// <summary>Wynik „nie wiadomo” — patrz <see cref="MainScreenVisible"/>.</summary>
    public static MainScreenDetectionResult Unknown { get; } = new(null, null, false);
}

public sealed class MainScreenDetector
{
    private readonly MainScreenWatchProfile _profile;
    private readonly IOcrEngine _ocr;
    private readonly ILogger _logger;

    /// <summary>Kiedy ostatnio zalogowano ostrzeżenie o awarii silnika OCR — throttling jak w <see cref="ProgressNumberReader"/>.</summary>
    private DateTimeOffset? _lastOcrFailureLoggedAt;

    public MainScreenDetector(MainScreenWatchProfile profile, IOcrEngine ocr, ILogger? logger = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Ile razy w tej instancji faktycznie wywołano <see cref="IOcrEngine.RecognizeAsync"/> (diagnostyka/testy).</summary>
    public int OcrCalls { get; private set; }

    /// <summary>
    /// Sprawdza pełną klatkę <paramref name="fullFrame"/>: OCR w skali 1:1, a gdy etykiety nie ma i
    /// <see cref="MainScreenWatchProfile.RetryScaled"/> — jeszcze raz na klatce powiększonej ×2 (mała
    /// czcionka HMI). Awaria silnika OCR albo całkowicie pusty odczyt (zero słów z całego ekranu —
    /// prawdziwy ekran HMI, także „Production info”, zawsze ma jakiś tekst) daje
    /// <see cref="MainScreenDetectionResult.Unknown"/>, NIE „inny ekran”.
    /// </summary>
    public async Task<MainScreenDetectionResult> DetectAsync(RgbaImage fullFrame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fullFrame);

        if (!_ocr.IsAvailable || fullFrame.Width == 0 || fullFrame.Height == 0)
        {
            return MainScreenDetectionResult.Unknown;
        }

        try
        {
            OcrResult ocr1 = await RecognizeAsync(ImageOps.ToGray(fullFrame), ct).ConfigureAwait(false);
            if (HasAlarmPage(ocr1, fullFrame.Width, fullFrame.Height))
                return new MainScreenDetectionResult(false, "Current alarm page", false) { AlarmPageVisible = true, Words = ocr1.Words };
            if (FindOtherScreen(ocr1, fullFrame.Width, fullFrame.Height) is { } cover1)
                return new MainScreenDetectionResult(false, cover1.LabelText, false) { Words = ocr1.Words };
            AnchoredField? found = LabelAnchorLocator.FindAny(
                ocr1, _profile.EffectiveAnchorLabels, fullFrame.Width, fullFrame.Height, _profile.MaxDistance);
            if (found is not null)
            {
                return new MainScreenDetectionResult(true, found.LabelText, false) { LabelBounds = found.Label, Words = ocr1.Words };
            }

            bool anyWords = ocr1.Words.Count > 0 || !ocr1.IsEmpty;

            if (_profile.RetryScaled)
            {
                RgbaImage scaled = fullFrame.Scale(2);
                OcrResult ocr2 = await RecognizeAsync(ImageOps.ToGray(scaled), ct).ConfigureAwait(false);
                if (HasAlarmPage(ocr2, scaled.Width, scaled.Height))
                    return new MainScreenDetectionResult(false, "Current alarm page", true) { AlarmPageVisible = true, Words = ocr1.Words };
                if (FindOtherScreen(ocr2, scaled.Width, scaled.Height) is { } cover2)
                    return new MainScreenDetectionResult(false, cover2.LabelText, true) { Words = ocr1.Words };
                found = LabelAnchorLocator.FindAny(
                    ocr2, _profile.EffectiveAnchorLabels, scaled.Width, scaled.Height, _profile.MaxDistance);
                if (found is not null)
                {
                    // Klatka była powiększona ×2 — prostokąt sprowadzamy do skali 1:1, żeby wołający
                    // mógł go porównywać na zwykłych klatkach (z zapasem 1 px na zaokrąglenie).
                    RectInt label = new(found.Label.X / 2, found.Label.Y / 2,
                        (found.Label.Width / 2) + 1, (found.Label.Height / 2) + 1);
                    return new MainScreenDetectionResult(true, found.LabelText, true)
                    {
                        LabelBounds = label.Clamp(fullFrame.Width, fullFrame.Height),
                        Words = ocr1.Words,
                    };
                }

                anyWords |= ocr2.Words.Count > 0 || !ocr2.IsEmpty;
            }

            // Zero słów z całego ekranu = OCR nic nie widzi (czarny ekran po wyłączeniu HMI, awaria
            // silnika, obraz jeszcze niezaładowany) — nie wiemy, jaki to ekran. Ekran z jakimkolwiek
            // tekstem, ale bez etykiety, to faktycznie inny ekran HMI.
            return anyWords ? new MainScreenDetectionResult(false, null, false) { Words = ocr1.Words } : MainScreenDetectionResult.Unknown;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailureThrottled(ex);
            return MainScreenDetectionResult.Unknown;
        }
    }

    private async Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
    {
        OcrCalls++;
        return await _ocr.RecognizeAsync(image, ct).ConfigureAwait(false);
    }

    private static bool HasAlarmPage(OcrResult result, int width, int height) =>
        LabelAnchorLocator.FindAny(result, [["Current", "alarm", "page"]], width, height, 1) is not null;

    private AnchoredField? FindOtherScreen(OcrResult result, int width, int height) =>
        _profile.OtherScreenLabels.Count == 0
            ? null
            : LabelAnchorLocator.FindAny(result, _profile.OtherScreenLabels, width, height, _profile.MaxDistance);

    private void LogFailureThrottled(Exception ex)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_lastOcrFailureLoggedAt is { } last && now - last < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastOcrFailureLoggedAt = now;
        _logger.LogWarning(ex, "Błąd silnika OCR ('{Engine}') przy sprawdzaniu ekranu głównego HMI: {Error}", _ocr.Name, ex.Message);
    }
}
