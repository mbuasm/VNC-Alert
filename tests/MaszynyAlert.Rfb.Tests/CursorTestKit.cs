using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

internal static class CursorTestKit
{
    public const int W = 48;
    public const int H = 32;
    public static readonly RectInt Full = new(0, 0, W, H);

    /// <summary>Limit pojedynczej operacji klienta w testach — nic nie może wisieć.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static readonly RfbPixelFormat Bgr233 = new(8, 8, false, true, 7, 7, 3, 0, 3, 6);

    public static readonly RfbPixelFormat QtPalette8 = new(8, 8, false, false, 0, 0, 0, 0, 0, 0);

    /// <summary>Kolory dokładnie odwzorowywalne w RGB565, BGRA32 i bgr233; bez zieleni kursora (0,255,0).</summary>
    public static readonly Rgba[] Palette =
    [
        new(0, 0, 0), new(255, 255, 255), new(255, 0, 0), new(0, 0, 255),
        new(255, 255, 0), new(255, 0, 255), new(0, 255, 255),
    ];

    public static RfbPixelFormat FormatFor(int bitsPerPixel) => bitsPerPixel switch
    {
        8 => Bgr233,
        16 => RfbPixelFormat.Rgb565LittleEndian,
        32 => RfbPixelFormat.Bgra32LittleEndian,
        _ => throw new ArgumentOutOfRangeException(nameof(bitsPerPixel)),
    };

    public static RfbClientOptions Options(int port, TimeSpan? readTimeout = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(10),
    };

    public static (MachineConfig Machine, TimingConfig Timing) MonitorLike(int port) =>
    (
        new MachineConfig
        {
            Id = "520-sym", Name = "520 (symulator Qt)", Host = "127.0.0.1", Port = port,
            Profile = "aidite5x", Mode = ConnectionMode.Persistent,
        },
        new TimingConfig { ConnectTimeoutMs = 5000, ReadTimeoutMs = 3000, IdleUpdateTimeoutMs = 200 }
    );

    public static void AssertSame(RgbaImage expected, RgbaImage actual, string context)
    {
        Assert.True(expected.Width == actual.Width && expected.Height == actual.Height,
            $"{context}: rozmiar {actual.Width}x{actual.Height}, oczekiwano {expected.Width}x{expected.Height}");
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (expected.GetPixel(x, y) != actual.GetPixel(x, y))
                {
                    Assert.Fail($"{context}: piksel ({x},{y}) = {actual.GetPixel(x, y)}, oczekiwano {expected.GetPixel(x, y)}");
                }
            }
        }
    }

    /// <summary>Kursor nigdy na klatce: zieleń kursora atrapy nie może pojawić się w buforze klienta.</summary>
    public static void AssertNoCursor(RgbaImage image, string context)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y) == QtQwsCursorSimServer.CursorColour)
                {
                    Assert.Fail($"{context}: piksel kursora (zielony) na klatce w ({x},{y})");
                }
            }
        }
    }

    public static RectInt RandomRect(Random rng)
    {
        int x = rng.Next(W);
        int y = rng.Next(H);
        return new RectInt(x, y, 1 + rng.Next(W - x), 1 + rng.Next(H - y));
    }

    public static byte[] CursorRect(int w, int h, int hotX, int hotY, int bytesPerPixel, RfbPixelFormat format)
    {
        byte[] header = ScriptedRfbServer.RectangleHeader(hotX, hotY, w, h, -239);
        if (w == 0 || h == 0)
        {
            return header;
        }

        var encoder = new PixelEncoder(format, null);
        byte[] pixel = new byte[bytesPerPixel];
        encoder.Write(encoder.ToRaw(QtQwsCursorSimServer.CursorColour), pixel);
        byte[] result = new byte[12 + (w * h * bytesPerPixel) + (((w + 7) / 8) * h)];
        header.CopyTo(result, 0);
        for (int i = 0; i < w * h; i++)
        {
            pixel.CopyTo(result, 12 + (i * bytesPerPixel));
        }

        result.AsSpan(12 + (w * h * bytesPerPixel)).Fill(0xC3);
        return result;
    }

    public static byte[] RawRect(RectInt r, Rgba colour, RfbPixelFormat format)
    {
        var encoder = new PixelEncoder(format, null);
        using var ms = new MemoryStream();
        ms.Write(ScriptedRfbServer.RectangleHeader(r.X, r.Y, r.Width, r.Height, RfbEncodings.Raw));
        for (int i = 0; i < r.Width * r.Height; i++)
        {
            encoder.WritePixel(colour, ms);
        }

        return ms.ToArray();
    }
}
