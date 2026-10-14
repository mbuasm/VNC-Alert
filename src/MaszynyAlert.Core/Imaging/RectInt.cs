namespace MaszynyAlert.Core.Imaging;

/// <summary>
/// Prostokąt o współrzędnych całkowitych (piksele). Używany do opisu ROI (obszaru zainteresowania)
/// na obrazie oraz prostokątów aktualizacji protokołu RFB.
/// </summary>
public readonly record struct RectInt(int X, int Y, int Width, int Height)
{
    /// <summary>Współrzędna X prawej krawędzi (X + Width).</summary>
    public int Right => X + Width;

    /// <summary>Współrzędna Y dolnej krawędzi (Y + Height).</summary>
    public int Bottom => Y + Height;

    /// <summary>Prawda, gdy prostokąt nie ma powierzchni (Width lub Height &lt;= 0).</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Pusty prostokąt (0,0,0,0).</summary>
    public static RectInt Empty => new(0, 0, 0, 0);

    /// <summary>Zwraca część wspólną z <paramref name="other"/> albo <see cref="Empty"/>, gdy się nie przecinają.</summary>
    public RectInt Intersect(RectInt other)
    {
        int x1 = Math.Max(X, other.X);
        int y1 = Math.Max(Y, other.Y);
        int x2 = Math.Min(Right, other.Right);
        int y2 = Math.Min(Bottom, other.Bottom);
        if (x2 <= x1 || y2 <= y1)
        {
            return Empty;
        }

        return new RectInt(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Sprawdza, czy punkt (x,y) leży wewnątrz prostokąta (górna/lewa krawędź włącznie, dolna/prawa wyłącznie).</summary>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>
    /// Zwraca najmniejszy prostokąt obejmujący ten prostokąt i <paramref name="other"/> (używane do
    /// wyznaczenia JEDNEGO obszaru pobieranego z VNC, obejmującego kilka ROI naraz — patrz
    /// <c>Machines.MachineMonitor</c>, obserwacja postoju). Gdy jeden z prostokątów jest pusty (<see cref="IsEmpty"/>),
    /// zwraca drugi bez zmian (pusty "nie liczy się" do sumy — analogicznie do sumy zbiorów z elementem
    /// neutralnym); gdy oba są puste, zwraca <paramref name="other"/> (czyli <see cref="Empty"/>, gdy oba nim są).
    /// </summary>
    public RectInt Union(RectInt other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        int x1 = Math.Min(X, other.X);
        int y1 = Math.Min(Y, other.Y);
        int x2 = Math.Max(Right, other.Right);
        int y2 = Math.Max(Bottom, other.Bottom);
        return new RectInt(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Zwraca prostokąt powiększony o <paramref name="dx"/>/<paramref name="dy"/> z każdej strony (ujemne wartości zmniejszają).</summary>
    public RectInt Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    /// <summary>Przycina prostokąt do granic obrazu o rozmiarze <paramref name="maxW"/> x <paramref name="maxH"/> (od (0,0)).</summary>
    public RectInt Clamp(int maxW, int maxH)
    {
        int x1 = Math.Clamp(X, 0, maxW);
        int y1 = Math.Clamp(Y, 0, maxH);
        int x2 = Math.Clamp(Right, 0, maxW);
        int y2 = Math.Clamp(Bottom, 0, maxH);
        int w = Math.Max(0, x2 - x1);
        int h = Math.Max(0, y2 - y1);
        return new RectInt(x1, y1, w, h);
    }
}
