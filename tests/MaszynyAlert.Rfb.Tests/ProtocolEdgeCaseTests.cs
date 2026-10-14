using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Nietypowe, ale spotykane w praktyce zachowania serwerów RFB.</summary>
public sealed class ProtocolEdgeCaseTests
{
    [Fact]
    public async Task Protocol33_NoneAuth_DoesNotWaitForSecurityResult()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(32, 16);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.3",
            SecurityTypes = [(byte)RfbSecurityType.None],
            Screen = screen,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(3)));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.Equal("3.3", info.ProtocolVersion);
        Assert.Equal(RfbSecurityType.None, info.UsedSecurityType);
        ImageAssert.Equal(screen, await client.CaptureAsync(new RectInt(0, 0, 32, 16), false, cts.Token));
    }

    [Fact]
    public async Task ServerWithHigherMinorVersion_IsTreatedAs38()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            RawVersionHeader = "RFB 003.889\n",
            Screen = TestScreens.Gradient(16, 16),
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.Equal("3.8", info.ProtocolVersion);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Equal("RFB 003.008\n", session.ClientProtocolVersion);
    }

    [Fact]
    public async Task ServerWithHigherMajorVersion_RealVnc5_IsTreatedAs38()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            RawVersionHeader = "RFB 005.000\n",
            SecurityTypes = [(byte)RfbSecurityType.VncAuth],
            Password = "tajne",
            Screen = TestScreens.Gradient(16, 16),
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Password = "tajne"));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.Equal("3.8", info.ProtocolVersion);
        Assert.Equal(RfbSecurityType.VncAuth, info.UsedSecurityType);
        Assert.True(client.IsConnected);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Equal("RFB 003.008\n", session.ClientProtocolVersion);
    }

    [Fact]
    public async Task ServerWithMajorVersionBelow3_ThrowsProtocolException()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            RawVersionHeader = "RFB 002.003\n",
            Screen = TestScreens.Gradient(16, 16),
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));

        RfbProtocolException ex = await Assert.ThrowsAsync<RfbProtocolException>(
            () => client.ConnectAsync(cts.Token));
        Assert.Contains("RFB 2.3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedVersionHeader_ThrowsProtocolException()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            RawVersionHeader = "NIE-JEST-RFB", // dokładnie 12 bajtów, ale zły format
            Screen = TestScreens.Gradient(16, 16),
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));

        await Assert.ThrowsAsync<RfbProtocolException>(() => client.ConnectAsync(cts.Token));
    }

    [Fact]
    public async Task UnsupportedRectangleEncoding_ThrowsProtocolException()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(32, 32),
            OverrideRectEncoding = RfbEncodings.ZRLE,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RfbProtocolException ex = await Assert.ThrowsAsync<RfbProtocolException>(
            () => client.CaptureAsync(new RectInt(0, 0, 32, 32), false, cts.Token));

        Assert.Contains("16", ex.Message);
    }

    [Fact]
    public async Task UnknownServerMessageType_ThrowsProtocolException()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(32, 32),
            SendUnknownServerMessageType = 250,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        await Assert.ThrowsAsync<RfbProtocolException>(
            () => client.CaptureAsync(new RectInt(0, 0, 32, 32), false, cts.Token));
    }

    [Fact]
    public async Task ServerInitWithHugeDimensions_ThrowsProtocolExceptionInsteadOfHugeAllocation()
    {
        // serwer zgłaszający np. 65535×65535 w ServerInit doprowadzałby do alokacji rzędu
        // gigabajtów / przepełnienia arytmetyki `int` w konstruktorze RgbaImage. Ekran w opcjach zostaje
        // mały (16×16) — tylko ogłoszone wymiary (Width/Height) są nierealistyczne.
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(16, 16),
            Width = 65535,
            Height = 65535,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));

        RfbProtocolException ex = await Assert.ThrowsAsync<RfbProtocolException>(() => client.ConnectAsync(cts.Token));
        Assert.Contains("rozmiar ekranu", ex.Message, StringComparison.Ordinal);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DesktopSizePseudoEncoding_ResizesFramebuffer()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(80, 60);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            // ServerInit ogłasza 64x48, ale pierwszy update przynosi DesktopSize 80x60.
            Width = 64,
            Height = 48,
            Screen = screen,
            AnnounceDesktopSize = true,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);
        Assert.Equal(64, info.Width);

        FramebufferUpdate update = await client.UpdateAsync(new RectInt(0, 0, 64, 48), false, cts.Token);

        Assert.Contains(-223, update.EncodingsUsed);
        Assert.Equal(80, client.Framebuffer.Width);
        Assert.Equal(60, client.Framebuffer.Height);
        Assert.Equal(80, client.ServerInfo!.Width);
        Assert.Equal(60, client.ServerInfo.Height);

        // Po zmianie rozmiaru pełna klatka musi się zgadzać z nowym ekranem.
        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 80, 60), false, cts.Token);
        ImageAssert.Equal(screen, frame);
    }

    [Fact]
    public async Task DesktopSizeZmniejszaEkranPodROI_RfbScreenResizedExceptionBezRozlaczenia()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new ScriptedRfbServer
        {
            OnRequest = async (s, number, ct) =>
            {
                if (number == 0)
                {
                    await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                    await s.WriteAsync(ScriptedRfbServer.RectangleHeader(0, 0, 64, 32, 0), ct);
                    await s.WriteAsync(new byte[64 * 32 * 4], ct);
                    return;
                }

                // Druga odpowiedź: sam DesktopSize 20x10 (pseudo-kodowanie -223, bez danych pikseli).
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(ScriptedRfbServer.RectangleHeader(0, 0, 20, 10, -223), ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port });
        await client.ConnectAsync(cts.Token);
        var roi = new RectInt(40, 0, 20, 10);
        await client.CaptureAsync(roi, false, cts.Token);

        RfbScreenResizedException ex = await Assert.ThrowsAsync<RfbScreenResizedException>(
            () => client.CaptureAsync(roi, true, cts.Token));

        Assert.Equal(20, ex.Width);
        Assert.Equal(10, ex.Height);
        Assert.True(client.IsConnected);
    }
}
