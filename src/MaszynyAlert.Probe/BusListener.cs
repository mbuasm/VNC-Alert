using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Probe;

public sealed class BusListener : IAsyncDisposable
{
    private static readonly byte[] TestKey = SHA256.HashData(Encoding.UTF8.GetBytes("maszyny-alert-bus-test"));

    private readonly ClusterNode _node;
    private readonly Report _report;
    private readonly string _hostName;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _seenHosts = new(StringComparer.Ordinal);
    private readonly List<PeerInfo> _seenOrder = [];

    private Task? _loopTask;

    private BusListener(ClusterNode node, Report report, string hostName)
    {
        _node = node;
        _report = report;
        _hostName = hostName;
    }

    /// <summary>
    /// Próbuje otworzyć port UDP <paramref name="port"/> i uruchomić nasłuch w tle. Zwraca
    /// <see langword="null"/> (po wypisaniu ostrzeżenia do raportu, BEZ rzucania wyjątku), gdy port jest
    /// zajęty — wołający (<c>auto</c>) ma kontynuować bez nasłuchu, nie przerywać całego przebiegu.
    /// </summary>
    public static async Task<BusListener?> TryStartAsync(int port, Report report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        string hostName = Environment.MachineName;
        var clusterConfig = new ClusterConfig { UdpPort = port, Priority = 100 };
        var signer = new HmacSigner(TestKey);
        IClock clock = SystemClock.Instance;

        UdpPeerBus bus;
        try
        {
            bus = new UdpPeerBus(clusterConfig, signer, clock, hostName);
        }
        catch (SocketException ex)
        {
            report.Line(
                $"port UDP {port} zajęty – nasłuch testu sieci między komputerami pominięty ({ex.Message}). " +
                "Reszta programu działa normalnie; sieć możesz sprawdzić osobno przez \"probe.exe bus-test\".");
            return null;
        }

        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var node = new ClusterNode(clusterConfig, bus, clock, acks, VersionInfo.InformationalVersion);
        var listener = new BusListener(node, report, hostName);

        bus.Rejected += reason => report.FileOnly($"bus: odrzucono pakiet: {reason}");

        await node.StartAsync(ct).ConfigureAwait(false);
        listener._loopTask = listener.RunLoopAsync(listener._stopCts.Token);

        report.Line(
            $"Nasłuch testu sieci między komputerami: włączony (port UDP {port}). Jeśli w ciągu zmiany kto inny " +
            "uruchomi \"probe.exe auto\" (albo \"bus-test\") na drugim komputerze, pojawi się tu linia \"widzę komputer ...\".");

        return listener;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _node.TickAsync(ct).ConfigureAwait(false);

                List<PeerInfo> newPeers = [];
                lock (_lock)
                {
                    foreach (PeerInfo peer in _node.Peers.Where(p => !string.Equals(p.Host, _hostName, StringComparison.Ordinal)))
                    {
                        if (_seenHosts.Add(peer.Host))
                        {
                            _seenOrder.Add(peer);
                            newPeers.Add(peer);
                        }
                    }
                }

                foreach (PeerInfo peer in newPeers)
                {
                    _report.Line($"widzę komputer {peer.Host} (priorytet {peer.Priority})");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normalne zatrzymanie — patrz StopAsync.
        }
    }

    /// <summary>Zatrzymuje nasłuch (bez wypisywania podsumowania — patrz <see cref="PrintSummary"/>, wołaj ją PO tym).</summary>
    public async Task StopAsync()
    {
        _stopCts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normalne
            }
        }

        await _node.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Wypisuje "widziane komputery: N (…)" do raportu — wołaj po <see cref="StopAsync"/> (na koniec przebiegu).</summary>
    public void PrintSummary(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<PeerInfo> seen;
        lock (_lock)
        {
            seen = [.. _seenOrder];
        }

        report.Line();
        report.Line(seen.Count > 0
            ? $"widziane komputery: {seen.Count} ({string.Join(", ", seen.Select(p => p.Host))})"
            : "widziane komputery: 0 (nikt inny nie uruchomił w tym czasie \"auto\" ani \"bus-test\" — to nie musi być błąd, jeśli nikt tego nie robił).");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
