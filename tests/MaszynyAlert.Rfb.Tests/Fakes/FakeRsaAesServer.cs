using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Rfb.Tests.Fakes;

internal static class FakeRsaAesServer
{
    public const byte SubtypeUserAndPassword = 1;
    public const byte SubtypePasswordOnly = 2;

    public static async Task<FakeRsaAesOutcome> AuthenticateAsync(
        Stream raw,
        byte securityType,
        FakeRsaAesOptions options,
        CancellationToken ct)
    {
        int aesKeyBytes = securityType is 129 or 130 ? 32 : 16;
        bool sha256 = aesKeyBytes == 32;

        // 1. Klucz publiczny serwera.
        using RSA ownRsa = RSA.Create(options.ServerKeyBits);
        RSA serverRsa = options.FixedServerKey ?? ownRsa;
        RSAParameters serverPublic = serverRsa.ExportParameters(includePrivateParameters: false);
        int serverKeyLen = (options.ServerKeyBits + 7) / 8;
        byte[] serverModulus = LeftPad(serverPublic.Modulus!, serverKeyLen);
        byte[] serverExponent = LeftPad(options.OverrideServerExponent ?? serverPublic.Exponent!, serverKeyLen);

        byte[] serverKeyMessage = new byte[4 + (serverKeyLen * 2)];
        BinaryPrimitives.WriteUInt32BigEndian(serverKeyMessage.AsSpan(0, 4), (uint)options.ServerKeyBits);
        serverModulus.CopyTo(serverKeyMessage, 4);
        serverExponent.CopyTo(serverKeyMessage, 4 + serverKeyLen);
        await raw.WriteAsync(serverKeyMessage, ct).ConfigureAwait(false);

        // 2. Klucz publiczny klienta.
        byte[] clientKeyHeader = await ReadExactlyAsync(raw, 4, ct).ConfigureAwait(false);
        int clientKeyBits = (int)BinaryPrimitives.ReadUInt32BigEndian(clientKeyHeader);
        int clientKeyLen = (clientKeyBits + 7) / 8;
        byte[] clientModulus = await ReadExactlyAsync(raw, clientKeyLen, ct).ConfigureAwait(false);
        byte[] clientExponent = await ReadExactlyAsync(raw, clientKeyLen, ct).ConfigureAwait(false);

        using RSA clientRsa = RSA.Create();
        clientRsa.ImportParameters(new RSAParameters
        {
            Modulus = TrimLeadingZeros(clientModulus),
            Exponent = TrimLeadingZeros(clientExponent),
        });

        // 3. Wymiana losowych ciągów.
        byte[] clientRandomHeader = await ReadExactlyAsync(raw, 2, ct).ConfigureAwait(false);
        int clientRandomLength = BinaryPrimitives.ReadUInt16BigEndian(clientRandomHeader);
        byte[] encryptedClientRandom = await ReadExactlyAsync(raw, clientRandomLength, ct).ConfigureAwait(false);
        byte[] clientRandom = serverRsa.Decrypt(encryptedClientRandom, RSAEncryptionPadding.Pkcs1);

        byte[] serverRandom = RandomNumberGenerator.GetBytes(aesKeyBytes);
        byte[] encryptedServerRandom = clientRsa.Encrypt(serverRandom, RSAEncryptionPadding.Pkcs1);
        byte[] serverRandomMessage = new byte[2 + encryptedServerRandom.Length];
        BinaryPrimitives.WriteUInt16BigEndian(serverRandomMessage.AsSpan(0, 2), (ushort)encryptedServerRandom.Length);
        encryptedServerRandom.CopyTo(serverRandomMessage, 2);
        await raw.WriteAsync(serverRandomMessage, ct).ConfigureAwait(false);

        // 4. Klucze sesji — lustrzanie do klienta: my czytamy tym, czym on pisze.
        byte[] serverWriteKey = Hash(sha256, clientRandom, serverRandom)[..aesKeyBytes];
        byte[] serverReadKey = Hash(sha256, serverRandom, clientRandom)[..aesKeyBytes];
        var secure = new RsaAesStream(raw, serverReadKey, serverWriteKey, "fake-server");

        // 5. Skróty obu kluczy publicznych.
        byte[] serverKeyWire = KeyOnTheWire((uint)options.ServerKeyBits, serverModulus, serverExponent);
        byte[] clientKeyWire = KeyOnTheWire((uint)clientKeyBits, clientModulus, clientExponent);
        int hashLength = sha256 ? 32 : 20;

        byte[] serverHash = Hash(sha256, serverKeyWire, clientKeyWire);
        if (options.CorruptServerHash)
        {
            serverHash[0] ^= 0xFF;
        }

        await secure.WriteAsync(serverHash, ct).ConfigureAwait(false);

        byte[] clientHash = await ReadExactlyAsync(secure, hashLength, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(clientHash, Hash(sha256, clientKeyWire, serverKeyWire)))
        {
            return new FakeRsaAesOutcome(null, false);
        }

        // 6. Poświadczenia.
        await secure.WriteAsync(new[] { options.Subtype }, ct).ConfigureAwait(false);

        byte[] usernameLength = await ReadExactlyAsync(secure, 1, ct).ConfigureAwait(false);
        byte[] username = await ReadExactlyAsync(secure, usernameLength[0], ct).ConfigureAwait(false);
        byte[] passwordLength = await ReadExactlyAsync(secure, 1, ct).ConfigureAwait(false);
        byte[] password = await ReadExactlyAsync(secure, passwordLength[0], ct).ConfigureAwait(false);

        options.ReceivedUsername = Encoding.UTF8.GetString(username);
        options.ReceivedPassword = Encoding.UTF8.GetString(password);

        bool credentialsOk = options.ExpectedPassword is null
            || (options.ReceivedPassword == options.ExpectedPassword
                && (options.Subtype != SubtypeUserAndPassword || options.ReceivedUsername == options.ExpectedUsername));

        // Warianty „ne” wracają po uwierzytelnieniu do zwykłego strumienia.
        Stream continuation = securityType is 5 or 129 ? secure : raw;
        return new FakeRsaAesOutcome(continuation, credentialsOk);
    }

