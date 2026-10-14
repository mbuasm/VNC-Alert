using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MaszynyAlert.Windows;
using Microsoft.Win32.SafeHandles;

// Osobny proces testowy; nigdy nie uruchamia aplikacji ani połączeń VNC.
if (args.Length != 2) throw new ArgumentException("Expected: seed|verify|update|vnc-password|operator <data directory>");
string directory = Path.GetFullPath(args[1]);
const string key = "installer-test";
const string original = "synthetic-DPAPI-before-upgrade";
const string changed = "synthetic-DPAPI-after-upgrade";
var store = DpapiSecretStore.ForMachine(directory);
switch (args[0])
{
    case "seed":
        store.Set(key, original);
        Verify(original);
        break;
    case "verify":
        Verify(original);
        break;
    case "update":
        Verify(original);
        store.Set(key, changed);
        Verify(changed);
        break;
    // Hasło VNC pierwszej włączonej maszyny: bez niego aplikacja otwiera przy starcie osobny
    // (podniesiony) edytor ustawień, a test instalatora ma uruchomić zwykły monitoring.
    case "vnc-password":
        store.Set(MaszynyAlert.Core.Abstractions.SecretKeys.VncPassword("aidite1"), "synthetic-installer-test");
        break;
    case "operator":
        // Faktyczny token bez grupy Administratorzy i bez przywilejów obejścia ACL.
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            byte[] bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var disabled = new[] { new Native.SidAndAttributes { Sid = pointer } };
                if (!Native.CreateRestrictedToken(identity.AccessToken, 1, 1, disabled, 0,
                        IntPtr.Zero, 0, IntPtr.Zero, out var token))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                using (token)
                    WindowsIdentity.RunImpersonated(token, () =>
                    {
                        using var restricted = WindowsIdentity.GetCurrent();
                        if (new WindowsPrincipal(restricted).IsInRole(WindowsBuiltInRole.Administrator))
                            throw new InvalidOperationException("Test token still has administrator rights.");
                        Verify(changed);
                        foreach (string name in new[] { "config.json", "rules.json", "secrets.dat" })
                        {
                            _ = File.ReadAllBytes(Path.Combine(directory, name));
                            ExpectDenied(() =>
                            {
                                using var stream = File.Open(Path.Combine(directory, name),
                                    FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                            });
                        }
                        ExpectDenied(() => File.WriteAllText(Path.Combine(directory, "forbidden.txt"), "test"));
                        // 'state' (znacznik komendy zatrzymania), 'group-instructions' (wspolne instrukcje
                        // grupy) i 'shared-config' (wspolna konfiguracja z Panelu) MUSZA byc zapisywalne bez
                        // UAC - inaczej ani "vncalert killapp", ani zmiana instrukcji czy ustawien odebrana
                        // z innego komputera nie zapisza sie u operatora.
                        foreach (string name in new[] { "logs", "captures", "test-ekran", "state", "group-instructions", "shared-config" })
                        {
                            string path = Path.Combine(directory, name, "installer-probe.txt");
                            File.WriteAllText(path, "test");
                            File.Delete(path);
                        }
                        Directory.CreateDirectory(Path.Combine(directory, "state", "updates"));
                        string downloads = Path.Combine(directory, "state", "updates", "installer-probe.txt");
                        File.WriteAllText(downloads, "test");
                        File.Delete(downloads);
                        ExpectDenied(() => File.WriteAllText(Path.Combine(directory, "updates-admin", "pending-install.json"), "{}"));
                        ExpectDenied(() => Directory.CreateDirectory(Path.Combine(directory, "updates-admin", "run-operator")));
                        ExpectDenied(() => Directory.CreateDirectory(Path.Combine(directory, "updates-admin-operator")));
                        File.AppendAllText(Path.Combine(directory, "history.csv"), "\noperator-test");
                        File.AppendAllText(Path.Combine(directory, "downtime.jsonl"), "\n");
                    });
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        break;
    default: throw new ArgumentException("Unknown operation.");
}
Console.WriteLine($"PASS: {args[0]} (real DPAPI/ACL)");

void Verify(string expected)
{
    if (DpapiSecretStore.ForMachine(directory).Get(key) != expected)
        throw new InvalidOperationException("DPAPI value did not survive reopening.");
    if (File.ReadAllText(Path.Combine(directory, "secrets.dat")).Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException("Secret stored as plaintext.");
}

void ExpectDenied(Action action)
{
    try { action(); }
    catch (UnauthorizedAccessException) { return; }
    throw new InvalidOperationException("Operator unexpectedly has write access to protected data.");
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateRestrictedToken(SafeAccessTokenHandle existingToken, uint flags,
        uint disableSidCount, [In] SidAndAttributes[] sidsToDisable, uint deletePrivilegeCount,
        IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict,
        out SafeAccessTokenHandle newToken);
}
