using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.Core.Cluster;

public interface IClusterNode : IAsyncDisposable
{
    bool IsWatcher { get; }
    string? WatcherHost { get; }
    bool NoWatcher { get; }
    IReadOnlyList<PeerInfo> Peers { get; }
    IReadOnlyList<Alert> MergedAlerts { get; }
    IReadOnlyList<MachineStatusSnapshot> RemoteMachines { get; }
    event Action<bool>? RoleChanged;
    event Action? PeersChanged;
    event Action<IReadOnlyList<Alert>>? AlertsChanged;
    event Action<AckRecord>? AckReceived;
    event Action? SleepDetected;
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task TickAsync(CancellationToken ct);
    void SetLocalState(IReadOnlyList<Alert> alerts, IReadOnlyList<MachineStatusSnapshot> machines,
        IReadOnlyList<AlertKey>? recentlyClosed = null);
    Task SendAckAsync(AlertKey key, string user, CancellationToken ct);
    void ForceYield();
}
