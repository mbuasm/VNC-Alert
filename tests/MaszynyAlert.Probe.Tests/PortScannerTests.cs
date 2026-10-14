using System.Net;
using System.Net.Sockets;
using MaszynyAlert.Probe.Diagnoza;

namespace MaszynyAlert.Probe.Tests;

/// <summary>Sieć z poziomu <c>diagnoza</c> (<see cref="PortScanner"/>) na lokalnym nasłuchu TCP.</summary>
public sealed class PortScannerTests
{
    [Fact]
    public async Task ProbeAsync_OtwartyPort_JestOtwartyZCzasem()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TcpProbeResult r = await PortScanner.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(2), CancellationToken.None);

            Assert.True(r.Open, r.Error);
            Assert.Null(r.Error);
            Assert.True(r.ElapsedMs < 2000);
            Assert.Contains("otwarty", r.Describe(), StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ProbeAsync_ZamknietyPort_NieRzucaIOpisujePowod()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // port na pewno zamknięty

        TcpProbeResult r = await PortScanner.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.False(r.Open);
        Assert.NotNull(r.Error);
        Assert.Contains("zamknięty", r.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_ZnajdujeNasluchWsrodPortow()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int open = ((IPEndPoint)listener.LocalEndpoint).Port;
            var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start();
            int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();

            IReadOnlyList<TcpProbeResult> results = await PortScanner.ScanAsync("127.0.0.1", [closedPort, open], TimeSpan.FromSeconds(2), CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.Port == open && r.Open);
            Assert.Contains(results, r => r.Port == closedPort && !r.Open);
            Assert.Contains($"otwarte porty: {open}", PortScanner.DescribeScan(results), StringComparison.Ordinal);
            Assert.Contains("żaden z 1", PortScanner.DescribeScan(results.Where(r => !r.Open).ToList()), StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void DefaultScanPorts_To5900Do5909_5800_9100_9101()
    {
        Assert.Equal([5900, 5901, 5902, 5903, 5904, 5905, 5906, 5907, 5908, 5909, 5800, 9100, 9101], PortScanner.DefaultScanPorts);
    }
}
