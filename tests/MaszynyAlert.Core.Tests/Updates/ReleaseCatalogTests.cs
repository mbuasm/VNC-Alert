using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Updates;

public sealed class ReleaseCatalogTests
{
    private const string ShaA = "1617749400aa0000000000000000000000000000000000000000000000000001";
    private const string ShaB = "2617749400aa0000000000000000000000000000000000000000000000000002";
    private const string ShaC = "3617749400aa0000000000000000000000000000000000000000000000000003";

    /// <summary>Wierny odpowiednik pliku z serwera — wiersze posortowane `LC_ALL=C sort` po nazwie.</summary>
    internal static string Index(params (string File, long Size, string Sha, string Date)[] rows)
    {
        var lines = new List<string>
        {
            "# Wydania: maszyny-alert",
            "",
            "Plik generuje `wydanie-zglos` (2026-10-02T18:52:10Z) - nie edytowac recznie.",
            "",
            "| Plik | Rozmiar | SHA-256 | Data (UTC) |",
            "|---|---:|---|---|",
        };
        foreach (var (file, size, sha, date) in rows)
        {
            lines.Add($"| [{file}]({file}) | {size / 1024.0 / 1024.0:0.0} MB ({size} B) | `{sha}` | {date} |");
        }
        return string.Join("\n", lines) + "\n";
    }

