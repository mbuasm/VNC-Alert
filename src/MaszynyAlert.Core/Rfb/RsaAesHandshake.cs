using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MaszynyAlert.Core.Rfb;

internal static class RsaAesHandshake
{
    /// <summary>Najkrótszy akceptowany klucz RSA serwera (bity). Krótszy klucz to nie jest ochrona, tylko jej pozór.</summary>
    private const int MinKeyBits = 1024;

    /// <summary>Najdłuższy akceptowany klucz RSA serwera (bity) — zapora przed absurdalną alokacją z uszkodzonej odpowiedzi.</summary>
    private const int MaxKeyBits = 8192;

    internal const byte SubtypeUserAndPassword = 1;

    /// <summary>Wariant poświadczeń: samo hasło VNC.</summary>
    internal const byte SubtypePasswordOnly = 2;

    public static bool IsRsaAes(byte securityType) => securityType is 5 or 6 or 129 or 130;

    /// <summary>Czy po uwierzytelnieniu sesja ma zostać zaszyfrowana (RA2/RA2_256), czy wrócić do jawnej (…ne).</summary>
    public static bool KeepsSessionEncrypted(byte securityType) => securityType is 5 or 129;

    /// <summary>Rozmiar klucza AES w bajtach: 16 dla RA2/RA2ne, 32 dla wariantów 256-bitowych.</summary>
    private static int AesKeyBytes(byte securityType) => securityType is 129 or 130 ? 32 : 16;

