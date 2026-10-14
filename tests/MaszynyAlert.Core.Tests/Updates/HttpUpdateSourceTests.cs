using System.Net;
using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Updates;

/// <summary>
/// Klient katalogu wydań przez prawdziwy <see cref="HttpClient"/> na podstawionym
/// <see cref="HttpMessageHandler"/> — bez sieci. Katalog jest za Cloudflare Access, więc najważniejsze są:
/// nagłówki tokenu usługi, rozpoznanie przekierowania na logowanie i to, że ŻADEN błąd sieci nie wylatuje
/// do programu (monitoring maszyn ma działać dalej bez względu na serwer wydań).
/// </summary>
public sealed class HttpUpdateSourceTests : IDisposable
{
    private const string FileName = "MaszynyAlert-Setup-0.4.26.0.exe";
    private readonly TempDir _dir = new("updates-http");
    private readonly FakeClock _clock = new();
    private readonly FakeHandler _handler = new();
    private string? _secret = "sekret-tokenu";

    private static readonly byte[] Payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("instalator-", 5000)));
    private static readonly string PayloadSha = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    private HttpUpdateSource Source(UpdatesConfig? config = null, TimeSpan? timeout = null) =>
        new(_handler, config ?? new UpdatesConfig { Url = "https://wydania.example.test/maszyny-alert", AccessClientId = "id-tokenu.access" },
            () => _secret, _clock, timeout: timeout);

    private static ReleaseEntry Release(long? size = null, string? sha = null) =>
        new(FileName, new Version(0, 4, 26, 0), size ?? Payload.Length, sha ?? PayloadSha, null);

    private static string Index(string sha = "1617749400aa0000000000000000000000000000000000000000000000000001") =>
        ReleaseCatalogTests.Index(
            ("MaszynyAlert-Setup-0.4.22.0.exe", 100, sha, "2026-10-02T18:02:31Z"),
            (FileName, 200, sha, "2026-10-03T08:00:00Z"));

    [Fact]
    public async Task IndexIsRequestedWithTheAccessServiceTokenHeaders()
    {
        _handler.Respond = _ => Ok(Index());

        await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://wydania.example.test/maszyny-alert/INDEX.md", request.Uri.ToString());
        Assert.Equal("id-tokenu.access", request.Headers["CF-Access-Client-Id"]);
        Assert.Equal("sekret-tokenu", request.Headers["CF-Access-Client-Secret"]);
    }

    [Fact]
    public async Task UrlWithATrailingSlashGivesTheSameAddress()
    {
        _handler.Respond = _ => Ok(Index());

        await Source(new UpdatesConfig { Url = "https://wydania.example.test/maszyny-alert/" }).CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal("https://wydania.example.test/maszyny-alert/INDEX.md", Assert.Single(_handler.Requests).Uri.ToString());
    }

    [Fact]
    public async Task WithoutTheSecretNoAccessHeadersAreSent()
    {
        _secret = null;
        _handler.Respond = _ => Ok(Index());

        await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        var request = Assert.Single(_handler.Requests);
        Assert.False(request.Headers.ContainsKey("CF-Access-Client-Id"));
        Assert.False(request.Headers.ContainsKey("CF-Access-Client-Secret"));
    }

    [Fact]
    public async Task SecretIsReadAtEveryRequestSoATokenAddedLaterWorksWithoutARestart()
    {
        _secret = null;
        _handler.Respond = _ => Ok(Index());
        var source = Source();
        await source.CheckAsync(new Version(0, 4, 22, 0), default);

        _secret = "nowy-sekret";
        await source.CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal("nowy-sekret", _handler.Requests[1].Headers["CF-Access-Client-Secret"]);
    }

    [Fact]
    public async Task NewerReleaseInTheIndexIsAvailable()
    {
        _handler.Respond = _ => Ok(Index());

        var result = await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal(FileName, result.Latest!.FileName);
        Assert.Equal(_clock.UtcNow, result.CheckedAt);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Releases.Count);
    }

    [Fact]
    public async Task SameOrOlderReleaseMeansUpToDate()
    {
        _handler.Respond = _ => Ok(Index());

        var result = await Source().CheckAsync(new Version(0, 4, 26, 0), default);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Equal(new Version(0, 4, 26, 0), result.Latest!.Version);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RedirectToTheLoginPageMeansTheCatalogNeedsLogin(HttpStatusCode status)
    {
        _handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Found)
                response.Headers.Location = new Uri("https://mbuas.cloudflareaccess.com/cdn-cgi/access/login/wydania");
            return response;
        };

        var result = await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal(UpdateCheckOutcome.NeedsLogin, result.Outcome);
        Assert.Null(result.Latest);
        Assert.Contains("Cloudflare Access", result.Error, StringComparison.Ordinal);
        Assert.Contains("Panel", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerErrorMeansUnavailable()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var result = await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal(UpdateCheckOutcome.Unavailable, result.Outcome);
        Assert.Contains("502", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkFailureMeansUnavailableAndNeverThrows()
    {
        _handler.Respond = _ => throw new HttpRequestException("Nie można rozwiązać nazwy hosta");

        var result = await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal(UpdateCheckOutcome.Unavailable, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task TimeoutMeansUnavailable()
    {
        _handler.RespondAsync = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok(Index());
        };

        var result = await Source(timeout: TimeSpan.FromMilliseconds(100)).CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal(UpdateCheckOutcome.Unavailable, result.Outcome);
        Assert.Contains("czas", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        _handler.RespondAsync = async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok(Index());
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Source().CheckAsync(new Version(0, 4, 22, 0), cts.Token));
    }

    [Fact]
    public async Task IndexWithoutAnyReleaseIsNotTakenAsUpToDate()
    {
        _handler.Respond = _ => Ok("<html>Cloudflare Access — Sign in</html>");

        var result = await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.NotEqual(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.NotEqual(UpdateCheckOutcome.Available, result.Outcome);
    }

    [Fact]
    public async Task InvalidCatalogAddressGivesUnavailableInsteadOfBreakingTheStart()
    {
        // Konstruktor nie może wywrócić startu programu — nawet gdy ktoś ominie walidację config.json.
        var source = Source(new UpdatesConfig { Url = "nie adres" });

        var result = await source.CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal(UpdateCheckOutcome.Unavailable, result.Outcome);
        Assert.Contains("updates.url", result.Error, StringComparison.Ordinal);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task OversizedIndexIsRefused()
    {
        _handler.Respond = _ => Ok(Index() + new string('x', 2 * 1024 * 1024));

        var result = await Source().CheckAsync(new Version(0, 4, 22, 0), default);

        Assert.Equal(UpdateCheckOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task InstalledOldNameSeesTheNewerFileUnderTheNewNameAsAvailable()
    {
        const string newer = "VncAlert-Setup-0.4.160.0.exe";
        _handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("INDEX.md", StringComparison.Ordinal)
            ? Ok(ReleaseCatalogTests.Index(
                ("MaszynyAlert-Setup-0.4.148.0.exe", 100, "1617749400aa0000000000000000000000000000000000000000000000000001", "2026-10-07T17:20:00Z"),
                (newer, Payload.Length, PayloadSha, "2026-10-09T12:00:00Z")))
            : OkBytes(Payload);

        var check = await Source().CheckAsync(new Version(0, 4, 148, 0), default);

        Assert.Equal(UpdateCheckOutcome.Available, check.Outcome);
        Assert.Equal(newer, check.Latest!.FileName);
        Assert.Equal(2, check.Releases.Count);

        string path = await Source().DownloadAsync(check.Latest, _dir.Path, null, default);

        Assert.Equal(Path.Combine(_dir.Path, newer), path);
        Assert.Equal("https://wydania.example.test/maszyny-alert/" + newer, _handler.Requests[^1].Uri.ToString());
    }

    [Fact]
    public async Task DownloadVerifiesTheChecksumAndReportsProgress()
    {
        _handler.Respond = _ => OkBytes(Payload);
        var reports = new List<double>();

        string path = await Source().DownloadAsync(Release(), _dir.Path, new SyncProgress(reports.Add), default);

        Assert.Equal(Path.Combine(_dir.Path, FileName), path);
        Assert.Equal(Payload, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal("https://wydania.example.test/maszyny-alert/" + FileName, Assert.Single(_handler.Requests).Uri.ToString());
        Assert.Equal("sekret-tokenu", _handler.Requests[0].Headers["CF-Access-Client-Secret"]);
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
        Assert.All(reports, p => Assert.InRange(p, 0.0, 1.0));
    }

    [Fact]
    public async Task DownloadWithAWrongChecksumIsDeletedAndRefused()
    {
        _handler.Respond = _ => OkBytes(Payload);
        string wrong = new('a', 64);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Source().DownloadAsync(Release(sha: wrong), _dir.Path, null, default));

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task DownloadWithAWrongSizeIsDeletedAndRefused()
    {
        _handler.Respond = _ => OkBytes(Payload);

        await Assert.ThrowsAsync<InvalidDataException>(() => Source().DownloadAsync(Release(size: Payload.Length + 10), _dir.Path, null, default));

        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task FileAlreadyDownloadedIsReturnedWithoutAnyRequest()
    {
        File.WriteAllBytes(Path.Combine(_dir.Path, FileName), Payload);

        string path = await Source().DownloadAsync(Release(), _dir.Path, null, default);

        Assert.Equal(Path.Combine(_dir.Path, FileName), path);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task DamagedFileFromAnEarlierDownloadIsDownloadedAgain()
    {
        File.WriteAllBytes(Path.Combine(_dir.Path, FileName), Encoding.ASCII.GetBytes("ucięty"));
        _handler.Respond = _ => OkBytes(Payload);

        string path = await Source().DownloadAsync(Release(), _dir.Path, null, default);

        Assert.Equal(Payload, File.ReadAllBytes(path));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task OlderInstallersAndLeftoverPartsAreRemovedButOtherFilesStay()
    {
        File.WriteAllBytes(Path.Combine(_dir.Path, "MaszynyAlert-Setup-0.4.22.0.exe"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir.Path, "MaszynyAlert-Setup-0.4.25.0.exe.part"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(_dir.Path, "pending-install.json"), "{}");
        _handler.Respond = _ => OkBytes(Payload);

        await Source().DownloadAsync(Release(), _dir.Path, null, default);

        Assert.Equal(new[] { FileName, "pending-install.json" },
            Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task DownloadBehindTheLoginPageThrowsInsteadOfSavingTheLoginPage()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://mbuas.cloudflareaccess.com/cdn-cgi/access/login") },
        };

        await Assert.ThrowsAnyAsync<Exception>(() => Source().DownloadAsync(Release(), _dir.Path, null, default));

        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task ReleaseNameThatCouldEscapeTheDirectoryIsRefused()
    {
        var evil = new ReleaseEntry("../MaszynyAlert-Setup-0.4.26.0.exe", new Version(0, 4, 26, 0), 10, PayloadSha, null);

        await Assert.ThrowsAsync<ArgumentException>(() => Source().DownloadAsync(evil, _dir.Path, null, default));
        Assert.Empty(_handler.Requests);
    }

    private static HttpResponseMessage Ok(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/markdown") };

    private static HttpResponseMessage OkBytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public void Dispose() => _dir.Dispose();

    /// <summary><see cref="Progress{T}"/> zgłasza asynchronicznie — w teście potrzebny jest zapis od razu.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Zapamiętany kształt żądania (nagłówki skopiowane, bo żądanie jest zwalniane po wysłaniu).</summary>
    internal sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers);

    internal sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; set; }
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(new RecordedRequest(request.RequestUri!,
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            }
            if (RespondAsync is { } respondAsync) return await respondAsync(request, cancellationToken);
            if (Respond is { } respond) return respond(request);
            throw new InvalidOperationException("Test nie przewidział żądania " + request.RequestUri);
        }
    }
}
