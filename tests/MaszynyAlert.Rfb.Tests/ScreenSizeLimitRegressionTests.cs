using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class ScreenSizeLimitRegressionTests
{
    private const int MaxScreenDimension = 8192;

    [Theory]
    [InlineData(65535, 65535)] // maksimum, jakie w ogóle mieści się w polu ServerInit
    [InlineData(40000, 40000)]
    [InlineData(MaxScreenDimension + 1, 600)] // tuż powyżej granicy
    [InlineData(1024, MaxScreenDimension + 1)]
    [InlineData(0, 0)] // serwer bez ekranu
    [InlineData(1024, 0)]
    public async Task ConnectAsync_GdySerwerZglaszaNierealistycznyRozmiarEkranu_RzucaRfbProtocolException(int width, int height)
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(16, 16),
            Width = width,
            Height = height,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));

        RfbProtocolException ex = await Assert.ThrowsAsync<RfbProtocolException>(() => client.ConnectAsync(cts.Token));

        Assert.Contains("nieprawidłowy rozmiar ekranu", ex.Message, StringComparison.Ordinal);
        Assert.Contains(MaxScreenDimension.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData(MaxScreenDimension, MaxScreenDimension)]
    [InlineData(5000, 4000)] // 20 Mpx
    public async Task ConnectAsync_GdyLacznaLiczbaPikseliPrzekraczaLimit_RzucaRfbProtocolExceptionBezAlokacji(int width, int height)
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(16, 16),
            Width = width,
            Height = height,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));

        RfbProtocolException ex = await Assert.ThrowsAsync<RfbProtocolException>(() => client.ConnectAsync(cts.Token));
        Assert.Contains("nieprawidłowy rozmiar ekranu", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_GdySerwerZglaszaDokladnieGorniaGranice_LaczySieNormalnie()
    {
        // Granica jest DOPUSZCZALNA (8192 w każdym wymiarze) — testujemy ją wąskim ekranem, żeby nie
        // alokować 256 MB bufora tylko po to, by sprawdzić warunek brzegowy.
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(MaxScreenDimension, 1),
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.Equal(MaxScreenDimension, info.Width);
        Assert.Equal(1, info.Height);
        Assert.True(client.IsConnected);

        var roi = new RectInt(0, 0, 16, 1);
        RgbaImage strip = await client.CaptureAsync(roi, incremental: false, cts.Token);
        ImageAssert.EqualToRegion(server.Screen, roi, strip);
    }

    [Theory]
    [InlineData(40000, 40000)]
    [InlineData(MaxScreenDimension + 1, 600)]
    [InlineData(0, 600)]
    [InlineData(MaxScreenDimension, MaxScreenDimension)]
    public async Task CaptureAsync_GdyDesktopSizeZglaszaNierealistycznyRozmiar_RzucaRfbProtocolException(int width, int height)
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(32, 32),
            AnnounceDesktopSize = true,
            AnnouncedDesktopWidth = width,
            AnnouncedDesktopHeight = height,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RfbProtocolException ex = await Assert.ThrowsAsync<RfbProtocolException>(
            () => client.CaptureAsync(new RectInt(0, 0, 32, 32), incremental: false, cts.Token));

        Assert.Contains("nieprawidłowy rozmiar ekranu", ex.Message, StringComparison.Ordinal);
        Assert.Contains(MaxScreenDimension.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.False(client.IsConnected);

        // Bufor ramki NIE został powiększony do zgłoszonego rozmiaru (nic wielkiego nie zaalokowano).
        Assert.Equal(32, client.Framebuffer.Width);
        Assert.Equal(32, client.Framebuffer.Height);
    }

    [Fact]
    public async Task CaptureAsync_GdyDesktopSizeZglaszaRozsadnyRozmiar_ZmieniaBuforRamki()
    {
        // Kontrola pozytywna: normalna zmiana rozdzielczości maszyny nadal działa.
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(32, 32),
            AnnounceDesktopSize = true,
            AnnouncedDesktopWidth = 64,
            AnnouncedDesktopHeight = 48,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);
        await client.CaptureAsync(new RectInt(0, 0, 32, 32), incremental: false, cts.Token);

        Assert.Equal(64, client.Framebuffer.Width);
        Assert.Equal(48, client.Framebuffer.Height);
        Assert.Equal(64, client.ServerInfo!.Width);
        Assert.True(client.IsConnected);
    }
}
