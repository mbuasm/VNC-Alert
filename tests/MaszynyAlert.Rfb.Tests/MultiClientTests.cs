using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class MultiClientTests
{
    [Fact]
    public async Task TwoClients_SharedServer_BothKeepWorking()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(32, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = screen });

        await using var first = new RfbClient(TestSupport.ClientOptions(server));
        await first.ConnectAsync(cts.Token);
        ImageAssert.Equal(screen, await first.CaptureAsync(new RectInt(0, 0, 32, 32), false, cts.Token));

        await using var second = new RfbClient(TestSupport.ClientOptions(server));
        await second.ConnectAsync(cts.Token);
        ImageAssert.Equal(screen, await second.CaptureAsync(new RectInt(0, 0, 32, 32), false, cts.Token));

        // Pierwszy klient nadal działa — serwer nikogo nie rozłączył.
        ImageAssert.Equal(screen, await first.CaptureAsync(new RectInt(0, 0, 32, 32), true, cts.Token));
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task KickPrevious_FirstClientDisconnected_SecondWorks()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(32, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = screen,
            KickPreviousClientOnNewConnection = true,
        });

        await using var first = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(3)));
        await first.ConnectAsync(cts.Token);
        ImageAssert.Equal(screen, await first.CaptureAsync(new RectInt(0, 0, 32, 32), false, cts.Token));

        FakeClientSession firstSession = await server.WaitForSessionAsync(0, TestSupport.ShortWait);

        await using var second = new RfbClient(TestSupport.ClientOptions(server));
        await second.ConnectAsync(cts.Token);

        // Serwer zamknął połączenie pierwszego klienta.
        await firstSession.WaitForCloseAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstSession.IsClosed);

        await Assert.ThrowsAnyAsync<RfbException>(
            () => first.CaptureAsync(new RectInt(0, 0, 32, 32), true, cts.Token));
        Assert.False(first.IsConnected);

        // Drugi klient działa normalnie.
        ImageAssert.Equal(screen, await second.CaptureAsync(new RectInt(0, 0, 32, 32), false, cts.Token));
        Assert.True(second.IsConnected);
    }

    [Fact]
    public async Task PollingLikeUsage_ThreeSequentialSessions_AllShared()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(48, 16);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = screen });

        for (int i = 0; i < 3; i++)
        {
            await using var client = new RfbClient(TestSupport.ClientOptions(server));
            await client.ConnectAsync(cts.Token);
            ImageAssert.Equal(screen, await client.CaptureAsync(new RectInt(0, 0, 48, 16), false, cts.Token));
        }

        Assert.Equal(3, server.ConnectionCount);
        foreach (FakeClientSession session in server.Sessions)
        {
            Assert.True(session.Shared, $"Sesja {session.Index} nie miała flagi shared=1.");
            Assert.All(session.ReceivedMessageTypes, t => Assert.True(t is 2 or 3));
        }
    }
}
