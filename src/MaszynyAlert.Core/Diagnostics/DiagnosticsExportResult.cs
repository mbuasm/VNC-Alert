namespace MaszynyAlert.Core.Diagnostics;

public sealed record DiagnosticsExportResult(
    string ArchivePath,
    IReadOnlyList<string> Warnings,
    int FilesIncluded);
