namespace MaszynyAlert.Core.Diagnostics;

/// <summary>Jawne limity niezaufanych danych wejściowych paczki diagnostycznej.</summary>
public sealed record DiagnosticsExportOptions
{
    public int MaxFiles { get; init; } = 200;

    public long MaxTotalBytes { get; init; } = 50L * 1024 * 1024;

    public long MaxFileBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Maksymalna liczba plików i katalogów obejrzanych podczas wyboru danych.</summary>
    public int MaxScannedEntries { get; init; } = 5_000;

    internal Action<string>? BeforeCopyFileForTests { get; init; }
}
