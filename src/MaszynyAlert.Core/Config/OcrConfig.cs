namespace MaszynyAlert.Core.Config;

/// <summary>Ustawienia silnika OCR (odczytu tekstu z obrazu).</summary>
public sealed record OcrConfig
{
    /// <summary>Preferowany język OCR (kod IETF, np. "en-US" — komunikaty maszyn są po angielsku).</summary>
    public string Language { get; init; } = "en-US";

    /// <summary>Silnik OCR: "windows" (Windows.Media.Ocr) albo "none" (wyłączony). Tesseract nie jest dołączony.</summary>
    public string Engine { get; init; } = "windows";
}
