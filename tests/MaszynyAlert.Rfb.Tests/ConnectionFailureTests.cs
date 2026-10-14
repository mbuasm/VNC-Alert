using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Zachowanie przy awariach sieci: brak serwera, cisza serwera, zerwanie w środku aktualizacji.</summary>
public sealed class ConnectionFailureTests
{
    [Fact]
    public async Task ConnectToClosedPort_ThrowsRfbExceptionQuickly()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = new RfbClient(new RfbClientOptions
        {
            Host = "127.0.0.1",
            Port = closedPort,
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        var stopwatch = Stopwatch.StartNew();
        RfbException ex = await Assert.ThrowsAnyAsync<RfbException>(() => client.ConnectAsync(cts.Token));
        stopwatch.Stop();

        // Próg 5s (a nie 1s + chwila) jest świadomie luźny: mierzymy czas ścianowy, więc na obciążonym
        // runnerze CI (mało rdzeni, testy równolegle) samo wznowienie kontynuacji po zadziałaniu limitu
        // czasu może opóźnić pomiar o sekundy. Istotne jest, że ConnectTimeout = 1s w ogóle przerywa
        // próbę połączenia, a nie że pomiar mieści się co do dziesiątych sekundy.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Zbyt długa reakcja na zamknięty port: {stopwatch.Elapsed}.");
        Assert.Contains("połączyć", ex.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task SilentServer_HitsReadTimeout_ThrowsRfbException()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Serwer, który akceptuje połączenie i milczy (nie wysyła nawet nagłówka wersji).
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(cts.Token).AsTask();

        try
        {
            await using var client = new RfbClient(new RfbClientOptions
            {
                Host = "127.0.0.1",
                Port = port,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ReadTimeout = TimeSpan.FromSeconds(1),
            });

            var stopwatch = Stopwatch.StartNew();
            RfbException ex = await Assert.ThrowsAnyAsync<RfbException>(() => client.ConnectAsync(cts.Token));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Limit czasu odczytu nie zadziałał: {stopwatch.Elapsed}.");
            Assert.Contains("limit czasu odczytu", ex.Message);
        }
        finally
        {
            listener.Stop();
            if (accepted.IsCompletedSuccessfully)
            {
                (await accepted).Dispose();
            }
        }
    }

    [Fact]
    public async Task ServerClosesMidUpdate_ThrowsRfbException()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = TestScreens.Gradient(64, 48),
            CloseAfterUpdateHeader = true,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.ReadTimeout = TimeSpan.FromSeconds(3)));
        await client.ConnectAsync(cts.Token);

        await Assert.ThrowsAnyAsync<RfbException>(
            () => client.CaptureAsync(new RectInt(0, 0, 64, 48), false, cts.Token));

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_CalledTwice_Throws()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { Screen = TestScreens.Gradient(16, 16) });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));
        await client.ConnectAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(cts.Token));
    }
}
