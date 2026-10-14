using System.Text.RegularExpressions;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class InstallerUpdateTaskTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestPaths.RepositoryRoot, .. parts]));

    private static string Iss => Read("installer", "MaszynyAlert.iss");
    private static string InitializeData => Read("installer", "Initialize-Data.ps1");
    private static string RegisterUpdateTask => Read("installer", "Register-UpdateTask.ps1");
    private static string UpdateTaskScript => Read("installer", "Update-Task.ps1");
    private static string TestInstaller => Read("scripts", "Test-Installer.ps1");

    [Fact]
    public void Instalator_KopiujeSkryptyZadaniaDoProgramFiles()
    {
        Assert.Matches(new Regex(@"^Source: ""Update-Task\.ps1""; DestDir: ""\{app\}\\installer""", RegexOptions.Multiline), Iss);
        Assert.Matches(new Regex(@"^Source: ""Register-UpdateTask\.ps1""; DestDir: ""\{app\}\\installer""", RegexOptions.Multiline), Iss);
    }

    [Fact]
    public void Instalator_RejestrujeZadaniePoInstalacjiIUsuwaJePrzyOdinstalowaniu()
    {
        Assert.Contains($"UpdateTaskName = '{UpdateTaskContract.TaskName}';", Iss, StringComparison.Ordinal);
        Assert.Contains("Register-UpdateTask.ps1", Iss, StringComparison.Ordinal);
        Assert.Contains("<event('CurStepChanged')>", Iss, StringComparison.Ordinal);
        Assert.Contains("<event('CurUninstallStepChanged')>", Iss, StringComparison.Ordinal);
        Assert.Contains("procedure RemoveUpdateTask;", Iss, StringComparison.Ordinal);
    }

    [Fact]
    public void Zadanie_DzialaJakoSystemBezWyzwalaczyAUsersMogaJeTylkoUruchomic()
    {
        string script = RegisterUpdateTask;
        Assert.Contains("<UserId>S-1-5-18</UserId>", script, StringComparison.Ordinal);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", script, StringComparison.Ordinal);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("<(TimeTrigger|LogonTrigger|BootTrigger|CalendarTrigger|EventTrigger|IdleTrigger)"), script);
        // Deskryptor zadania: SYSTEM i Administratorzy — pełne prawa; Users — odczyt i uruchomienie (bez zapisu i usunięcia).
        Assert.Contains("D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)", script, StringComparison.Ordinal);
        Assert.Contains("SetSecurityDescriptor", script, StringComparison.Ordinal);
        Assert.Contains(@"installer\Update-Task.ps1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SkryptZadania_SprawdzaPlikProgramemZProgramFilesIUruchamiaTylkoKopie()
    {
        string script = UpdateTaskScript;
        Assert.Contains(UpdateTaskContract.VerifyArgument, script, StringComparison.Ordinal);
        Assert.Contains(UpdateTaskContract.VerifiedFileName, script, StringComparison.Ordinal);
        Assert.Contains(UpdateTaskContract.TaskResultFileName, script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[System.IO.FileShare]::Read", script, StringComparison.Ordinal);
        Assert.Contains("/VERYSILENT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Verb RunAs", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkryptyZadania_SaAscii_BoWindowsPowerShell51CzytaPlikBezBomJakoAnsi()
    {
        foreach (string text in new[] { RegisterUpdateTask, UpdateTaskScript })
            Assert.DoesNotContain(text, c => c > 127);
    }

    [Fact]
    public void Dane_KatalogZadaniaWKataloguDanych_BezMigracjiKataloguOperatora()
    {
        string script = InitializeData;
        Assert.Matches(new Regex(@"\$updatesAdmin = Join-Path \$DataDirectory 'updates-admin'"), script);
        Assert.Contains("Set-DataAcl $updatesAdmin $true $false", script, StringComparison.Ordinal);
        foreach (string gone in new[] { "Protect-UpdatesDirectory", "'downloads'", "hardlink list", "Directory]::Delete" })
            Assert.DoesNotContain(gone, script, StringComparison.Ordinal);
    }

    [Fact]
    public void Instalator_RejestrujeZadanieTylkoPoUdanymPrzygotowaniuDanych()
    {
        Assert.Matches(new Regex(@"if DataReady then\s+RegisterUpdateTask\s+else\s+RemoveUpdateTask", RegexOptions.Singleline), Iss);
    }

    [Fact]
    public void SkryptZadania_SprawdzaKatalogRoboczyIRodzicaPrzedPraca()
    {
        string script = UpdateTaskScript;
        Assert.Contains("function Test-AdminOnlyDirectory", script, StringComparison.Ordinal);
        Assert.Contains("GetOwner(", script, StringComparison.Ordinal);
        int checkWork = script.IndexOf("Test-AdminOnlyDirectory $work", StringComparison.Ordinal);
        int checkParent = script.IndexOf("Test-AdminOnlyDirectory $DataDirectory", StringComparison.Ordinal);
        int firstUse = script.IndexOf("Write-TaskLog ('start", StringComparison.Ordinal);
        Assert.True(checkWork > 0 && checkParent > 0 && firstUse > checkWork && firstUse > checkParent,
            "Kontrola katalogów ma być przed pierwszym zapisem do katalogu zadania.");
    }

    [Fact]
    public void Zadanie_UruchamiaTylkoProgramWWersjiZRejestracji()
    {
        Assert.Matches(new Regex(@"-ExpectedVersion ' \+ \$AppVersion"), RegisterUpdateTask);
        Assert.Contains("-AppVersion {#AppVersion}", Iss, StringComparison.Ordinal);
        string code = CodeOnly(UpdateTaskScript);
        Assert.Matches(new Regex(@"GetVersionInfo\(\$exe\)\.FileVersion\s+if \(\$installed -ne \$ExpectedVersion\) \{[^}]*?exit 8\s+\}"), code);
        int versionCheck = code.IndexOf("$installed -ne $ExpectedVersion", StringComparison.Ordinal);
        int verify = code.IndexOf("--update-task-verify", versionCheck, StringComparison.Ordinal);
        Assert.True(verify > versionCheck, "Wersja ma być sprawdzona, zanim zadanie uruchomi program.");
        Assert.Equal(8, UpdateTaskContract.ExitVersionMismatch);
    }

    [Fact]
    public void Program_BramkaSystemPrzedMuteksem_StopJakoSystemDoUpdatesAdmin()
    {
        string program = Read("src", "MaszynyAlert.App", "Program.cs");
        int gate = program.IndexOf("if (IsLocalSystem(identity.User))", StringComparison.Ordinal);
        int mutex = program.IndexOf("new Mutex(initiallyOwned: true, SingleInstanceMutexName", StringComparison.Ordinal);
        Assert.True(gate > 0 && mutex > gate, "Bramka SYSTEM ma stać przed muteksem jednej instancji.");
        int stop = program.IndexOf("args.Contains(StopRequest.Argument", StringComparison.Ordinal);
        int suppress = program.IndexOf("WatchdogTask.Suppress(AppPaths.DataDir, DateTimeOffset.Now, asSystem)", StringComparison.Ordinal);
        Assert.True(stop > 0 && suppress > stop && suppress < gate, "--stop ma rozpoznawać SYSTEM i działać przed bramką SYSTEM.");
    }

    [Fact]
    public void Dane_UprawnieniaPrzezUchwytBezPodazaniaZaPrzekierowaniem()
    {
        string code = CodeOnly(InitializeData);
        Assert.DoesNotMatch(new Regex(@"Set-Acl\s+-"), code);
        Assert.DoesNotContain("Add-Type", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x02200000", code, StringComparison.Ordinal);
        Assert.Contains("GetFileInformationByHandle", code, StringComparison.Ordinal);
        Assert.Contains("Set-AclByHandle $Path $acl $Directory", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Instalator_NieudanaRejestracjaZadania_UsuwaZadanie()
    {
        int start = Iss.IndexOf("procedure RegisterUpdateTask;", StringComparison.Ordinal);
        int end = Iss.IndexOf("<event('CurStepChanged')>", start, StringComparison.Ordinal);
        string body = Iss[start..end];
        int failure = body.IndexOf("NIE udalo sie zarejestrowac zadania aktualizacji", StringComparison.Ordinal);
        Assert.True(failure > 0);
        Assert.Contains("RemoveUpdateTask;", body[failure..], StringComparison.Ordinal);
    }

    [Fact]
    public void SkryptZadania_NiePiszeINieKasujeWKataloguOperatora()
    {
        string code = CodeOnly(UpdateTaskScript);
        Assert.DoesNotContain(@"state\", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'state'", code, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex(@"\$systemMarker = Join-Path \$work 'stop\.requested'"), code);
        Assert.Contains("[System.IO.File]::Delete($systemMarker)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SkryptZadania_KatalogNiechroniony_PowodWDziennikuZdarzen()
    {
        string code = CodeOnly(UpdateTaskScript);
        int check = code.IndexOf("Test-AdminOnlyDirectory $work", StringComparison.Ordinal);
        int eventLog = code.IndexOf("Write-TaskEvent", check, StringComparison.Ordinal);
        int exit7 = code.IndexOf("exit 7", check, StringComparison.Ordinal);
        Assert.True(check > 0 && eventLog > check && exit7 > eventLog, "Powód kodu 7 ma trafić do dziennika zdarzeń przed exit 7.");
        Assert.Contains("eventcreate.exe", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SkryptZadania_PonawiaZapisWyniku()
    {
        Assert.Matches(new Regex(@"for \(\$attempt = 0; \$attempt -lt \d+"), UpdateTaskScript);
    }

    [Fact]
    public void SkryptZadania_ZdejmujeZnacznikSystemZPonowieniemIWpisemWLogu()
    {
        string code = CodeOnly(UpdateTaskScript);
        int start = code.IndexOf("function Remove-SystemMarker", StringComparison.Ordinal);
        Assert.True(start > 0, "Brak funkcji Remove-SystemMarker.");
        int end = code.IndexOf("\n}", start, StringComparison.Ordinal);
        string body = code[start..end];
        int loop = body.IndexOf("for ($attempt = 0; $attempt -lt", StringComparison.Ordinal);
        int delete = body.IndexOf("[System.IO.File]::Delete($systemMarker)", StringComparison.Ordinal);
        int failure = body.LastIndexOf("nie zdjeto znacznika", StringComparison.Ordinal);
        Assert.True(loop > 0 && delete > loop, "Usunięcie znacznika ma być w pętli prób.");
        Assert.Contains("Start-Sleep -Milliseconds 500", body, StringComparison.Ordinal);
        Assert.True(failure > delete, "Po nieudanych próbach — wpis w logu zadania.");
        Assert.Contains("$null = Remove-SystemMarker", code, StringComparison.Ordinal);
    }

    /// <summary>Treść skryptu PowerShell bez wierszy komentarza (asercje dotyczą kodu, nie opisu).</summary>
    private static string CodeOnly(string script) =>
        string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    [Fact]
    public void TestInstalatora_WywolujeSprawdzeniaZadania()
    {
        string script = TestInstaller;
        foreach (string call in new[]
                 {
                     "Assert-UpdateTask \\$true", "Assert-UpdateTask \\$false", "Assert-UpdatesAdminDirectory",
                     "Test-UpdateTaskAsStandardUser", "Test-UpdateTaskInstall", "Test-UpdateTaskRefusesUnprotectedDirectory",
                     "Test-UpdateTaskVersionBinding", "Test-ProgramRefusesSystem",
                     "Test-DataAclByHandle", "Test-StopSignalFromOtherOwner",
                     "Test-SystemMarkerRemovalRetry",
                 })
        {
            Assert.Matches(new Regex("^" + call, RegexOptions.Multiline), script);
        }
        Assert.Contains($"'{UpdateTaskContract.TaskName}'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SKIP: zadanie z konta standardowego", script, StringComparison.Ordinal);
        Assert.Contains("https://127.0.0.1:", script, StringComparison.Ordinal);
    }
}
