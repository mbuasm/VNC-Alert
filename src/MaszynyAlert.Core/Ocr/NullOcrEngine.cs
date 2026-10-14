using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Ocr;

/// <summary>Silnik OCR "brak" — używany, gdy żaden prawdziwy silnik nie jest dostępny (np. testy na Linux). Zawsze zwraca pusty wynik.</summary>
public sealed class NullOcrEngine : IOcrEngine
{
    /// <inheritdoc />
    public string Name => "brak";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string? LanguageTag => null;

    /// <inheritdoc />
    public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct) => Task.FromResult(OcrResult.Empty);
}
