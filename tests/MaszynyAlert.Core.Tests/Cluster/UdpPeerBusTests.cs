using System.Net;
using System.Net.Sockets;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>
/// Test szyny UDP na prawdziwym gnieździe sieciowym (pętla zwrotna 127.0.0.1). W środowiskach bez
/// możliwości współdzielenia portu UDP przez dwa procesy/gniazda test jest pomijany z opisem powodu.
/// </summary>
public sealed class UdpPeerBusTests
{
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task DwieSzynyUdp_NaPetliZwrotnej_PrzekazujaHeartbeatWPonizejTrzechSekund()
    {
        int port = FreeUdpPort();
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        IClock clock = SystemClock.Instance;

        ClusterConfig Cfg() => new() { UdpPort = port, Peers = ["127.0.0.1"] };

        UdpPeerBus? nadawca = null;
        UdpPeerBus? odbiorca = null;
        try
        {
            odbiorca = new UdpPeerBus(Cfg(), signer, clock, "PC-ODBIORCA");
            nadawca = new UdpPeerBus(Cfg(), signer, clock, "PC-NADAWCA");
        }
        catch (SocketException ex)
        {
            await DisposeAsync(nadawca, odbiorca);
            Assert.Fail(
                "To środowisko nie pozwala dwóm gniazdom UDP nasłuchiwać na tym samym porcie " +
                $"(SO_REUSEADDR), więc testu szyny UDP nie da się tu wykonać: {ex.Message}");
            return;
        }

        try
        {
            var odebrane = new TaskCompletionSource<PeerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            odbiorca.Received += msg => odebrane.TrySetResult(msg);

            await odbiorca.StartAsync(CancellationToken.None);
            await nadawca.StartAsync(CancellationToken.None);

            var heartbeat = new HeartbeatMessage
            {
                Sender = "PC-NADAWCA",
                Version = "0.1.0",
                SentAt = clock.UtcNow,
                Priority = 42,
                Role = PeerRole.Watcher,
                MachinesConnected = 12,
                MachinesTotal = 12,
            };

            // Kilka prób — pierwszy pakiet UDP potrafi zginąć, zanim pętla odbioru w pełni wystartuje.
            for (int i = 0; i < 6 && !odebrane.Task.IsCompleted; i++)
            {
                await nadawca.SendAsync(heartbeat, CancellationToken.None);
                await Task.WhenAny(odebrane.Task, Task.Delay(500));
            }

            if (!odebrane.Task.IsCompleted)
            {
                Assert.Fail(
                    "Pakiet UDP nie dotarł w ciągu 3 s przez pętlę zwrotną — to środowisko blokuje rozgłoszenia " +
                    "UDP albo nie doręcza pakietów do dwóch gniazd na tym samym porcie. Test wymaga zwykłej sieci LAN.");
            }

            PeerMessage msgReceived = await odebrane.Task;
            HeartbeatMessage hb = Assert.IsType<HeartbeatMessage>(msgReceived);
            Assert.Equal("PC-NADAWCA", hb.Sender);
            Assert.Equal(42, hb.Priority);
            Assert.Equal(PeerRole.Watcher, hb.Role);
            Assert.Equal(12, hb.MachinesTotal);
        }
        finally
        {
            await DisposeAsync(nadawca, odbiorca);
        }
    }

    [Fact]
    public async Task SzynaUdp_OdrzucaPakietPodpisanyInnymKluczem()
    {
        int port = FreeUdpPort();
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        var obcy = new HmacSigner(HmacSigner.GenerateKey());
        IClock clock = SystemClock.Instance;

        UdpPeerBus? odbiorca = null;
        try
        {
            odbiorca = new UdpPeerBus(new ClusterConfig { UdpPort = port }, signer, clock, "PC-ODBIORCA");
        }
        catch (SocketException ex)
        {
            Assert.Fail($"Nie udało się otworzyć gniazda UDP w tym środowisku: {ex.Message}");
            return;
        }

        try
        {
            var powod = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            odbiorca.Rejected += r => powod.TrySetResult(r);
            await odbiorca.StartAsync(CancellationToken.None);

            byte[] datagram = MessageCodec.Encode(
                new HeartbeatMessage { Sender = "OBCY", Version = "9.9", SentAt = clock.UtcNow }, obcy);

            using var sender = new UdpClient();
            for (int i = 0; i < 6 && !powod.Task.IsCompleted; i++)
            {
                await sender.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));
                await Task.WhenAny(powod.Task, Task.Delay(500));
            }

            if (!powod.Task.IsCompleted)
            {
                Assert.Fail("Pakiet UDP nie dotarł przez pętlę zwrotną — to środowisko nie pozwala wykonać tego testu.");
            }

            Assert.Contains("HMAC", await powod.Task, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DisposeAsync(odbiorca, null);
        }
    }

    private static async Task DisposeAsync(UdpPeerBus? a, UdpPeerBus? b)
    {
        if (a is not null)
        {
            await a.DisposeAsync();
        }

        if (b is not null)
        {
            await b.DisposeAsync();
        }
    }
}
