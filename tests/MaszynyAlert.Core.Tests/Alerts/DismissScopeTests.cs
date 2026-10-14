using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Core.Tests.Alerts;

/// <summary>
/// Zasięg „✕ Skasuj” pokazywany operatorowi (<see cref="DismissScope"/>). Zdanie pod listą postojów musi
/// odróżnić trzy sytuacje: praca bez grupy (potwierdzenie zostaje tutaj), grupa bez widocznego partnera
/// (coś nie chodzi albo pozostałe komputery są wyłączone) i grupa z widocznymi komputerami.
/// </summary>
public sealed class DismissScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static PeerInfo Peer(string host, DateTimeOffset lastSeen,
        GroupCompatibility compatibility = GroupCompatibility.Compatible) =>
        new(host, 100, PeerRole.Ui, "0.2.0", lastSeen, 1, null, 0, 0) { Compatibility = compatibility };

    [Fact]
    public void BezGrupy_PotwierdzenieZostajeNaTymKomputerze()
    {
        Assert.Equal(DismissReach.LocalOnly, DismissScope.For(groupTransportActive: false, liveOtherComputers: 0));
        // Nawet gdyby w tabeli został wiersz z poprzedniej konfiguracji — bez transportu nie ma dokąd wysłać.
        Assert.Equal(DismissReach.LocalOnly, DismissScope.For(groupTransportActive: false, liveOtherComputers: 3));
        Assert.Contains("tylko tutaj", DismissScope.Note(DismissReach.LocalOnly, 0));
        Assert.True(DismissScope.IsWarning(DismissReach.LocalOnly));
    }

    [Fact]
    public void GrupaBezWidocznegoKomputera_OstrzegaIWskazujePanel()
    {
        Assert.Equal(DismissReach.GroupAlone, DismissScope.For(groupTransportActive: true, liveOtherComputers: 0));
        string note = DismissScope.Note(DismissReach.GroupAlone, 0);
        Assert.Contains("Nie widać", note);
        Assert.Contains("Praca na kilku komputerach", note);
        Assert.True(DismissScope.IsWarning(DismissReach.GroupAlone));
    }

    [Fact]
    public void GrupaZWidocznymiKomputerami_MowiZeGasiNaWszystkich()
    {
        Assert.Equal(DismissReach.Group, DismissScope.For(groupTransportActive: true, liveOtherComputers: 1));
        Assert.False(DismissScope.IsWarning(DismissReach.Group));
        Assert.Contains("na drugim komputerze", DismissScope.Note(DismissReach.Group, 1));
        Assert.Contains("widoczne: 4", DismissScope.Note(DismissReach.Group, 4));
    }

    [Fact]
    public void PodpowiedzPrzycisku_RozniSieDlaKazdegoZasiegu()
    {
        string[] hints =
        [
            DismissScope.ButtonHint(DismissReach.LocalOnly),
            DismissScope.ButtonHint(DismissReach.GroupAlone),
            DismissScope.ButtonHint(DismissReach.Group),
        ];

        Assert.Equal(3, hints.Distinct().Count());
        Assert.All(hints, h => Assert.StartsWith("Kasuje powiadomienie", h));
    }

    [Fact]
    public void LiczenieKomputerow_PomijaWlasnyWierszMartwychINiezgodnych()
    {
        PeerInfo[] peers =
        [
            Peer("PC-TEN", Now),                                   // własny wiersz
            Peer("PC-DRUGI", Now - TimeSpan.FromSeconds(2)),        // żywy partner
            Peer("PC-SPIACY", Now - TimeSpan.FromMinutes(5)),       // dawno bez heartbeatu
            Peer("PC-INNY-PROFIL", Now, GroupCompatibility.OtherProfile),
        ];

        Assert.Equal(1, DismissScope.LiveOtherComputers(peers, "PC-TEN", Now, Timeout));
        // Sam, bez partnerów: tylko własny wiersz.
        Assert.Equal(0, DismissScope.LiveOtherComputers([Peer("PC-TEN", Now)], "PC-TEN", Now, Timeout));
    }
}
