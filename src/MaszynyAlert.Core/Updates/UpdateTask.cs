using System.Security.Cryptography;
using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Updates;

public static class UpdateTaskContract
{
    /// <summary>Nazwa zadania w Harmonogramie zadań (ta sama w instalatorze, programie i teście instalatora).</summary>
    public const string TaskName = "VNC Alert update";

    /// <summary>Tryb programu wołany przez zadanie (SYSTEM): sprawdzenie żądania i kopii pliku ze spisem wydań.</summary>
    public const string VerifyArgument = "--update-task-verify";

    /// <summary>Żądanie instalacji zapisywane przez program w katalogu pobrań.</summary>
    public const string RequestFileName = "update-request.json";

    /// <summary>Wynik sprawdzenia (program jako SYSTEM → skrypt zadania), w katalogu tylko dla administratorów.</summary>
    public const string VerifiedFileName = "install-task.verified.json";

    /// <summary>Wynik zadania (czyta go pomocnik operatora i diagnostyka — wzorzec <c>install-*.result.json</c>).</summary>
    public const string TaskResultFileName = "install-task.result.json";

    /// <summary>Log zadania (wzorzec <c>install-*.log</c> — trafia do eksportu diagnostyki).</summary>
    public const string TaskLogFileName = "install-task.log";

    /// <summary>Kod wyjścia zadania i pomocnika: zadanie odrzuciło żądanie albo plik (nic nie uruchomiono).</summary>
    public const int ExitRejected = 6;

    public const int ExitVersionMismatch = 8;

    public static string AdminDirectory(string dataDirectory) => Path.Combine(dataDirectory, "updates-admin");

    /// <summary><c>state\updates</c> — pobrane instalatory, żądanie i pomocnik operatora (zapisywalny dla operatora).</summary>
    public static string DownloadDirectory(string dataDirectory) => Path.Combine(dataDirectory, "state", "updates");

    public const string RunDirectoryPrefix = "run-";

    /// <summary>Log instalatora danej wersji w katalogu zadania.</summary>
    public static string SetupLogFileName(Version version) => $"install-{version.ToString(4)}.log";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Żądanie instalacji od programu (niezaufane — zapisuje je konto operatora).</summary>
/// <param name="RequestId">Identyfikator żądania (GUID w zapisie „N”), po którym pomocnik rozpoznaje swój wynik.</param>
/// <param name="FileName">Nazwa pliku instalatora z katalogu wydań.</param>
public sealed record UpdateTaskRequest(string RequestId, string FileName)
{
    /// <summary>Najwięcej bajtów pliku żądania — więcej to na pewno nie nasze żądanie.</summary>
    public const int MaxBytes = 4096;

    /// <summary>Treść pliku żądania (JSON camelCase).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, UpdateTaskContract.Json);

    /// <summary>Czyta żądanie; odrzuca wszystko poza poprawnym GUID i nazwą instalatora bez ścieżki.</summary>
    public static bool TryParse(string json, out UpdateTaskRequest? request, out string error)
    {
        request = null;
        RawRequest? raw;
        try { raw = JsonSerializer.Deserialize<RawRequest>(json, UpdateTaskContract.Json); }
        catch (JsonException)
        {
            error = "Żądanie instalacji nie jest poprawnym JSON.";
            return false;
        }

        if (raw?.RequestId is not { } id || !Guid.TryParseExact(id, "N", out var guid))
        {
            error = "Żądanie instalacji nie ma poprawnego identyfikatora.";
            return false;
        }
        if (raw.FileName is not { } name || !ReleaseCatalog.TryParseFileVersion(name, out _))
        {
            error = "Żądanie wskazuje plik spoza konwencji nazw instalatora (VncAlert-Setup-<wersja>.exe albo dawne MaszynyAlert-Setup-<wersja>.exe, bez ścieżki).";
            return false;
        }

        request = new UpdateTaskRequest(guid.ToString("N"), name);
        error = "";
        return true;
    }

    /// <summary>
    /// Sam identyfikator z odrzuconego żądania (np. nazwa pliku ze ścieżką) — żeby pomocnik operatora rozpoznał odmowę
    /// swojego żądania od razu, a nie po 30 min czekania.
    /// </summary>
    public static string? TryReadRequestId(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<RawRequest>(json, UpdateTaskContract.Json);
            return raw?.RequestId is { } id && Guid.TryParseExact(id, "N", out var guid) ? guid.ToString("N") : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record RawRequest(string? RequestId, string? FileName);
}

/// <summary>Wynik sprawdzenia żądania przez zadanie (zapisywany do <see cref="UpdateTaskContract.VerifiedFileName"/>).</summary>
public sealed record UpdateTaskVerification
{
    public bool Accepted { get; init; }
    public string? RequestId { get; init; }
    public string? FileName { get; init; }
    public string? Version { get; init; }
    public string? SetupPath { get; init; }
    public string? Sha256 { get; init; }
    public long? SizeBytes { get; init; }
    public string Message { get; init; } = "";

