using MaszynyAlert.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Machines;

/// <summary>
/// Zapisuje zrzuty ekranu do plików PNG w katalogu diagnostycznym (<c>PathsConfig.CapturesDir</c>).
/// Nazwa pliku: <c>{machineId}_{yyyyMMdd-HHmmss}_{tag}.png</c>; przy kolizji nazw (dwa zrzuty tej
/// samej maszyny w tej samej sekundzie) dopisywany jest numer porządkowy (<c>-2</c>, <c>-3</c>, …).
/// </summary>
public sealed class FileCaptureStore : ICaptureStore
{
    private readonly string _dir;
    private readonly ILogger<FileCaptureStore> _logger;

    public FileCaptureStore(string dir, ILogger<FileCaptureStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        _dir = dir;
        _logger = logger ?? NullLogger<FileCaptureStore>.Instance;
    }

    /// <inheritdoc />
    public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(tag);

        try
        {
            Directory.CreateDirectory(_dir);

            // Czas LOKALNY w nazwie pliku — spójnie z logiem (Serilog) i history.csv (HistoryWriter
            // używa IClock.Now, też lokalnego).
            string baseName = $"{machineId}_{at.ToLocalTime():yyyyMMdd-HHmmss}_{tag}";
            string path = Path.Combine(_dir, baseName + ".png");
            int suffix = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(_dir, $"{baseName}-{suffix}.png");
                suffix++;
            }

            PngCodec.EncodeToFile(image, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Nie udało się zapisać zrzutu ekranu maszyny '{MachineId}' do katalogu '{Dir}'.", machineId, _dir);
            return null;
        }
    }

    /// <inheritdoc />
    public void Prune(TimeSpan retention) => Prune(retention, long.MaxValue, null);

    /// <inheritdoc />
    public void Prune(TimeSpan retention, long maxBytes, IReadOnlyCollection<string>? protectedFiles)
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }

        var protectedSet = BuildProtectedSet(protectedFiles);
        DateTime threshold = DateTime.UtcNow - retention;

        // Najpierw wiek; zostają tylko pliki mieszczące się w oknie retencji oraz te
        // wskazywane przez otwarty alert (przycisk „Pokaż zrzut” musi mieć co otworzyć).
        var kept = new List<(string Path, DateTime WrittenUtc, long Length)>();
        foreach (string file in Directory.EnumerateFiles(_dir, "*.png"))
        {
            try
            {
                var info = new FileInfo(file);
                bool isProtected = protectedSet.Contains(info.FullName);
                if (!isProtected && info.LastWriteTimeUtc < threshold)
                {
                    info.Delete();
                    continue;
                }

                if (!isProtected)
                {
                    kept.Add((info.FullName, info.LastWriteTimeUtc, info.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Nie udało się usunąć przeterminowanego zrzutu ekranu '{File}'.", file);
            }
        }

        if (maxBytes >= long.MaxValue)
        {
            return;
        }

        // Potem rozmiar. Do sumy wliczamy WSZYSTKO, co zostało w katalogu (także pliki chronione i
        // nie-PNG), bo to one realnie zajmują dysk; usuwać wolno tylko niechronione PNG, od najstarszego.
        long total = DirectorySize();
        if (total <= maxBytes)
        {
            return;
        }

        int deleted = 0;
        long freed = 0;
        foreach (var candidate in kept.OrderBy(f => f.WrittenUtc))
        {
            if (total - freed <= maxBytes)
            {
                break;
            }

            try
            {
                File.Delete(candidate.Path);
                freed += candidate.Length;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Nie udało się usunąć zrzutu ekranu '{File}' przy egzekwowaniu limitu miejsca.", candidate.Path);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Katalog zrzutów '{Dir}' przekraczał limit {LimitMb} MB — usunięto {Count} najstarszych plików ({FreedMb} MB).",
                _dir, maxBytes / (1024 * 1024), deleted, freed / (1024 * 1024));
        }
    }

    /// <inheritdoc />
    public CaptureFolderStats Describe()
    {
        if (!Directory.Exists(_dir))
        {
            return CaptureFolderStats.Empty;
        }

        int files = 0;
        long bytes = 0;
        DateTime? oldest = null;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_dir, "*.png"))
            {
                try
                {
                    var info = new FileInfo(file);
                    files++;
                    bytes += info.Length;
                    if (oldest is null || info.LastWriteTimeUtc < oldest)
                    {
                        oldest = info.LastWriteTimeUtc;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Pominięto zrzut '{File}' przy liczeniu zawartości katalogu.", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Nie udało się odczytać zawartości katalogu zrzutów '{Dir}'.", _dir);
            return CaptureFolderStats.Empty;
        }

        return new CaptureFolderStats(files, bytes, oldest is { } o ? new DateTimeOffset(o, TimeSpan.Zero) : null);
    }

    /// <summary>Pełne ścieżki plików, których porządkowanie nie może usunąć (zrzuty otwartych alertów).</summary>
    private HashSet<string> BuildProtectedSet(IReadOnlyCollection<string>? protectedFiles)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (protectedFiles is null)
        {
            return set;
        }

        foreach (string file in protectedFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            try
            {
                set.Add(Path.GetFullPath(file));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                // Ścieżka z innego komputera (alert przyjęty przez sieć) albo uszkodzony wpis — nie chroni
                // niczego lokalnie i nie może przerwać porządkowania.
                _logger.LogDebug(ex, "Pominięto nieprawidłową ścieżkę chronionego zrzutu '{File}'.", file);
            }
        }

        return set;
    }

    /// <summary>Suma rozmiarów WSZYSTKICH plików w katalogu zrzutów — to one zajmują dysk, nie tylko PNG.</summary>
    private long DirectorySize()
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Pominięto plik '{File}' przy liczeniu rozmiaru katalogu zrzutów.", file);
            }
        }

        return total;
    }
}
