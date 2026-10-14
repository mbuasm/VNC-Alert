using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Wiadomości serwera inne niż <c>FramebufferUpdate</c> nie mogą zaburzać odczytu ramek.</summary>
public sealed class ServerMessageTests
{
    [Fact]
    public async Task BellAndServerCutText_AreIgnored()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(48, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = screen,
            SendBellBeforeUpdate = true,
            SendServerCutTextBeforeUpdate = true,
            ServerCutText = "tekst ze schowka maszyny",
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 48, 32), false, cts.Token);
        ImageAssert.Equal(screen, frame);

        // Druga aktualizacja — Bell/ServerCutText nie mogą rozjechać strumienia.
        RgbaImage again = await client.CaptureAsync(new RectInt(0, 0, 48, 32), true, cts.Token);
        ImageAssert.Equal(screen, again);
    }

    [Fact]
    public async Task ServerCutText_Empty_IsIgnored()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(16, 16);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = screen,
            SendServerCutTextBeforeUpdate = true,
            ServerCutText = string.Empty,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        ImageAssert.Equal(screen, await client.CaptureAsync(new RectInt(0, 0, 16, 16), false, cts.Token));
    }
}
