using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Parsowanie/serializacja 16-bajtowej struktury PIXEL_FORMAT i zamiana pikseli na RGBA.</summary>
public sealed class RfbPixelFormatTests
{
    private static readonly RfbPixelFormat AiditeRgb555 =
        new(16, 15, BigEndian: false, TrueColour: true, 31, 31, 31, 10, 5, 0);

    private static readonly RfbPixelFormat[] Formats =
    [
        RfbPixelFormat.Bgra32LittleEndian,
        RfbPixelFormat.Rgb565LittleEndian,
        AiditeRgb555,
        new RfbPixelFormat(32, 24, BigEndian: true, TrueColour: true, 255, 255, 255, 0, 8, 16),
        new RfbPixelFormat(8, 8, BigEndian: false, TrueColour: false, 0, 0, 0, 0, 0, 0),
    ];

    [Fact]
    public void ParseWrite_RoundTrip()
    {
        foreach (RfbPixelFormat format in Formats)
        {
            var buffer = new byte[16];
            format.Write(buffer);
            RfbPixelFormat parsed = RfbPixelFormat.Parse(buffer);

            Assert.Equal(format, parsed);
            Assert.Equal(0, buffer[13] | buffer[14] | buffer[15]);
        }
    }

    [Fact]
    public void Parse_TooShortBuffer_Throws()
    {
        Assert.Throws<ArgumentException>(() => RfbPixelFormat.Parse(new byte[15]));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(31, 63, 31)]
    [InlineData(31, 0, 0)]
    [InlineData(0, 63, 0)]
    [InlineData(0, 0, 31)]
    public void ToRgba_Rgb565(int r5, int g6, int b5)
    {
        RfbPixelFormat format = RfbPixelFormat.Rgb565LittleEndian;
        uint raw = (uint)((r5 << 11) | (g6 << 5) | b5);

        Rgba color = format.ToRgba(raw);

        Assert.Equal((byte)(r5 * 255 / 31), color.R);
        Assert.Equal((byte)(g6 * 255 / 63), color.G);
        Assert.Equal((byte)(b5 * 255 / 31), color.B);
        Assert.Equal(255, color.A);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(31, 31, 31)]
    [InlineData(31, 0, 0)]
    [InlineData(0, 31, 0)]
    [InlineData(0, 0, 31)]
    public void ToRgba_AiditeRgb555(int r5, int g5, int b5)
    {
        uint raw = (uint)((r5 << 10) | (g5 << 5) | b5);

        Rgba color = AiditeRgb555.ToRgba(raw);

        Assert.Equal((byte)(r5 * 255 / 31), color.R);
        Assert.Equal((byte)(g5 * 255 / 31), color.G);
        Assert.Equal((byte)(b5 * 255 / 31), color.B);
        Assert.Equal(255, color.A);
    }

    [Fact]
    public void ToRgba_Bgra32()
    {
        RfbPixelFormat format = RfbPixelFormat.Bgra32LittleEndian;
        uint raw = (12u << 16) | (34u << 8) | 56u;

        Rgba color = format.ToRgba(raw);

        Assert.Equal(new Rgba(12, 34, 56), color);
        Assert.Equal(255, color.A);
    }

    [Fact]
    public void ReadPixel_RespectsEndianness()
    {
        RfbPixelFormat little = RfbPixelFormat.Rgb565LittleEndian;
        RfbPixelFormat big = little with { BigEndian = true };
        byte[] bytes = [0x12, 0x34];

        Assert.Equal(0x3412u, little.ReadPixel(bytes));
        Assert.Equal(0x1234u, big.ReadPixel(bytes));
    }

    [Fact]
    public void Describe_NamesCommonLayouts()
    {
        Assert.Equal("16bpp depth 16 RGB565 LE", RfbPixelFormat.Rgb565LittleEndian.Describe());
        Assert.Equal("16bpp depth 15 RGB max(31,31,31) shift(10,5,0) LE", AiditeRgb555.Describe());
        Assert.Equal("32bpp depth 24 BGRA32 LE", RfbPixelFormat.Bgra32LittleEndian.Describe());
        Assert.Contains("paleta", new RfbPixelFormat(8, 8, false, false, 0, 0, 0, 0, 0, 0).Describe());
    }

    [Fact]
    public void BytesPerPixel_MatchesBitsPerPixel()
    {
        Assert.Equal(4, RfbPixelFormat.Bgra32LittleEndian.BytesPerPixel);
        Assert.Equal(2, RfbPixelFormat.Rgb565LittleEndian.BytesPerPixel);
    }
}
