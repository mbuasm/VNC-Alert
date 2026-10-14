namespace MaszynyAlert.Core.Cluster;

public static class GroupPeerStatus
{
    /// <summary>Numer wersji bez metadanych buildu (część przed „+”).</summary>
    public static string ShortVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? string.Empty : version.Split('+', 2)[0].Trim();

    public static bool Cooperates(PeerInfo peer) => peer.Compatibility == GroupCompatibility.Compatible;

    private static bool SameVersion(PeerInfo peer, string? localVersion) =>
        string.Equals(ShortVersion(peer.Version), ShortVersion(localVersion), StringComparison.OrdinalIgnoreCase);

    private static bool OtherVersion(PeerInfo peer, string? localVersion) =>
        !string.IsNullOrWhiteSpace(ShortVersion(localVersion)) && !SameVersion(peer, localVersion);

    /// <summary>Numer wersji do tekstu; komputer, który wersji nie podał — „nieznana” zamiast pustych nawiasów.</summary>
    private static string VersionLabel(PeerInfo peer) =>
        ShortVersion(peer.Version) is { Length: > 0 } version ? version : "nieznana";

    public static string? IncompatibilityReason(PeerInfo peer, string? localVersion) => peer.Compatibility switch
    {
        GroupCompatibility.Compatible => null,
        // Dziś martwe: GroupClusterNode.Receive odrzuca pakiet innej grupy / innego klucza przed tabelą komputerów.
        GroupCompatibility.OtherGroup => "inna grupa",
        GroupCompatibility.OtherKey => "inny klucz grupy",
        GroupCompatibility.OtherProtocol when OtherVersion(peer, localVersion) => $"inna wersja programu ({VersionLabel(peer)})",
        GroupCompatibility.OtherProtocol => "inna wersja protokołu grupy",
        _ when OtherVersion(peer, localVersion) => $"inne ustawienia grupy albo inna wersja programu ({VersionLabel(peer)})",
        _ => "inne ustawienia grupy",
    };

    /// <summary>Co zrobić, żeby komputer znów współpracował (mała litera — część zdania).</summary>
    public static string Remedy(PeerInfo peer, string? localVersion) => peer.Compatibility switch
    {
        GroupCompatibility.OtherGroup or GroupCompatibility.OtherKey => "dołącz go ponownie do grupy z aktualnego pliku grupy",
        GroupCompatibility.OtherProtocol => "zainstaluj tę samą wersję",
        _ when OtherVersion(peer, localVersion) =>
            "zainstaluj tę samą wersję, a jeśli to nie pomoże — wczytaj na obu komputerach ten sam, aktualny plik grupy",
        _ => "wczytaj na obu komputerach ten sam, aktualny plik grupy",
    };

    /// <summary>Stan współpracy do kolumny „Współpraca” w tabelach komputerów.</summary>
    public static string Describe(PeerInfo peer, string? localVersion, bool isThisComputer = false)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (isThisComputer) return "ten komputer";
        if (IncompatibilityReason(peer, localVersion) is { } reason)
            return $"{reason} — nie współpracuje z tym komputerem; {Remedy(peer, localVersion)}";
        return OtherVersion(peer, localVersion)
            ? $"współpracuje; inna wersja programu ({VersionLabel(peer)}) — zainstaluj tę samą wersję na wszystkich komputerach"
            : "współpracuje";
    }

    private static string Advice(IReadOnlyCollection<PeerInfo> incompatible, string? localVersion)
    {
        if (incompatible.Count == 0) return string.Empty;
        if (incompatible.All(p => p.Compatibility is GroupCompatibility.OtherGroup or GroupCompatibility.OtherKey))
            return "Dołącz te komputery ponownie do grupy z aktualnego pliku grupy.";
        bool version = incompatible.Any(p => p.Compatibility == GroupCompatibility.OtherProtocol || OtherVersion(p, localVersion));
        bool settings = incompatible.Any(p => p.Compatibility == GroupCompatibility.OtherProfile);
        return (version, settings) switch
        {
            (true, true) => "Zainstaluj na wszystkich komputerach grupy tę samą wersję programu; jeśli to nie pomoże, wczytaj na nich "
                + "ten sam, aktualny plik grupy.",
            (true, false) => "Zainstaluj na wszystkich komputerach grupy tę samą wersję programu.",
            _ => "Wczytaj na wszystkich komputerach grupy ten sam, aktualny plik grupy.",
        };
    }

    public static GroupPeerSummary Summarize(IReadOnlyList<PeerInfo> peers, string? localVersion, DateTimeOffset now,
        TimeSpan aliveTimeout)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var alive = peers.Where(p => p.IsAlive(now, aliveTimeout))
            .OrderBy(p => p.Host, StringComparer.OrdinalIgnoreCase).ToArray();
        var incompatible = alive.Where(p => !Cooperates(p)).ToArray();
        return new(
            incompatible.Select(p => $"{p.Host} — {IncompatibilityReason(p, localVersion)}").ToArray(),
            alive.Where(p => p.Role == PeerRole.Watcher).Select(p => p.Host).ToArray(),
            Advice(incompatible, localVersion));
    }
}

