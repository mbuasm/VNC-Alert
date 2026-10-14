using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Dekodowanie ramek: Raw (32 bpp i RGB565), Hextile (wszystkie sub-kodowania) i CopyRect.</summary>
public sealed class DecodingTests
{
    [Fact]
    public async Task FullFrame_Raw32bpp_DecodesExactly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            Encoding = FakeEncodingMode.Raw,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 64, 48), false, cts.Token);

        ImageAssert.Equal(screen, frame);
        Assert.All(frame.Pixels.Where((_, i) => i % 4 == 3), a => Assert.Equal((byte)255, a));
    }

    [Fact]
    public async Task FullFrame_Raw16bppRgb565_DecodesWithTolerance()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Rgb565Friendly(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            Encoding = FakeEncodingMode.Raw,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);
        Assert.Equal(16, info.PixelFormat.BitsPerPixel);
        Assert.Equal("16bpp depth 16 RGB565 LE", info.PixelFormat.Describe());

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 64, 48), false, cts.Token);

        // Kolory dobrane tak, by round-trip 565 był bezstratny; tolerancja to zapas bezpieczeństwa.
        ImageAssert.Close(screen, frame, 8);

        // Kolory czyste muszą być odwzorowane dokładnie.
        Assert.Equal(new Rgba(0, 0, 0), frame.GetPixel(0, 0));
        Assert.Equal(new Rgba(255, 255, 255), frame.GetPixel(1, 0));
        Assert.Equal(new Rgba(255, 0, 0), frame.GetPixel(2, 0));
        Assert.Equal(new Rgba(0, 255, 0), frame.GetPixel(3, 0));
        Assert.Equal(new Rgba(0, 0, 255), frame.GetPixel(4, 0));
        Assert.Equal(new Rgba(255, 255, 0), frame.GetPixel(5, 0));
    }

    [Fact]
    public async Task AiditeWinCe_Raw16bppDepth15Rgb555_DecodesAndRemainsReadOnly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        var rgb555 = new RfbPixelFormat(
            BitsPerPixel: 16,
            Depth: 15,
            BigEndian: false,
            TrueColour: true,
            RedMax: 31,
            GreenMax: 31,
            BlueMax: 31,
            RedShift: 10,
            GreenShift: 5,
            BlueShift: 0);
        RgbaImage screen = TestScreens.Rgb555Friendly(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            PixelFormat = rgb555,
            DesktopName = "WinCE",
            Encoding = FakeEncodingMode.Raw,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);
        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 64, 48), false, cts.Token);

        Assert.Equal(rgb555, info.PixelFormat);
        Assert.Equal("16bpp depth 15 RGB max(31,31,31) shift(10,5,0) LE", info.PixelFormat.Describe());
        Assert.Equal("WinCE", info.DesktopName);
        ImageAssert.Close(screen, frame, 8);
        Assert.Equal(new Rgba(255, 0, 0), frame.GetPixel(2, 0));

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.NotEmpty(session.ReceivedMessageTypes);
        Assert.All(session.ReceivedMessageTypes, type => Assert.True(type is 2 or 3));
        Assert.DoesNotContain((byte)0, session.ReceivedMessageTypes);
        Assert.DoesNotContain((byte)4, session.ReceivedMessageTypes);
        Assert.DoesNotContain((byte)5, session.ReceivedMessageTypes);
        Assert.DoesNotContain((byte)6, session.ReceivedMessageTypes);
        Assert.All(server.AllReceivedMessageTypes, type => Assert.True(type is 2 or 3));
    }

    [Fact]
    public async Task Hextile_AllSubencodings_DecodesExactly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.HextileMixed();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            Encoding = FakeEncodingMode.Hextile,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 40, 40), false, cts.Token);
        ImageAssert.Equal(screen, frame);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        IReadOnlyList<byte> subencodings = session.HextileSubencodings;

        Assert.Contains(subencodings, s => (s & 1) != 0);                    // Raw
        Assert.Contains(subencodings, s => s == 2);                          // samo tło
        Assert.Contains(subencodings, s => s == (2 | 4 | 8));                // tło + pierwszy plan + podprostokąty
    }

    [Fact]
    public async Task Hextile_StickyBackground_DecodesExactly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.HextileMixed();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            Encoding = FakeEncodingMode.Hextile,
            HextileUseStickyBackground = true,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 40, 40), false, cts.Token);
        ImageAssert.Equal(screen, frame);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);

        // Musiał wystąpić przynajmniej jeden kafel BEZ bitu BackgroundSpecified — inaczej test nic nie sprawdza.
        Assert.Contains(session.HextileSubencodings, s => (s & 1) == 0 && (s & 2) == 0);
    }

    [Fact]
    public async Task Hextile_ColouredSubrects_DecodesExactly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        // Jeden kafel 16x16 z czterema kolorami → koder użyje AnySubrects + SubrectsColoured.
        var screen = new RgbaImage(32, 32);
        screen.Fill(new Rgba(10, 10, 10));
        Fill(screen, 2, 2, 4, 4, new Rgba(200, 0, 0));
        Fill(screen, 9, 2, 4, 4, new Rgba(0, 200, 0));
        Fill(screen, 2, 9, 4, 4, new Rgba(0, 0, 200));
        Fill(screen, 18, 18, 6, 6, new Rgba(240, 240, 0));

        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            Encoding = FakeEncodingMode.Hextile,
            HextileUseColouredSubrects = true,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 32, 32), false, cts.Token);
        ImageAssert.Equal(screen, frame);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Contains(session.HextileSubencodings, s => (s & 16) != 0);
    }

    private static void Fill(RgbaImage image, int x0, int y0, int width, int height, Rgba color)
    {
        for (int y = y0; y < y0 + height; y++)
        {
            for (int x = x0; x < x0 + width; x++)
            {
                image.SetPixel(x, y, color);
            }
        }
    }

    [Fact]
    public async Task Hextile_Rgb565_DecodesWithTolerance()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.HextileMixed();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            Encoding = FakeEncodingMode.Hextile,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 40, 40), false, cts.Token);

        // 16 bpp gubi młodsze bity kanałów — sprawdzamy zgodność w granicach kwantyzacji RGB565.
        ImageAssert.Close(screen, frame, 8);
    }

    [Fact]
    public async Task Hextile_RegionOfInterestNotAtOrigin_DecodesExactly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.HextileMixed();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            Encoding = FakeEncodingMode.Hextile,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        // Kafle Hextile są liczone od początku PROSTOKĄTA, nie od (0,0) ekranu (RFC 6143 §7.7.4).
        var roi = new RectInt(6, 10, 27, 23);
        RgbaImage part = await client.CaptureAsync(roi, false, cts.Token);

        ImageAssert.EqualToRegion(screen, roi, part);
    }

    [Fact]
    public async Task FullFrame_Raw32bppBigEndian_DecodesExactly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(32, 24);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            // 32 bpp, kolejność bajtów big-endian, układ kanałów RGBA (RedShift = 0).
            PixelFormat = new RfbPixelFormat(32, 24, BigEndian: true, TrueColour: true, 255, 255, 255, 0, 8, 16),
            Encoding = FakeEncodingMode.Raw,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);
        Assert.True(info.PixelFormat.BigEndian);

        ImageAssert.Equal(screen, await client.CaptureAsync(new RectInt(0, 0, 32, 24), false, cts.Token));
    }

    [Fact]
    public async Task CopyRect_Decodes()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.MirroredHalves(64, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            Encoding = FakeEncodingMode.CopyRectHalves,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        FramebufferUpdate update = await client.UpdateAsync(new RectInt(0, 0, 64, 32), false, cts.Token);

        Assert.Equal(2, update.Rectangles.Count);
        Assert.Equal(new[] { RfbEncodings.Raw, RfbEncodings.CopyRect }, update.EncodingsUsed);
        ImageAssert.Equal(screen, client.Framebuffer.Snapshot());
    }

    [Fact]
    public async Task Palette8bpp_UsesColourMap()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        Rgba[] palette =
        [
            new Rgba(0, 0, 0), new Rgba(255, 255, 255), new Rgba(255, 0, 0),
            new Rgba(0, 255, 0), new Rgba(0, 0, 255), new Rgba(128, 128, 0),
        ];
        RgbaImage screen = TestScreens.FromPalette(32, 24, palette);

        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            PixelFormat = new RfbPixelFormat(8, 8, BigEndian: false, TrueColour: false, 0, 0, 0, 0, 0, 0),
            Palette = palette,
            SendColourMap = true,
            Encoding = FakeEncodingMode.Raw,
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);
        Assert.False(info.PixelFormat.TrueColour);

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 32, 24), false, cts.Token);
        ImageAssert.Equal(screen, frame);
    }
}
