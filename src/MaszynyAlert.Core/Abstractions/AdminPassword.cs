using System.Security.Cryptography;

namespace MaszynyAlert.Core.Abstractions;

public static class AdminPassword
{
    public const string SecretKey = "admin-password-hash";
    private const int Iterations = 600_000;

    public static bool VerifyLogin(string password, Func<string?> readStoredHash) =>
        VerifyHash(password, readStoredHash());

    public static string Hash(string password)
    {
        if (password.Length < 10) throw new ArgumentException("Hasło musi mieć co najmniej 10 znaków.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
        => VerifyHash(password, stored);

    private static bool VerifyHash(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 2) return false;
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
