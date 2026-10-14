using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Żądania <c>FramebufferUpdateRequest</c>: ROI, przycinanie i tryb przyrostowy.</summary>
public sealed class UpdateRequestTests
{
    [Fact]
    public async Task IncrementalRoiRequest_SendsCorrectRectangle()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = screen });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        var roi = new RectInt(0, 0, 64, 8);
        RgbaImage strip = await client.CaptureAsync(roi, incremental: true, cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Equal(new FakeUpdateRequest(0, 0, 64, 8, true), session.UpdateRequests[^1]);

        Assert.Equal(64, strip.Width);
        Assert.Equal(8, strip.Height);
        ImageAssert.EqualToRegion(screen, roi, strip);
    }

    [Fact]
    public async Task RoiOutsideScreen_IsClampedBeforeSending()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = screen });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        RgbaImage corner = await client.CaptureAsync(new RectInt(60, 40, 100, 100), incremental: true, cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        Assert.Equal(new FakeUpdateRequest(60, 40, 4, 8, true), session.UpdateRequests[^1]);

        Assert.Equal(4, corner.Width);
        Assert.Equal(8, corner.Height);
        ImageAssert.EqualToRegion(screen, new RectInt(60, 40, 4, 8), corner);
    }

    [Fact]
    public async Task CaptureAsync_RoiCompletelyOutsideScreen_ThrowsArgumentExceptionWithoutSendingRequest()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = screen });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        int requestsBefore = session.UpdateRequests.Count;

        var roi = new RectInt(5000, 5000, 10, 10);
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CaptureAsync(roi, false, cts.Token));

        Assert.Contains("poza ekranem", ex.Message, StringComparison.Ordinal);
        Assert.Equal(requestsBefore, session.UpdateRequests.Count);
    }

    [Fact]
    public async Task RequestUpdateAsync_RoiCompletelyOutsideScreen_ThrowsArgumentExceptionWithoutSendingRequest()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = screen });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        int requestsBefore = session.UpdateRequests.Count;

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.RequestUpdateAsync(new RectInt(-50, -50, 10, 10), false, cts.Token));

        Assert.Equal(requestsBefore, session.UpdateRequests.Count);
    }

    [Fact]
    public async Task IncrementalWithoutChange_ServerSendsEmptyUpdate_ClientDoesNotHang()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage first = TestScreens.Gradient(32, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = first,
            IncrementalSendsNothingWhenUnchanged = true,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        FramebufferUpdate full = await client.UpdateAsync(new RectInt(0, 0, 32, 32), false, cts.Token);
        Assert.Single(full.Rectangles);

        FramebufferUpdate unchanged = await client.UpdateAsync(new RectInt(0, 0, 32, 32), true, cts.Token);
        Assert.Empty(unchanged.Rectangles);
        ImageAssert.Equal(first, client.Framebuffer.Snapshot());

        // Po podmianie ekranu przyrostowe żądanie znowu dostaje dane.
        RgbaImage second = TestScreens.MirroredHalves(32, 32);
        server.Screen = second;

        FramebufferUpdate changed = await client.UpdateAsync(new RectInt(0, 0, 32, 32), true, cts.Token);
        Assert.Single(changed.Rectangles);
        ImageAssert.Equal(second, client.Framebuffer.Snapshot());
    }

    [Fact]
    public async Task TryReceiveUpdateAsync_ReturnsNullAfterIdleTimeout_ConnectionStaysUsableAfterwards()
    {
        // serwer WinCE/OpenCnc na niezmienionym ekranie potrafi nie odpowiadać WCALE na
        // żądanie przyrostowe. Klient nie może ani zawiesić się do twardego ReadTimeout, ani uznać to za
        // zerwanie połączenia — musi zwrócić null i zostać gotowy na kolejne (pełne) żądanie.
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(32, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = screen,
            IncrementalNoResponseWhenUnchanged = true,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(5)));
        await client.ConnectAsync(cts.Token);

        // Ustala "wersję" ekranu po stronie fałszywego serwera — pełne (nieprzyrostowe) żądanie zawsze
        // dostaje natychmiastową odpowiedź, niezależnie od IncrementalNoResponseWhenUnchanged.
        await client.CaptureAsync(new RectInt(0, 0, 32, 32), incremental: false, cts.Token);

        await client.RequestUpdateAsync(new RectInt(0, 0, 32, 32), incremental: true, cts.Token);
        FramebufferUpdate? none = await client.TryReceiveUpdateAsync(TimeSpan.FromMilliseconds(200), cts.Token);

        Assert.Null(none);
        Assert.True(client.IsConnected, "Brak odpowiedzi w oknie oczekiwania nie może zamykać połączenia.");

        // Połączenie nadal działa: kolejne pełne (nieprzyrostowe) żądanie dostaje poprawną odpowiedź.
        RgbaImage frame = await client.CaptureAsync(new RectInt(0, 0, 32, 32), incremental: false, cts.Token);
        ImageAssert.Equal(screen, frame);
    }

    [Fact]
    public async Task TryCaptureAsync_GdyEkranSieNieZmienia_ZwracaNullBezWysylaniaKolejnegoZadania()
    {
        // gdy poprzednie żądanie na ten sam ROI wciąż czeka na odpowiedź, TryCaptureAsync nie
        // wysyła nowego (nie zalewa serwera WinCE żądaniami co cykl) — tylko czeka dalej na tę samą odpowiedź.
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(32, 32);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = screen,
            IncrementalNoResponseWhenUnchanged = true,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(5)));
        await client.ConnectAsync(cts.Token);
        await client.CaptureAsync(new RectInt(0, 0, 32, 32), incremental: false, cts.Token);

        var roi = new RectInt(0, 0, 32, 32);
        RgbaImage? first = await client.TryCaptureAsync(roi, incremental: true, TimeSpan.FromMilliseconds(150), cts.Token);
        RgbaImage? second = await client.TryCaptureAsync(roi, incremental: true, TimeSpan.FromMilliseconds(150), cts.Token);

        Assert.Null(first);
        Assert.Null(second);

        FakeClientSession session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
        // Jedno żądanie nieprzyrostowe (baza) + JEDNO przyrostowe (drugie wywołanie TryCaptureAsync nie
        // wysłało nowego, bo poprzednie wciąż "czekało") — łącznie dwa, nie trzy.
        Assert.Equal(2, session.UpdateRequests.Count);
        Assert.True(client.IsConnected);

        // Jawne (niezależne od licznika "pending") pełne żądanie nadal działa poprawnie.
        RgbaImage frame = await client.CaptureAsync(roi, incremental: false, cts.Token);
        ImageAssert.Equal(screen, frame);
    }

    [Fact]
    public async Task RequestUpdateBeforeConnect_ThrowsInvalidOperation()
    {
        await using var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RequestUpdateAsync(new RectInt(0, 0, 1, 1), false, CancellationToken.None));
    }
}
