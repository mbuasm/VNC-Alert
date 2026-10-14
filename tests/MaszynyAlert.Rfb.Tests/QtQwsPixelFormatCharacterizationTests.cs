using System.Buffers.Binary;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtQwsPixelFormatCharacterizationTests
{
    private const int Width = 32;
    private const int Height = 16;

    private const string NotQtName = "skrypt";

    /// <summary>
    /// Aktualizacja w stylu <c>QRfbHextileEncoder::write</c>: dwa prostokąty 16×16, każdy jeden kafel jednokolorowy
    /// (czerwony). Serwer koduje piksel w formacie ostatniego <c>SetPixelFormat</c> klienta, a bez niego — w formacie
    /// zastanym o <paramref name="leftoverBytesPerPixel"/> bajtach (format poprzedniego klienta albo niezainicjowany).
    /// </summary>
    private static ScriptedRfbServer QtStyleServer(int leftoverBytesPerPixel, string desktopName)
    {
        ScriptedRfbServer? server = null;
        server = new ScriptedRfbServer
        {
            Width = Width,
            Height = Height,
            Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = desktopName,
            OnRequest = async (s, number, ct) =>
            {
                int bpp = server!.ClientPixelFormat?.BytesPerPixel ?? leftoverBytesPerPixel;
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(2), ct);
                for (int i = 0; i < 2; i++)
                {
                    await s.WriteAsync(ScriptedRfbServer.RectangleHeader(i * 16, 0, 16, 16, RfbEncodings.Hextile), ct);
                    byte[] tile = new byte[1 + bpp];
                    tile[0] = 2;
                    if (bpp == 2) BinaryPrimitives.WriteUInt16LittleEndian(tile.AsSpan(1), 0xF800); // czerwień RGB565
                    await s.WriteAsync(tile, ct);
                }
            },
        };
        return server;
    }

    private static async Task<(Exception? Error, RgbaImage? Image)> CaptureFirstFrame(ScriptedRfbServer server)
    {
        server.Start();
        await using var client = new RfbClient(new RfbClientOptions
        {
            Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(3),
        });
        await client.ConnectAsync(CancellationToken.None);
        RgbaImage? image = null;
        Exception? ex = await Record.ExceptionAsync(async () =>
            image = await client.CaptureAsync(new RectInt(0, 0, Width, Height), incremental: false, CancellationToken.None));
        return (ex, image);
    }


    [Fact]
    public async Task BezSetPixelFormat_SerwerKodujacy1BajtNaPiksel_PrzyServerInitRgb565_DajeKodowanie1282()
    {
        await using var server = QtStyleServer(leftoverBytesPerPixel: 1, NotQtName);

        (Exception? ex, _) = await CaptureFirstFrame(server);

        RfbProtocolException protocol = Assert.IsType<RfbProtocolException>(ex);
        Assert.Contains("kodowanie prostokąta aktualizacji: 1282 ", protocol.Message);
        Assert.Null(server.ClientPixelFormat);
    }

    [Fact]
    public async Task BezSetPixelFormat_SerwerKodujacy4BajtyNaPiksel_DajeInnaWartoscNiz1282()
    {
        // Kontrola: 1282 nie jest „dowolnym rozjechaniem” — format 32 bpp poprzedniego klienta dałby 0x00100000.
        await using var server = QtStyleServer(leftoverBytesPerPixel: 4, NotQtName);

        (Exception? ex, _) = await CaptureFirstFrame(server);

        RfbProtocolException protocol = Assert.IsType<RfbProtocolException>(ex);
        Assert.Contains("kodowanie prostokąta aktualizacji: 1048576 ", protocol.Message);
    }

    [Fact]
    public async Task BezSetPixelFormat_SerwerKodujacyWFormacieServerInit_KlatkaDekodujeSiePoprawnie()
    {
        await using var server = QtStyleServer(leftoverBytesPerPixel: 2, NotQtName);

        (Exception? ex, _) = await CaptureFirstFrame(server);

        Assert.Null(ex);
    }

    [Fact]
    public async Task SerwerInnyNizQt_KlientNieWysylaSetPixelFormat_TylkoSetEncodingsIFramebufferUpdateRequest()
    {
        await using var server = QtStyleServer(leftoverBytesPerPixel: 2, NotQtName);

        Assert.Null((await CaptureFirstFrame(server)).Error);

        byte[] types = [.. server.ClientMessageTypes];
        Assert.NotEmpty(types);
        Assert.Equal(2, types[0]);
        Assert.DoesNotContain((byte)0, types);
        Assert.All(types, t => Assert.True(t is 2 or 3, $"Nieoczekiwany typ wiadomości klienta: {t}"));
    }


    [Theory]
    [InlineData(1)]
    [InlineData(4)] // 32 bpp poprzedniego klienta
    [InlineData(3)] // format „niezainicjowany” (dowolna inna wartość) — pierwszy klient od startu HMI
    public async Task SerwerQt_ZFormatemZastanym_KlientUstawiaFormatZServerInit_KlatkaPoprawnaBez1282(int leftoverBytesPerPixel)
    {
        await using var server = QtStyleServer(leftoverBytesPerPixel, QtEmbeddedVncServer.DesktopName);

        (Exception? ex, RgbaImage? image) = await CaptureFirstFrame(server);

        Assert.Null(ex);
        Assert.NotNull(image);
        Assert.Equal(new Rgba(255, 0, 0), image!.GetPixel(0, 0));
        Assert.Equal(new Rgba(255, 0, 0), image.GetPixel(Width - 1, Height - 1));
        Assert.Equal(RfbPixelFormat.Rgb565LittleEndian, server.ClientPixelFormat);
    }

    [Fact]
    public async Task SerwerQt_SetPixelFormatJestPierwszaWiadomosciaPoServerInit_JedenRaz_ZFormatemServerInit()
    {
        await using var server = QtStyleServer(leftoverBytesPerPixel: 1, QtEmbeddedVncServer.DesktopName);

        Assert.Null((await CaptureFirstFrame(server)).Error);

        byte[] types = [.. server.ClientMessageTypes];
        Assert.Equal(new byte[] { 0, 2, 3 }, types);
        RfbPixelFormat sent = Assert.Single(server.ReceivedPixelFormats);
        Assert.Equal(RfbPixelFormat.Rgb565LittleEndian, sent);

        // Bajt w bajt: typ 0, 3 bajty wypełnienia, 16 bajtów PIXEL_FORMAT z ServerInit (RFC 6143 §7.5.1).
        byte[] expected = new byte[20];
        RfbPixelFormat.Rgb565LittleEndian.Write(expected.AsSpan(4, 16));
        Assert.Equal(expected, server.ClientBytesAfterInit.Take(20).ToArray());
    }

    [Theory]
    [InlineData("qt for embedded linux vnc server")]
    [InlineData("  Qt  for Embedded\tLinux VNC   Server ")]
    [InlineData("Qt for Embedded Linux VNC Server (520)")]
    public async Task SerwerQt_NazwaZInnaWielkosciaLiterIBialymiZnakami_TezDostajeSetPixelFormat(string name)
    {
        await using var server = QtStyleServer(leftoverBytesPerPixel: 1, name);

        Assert.Null((await CaptureFirstFrame(server)).Error);

        Assert.Equal((byte)0, server.ClientMessageTypes.First());
    }


    private static byte[] CursorRectangle(int hotX, int hotY, int w, int h)
    {
        byte[] header = ScriptedRfbServer.RectangleHeader(hotX, hotY, w, h, -239);
        byte[] pixels = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++) BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2), 0x07E0);
        byte[] mask = Enumerable.Repeat((byte)0xFF, ((w + 7) / 8) * h).ToArray();
        return [.. header, .. pixels, .. mask];
    }

    private static async Task WriteRedHextileTilesAsync(System.Net.Sockets.NetworkStream s, CancellationToken ct)
    {
        for (int i = 0; i < 2; i++)
        {
            await s.WriteAsync(ScriptedRfbServer.RectangleHeader(i * 16, 0, 16, 16, RfbEncodings.Hextile), ct);
            byte[] tile = new byte[3];
            tile[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(tile.AsSpan(1), 0xF800);
            await s.WriteAsync(tile, ct);
        }
    }

    [Fact]
    public async Task SerwerQt_SetEncodingsZawieraCursor_SerwerInnyNizQtNie()
    {
        await using var qt = QtStyleServer(leftoverBytesPerPixel: 2, QtEmbeddedVncServer.DesktopName);
        await using var other = QtStyleServer(leftoverBytesPerPixel: 2, NotQtName);

        Assert.Null((await CaptureFirstFrame(qt)).Error);
        Assert.Null((await CaptureFirstFrame(other)).Error);

        Assert.Equal(new[] { RfbEncodings.Hextile, RfbEncodings.CopyRect, RfbEncodings.Raw, -239 }, qt.ReceivedEncodings);
        Assert.Equal(new[] { RfbEncodings.Hextile, RfbEncodings.CopyRect, RfbEncodings.Raw }, other.ReceivedEncodings);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(16, 16)]
    [InlineData(17, 20)]
    public async Task SerwerQt_OdpowiedzSamymKursorem_KlientPomijaKursorIPonawiaZadanie_KlatkaBezKursora(int w, int h)
    {
        ScriptedRfbServer? server = null;
        server = new ScriptedRfbServer
        {
            Width = Width,
            Height = Height,
            Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                if (number == 0)
                {
                    await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                    await s.WriteAsync(CursorRectangle(2, 3, w, h), ct);
                    return;
                }

                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(2), ct);
                await WriteRedHextileTilesAsync(s, ct);
            },
        };
        await using (server)
        {
            (Exception? ex, RgbaImage? image) = await CaptureFirstFrame(server);

            Assert.Null(ex);
            Assert.All(Enumerable.Range(0, Width), x => Assert.Equal(new Rgba(255, 0, 0), image!.GetPixel(x, 3)));
            Assert.Equal(2, server.ClientMessageTypes.Count(t => t == 3));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 12)]
    public async Task SerwerQt_KursorIRawWJednejAktualizacji_KlatkaPoprawnaKursorPominiety(int w, int h)
    {
        await using var server = new ScriptedRfbServer
        {
            Width = Width,
            Height = Height,
            Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(2), ct);
                await s.WriteAsync(CursorRectangle(0, 0, w, h), ct);
                await s.WriteAsync(ScriptedRfbServer.RectangleHeader(0, 0, Width, Height, RfbEncodings.Raw), ct);
                byte[] raw = new byte[Width * Height * 2];
                for (int i = 0; i < Width * Height; i++) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2), 0xF800);
                await s.WriteAsync(raw, ct);
            },
        };

        (Exception? ex, RgbaImage? image) = await CaptureFirstFrame(server);

        Assert.Null(ex);
        Assert.Equal(new Rgba(255, 0, 0), image!.GetPixel(0, 0));
        Assert.Equal(new Rgba(255, 0, 0), image.GetPixel(5, 5));
        Assert.Equal(1, server.ClientMessageTypes.Count(t => t == 3));
    }

    [Fact]
    public async Task SerwerInnyNizQt_NieproszonyKursor_NadalNieobslugiwaneKodowanie()
    {
        await using var server = new ScriptedRfbServer
        {
            Width = Width,
            Height = Height,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = NotQtName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(CursorRectangle(0, 0, 4, 4), ct);
            },
        };

        (Exception? ex, _) = await CaptureFirstFrame(server);

        RfbProtocolException protocol = Assert.IsType<RfbProtocolException>(ex);
        Assert.Contains("kodowanie prostokąta aktualizacji: -239 ", protocol.Message);
    }
}
