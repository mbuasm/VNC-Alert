using System.Diagnostics;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class DripFeedRegressionTests
{
    private static readonly RectInt Full = new(0, 0, 64, 32);

    private static ScriptedRfbServer DripFeed(int rectangles, int intervalMs) => new()
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

            await s.WriteAsync(ScriptedRfbServer.UpdateHeader(rectangles), ct);
            for (int i = 0; i < rectangles; i++)
            {
                await Task.Delay(intervalMs, ct);
                await s.WriteAsync(ScriptedRfbServer.RectangleHeader(i % 64, 0, 1, 1, 0), ct);
                await s.WriteAsync(new byte[4], ct);
            }
        },
    };

    [Fact]
    public async Task TryCaptureAsync_AktualizacjaKroplowka_PrzerwanaPoLimicieOdczytu()
    {
        await using var server = DripFeed(rectangles: 10, intervalMs: 800);
        server.Start();
        await using var client = new RfbClient(new RfbClientOptions
        {
            Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(1),
        });
        await client.ConnectAsync(CancellationToken.None);
        await client.CaptureAsync(Full, incremental: false, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        Exception? ex = await Record.ExceptionAsync(
            () => client.TryCaptureAsync(Full, incremental: true, TimeSpan.FromMilliseconds(500), CancellationToken.None));
        watch.Stop();

        Assert.IsAssignableFrom<RfbException>(ex);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Wróciło po {watch.ElapsedMilliseconds} ms (bez limitu ~8 s).");
        Assert.False(client.IsConnected);
    }
}
