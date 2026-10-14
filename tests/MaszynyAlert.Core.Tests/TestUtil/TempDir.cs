namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>Tymczasowy katalog na czas jednego testu — usuwany (razem z zawartością) przy <see cref="Dispose"/>.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string prefix = "maszyny-alert")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Pełna ścieżka katalogu.</summary>
    public string Path { get; }

    /// <summary>Ścieżka do pliku/podkatalogu wewnątrz tego katalogu.</summary>
    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Sprzątanie katalogu tymczasowego nie może wywalić testu.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
