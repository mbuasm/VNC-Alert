using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Updates;

public sealed class UpdateTaskVerifierTests : IDisposable
{
    private static readonly Version Installed = new(0, 4, 79, 0);
    private static readonly Version Newer = new(0, 4, 85, 0);
    private const string NewerFile = "MaszynyAlert-Setup-0.4.85.0.exe";
    private const string RequestId = "0123456789abcdef0123456789abcdef";

    private readonly TempDir _data = new("update-task");
    private readonly FakeClock _clock = new();
    private readonly CatalogSource _source = new();

    public UpdateTaskVerifierTests()
    {
        _source.Releases = [Entry(Installed), Entry(Newer)];
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Admin);
    }

    public void Dispose() => _data.Dispose();

    private string Downloads => UpdateTaskContract.DownloadDirectory(_data.Path);
    private string Admin => UpdateTaskContract.AdminDirectory(_data.Path);
    private string PendingPath => Path.Combine(Admin, UpdateCoordinator.PendingInstallFileName);

    private static byte[] SetupBytes(Version version) => Encoding.UTF8.GetBytes($"oficjalny instalator {version}");

    private static ReleaseEntry Entry(Version version, string? fileName = null) =>
        new(fileName ?? $"MaszynyAlert-Setup-{version}.exe", version, SetupBytes(version).Length,
            Convert.ToHexString(SHA256.HashData(SetupBytes(version))).ToLowerInvariant(), null);

    private UpdateTaskVerifier Verifier(UpdatesConfig? config = null) =>
        new(_data.Path, Installed, config ?? new UpdatesConfig(), _source, _clock);

    private void Request(string fileName, string requestId = RequestId) =>
        File.WriteAllText(Path.Combine(Downloads, UpdateTaskContract.RequestFileName),
            JsonSerializer.Serialize(new { requestId, fileName }));

    private void Download(string fileName, byte[] content) => File.WriteAllBytes(Path.Combine(Downloads, fileName), content);

    [Fact]
    public async Task OficjalnyNowszyPlik_KopiaWKataloguAdministratorowIZnacznikProby()
    {
        Download(NewerFile, SetupBytes(Newer));
        Request(NewerFile);

        var result = await Verifier().VerifyAsync(default);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(RequestId, result.RequestId);
        Assert.Equal("0.4.85.0", result.Version);
        string runDirectory = Path.GetDirectoryName(result.SetupPath!)!;
        Assert.Equal(Admin, Path.GetDirectoryName(runDirectory));
        Assert.StartsWith("run-", Path.GetFileName(runDirectory), StringComparison.Ordinal);
        Assert.Equal(NewerFile, Path.GetFileName(result.SetupPath));
        Assert.Single(Directory.EnumerateFileSystemEntries(runDirectory));
        Assert.Equal(Entry(Newer).Sha256, result.Sha256);
        Assert.Equal(SetupBytes(Newer), File.ReadAllBytes(result.SetupPath!));
        Assert.Contains("0.4.85.0", File.ReadAllText(PendingPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlikPodmienionyPrzezOperatora_OdrzuconyIKopiaUsunieta()
    {
        // Ta sama długość, inna treść: tylko suma ze spisu odczytanego przez zadanie może to wykryć.
        byte[] forged = SetupBytes(Newer);
        forged[0] ^= 0x20;
        Download(NewerFile, forged);
        Request(NewerFile);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Contains("SHA-256", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Admin, "*.exe", SearchOption.AllDirectories));
        string forgedSha = Convert.ToHexString(SHA256.HashData(forged)).ToLowerInvariant();
        Assert.DoesNotContain(forgedSha, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task PlikInnejDlugosciNizWSpisie_Odrzucony()
    {
        Download(NewerFile, [.. SetupBytes(Newer), 0x00]);
        Request(NewerFile);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Empty(Directory.EnumerateFiles(Admin, "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PlikKrotszyNizWSpisie_OdmowaBezRozmiaruOdczytanegoPliku()
    {
        byte[] shorter = SetupBytes(Newer)[..7];
        Download(NewerFile, shorter);
        Request(NewerFile);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.DoesNotContain($"{shorter.Length} B", result.Message, StringComparison.Ordinal);
        Assert.Contains("spisem", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KatalogZadania_PozaKatalogiemOperatora_PobraniaJakPrzedP7b()
    {
        string data = Path.Combine(Path.GetTempPath(), "dane");
        Assert.Equal(Path.Combine(data, "updates-admin"), UpdateTaskContract.AdminDirectory(data));
        Assert.Equal(Path.Combine(data, "state", "updates"), UpdateTaskContract.DownloadDirectory(data));
    }

    [Fact]
    public async Task KazdyPrzebiegMaWlasnyKatalog_StarePrzebiegiUsuniete()
    {
        Download(NewerFile, SetupBytes(Newer));
        Request(NewerFile);
        var first = await Verifier().VerifyAsync(default);
        var second = await Verifier().VerifyAsync(default);

        Assert.True(first.Accepted && second.Accepted);
        Assert.NotEqual(Path.GetDirectoryName(first.SetupPath), Path.GetDirectoryName(second.SetupPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(first.SetupPath)), "Katalog poprzedniego przebiegu ma zniknąć.");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://localhost/maszyny-alert/")]
    public async Task ZadanieWymagaHttpsBezWyjatkuDlaLocalhost(string url)
    {
        Download(NewerFile, SetupBytes(Newer));
        Request(NewerFile);

        var result = await Verifier(new UpdatesConfig { Url = url }).VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Contains("https", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, _source.CheckCalls);
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Users\Public\MaszynyAlert-Setup-0.4.85.0.exe")]
    [InlineData("MaszynyAlert-Setup-0.4.85.0.exe:ads")]
    [InlineData("zlosliwy.exe")]
    [InlineData("")]
    public async Task NazwaSpozaKonwencjiWydan_OdrzuconaBezSieganiaDoSpisu(string fileName)
    {
        Request(fileName);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Equal(0, _source.CheckCalls);
        Assert.Equal(RequestId, result.RequestId);
    }

    [Fact]
    public async Task WersjaNieNowszaOdZainstalowanej_Odrzucona()
    {
        // Zadanie nie służy do ponownego uruchamiania instalatora ani do cofania wersji.
        string sameFile = $"MaszynyAlert-Setup-{Installed}.exe";
        Download(sameFile, SetupBytes(Installed));
        Request(sameFile);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Contains("nowsz", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task NowaNazwaPlikuWMieszanymSpisie_Przyjeta()
    {
        const string newer = "VncAlert-Setup-0.4.85.0.exe";
        _source.Releases = [Entry(Installed), Entry(Newer, newer)];
        Download(newer, SetupBytes(Newer));
        Request(newer);

        var result = await Verifier().VerifyAsync(default);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(newer, result.FileName);
        Assert.Equal(newer, Path.GetFileName(result.SetupPath));
        Assert.Equal("0.4.85.0", result.Version);
    }

    [Fact]
    public async Task NowszaWersjaSpozaSpisu_Odrzucona()
    {
        const string unknown = "MaszynyAlert-Setup-0.4.99.0.exe";
        Download(unknown, SetupBytes(new Version(0, 4, 99, 0)));
        Request(unknown);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Contains("spisie", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Admin, unknown)));
    }

    [Fact]
    public async Task SpisNiedostepny_Odrzucone()
    {
        _source.Outcome = UpdateCheckOutcome.Unavailable;
        Download(NewerFile, SetupBytes(Newer));
        Request(NewerFile);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task AktualizacjeWylaczoneWKonfiguracji_Odrzucone()
    {
        Download(NewerFile, SetupBytes(Newer));
        Request(NewerFile);

        var result = await Verifier(new UpdatesConfig { Enabled = false }).VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Equal(0, _source.CheckCalls);
    }

    [Theory]
    [InlineData("{ to nie jest JSON")]
    [InlineData("{\"requestId\":\"nie-guid\",\"fileName\":\"MaszynyAlert-Setup-0.4.85.0.exe\"}")]
    [InlineData("{\"fileName\":\"MaszynyAlert-Setup-0.4.85.0.exe\"}")]
    public async Task UszkodzoneZadanie_Odrzucone(string json)
    {
        Download(NewerFile, SetupBytes(Newer));
        File.WriteAllText(Path.Combine(Downloads, UpdateTaskContract.RequestFileName), json);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task BrakZadania_Odrzucone()
    {
        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
        Assert.Null(result.RequestId);
    }

    [Fact]
    public async Task ZaDuzyPlikZadania_Odrzucony()
    {
        File.WriteAllText(Path.Combine(Downloads, UpdateTaskContract.RequestFileName), new string(' ', UpdateTaskRequest.MaxBytes + 1));

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("state/updates")]
    public async Task KatalogPobranPrzekierowany_OdrzuconyBezCzytaniaPlikow(string redirected)
    {
        string elsewhere = Path.Combine(_data.Path, "gdzie-indziej");
        string inside = redirected == "state" ? Path.Combine(elsewhere, "updates") : elsewhere;
        Directory.CreateDirectory(inside);
        File.WriteAllBytes(Path.Combine(inside, NewerFile), SetupBytes(Newer));
        File.WriteAllText(Path.Combine(inside, UpdateTaskContract.RequestFileName),
            JsonSerializer.Serialize(new { requestId = RequestId, fileName = NewerFile }));
        string link = Path.Combine(_data.Path, redirected.Replace('/', Path.DirectorySeparatorChar));
        Directory.Delete(Downloads, recursive: true);
        if (redirected == "state") Directory.Delete(Path.GetDirectoryName(Downloads)!, recursive: true);
        CreateDirectoryLink(link, elsewhere);

        var result = await Verifier().VerifyAsync(default);

        Assert.False(result.Accepted, result.Message);
        Assert.Null(result.RequestId);
        Assert.Equal(0, _source.CheckCalls);
        Assert.Empty(Directory.EnumerateDirectories(Admin, "run-*"));
    }

    [Fact]
    public async Task ProblemyZPlikiemZadania_JedenWspolnyPowod()
    {
        string requestPath = Path.Combine(Downloads, UpdateTaskContract.RequestFileName);
        string missing = (await Verifier().VerifyAsync(default)).Message;
        File.WriteAllText(requestPath, new string(' ', UpdateTaskRequest.MaxBytes + 1));
        string tooBig = (await Verifier().VerifyAsync(default)).Message;
        File.WriteAllText(requestPath, "{ to nie jest JSON");
        string notJson = (await Verifier().VerifyAsync(default)).Message;

        Assert.Equal(missing, tooBig);
        Assert.Equal(missing, notJson);
        Assert.Contains("Brak", missing, StringComparison.Ordinal);
    }

    /// <summary>Przekierowanie katalogu: na Windows połączenie (junction — bez uprawnień administratora), gdzie indziej symlink.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public async Task StareKopieInstalatorowWKataloguZadania_SaUsuwane()
    {
        // Kopia z nieudanej próby nie może czekać w katalogu zadania na „ponowne użycie”.
        File.WriteAllBytes(Path.Combine(Admin, "MaszynyAlert-Setup-0.4.80.0.exe"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(Admin, "run-stary"));
        File.WriteAllBytes(Path.Combine(Admin, "run-stary", "version.dll"), [1, 2, 3]);
        Download(NewerFile, SetupBytes(Newer));
        Request(NewerFile);

        var result = await Verifier().VerifyAsync(default);

        Assert.True(result.Accepted, result.Message);
        Assert.False(File.Exists(Path.Combine(Admin, "MaszynyAlert-Setup-0.4.80.0.exe")));
        Assert.False(Directory.Exists(Path.Combine(Admin, "run-stary")));
    }

    [Fact]
    public async Task KopiaPowstajeFunkcjaPlikuChronionego()
    {
        // Na Windows funkcja nadaje kopii ACL bez operatora — zanim trafi do niej choć jeden bajt z katalogu operatora.
        var created = new List<string>();
        Download(NewerFile, SetupBytes(Newer));
        Request(NewerFile);
        var verifier = new UpdateTaskVerifier(_data.Path, Installed, new UpdatesConfig(), _source, _clock,
            path => { created.Add(path); return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); });

        var result = await verifier.VerifyAsync(default);

        Assert.True(result.Accepted, result.Message);
        string copy = Assert.Single(created);
        Assert.Equal(NewerFile, Path.GetFileName(copy));
        Assert.Equal(Admin, Path.GetDirectoryName(Path.GetDirectoryName(copy)));
    }

    [Fact]
    public void ZadanieIWynik_DajaSieOdczytacPoZapisie()
    {
        var request = new UpdateTaskRequest(RequestId, NewerFile);
        Assert.True(UpdateTaskRequest.TryParse(request.ToJson(), out var parsed, out string error), error);
        Assert.Equal(request, parsed);

        var verification = new UpdateTaskVerification { Accepted = true, RequestId = RequestId, Version = "0.4.85.0", Message = "ok" };
        using var json = JsonDocument.Parse(verification.ToJson());
        Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(RequestId, json.RootElement.GetProperty("requestId").GetString());
    }

    private sealed class CatalogSource : IUpdateSource
    {
        private int _checkCalls;
        public IReadOnlyList<ReleaseEntry> Releases { get; set; } = [];
        public UpdateCheckOutcome? Outcome { get; set; }
        public int CheckCalls => Volatile.Read(ref _checkCalls);

        public Task<UpdateCheckResult> CheckAsync(Version installed, CancellationToken ct)
        {
            Interlocked.Increment(ref _checkCalls);
            if (Outcome is UpdateCheckOutcome.Unavailable or UpdateCheckOutcome.NeedsLogin)
                return Task.FromResult(new UpdateCheckResult(Outcome.Value, null, "katalog niedostępny", DateTimeOffset.UnixEpoch));
            var latest = ReleaseCatalog.Latest(Releases);
            var outcome = latest is not null && latest.Version > installed ? UpdateCheckOutcome.Available : UpdateCheckOutcome.UpToDate;
            return Task.FromResult(new UpdateCheckResult(outcome, latest, null, DateTimeOffset.UnixEpoch) { Releases = Releases });
        }

        public Task<string> DownloadAsync(ReleaseEntry release, string targetDirectory, IProgress<double>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Zadanie nie pobiera plików — sprawdza to, co pobrał program.");
    }
}