    /// <summary>Treść pliku wyniku (JSON camelCase).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, UpdateTaskContract.Json);
}

/// <summary>
/// Logika zadania „VNC Alert update” po stronie programu uruchomionego jako SYSTEM: żądanie → spis wydań → kopia pliku
/// w katalogu tylko dla administratorów → rozmiar i SHA-256 kopii → znacznik próby. Uruchomienie instalatora robi skrypt
/// zadania (<c>installer/Update-Task.ps1</c>), bo instalator kończy procesy o nazwie programu.
/// </summary>
public sealed class UpdateTaskVerifier
{
    private readonly string _downloads;
    private readonly string _admin;
    private readonly Version _installed;
    private readonly UpdatesConfig _config;
    private readonly IUpdateSource _source;
    private readonly IClock _clock;
    private readonly Func<string, Stream> _createProtectedFile;

    /// <param name="dataDirectory">Katalog danych (<c>%ProgramData%\MaszynyAlert</c>).</param>
    /// <param name="installedVersion">Wersja tego programu — zadanie instaluje tylko wersję nowszą.</param>
    /// <param name="config">Sekcja <c>updates</c> z <c>config.json</c> (tylko dla administratorów).</param>
    /// <param name="source">Katalog wydań (adres i token z konfiguracji tylko dla administratorów).</param>
    /// <param name="clock">Zegar (czas w znaczniku próby).</param>
    /// <param name="createProtectedFile">Tworzy plik kopii bez praw operatora (Windows: ACL tylko SYSTEM i Administratorzy).</param>
    public UpdateTaskVerifier(string dataDirectory, Version installedVersion, UpdatesConfig config, IUpdateSource source,
        IClock clock, Func<string, Stream>? createProtectedFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(installedVersion);
        _downloads = UpdateTaskContract.DownloadDirectory(dataDirectory);
        _admin = UpdateTaskContract.AdminDirectory(dataDirectory);
        _installed = installedVersion;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _createProtectedFile = createProtectedFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    }

