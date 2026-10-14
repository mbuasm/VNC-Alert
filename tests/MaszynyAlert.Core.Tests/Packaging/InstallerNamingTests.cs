using System.Text.RegularExpressions;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class InstallerNamingTests
{
    // Pliki w repozytorium mają CRLF, a `$` z RegexOptions.Multiline zatrzymuje się dopiero przed '\n' — normalizujemy.
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestPaths.RepositoryRoot, .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Iss => Read("installer", "MaszynyAlert.iss");
    private static string IssWydanie => Read("installer", "MaszynyAlert-wydanie.iss");
    private static string BuildInstaller => Read("scripts", "Build-Installer.ps1");
    private static string PrepareInstallerTests => Read("scripts", "Prepare-InstallerTests.ps1");
    private static string TestInstaller => Read("scripts", "Test-Installer.ps1");
    private static string Wydanie => Read(".github", "workflows", "wydanie.yml");
    private static string VncAlertCmd => Read("src", "MaszynyAlert.App", "vncalert.cmd");

    [Fact]
    public void PlikInstalatora_NazywaSieVncAlertSetup()
    {
        Assert.Matches(new Regex(@"^\s*#define OutputBaseName ""VncAlert-Setup""", RegexOptions.Multiline), Iss);
        Assert.Contains(@"#define OutputBaseName ""VncAlert-Setup-"" + GetFileVersion(", IssWydanie, StringComparison.Ordinal);
        Assert.Contains("'VncAlert-Setup.exe'", BuildInstaller, StringComparison.Ordinal);
        Assert.Contains("VncAlert-Setup.exe`n", BuildInstaller, StringComparison.Ordinal);
        Assert.Contains("uses: mbuasm/serwer-laptop/.github/workflows/ui-sprawdz.yml@main", Wydanie, StringComparison.Ordinal);
        Assert.DoesNotContain("MaszynyAlert-Setup.exe", BuildInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain(@"""MaszynyAlert-Setup-", IssWydanie, StringComparison.Ordinal);
    }

    [Fact]
    public void Instalator_NazwaUzytkowaWszedzie_AppIdBezZmian()
    {
        Assert.Matches(new Regex(@"^AppName=VNC Alert$", RegexOptions.Multiline), Iss);
        Assert.Matches(new Regex(@"^AppPublisher=VNC Alert$", RegexOptions.Multiline), Iss);
        Assert.Matches(new Regex(@"^UninstallDisplayName=VNC Alert$", RegexOptions.Multiline), Iss);
        Assert.Matches(new Regex(@"^VersionInfoProductName=VNC Alert$", RegexOptions.Multiline), Iss);
        // Ta sama aplikacja w Dodaj/usuń programy: stały AppId rozpoznaje instalację 0.4.148 i aktualizuje ją w miejscu.
        Assert.Matches(new Regex(@"^AppId=\{\{DBFA5243-64F6-49C5-9FDC-89B197AD78F9\}$", RegexOptions.Multiline), Iss);
        Assert.DoesNotContain("Maszyny-alert", Iss.Replace(@"{commonprograms}\Maszyny-alert.lnk", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void KatalogProgramu_NowyDlaNowychInstalacji_PoprzedniZostajeIJestPrzyjmowany()
    {
        Assert.Matches(new Regex(@"^\s*#define AppDirName ""VNC Alert""", RegexOptions.Multiline), Iss);
        Assert.Matches(new Regex(@"^DefaultDirName=\{autopf\}\\\{#AppDirName\}$", RegexOptions.Multiline), Iss);
        Assert.DoesNotContain("UsePreviousAppDir=no", Iss, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AppFolderCurrent = 'VNC Alert';", Iss, StringComparison.Ordinal);
        Assert.Contains("AppFolderPrevious = 'MaszynyAlert';", Iss, StringComparison.Ordinal);
        // Zatrzymanie programu przed wyborem katalogu: EXE z katalogu z rejestru (wpis Inno po AppId), nie ze stałej ścieżki.
        Assert.Contains("RegQueryStringValue(HKLM, UninstallKey, 'Inno Setup: App Path', Previous)", Iss, StringComparison.Ordinal);
        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{DBFA5243-64F6-49C5-9FDC-89B197AD78F9}_is1", Iss, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ExpandConstant('{autopf}\MaszynyAlert\MaszynyAlert.App.exe')", Iss, StringComparison.Ordinal);
        // PrepareToInstall przyjmuje oba katalogi (odmowa dla każdego innego zostaje).
        Assert.Contains("ExpandConstant('{autopf}\\' + AppFolderCurrent)", Iss, StringComparison.Ordinal);
        Assert.Contains("ExpandConstant('{autopf}\\' + AppFolderPrevious)", Iss, StringComparison.Ordinal);
        Assert.Contains("Ta wersja wymaga instalacji w Program Files\\VNC Alert (albo w dotychczasowym Program Files\\MaszynyAlert).", Iss, StringComparison.Ordinal);
    }

    [Fact]
    public void TestInstalatora_SprawdzaDrogeHaliINowaInstalacje()
    {
        // Baza 0.1.3 w starym katalogu (/DAppDirName=MaszynyAlert) → aktualizacja w miejscu; na końcu nowa instalacja w VNC Alert.
        Assert.Contains("-AppDirName 'MaszynyAlert'", PrepareInstallerTests, StringComparison.Ordinal);
        Assert.Contains(@"if ($AppDirName) { $defineArguments += ""/DAppDirName=$AppDirName"" }", BuildInstaller, StringComparison.Ordinal);
        Assert.Contains("$previousApp = Join-Path $env:ProgramFiles 'MaszynyAlert'", TestInstaller, StringComparison.Ordinal);
        Assert.Contains("$currentApp = Join-Path $env:ProgramFiles 'VNC Alert'", TestInstaller, StringComparison.Ordinal);
        Assert.Contains("$app = $previousApp", TestInstaller, StringComparison.Ordinal);
        Assert.Contains("Run-Setup $setup 'fresh-install.log'", TestInstaller, StringComparison.Ordinal);
        Assert.Contains("$app = $currentApp", TestInstaller, StringComparison.Ordinal);
        Assert.Contains("$fileName = 'VncAlert-Setup-' + $newer.Version + '.exe'", TestInstaller, StringComparison.Ordinal);
        Assert.Contains("$installedFileName = 'MaszynyAlert-Setup-' + $installedBefore + '.exe'", TestInstaller, StringComparison.Ordinal);
        Assert.Contains("'VncAlert-Setup.exe'", TestInstaller, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCoMusiZostac_ZebyAktualizacjaZ0_4_148Przeszla()
    {
        // Nazwa EXE, muteksy, katalog danych: 0.4.148 (działający pomocnik, zadanie „VNC Alert update”, watchdog) zna te nazwy.
        Assert.Matches(new Regex(@"^AppMutex=Global\\MaszynyAlert\.App,Global\\MaszynyAlert\.Setup$", RegexOptions.Multiline), Iss);
        Assert.Contains("AppMutexName = 'Global\\MaszynyAlert.App';", Iss, StringComparison.Ordinal);
        Assert.Contains(@"GetFileVersion(PublishDir + ""\MaszynyAlert.App.exe"")", Iss, StringComparison.Ordinal);
        Assert.Contains(@"{commonappdata}\MaszynyAlert", Iss, StringComparison.Ordinal);
        Assert.Contains(@"set ""EXE=%~dp0MaszynyAlert.App.exe""", VncAlertCmd, StringComparison.Ordinal);
        Assert.Contains("projekt: maszyny-alert", Wydanie, StringComparison.Ordinal);
    }
}
