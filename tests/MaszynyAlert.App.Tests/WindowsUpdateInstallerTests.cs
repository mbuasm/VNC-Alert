using System.IO;
using System.Security.Cryptography;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class WindowsUpdateInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "update-installer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Script_NieUzywaGetFileHash()
    {
        Assert.DoesNotContain("Get-FileHash", WindowsUpdateInstaller.Script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Security.Cryptography.SHA256", WindowsUpdateInstaller.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_TrzymaPlikInstalatoraOtwartyDoKoncaInstalacji()
    {
        Assert.Contains("[System.IO.File]::Open($Setup", WindowsUpdateInstaller.Script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileShare]::Read", WindowsUpdateInstaller.Script, StringComparison.Ordinal);
        int waitForExit = WindowsUpdateInstaller.Script.IndexOf("$process.WaitForExit()", StringComparison.Ordinal);
        int dispose = WindowsUpdateInstaller.Script.IndexOf("$guard.Dispose()", StringComparison.Ordinal);
        Assert.True(waitForExit > 0 && dispose > waitForExit, "Plik ma być zamknięty dopiero po zakończeniu instalatora.");
    }

    [Fact]
    public void Script_DzialajacyProgramRozpoznajePoMuteksieInstancjiNiePoNazwieProcesu()
    {
        Assert.Contains($"'{Program.SingleInstanceMutexName}'", WindowsUpdateInstaller.Script, StringComparison.Ordinal);
        Assert.Contains("[System.Threading.Mutex]::TryOpenExisting", WindowsUpdateInstaller.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process", WindowsUpdateInstaller.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_WynikMowiPrawdeOUruchomieniuProgramu()
    {
        string script = WindowsUpdateInstaller.Script;
        int confirmed = script.IndexOf("if (Start-AppIfStopped)", StringComparison.Ordinal);
        Assert.True(confirmed > 0, "Komunikat o uruchomieniu musi zależeć od wyniku Start-AppIfStopped.");
        Assert.Contains("nie potwierdzono uruchomienia programu", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_TrybZadania_UruchamiaZadanieIProgramWSesjiOperatora()
    {
        string script = WindowsUpdateInstaller.Script;
        Assert.Contains(@"'System32\schtasks.exe'", script, StringComparison.Ordinal);
        Assert.Contains("'/run'", script, StringComparison.Ordinal);
        Assert.Contains("$TaskName", script, StringComparison.Ordinal);
        Assert.Contains("$RequestId", script, StringComparison.Ordinal);
        Assert.Contains("'-TaskName'", script, StringComparison.Ordinal);
        int task = script.IndexOf("if ($TaskName)", StringComparison.Ordinal);
        int runAs = script.IndexOf("-Verb RunAs", StringComparison.Ordinal);
        Assert.True(task > 0 && runAs > task, "Gałąź zadania ma być przed ścieżką z UAC i kończyć się własnym exit.");
        string taskBranch = script[task..runAs];
        Assert.Contains("Start-AppIfStopped", taskBranch, StringComparison.Ordinal);
        Assert.Contains("exit", taskBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_TrybZadania_OdmowaWersji_PrzechodziNaSciezkeZUac()
    {
        string script = WindowsUpdateInstaller.Script;
        int task = script.IndexOf("if ($TaskName)", StringComparison.Ordinal);
        int runAs = script.IndexOf("-Verb RunAs", StringComparison.Ordinal);
        string taskBranch = script[task..runAs];
        int fallback = taskBranch.IndexOf("if ($code -eq 8)", StringComparison.Ordinal);
        Assert.True(fallback > 0, "Gałąź zadania ma rozpoznać odmowę „wersja” (kod 8).");
        int nextBranch = taskBranch.IndexOf("elseif", fallback, StringComparison.Ordinal);
        Assert.True(nextBranch > fallback);
        string onVersion = taskBranch[fallback..nextBranch];
        Assert.Contains("$TaskName = $null", onVersion, StringComparison.Ordinal);
        Assert.DoesNotContain("exit", onVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_TrybZadania_ZadanieSkonczoneBezWyniku_BladOdRazu()
    {
        string script = WindowsUpdateInstaller.Script;
        int task = script.IndexOf("if ($TaskName)", StringComparison.Ordinal);
        int runAs = script.IndexOf("-Verb RunAs", StringComparison.Ordinal);
        string taskBranch = script[task..runAs];
        Assert.Contains("Get-TaskRun", taskBranch, StringComparison.Ordinal);
        Assert.Contains("Schedule.Service", script, StringComparison.Ordinal);
        Assert.Contains("LastTaskResult", script, StringComparison.Ordinal);
        Assert.Contains("Dzienniku zdarzen", taskBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_TrybZadania_ZnacznikSystemNieZdjety_KomunikatNieObiecujeMinuty()
    {
        string script = WindowsUpdateInstaller.Script;
        int task = script.IndexOf("if ($TaskName)", StringComparison.Ordinal);
        int runAs = script.IndexOf("-Verb RunAs", StringComparison.Ordinal);
        string taskBranch = script[task..runAs];
        int marker = taskBranch.IndexOf("'stop.requested'", StringComparison.Ordinal);
        int minute = taskBranch.IndexOf("watchdog wznowi go w ciagu minuty", StringComparison.Ordinal);
        Assert.True(marker > 0 && minute > marker, "Przed obietnicą „w ciągu minuty” pomocnik ma sprawdzić znacznik SYSTEM.");
        Assert.Contains("Split-Path -Parent $TaskResult", taskBranch, StringComparison.Ordinal);
        Assert.Contains("nie zdjelo znacznika zatrzymania instalatora", taskBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void Katalogi_PobraniaDlaOperatora_ZadanieTylkoDlaAdministratorow()
    {
        var (download, admin) = UpdateService.DirectoriesFor(@"C:\ProgramData\MaszynyAlert");

        Assert.Equal(@"C:\ProgramData\MaszynyAlert\state\updates", download);
        Assert.Equal(@"C:\ProgramData\MaszynyAlert\updates-admin", admin);
    }

    [Fact]
    public void Script_TrybZadania_StartujeProgramZIfNotRunning()
    {
        string script = WindowsUpdateInstaller.Script;
        int task = script.IndexOf("if ($TaskName)", StringComparison.Ordinal);
        int runAs = script.IndexOf("-Verb RunAs", StringComparison.Ordinal);
        string taskBranch = script[task..runAs];
        Assert.Contains("Start-AppIfStopped '--if-not-running'", taskBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-AppIfStopped '--if-not-running'", script[runAs..], StringComparison.Ordinal);
    }

    [Fact]
    public void KopiaInstalatora_MaListeUprawnienTylkoSystemIAdministratorzy()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "kopia.exe");
        using (var stream = UpdateTaskCommand.CreateProtectedFile(path)) stream.WriteByte(1);

        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Select(r => r.IdentityReference.Value).Distinct().OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, sids);

        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [Fact]
    public async Task LaunchAsync_ZadanieZarejestrowane_ZapisujeZadanieIUruchamiaPomocnikaBezUac()
    {
        // Prawdziwy PowerShell 5.1 i schtasks.exe, ale z nazwą zadania, którego nie ma: pomocnik ma zgłosić, że zadania
        // nie uruchomił — nie wolno mu wtedy przejść na UAC ani uruchamiać programu (program działa).
        Directory.CreateDirectory(_dir);
        string setup = Path.Combine(_dir, "MaszynyAlert-Setup-0.4.99.0.exe");
        File.WriteAllText(setup, "instalator");
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setup)));
        string work = Path.Combine(_dir, "downloads");
        string admin = Path.Combine(_dir, "updates");
        string taskName = "VNC Alert test " + Guid.NewGuid().ToString("N");
        var installer = new WindowsUpdateInstaller(work, @"C:\nie-ma\MaszynyAlert.App.exe",
            NullLogger<WindowsUpdateInstaller>.Instance, admin, () => true, taskName);
        bool consentHint = false;
        installer.ConsentPending += _ => consentHint = true;

        var (result, error) = await installer.LaunchAsync(setup, new Version(0, 4, 99, 0), sha, default);

        Assert.Equal(InstallLaunchResult.Failed, result);
        Assert.Contains("zadani", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(consentHint, "W trybie zadania nie ma okna UAC — podpowiedź o UAC byłaby fałszywa.");
        string request = File.ReadAllText(Path.Combine(work, UpdateTaskContract.RequestFileName));
        Assert.True(UpdateTaskRequest.TryParse(request, out var parsed, out string parseError), parseError);
        Assert.Equal("MaszynyAlert-Setup-0.4.99.0.exe", parsed!.FileName);
    }

    [Fact]
    public void Program_JakoSystem_NieUruchamiaMonitoringu()
    {
        Assert.True(Program.IsLocalSystem(new System.Security.Principal.SecurityIdentifier("S-1-5-18")));
        Assert.False(Program.IsLocalSystem(new System.Security.Principal.SecurityIdentifier("S-1-5-32-544")));
        Assert.False(Program.IsLocalSystem(null));
    }

    [Fact]
    public void KlientKataloguWydan_NiePodazaZaPrzekierowaniem()
    {
        using var handler = UpdateService.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task LaunchAsync_PlikZInnaSumaNizWSpisie_NieUruchamiaPomocnika()
    {
        Directory.CreateDirectory(_dir);
        string setup = Path.Combine(_dir, "MaszynyAlert-Setup-0.4.99.0.exe");
        File.WriteAllText(setup, "podmieniony plik");
        string catalogSha = Convert.ToHexString(SHA256.HashData("oryginał"u8.ToArray())).ToLowerInvariant();
        var installer = new WindowsUpdateInstaller(Path.Combine(_dir, "work"), @"C:\nie-ma\MaszynyAlert.App.exe",
            NullLogger<WindowsUpdateInstaller>.Instance);

        var (result, error) = await installer.LaunchAsync(setup, new Version(0, 4, 99, 0), catalogSha, default);

        Assert.Equal(InstallLaunchResult.Failed, result);
        Assert.Contains("SHA-256", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "work", WindowsUpdateInstaller.ScriptFileName)),
            "Przy niezgodnej sumie pomocnik nie powinien być nawet przygotowany.");
    }
}
