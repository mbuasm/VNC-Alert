namespace MaszynyAlert.Core.Cluster;

public static class Election
{
    /// <summary>
    /// Wybiera kandydata na obserwatora spośród podanych "żywych" komputerów: najniższy
    /// <see cref="PeerInfo.Priority"/>, a przy remisie — alfabetycznie (<see cref="StringComparer.Ordinal"/>)
    /// po <see cref="PeerInfo.Host"/>. Zwraca <see langword="null"/>, gdy lista jest pusta.
    /// </summary>
    public static string? ChooseWatcher(IEnumerable<PeerInfo> alivePeersIncludingSelf)
    {
        ArgumentNullException.ThrowIfNull(alivePeersIncludingSelf);

        PeerInfo? best = null;
        foreach (PeerInfo peer in alivePeersIncludingSelf)
        {
            if (best is null
                || peer.Priority < best.Priority
                || (peer.Priority == best.Priority && string.CompareOrdinal(peer.Host, best.Host) < 0))
            {
                best = peer;
            }
        }

        return best?.Host;
    }
}
