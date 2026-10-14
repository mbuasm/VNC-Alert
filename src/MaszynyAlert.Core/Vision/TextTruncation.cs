using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

public static class TextTruncation
{
    /// <summary>Ile pikseli od krawędzi wycinka uznajemy za „dotyka krawędzi" (antyaliasing ostatniej litery).</summary>
    public const int EdgePx = 2;

    /// <summary>Znak doklejany do uciętego tekstu.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// Czy prostokąt czerwieni <paramref name="redBounds"/> (we współrzędnych wycinka o szerokości
    /// <paramref name="roiWidth"/>) dotyka lewej / prawej krawędzi wycinka. Brak czerwieni albo zerowa
    /// szerokość → oba <see langword="false"/>.
    /// </summary>
    public static (bool Left, bool Right) TouchesEdge(RectInt? redBounds, int roiWidth)
    {
        if (redBounds is not { } b || b.IsEmpty || roiWidth <= 0)
        {
            return (false, false);
        }

        bool left = b.X <= EdgePx - 1;
        bool right = b.Right >= roiWidth - EdgePx;
        return (left, right);
    }

    /// <summary>
    /// Zwraca <paramref name="text"/> z doklejonym „…" na końcu, gdy czerwień dotyka prawej krawędzi, i/lub
    /// na początku, gdy dotyka lewej. Pusty/biały tekst zwraca bez zmian (nie ma czego oznaczać — brak
    /// treści to osobny przypadek „Błąd (nie odczytano treści)"). Tekst już zakończony „…" nie dostaje
    /// drugiego.
    /// </summary>
    public static string MarkIfCut(string? text, RectInt? redBounds, int roiWidth)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return t;
        }

        (bool left, bool right) = TouchesEdge(redBounds, roiWidth);
        if (right && !t.EndsWith(Ellipsis, StringComparison.Ordinal))
        {
            t = t + " " + Ellipsis;
        }

        if (left && !t.StartsWith(Ellipsis, StringComparison.Ordinal))
        {
            t = Ellipsis + " " + t;
        }

        return t;
    }
}
