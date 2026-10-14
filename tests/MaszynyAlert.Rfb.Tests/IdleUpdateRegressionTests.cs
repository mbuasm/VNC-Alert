using System.Diagnostics;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class IdleUpdateRegressionTests
{
    private static readonly RectInt FullRoi = new(0, 0, 32, 32);

    private static FakeRfbServerOptions SilentServer(RgbaImage screen) => new()
    {
        Screen = screen,
        IncrementalNoResponseWhenUnchanged = true,
        DeliverPendingIncrementalOnScreenChange = true,
    };

    [Fact]
    public async Task TryCaptureAsync_GdySerwerMilczy_WracaPoLimicieBezczynnosciANiePoReadTimeout()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(32, 32);
        await using var server = new FakeRfbServer(SilentServer(screen));

        // ReadTimeout celowo wielokrotnie dłuższy niż limit bezczynności — test pokazuje, że to limit
        // bezczynności (300 ms) decyduje o powrocie, a nie twardy limit odczytu (5 s).
        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(5)));
        await client.ConnectAsync(cts.Token);
        await client.CaptureAsync(FullRoi, incremental: false, cts.Token);

        var sw = Stopwatch.StartNew();
        RgbaImage? nothing = await client.TryCaptureAsync(FullRoi, incremental: true, TimeSpan.FromMilliseconds(300), cts.Token);
        sw.Stop();

        Assert.Null(nothing);
        Assert.True(client.IsConnected, "Milczenie serwera nie może być traktowane jak zerwane połączenie.");
        Assert.True(
            sw.Elapsed >= TimeSpan.FromMilliseconds(150),
            $"Powrót po {sw.ElapsedMilliseconds} ms — limit bezczynności (300 ms) najwyraźniej nie był respektowany.");
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Powrót dopiero po {sw.ElapsedMilliseconds} ms — klient czekał do twardego ReadTimeout.");
    }

    [Fact]
    public async Task TryCaptureAsync_GdyEkranSieWKoncuZmieni_ZwracaNowaTrescBezPonownegoZadania()
    {
        // zaległe żądanie zostaje u serwera (RFC 6143 §7.5.3), więc klient NIE wysyła
        // drugiego żądania na ten sam obszar — po zmianie ekranu po prostu odbiera zaległą odpowiedź.
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage before = TestScreens.Gradient(32, 32);
        RgbaImage after = TestScreens.MirroredHalves(32, 32);
        await using var server = new FakeRfbServer(SilentServer(before));

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(5)));
        await client.ConnectAsync(cts.Token);
        await client.CaptureAsync(FullRoi, incremental: false, cts.Token);

        RgbaImage? nothing = await client.TryCaptureAsync(FullRoi, incremental: true, TimeSpan.FromMilliseconds(200), cts.Token);
        Assert.Null(nothing);

        server.Screen = after;

        RgbaImage? changed = await client.TryCaptureAsync(FullRoi, incremental: true, TimeSpan.FromSeconds(3), cts.Token);

        Assert.NotNull(changed);
        ImageAssert.Equal(after, changed!);
        ImageAssert.Equal(after, client.Framebuffer.Snapshot());

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        // Jedno żądanie bazowe (nieprzyrostowe) + JEDNO przyrostowe na oba wywołania TryCaptureAsync.
        Assert.Equal(2, session.UpdateRequests.Count);
        Assert.Equal(new FakeUpdateRequest(0, 0, 32, 32, true), session.UpdateRequests[^1]);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task TryCaptureAsync_PoOdebraniuZaleglejAktualizacji_ZnowuWysylaWlasneZadanie()
    {
        // Po odebraniu odpowiedzi nic już „nie czeka”, więc kolejne wywołanie musi wysłać nowe żądanie
        // (inaczej klient zamilkłby na zawsze i przestał odświeżać pasek statusu).
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage before = TestScreens.Gradient(32, 32);
        await using var server = new FakeRfbServer(SilentServer(before));

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(5)));
        await client.ConnectAsync(cts.Token);
        await client.CaptureAsync(FullRoi, incremental: false, cts.Token);

        Assert.Null(await client.TryCaptureAsync(FullRoi, incremental: true, TimeSpan.FromMilliseconds(200), cts.Token));
        server.Screen = TestScreens.MirroredHalves(32, 32);
        Assert.NotNull(await client.TryCaptureAsync(FullRoi, incremental: true, TimeSpan.FromSeconds(3), cts.Token));

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Equal(2, session.UpdateRequests.Count);

        // Ekran znowu nieruchomy — to wywołanie MUSI wysłać kolejne (trzecie) żądanie.
        Assert.Null(await client.TryCaptureAsync(FullRoi, incremental: true, TimeSpan.FromMilliseconds(200), cts.Token));

        Assert.Equal(3, session.UpdateRequests.Count);
        Assert.True(client.IsConnected);

        // Pełne pobranie ekranu działa niezależnie od stanu „zaległego” żądania.
        RgbaImage full = await client.CaptureAsync(FullRoi, incremental: false, cts.Token);
        ImageAssert.Equal(server.Screen, full);
    }
}