    /// <summary>
    /// Wykonuje całe uwierzytelnienie. Zwraca strumień, przez który ma iść dalsza część sesji: przy
    /// wariantach szyfrowanych jest to <see cref="RsaAesStream"/>, przy <c>…ne</c> — <paramref name="raw"/>.
    /// </summary>
    public static async Task<RsaAesResult> AuthenticateAsync(
        Stream raw,
        RfbBinaryReader reader,
        byte securityType,
        string? username,
        string? password,
        string endpointDescription,
        TimeSpan readTimeout,
        string? expectedServerKeySha256,
        CancellationToken ct)
    {
        int aesKeyBytes = AesKeyBytes(securityType);
        bool sha256 = aesKeyBytes == 32;

        // --- 1. Klucz publiczny serwera -------------------------------------------------------
        uint serverKeyBits = await reader.ReadUInt32Async(ct).ConfigureAwait(false);
        if (serverKeyBits is < MinKeyBits or > MaxKeyBits)
        {
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} podał klucz RSA o długości {serverKeyBits} bitów — " +
                $"poza dopuszczalnym zakresem {MinKeyBits}–{MaxKeyBits}.");
        }

        int serverKeyLen = ((int)serverKeyBits + 7) / 8;
        byte[] serverModulus = await reader.ReadExactlyAsync(serverKeyLen, ct).ConfigureAwait(false);
        byte[] serverExponent = await reader.ReadExactlyAsync(serverKeyLen, ct).ConfigureAwait(false);

        var exponentValue = new System.Numerics.BigInteger(serverExponent, isUnsigned: true, isBigEndian: true);
        if (exponentValue < 3 || exponentValue.IsEven)
        {
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} podał klucz RSA ze słabym wykładnikiem ({exponentValue}) — logowanie przerwane przed wysłaniem hasła.");
        }

        // Odcisk klucza serwera (zawsze liczony — trafia do logu, żeby administrator mógł go przypiąć w config.json).
        string serverKeySha256 = Convert.ToHexString(SHA256.HashData([.. serverModulus, .. serverExponent])).ToLowerInvariant();
        if (expectedServerKeySha256 is { Length: > 0 } expected
            && !string.Equals(expected, serverKeySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RfbAuthenticationException(
                $"Klucz RSA serwera {endpointDescription} ma odcisk {serverKeySha256}, a config.json oczekuje {expected.ToLowerInvariant()} — " +
                "logowanie przerwane przed wysłaniem hasła. Jeśli serwer VNC zainstalowano ponownie, wpisz nowy odcisk (machines[].rsaAesKeySha256).");
        }

        using RSA serverRsa = RSA.Create();
        try
        {
            serverRsa.ImportParameters(new RSAParameters
            {
                Modulus = TrimLeadingZeros(serverModulus),
                Exponent = TrimLeadingZeros(serverExponent),
            });
        }
        catch (CryptographicException ex)
        {
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} przysłał klucz publiczny RSA, którego nie da się wczytać: {ex.Message}");
        }

        // --- 2. Klucz publiczny klienta -------------------------------------------------------
        // Odpowiadamy kluczem tej samej długości co serwer: taką długość serwer na pewno akceptuje.
        int clientKeyBits = serverKeyBits % 8 == 0 ? (int)serverKeyBits : 2048;
        using RSA clientRsa = RSA.Create(clientKeyBits);
        RSAParameters clientPublic = clientRsa.ExportParameters(includePrivateParameters: false);
        int clientKeyLen = (clientKeyBits + 7) / 8;

        byte[] clientModulus = LeftPad(clientPublic.Modulus!, clientKeyLen);
        byte[] clientExponent = LeftPad(clientPublic.Exponent!, clientKeyLen);

        byte[] clientKeyMessage = new byte[4 + (clientKeyLen * 2)];
        BinaryPrimitives.WriteUInt32BigEndian(clientKeyMessage.AsSpan(0, 4), (uint)clientKeyBits);
        clientModulus.CopyTo(clientKeyMessage, 4);
        clientExponent.CopyTo(clientKeyMessage, 4 + clientKeyLen);
        await raw.WriteAsync(clientKeyMessage, ct).ConfigureAwait(false);

        // --- 3. Wymiana losowych ciągów -------------------------------------------------------
        byte[] clientRandom = RandomNumberGenerator.GetBytes(aesKeyBytes);
        byte[] encryptedClientRandom;
        try
        {
            encryptedClientRandom = serverRsa.Encrypt(clientRandom, RSAEncryptionPadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new RfbAuthenticationException(
                $"Nie udało się zaszyfrować losowego ciągu kluczem serwera {endpointDescription}: {ex.Message}");
        }

        byte[] randomMessage = new byte[2 + encryptedClientRandom.Length];
        BinaryPrimitives.WriteUInt16BigEndian(randomMessage.AsSpan(0, 2), (ushort)encryptedClientRandom.Length);
        encryptedClientRandom.CopyTo(randomMessage, 2);
        await raw.WriteAsync(randomMessage, ct).ConfigureAwait(false);

        ushort serverRandomLength = await reader.ReadUInt16Async(ct).ConfigureAwait(false);
        if (serverRandomLength != clientKeyLen)
        {
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} przysłał losowy ciąg zaszyfrowany na {serverRandomLength} bajtach, " +
                $"a nasz klucz ma {clientKeyLen} — niezgodny przebieg uwierzytelnienia RSA-AES.");
        }

        byte[] encryptedServerRandom = await reader.ReadExactlyAsync(serverRandomLength, ct).ConfigureAwait(false);
        byte[] serverRandom;
        try
        {
            serverRandom = clientRsa.Decrypt(encryptedServerRandom, RSAEncryptionPadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new RfbAuthenticationException(
                $"Nie udało się odszyfrować losowego ciągu od serwera {endpointDescription}: {ex.Message}");
        }

        if (serverRandom.Length != aesKeyBytes)
        {
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} przysłał losowy ciąg o długości {serverRandom.Length} B zamiast {aesKeyBytes} B.");
        }

        // --- 4. Klucze sesji ------------------------------------------------------------------
        // Kierunki są różne po obu stronach: tym, co my wysyłamy, szyfruje klucz "klienta".
        byte[] writeKey = DeriveKey(sha256, aesKeyBytes, serverRandom, clientRandom);
        byte[] readKey = DeriveKey(sha256, aesKeyBytes, clientRandom, serverRandom);

        var secure = new RsaAesStream(raw, readKey, writeKey, endpointDescription);
        var secureReader = new RfbBinaryReader(secure, readTimeout, endpointDescription);

        // --- 5. Skróty obu kluczy publicznych (już przez szyfrowany kanał) ---------------------
        byte[] serverKeyWire = KeyOnTheWire(serverKeyBits, serverModulus, serverExponent);
        byte[] clientKeyWire = KeyOnTheWire((uint)clientKeyBits, clientModulus, clientExponent);

        int hashLength = sha256 ? 32 : 20;
        byte[] serverHash = await secureReader.ReadExactlyAsync(hashLength, ct).ConfigureAwait(false);
        byte[] expectedServerHash = Hash(sha256, serverKeyWire, clientKeyWire);
        if (!CryptographicOperations.FixedTimeEquals(serverHash, expectedServerHash))
        {
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} nie potwierdził własnego klucza publicznego (niezgodny skrót). " +
                "Połączenie przerwane bez wysyłania hasła.");
        }

        await secure.WriteAsync(Hash(sha256, clientKeyWire, serverKeyWire), ct).ConfigureAwait(false);

        // --- 6. Poświadczenia -----------------------------------------------------------------
        byte subtype = await secureReader.ReadByteAsync(ct).ConfigureAwait(false);
        if (subtype is not (SubtypeUserAndPassword or SubtypePasswordOnly))
        {
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} zażądał nieznanego wariantu poświadczeń RSA-AES ({subtype}).");
        }

        if (string.IsNullOrEmpty(password))
        {
            // Fraza „wymaga hasła VNC” jest celowa: po niej probe.exe rozpoznaje, że ma zapytać
            // operatora o hasło i ponowić próbę (ConnectionHelper.IsRequiresPassword).
            throw new RfbAuthenticationException(
                $"Serwer {endpointDescription} wymaga hasła VNC (szyfrowane logowanie RSA-AES), ale w " +
                "konfiguracji nie podano hasła dla tej maszyny — wpisz je w Ustawieniach.");
        }

        bool emptyUsername = subtype == SubtypeUserAndPassword && string.IsNullOrEmpty(username);

        await secure.WriteAsync(
            BuildCredentials(subtype == SubtypeUserAndPassword ? username : null, password),
            ct).ConfigureAwait(false);

        return KeepsSessionEncrypted(securityType)
            ? new RsaAesResult(secure, secureReader, Encrypted: true, subtype, emptyUsername, serverKeySha256)
            : new RsaAesResult(raw, reader, Encrypted: false, subtype, emptyUsername, serverKeySha256);
    }

    /// <summary>Klucz sesji: skrót ze sklejonych losowych ciągów, przycięty do długości klucza AES.</summary>
    private static byte[] DeriveKey(bool sha256, int keyBytes, byte[] first, byte[] second)
    {
        byte[] digest = Hash(sha256, first, second);
        return digest[..keyBytes];
    }

    private static byte[] Hash(bool sha256, byte[] first, byte[] second)
    {
        byte[] joined = new byte[first.Length + second.Length];
        first.CopyTo(joined, 0);
        second.CopyTo(joined, first.Length);
        return sha256 ? SHA256.HashData(joined) : SHA1.HashData(joined);
    }

    /// <summary>Klucz publiczny dokładnie w takiej postaci, w jakiej idzie po sieci — to ona wchodzi do skrótu.</summary>
    private static byte[] KeyOnTheWire(uint keyBits, byte[] modulus, byte[] exponent)
    {
        byte[] wire = new byte[4 + modulus.Length + exponent.Length];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), keyBits);
        modulus.CopyTo(wire, 4);
        exponent.CopyTo(wire, 4 + modulus.Length);
        return wire;
    }

    private static byte[] BuildCredentials(string? username, string password)
    {
        byte[] user = Encoding.UTF8.GetBytes(username ?? string.Empty);
        byte[] pass = Encoding.UTF8.GetBytes(password);
        if (user.Length > 255 || pass.Length > 255)
        {
            throw new RfbAuthenticationException(
                "Nazwa użytkownika albo hasło do logowania RSA-AES są dłuższe niż 255 bajtów.");
        }

        byte[] message = new byte[1 + user.Length + 1 + pass.Length];
        message[0] = (byte)user.Length;
        user.CopyTo(message, 1);
        message[1 + user.Length] = (byte)pass.Length;
        pass.CopyTo(message, 2 + user.Length);
        return message;
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

    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        if (value.Length > length)
        {
            throw new RfbAuthenticationException(
                $"Element klucza RSA ma {value.Length} B i nie mieści się w {length} B.");
        }

        byte[] padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }
}

/// <summary>Strumień i czytnik, przez które ma iść dalsza część sesji po uwierzytelnieniu RSA-AES.</summary>
/// <param name="CredentialSubtype">Wariant poświadczeń, którego zażądał serwer: 1 = nazwa użytkownika i hasło (logowanie systemowe), 2 = samo hasło VNC.</param>
/// <param name="SentEmptyUsername">Prawda, gdy serwer zażądał nazwy użytkownika, a w konfiguracji jej nie było i poszła nazwa pusta — wtedy odmowa serwera znaczy „ta nazwa jest jednak potrzebna”.</param>
internal sealed record RsaAesResult(
    Stream Transport,
    RfbBinaryReader Reader,
    bool Encrypted,
    byte CredentialSubtype,
    bool SentEmptyUsername,
    string ServerKeySha256);
