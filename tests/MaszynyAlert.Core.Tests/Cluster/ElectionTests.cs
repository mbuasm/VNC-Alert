using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class ElectionTests
{
    private static PeerInfo Peer(string host, int priority, PeerRole role = PeerRole.Ui) =>
        new(host, priority, role, "1.0", FakeClock.DefaultStart, 1, null, 0, 0);

    [Fact]
    public void ChooseWatcher_WybieraKomputerONajnizszymPriorytecie()
    {
        string? watcher = Election.ChooseWatcher([Peer("PC-A", 100), Peer("PC-B", 50), Peer("PC-C", 50)]);

        Assert.Equal("PC-B", watcher);
    }

    [Fact]
    public void ChooseWatcher_PrzyRemisiePriorytetow_WybieraAlfabetycznieNajwczesniejszaNazwe()
    {
        string? watcher = Election.ChooseWatcher([Peer("PC-Z", 10), Peer("PC-A", 10), Peer("PC-M", 10)]);

        Assert.Equal("PC-A", watcher);
    }

    [Fact]
    public void ChooseWatcher_DlaPustejListy_ZwracaNull()
    {
        Assert.Null(Election.ChooseWatcher([]));
    }

    [Fact]
    public void ChooseWatcher_DlaJednegoKomputera_ZwracaJegoNazwe()
    {
        Assert.Equal("PC-JEDYNY", Election.ChooseWatcher([Peer("PC-JEDYNY", 100)]));
    }

    [Fact]
    public void ChooseWatcher_NieZalezyOdKolejnosciWejscia()
    {
        PeerInfo[] peers = [Peer("PC-A", 100), Peer("PC-B", 50), Peer("PC-C", 50)];

        Assert.Equal(Election.ChooseWatcher(peers), Election.ChooseWatcher(peers.Reverse()));
    }

    [Fact]
    public void ChooseWatcher_PorownujeNazwyPorzadkiemOrdinal()
    {
        // 'B' (0x42) jest mniejsze niż 'a' (0x61) w porównaniu porządkowym.
        Assert.Equal("PC-B", Election.ChooseWatcher([Peer("PC-a", 10), Peer("PC-B", 10)]));
    }
}
