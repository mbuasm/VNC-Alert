using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Dokąd sięga skasowanie powiadomienia o postoju ("✕ Skasuj") i potwierdzenie błędu ("Idę sprawdzić")
/// wykonane na TYM komputerze — patrz <see cref="DismissScope"/>.
/// </summary>
public enum DismissReach
{
    /// <summary>
    /// Tylko ten komputer: praca bez grupy (<c>cluster.enabled:false</c>) albo transport grupy nie działa
    /// (nieudany start UDP — <c>ClusterRuntime.NetworkActive</c> jest wtedy fałszywe). Potwierdzenie nie ma
    /// dokąd pójść; pozostałe komputery liczą własne alerty i pokazują je dalej.
    /// </summary>
    LocalOnly,

    /// <summary>
    /// Grupa działa, ale w tej chwili nie widać ŻADNEGO innego komputera (żaden nie przysłał heartbeatu w
    /// oknie <c>cluster.peerTimeoutMs</c>). Potwierdzenie jest rozgłaszane i dojdzie do komputerów, które
    /// wrócą — ale jeśli one są włączone, to znaczy, że pakiety nie chodzą (zapora, inna sieć, inny klucz).
    /// </summary>
    GroupAlone,

    /// <summary>Grupa działa i widać inne komputery — potwierdzenie gasi powiadomienie na wszystkich naraz.</summary>
    Group,
}

/// <summary>
/// Zasięg potwierdzenia alertu na tym komputerze i gotowe zdania dla operatora.
/// Program pokazuje więc zasięg wprost przy przycisku, zamiast zostawiać operatora z domysłami.
/// </summary>
public static class DismissScope
{
    /// <summary>
    /// Ile INNYCH komputerów grupy jest teraz widocznych: zgodnych i z heartbeatem w oknie
    /// <paramref name="peerTimeout"/> (<c>cluster.peerTimeoutMs</c>). Lista <paramref name="peers"/> zawiera
    /// także wiersz tego komputera (patrz <c>IClusterNode.Peers</c>), dlatego jest tu odsiewany po nazwie.
    /// </summary>
    public static int LiveOtherComputers(IReadOnlyList<PeerInfo> peers, string localHost,
        DateTimeOffset now, TimeSpan peerTimeout)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(localHost);

        return peers.Count(p => p is not null
            && !string.Equals(p.Host, localHost, StringComparison.Ordinal)
            && p.Compatibility == GroupCompatibility.Compatible
            && p.IsAlive(now, peerTimeout));
    }

    /// <summary>
    /// Zasięg potwierdzenia: <paramref name="groupTransportActive"/> to „grupa jest włączona I transport
    /// UDP faktycznie działa” (<c>ClusterRuntime.Configuration.Enabled &amp;&amp; NetworkActive</c>).
    /// </summary>
    public static DismissReach For(bool groupTransportActive, int liveOtherComputers) =>
        !groupTransportActive ? DismissReach.LocalOnly
            : liveOtherComputers > 0 ? DismissReach.Group : DismissReach.GroupAlone;

    /// <summary>
    /// Jedno zdanie pod listą postojów (pop-up, Podgląd, okno statusu) — mówi wprost, czy „✕ Skasuj”
    /// zgaśnie także na pozostałych komputerach.
    /// </summary>
    public static string Note(DismissReach reach, int liveOtherComputers) => reach switch
    {
        DismissReach.Group => liveOtherComputers == 1
            ? "Skasowanie gasi powiadomienie także na drugim komputerze grupy."
            : $"Skasowanie gasi powiadomienie także na pozostałych komputerach grupy (widoczne: {liveOtherComputers}).",
        DismissReach.GroupAlone =>
            "Nie widać teraz innego komputera grupy — jeśli pozostałe są włączone, sprawdź połączenie "
            + "(Panel administracyjny → Praca na kilku komputerach).",
        _ => "Ten komputer pracuje bez grupy — skasowanie działa tylko tutaj.",
    };

    /// <summary>Czy zdanie z <see cref="Note"/> jest ostrzeżeniem (potwierdzenie może nie dojść do pozostałych).</summary>
    public static bool IsWarning(DismissReach reach) => reach != DismissReach.Group;

    /// <summary>Podpowiedź przy samym przycisku (tooltip) — ta sama treść w skrócie.</summary>
    public static string ButtonHint(DismissReach reach) => reach switch
    {
        DismissReach.Group => "Kasuje powiadomienie na tym i na pozostałych komputerach grupy.",
        DismissReach.GroupAlone => "Kasuje powiadomienie tutaj i rozgłasza je do grupy — teraz nie widać innego komputera.",
        _ => "Kasuje powiadomienie tylko na tym komputerze (praca bez grupy).",
    };
}
