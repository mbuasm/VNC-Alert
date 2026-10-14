using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.App.ViewModels;

public sealed class PeerRowViewModel : ObservableObject
{
    public PeerRowViewModel(PeerInfo peer, DateTimeOffset now, TimeSpan aliveTimeout, bool isThisComputer,
        string? localVersion = null)
    {
        ArgumentNullException.ThrowIfNull(peer);

        Host = peer.Host + (isThisComputer ? " (ten komputer)" : string.Empty);
        IsAliveText = peer.IsAlive(now, aliveTimeout) ? "Tak" : "Nie";
        RoleText = peer.Role == PeerRole.Watcher ? "Obserwator" : "UI (tylko alerty)";
        Priority = peer.Priority;
        Version = ShortVersion(peer.Version);
        LastSeenText = peer.LastSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        MachinesText = $"{peer.MachinesConnected}/{peer.MachinesTotal}";
        IsIncompatible = !isThisComputer && !GroupPeerStatus.Cooperates(peer);
        CooperationText = GroupPeerStatus.Describe(peer, localVersion, isThisComputer);
    }

    private string _isAliveText = "";
    private string _roleText = "";
    private int _priority;
    private string _version = "";
    private string _lastSeenText = "";
    private string _machinesText = "";
    private string _cooperationText = "";
    private bool _isIncompatible;

    public void CopyFrom(PeerRowViewModel fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        IsAliveText = fresh.IsAliveText;
        RoleText = fresh.RoleText;
        Priority = fresh.Priority;
        Version = fresh.Version;
        LastSeenText = fresh.LastSeenText;
        MachinesText = fresh.MachinesText;
        CooperationText = fresh.CooperationText;
        IsIncompatible = fresh.IsIncompatible;
    }

    public string Host { get; }
    public string IsAliveText { get => _isAliveText; private set => SetProperty(ref _isAliveText, value); }
    public string RoleText { get => _roleText; private set => SetProperty(ref _roleText, value); }
    public int Priority { get => _priority; private set => SetProperty(ref _priority, value); }
    public string Version { get => _version; private set => SetProperty(ref _version, value); }
    public string LastSeenText { get => _lastSeenText; private set => SetProperty(ref _lastSeenText, value); }
    public string MachinesText { get => _machinesText; private set => SetProperty(ref _machinesText, value); }

    /// <summary>Czy komputer współpracuje z tym, a jeśli nie — dlaczego i co zrobić (kolumna „Współpraca”).</summary>
    public string CooperationText { get => _cooperationText; private set => SetProperty(ref _cooperationText, value); }

    /// <summary>Komputer jest w grupie, ale nie współpracuje z tym komputerem (wiersz wyróżniony w tabeli).</summary>
    public bool IsIncompatible { get => _isIncompatible; private set => SetProperty(ref _isIncompatible, value); }

    /// <summary>Numer wersji bez metadanych buildu (część przed „+”).</summary>
    public static string ShortVersion(string? version) => GroupPeerStatus.ShortVersion(version);
}
