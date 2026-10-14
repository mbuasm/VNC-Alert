using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Machines;

public interface ICaptureStore
{
    /// <summary>
    /// Zapisuje <paramref name="image"/> jako PNG. Zwraca pełną ścieżkę zapisanego pliku, albo
    /// <see langword="null"/>, gdy zapis się nie powiódł (błąd jest wtedy tylko logowany — nigdy nie
    /// przerywa pętli obserwatora).
    /// </summary>
    /// <param name="machineId">Identyfikator maszyny.</param>
    /// <param name="image">Obraz do zapisania (zwykle wycinek ROI paska statusu).</param>
    /// <param name="at">Chwila zdarzenia — używana w nazwie pliku.</param>
    /// <param name="tag">Krótki znacznik celu zrzutu: "blad" (nowy epizod) albo "pelny" (pełna klatka).</param>
    string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag);

    /// <summary>Usuwa zapisane zrzuty starsze niż <paramref name="retention"/> (wg czasu ostatniego zapisu).</summary>
    void Prune(TimeSpan retention);

    void Prune(TimeSpan retention, long maxBytes, IReadOnlyCollection<string>? protectedFiles) => Prune(retention);

    /// <summary>Liczba plików, zajęte miejsce i wiek najstarszego zrzutu — do licznika w Panelu administracyjnym.</summary>
    CaptureFolderStats Describe() => CaptureFolderStats.Empty;
}