    private static byte[] Hash(bool sha256, byte[] first, byte[] second)
    {
        byte[] joined = new byte[first.Length + second.Length];
        first.CopyTo(joined, 0);
        second.CopyTo(joined, first.Length);
        return sha256 ? SHA256.HashData(joined) : SHA1.HashData(joined);
    }

    private static byte[] KeyOnTheWire(uint keyBits, byte[] modulus, byte[] exponent)
    {
        byte[] wire = new byte[4 + modulus.Length + exponent.Length];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), keyBits);
        modulus.CopyTo(wire, 4);
        exponent.CopyTo(wire, 4 + modulus.Length);
        return wire;
    }

    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        byte[] padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }

    private static byte[] TrimLeadingZeros(byte[] value)
    {
        int start = 0;
        while (start < value.Length - 1 && value[start] == 0)
        {
            start++;
        }

        return start == 0 ? value : value[start..];
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Klient zamknął połączenie w trakcie uwierzytelnienia RSA-AES.");
            }

            total += read;
        }

        return buffer;
    }
}

/// <summary>Wynik uwierzytelnienia RSA-AES po stronie fałszywego serwera.</summary>
/// <param name="Stream">Strumień dalszej sesji, albo <see langword="null"/>, gdy nie udał się sam handshake.</param>
/// <param name="CredentialsOk">Czy nazwa użytkownika i hasło są zgodne z oczekiwanymi.</param>
internal sealed record FakeRsaAesOutcome(Stream? Stream, bool CredentialsOk);

/// <summary>Ustawienia strony serwerowej RSA-AES w <see cref="FakeRfbServer"/>.</summary>
internal sealed class FakeRsaAesOptions
{
    /// <summary>Długość klucza RSA serwera w bitach.</summary>
    public int ServerKeyBits { get; set; } = 2048;

    /// <summary>Wariant poświadczeń zgłaszany klientowi.</summary>
    public byte Subtype { get; set; } = FakeRsaAesServer.SubtypePasswordOnly;

    /// <summary>Oczekiwane hasło; <see langword="null"/> = serwer akceptuje dowolne.</summary>
    public string? ExpectedPassword { get; set; }

    /// <summary>Oczekiwana nazwa użytkownika (tylko przy <see cref="FakeRsaAesServer.SubtypeUserAndPassword"/>).</summary>
    public string? ExpectedUsername { get; set; }

    /// <summary>Tryb awarii: serwer psuje własny skrót klucza — klient ma przerwać PRZED wysłaniem hasła.</summary>
    public bool CorruptServerHash { get; set; }

    /// <summary>Stały klucz serwera (przypinanie odcisku); <see langword="null"/> = nowy losowy klucz przy każdym połączeniu.</summary>
    public RSA? FixedServerKey { get; set; }

    /// <summary>Tryb awarii: serwer podaje ten wykładnik zamiast prawdziwego (np. 0 albo 1 — słaby klucz).</summary>
    public byte[]? OverrideServerExponent { get; set; }

    /// <summary>Nazwa użytkownika faktycznie odebrana od klienta (do asercji w teście).</summary>
    public string? ReceivedUsername { get; set; }

    /// <summary>Hasło faktycznie odebrane od klienta (do asercji w teście).</summary>
    public string? ReceivedPassword { get; set; }
}
