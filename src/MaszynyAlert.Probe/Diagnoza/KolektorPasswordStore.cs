using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Windows;

namespace MaszynyAlert.Probe.Diagnoza;

public static class KolektorPasswordStore
{
    public const string FileName = "hasla.dpapi";

    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("diagnoza-hali.hasla.v1"));

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "diagnoza-hali", FileName);

    /// <summary>Otwiera magazyn (plik może jeszcze nie istnieć — powstaje przy pierwszym zapisie).</summary>
    public static ISecretStore Open(string? path = null)
    {
        string file = string.IsNullOrWhiteSpace(path) ? DefaultPath : Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new DpapiSecretStore(file, DataProtectionScope.CurrentUser, Entropy);
    }

    /// <summary>
    /// Czy DPAPI działa w tej sesji (próbny Protect/Unprotect). W sesjach bez dostępu do DPAPI (<c>CryptographicException:
    /// Odmowa dostępu</c>) komenda <c>haslo</c> i kolektor mają powiedzieć to wprost, a testy — pominąć się, nie udawać.
    /// </summary>
    public static bool IsDpapiAvailable(out string? error)
    {
        try
        {
            byte[] probe = ProtectedData.Protect([1, 2, 3], Entropy, DataProtectionScope.CurrentUser);
            byte[] back = ProtectedData.Unprotect(probe, Entropy, DataProtectionScope.CurrentUser);
            error = back.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }) ? null : "DPAPI zwróciło inne dane niż zaszyfrowano";
            return error is null;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
