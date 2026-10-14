using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Windows;

public enum NetworkProfileChangeOutcome { Changed, AlreadyPrivate, Denied, Failed }

public sealed record NetworkProfileChangeResult(NetworkProfileChangeOutcome Outcome, string Message);

public static class NetworkProfileHelper
{
    public const string Argument = "--set-private-network-profile";
    public const string ChangeLogFileName = "network-profile-changes.log";
    private const int ErrorCancelled = 1223;

    /// <summary>Operator side: starts the elevated process and waits for it. Its exit code and the change log say
    /// what happened; the caller re-checks readiness for the truth.</summary>
    public static async Task<NetworkProfileChangeResult> RunElevatedAsync(string address, CancellationToken ct)
    {
        if (!GroupFirewall.IsUnicastIPv4(address, out _))
            return new(NetworkProfileChangeOutcome.Failed, "Nieprawidłowy adres interfejsu.");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(Argument); start.ArgumentList.Add(address);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Nie uruchomiono procesu zmiany profilu sieci."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        { return new(NetworkProfileChangeOutcome.Denied, "Odmówiono uprawnień administratora. Profil sieci pozostaje bez zmian."); }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(GroupFirewall.Timeout + TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return new(NetworkProfileChangeOutcome.Failed, "Proces zmiany profilu sieci nie zakończył się w wyznaczonym czasie. Sprawdź stan przyciskiem Sprawdź ponownie."); }
            return process.ExitCode switch
            {
                0 => new(NetworkProfileChangeOutcome.Changed, "Profil sieci ustawiono na Prywatny. Dla sieci niezidentyfikowanej ustawiono też zasadę lokalną "
                    + "„Sieci niezidentyfikowane → Prywatna”, żeby profil przetrwał restart. Wpis w " + ChangeLogFileName + "."),
                3 => new(NetworkProfileChangeOutcome.AlreadyPrivate, "Profil sieci już nie był Publiczny, a zasada dla sieci niezidentyfikowanej była ustawiona; nic nie zmieniono."),
                2 => new(NetworkProfileChangeOutcome.Failed, "Wskazany adres nie należy do żadnego rzeczywistego interfejsu LAN tego komputera; nic nie zmieniono."),
                _ => new(NetworkProfileChangeOutcome.Failed, "Nie udało się zmienić profilu sieci. Szczegóły są w pliku "
                    + GroupFirewall.ErrorLogFileName + " w katalogu ustawień grupy."),
            };
        }
    }

    /// <summary>Elevated side. Exit codes: 0 profile or policy changed, 3 nothing to change, 2 address rejected,
    /// 1 PowerShell failure (reason in the firewall error log), 4 not elevated.</summary>
    public static int RunHelper(string address) => RunHelper(address, AppPaths.GroupSettingsDir, null,
        script => GroupFirewall.Execute(script, AppPaths.GroupSettingsDir));

    internal static int RunHelper(string address, string directory, IReadOnlyList<LanInterface>? interfaces, Func<string, string> execute)
    {
        if (interfaces is null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 4;
        }
        LanInterface lan;
        try { lan = LanInterface.Select(address, interfaces); }
        catch (InvalidOperationException) { return 2; }
        string result;
        try { result = execute(Script(lan.Address.ToString())); }
        catch (Exception ex) when (ex is IOException or Win32Exception) { return 1; }
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, ChangeLogFileName),
                $"{DateTimeOffset.Now:O} interface={lan.Name} address={lan.Address} result={result}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception) { /* the change itself is done; the log is for IT only */ }
        return result.StartsWith("UNCHANGED", StringComparison.Ordinal) && !result.Contains("|POLICY_SET|", StringComparison.Ordinal) ? 3 : 0;
    }

    /// <summary>Registry form of the local policy "Network List Manager Policies → Unidentified Networks → Location
    /// type: Private" (what secpol.msc writes). Category 1 = Private. Read by the readiness check, written only here.</summary>
    public const string UnidentifiedNetworkPolicyKey = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\010103000F0000F0010000000F0000F0C967A3643C3AD745950DA7859209176EF5B87C875FA20DF21951640E807D7C24";
    internal const string UnidentifiedNamePattern = "niezident|unidentified";

    /// <summary>Only Public becomes Private; Domain and Private are left alone. For an unidentified network (no
    /// gateway, so Windows cannot remember the choice per network) the local policy for unidentified networks is set
    /// to Private as well, so the profile survives a restart. Both steps echo what they did for the change log; undo
    /// is <c>Set-NetConnectionProfile -NetworkCategory Public</c> and removing the policy value.</summary>
    internal static string Script(string address) => GroupFirewall.Wrap("SetPrivateProfile",
        "$ip=Get-NetIPAddress -AddressFamily IPv4 -IPAddress " + GroupFirewall.Quote(address) + " -ErrorAction SilentlyContinue | Select-Object -First 1\n"
        + "if (-not $ip) { throw ('NO_IP|' + " + GroupFirewall.Quote(address) + ") }\n"
        + "$network=Get-NetConnectionProfile -InterfaceIndex $ip.InterfaceIndex -ErrorAction SilentlyContinue | Select-Object -First 1\n"
        + "if (-not $network) { throw ('NO_PROFILE|' + $ip.InterfaceAlias + '|' + " + GroupFirewall.Quote(address) + ") }\n"
        + "if ($network.NetworkCategory -eq 'DomainAuthenticated') { 'UNCHANGED|Domain|' + $network.Name; exit 0 }\n"
        + "$profile='UNCHANGED|' + $network.NetworkCategory\n"
        + "if ($network.NetworkCategory -eq 'Public') { Set-NetConnectionProfile -InterfaceIndex $ip.InterfaceIndex -NetworkCategory Private; $profile='CHANGED|Public->Private' }\n"
        + "$policy='POLICY_NOT_NEEDED'\n"
        + "if ($network.Name -match '" + UnidentifiedNamePattern + "') {\n"
        + "  $key=" + GroupFirewall.Quote(UnidentifiedNetworkPolicyKey) + "\n"
        + "  $current=(Get-ItemProperty -Path $key -Name Category -ErrorAction SilentlyContinue).Category\n"
        + "  if ($current -eq 1) { $policy='POLICY_KEPT' } else { New-Item -Path $key -Force | Out-Null; Set-ItemProperty -Path $key -Name Category -Value 1 -Type DWord; $policy='POLICY_SET' }\n"
        + "}\n"
        + "$profile + '|' + $policy + '|' + $network.Name + '|' + $ip.InterfaceAlias\n", requireService: false);
}
