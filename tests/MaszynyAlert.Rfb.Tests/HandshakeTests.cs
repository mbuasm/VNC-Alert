using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Negocjacja wersji protokołu i typu zabezpieczeń dla RFB 3.3 / 3.7 / 3.8.</summary>
public sealed class HandshakeTests
{
    [Fact]
    public async Task Handshake38_NoneAuth_ReportsServerInfo()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [(byte)RfbSecurityType.None],
            PixelFormat = RfbPixelFormat.Bgra32LittleEndian,
            Screen = TestScreens.Gradient(64, 48),
            DesktopName = "Aidite N-1",
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.Equal("3.8", info.ProtocolVersion);
        Assert.Equal(RfbSecurityType.None, info.UsedSecurityType);
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal("Aidite N-1", info.DesktopName);
        Assert.Equal(32, info.PixelFormat.BitsPerPixel);
        Assert.True(info.PixelFormat.TrueColour);
        Assert.True(client.IsConnected);
        Assert.Equal(new byte[] { (byte)RfbSecurityType.None }, info.OfferedSecurityTypes);
    }

    [Fact]
    public async Task Handshake33_VncAuth_Succeeds()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.3",
            SecurityTypes = [(byte)RfbSecurityType.VncAuth],
            Password = "abc",
            Screen = TestScreens.Gradient(32, 16),
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Password = "abc"));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.Equal("3.3", info.ProtocolVersion);
        Assert.Equal(RfbSecurityType.VncAuth, info.UsedSecurityType);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Handshake37_NoneAuth_SucceedsWithoutSecurityResult()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.7",
            SecurityTypes = [(byte)RfbSecurityType.None],
            Screen = TestScreens.Gradient(24, 24),
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.Equal("3.7", info.ProtocolVersion);
        Assert.Equal(RfbSecurityType.None, info.UsedSecurityType);

        // Serwer 3.7 NIE wysyła SecurityResult dla None — gdyby klient go czekał, ten odczyt by wisiał.
        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 24, 24), false, cts.Token);
        Assert.Equal(24, frame.Width);
    }

    [Fact]
    public async Task Handshake38_ClientAnnouncesSameVersion()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = TestScreens.Gradient(16, 16) });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Equal("RFB 003.008\n", session.ClientProtocolVersion);
    }
}
