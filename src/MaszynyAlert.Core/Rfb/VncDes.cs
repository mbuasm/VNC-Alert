using System.Text;

namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Własna, minimalna implementacja DES (ECB, bez paddingu) na potrzeby uwierzytelnienia VncAuth
/// (RFC 6143 §7.2.2). Nie używamy <c>System.Security.Cryptography.DES</c>, bo bywa niedostępny lub
/// oznaczony jako przestarzały na niektórych platformach/zasadach FIPS — a wyzwanie VNC to zawsze
/// dokładnie 16 bajtów (2 niezależne bloki po 8 B), więc własna implementacja jest mała i w pełni
/// wystarczająca.
///
/// VNC quirk: klucz DES to hasło ucięte/dopełnione zerami do 8 bajtów, ale KAŻDY bajt klucza ma
/// odwróconą kolejność bitów względem standardowego DES (tak historycznie zrobiono w RFB/VNC —
/// najstarszy bit staje się najmłodszym itd.).
/// </summary>
public static class VncDes
{
    /// <summary>
    /// Szyfruje 16-bajtowe wyzwanie serwera hasłem VNC i zwraca 16-bajtową odpowiedź
    /// (2 niezależne bloki DES-ECB po 8 bajtów, klucz pochodny z hasła — patrz dokumentacja klasy).
    /// </summary>
    public static byte[] EncryptChallenge(ReadOnlySpan<byte> challenge16, string password)
    {
        if (challenge16.Length != 16)
        {
            throw new ArgumentException("Wyzwanie VNC (VncAuth) musi mieć dokładnie 16 bajtów.", nameof(challenge16));
        }

        byte[] key = DeriveKey(password ?? string.Empty);
        ulong[] roundKeys = ComputeRoundKeys(key);

        byte[] result = new byte[16];
        EncryptBlock(challenge16[..8], roundKeys, result.AsSpan(0, 8));
        EncryptBlock(challenge16.Slice(8, 8), roundKeys, result.AsSpan(8, 8));
        return result;
    }

    /// <summary>
    /// Wyprowadza 8-bajtowy klucz DES z hasła VNC: bajty hasła (Latin1 — VNC/DES pracuje na 8-bitowych
    /// bajtach, a hasła VNC są w praktyce ASCII), ucięte/dopełnione zerami do 8 bajtów, każdy bajt
    /// z odwróconą kolejnością bitów.
    /// </summary>
    private static byte[] DeriveKey(string password)
    {
        byte[] raw = Encoding.Latin1.GetBytes(password);
        byte[] key = new byte[8];
        int n = Math.Min(8, raw.Length);
        Array.Copy(raw, key, n);
        for (int i = 0; i < 8; i++)
        {
            key[i] = ReverseBits(key[i]);
        }

        return key;
    }

    private static byte ReverseBits(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        b = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        return b;
    }

    // ---- Rdzeń DES (FIPS 46-3) --------------------------------------------------------------
    // Bity numerowane 1..N licząc od najstarszego (jak w specyfikacji DES); Permute() sama liczy
    // przesunięcia względem szerokości wejścia, więc tabele są przepisane wprost ze specyfikacji.

    private static readonly int[] Ip =
    [
        58, 50, 42, 34, 26, 18, 10, 2,
        60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6,
        64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1,
        59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5,
        63, 55, 47, 39, 31, 23, 15, 7,
    ];

    private static readonly int[] Fp =
    [
        40, 8, 48, 16, 56, 24, 64, 32,
        39, 7, 47, 15, 55, 23, 63, 31,
        38, 6, 46, 14, 54, 22, 62, 30,
        37, 5, 45, 13, 53, 21, 61, 29,
        36, 4, 44, 12, 52, 20, 60, 28,
        35, 3, 43, 11, 51, 19, 59, 27,
        34, 2, 42, 10, 50, 18, 58, 26,
        33, 1, 41, 9, 49, 17, 57, 25,
    ];

    private static readonly int[] E =
    [
        32, 1, 2, 3, 4, 5,
        4, 5, 6, 7, 8, 9,
        8, 9, 10, 11, 12, 13,
        12, 13, 14, 15, 16, 17,
        16, 17, 18, 19, 20, 21,
        20, 21, 22, 23, 24, 25,
        24, 25, 26, 27, 28, 29,
        28, 29, 30, 31, 32, 1,
    ];

    private static readonly int[] P =
    [
        16, 7, 20, 21,
        29, 12, 28, 17,
        1, 15, 23, 26,
        5, 18, 31, 10,
        2, 8, 24, 14,
        32, 27, 3, 9,
        19, 13, 30, 6,
        22, 11, 4, 25,
    ];

    private static readonly int[] Pc1 =
    [
        57, 49, 41, 33, 25, 17, 9,
        1, 58, 50, 42, 34, 26, 18,
        10, 2, 59, 51, 43, 35, 27,
        19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15,
        7, 62, 54, 46, 38, 30, 22,
        14, 6, 61, 53, 45, 37, 29,
        21, 13, 5, 28, 20, 12, 4,
    ];

