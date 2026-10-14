using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Zawartość <c>ClientInit</c> (flaga shared) i <c>SetEncodings</c>.</summary>
public sealed class ClientInitAndEncodingsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientInit_SendsSharedFlag(bool shared)
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = TestScreens.Gradient(16, 16) });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Shared = shared));
        await client.ConnectAsync(cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Equal(shared, session.Shared);
    }

    [Fact]
    public async Task ClientInit_DefaultsToShared()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = TestScreens.Gradient(16, 16) });

        var options = new RfbClientOptions { Host = "127.0.0.1", Port = server.Port };
        Assert.True(options.Shared);

        await using var client = new RfbClient(options);
        await client.ConnectAsync(cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.True(session.Shared);
    }

    [Fact]
    public async Task SetEncodings_SentOnceWithConfiguredOrderAndNoPseudoEncodings()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = TestScreens.Gradient(16, 16) });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        int[] encodings = await session.WaitForEncodingsAsync(TestSupport.ShortWait);

        Assert.Equal(new[] { RfbEncodings.Hextile, RfbEncodings.CopyRect, RfbEncodings.Raw }, encodings);
        Assert.All(encodings, e => Assert.True(e >= 0, $"Kodowanie {e} to pseudo-kodowanie — klient nie powinien go zgłaszać."));

        // Po kilku aktualizacjach SetEncodings nadal ma być wysłany dokładnie raz.
        await client.CaptureAsync(new RectInt(0, 0, 16, 16), false, cts.Token);
        await client.CaptureAsync(new RectInt(0, 0, 16, 16), true, cts.Token);

        Assert.Single(session.ReceivedMessageTypes, t => t == 2);
    }

    [Fact]
    public async Task SetEncodings_HonoursCustomOrder()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = TestScreens.Gradient(16, 16) });

        await using var client = new RfbClient(TestSupport.ClientOptions(
            server, o => o.Encodings = [RfbEncodings.Raw, RfbEncodings.Hextile]));
        await client.ConnectAsync(cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        int[] encodings = await session.WaitForEncodingsAsync(TestSupport.ShortWait);

        Assert.Equal(new[] { RfbEncodings.Raw, RfbEncodings.Hextile }, encodings);
    }
}
