using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class ReportRegressionTests
{
    private static readonly RectInt Full = new(0, 0, 32, 32);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSmallRoi_DoesNotSatisfyFullCapture(bool tryCapture)
    {
        using var cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        { Screen = TestScreens.Gradient(32, 32), IncrementalNoResponseWhenUnchanged = true });
        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);
        await client.CaptureAsync(Full, false, cts.Token);
        var small = new RectInt(0, 0, 2, 2);
        Assert.Null(await client.TryCaptureAsync(small, true, TimeSpan.FromMilliseconds(50), cts.Token));
        var session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        server.Screen = TestScreens.MirroredHalves(32, 32);
        // Zaległa odpowiedź tylko na 2×2, już w drodze przed nowym żądaniem pełnej klatki.
        byte[] reply = new byte[16 + 16];
        reply[3] = 1; reply[9] = 2; reply[11] = 2; // 1 prostokąt Raw, 2×2
        await session.Tcp.GetStream().WriteAsync(reply, cts.Token);
        RgbaImage? image;
        if (tryCapture)
        {
            image = await client.TryCaptureAsync(Full, true, TimeSpan.FromSeconds(1), cts.Token);
            while (image is null)
                image = await client.TryCaptureAsync(Full, true, TimeSpan.FromSeconds(1), cts.Token);
        }
        else image = await client.CaptureAsync(Full, false, cts.Token);
        ImageAssert.Equal(server.Screen, image);
        Assert.False(session.UpdateRequests[^1].Incremental);
    }

    [Fact]
    public async Task BellWithoutUpdate_KeepsIdleTimeoutAndConnection()
    {
        using var cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        { IncrementalNoResponseWhenUnchanged = true });
        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(3)));
        await client.ConnectAsync(cts.Token);
        await client.CaptureAsync(new(0, 0, 16, 16), false, cts.Token);
        Assert.Null(await client.TryCaptureAsync(new(0, 0, 16, 16), true, TimeSpan.FromMilliseconds(50), cts.Token));
        var session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        await session.Tcp.GetStream().WriteAsync(new byte[] { 2 }, cts.Token);
        Assert.Null(await client.TryReceiveUpdateAsync(TimeSpan.FromMilliseconds(100), cts.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(client.IsConnected);
        ImageAssert.Equal(server.Screen, await client.CaptureAsync(new(0, 0, 16, 16), false, cts.Token));
    }

    [Fact]
    public async Task ClosedSocket_DoesNotLookLikeUnchangedScreen()
    {
        using var cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions());
        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);
        var session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        session.Close();
        await Assert.ThrowsAsync<RfbException>(() => client.TryReceiveUpdateAsync(TimeSpan.FromSeconds(1), cts.Token));
        Assert.False(client.IsConnected);
    }
}
