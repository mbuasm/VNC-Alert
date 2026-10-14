using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MaszynyAlert.Core.Imaging;

/// <summary>
/// Minimalny, samodzielny dekoder/koder PNG (bez zewnętrznych bibliotek obrazów) — tyle, ile potrzebuje
/// VNC Alert do wczytywania zrzutów ekranu maszyn i zapisywania przechwyceń diagnostycznych.
/// Dekodowanie: typy koloru 0 (skala szarości), 2 (RGB), 3 (paleta + tRNS), 4 (szarość+alfa), 6 (RGBA);
/// głębia 8 bitów (16 bitów → brany jest górny bajt każdej próbki); filtry 0–4; bez przeplotu (interlace).
/// Kodowanie: RGBA8 (typ koloru 6) albo skala szarości 8 bitów (typ koloru 0), filtr None.
/// </summary>
public static class PngCodec
{
    public const int MaxDimension = 8192;
    public const int MaxPixels = 16 * 1024 * 1024;
    private const int MaxEncodedBytes = 128 * 1024 * 1024;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    // ---------------------------------------------------------------------
    // Dekodowanie
    // ---------------------------------------------------------------------

    /// <summary>Dekoduje obraz PNG z bufora w pamięci.</summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> png) => DecodeCore(png);

    /// <summary>Dekoduje obraz PNG ze strumienia (czyta go do końca).</summary>
    public static RgbaImage Decode(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = s.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (ms.Length + read > MaxEncodedBytes)
                throw new PngFormatException("Plik PNG przekracza limit 128 MiB.");
            ms.Write(buffer, 0, read);
        }
        return DecodeCore(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>Dekoduje obraz PNG z pliku.</summary>
    public static RgbaImage DecodeFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Decode(fs);
    }

