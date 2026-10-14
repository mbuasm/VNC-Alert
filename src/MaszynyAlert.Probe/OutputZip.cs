namespace MaszynyAlert.Probe;

public static class OutputZip
{
    public static string BuildZipPath(string outDir, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);

        string fullOutDir = Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(fullOutDir);
        parent = string.IsNullOrEmpty(parent) ? "." : parent;
        string outName = Path.GetFileName(fullOutDir);
        return Path.Combine(parent, $"{outName}_{now:yyyyMMdd-HHmmss}.zip");
    }
}
