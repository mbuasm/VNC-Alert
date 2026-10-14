using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>
/// Buduje pliki PNG „ręcznie”, chunk po chunku — potrzebne do testów dekodera PngCodec na wariantach,
/// których sam koder nie potrafi zapisać (paleta + tRNS, 16 bitów, szarość z alfą, przeplot, złe CRC).
/// Własna implementacja CRC-32 (PngCodec ma swoją, ale prywatną) i kompresja przez ZLibStream.
/// </summary>
public static class PngBuilder
{
    public static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Składa plik PNG z sygnatury i podanych fragmentów (chunków).</summary>
    public static byte[] Build(params (string Type, byte[] Data)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        foreach ((string type, byte[] data) in chunks)
        {
            WriteChunk(ms, type, data, corruptCrc: false);
        }

        return ms.ToArray();
    }

    /// <summary>Zawartość fragmentu IHDR.</summary>
    public static byte[] Ihdr(int width, int height, byte bitDepth, byte colorType, byte interlace = 0)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), (uint)height);
        data[8] = bitDepth;
        data[9] = colorType;
        data[10] = 0;
        data[11] = 0;
        data[12] = interlace;
        return data;
    }

    /// <summary>
    /// Buduje zawartość IDAT z surowych wierszy pikseli (bez bajtu filtra) — dokłada filtr 0 (None)
    /// na początku każdego wiersza i pakuje całość strumieniem zlib.
    /// </summary>
    public static byte[] Idat(IReadOnlyList<byte[]> rows)
    {
        using var raw = new MemoryStream();
        foreach (byte[] row in rows)
        {
            raw.WriteByte(0);
            raw.Write(row);
        }

        return Deflate(raw.ToArray());
    }

    /// <summary>Pakuje bajty strumieniem zlib (tak jak wymaga tego format PNG).</summary>
    public static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    /// <summary>Składa plik PNG, w którym fragment o nazwie <paramref name="corruptChunkType"/> ma zepsutą sumę CRC.</summary>
    public static byte[] BuildWithBadCrc(string corruptChunkType, params (string Type, byte[] Data)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        foreach ((string type, byte[] data) in chunks)
        {
            WriteChunk(ms, type, data, corruptCrc: type == corruptChunkType);
        }

        return ms.ToArray();
    }

    private static void WriteChunk(Stream target, string type, byte[] data, bool corruptCrc)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        target.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        var forCrc = new byte[4 + data.Length];
        typeBytes.CopyTo(forCrc, 0);
        data.CopyTo(forCrc, 4);
        uint crc = Crc32(forCrc);
        if (corruptCrc)
        {
            crc ^= 0xFFFF;
        }

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        target.Write(crcBytes);
    }

    /// <summary>CRC-32 wg wielomianu używanego przez PNG/zlib (0xEDB88320).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xFFFFFFFF;
    }
}
