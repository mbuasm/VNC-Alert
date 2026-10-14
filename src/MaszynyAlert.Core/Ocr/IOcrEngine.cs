using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Ocr;

/// <summary>Abstrakcja silnika OCR (odczytu tekstu z obrazu). Implementacje: <c>WindowsOcrEngine</c>, ew. wtyczka Tesseract.</summary>
public interface IOcrEngine
{
    /// <summary>Nazwa silnika (do logów/diagnostyki), np. "Windows.Media.Ocr" albo "brak".</summary>
    string Name { get; }

    /// <summary>Czy silnik jest w tym środowisku dostępny (np. zainstalowany pakiet językowy).</summary>
    bool IsAvailable { get; }

    /// <summary>Tag języka używany przez silnik (np. "en-US"), albo <see langword="null"/>, gdy nieznany/niedostępny.</summary>
    string? LanguageTag { get; }

    /// <summary>Rozpoznaje tekst na obrazie w skali szarości (zwykle wynik <c>ImageOps.PrepareForOcr</c>).</summary>
    Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct);
}
