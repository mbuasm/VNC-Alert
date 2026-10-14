using System.Security.Cryptography;

namespace MaszynyAlert.Core.Cluster;

public sealed class HmacSigner
{
    private const int MinKeyLengthBytes = 16;
    private const int GeneratedKeyLengthBytes = 32;
    private const int HeaderLength = 4;
    private const int HashLength = 32;
    private const int MinDatagramLength = HeaderLength + HashLength;

    /// <summary>Stały narzut zapieczętowanego pakietu (nagłówek + HMAC) — do liczenia porcji w <see cref="MessageCodec"/>.</summary>
    public const int SealOverheadBytes = HeaderLength + HashLength;

    private static readonly byte[] MagicHeader = "MAL1"u8.ToArray();

    private readonly byte[] _key;

    /// <summary>Tworzy podpisujący na podstawie klucza binarnego (co najmniej 16 bajtów).</summary>
    /// <exception cref="ArgumentException">Klucz jest krótszy niż 16 bajtów.</exception>
    public HmacSigner(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinKeyLengthBytes)
        {
            throw new ArgumentException(
                $"Klucz sieciowy jest za krótki (ma {key.Length} B, potrzeba co najmniej {MinKeyLengthBytes} B). " +
                "Wygeneruj nowy klucz w Ustawieniach.",
                nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    /// <summary>Losuje nowy, 32-bajtowy klucz sieciowy (kryptograficzny generator liczb losowych).</summary>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(GeneratedKeyLengthBytes);

    /// <summary>Tworzy podpisujący na podstawie klucza zakodowanego Base64 (postać do wklejenia w Ustawieniach).</summary>
    public static HmacSigner FromBase64(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Klucz sieciowy nie jest poprawnym ciągiem Base64.", nameof(base64), ex);
        }

        return new HmacSigner(key);
    }

    /// <summary>Klucz sieciowy zakodowany Base64 — do pokazania/skopiowania w oknie Ustawień.</summary>
    public string KeyBase64 => Convert.ToBase64String(_key);

    /// <summary>Pieczętuje ładunek: nagłówek "MAL1" + HMAC-SHA256(payload) + payload.</summary>
    public byte[] Seal(ReadOnlySpan<byte> payload)
    {
        byte[] result = new byte[HeaderLength + HashLength + payload.Length];
        MagicHeader.CopyTo(result.AsSpan(0, HeaderLength));
        HMACSHA256.HashData(_key, payload, result.AsSpan(HeaderLength, HashLength));
        payload.CopyTo(result.AsSpan(HeaderLength + HashLength));
        return result;
    }

    /// <summary>
    /// Weryfikuje i rozpieczętowuje odebrany datagram. Zwraca <see langword="false"/> z polskim powodem w
    /// <paramref name="reason"/>, gdy: pakiet jest za krótki, nagłówek się nie zgadza, albo podpis HMAC jest
    /// niepoprawny (porównanie w stałym czasie — <see cref="CryptographicOperations.FixedTimeEquals"/> — żeby
    /// nie dało się go odgadnąć przez pomiar czasu odpowiedzi). Nigdy nie rzuca dla źle sformułowanego datagramu.
    /// </summary>
    public bool TryOpen(byte[] datagram, out byte[] payload, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(datagram);

        if (datagram.Length < MinDatagramLength)
        {
            payload = [];
            reason = "za krótki pakiet";
            return false;
        }

        if (!datagram.AsSpan(0, HeaderLength).SequenceEqual(MagicHeader))
        {
            payload = [];
            reason = "zły nagłówek";
            return false;
        }

        ReadOnlySpan<byte> receivedHash = datagram.AsSpan(HeaderLength, HashLength);
        ReadOnlySpan<byte> payloadSpan = datagram.AsSpan(MinDatagramLength);

        Span<byte> expectedHash = stackalloc byte[HashLength];
        HMACSHA256.HashData(_key, payloadSpan, expectedHash);

        if (!CryptographicOperations.FixedTimeEquals(receivedHash, expectedHash))
        {
            payload = [];
            reason = "niepoprawny podpis HMAC";
            return false;
        }

        payload = payloadSpan.ToArray();
        reason = null;
        return true;
    }
}