    private static RgbaImage DecodeCore(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxEncodedBytes)
            throw new PngFormatException("Plik PNG przekracza limit 128 MiB.");
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature))
        {
            throw new PngFormatException("Plik nie jest poprawnym obrazem PNG (nieprawidłowa sygnatura na początku pliku).");
        }

        int pos = 8;
        bool haveIhdr = false;
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();
        bool ended = false;

        while (pos < data.Length && !ended)
        {
            if (pos + 8 > data.Length)
            {
                throw new PngFormatException("Uszkodzony plik PNG: dane urywają się w środku nagłówka fragmentu.");
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            pos += 4;
            int typePos = pos;
            string type = Encoding.ASCII.GetString(data.Slice(pos, 4));
            pos += 4;

            if (length > int.MaxValue - 8 || pos + (long)length + 4 > data.Length)
            {
                throw new PngFormatException($"Uszkodzony plik PNG: nieprawidłowa długość fragmentu '{type}'.");
            }

            ReadOnlySpan<byte> chunkData = data.Slice(pos, (int)length);
            pos += (int)length;
            uint crcStored = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            pos += 4;

            uint crcComputed = Crc32.Compute(data.Slice(typePos, 4 + (int)length));
            if (crcComputed != crcStored)
            {
                throw new PngFormatException($"Uszkodzony plik PNG: błędna suma kontrolna (CRC) fragmentu '{type}' — plik jest uszkodzony lub niekompletny.");
            }

            switch (type)
            {
                case "IHDR":
                    if (chunkData.Length != 13)
                    {
                        throw new PngFormatException("Uszkodzony plik PNG: nieprawidłowy rozmiar nagłówka IHDR.");
                    }

                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                    bitDepth = chunkData[8];
                    colorType = chunkData[9];
                    byte compressionMethod = chunkData[10];
                    byte filterMethod = chunkData[11];
                    byte interlaceMethod = chunkData[12];

                    if (width <= 0 || height <= 0)
                    {
                        throw new PngFormatException("Nieprawidłowy plik PNG: zerowy lub ujemny rozmiar obrazu.");
                    }
                    if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
                        throw new PngFormatException("Obraz PNG przekracza limit wymiaru 8192 px lub 16 milionów pikseli.");

                    if (compressionMethod != 0 || filterMethod != 0)
                    {
                        throw new PngFormatException("Nieobsługiwana metoda kompresji lub filtrowania w pliku PNG.");
                    }

                    if (colorType is not (0 or 2 or 3 or 4 or 6))
                    {
                        throw new PngFormatException($"Nieobsługiwany typ koloru PNG: {colorType}. Obsługiwane są: skala szarości, RGB, paleta, szarość z alfą, RGBA.");
                    }

                    if (bitDepth != 8 && !(bitDepth == 16 && colorType != 3))
                    {
                        throw new PngFormatException($"Nieobsługiwana głębia bitowa PNG: {bitDepth} bitów dla tego typu koloru. Obsługiwane jest 8 bitów (oraz 16 bitów dla obrazów bez palety).");
                    }

                    if (interlaceMethod != 0)
                    {
                        throw new PngFormatException("Ten plik PNG używa przeplotu (interlace), a taki wariant nie jest obsługiwany przez ten program. Zapisz obraz bez przeplotu (np. w GIMP/Paint.NET) i spróbuj ponownie.");
                    }

                    haveIhdr = true;
                    break;

                case "PLTE":
                    palette = chunkData.ToArray();
                    break;

                case "tRNS":
                    trns = chunkData.ToArray();
                    break;

                case "IDAT":
                    idat.Write(chunkData);
                    break;

                case "IEND":
                    ended = true;
                    break;
            }
        }

        if (!haveIhdr)
        {
            throw new PngFormatException("Uszkodzony plik PNG: brak nagłówka IHDR.");
        }

        if (colorType == 3 && palette is null)
        {
            throw new PngFormatException("Uszkodzony plik PNG: obraz z paletą (PLTE) nie zawiera samej palety.");
        }

        if (idat.Length == 0)
        {
            throw new PngFormatException("Uszkodzony plik PNG: brak danych obrazu (IDAT).");
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new PngFormatException($"Nieobsługiwany typ koloru PNG: {colorType}."),
        };

        int bytesPerSample = bitDepth == 16 ? 2 : 1;
        int bytesPerPixel = channels * bytesPerSample;
        int bytesPerScanline = width * bytesPerPixel;
        long expectedTotal = (long)height * (1 + bytesPerScanline);
        byte[] rawBytes = Inflate(idat.ToArray(), checked((int)expectedTotal));

        byte[] unfiltered = Unfilter(rawBytes, height, bytesPerPixel, bytesPerScanline);
        return BuildImage(unfiltered, width, height, colorType, bytesPerSample, bytesPerPixel, bytesPerScanline, palette, trns);
    }

    private static byte[] Inflate(byte[] compressed, int expectedLength)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            // Nigdy nie rozpakowujemy więcej niż zapowiada zweryfikowany IHDR.
            var output = new byte[expectedLength];
            zlib.ReadExactly(output);
            if (zlib.ReadByte() != -1)
                throw new PngFormatException("Uszkodzony plik PNG: rozpakowane dane przekraczają rozmiar z IHDR.");
            return output;
        }
        catch (InvalidDataException ex)
        {
            throw new PngFormatException("Uszkodzony plik PNG: nie udało się rozpakować danych obrazu (zły strumień zlib/DEFLATE).", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new PngFormatException("Uszkodzony plik PNG: rozpakowane dane są krótsze niż rozmiar z IHDR.", ex);
        }
    }

    private static byte[] Unfilter(byte[] raw, int height, int bpp, int bytesPerScanline)
    {
        var output = new byte[(long)height * bytesPerScanline];
        var prevRow = new byte[bytesPerScanline];
        int rawPos = 0;

        for (int y = 0; y < height; y++)
        {
            byte filterType = raw[rawPos++];
            int outRowStart = y * bytesPerScanline;

            for (int x = 0; x < bytesPerScanline; x++)
            {
                byte rawValue = raw[rawPos + x];
                byte a = x >= bpp ? output[outRowStart + x - bpp] : (byte)0;
                byte b = prevRow[x];
                byte c = x >= bpp ? prevRow[x - bpp] : (byte)0;

                byte reconstructed = filterType switch
                {
                    0 => rawValue,
                    1 => unchecked((byte)(rawValue + a)),
                    2 => unchecked((byte)(rawValue + b)),
                    3 => unchecked((byte)(rawValue + (byte)((a + b) / 2))),
                    4 => unchecked((byte)(rawValue + PaethPredictor(a, b, c))),
                    _ => throw new PngFormatException($"Uszkodzony plik PNG: nieznany typ filtra wiersza ({filterType})."),
                };

                output[outRowStart + x] = reconstructed;
            }

            rawPos += bytesPerScanline;
            Buffer.BlockCopy(output, outRowStart, prevRow, 0, bytesPerScanline);
        }

        return output;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static RgbaImage BuildImage(byte[] unfiltered, int width, int height, byte colorType, int bytesPerSample, int bytesPerPixel, int bytesPerScanline, byte[]? palette, byte[]? trns)
    {
        var img = new RgbaImage(width, height);
        byte[] dst = img.Pixels;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * bytesPerScanline;
            int dstRowStart = y * img.Stride;

            for (int x = 0; x < width; x++)
            {
                int p = rowStart + x * bytesPerPixel;
                int d = dstRowStart + x * 4;
                byte r, g, b, a;

                switch (colorType)
                {
                    case 0:
                        r = g = b = unfiltered[p];
                        a = 255;
                        break;

                    case 2:
                        r = unfiltered[p];
                        g = unfiltered[p + bytesPerSample];
                        b = unfiltered[p + (2 * bytesPerSample)];
                        a = 255;
                        break;

                    case 3:
                        {
                            byte index = unfiltered[p];
                            int po = index * 3;
                            if (palette is null || po + 2 >= palette.Length)
                            {
                                throw new PngFormatException($"Uszkodzony plik PNG: indeks palety ({index}) wykracza poza paletę (PLTE).");
                            }

                            r = palette[po];
                            g = palette[po + 1];
                            b = palette[po + 2];
                            a = trns is not null && index < trns.Length ? trns[index] : (byte)255;
                        }

                        break;

                    case 4:
                        r = g = b = unfiltered[p];
                        a = unfiltered[p + bytesPerSample];
                        break;

                    case 6:
                        r = unfiltered[p];
                        g = unfiltered[p + bytesPerSample];
                        b = unfiltered[p + (2 * bytesPerSample)];
                        a = unfiltered[p + (3 * bytesPerSample)];
                        break;

                    default:
                        throw new PngFormatException($"Nieobsługiwany typ koloru PNG: {colorType}.");
                }

                dst[d] = r;
                dst[d + 1] = g;
                dst[d + 2] = b;
                dst[d + 3] = a;
            }
        }

        return img;
    }

    // ---------------------------------------------------------------------
    // Kodowanie
    // ---------------------------------------------------------------------

    /// <summary>Koduje obraz RGBA do PNG (typ koloru 6, 8 bitów/kanał).</summary>
    public static byte[] Encode(RgbaImage img)
    {
        ArgumentNullException.ThrowIfNull(img);
        return EncodeCore(img.Width, img.Height, colorType: 6, bytesPerPixel: 4, img.Pixels);
    }

    /// <summary>Koduje obraz w skali szarości do PNG (typ koloru 0, 8 bitów/kanał).</summary>
    public static byte[] Encode(Gray8Image img)
    {
        ArgumentNullException.ThrowIfNull(img);
        return EncodeCore(img.Width, img.Height, colorType: 0, bytesPerPixel: 1, img.Pixels);
    }

    /// <summary>Koduje obraz RGBA do pliku PNG.</summary>
    public static void EncodeToFile(RgbaImage img, string path) => File.WriteAllBytes(path, Encode(img));

    /// <summary>Koduje obraz w skali szarości do pliku PNG.</summary>
    public static void EncodeToFile(Gray8Image img, string path) => File.WriteAllBytes(path, Encode(img));

    private static byte[] EncodeCore(int width, int height, byte colorType, int bytesPerPixel, byte[] pixels)
    {
        int bytesPerScanline = width * bytesPerPixel;

        // Surowe dane wierszy z bajtem filtra (0 = None) na początku każdego wiersza.
        var raw = new byte[(long)height * (1 + bytesPerScanline)];
        int rawPos = 0;
        for (int y = 0; y < height; y++)
        {
            raw[rawPos++] = 0; // filtr None
            Buffer.BlockCopy(pixels, y * bytesPerScanline, raw, rawPos, bytesPerScanline);
            rawPos += bytesPerScanline;
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = output.ToArray();
        }

        using var result = new MemoryStream();
        result.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8; // głębia bitowa
        ihdr[9] = colorType;
        ihdr[10] = 0; // metoda kompresji
        ihdr[11] = 0; // metoda filtrowania
        ihdr[12] = 0; // bez przeplotu
        WriteChunk(result, "IHDR", ihdr);

        WriteChunk(result, "IDAT", compressed);
        WriteChunk(result, "IEND", ReadOnlySpan<byte>.Empty);

        return result.ToArray();
    }

    private static void WriteChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBuf, (uint)data.Length);
        target.Write(lengthBuf);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        target.Write(typeBytes);
        target.Write(data);

        var forCrc = new byte[4 + data.Length];
        typeBytes.CopyTo(forCrc);
        data.CopyTo(forCrc.AsSpan(4));
        uint crc = Crc32.Compute(forCrc);

        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        target.Write(crcBuf);
    }

    /// <summary>Własna implementacja CRC-32 (wielomian PNG/zlib 0xEDB88320) — bez zależności zewnętrznych.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFF;
        }
    }
}
