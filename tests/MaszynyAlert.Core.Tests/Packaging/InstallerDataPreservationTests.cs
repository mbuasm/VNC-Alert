using System.Text.RegularExpressions;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class InstallerDataPreservationTests
{
    // Pliki w repozytorium mają CRLF — normalizujemy, żeby wyrażenia z RegexOptions.Multiline działały na końcach linii.
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestPaths.RepositoryRoot, .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Iss => Read("installer", "MaszynyAlert.iss");
    private static string InitializeData => Read("installer", "Initialize-Data.ps1");
    private static string AppPaths => Read("src", "MaszynyAlert.Windows", "AppPaths.cs");
    private static string TestInstaller => Read("scripts", "Test-Installer.ps1");

    /// <summary>Wpisy [Files] (linie zaczynające się od „Source:”) — sekcja kończy się na następnym nagłówku sekcji.</summary>
    private static string[] FileEntries()
    {
        string iss = Iss;
        int start = iss.IndexOf("\n[Files]\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "Brak sekcji [Files] w installer/MaszynyAlert.iss.");
        int end = iss.IndexOf("\n[", start + "\n[Files]\n".Length, StringComparison.Ordinal);
        string section = end < 0 ? iss[start..] : iss[start..end];
        return section.Split('\n').Where(l => l.StartsWith("Source:", StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public void Instalator_NieKopiujeDanychStanowiskaDoKataloguProgramu()
    {
        string[] entries = FileEntries();
        // Jedyny wpis kopiujący całą paczkę: publish\app zawiera config.json i rules.json (Przygotuj-Wydanie.ps1 i smoke ich
        // wymagają), więc muszą być w Excludes. Wzorzec bez wiodącego „\” Inno dopasowuje do nazwy pliku w KAŻDYM podkatalogu.
        string package = Assert.Single(entries, l => l.StartsWith(@"Source: ""{#PublishDir}\*"";", StringComparison.Ordinal));
        Match excludes = Regex.Match(package, @"Excludes: ""(?<list>[^""]*)""");
        Assert.True(excludes.Success, "Wpis paczki w [Files] nie ma Excludes: " + package);
        string[] excluded = excludes.Groups["list"].Value.Split(',').Select(p => p.Trim()).ToArray();
        foreach (string required in new[]
        {
            "config.json", "rules.json", "secrets.dat", "history.csv", "downtime.jsonl", "alarm-instructions.json*",
            @"logs\*", @"captures\*", @"state\*", @"smoke-test-output\*", @"test-ekran\*", @"sounds\*",
        })
        {
            Assert.Contains(required, excluded);
        }

        // Żaden inny wpis nie wskazuje wprost plików danych stanowiska.
        foreach (string entry in entries.Where(e => !ReferenceEquals(e, package)))
        {
            Assert.DoesNotMatch(new Regex(@"(config|rules)\.json""|secrets\.dat|history\.csv|downtime\.jsonl", RegexOptions.IgnoreCase), entry);
        }

        string[] toData = entries.Where(e => e.Contains("{commonappdata}", StringComparison.Ordinal)).ToArray();
        string readme = Assert.Single(toData);
        Assert.Contains(@"test-ekran\CZYTAJ-TO.txt", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Instalator_DzwiekiTylkoGdyBrakPliku()
    {
        string sounds = Assert.Single(FileEntries(), l => l.StartsWith(@"Source: ""{#PublishDir}\sounds\*"";", StringComparison.Ordinal));
        Assert.Contains("onlyifdoesntexist", sounds, StringComparison.Ordinal);
        Assert.DoesNotContain("ignoreversion", sounds, StringComparison.Ordinal);
    }

    [Fact]
    public void PierwszaInstalacja_KopiujeWzorzecKonfiguracjiTylkoGdyBrakPliku()
    {
        string script = InitializeData;
        Assert.Contains("@('config.example.json', 'config.json')", script, StringComparison.Ordinal);
        Assert.Contains("@('rules.example.json', 'rules.json')", script, StringComparison.Ordinal);
        // Jedyne kopiowanie w skrypcie — wewnątrz warunku „pliku jeszcze nie ma”, bez -Force.
        Assert.Single(Regex.Matches(script, @"\bCopy-Item\b"));
        Assert.Matches(new Regex(
            @"if \(!\(Test-Path -LiteralPath \$target\)\) \{\s*Copy-Item -LiteralPath \(Join-Path \$ApplicationDirectory \$pair\[0\]\) -Destination \$target\s*\}"),
            script);
        Assert.DoesNotMatch(new Regex(@"Copy-Item[^\n]*-Force"), script);
        // Dzienniki: pusty plik tylko przy pierwszej instalacji — istniejące rekordy zostają bajt w bajt.
        Assert.Contains("if (!(Test-Path -LiteralPath $history)) { [IO.File]::WriteAllBytes($history, [byte[]]@()) }", script, StringComparison.Ordinal);
        Assert.Contains("if (!(Test-Path -LiteralPath $downtime)) { [IO.File]::WriteAllBytes($downtime, [byte[]]@()) }", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(Set-Content|Out-File|WriteAllText|Remove-Item)[^\n]*(config|rules)\.json"), script);
    }

    [Fact]
    public void Program_KopiujeWzorzecKonfiguracjiTylkoGdyBrakPliku()
    {
        string source = AppPaths;
        Assert.Contains("if (File.Exists(destination) || !File.Exists(source))", source, StringComparison.Ordinal);
        Assert.Contains("File.Copy(source, destination, overwrite: false);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("overwrite: true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TestInstalatora_AktualizacjaINowaInstalacjaNieZmieniajaKonfiguracji()
    {
        string script = TestInstaller;
        // Skrót danych obejmuje konfigurację, słownik, sekrety i oba dzienniki.
        Match hashes = Regex.Match(script, @"function Get-DataHashes \{.*?foreach \(\$name in @\((?<names>[^)]*)\)", RegexOptions.Singleline);
        Assert.True(hashes.Success, "Brak Get-DataHashes w Test-Installer.ps1.");
        foreach (string name in new[] { "'config.json'", "'rules.json'", "'secrets.dat'", "'history.csv'", "'downtime.jsonl'" })
            Assert.Contains(name, hashes.Groups["names"].Value, StringComparison.Ordinal);

        // config.json i rules.json różnią się od wzorca PRZED zapamiętaniem skrótów — nadpisanie wzorcem zmieniłoby skrót.
        int touchConfig = script.IndexOf("Add-Content -LiteralPath (Join-Path $data 'config.json') -Value ' '", StringComparison.Ordinal);
        int touchRules = script.IndexOf("Add-Content -LiteralPath (Join-Path $data 'rules.json') -Value ' '", StringComparison.Ordinal);
        int firstHashes = script.IndexOf("$hashes = Get-DataHashes", StringComparison.Ordinal);
        Assert.True(touchConfig > 0 && touchRules > 0 && firstHashes > touchConfig && firstHashes > touchRules,
            "config.json/rules.json mają być zmienione przed pierwszym Get-DataHashes.");

        // Linia „aktualizacja zachowała własny sounds/alarm.wav” w logu `sprawdz` jest dowodem, że po aktualizacji
        // Assert-DataUnchanged przeszedł — stoi PO nim (weryfikacja wydań opiera się na tej kolejności).
        int upgrade = script.IndexOf("Run-Setup $setup 'upgrade.log'", StringComparison.Ordinal);
        int unchangedAfterUpgrade = script.IndexOf("Assert-DataUnchanged $hashes", upgrade, StringComparison.Ordinal);
        int soundPass = script.IndexOf("'PASS: aktualizacja zachowała własny sounds/alarm.wav.'", StringComparison.Ordinal);
        Assert.True(upgrade > firstHashes && unchangedAfterUpgrade > upgrade && soundPass > unchangedAfterUpgrade,
            "Po Run-Setup upgrade.log ma iść Assert-DataUnchanged, a dopiero potem PASS o dźwięku.");

        // Nowa instalacja po odinstalowaniu (dane zostały w ProgramData) też nie zmienia danych.
        int fresh = script.IndexOf("Run-Setup $setup 'fresh-install.log'", StringComparison.Ordinal);
        int unchangedAfterFresh = script.IndexOf("Assert-DataUnchanged $hashes", fresh, StringComparison.Ordinal);
        int freshPass = script.IndexOf("\"PASS: nowa instalacja w $app", StringComparison.Ordinal);
        Assert.True(fresh > soundPass && unchangedAfterFresh > fresh && freshPass > unchangedAfterFresh,
            "Po Run-Setup fresh-install.log ma iść Assert-DataUnchanged przed PASS nowej instalacji.");

        // Aktualizacja przez zadanie (SYSTEM) przywraca config.json testu dokładnie w poprzedniej treści.
        Assert.Contains("[IO.File]::WriteAllText($configPath, $config)", script, StringComparison.Ordinal);
    }
}
