using System.Buffers.Binary;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtCursorReviewTests
{
    private const int Width = 32;
    private const int Height = 16;

    private static byte[] CursorRectangle(int w, int h, int bytesPerPixel, byte fill)
    {
        byte[] header = ScriptedRfbServer.RectangleHeader(1, 1, w, h, -239);
        byte[] pixels = Enumerable.Repeat(fill, w * h * bytesPerPixel).ToArray();
        byte[] mask = Enumerable.Repeat((byte)0xFF, ((w + 7) / 8) * h).ToArray();
        return [.. header, .. pixels, .. mask];
    }

    private static async Task WriteRawRed565Async(System.Net.Sockets.NetworkStream s, CancellationToken ct)
    {
        await s.WriteAsync(ScriptedRfbServer.RectangleHeader(0, 0, Width, Height, RfbEncodings.Raw), ct);
        byte[] raw = new byte[Width * Height * 2];
        for (int i = 0; i < Width * Height; i++) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2), 0xF800);
        await s.WriteAsync(raw, ct);
    }

    [Fact]
    public async Task SerwerQt_ZapisWiadomosciKlientaPoServerInit_BajtWBajt()
    {
        await using var server = new ScriptedRfbServer
        {
            Width = Width, Height = Height, Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await WriteRawRed565Async(s, ct);
            },
        };
        server.Start();
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port }))
        {
            await client.ConnectAsync(CancellationToken.None);
            await client.CaptureAsync(new RectInt(0, 0, Width, Height), incremental: false, CancellationToken.None);
        }

        byte[] pixelFormat = new byte[16];
        RfbPixelFormat.Rgb565LittleEndian.Write(pixelFormat);
        byte[] expected =
        [
            0, 0, 0, 0, .. pixelFormat, // SetPixelFormat z formatem ServerInit
            2, 0, 0, 4, 0, 0, 0, 5, 0, 0, 0, 1, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x11, // SetEncodings [5,1,0,-239]
            3, 0, 0, 0, 0, 0, 0, Width, 0, Height, // FramebufferUpdateRequest nieprzyrostowy
        ];
        byte[] actual = server.ClientBytesAfterInit;
        for (int i = 0; actual.Length < expected.Length && i < 50; i++)
        {
            await Task.Delay(20);
            actual = server.ClientBytesAfterInit;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(17, 20)]
    [InlineData(1, 1)]
    [InlineData(32, 32)]
    public async Task SerwerQt32bpp_SamKursor_PotemKlatka_DlugoscKursoraWFormacie4B(int w, int h)
    {
        await using var server = new ScriptedRfbServer
        {
            Width = Width, Height = Height, Rfb33 = true,
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                if (number == 0)
                {
                    await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                    await s.WriteAsync(CursorRectangle(w, h, 4, 0x55), ct);
                    return;
                }

                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(ScriptedRfbServer.RectangleHeader(0, 0, Width, Height, RfbEncodings.Raw), ct);
                byte[] raw = new byte[Width * Height * 4];
                for (int i = 0; i < Width * Height; i++) { raw[i * 4] = 0; raw[(i * 4) + 1] = 0; raw[(i * 4) + 2] = 255; } // BGRA czerwony
                await s.WriteAsync(raw, ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(3) });
        await client.ConnectAsync(CancellationToken.None);

        RgbaImage image = await client.CaptureAsync(new RectInt(0, 0, Width, Height), incremental: false, CancellationToken.None);

        Assert.Equal(new Rgba(255, 0, 0), image.GetPixel(0, 0));
        Assert.Equal(new Rgba(255, 0, 0), image.GetPixel(Width - 1, Height - 1));
        Assert.Equal(2, server.ClientMessageTypes.Count(t => t == 3));
    }

    [Fact]
    public async Task SerwerQt_KilkaOdpowiedziSamymKursorem_JednoPonowienieNaOdpowiedz_BezLawiny()
    {
        await using var server = new ScriptedRfbServer
        {
            Width = Width, Height = Height, Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                if (number < 3)
                {
                    await s.WriteAsync(CursorRectangle(5, 5, 2, 0x11), ct);
                    return;
                }

                await WriteRawRed565Async(s, ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(3) });
        await client.ConnectAsync(CancellationToken.None);

        RgbaImage image = await client.CaptureAsync(new RectInt(0, 0, Width, Height), incremental: false, CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(new Rgba(255, 0, 0), image.GetPixel(3, 3));
        Assert.Equal(4, server.ClientMessageTypes.Count(t => t == 3)); // 1 + 3 ponowienia, nic więcej
    }

    [Fact]
    public async Task SerwerQt_PrzyrostoweZadanie_OdpowiedzSamymKursorem_BezPonowienia_NastepnyCyklNoweZadanie()
    {
        await using var server = new ScriptedRfbServer
        {
            Width = Width, Height = Height, Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                if (number == 1)
                {
                    await s.WriteAsync(CursorRectangle(4, 4, 2, 0x22), ct); // odpowiedź na żądanie przyrostowe
                    return;
                }

                await WriteRawRed565Async(s, ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(3) });
        await client.ConnectAsync(CancellationToken.None);
        var roi = new RectInt(0, 0, Width, Height);

        await client.CaptureAsync(roi, incremental: false, CancellationToken.None);
        RgbaImage? afterCursor = await client.TryCaptureAsync(roi, incremental: true, TimeSpan.FromSeconds(2), CancellationToken.None);
        await Task.Delay(200);
        int requestsAfterCursor = server.ClientMessageTypes.Count(t => t == 3);
        RgbaImage? next = await client.TryCaptureAsync(roi, incremental: true, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.NotNull(afterCursor); // bufor bez zmian, kursor nie narysowany
        Assert.Equal(new Rgba(255, 0, 0), afterCursor!.GetPixel(2, 2));
        Assert.Equal(2, requestsAfterCursor); // bez ponowienia po odpowiedzi „sam kursor” na żądanie przyrostowe
        Assert.NotNull(next);
        Assert.Equal(3, server.ClientMessageTypes.Count(t => t == 3));
    }

    [Fact]
    public async Task SerwerQt_NieruchomyEkran_PrzyrostoweBezOdpowiedzi_NullBezRozlaczeniaIBezNowegoZadania()
    {
        await using var server = new ScriptedRfbServer
        {
            Width = Width, Height = Height, Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                if (number > 0) return;
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await WriteRawRed565Async(s, ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(3) });
        await client.ConnectAsync(CancellationToken.None);
        var roi = new RectInt(0, 0, Width, Height);

        await client.CaptureAsync(roi, incremental: false, CancellationToken.None);
        RgbaImage? first = await client.TryCaptureAsync(roi, incremental: true, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        RgbaImage? second = await client.TryCaptureAsync(roi, incremental: true, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.True(client.IsConnected);
        Assert.Equal(2, server.ClientMessageTypes.Count(t => t == 3));
    }
}