public sealed record GroupPeerSummary(IReadOnlyList<string> Incompatible, IReadOnlyList<string> Watchers, string Advice = "")
{
    /// <summary>Ostrzeżenie dla operatora (pusty napis = wszystko w porządku).</summary>
    public string Warning
    {
        get
        {
            var parts = new List<string>();
            if (Incompatible.Count > 0)
                parts.Add(($"Nie współpracuje z tym komputerem: {string.Join("; ", Incompatible)}. Zamknięcie alertu i raport "
                    + $"postojów nie przechodzą między nimi. {Advice}").TrimEnd());
            if (Watchers.Count > 1)
                parts.Add($"Maszyny obserwuje naraz kilka komputerów: {string.Join(", ", Watchers)}.");
            return string.Join(" ", parts);
        }
    }
}

public sealed class GroupPeerWarnings
{
    public static readonly TimeSpan Throttle = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTimeOffset> _lastLogged = new(StringComparer.Ordinal);

    private readonly object _gate = new();

    /// <summary>
    /// Ostrzeżenia należne teraz; wołać z dowolną częstotliwością (zwykle co sekundę). Wpis o drugim obserwatorze
    /// tylko dla obserwatorów z <paramref name="confirmedWatchers"/> — listy potwierdzonej przez
    /// <see cref="WatcherOverlapTracker"/> (60 s), ten sam próg co w Panelu.
    /// </summary>
    public IReadOnlyList<string> Collect(IReadOnlyList<PeerInfo> peers, string localHost, string? localVersion,
        DateTimeOffset now, TimeSpan aliveTimeout, IReadOnlyCollection<string> confirmedWatchers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(confirmedWatchers);
        lock (_gate) return CollectLocked(peers, localHost, localVersion, now, aliveTimeout, confirmedWatchers);
    }

    private List<string> CollectLocked(IReadOnlyList<PeerInfo> peers, string localHost, string? localVersion,
        DateTimeOffset now, TimeSpan aliveTimeout, IReadOnlyCollection<string> confirmedWatchers)
    {
        Forget(now);
        var due = new List<string>();
        var alive = peers.Where(p => p.IsAlive(now, aliveTimeout)
            && !string.Equals(p.Host, localHost, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var peer in alive)
        {
            if (GroupPeerStatus.IncompatibilityReason(peer, localVersion) is not { } reason) continue;
            if (Due($"niezgodny|{peer.Host}|{peer.Compatibility}|{GroupPeerStatus.ShortVersion(peer.Version)}", now))
                due.Add($"Grupa: komputer {peer.Host} (wersja {GroupPeerStatus.ShortVersion(peer.Version)}) nie współpracuje "
                    + $"z tym komputerem — {reason}. Każdy z nich może wtedy sam obserwować maszyny, zamknięcie alertu i "
                    + $"raport postojów nie przechodzą między nimi. Zalecenie: {GroupPeerStatus.Remedy(peer, localVersion)}.");
        }
        bool overlap = confirmedWatchers.Count > 1
            && confirmedWatchers.Contains(localHost, StringComparer.OrdinalIgnoreCase);
        foreach (var other in alive.Where(p => p.Role == PeerRole.Watcher))
        {
            if (!overlap) break;
            if (!confirmedWatchers.Contains(other.Host, StringComparer.OrdinalIgnoreCase)) continue;
            if (Due($"obserwator|{other.Host}|{other.Compatibility}", now))
                due.Add($"Grupa: widoczny drugi obserwator — {other.Host} też obserwuje maszyny od ponad minuty "
                    + (GroupPeerStatus.Cooperates(other)
                        ? "(zgodny; zwykle ustępuje sam — jeśli to trwa, sprawdź w Panelu kolumnę „Współpraca”)."
                        : $"({GroupPeerStatus.IncompatibilityReason(other, localVersion)})."));
        }
        return due;
    }

    /// <summary>Liczba zapamiętanych wpisów (test sprzątania).</summary>
    internal int TrackedCount { get { lock (_gate) return _lastLogged.Count; } }

    /// <summary>Wpisy starsze niż <see cref="Throttle"/> nic już nie dławią — usuwamy je (klucz zawiera host i wersję).</summary>
    private void Forget(DateTimeOffset now)
    {
        foreach (var stale in _lastLogged.Where(p => now - p.Value >= Throttle).Select(p => p.Key).ToArray())
            _lastLogged.Remove(stale);
    }

    private bool Due(string key, DateTimeOffset now)
    {
        if (_lastLogged.TryGetValue(key, out var last) && now - last < Throttle) return false;
        _lastLogged[key] = now;
        return true;
    }
}