    /// <summary>Sprawdza żądanie i przygotowuje kopię instalatora. Nie rzuca przy odrzuceniu — wynik mówi dlaczego.</summary>
    public async Task<UpdateTaskVerification> VerifyAsync(CancellationToken ct)
    {
        RemoveStaleCopies();

        if (!TryReadRequest(out var request, out string requestError))
            return Reject(null, requestError) with { RequestId = _rejectedRequestId };
        if (!_config.Enabled)
            return Reject(request, "Automatyczne aktualizacje są wyłączone w config.json (updates.enabled) — zadanie nic nie instaluje.");
        if (!Uri.TryCreate((_config.Url ?? "").Trim(), UriKind.Absolute, out var catalog) || catalog.Scheme != Uri.UriSchemeHttps)
            return Reject(request, "Zadanie aktualizacji przyjmuje wyłącznie katalog wydań po https:// (updates.url) — instalacja przez okno UAC.");

        ReleaseCatalog.TryParseFileVersion(request!.FileName, out var requested);
        if (requested <= _installed)
        {
            return Reject(request,
                $"Wersja {requested.ToString(4)} nie jest nowsza od zainstalowanej {_installed.ToString(4)} — zadanie instaluje tylko nowszą.");
        }

        UpdateCheckResult check;
        try { check = await _source.CheckAsync(_installed, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Reject(request, "Zadanie nie odczytało spisu wydań: " + ex.Message);
        }
        if (check.Outcome is not (UpdateCheckOutcome.Available or UpdateCheckOutcome.UpToDate))
            return Reject(request, "Zadanie nie odczytało spisu wydań: " + (check.Error ?? check.Outcome.ToString()));

        var entry = check.Releases.FirstOrDefault(r => string.Equals(r.FileName, request.FileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Version != requested)
            return Reject(request, $"Pliku {request.FileName} nie ma w spisie wydań (INDEX.md) — zadanie uruchamia tylko wydania ze spisu.");

        string source = Path.Combine(_downloads, entry.FileName);
        var sourceInfo = new FileInfo(source);
        if (!DownloadDirectoriesArePlain() || !sourceInfo.Exists || sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return Reject(request, $"Brak pobranego pliku {entry.FileName} jako zwykłego pliku w katalogu pobrań — odrzucone.");

        string run = Path.Combine(_admin, UpdateTaskContract.RunDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        string copy = Path.Combine(run, entry.FileName);
        string? mismatch;
        try { mismatch = await CopyAndVerifyAsync(source, copy, entry, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(run);
            return Reject(request, $"Nie skopiowano pobranego pliku {entry.FileName} — odrzucone.");
        }
        if (mismatch is not null)
        {
            TryDeleteDirectory(run);
            return Reject(request, mismatch);
        }

        File.WriteAllText(Path.Combine(_admin, UpdateCoordinator.PendingInstallFileName),
            UpdateCoordinator.SerializePending(entry.Version, _clock.UtcNow));

        return new UpdateTaskVerification
        {
            Accepted = true,
            RequestId = request.RequestId,
            FileName = entry.FileName,
            Version = entry.Version.ToString(4),
            SetupPath = copy,
            Sha256 = entry.Sha256,
            SizeBytes = entry.SizeBytes,
            Message = $"Plik {entry.FileName} zgodny ze spisem wydań (rozmiar i SHA-256).",
        };
    }

    internal const string RequestProblem = @"Brak poprawnego żądania instalacji od programu (state\updates\update-request.json).";

    private bool DownloadDirectoriesArePlain()
    {
        foreach (string directory in new[] { Path.GetDirectoryName(_downloads)!, _downloads })
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private bool TryReadRequest(out UpdateTaskRequest? request, out string error)
    {
        request = null;
        error = RequestProblem;
        string path = Path.Combine(_downloads, UpdateTaskContract.RequestFileName);
        if (!DownloadDirectoriesArePlain()) return false;
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

        string json;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[UpdateTaskRequest.MaxBytes + 1];
            int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            if (total > UpdateTaskRequest.MaxBytes) return false;
            json = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (UpdateTaskRequest.TryParse(json, out request, out string parseError)) return true;
        // Poprawny identyfikator z niepoprawną nazwą pliku = nasze żądanie z błędną treścią: odmowa z powodem i identyfikatorem,
        // żeby pomocnik operatora rozpoznał ją od razu. Plik bez poprawnego identyfikatora — wspólny powód, bez szczegółów.
        _rejectedRequestId = UpdateTaskRequest.TryReadRequestId(json);
        if (_rejectedRequestId is not null) error = parseError;
        return false;
    }

    private string? _rejectedRequestId;

    /// <summary>
    /// Kopiuje plik operatora do pliku chronionego, licząc rozmiar i SHA-256 KOPII. Nie czyta więcej niż rozmiar ze spisu
    /// (+1 bajt, żeby wykryć dłuższy plik). <see langword="null"/> = zgodny.
    /// </summary>
    private async Task<string?> CopyAndVerifyAsync(string source, string copy, ReleaseEntry entry, CancellationToken ct)
    {
        TryDelete(copy);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var output = _createProtectedFile(copy))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, entry.SizeBytes + 1 - total)), ct)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > entry.SizeBytes) return Mismatch(entry);
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        if (total != entry.SizeBytes) return Mismatch(entry);
        string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return actual == entry.Sha256.ToLowerInvariant() ? null : Mismatch(entry);
    }

    private static string Mismatch(ReleaseEntry entry) =>
        $"Plik {entry.FileName} nie zgadza się ze spisem wydań (rozmiar {entry.SizeBytes} B albo SHA-256 {entry.Sha256}) — odrzucony, instalator nie wystartował.";

    /// <summary>
    /// Katalogi wcześniejszych przebiegów i luźne kopie instalatorów nie czekają w katalogu zadania na ponowne użycie. Katalog
    /// zadania jest tylko dla administratorów i SYSTEM, więc usuwanie rekurencyjne nie chodzi po cudzych przekierowaniach.
    /// </summary>
    private void RemoveStaleCopies()
    {
        if (!Directory.Exists(_admin)) Directory.CreateDirectory(_admin);
        foreach (string file in Directory.EnumerateFiles(_admin, "*.exe"))
        {
            if (ReleaseCatalog.TryParseFileVersion(Path.GetFileName(file), out _)) TryDelete(file);
        }
        foreach (string directory in Directory.EnumerateDirectories(_admin, UpdateTaskContract.RunDirectoryPrefix + "*"))
            TryDeleteDirectory(directory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static UpdateTaskVerification Reject(UpdateTaskRequest? request, string message) => new()
    {
        Accepted = false,
        RequestId = request?.RequestId,
        FileName = request?.FileName,
        Version = request is not null && ReleaseCatalog.TryParseFileVersion(request.FileName, out var v) ? v.ToString(4) : null,
        Message = message,
    };
}
