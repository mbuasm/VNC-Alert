using System.Security.Cryptography;
using System.Text;

namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>
/// Referencyjna (niezależna od <c>MaszynyAlert.Core.Rfb.VncDes</c>) implementacja odpowiedzi VncAuth,
/// oparta o <see cref="DES"/> z biblioteki standardowej (ECB, bez paddingu) i klucz VNC: hasło
/// w Latin1 ucięte/dopełnione zerami do 8 bajtów, każdy bajt z odwróconą kolejnością bitów.
/// Używana przez <see cref="FakeRfbServer"/> do sprawdzania odpowiedzi klienta oraz przez testy
/// jako wzorzec porównawczy.
/// </summary>
internal static class ReferenceVncDes
{
    /// <summary>Wyprowadza 8-bajtowy klucz DES z hasła VNC.</summary>
    public static byte[] DeriveKey(string? password)
    {
        byte[] raw = Encoding.Latin1.GetBytes(password ?? string.Empty);
        var key = new byte[8];
        Array.Copy(raw, key, Math.Min(8, raw.Length));
        for (int i = 0; i < 8; i++)
        {
            key[i] = ReverseBits(key[i]);
        }

        return key;
    }

    /// <summary>
    /// Prawda, gdy klucz wyprowadzony z hasła jest słabym/półsłabym kluczem DES — <see cref="DES"/>
    /// z .NET odmawia wtedy szyfrowania, więc taki wektor testowy trzeba pominąć.
    /// </summary>
    public static bool IsRejectedByDotNetDes(string? password)
    {
        byte[] key = DeriveKey(password);
        return DES.IsWeakKey(key) || DES.IsSemiWeakKey(key);
    }

    /// <summary>Szyfruje 16-bajtowe wyzwanie (2 bloki DES-ECB) kluczem wyprowadzonym z hasła.</summary>
    public static byte[] EncryptChallenge(ReadOnlySpan<byte> challenge16, string? password)
    {
        if (challenge16.Length != 16)
        {
            throw new ArgumentException("Wyzwanie VncAuth musi mieć 16 bajtów.", nameof(challenge16));
        }

        byte[] key = DeriveKey(password);
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;

        using ICryptoTransform encryptor = des.CreateEncryptor();
        byte[] input = challenge16.ToArray();
        var output = new byte[16];
        encryptor.TransformBlock(input, 0, 16, output, 0);
        return output;
    }

    private static byte ReverseBits(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        b = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        return b;
    }
}
