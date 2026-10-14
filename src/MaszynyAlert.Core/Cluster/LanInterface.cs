using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MaszynyAlert.Core.Cluster;

public sealed record LanInterface(string Name, IPAddress Address, IPAddress Mask)
{
    public IPAddress Broadcast => new(Address.GetAddressBytes().Zip(Mask.GetAddressBytes(), (a, m) => (byte)(a | ~m)).ToArray());
    public static IReadOnlyList<LanInterface> Available() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
            && !new[] { "virtual", "vethernet", "vpn", "vmware", "hyper-v", "wsl", "tap-", "tailscale", "wireguard" }
                .Any(s => (n.Name + " " + n.Description).Contains(s, StringComparison.OrdinalIgnoreCase)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(a.Address) && a.IPv4Mask is not null && !a.IPv4Mask.Equals(IPAddress.Any)
            && a.Address.GetAddressBytes()[0] != 169).Select(a => new LanInterface(n.Name, a.Address, a.IPv4Mask)))
        .ToArray();
    public static LanInterface Select(string? configuredAddress, IReadOnlyList<LanInterface>? available = null)
    {
        var choices = available ?? Available();
        if (configuredAddress is not null)
            return choices.SingleOrDefault(a => a.Address.ToString() == configuredAddress)
                ?? throw new InvalidOperationException("Wybrany interfejs LAN jest niedostępny.");
        return choices.Count == 1 ? choices[0] : throw new InvalidOperationException(
            choices.Count == 0 ? "Brak aktywnego interfejsu LAN." : "Wybierz interfejs LAN do współpracy komputerów.");
    }
}
