using System.Net;
using System.Net.Sockets;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.Rfb.Tests;

public sealed class VncScreenSourceTests
{
    [Fact]
    public async Task ConnectAsync_AnulowanyPodczasHandshake_ZamykaGniazdo()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var machine = new MachineConfig
            {
                Id = "cancelled-handshake",
                Name = "Cancelled handshake",
                Host = IPAddress.Loopback.ToString(),
                Port = port,
                Profile = "test",
            };
            var timing = new TimingConfig
            {
                ConnectTimeoutMs = 5000,
                ReadTimeoutMs = 5000,
            };

            await using var source = new VncScreenSource(machine, password: null, timing);
            using var cancellation = new CancellationTokenSource();
            Task<ScreenSourceInfo> connecting = source.ConnectAsync(cancellation.Token);
            using TcpClient serverSide = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);

            byte[] buffer = new byte[1];
            int read = await serverSide.GetStream().ReadAsync(buffer.AsMemory())
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, read);
            Assert.False(source.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }
}
