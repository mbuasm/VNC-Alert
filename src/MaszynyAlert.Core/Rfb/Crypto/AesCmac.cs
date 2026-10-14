using System.Security.Cryptography;

namespace MaszynyAlert.Core.Rfb.Crypto;

internal sealed class AesCmac : IDisposable
{
    private const int BlockSize = 16;

    /// <summary>Wielomian redukcji dla mnożenia przez x w GF(2^128) przy 128-bitowym bloku (SP 800-38B §6.1).</summary>
    private const byte Rb = 0x87;

    private readonly Aes _aes;
    private readonly byte[] _k1 = new byte[BlockSize];
    private readonly byte[] _k2 = new byte[BlockSize];

    public AesCmac(ReadOnlySpan<byte> key)
    {
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key.ToArray();

        // Podklucze: L = E(0^128), K1 = L·x, K2 = L·x².
        Span<byte> zero = stackalloc byte[BlockSize];
        Span<byte> l = stackalloc byte[BlockSize];
        EncryptBlock(zero, l);
        Double(l, _k1);
        Double(_k1, _k2);
    }

    /// <summary>Liczy CMAC z <paramref name="message"/> i zapisuje 16 bajtów do <paramref name="tag"/>.</summary>
    public void ComputeTag(ReadOnlySpan<byte> message, Span<byte> tag)
    {
        if (tag.Length != BlockSize)
        {
            throw new ArgumentException($"Znacznik CMAC ma {BlockSize} bajtów.", nameof(tag));
        }

        int completeBlocks = message.Length / BlockSize;
        bool lastBlockIsWhole = message.Length > 0 && message.Length % BlockSize == 0;
        int blocksBeforeLast = lastBlockIsWhole ? completeBlocks - 1 : completeBlocks;

        Span<byte> x = stackalloc byte[BlockSize];
        Span<byte> block = stackalloc byte[BlockSize];
        x.Clear();

        for (int i = 0; i < blocksBeforeLast; i++)
        {
            Xor(x, message.Slice(i * BlockSize, BlockSize), block);
            EncryptBlock(block, x);
        }

        // Ostatni blok: pełny → XOR z K1; niepełny (także pusta wiadomość) → dopełnienie 0x80 00... i XOR z K2.
        Span<byte> last = stackalloc byte[BlockSize];
        if (lastBlockIsWhole)
        {
            Xor(message.Slice(blocksBeforeLast * BlockSize, BlockSize), _k1, last);
        }
        else
        {
            int rest = message.Length - (blocksBeforeLast * BlockSize);
            Span<byte> padded = stackalloc byte[BlockSize];
            padded.Clear();
            message.Slice(blocksBeforeLast * BlockSize, rest).CopyTo(padded);
            padded[rest] = 0x80;
            Xor(padded, _k2, last);
        }

        Xor(x, last, block);
        EncryptBlock(block, tag);
    }

    /// <summary>Jednorazowe AES-ECB na jednym bloku — „funkcja blokowa” CMAC-a, nie szyfrowanie danych.</summary>
    private void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output) =>
        _aes.EncryptEcb(input, output, PaddingMode.None);

    /// <summary>Mnożenie przez x w GF(2^128): przesunięcie w lewo o bit, a przy przeniesieniu XOR z <see cref="Rb"/>.</summary>
    private static void Double(ReadOnlySpan<byte> input, Span<byte> output)
    {
        byte carry = 0;
        for (int i = BlockSize - 1; i >= 0; i--)
        {
            byte value = input[i];
            output[i] = (byte)((value << 1) | carry);
            carry = (byte)((value >> 7) & 1);
        }

        if (carry != 0)
        {
            output[BlockSize - 1] ^= Rb;
        }
    }

    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> result)
    {
        for (int i = 0; i < BlockSize; i++)
        {
            result[i] = (byte)(a[i] ^ b[i]);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_k1);
        CryptographicOperations.ZeroMemory(_k2);
        _aes.Dispose();
    }
}
