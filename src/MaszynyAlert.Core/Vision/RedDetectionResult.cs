using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Wynik analizy udziału czerwonych pikseli na obrazie (zwykle wycinku paska statusu HMI).
/// </summary>
/// <param name="RedPixels">Liczba pikseli uznanych za czerwone.</param>
/// <param name="TotalPixels">Łączna liczba pikseli analizowanego obrazu.</param>
/// <param name="RedFraction">Udział czerwonych pikseli (0..1).</param>
/// <param name="IsRed"><see langword="true"/>, gdy <see cref="RedFraction"/> osiągnął próg <c>minFraction</c>.</param>
/// <param name="RedBounds">
/// Prostokąt obejmujący wszystkie czerwone piksele (w układzie współrzędnych analizowanego obrazu),
/// albo <see langword="null"/>, gdy nie znaleziono żadnego czerwonego piksela.
/// </param>
public sealed record RedDetectionResult(int RedPixels, int TotalPixels, double RedFraction, bool IsRed, RectInt? RedBounds);
