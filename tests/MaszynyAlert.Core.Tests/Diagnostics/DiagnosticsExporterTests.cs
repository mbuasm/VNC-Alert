using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Diagnostics;

public sealed class DiagnosticsExporterTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Export_PakujeTylkoAllowlistyIKanoniczneMetadane()
    {
        using var dir = new TempDir("diagnostics-allowlist");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        string nestedCaptures = CreateDirectory(Path.Combine(captures, "aidite1", "2026-09-12"));
        string nestedLogs = CreateDirectory(Path.Combine(logs, "nested"));
        File.WriteAllText(Path.Combine(logs, "app-20260912.log"), "bezpieczny log");
        File.WriteAllText(Path.Combine(logs, "inne.log"), "nie pakuj");
        File.WriteAllText(Path.Combine(logs, "config.json"), "SEKRET");
        File.WriteAllText(Path.Combine(logs, "Hala.vncgroup"), "TAJNY-KLUCZ-GRUPY");
        File.WriteAllText(Path.Combine(captures, "Hala.vncgroup"), "TAJNY-KLUCZ-GRUPY");
        File.WriteAllText(Path.Combine(nestedLogs, "app-zagniezdzony.log"), "nie pakuj rekursywnie");
        File.WriteAllBytes(Path.Combine(nestedCaptures, "alarm.png"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(captures, "rules.json"), "SEKRET");
        File.WriteAllText(Path.Combine(captures, "obcy.jpg"), "nie pakuj");
        string testScreen = CreateDirectory(Path.Combine(captures, "test-ekran"));
        File.WriteAllBytes(Path.Combine(testScreen, "symulator.png"), [5, 6, 7]);
        string history = dir.File("history.csv");
        File.WriteAllText(history, "czas;zdarzenie\n");

        DiagnosticsExportResult result = new DiagnosticsExporter().Export(
            dir.File("diagnostyka.zip"),
            Request(logs, captures, history));

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Equal(7, result.FilesIncluded);
        Assert.Contains("build-info.json", names);
        Assert.Contains("status.json", names);
        Assert.Contains("manifest.json", names);
        Assert.Contains("formularz-odbioru.md", names);
        Assert.Contains("logs/app-20260912.log", names);
        Assert.Contains("captures/aidite1/2026-09-12/alarm.png", names);
        Assert.Contains("history/history.csv", names);
        Assert.DoesNotContain(names, name => name.Contains("config", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("rules", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.EndsWith(".vncgroup", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("zagniezdzony", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("test-ekran", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));

        using JsonDocument build = ReadJson(archive, "build-info.json");
        Assert.Equal($"0.1.0+{Commit}", build.RootElement.GetProperty("version").GetString());
        Assert.Equal(Commit, build.RootElement.GetProperty("commit").GetString());

        using JsonDocument status = ReadJson(archive, "status.json");
        Assert.Equal("Obserwator", status.RootElement.GetProperty("role").GetString());
        Assert.False(status.RootElement.TryGetProperty("secret", out _));

        using JsonDocument manifest = ReadJson(archive, "manifest.json");
        Assert.Equal("NIE ODEBRANO", manifest.RootElement.GetProperty("acceptanceStatus").GetString());
        Assert.Contains(
            manifest.RootElement.GetProperty("omitted").EnumerateArray(),
            item => item.GetProperty("reason").GetString()!.Contains("allowlist", StringComparison.OrdinalIgnoreCase));

        string form = ReadText(archive, "formularz-odbioru.md");
        Assert.Contains("NIE ODEBRANO", form, StringComparison.Ordinal);
        Assert.Contains("NIE SPRAWDZONO", form, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_DolaczaLogIWynikNieudanejAktualizacjiAleNieInstalatorISkrypt()
    {
        using var dir = new TempDir("diagnostics-updates");
        string updates = CreateDirectory(dir.File(Path.Combine("state", "updates")));
        File.WriteAllText(Path.Combine(updates, "install-0.4.99.0.log"), "log Inno Setup");
        File.WriteAllText(Path.Combine(updates, "install-0.4.99.0.result.json"), "{\"exitCode\":4}");
        File.WriteAllText(Path.Combine(updates, "install-update.ps1"), "skrypt");
        File.WriteAllText(Path.Combine(updates, "MaszynyAlert-Setup-0.4.99.0.exe"), "duży instalator");
        File.WriteAllText(Path.Combine(updates, "pending-install.json"), "{}");

        DiagnosticsExportResult result = new DiagnosticsExporter().Export(
            dir.File("diagnostyka.zip"),
            Request(dir.File("logs"), dir.File("captures"), dir.File("history.csv")) with { UpdatesDirectory = updates });

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("updates/install-0.4.99.0.log", names);
        Assert.Contains("updates/install-0.4.99.0.result.json", names);
        Assert.DoesNotContain(names, name => name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("pending", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LogiZadaniaAktualizacji_ZKataloguZadania_TrafiajaDoEksportuObokPobran()
    {
        using var dir = new TempDir("diagnostics-update-task");
        string updates = CreateDirectory(dir.File(Path.Combine("state", "updates")));
        string admin = CreateDirectory(dir.File("updates-admin"));
        File.WriteAllText(Path.Combine(updates, "install-0.4.99.0.log"), "log z UAC");
        File.WriteAllText(Path.Combine(admin, "install-0.4.99.0.log"), "log z zadania");
        File.WriteAllText(Path.Combine(admin, "install-task.result.json"), "{\"exitCode\":6}");
        CreateDirectory(Path.Combine(admin, "run-1"));
        File.WriteAllText(Path.Combine(admin, "run-1", "MaszynyAlert-Setup-0.4.99.0.exe"), "kopia");

        DiagnosticsExportResult result = new DiagnosticsExporter().Export(
            dir.File("diagnostyka.zip"),
            Request(dir.File("logs"), dir.File("captures"), dir.File("history.csv"))
                with { UpdatesDirectory = updates, UpdateTaskDirectory = admin });

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("updates/install-0.4.99.0.log", names);
        Assert.Contains("updates-admin/install-0.4.99.0.log", names);
        Assert.Contains("updates-admin/install-task.result.json", names);
        Assert.DoesNotContain(names, name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Export_BrakKatalogowIHistorii_NadalTworzyUzytecznyZipZOstrzezeniami()
    {
        using var dir = new TempDir("diagnostics-missing");

        DiagnosticsExportResult result = new DiagnosticsExporter().Export(
            dir.File("diagnostyka.zip"),
            Request(dir.File("brak-logow"), dir.File("brak-zrzutow"), dir.File("brak-history.csv")));

        Assert.Equal(4, result.FilesIncluded);
        Assert.True(result.Warnings.Count >= 3);
        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(
            ["build-info.json", "status.json", "formularz-odbioru.md", "manifest.json"],
            archive.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public void Export_WybieraNajnowszePlikiPrzyLimicieLiczby()
    {
        using var dir = new TempDir("diagnostics-count-limit");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        string older = Path.Combine(logs, "app-starszy.log");
        string newer = Path.Combine(logs, "app-nowszy.log");
        File.WriteAllText(older, "stary");
        File.WriteAllText(newer, "nowy");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc));

        var exporter = new DiagnosticsExporter(new DiagnosticsExportOptions { MaxFiles = 5 });
        DiagnosticsExportResult result = exporter.Export(
            dir.File("diagnostyka.zip"),
            Request(logs, captures, dir.File("missing.csv")));

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.NotNull(archive.GetEntry("logs/app-nowszy.log"));
        Assert.Null(archive.GetEntry("logs/app-starszy.log"));
        Assert.Contains(result.Warnings, warning => warning.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Export_PomijaPlikPonadLimitIPodajePowodWManifescie()
    {
        using var dir = new TempDir("diagnostics-size-limit");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        File.WriteAllBytes(Path.Combine(logs, "app-duzy.log"), new byte[(8 * 1024) + 1]);
        File.WriteAllText(Path.Combine(logs, "app-maly.log"), "ok");
        var options = new DiagnosticsExportOptions
        {
            MaxFileBytes = 8 * 1024,
            MaxTotalBytes = 128 * 1024,
        };

        DiagnosticsExportResult result = new DiagnosticsExporter(options).Export(
            dir.File("diagnostyka.zip"),
            Request(logs, captures, dir.File("missing.csv")));

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Null(archive.GetEntry("logs/app-duzy.log"));
        Assert.NotNull(archive.GetEntry("logs/app-maly.log"));
        using JsonDocument manifest = ReadJson(archive, "manifest.json");
        Assert.Contains(
            manifest.RootElement.GetProperty("omitted").EnumerateArray(),
            item => item.GetProperty("source").GetString() == "logs/app-duzy.log" &&
                    item.GetProperty("reason").GetString()!.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Export_CzytaLogOtwartyPrzezAplikacje()
    {
        using var dir = new TempDir("diagnostics-open-log");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        string logPath = Path.Combine(logs, "app-otwarty.log");
        using var appLog = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        byte[] expected = Encoding.UTF8.GetBytes("log nadal otwarty");
        appLog.Write(expected);
        appLog.Flush();

        DiagnosticsExportResult result = new DiagnosticsExporter().Export(
            dir.File("diagnostyka.zip"),
            Request(logs, captures, dir.File("missing.csv")));

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        using Stream stream = archive.GetEntry("logs/app-otwarty.log")!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(expected, memory.ToArray());
    }

    [Fact]
    public void Export_IstniejacegoCeluNieNadpisuje()
    {
        using var dir = new TempDir("diagnostics-existing");
        string destination = dir.File("diagnostyka.zip");
        byte[] original = [9, 8, 7, 6];
        File.WriteAllBytes(destination, original);

        Assert.Throws<IOException>(() => new DiagnosticsExporter().Export(
            destination,
            Request(dir.File("logs"), dir.File("captures"), dir.File("history.csv"))));
        Assert.Equal(original, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Export_AnulowanyPrzedStartemNieTworzyPliku()
    {
        using var dir = new TempDir("diagnostics-cancelled");
        string destination = dir.File("diagnostyka.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new DiagnosticsExporter().Export(
            destination,
            Request(dir.File("logs"), dir.File("captures"), dir.File("history.csv")),
            cancellation.Token));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Export_NieprawidlowyStatusJsonNieZostawiaPozornegoSukcesu()
    {
        using var dir = new TempDir("diagnostics-json");
        string destination = dir.File("diagnostyka.zip");
        DiagnosticsExportRequest request = Request(dir.File("logs"), dir.File("captures"), dir.File("history.csv")) with
        {
            StatusJson = "{ to nie jest JSON",
        };

        Assert.Throws<InvalidDataException>(() => new DiagnosticsExporter().Export(destination, request));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Export_WpisyZipSaWzgledneIBezTraversal()
    {
        using var dir = new TempDir("diagnostics-paths");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(Path.Combine(dir.Path, "captures", "maszyna", "alarmy"));
        File.WriteAllBytes(Path.Combine(captures, "blad.png"), [137, 80, 78, 71]);

        DiagnosticsExportResult result = new DiagnosticsExporter().Export(
            dir.File("diagnostyka.zip"),
            Request(logs, dir.File("captures"), dir.File("missing.csv")));

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.All(archive.Entries, entry =>
        {
            Assert.False(Path.IsPathRooted(entry.FullName));
            Assert.DoesNotContain("\\", entry.FullName, StringComparison.Ordinal);
            Assert.DoesNotContain("../", entry.FullName, StringComparison.Ordinal);
            Assert.DoesNotContain("/../", entry.FullName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Export_NieWchodziWZagniezdzoneDowiazanie()
    {
        using var dir = new TempDir("diagnostics-link");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        string outside = CreateDirectory(dir.File("outside"));
        File.WriteAllBytes(Path.Combine(outside, "sekret.png"), [1, 2, 3]);
        string link = Path.Combine(captures, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        DiagnosticsExportResult result = new DiagnosticsExporter().Export(
            dir.File("diagnostyka.zip"),
            Request(logs, captures, dir.File("missing.csv")));

        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("sekret", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("reparse", StringComparison.OrdinalIgnoreCase));
        using JsonDocument manifest = ReadJson(archive, "manifest.json");
        Assert.Contains(
            manifest.RootElement.GetProperty("omitted").EnumerateArray(),
            item => item.GetProperty("reason").GetString()!.Contains("reparse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Export_LimitSkanuOgraniczaKandydatowIPodajeOstrzezenie()
    {
        using var dir = new TempDir("diagnostics-scan-limit");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        for (int index = 0; index < 10; index++)
        {
            File.WriteAllText(Path.Combine(logs, $"app-{index:D2}.log"), index.ToString());
        }

        var exporter = new DiagnosticsExporter(new DiagnosticsExportOptions { MaxScannedEntries = 2 });
        DiagnosticsExportResult result = exporter.Export(
            dir.File("diagnostyka.zip"),
            Request(logs, captures, dir.File("history.csv")));

        Assert.True(result.FilesIncluded <= 6);
        Assert.Contains(result.Warnings, warning => warning.Contains("sprawdzeniu 2", StringComparison.Ordinal));
        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        using JsonDocument manifest = ReadJson(archive, "manifest.json");
        Assert.Equal(2, manifest.RootElement.GetProperty("limits").GetProperty("maxScannedEntries").GetInt32());
    }

    [Fact]
    public void Export_PlikSkroconyPoOtwarciuPrzerywaEksportISprzataNiepelnyZip()
    {
        using var dir = new TempDir("diagnostics-truncated");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        string log = Path.Combine(logs, "app-biezacy.log");
        File.WriteAllBytes(log, new byte[4096]);
        string destination = dir.File("diagnostyka.zip");
        var options = new DiagnosticsExportOptions
        {
            BeforeCopyFileForTests = entryName =>
            {
                if (entryName == "logs/app-biezacy.log")
                {
                    File.WriteAllBytes(log, []);
                }
            },
        };

        Assert.Throws<EndOfStreamException>(() => new DiagnosticsExporter(options).Export(
            destination,
            Request(logs, captures, dir.File("history.csv"))));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Export_NiePakujeArchiwumDoSamegoSiebie()
    {
        using var dir = new TempDir("diagnostics-self");
        string logs = CreateDirectory(dir.File("logs"));
        string captures = CreateDirectory(dir.File("captures"));
        string destination = Path.Combine(logs, "app-diagnostyka.log");
        File.WriteAllText(destination, "istniejący cel");

        Assert.Throws<IOException>(() => new DiagnosticsExporter().Export(
            destination,
            Request(logs, captures, dir.File("missing.csv"))));
        Assert.Equal("istniejący cel", File.ReadAllText(destination));
    }

    [Fact]
    public void Export_OdrzucaCelUncPrzedProbaZapisu()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<NotSupportedException>(() => new DiagnosticsExporter().Export(
            @"\\nieistniejacy-serwer\udzial\diagnostyka.zip",
            Request(@"\\nieistniejacy-serwer\logs", @"\\nieistniejacy-serwer\captures", @"\\nieistniejacy-serwer\history.csv")));
    }

    [Fact]
    public void Export_OdrzucaCelPrzezDowiazanieINieZmieniaRzeczywistegoKatalogu()
    {
        using var dir = new TempDir("diagnostics-destination-link");
        string realDestination = CreateDirectory(dir.File("rzeczywisty-cel"));
        string sentinel = Path.Combine(realDestination, "istniejace-dane.txt");
        File.WriteAllText(sentinel, "nie zmieniaj");
        string destinationLink = dir.File("cel-przez-link");
        try
        {
            Directory.CreateSymbolicLink(destinationLink, realDestination);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        string archiveThroughLink = Path.Combine(destinationLink, "diagnostyka.zip");
        Assert.Throws<NotSupportedException>(() => new DiagnosticsExporter().Export(
            archiveThroughLink,
            Request(dir.File("logs"), dir.File("captures"), dir.File("history.csv"))));

        Assert.False(File.Exists(Path.Combine(realDestination, "diagnostyka.zip")));
        Assert.Equal("nie zmieniaj", File.ReadAllText(sentinel));
    }

    private static DiagnosticsExportRequest Request(string logs, string captures, string history) => new()
    {
        Build = BuildIdentity.FromInformationalVersion($"0.1.0+{Commit}"),
        StatusJson = """
                     {
                       "role": "Obserwator",
                       "machines": [{ "name": "Aidite 1", "connection": "połączono" }]
                     }
                     """,
        CreatedAt = new DateTimeOffset(2026, 9, 12, 12, 34, 56, TimeSpan.FromHours(2)),
        LogsDirectory = logs,
        CapturesDirectory = captures,
        HistoryPath = history,
    };

    private static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonDocument ReadJson(ZipArchive archive, string entryName) =>
        JsonDocument.Parse(ReadText(archive, entryName));

    private static string ReadText(ZipArchive archive, string entryName)
    {
        using Stream stream = archive.GetEntry(entryName)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
