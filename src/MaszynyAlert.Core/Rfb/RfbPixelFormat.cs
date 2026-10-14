using System.Buffers.Binary;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Rfb;

public sealed record RfbPixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColour,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift)
{
    /// <summary>Liczba bajtów zajmowanych przez jeden piksel (BitsPerPixel / 8).</summary>
    public int BytesPerPixel => BitsPerPixel / 8;

    /// <summary>Parsuje 16-bajtową strukturę PIXEL_FORMAT odebraną od serwera.</summary>
    public static RfbPixelFormat Parse(ReadOnlySpan<byte> b16)
    {
        if (b16.Length < 16)
        {
            throw new ArgumentException("Format piksela RFB musi mieć dokładnie 16 bajtów.", nameof(b16));
        }

        byte bitsPerPixel = b16[0];
        byte depth = b16[1];
        bool bigEndian = b16[2] != 0;
        bool trueColour = b16[3] != 0;
        ushort redMax = BinaryPrimitives.ReadUInt16BigEndian(b16.Slice(4, 2));
        ushort greenMax = BinaryPrimitives.ReadUInt16BigEndian(b16.Slice(6, 2));
        ushort blueMax = BinaryPrimitives.ReadUInt16BigEndian(b16.Slice(8, 2));
        byte redShift = b16[10];
        byte greenShift = b16[11];
        byte blueShift = b16[12];

        return new RfbPixelFormat(bitsPerPixel, depth, bigEndian, trueColour, redMax, greenMax, blueMax, redShift, greenShift, blueShift);
    }

    /// <summary>Zapisuje strukturę PIXEL_FORMAT do 16-bajtowego bufora (3 bajty wypełnienia na końcu, wyzerowane).</summary>
    public void Write(Span<byte> b16)
    {
        if (b16.Length < 16)
        {
            throw new ArgumentException("Bufor na format piksela RFB musi mieć co najmniej 16 bajtów.", nameof(b16));
        }

        b16[0] = BitsPerPixel;
        b16[1] = Depth;
        b16[2] = (byte)(BigEndian ? 1 : 0);
        b16[3] = (byte)(TrueColour ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(b16.Slice(4, 2), RedMax);
        BinaryPrimitives.WriteUInt16BigEndian(b16.Slice(6, 2), GreenMax);
        BinaryPrimitives.WriteUInt16BigEndian(b16.Slice(8, 2), BlueMax);
        b16[10] = RedShift;
        b16[11] = GreenShift;
        b16[12] = BlueShift;
        b16[13] = 0;
        b16[14] = 0;
        b16[15] = 0;
    }

    /// <summary>
    /// Odczytuje surową wartość jednego piksela z <paramref name="src"/> (dokładnie <see cref="BytesPerPixel"/>
    /// bajtów), respektując <see cref="BigEndian"/>. Dla 8 bpp zwraca po prostu ten bajt (indeks w palecie,
    /// gdy <see cref="TrueColour"/> = false).
    /// </summary>
    public uint ReadPixel(ReadOnlySpan<byte> src)
    {
        switch (BytesPerPixel)
        {
            case 1:
                return src[0];
            case 2:
                return BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(src) : BinaryPrimitives.ReadUInt16LittleEndian(src);
            case 4:
                return BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(src) : BinaryPrimitives.ReadUInt32LittleEndian(src);
            default:
                throw new RfbProtocolException($"Nieobsługiwana liczba bitów na piksel: {BitsPerPixel} (obsługiwane: 8, 16, 32).");
        }
    }

    /// <summary>
    /// Zamienia surową wartość piksela (true-colour) na RGBA, skalując kanały z zakresu
    /// 0..RedMax/GreenMax/BlueMax do 0..255 (np. dla RGB565: (v&gt;&gt;11 &amp; 31) * 255 / 31).
    /// Dla trybu paletowego (<see cref="TrueColour"/> = false) ta metoda NIE zna palety — rozwiązywaniem
    /// koloru z indeksu zajmują się dekodery przez wewnętrzną mapę kolorów (<c>RfbColourMap</c>),
    /// przekazywaną im przez <see cref="RfbClient"/> po odebraniu <c>SetColourMapEntries</c>.
    /// </summary>
    public Rgba ToRgba(uint raw)
    {
        byte r = ScaleChannel((raw >> RedShift) & RedMax, RedMax);
        byte g = ScaleChannel((raw >> GreenShift) & GreenMax, GreenMax);
        byte b = ScaleChannel((raw >> BlueShift) & BlueMax, BlueMax);
        return new Rgba(r, g, b);
    }

    private static byte ScaleChannel(uint value, ushort max) => max == 0 ? (byte)0 : (byte)(value * 255 / max);

    /// <summary>Typowy format 16 bpp RGB565 little-endian (częsty na starszych serwerach WinCE/OpenCnc).</summary>
    public static RfbPixelFormat Rgb565LittleEndian { get; } =
        new(BitsPerPixel: 16, Depth: 16, BigEndian: false, TrueColour: true,
            RedMax: 31, GreenMax: 63, BlueMax: 31, RedShift: 11, GreenShift: 5, BlueShift: 0);

    /// <summary>Typowy format 32 bpp (bajty w pamięci B,G,R,A — host little-endian), częsty u nowoczesnych serwerów.</summary>
    public static RfbPixelFormat Bgra32LittleEndian { get; } =
        new(BitsPerPixel: 32, Depth: 24, BigEndian: false, TrueColour: true,
            RedMax: 255, GreenMax: 255, BlueMax: 255, RedShift: 16, GreenShift: 8, BlueShift: 0);

    /// <summary>Krótki, czytelny opis formatu do logów/diagnostyki, np. "16bpp depth 16 RGB565 LE".</summary>
    public string Describe()
    {
        string endian = BigEndian ? "BE" : "LE";

        if (!TrueColour)
        {
            return $"{BitsPerPixel}bpp depth {Depth} paleta (indeksowany) {endian}";
        }

        string layout = (RedMax, GreenMax, BlueMax, RedShift, GreenShift, BlueShift) switch
        {
            (31, 63, 31, 11, 5, 0) => "RGB565",
            (255, 255, 255, 16, 8, 0) => "BGRA32",
            (255, 255, 255, 0, 8, 16) => "RGBA32",
            _ => $"RGB max({RedMax},{GreenMax},{BlueMax}) shift({RedShift},{GreenShift},{BlueShift})",
        };

        return $"{BitsPerPixel}bpp depth {Depth} {layout} {endian}";
    }
}