    [Theory]
    [InlineData("MaszynyAlert-Setup-0.4.22.0.exe", 0, 4, 22, 0)]
    [InlineData("MaszynyAlert-Setup-0.4.22.exe", 0, 4, 22, 0)]
    [InlineData("maszynyalert-setup-1.2.3.4.EXE", 1, 2, 3, 4)]
    [InlineData("VncAlert-Setup-0.5.1.0.exe", 0, 5, 1, 0)]
    [InlineData("VNC-Alert-Setup-0.5.1.exe", 0, 5, 1, 0)]
    [InlineData("VNCAlert-Setup-0.5.1.0.exe", 0, 5, 1, 0)]
    public void FileNameWithAKnownPrefixGivesAFourPartVersion(string fileName, int major, int minor, int build, int revision)
    {
        Assert.True(ReleaseCatalog.TryParseFileVersion(fileName, out var version));
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MaszynyAlert-Setup-0.4.exe")]
    [InlineData("MaszynyAlert-Setup-0.4.22.0.msi")]
    [InlineData("MaszynyAlert-Setup-0.4.22.0.1.exe")]
    [InlineData("MaszynyAlert-Setup-0.4.x.0.exe")]
    [InlineData("Inny-Program-Setup-0.4.22.0.exe")]
    [InlineData("../MaszynyAlert-Setup-0.4.22.0.exe")]
    [InlineData("sub/MaszynyAlert-Setup-0.4.22.0.exe")]
    [InlineData("MaszynyAlert-Setup-0.4.99999999999.0.exe")]
    public void FileNameThatIsNotAnInstallerIsRejected(string fileName)
    {
        Assert.False(ReleaseCatalog.TryParseFileVersion(fileName, out _));
    }

    [Fact]
    public void IndexFromTheServerIsParsedWithSizeShaAndDate()
    {
        string markdown = Index(("MaszynyAlert-Setup-0.4.22.0.exe", 57461760, ShaA, "2026-10-02T18:02:31Z"));

        var entry = Assert.Single(ReleaseCatalog.ParseIndex(markdown));

        Assert.Equal("MaszynyAlert-Setup-0.4.22.0.exe", entry.FileName);
        Assert.Equal(new Version(0, 4, 22, 0), entry.Version);
        Assert.Equal(57461760, entry.SizeBytes);
        Assert.Equal(ShaA, entry.Sha256);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 18, 2, 31, TimeSpan.Zero), entry.PublishedAt);
    }

    [Fact]
    public void ShaIsStoredInLowerCase()
    {
        string markdown = Index(("MaszynyAlert-Setup-0.4.22.0.exe", 100, ShaA.ToUpperInvariant(), "2026-10-02T18:02:31Z"));

        Assert.Equal(ShaA, Assert.Single(ReleaseCatalog.ParseIndex(markdown)).Sha256);
    }

    [Fact]
    public void LatestIsTheHighestVersionNotTheLastRowOfTheFile()
    {
        // Dlaczego: serwer sortuje wiersze po nazwie (LC_ALL=C sort), więc „0.4.9” stoi PO „0.4.22”.
        // Ostatni wiersz pliku to wersja 9 — wybranie go cofnęłoby stanowisko do starszej wersji.
        string markdown = Index(
            ("MaszynyAlert-Setup-0.4.10.0.exe", 100, ShaA, "2026-09-30T10:00:00Z"),
            ("MaszynyAlert-Setup-0.4.22.0.exe", 200, ShaB, "2026-10-02T18:02:31Z"),
            ("MaszynyAlert-Setup-0.4.9.0.exe", 300, ShaC, "2026-09-29T10:00:00Z"));

        var entries = ReleaseCatalog.ParseIndex(markdown);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new Version(0, 4, 22, 0), ReleaseCatalog.Latest(entries)!.Version);
    }

    [Fact]
    public void LatestOfNothingIsNull()
    {
        Assert.Null(ReleaseCatalog.Latest([]));
    }

    [Fact]
    public void WindowsLineEndingsBlankLinesAndExtraColumnsDoNotBreakTheParser()
    {
        string markdown = string.Join("\r\n",
            "# Wydania: maszyny-alert", "", "",
            "| Plik | Rozmiar | SHA-256 | Data (UTC) | Uwagi |",
            "|---|---:|---|---|---|",
            $"| [MaszynyAlert-Setup-0.4.22.0.exe](MaszynyAlert-Setup-0.4.22.0.exe) | 54.8 MB (57461760 B) | `{ShaA}` | 2026-10-02T18:02:31Z | test |",
            "", "");

        var entry = Assert.Single(ReleaseCatalog.ParseIndex(markdown));

        Assert.Equal(57461760, entry.SizeBytes);
        Assert.Equal(ShaA, entry.Sha256);
    }

    [Theory]
    // Zła suma (63 znaki), brak rozmiaru, obca nazwa, pusta komórka — każdy taki wiersz jest pomijany,
    // a nie „naprawiany”: lepiej nie zaktualizować niż pobrać coś bez weryfikacji.
    [InlineData("| [MaszynyAlert-Setup-0.4.23.0.exe](x) | 54.8 MB (57461760 B) | `" + "abc" + "` | 2026-10-02T18:02:31Z |")]
    [InlineData("| [MaszynyAlert-Setup-0.4.23.0.exe](x) | 54.8 MB | `1617749400aa0000000000000000000000000000000000000000000000000001` | 2026-10-02T18:02:31Z |")]
    [InlineData("| [README.md](README.md) | 1 KB (1024 B) | `1617749400aa0000000000000000000000000000000000000000000000000001` | 2026-10-02T18:02:31Z |")]
    [InlineData("| | | | |")]
    [InlineData("| [MaszynyAlert-Setup-0.4.23.0.exe](x) | 0 MB (0 B) | `1617749400aa0000000000000000000000000000000000000000000000000001` | 2026-10-02T18:02:31Z |")]
    public void InvalidRowsAreSkipped(string row)
    {
        string markdown = Index(("MaszynyAlert-Setup-0.4.22.0.exe", 100, ShaA, "2026-10-02T18:02:31Z")) + row + "\n";

        var entry = Assert.Single(ReleaseCatalog.ParseIndex(markdown));
        Assert.Equal(new Version(0, 4, 22, 0), entry.Version);
    }

    [Fact]
    public void RowWithoutADateIsStillARelease()
    {
        string markdown = Index(("MaszynyAlert-Setup-0.4.22.0.exe", 100, ShaA, "nieznana"));

        Assert.Null(Assert.Single(ReleaseCatalog.ParseIndex(markdown)).PublishedAt);
    }

    [Fact]
    public void FutureInstallerNameVncAlertIsAcceptedNextToTheOldOne()
    {
        string markdown = Index(
            ("MaszynyAlert-Setup-0.4.22.0.exe", 100, ShaA, "2026-10-02T18:02:31Z"),
            ("VNC-Alert-Setup-0.5.1.0.exe", 200, ShaB, "2026-11-02T18:02:31Z"));

        var latest = ReleaseCatalog.Latest(ReleaseCatalog.ParseIndex(markdown))!;

        Assert.Equal("VNC-Alert-Setup-0.5.1.0.exe", latest.FileName);
    }

    [Fact]
    public void EmptyOrForeignTextGivesNoReleases()
    {
        Assert.Empty(ReleaseCatalog.ParseIndex(""));
        Assert.Empty(ReleaseCatalog.ParseIndex("<html><body>Sign in with Google</body></html>"));
    }

    [Theory]
    [InlineData("0.4.26", 0, 4, 26, 0)]
    [InlineData("0.4.26-pr78", 0, 4, 26, 0)]
    [InlineData("0.4.26+0123456789abcdef0123456789abcdef01234567", 0, 4, 26, 0)]
    [InlineData("0.4.26-pr78+0123456789abcdef", 0, 4, 26, 0)]
    [InlineData("0.3.0-dev", 0, 3, 0, 0)]
    [InlineData("0.4.26.0", 0, 4, 26, 0)]
    [InlineData(" 1.2.3.4 ", 1, 2, 3, 4)]
    [InlineData(null, 0, 0, 0, 0)]
    [InlineData("", 0, 0, 0, 0)]
    [InlineData("unknown/dev", 0, 0, 0, 0)]
    [InlineData("1.x.3", 0, 0, 0, 0)]
    public void InstalledVersionIsReadFromTheInformationalVersion(string? value, int major, int minor, int build, int revision)
    {
        Assert.Equal(new Version(major, minor, build, revision), ReleaseCatalog.ParseInstalledVersion(value));
    }

    [Fact]
    public void FourPartFileVersionComparesEqualToTheThreePartInstalledVersion()
    {
        // Dlaczego: System.Version(0,4,22) jest MNIEJSZA od Version(0,4,22,0) — bez wyrównania do czterech
        // części stanowisko z 0.4.22 widziałoby „nowszą” 0.4.22.0 i instalowało ją w kółko.
        Assert.True(ReleaseCatalog.TryParseFileVersion("MaszynyAlert-Setup-0.4.22.0.exe", out var file));
        Assert.Equal(ReleaseCatalog.ParseInstalledVersion("0.4.22"), file);
    }


    [Fact]
    public void MixedCatalogWithOldAndNewFileNamesGivesTheNewestVersionRegardlessOfTheName()
    {
        string markdown = Index(
            ("MaszynyAlert-Setup-0.4.148.0.exe", 57_500_000, ShaA, "2026-10-07T17:20:00Z"),
            ("VncAlert-Setup-0.4.160.0.exe", 57_600_000, ShaB, "2026-10-09T12:00:00Z"));

        var entries = ReleaseCatalog.ParseIndex(markdown);

        Assert.Equal(2, entries.Count);
        var latest = ReleaseCatalog.Latest(entries)!;
        Assert.Equal("VncAlert-Setup-0.4.160.0.exe", latest.FileName);
        Assert.Equal(new Version(0, 4, 160, 0), latest.Version);
        Assert.True(latest.Version > new Version(0, 4, 148, 0));
    }

    [Fact]
    public void MixedCatalogWhereTheOldNameIsNewerStillPicksByVersionNotByName()
    {
        // Odwrotna kolejność (np. wydanie wycofujące pod starą nazwą po nowym): nazwa nie daje pierwszeństwa.
        string markdown = Index(
            ("VncAlert-Setup-0.4.160.0.exe", 57_600_000, ShaB, "2026-10-09T12:00:00Z"),
            ("MaszynyAlert-Setup-0.4.161.0.exe", 57_500_000, ShaA, "2026-10-09T13:00:00Z"));

        var latest = ReleaseCatalog.Latest(ReleaseCatalog.ParseIndex(markdown))!;

        Assert.Equal("MaszynyAlert-Setup-0.4.161.0.exe", latest.FileName);
        Assert.Equal(new Version(0, 4, 161, 0), latest.Version);
    }

    [Fact]
    public void TheSameVersionUnderBothNamesIsTwoEntriesAndTheFirstRowWins()
    {
        // Wydanie przejściowe pod obiema nazwami (ten sam numer): spis sortowany po nazwie stawia MaszynyAlert przed VncAlert.
        string markdown = Index(
            ("MaszynyAlert-Setup-0.4.160.0.exe", 57_600_000, ShaA, "2026-10-09T12:00:00Z"),
            ("VncAlert-Setup-0.4.160.0.exe", 57_600_000, ShaA, "2026-10-09T12:00:00Z"));

        var entries = ReleaseCatalog.ParseIndex(markdown);

        Assert.Equal(2, entries.Count);
        Assert.Equal("MaszynyAlert-Setup-0.4.160.0.exe", ReleaseCatalog.Latest(entries)!.FileName);
    }
}
