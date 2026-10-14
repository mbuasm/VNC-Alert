using System.Net;
using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Updates;

public sealed class HttpUpdateSource : IUpdateSource
{
    /// <summary>Spis jest tabelą kilkudziesięciu wierszy; limit chroni przed wczytaniem czegoś zupełnie innego.</summary>
    public const int MaxIndexBytes = 1024 * 1024;

    /// <summary>Instalator ma dziś ~55 MB; 2 GiB to granica, powyżej której plik na pewno nie jest naszym Setupem.</summary>
    public const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    private const string NeedsLoginMessage =
        "Katalog wydań wymaga logowania (Cloudflare Access) — wpisz identyfikator i sekret tokenu dostępu w Panel → Ustawienia → Ten komputer";

    private readonly HttpClient _http;
    private readonly UpdatesConfig _config;
    private readonly Func<string?> _accessSecret;
    private readonly IClock _clock;
    private readonly ILogger? _logger;
    private readonly TimeSpan _indexTimeout;
    private readonly TimeSpan _downloadTimeout;
    private readonly Uri? _baseUri;

    /// <param name="handler">Handler HTTP (w programie: z <c>AllowAutoRedirect = false</c>; w testach — podstawiony). Nie jest zwalniany przez tę klasę.</param>
    /// <param name="config">Sekcja <c>updates</c> (adres katalogu, identyfikator tokenu Access).</param>
    /// <param name="accessSecret">Sekret tokenu Access, czytany przy każdym żądaniu; <see langword="null"/>/pusty = bez nagłówków Access.</param>
    /// <param name="clock">Zegar programu (czas sprawdzenia w wyniku).</param>
    /// <param name="logger">Opcjonalny log.</param>
    /// <param name="timeout">
    /// Limit czasu jednego żądania. Domyślnie 15 s na spis i 10 min na pobranie instalatora; podana wartość
    /// obowiązuje dla obu (testy, lokalny serwer).
    /// </param>
    public HttpUpdateSource(HttpMessageHandler handler, UpdatesConfig config, Func<string?> accessSecret, IClock clock,
        ILogger? logger = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(accessSecret);
        ArgumentNullException.ThrowIfNull(clock);
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _config = config;
        _accessSecret = accessSecret;
        _clock = clock;
        _logger = logger;
        _indexTimeout = timeout ?? TimeSpan.FromSeconds(15);
        _downloadTimeout = timeout ?? TimeSpan.FromMinutes(10);
        _baseUri = Uri.TryCreate((config.Url ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ? baseUri : null;
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckAsync(Version installed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (_baseUri is null)
            return Result(UpdateCheckOutcome.Unavailable, null, $"Nieprawidłowy adres katalogu wydań (updates.url): '{_config.Url}'.");
        var uri = new Uri(_baseUri, "INDEX.md");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_indexTimeout);
        try
        {
            using var request = CreateRequest(uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (IsLoginResponse(response))
            {
                _logger?.LogWarning("Katalog wydań {Uri} wymaga logowania (HTTP {Status}).", uri, (int)response.StatusCode);
                return Result(UpdateCheckOutcome.NeedsLogin, null, NeedsLoginMessage);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Result(UpdateCheckOutcome.Unavailable, null,
                    $"Katalog wydań odpowiedział kodem HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }
            if (response.Content.Headers.ContentLength is > MaxIndexBytes)
                return Result(UpdateCheckOutcome.Unavailable, null, "Spis wydań (INDEX.md) jest za duży — to nie jest spis z serwera wydań.");

            string? text = await ReadLimitedTextAsync(response.Content, MaxIndexBytes, timeout.Token).ConfigureAwait(false);
            if (text is null)
                return Result(UpdateCheckOutcome.Unavailable, null, "Spis wydań (INDEX.md) jest za duży — to nie jest spis z serwera wydań.");

            var releases = ReleaseCatalog.ParseIndex(text);
            if (releases.Count == 0)
            {
                // Strona logowania podana z kodem 200 (np. przez pośrednika) albo pusty katalog — w żadnym
                // przypadku nie wolno uznać tego za „masz najnowszą wersję”.
                return text.Contains("cloudflareaccess", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Cloudflare Access", StringComparison.OrdinalIgnoreCase)
                    ? Result(UpdateCheckOutcome.NeedsLogin, null, NeedsLoginMessage)
                    : Result(UpdateCheckOutcome.Unavailable, null, "Spis wydań (INDEX.md) nie zawiera żadnego instalatora programu.");
            }

            var latest = ReleaseCatalog.Latest(releases)!;
            var outcome = latest.Version > installed ? UpdateCheckOutcome.Available : UpdateCheckOutcome.UpToDate;
            return Result(outcome, latest, null) with { Releases = releases };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result(UpdateCheckOutcome.Unavailable, null,
                $"Przekroczono czas oczekiwania na katalog wydań ({_indexTimeout.TotalSeconds:0} s).");
        }
        catch (Exception ex)
        {
            // Każdy inny błąd (sieć, TLS, pośrednik, nieczytelna treść) to „katalog niedostępny” — kontrakt
            // IUpdateSource: monitoring maszyn nie może się wywrócić przez serwer wydań.
            if (UpdateCoordinator.IsOrdinaryTransferError(ex))
                _logger?.LogWarning("Nie udało się odczytać katalogu wydań {Uri}: {Error}", uri, ex.Message);
            else
                _logger?.LogWarning(ex, "Nie udało się odczytać katalogu wydań {Uri}.", uri);
            return Result(UpdateCheckOutcome.Unavailable, null, "Katalog wydań jest niedostępny: " + ex.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Plik powstaje jako <c>&lt;nazwa&gt;.part</c> i dopiero po zgodności rozmiaru i SHA-256 dostaje właściwą
    /// nazwę — dzięki temu w katalogu nigdy nie leży niezweryfikowany <c>.exe</c>, który ktoś (albo
    /// pomocnik instalacji) mógłby uruchomić. Inne <c>*.exe</c>/<c>*.part</c> w katalogu (starsze wersje,
    /// przerwane pobrania) są usuwane, żeby katalog stanu nie rósł z każdym wydaniem.
    /// </remarks>
    /// <exception cref="ArgumentException">Nazwa pliku nie jest nazwą instalatora (np. zawiera ścieżkę).</exception>
    /// <exception cref="InvalidDataException">Rozmiar albo suma SHA-256 nie zgadza się ze spisem.</exception>
    /// <exception cref="HttpRequestException">Serwer odmówił pliku (logowanie Access, inny kod HTTP).</exception>
    /// <exception cref="TimeoutException">Pobieranie przekroczyło limit czasu.</exception>
    public async Task<string> DownloadAsync(ReleaseEntry release, string targetDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (!ReleaseCatalog.TryParseFileVersion(release.FileName, out _) || Path.GetFileName(release.FileName) != release.FileName)
            throw new ArgumentException($"„{release.FileName}” nie jest nazwą pliku instalatora programu.", nameof(release));
        if (_baseUri is null)
            throw new InvalidOperationException($"Nieprawidłowy adres katalogu wydań (updates.url): '{_config.Url}'.");
        if (release.SizeBytes is <= 0 or > MaxDownloadBytes)
            throw new InvalidDataException($"Rozmiar wydania {release.FileName} w spisie ({release.SizeBytes} B) jest nieprawidłowy.");
        string expectedSha = (release.Sha256 ?? "").ToLowerInvariant();

        Directory.CreateDirectory(targetDirectory);
        string finalPath = Path.Combine(Path.GetFullPath(targetDirectory), release.FileName);
        string partPath = finalPath + ".part";
        RemoveOtherDownloads(targetDirectory, finalPath);

        if (File.Exists(finalPath) && await MatchesAsync(finalPath, release.SizeBytes, expectedSha, ct).ConfigureAwait(false))
        {
            progress?.Report(1.0);
            return finalPath;
        }

        var uri = new Uri(_baseUri, Uri.EscapeDataString(release.FileName));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_downloadTimeout);
        try
        {
            using var request = CreateRequest(uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (IsLoginResponse(response)) throw new HttpRequestException(NeedsLoginMessage, null, response.StatusCode);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException(
                    $"Serwer wydań odmówił pliku {release.FileName}: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
            long? announced = response.Content.Headers.ContentLength;
            if (announced is { } length && length != release.SizeBytes)
                throw new InvalidDataException(
                    $"Serwer podaje inny rozmiar pliku {release.FileName} ({length} B) niż spis wydań ({release.SizeBytes} B) — plik odrzucono.");

            progress?.Report(0.0);
            long total = announced ?? release.SizeBytes;
            long written = 0;
            double lastReported = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > release.SizeBytes)
                        throw new InvalidDataException(
                            $"Pobierany plik {release.FileName} jest większy niż podaje spis wydań ({release.SizeBytes} B) — plik odrzucono.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    double fraction = Math.Clamp((double)written / total, 0.0, 1.0);
                    if (fraction - lastReported >= 0.01) { progress?.Report(fraction); lastReported = fraction; }
                }
                await target.FlushAsync(timeout.Token).ConfigureAwait(false);
            }

            string actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (written != release.SizeBytes)
                throw new InvalidDataException(
                    $"Pobrany plik {release.FileName} ma {written} B, a spis wydań podaje {release.SizeBytes} B — plik odrzucono.");
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Pobrany plik {release.FileName} ma inną sumę kontrolną SHA-256 niż podana w spisie wydań — plik odrzucono.");

            File.Move(partPath, finalPath, overwrite: true);
            progress?.Report(1.0);
            _logger?.LogInformation("Pobrano {File} ({Bytes} B, SHA-256 zgodne).", release.FileName, written);
            return finalPath;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(partPath);
            throw new TimeoutException($"Przekroczono czas pobierania pliku {release.FileName} ({_downloadTimeout.TotalMinutes:0} min).");
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private UpdateCheckResult Result(UpdateCheckOutcome outcome, ReleaseEntry? latest, string? error) =>
        new(outcome, latest, error, _clock.UtcNow);

    private HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        string? clientId = _config.AccessClientId?.Trim();
        string? secret = _accessSecret()?.Trim();
        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(secret))
        {
            request.Headers.TryAddWithoutValidation("CF-Access-Client-Id", clientId);
            request.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", secret);
        }
        return request;
    }

    /// <summary>
    /// Przekierowanie (3xx — Access kieruje na <c>*.cloudflareaccess.com</c>) albo 401/403 (token odrzucony).
    /// Każde 3xx traktujemy jak logowanie: katalog plików nie ma innych przekierowań, a podążanie za nim
    /// i tak dałoby stronę HTML zamiast spisu.
    /// </summary>
    private static bool IsLoginResponse(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) return true;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
        return response.Headers.Location?.Host.Contains("cloudflareaccess.com", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Czyta treść jako UTF-8, ale najwyżej <paramref name="limit"/> bajtów; więcej = <see langword="null"/>.</summary>
    private static async Task<string?> ReadLimitedTextAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static async Task<bool> MatchesAsync(string path, long size, string sha, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length != size) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            byte[] actual = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(actual), sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RemoveOtherDownloads(string directory, string keep)
    {
        foreach (string pattern in new[] { "*.exe", "*.part" })
        {
            foreach (string file in Directory.EnumerateFiles(directory, pattern))
            {
                if (string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryDelete(file)) _logger?.LogWarning("Nie udało się usunąć starego pliku aktualizacji {File}.", file);
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
