namespace MaszynyAlert.Core.Vision;

public sealed record ProgressDetectionResult(double FilledFraction, bool IsFull)
{
    /// <summary>
    /// Tryb number: odczytany procent (0..100), albo <see langword="null"/>, gdy OCR nie zwrócił żadnej
    /// rozpoznawalnej liczby (odczyt nieczytelny) — patrz <see cref="ProgressNumberReader"/>. Zawsze
    /// <see langword="null"/> w trybie bar.
    /// </summary>
    public bool OcrUnavailable { get; init; }

    public int? Percent { get; init; }

    /// <summary>
    /// Tryb number: surowy tekst zwrócony przez silnik OCR dla wycinka z liczbą procent — do kolumny
    /// „Postęp” w oknie statusu (diagnostyka „OCR nie odczytał: …”) i do logów. Zawsze <see langword="null"/>
    /// w trybie bar.
    /// </summary>
    public string? RawText { get; init; }

    public bool IsZero => Percent == 0 && !HasNonZeroFraction;

    public bool HasNonZeroFraction { get; init; }
}
