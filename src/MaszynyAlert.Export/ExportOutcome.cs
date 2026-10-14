using MaszynyAlert.Core.Export;

namespace MaszynyAlert.Export;

/// <summary>Wynik eksportu: sukces albo komunikat dla administratora (bez wyjątku — Panel pokazuje go w oknie).</summary>
public sealed record ExportOutcome(bool Success, string? Error)
{
    public static readonly ExportOutcome Ok = new(true, null);
    public static ExportOutcome Fail(string error) => new(false, error);
}

/// <summary>
/// Zapis eksportu do pliku: najpierw cały dokument w pamięci, potem plik tymczasowy obok docelowego i podmiana — błąd
/// w trakcie tworzenia dokumentu nie zostawia uciętego pliku ani nie niszczy poprzedniego o tej samej nazwie. Błędy zapisu
/// (brak uprawnień, plik otwarty w Excelu, pełny dysk, zła ścieżka) wracają jako <see cref="ExportOutcome"/>.
/// </summary>
public static class ReportFileWriter
{
    /// <summary>Zapisuje PDF raportu do <paramref name="path"/>.</summary>
    public static ExportOutcome SavePdf(DowntimeExportData data, string path) =>
        Save(path, stream => DowntimePdfExporter.Write(data, stream));

    /// <summary>Zapisuje skoroszyt Excela raportu do <paramref name="path"/>.</summary>
    public static ExportOutcome SaveExcel(DowntimeExportData data, string path) =>
        Save(path, stream => DowntimeExcelExporter.Write(data, stream));

    private static string Describe(Exception ex, string path)
    {
        bool exists;
        bool readOnly = false;
        try
        {
            exists = File.Exists(path);
            if (exists) readOnly = File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
        }
        catch (Exception probe) when (probe is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            exists = false;
        }
        string reason = ex switch
        {
            DirectoryNotFoundException => "Katalog docelowy nie istnieje.",
            PathTooLongException => "Ścieżka pliku jest za długa.",
            _ when readOnly => "Plik docelowy jest tylko do odczytu.",
            _ when exists && (ex is IOException or UnauthorizedAccessException) =>
                "Plik jest otwarty w innym programie albo brak do niego dostępu. Jeśli plik jest otwarty (np. w Excelu albo przeglądarce PDF), "
                + "zamknij go i spróbuj ponownie.",
            UnauthorizedAccessException => "Brak uprawnień do zapisu w tym miejscu.",
            ArgumentException or NotSupportedException => "Nazwa albo ścieżka pliku jest niepoprawna.",
            _ => "Błąd zapisu na dysku.",
        };
        return $"{reason} (Szczegóły: {ex.Message})";
    }

    private static ExportOutcome Save(string path, Func<Stream, ExportOutcome> write)
    {
        if (string.IsNullOrWhiteSpace(path)) return ExportOutcome.Fail("Nie podano nazwy pliku.");
        using var buffer = new MemoryStream();
        var outcome = write(buffer);
        if (!outcome.Success) return outcome;
        string temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                buffer.Position = 0;
                buffer.CopyTo(file);
                file.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
            return ExportOutcome.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* zostaje plik .tmp */ }
            return ExportOutcome.Fail("Nie udało się zapisać pliku. " + Describe(ex, path));
        }
    }
}
