using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;
using WinOcrResult = Windows.Media.Ocr.OcrResult;

namespace MaszynyAlert.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrEngine : IOcrEngine
{
    /// <summary>Obraz mniejszy niż to (w którymkolwiek wymiarze) jest dopełniany białym tłem — OCR odrzuca zbyt małe obrazy.</summary>
    private const int MinImageSize = 40;

    private readonly WinOcrEngine? _engine;
    private readonly ILogger _logger;

    public WindowsOcrEngine(string preferredLanguage = "en-US", ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(preferredLanguage);
        _logger = logger ?? NullLogger.Instance;
        _engine = CreateEngine(preferredLanguage, _logger);
    }

    /// <inheritdoc />
    public string Name => _engine is not null ? "Windows OCR" : "Windows OCR (niedostępny)";

    /// <inheritdoc />
    public bool IsAvailable => _engine is not null;

    /// <inheritdoc />
    public string? LanguageTag => _engine?.RecognizerLanguage.LanguageTag;

    /// <summary>Tagi języków, dla których w tym Windows jest zainstalowany pakiet rozpoznawania tekstu — do diagnostyki (`probe.exe ocr`, okno Status).</summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        try
        {
            return WinOcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (_engine is null)
        {
            return OcrResult.Empty;
        }

        try
        {
            (SoftwareBitmap bitmap, double scale) = ToSoftwareBitmap(image);
            using (bitmap)
            {
                WinOcrResult ocrResult = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                List<string> lines = ocrResult.Lines.Select(l => l.Text).ToList();
                List<OcrWord> words = ExtractWords(ocrResult, scale);
                return new OcrResult(string.Join(' ', lines), lines) { Words = words };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Błąd Windows OCR — traktuję jako brak odczytanego tekstu w tym cyklu.");
            return OcrResult.Empty;
        }
    }

    private static WinOcrEngine? CreateEngine(string preferredLanguage, ILogger logger)
    {
        try
        {
            WinOcrEngine? engine = WinOcrEngine.TryCreateFromLanguage(new Language(preferredLanguage));
            if (engine is not null)
            {
                return engine;
            }

            engine = WinOcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                logger.LogWarning(
                    "Windows OCR jest niedostępny — brak pakietu językowego dla \"{Preferred}\" ani żadnego z profilu użytkownika. " +
                    "Alarm będzie działał (wykrywanie po kolorze), tylko bez opisu tekstowego błędu.",
                    preferredLanguage);
            }

            return engine;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nie udało się utworzyć silnika Windows OCR.");
            return null;
        }
    }

    private static (SoftwareBitmap Bitmap, double Scale) ToSoftwareBitmap(Gray8Image image)
    {
        (int width, int height, byte[] gray) = PadToMinimumSize(image, MinImageSize);
        (int scaledWidth, int scaledHeight, byte[] scaledGray) = DownscaleIfTooLarge(width, height, gray, WinOcrEngine.MaxImageDimension);

        double scale = scaledWidth == width ? 1.0 : (double)scaledWidth / width;

        byte[] bgra = new byte[scaledWidth * scaledHeight * 4];
        for (int i = 0; i < scaledWidth * scaledHeight; i++)
        {
            byte v = scaledGray[i];
            int di = i * 4;
            bgra[di] = v;       // B
            bgra[di + 1] = v;   // G
            bgra[di + 2] = v;   // R
            bgra[di + 3] = 255; // A
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, scaledWidth, scaledHeight, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(bgra.AsBuffer());
        return (bitmap, scale);
    }

    /// <summary>
    /// Buduje listę <see cref="OcrWord"/> ze słów zwróconych przez Windows OCR (<see cref="WinOcrResult.Lines"/>),
    /// przeliczając ich <c>BoundingRect</c> (współrzędne w SKALOWANEJ bitmapie, patrz <see cref="ToSoftwareBitmap"/>)
    /// z powrotem do układu oryginalnego obrazu przekazanego do <see cref="RecognizeAsync"/> — dzielimy przez
    /// <paramref name="scale"/> (odwrotność skalowania zastosowanego przy tworzeniu bitmapy) i zaokrąglamy do
    /// najbliższej liczby całkowitej (<see cref="RectInt"/> ma współrzędne całkowite w pikselach).
    /// </summary>
    private static List<OcrWord> ExtractWords(WinOcrResult ocrResult, double scale)
    {
        var words = new List<OcrWord>();
        IReadOnlyList<global::Windows.Media.Ocr.OcrLine> lines = ocrResult.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            IReadOnlyList<global::Windows.Media.Ocr.OcrWord> lineWords = lines[i].Words;
            for (int j = 0; j < lineWords.Count; j++)
            {
                global::Windows.Media.Ocr.OcrWord word = lineWords[j];
                RectInt bounds = ScaleRectBack(word.BoundingRect, scale);
                words.Add(new OcrWord(word.Text, bounds, i));
            }
        }

        return words;
    }

    /// <summary>Przelicza prostokąt WinRT (współrzędne <see langword="double"/>, układ skalowanej bitmapy) na <see cref="RectInt"/> w układzie oryginalnego obrazu.</summary>
    private static RectInt ScaleRectBack(global::Windows.Foundation.Rect rect, double scale)
    {
        int x = (int)Math.Round(rect.X / scale);
        int y = (int)Math.Round(rect.Y / scale);
        int width = (int)Math.Round(rect.Width / scale);
        int height = (int)Math.Round(rect.Height / scale);
        return new RectInt(x, y, width, height);
    }

    /// <summary>Dopełnia obraz białym tłem do co najmniej <paramref name="minSize"/> px w każdym wymiarze.</summary>
    private static (int Width, int Height, byte[] Pixels) PadToMinimumSize(Gray8Image image, int minSize)
    {
        if (image.Width >= minSize && image.Height >= minSize)
        {
            return (image.Width, image.Height, image.Pixels);
        }

        int width = Math.Max(image.Width, minSize);
        int height = Math.Max(image.Height, minSize);
        byte[] padded = new byte[width * height];
        Array.Fill(padded, (byte)255);

        for (int y = 0; y < image.Height; y++)
        {
            Array.Copy(image.Pixels, y * image.Width, padded, y * width, image.Width);
        }

        return (width, height, padded);
    }

    /// <summary>Skaluje obraz w dół (najbliższy sąsiad), żeby żaden wymiar nie przekraczał limitu silnika OCR.</summary>
    private static (int Width, int Height, byte[] Pixels) DownscaleIfTooLarge(int width, int height, byte[] pixels, uint maxDimension)
    {
        if (width <= maxDimension && height <= maxDimension)
        {
            return (width, height, pixels);
        }

        double scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
        int newWidth = Math.Max(1, (int)(width * scale));
        int newHeight = Math.Max(1, (int)(height * scale));

        byte[] scaled = new byte[newWidth * newHeight];
        for (int y = 0; y < newHeight; y++)
        {
            int srcY = Math.Min(height - 1, (int)(y / scale));
            int srcRow = srcY * width;
            int dstRow = y * newWidth;
            for (int x = 0; x < newWidth; x++)
            {
                int srcX = Math.Min(width - 1, (int)(x / scale));
                scaled[dstRow + x] = pixels[srcRow + srcX];
            }
        }

        return (newWidth, newHeight, scaled);
    }
}