    private static readonly int[] Pc2 =
    [
        14, 17, 11, 24, 1, 5,
        3, 28, 15, 6, 21, 10,
        23, 19, 12, 4, 26, 8,
        16, 7, 27, 20, 13, 2,
        41, 52, 31, 37, 47, 55,
        30, 40, 51, 45, 33, 48,
        44, 49, 39, 56, 34, 53,
        46, 42, 50, 36, 29, 32,
    ];

    private static readonly int[] Shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    private static readonly int[,] S1 =
    {
        { 14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7 },
        { 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8 },
        { 4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0 },
        { 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13 },
    };

    private static readonly int[,] S2 =
    {
        { 15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10 },
        { 3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5 },
        { 0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15 },
        { 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9 },
    };

    private static readonly int[,] S3 =
    {
        { 10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8 },
        { 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1 },
        { 13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7 },
        { 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12 },
    };

    private static readonly int[,] S4 =
    {
        { 7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15 },
        { 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9 },
        { 10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4 },
        { 3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14 },
    };

    private static readonly int[,] S5 =
    {
        { 2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9 },
        { 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6 },
        { 4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14 },
        { 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3 },
    };

    private static readonly int[,] S6 =
    {
        { 12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11 },
        { 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8 },
        { 9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6 },
        { 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13 },
    };

    private static readonly int[,] S7 =
    {
        { 4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1 },
        { 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6 },
        { 1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2 },
        { 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12 },
    };

    private static readonly int[,] S8 =
    {
        { 13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7 },
        { 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2 },
        { 7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8 },
        { 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11 },
    };

    private static ulong Permute(ulong input, int[] table, int inputBits)
    {
        ulong output = 0;
        for (int i = 0; i < table.Length; i++)
        {
            int bitPos = table[i]; // 1-based, licząc od najstarszego bitu wejścia
            int shift = inputBits - bitPos;
            ulong bit = (input >> shift) & 1UL;
            output = (output << 1) | bit;
        }

        return output;
    }

    private static uint LeftRotate28(uint value, int shifts)
    {
        value &= 0x0FFF_FFFF;
        return ((value << shifts) | (value >> (28 - shifts))) & 0x0FFF_FFFF;
    }

    private static ulong[] ComputeRoundKeys(byte[] key8)
    {
        ulong keyBits = BytesToBits64(key8);
        ulong pc1 = Permute(keyBits, Pc1, 64); // 56-bitowy wynik w dolnych 56 bitach ulong

        uint c = (uint)((pc1 >> 28) & 0x0FFF_FFFF);
        uint d = (uint)(pc1 & 0x0FFF_FFFF);

        var roundKeys = new ulong[16];
        for (int round = 0; round < 16; round++)
        {
            c = LeftRotate28(c, Shifts[round]);
            d = LeftRotate28(d, Shifts[round]);
            ulong cd = ((ulong)c << 28) | d; // 56 bitów
            roundKeys[round] = Permute(cd, Pc2, 56); // 48 bitów
        }

        return roundKeys;
    }

    private static ulong BytesToBits64(ReadOnlySpan<byte> b8)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            v = (v << 8) | b8[i];
        }

        return v;
    }

    private static void EncryptBlock(ReadOnlySpan<byte> block8, ulong[] roundKeys, Span<byte> out8)
    {
        ulong block = BytesToBits64(block8);
        ulong ip = Permute(block, Ip, 64);

        uint l = (uint)(ip >> 32);
        uint r = (uint)ip;

        for (int round = 0; round < 16; round++)
        {
            uint newL = r;
            uint f = Feistel(r, roundKeys[round]);
            uint newR = l ^ f;
            l = newL;
            r = newR;
        }

        ulong preOutput = ((ulong)r << 32) | l;
        ulong result = Permute(preOutput, Fp, 64);

        for (int i = 7; i >= 0; i--)
        {
            out8[i] = (byte)(result & 0xFF);
            result >>= 8;
        }
    }

    private static uint Feistel(uint r, ulong roundKey48)
    {
        ulong expanded = Permute(r, E, 32); // 48 bitów
        ulong x = expanded ^ roundKey48;

        uint sboxOutput = 0;
        for (int box = 0; box < 8; box++)
        {
            int shift = 42 - (box * 6);
            int sixBits = (int)((x >> shift) & 0x3F);
            int row = ((sixBits & 0x20) >> 4) | (sixBits & 0x01);
            int col = (sixBits >> 1) & 0x0F;
            int val = GetSBoxValue(box, row, col);
            sboxOutput = (sboxOutput << 4) | (uint)val;
        }

        ulong permuted = Permute(sboxOutput, P, 32);
        return (uint)permuted;
    }

    private static int GetSBoxValue(int box, int row, int col) => box switch
    {
        0 => S1[row, col],
        1 => S2[row, col],
        2 => S3[row, col],
        3 => S4[row, col],
        4 => S5[row, col],
        5 => S6[row, col],
        6 => S7[row, col],
        7 => S8[row, col],
        _ => throw new InvalidOperationException("Nieprawidłowy indeks S-boksu DES."),
    };
}
