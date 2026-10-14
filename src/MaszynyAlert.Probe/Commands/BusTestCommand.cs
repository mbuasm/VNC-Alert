using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Probe.Commands;

/// <summary>
/// <c>probe.exe bus-test [--port P] [--peer host]... [--seconds N]</c> — test widoczności komputerów
/// operatorów po sieci lokalnej (UDP broadcast + HMAC): uruchamiany JEDNOCZEŚNIE na dwóch komputerach,
/// wypisuje każdy nowo usłyszany komputer i podsumowanie po upływie czasu testu.
/// </summary>
public sealed class BusTestCommand : ICommand
{
    private const int DefaultSeconds = 30;
    private static readonly byte[] TestKey = SHA256.HashData(Encoding.UTF8.GetBytes("maszyny-alert-bus-test"));

    public async Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct)
    {
        int port = ParsePort(args);
        int seconds = ParseSeconds(args);
        List<string> peers = ArgsUtil.FindOptionAll(args, "--peer");
        // "--host-name" jest ukryta (do testów automatycznych) — nie pojawia się w probe.exe help.
        string hostName = ArgsUtil.FindOption(args, "--host-name") ?? Environment.MachineName;

        var clusterConfig = new ClusterConfig
        {
            UdpPort = port,
            Priority = 100,
            Peers = peers,
        };

        report.Line($"Ten komputer: {hostName}, port UDP: {port}, czas testu: {seconds} s.");
        if (peers.Count > 0)
        {
            report.Line($"Dodatkowi peerzy (unicast): {string.Join(", ", peers)}");
        }

        report.Line();
        report.Line("Lokalne adresy broadcast (na te adresy program wysyła pakiety):");
        foreach (var addr in UdpPeerBus.LocalBroadcastAddresses())
        {
            report.Line($"  {addr}");
        }

        report.Line();

        var signer = new HmacSigner(TestKey);
        IClock clock = SystemClock.Instance;

        UdpPeerBus bus;
        try
        {
            bus = new UdpPeerBus(clusterConfig, signer, clock, hostName);
        }
        catch (SocketException ex)
        {
            report.Line($"Nie udało się otworzyć portu UDP {port}: {ExplainSocketError(ex)}");
            return 1;
        }

        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        await using var clusterNode = new ClusterNode(clusterConfig, bus, clock, acks, VersionInfo.InformationalVersion);

        var seenHosts = new HashSet<string>(StringComparer.Ordinal);
        var seenOrder = new List<PeerInfo>();
        bus.Rejected += reason => report.Line($"odrzucono pakiet: {reason}");

        await clusterNode.StartAsync(ct).ConfigureAwait(false);

        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        testCts.CancelAfter(TimeSpan.FromSeconds(seconds));

        try
        {
            while (!testCts.IsCancellationRequested)
            {
                await clusterNode.TickAsync(testCts.Token).ConfigureAwait(false);

                foreach (PeerInfo peer in clusterNode.Peers.Where(p => !string.Equals(p.Host, hostName, StringComparison.Ordinal)))
                {
                    if (seenHosts.Add(peer.Host))
                    {
                        report.Line($"widzę komputer {peer.Host} (priorytet {peer.Priority}, rola {DescribeRole(peer.Role)})");
                        seenOrder.Add(peer);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), testCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Koniec testu (upłynął czas albo Ctrl+C) — normalne zakończenie.
        }

        report.Line();
        report.Line($"widziane komputery: {seenOrder.Count}");
        foreach (PeerInfo peer in seenOrder)
        {
            report.Line($"  {peer.Host} — priorytet {peer.Priority}, rola {DescribeRole(peer.Role)}, wersja {peer.Version}");
        }

        report.Line();
        report.Line(seenOrder.Count > 0
            ? "OK – komputery się słyszą."
            : "nie zobaczyłem żadnego innego komputera – uruchom bus-test na drugim komputerze w tym samym czasie albo sprawdź zaporę.");

        return 0;
    }

    private static string DescribeRole(PeerRole role) => role switch
    {
        PeerRole.Watcher => "obserwator",
        PeerRole.Ui => "UI",
        _ => role.ToString(),
    };

    private static int ParsePort(IReadOnlyList<string> args)
    {
        string? text = ArgsUtil.FindOption(args, "--port");
        if (text is null)
        {
            return 47777;
        }

        if (!int.TryParse(text, out int port) || port is < 1 or > 65535)
        {
            throw new ProbeUsageException("Opcja --port musi być liczbą 1-65535.");
        }

        return port;
    }

    private static int ParseSeconds(IReadOnlyList<string> args)
    {
        string? text = ArgsUtil.FindOption(args, "--seconds");
        if (text is null)
        {
            return DefaultSeconds;
        }

        if (!int.TryParse(text, out int seconds) || seconds <= 0)
        {
            throw new ProbeUsageException("Opcja --seconds musi być dodatnią liczbą całkowitą (sekundy).");
        }

        return seconds;
    }

    private static string ExplainSocketError(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse =>
            $"port UDP jest już zajęty (może być używany przez inny program albo już uruchomiony probe.exe) " +
            $"— zamknij go albo uruchom z inną wartością --port. Szczegóły: {ex.Message}",
        SocketError.AccessDenied =>
            $"brak uprawnień do otwarcia portu UDP — spróbuj uruchomić Wiersz polecenia jako administrator, " +
            $"albo sprawdź zaporę Windows (jeśli pojawi się okno „Zezwolić aplikacji…?” — kliknij Zezwól). Szczegóły: {ex.Message}",
        _ =>
            $"{ex.Message} — sprawdź zaporę Windows (kliknij Zezwól, jeśli zapyta) albo spróbuj jako administrator; " +
            "jeśli problem nie ustąpi, napisz mi dokładnie, co widzisz na ekranie.",
    };
}
