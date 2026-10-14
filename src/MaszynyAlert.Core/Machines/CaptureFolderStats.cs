namespace MaszynyAlert.Core.Machines;

public readonly record struct CaptureFolderStats(int Files, long Bytes, DateTimeOffset? Oldest)
{
    /// <summary>Pusty katalog (albo magazyn, który w ogóle nie zapisuje plików — <see cref="NullCaptureStore"/>).</summary>
    public static CaptureFolderStats Empty => new(0, 0, null);

    /// <summary>Rozmiar w megabajtach, zaokrąglony w górę do pełnego MB (0 tylko dla pustego katalogu).</summary>
    public int Megabytes => Bytes == 0 ? 0 : (int)Math.Max(1, (Bytes + (1024L * 1024L) - 1) / (1024L * 1024L));
}
