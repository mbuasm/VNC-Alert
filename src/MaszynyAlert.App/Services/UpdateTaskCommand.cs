using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.Updates;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Services;

internal static class UpdateTaskCommand
{
    /// <summary>Kod 0 — plik przyjęty, 6 — odrzucony (<see cref="UpdateTaskContract.ExitRejected"/>), 5 — błąd.</summary>
    public static int Run()
    {
        if (!AppPaths.IsInstalled) return 5;
        string dataDirectory = AppPaths.DataDir;
        string admin = UpdateTaskContract.AdminDirectory(dataDirectory);
        UpdateTaskVerification result;
        try
        {
            AppConfig config = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath);
            var secrets = DpapiSecretStore.ForMachine(dataDirectory);
            var clock = new SystemClock();
            using var handler = UpdateService.CreateHandler();
            var source = new HttpUpdateSource(handler, config.Updates,
                () => secrets.Get(SecretKeys.UpdatesAccessClientSecret), clock);
            var verifier = new UpdateTaskVerifier(dataDirectory, InstalledVersion(), config.Updates, source, clock, CreateProtectedFile);
            result = verifier.VerifyAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new UpdateTaskVerification { Accepted = false, Message = "Zadanie aktualizacji nie sprawdziło pliku: " + ex.Message };
        }

        try
        {
            File.WriteAllText(Path.Combine(admin, UpdateTaskContract.VerifiedFileName), result.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 5;
        }
        return result.Accepted ? 0 : UpdateTaskContract.ExitRejected;
    }

    private static Version InstalledVersion() =>
        ReleaseCatalog.ParseInstalledVersion(BuildIdentity.FromInformationalVersion(
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion).Version);

    /// <summary>
    /// Plik kopii instalatora z jawną listą uprawnień: tylko SYSTEM i Administratorzy, bez dziedziczenia — operator nie
    /// odczyta zawartości, zanim suma się zgodzi (plik operatora mógł być dowiązaniem do cudzych danych), i nie podmieni
    /// kopii przed uruchomieniem.
    /// </summary>
    internal static Stream CreateProtectedFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), administrators })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
            FileShare.None, 81920, FileOptions.None, security);
    }
}
