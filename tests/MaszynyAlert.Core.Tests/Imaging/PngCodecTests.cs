using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Imaging;

public sealed class PngCodecTests
{
    [Theory]
    [InlineData(20000, 20000)]
    [InlineData(8193, 1)]
    [InlineData(8192, 8192)]
    public void Decode_ExcessiveDimensions_RejectsBeforeInflating(int width, int height)
    {
        byte[] png = PngBuilder.Build(("IHDR", PngBuilder.Ihdr(width, height, 8, 6)),
            ("IDAT", [0]), ("IEND", []));
        var error = Assert.Throws<PngFormatException>(() => PngCodec.Decode(png));
        Assert.Contains("limit", error.Message);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1000000)]
    public void Decode_InflatedLengthMustMatchHeader(int length)
    {
        byte[] png = PngBuilder.Build(("IHDR", PngBuilder.Ihdr(1, 1, 8, 6)),
            ("IDAT", PngBuilder.Deflate(new byte[length])), ("IEND", []));
        Assert.Throws<PngFormatException>(() => PngCodec.Decode(png));
    }

    private static RgbaImage Pattern(int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                img.SetPixel(x, y, new Rgba(
                    (byte)((x * 13 + y * 7) % 256),
                    (byte)((x * 3 + y * 29) % 256),
                    (byte)((x + y * 5) % 256),
                    (byte)(200 + ((x + y) % 56))));
            }
        }

        return img;
    }

    private static Gray8Image GrayPattern(int w, int h)
    {
        var img = new Gray8Image(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                img.Set(x, y, (byte)((x * 17 + y * 31) % 256));
            }
        }

        return img;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 23)]
    [InlineData(300, 2)]
    public void EncodeDecode_ObrazRgba_ZachowujeWszystkiePiksele(int w, int h)
    {
        RgbaImage original = Pattern(w, h);
        RgbaImage decoded = PngCodec.Decode(PngCodec.Encode(original));

        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
        Assert.Equal(original.Pixels, decoded.Pixels);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 23)]
    [InlineData(300, 2)]
    public void EncodeDecode_ObrazSzary_ZachowujeJasnosciIPelnaNieprzezroczystosc(int w, int h)
    {
        Gray8Image original = GrayPattern(w, h);
        RgbaImage decoded = PngCodec.Decode(PngCodec.Encode(original));

        Assert.Equal(original.ToRgba().Pixels, decoded.Pixels);
    }

    [Fact]
    public void EncodeToFile_IDecodeFile_DzialajaNaDysku()
    {
        using var dir = new TempDir("png");
        string path = dir.File("obraz.png");
        RgbaImage original = Pattern(8, 5);

        PngCodec.EncodeToFile(original, path);
        RgbaImage decoded = PngCodec.DecodeFile(path);

        Assert.Equal(original.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Decode_ZeStrumienia_DajeTenSamWynikCoZBufora()
    {
        RgbaImage original = Pattern(6, 6);
        byte[] png = PngCodec.Encode(original);

        using var ms = new MemoryStream(png);
        RgbaImage decoded = PngCodec.Decode(ms);

        Assert.Equal(original.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Decode_PaletaZPrzezroczystoscia_OdczytujeKoloryIAlfe()
    {
        // 3 kolory w palecie, pierwszy w pełni przezroczysty (tRNS), obraz 3x2.
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        byte[] trns = [0, 128];
        byte[][] rows = [[0, 1, 2], [2, 1, 0]];

        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(3, 2, bitDepth: 8, colorType: 3)),
            ("PLTE", palette),
            ("tRNS", trns),
            ("IDAT", PngBuilder.Idat(rows)),
            ("IEND", []));

        RgbaImage img = PngCodec.Decode(png);

        Assert.Equal(new Rgba(255, 0, 0, 0), img.GetPixel(0, 0));
        Assert.Equal(new Rgba(0, 255, 0, 128), img.GetPixel(1, 0));
        Assert.Equal(new Rgba(0, 0, 255, 255), img.GetPixel(2, 0));
        Assert.Equal(new Rgba(0, 0, 255, 255), img.GetPixel(0, 1));
    }

    [Fact]
    public void Decode_ObrazRgb16Bitow_BierzeGornyBajtKazdejProbki()
    {
        // 2x1 piksele, RGB po 16 bitów: (0x1234, 0x5678, 0x9ABC) i (0xFFFF, 0x0000, 0x8080).
        byte[][] rows =
        [
            [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xFF, 0xFF, 0x00, 0x00, 0x80, 0x80],
        ];

        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(2, 1, bitDepth: 16, colorType: 2)),
            ("IDAT", PngBuilder.Idat(rows)),
            ("IEND", []));

        RgbaImage img = PngCodec.Decode(png);

        Assert.Equal(new Rgba(0x12, 0x56, 0x9A, 255), img.GetPixel(0, 0));
        Assert.Equal(new Rgba(0xFF, 0x00, 0x80, 255), img.GetPixel(1, 0));
    }

    [Fact]
    public void Decode_SzaroscZAlfa_OdczytujeJasnoscIPrzezroczystosc()
    {
        byte[][] rows = [[10, 255, 200, 0], [90, 128, 45, 64]];

        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(2, 2, bitDepth: 8, colorType: 4)),
            ("IDAT", PngBuilder.Idat(rows)),
            ("IEND", []));

        RgbaImage img = PngCodec.Decode(png);

        Assert.Equal(new Rgba(10, 10, 10, 255), img.GetPixel(0, 0));
        Assert.Equal(new Rgba(200, 200, 200, 0), img.GetPixel(1, 0));
        Assert.Equal(new Rgba(45, 45, 45, 64), img.GetPixel(1, 1));
    }

    [Fact]
    public void Decode_ObrazSzary8Bitow_RozjasniaDoTrzechKanalow()
    {
        byte[][] rows = [[0, 128, 255]];
        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(3, 1, bitDepth: 8, colorType: 0)),
            ("IDAT", PngBuilder.Idat(rows)),
            ("IEND", []));

        RgbaImage img = PngCodec.Decode(png);

        Assert.Equal(new Rgba(128, 128, 128, 255), img.GetPixel(1, 0));
    }

    [Fact]
    public void Decode_ZPrzeplotem_RzucaPngFormatExceptionZPolskimKomunikatem()
    {
        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(2, 2, bitDepth: 8, colorType: 6, interlace: 1)),
            ("IDAT", PngBuilder.Idat([new byte[8], new byte[8]])),
            ("IEND", []));

        PngFormatException ex = Assert.Throws<PngFormatException>(() => PngCodec.Decode(png));
        Assert.Contains("przeplot", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_ZeZlaSumaKontrolna_RzucaPngFormatException()
    {
        byte[] png = PngBuilder.BuildWithBadCrc(
            "IDAT",
            ("IHDR", PngBuilder.Ihdr(1, 1, bitDepth: 8, colorType: 6)),
            ("IDAT", PngBuilder.Idat([[1, 2, 3, 4]])),
            ("IEND", []));

        PngFormatException ex = Assert.Throws<PngFormatException>(() => PngCodec.Decode(png));
        Assert.Contains("CRC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_DanychNieBedacychPng_RzucaPngFormatException()
    {
        byte[] notPng = "To nie jest obraz PNG, tylko zwykły tekst."u8.ToArray();
        PngFormatException ex = Assert.Throws<PngFormatException>(() => PngCodec.Decode(notPng));
        Assert.Contains("PNG", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_PustegoBufora_RzucaPngFormatException()
    {
        Assert.Throws<PngFormatException>(() => PngCodec.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Decode_BezFragmentuIDAT_RzucaPngFormatException()
    {
        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(1, 1, bitDepth: 8, colorType: 6)),
            ("IEND", []));

        Assert.Throws<PngFormatException>(() => PngCodec.Decode(png));
    }

    [Fact]
    public void Decode_NieobslugiwanegoTypuKoloru_RzucaPngFormatException()
    {
        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(1, 1, bitDepth: 8, colorType: 5)),
            ("IDAT", PngBuilder.Idat([[1, 2, 3, 4]])),
            ("IEND", []));

        Assert.Throws<PngFormatException>(() => PngCodec.Decode(png));
    }

    [Fact]
    public void Decode_UszkodzonegoStrumieniaZlib_RzucaPngFormatException()
    {
        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(1, 1, bitDepth: 8, colorType: 6)),
            ("IDAT", [0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF]),
            ("IEND", []));

        Assert.Throws<PngFormatException>(() => PngCodec.Decode(png));
    }

    [Fact]
    public void DecodeFile_NieistniejacegoPliku_RzucaFileNotFoundException()
    {
        using var dir = new TempDir("png-brak");
        string path = dir.File("nie-ma-takiego.png");

        // Zachowanie utrwalone: PngCodec nie opakowuje błędów wejścia/wyjścia — leci wyjątek z File.OpenRead.
        Assert.Throws<FileNotFoundException>(() => PngCodec.DecodeFile(path));
    }

    [Fact]
    public void Decode_ObrazuZFiltremPaeth_OdtwarzaOryginalnePiksele()
    {
        // Wiersz 0: filtr None, wiersz 1: filtr Paeth (4) — sprawdzamy rekonstrukcję filtrów.
        var raw = new List<byte>();
        raw.Add(0);
        raw.AddRange([10, 20, 30, 255, 40, 50, 60, 255]);
        raw.Add(4);
        raw.AddRange(new byte[8]); // Paeth z samymi zerami → wiersz identyczny jak predykcja

        byte[] png = PngBuilder.Build(
            ("IHDR", PngBuilder.Ihdr(2, 2, bitDepth: 8, colorType: 6)),
            ("IDAT", PngBuilder.Deflate(raw.ToArray())),
            ("IEND", []));

        RgbaImage img = PngCodec.Decode(png);

        Assert.Equal(new Rgba(10, 20, 30, 255), img.GetPixel(0, 0));
        Assert.Equal(new Rgba(40, 50, 60, 255), img.GetPixel(1, 0));
        // Paeth przy zerowej różnicy powiela wiersz powyżej.
        Assert.Equal(new Rgba(10, 20, 30, 255), img.GetPixel(0, 1));
        Assert.Equal(new Rgba(40, 50, 60, 255), img.GetPixel(1, 1));
    }
}
