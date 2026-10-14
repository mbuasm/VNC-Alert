namespace MaszynyAlert.Core.Cluster;

public enum PeerRole
{
    /// <summary>Komputer tylko wyświetla alerty odebrane z sieci — sam nie łączy się z maszynami VNC.</summary>
    Ui,

    /// <summary>Komputer jest obserwatorem (watcherem) — utrzymuje połączenia VNC i rozgłasza <see cref="StateMessage"/>.</summary>
    Watcher,
}
