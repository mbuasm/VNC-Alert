using System.Security.Cryptography;

namespace MaszynyAlert.Core.Rfb.Crypto;

internal sealed class AesEax : IDisposable
{
    public const int BlockSize = 16;

    /// <summary>Długość znacznika uwierzytelniającego używana przez RFB RSA-AES (pełny blok).</summary>
    public const int TagSize = 16;

    private readonly AesCmac _cmac;
    private readonly Aes _aes;

    public AesEax(ReadOnlySpan<byte> key)
    {
        _cmac = new AesCmac(key);
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key.ToArray();
    }

    /// <summary>Szyfruje <paramref name="plaintext"/> i liczy znacznik obejmujący także <paramref name="header"/>.</summary>
    public void Encrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        if (ciphertext.Length != plaintext.Length)
        {
            throw new ArgumentException("Szyfrogram EAX ma tyle samo bajtów co tekst jawny.", nameof(ciphertext));
        }

        Span<byte> n = stackalloc byte[BlockSize];
        Span<byte> h = stackalloc byte[BlockSize];
        Omac(0, nonce, n);
        Omac(1, header, h);

        Ctr(n, plaintext, ciphertext);

        Span<byte> c = stackalloc byte[BlockSize];
        Omac(2, ciphertext, c);

        for (int i = 0; i < TagSize; i++)
        {
            tag[i] = (byte)(n[i] ^ h[i] ^ c[i]);
        }
    }

    /// <summary>
    /// Odszyfrowuje i sprawdza znacznik. Zwraca <see langword="false"/>, gdy znacznik się nie zgadza —
    /// wtedy <paramref name="plaintext"/> jest wyzerowany i NIE wolno go użyć.
    /// </summary>
    public bool TryDecrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext)
    {
        if (plaintext.Length != ciphertext.Length)
        {
            throw new ArgumentException("Tekst jawny EAX ma tyle samo bajtów co szyfrogram.", nameof(plaintext));
        }

        Span<byte> n = stackalloc byte[BlockSize];
        Span<byte> h = stackalloc byte[BlockSize];
        Span<byte> c = stackalloc byte[BlockSize];
        Omac(0, nonce, n);
        Omac(1, header, h);
        Omac(2, ciphertext, c);

        Span<byte> expected = stackalloc byte[TagSize];
        for (int i = 0; i < TagSize; i++)
        {
            expected[i] = (byte)(n[i] ^ h[i] ^ c[i]);
        }

        // Porównanie w stałym czasie — znacznik rozstrzyga o autentyczności pakietu.
        if (!CryptographicOperations.FixedTimeEquals(expected, tag[..TagSize]))
        {
            plaintext.Clear();
            return false;
        }

        Ctr(n, ciphertext, plaintext);
        return true;
    }

    /// <summary><c>OMAC^t(M) = CMAC(blok_t ‖ M)</c>, gdzie blok_t to 16 zer z ostatnim bajtem <paramref name="t"/>.</summary>
    private void Omac(byte t, ReadOnlySpan<byte> message, Span<byte> tag)
    {
        byte[] buffer = new byte[BlockSize + message.Length];
        buffer[BlockSize - 1] = t;
        message.CopyTo(buffer.AsSpan(BlockSize));
        try
        {
            _cmac.ComputeTag(buffer, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>AES-CTR: licznik to <paramref name="counter"/> traktowany jako 128-bitowa liczba big-endian.</summary>
    private void Ctr(ReadOnlySpan<byte> counter, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> block = stackalloc byte[BlockSize];
        Span<byte> keystream = stackalloc byte[BlockSize];
        counter.CopyTo(block);

        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            _aes.EncryptEcb(block, keystream, PaddingMode.None);
            int count = Math.Min(BlockSize, input.Length - offset);
            for (int i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            }

            IncrementBigEndian(block);
        }
    }

    private static void IncrementBigEndian(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cmac.Dispose();
        _aes.Dispose();
    }
}
