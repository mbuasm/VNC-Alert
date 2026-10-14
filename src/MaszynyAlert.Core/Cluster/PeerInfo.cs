namespace MaszynyAlert.Core.Cluster;

/// <summary>Znany stan jednego komputera klastra, wg ostatniego odebranego (lub własnego) <see cref="HeartbeatMessage"/>.</summary>
/// <param name="InstanceId">
/// Ostatni znany <see cref="PeerMessage.InstanceId"/> tego komputera — patrz <see cref="PeerTable.Observe"/>.
/// Domyślnie puste (zgodność wsteczna — istniejące wywołania konstruktora bez tego argumentu nadal działają).
/// </param>
public sealed record PeerInfo(
    string Host,
    int Priority,
    PeerRole Role,
    string Version,
    DateTimeOffset LastSeen,
    long LastSeq,
    string? BelievedWatcher,
    int MachinesConnected,
    int MachinesTotal,
    string InstanceId = "")
{
    public WatcherReadiness? Readiness { get; init; }

    public string? InstructionsHash { get; init; }

    public string? SharedConfigHash { get; init; }

    public DateTimeOffset? DowntimeSyncedAt { get; init; }

    public GroupCompatibility Compatibility { get; init; } = GroupCompatibility.Compatible;
    /// <summary>Czy ten komputer jest uznawany za "żywy" — czy ostatni heartbeat mieści się w oknie <paramref name="timeout"/>.</summary>
    public bool IsAlive(DateTimeOffset now, TimeSpan timeout) => now - LastSeen < timeout;
}
