using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class PeerTableTests
{
    private static HeartbeatMessage Heartbeat(
        string sender, long seq, DateTimeOffset sentAt, int priority = 100, PeerRole role = PeerRole.Ui) => new()
    {
        Sender = sender,
        Version = "1.0",
        Seq = seq,
        SentAt = sentAt,
        Priority = priority,
        Role = role,
    };

    [Fact]
    public void Observe_NowegoKomputera_ZwracaPrawdeIDodajeGoDoTabeli()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());

        Assert.True(table.Observe(Heartbeat("PC-A", 1, clock.UtcNow, priority: 42, role: PeerRole.Watcher)));

        PeerInfo peer = Assert.Single(table.All);
        Assert.Equal("PC-A", peer.Host);
        Assert.Equal(42, peer.Priority);
        Assert.Equal(PeerRole.Watcher, peer.Role);
        Assert.Equal(1, peer.LastSeq);
        Assert.Equal(clock.UtcNow, peer.LastSeen);
    }

    [Fact]
    public void Observe_PowtorzonegoNumeruSekwencyjnego_ZwracaFalsz()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        table.Observe(Heartbeat("PC-A", 5, clock.UtcNow));

        Assert.False(table.Observe(Heartbeat("PC-A", 5, clock.UtcNow)));
        Assert.False(table.Observe(Heartbeat("PC-A", 4, clock.UtcNow)));
        Assert.True(table.Observe(Heartbeat("PC-A", 6, clock.UtcNow)));
    }

    [Fact]
    public void Observe_GdyNumerSekwencyjnySpadlOPonad1000_UznajeToZaRestartKomputera()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        table.Observe(Heartbeat("PC-A", 5000, clock.UtcNow));

        Assert.False(table.Observe(Heartbeat("PC-A", 4000, clock.UtcNow)));
        Assert.True(table.Observe(Heartbeat("PC-A", 1, clock.UtcNow)));
        Assert.Equal(1, Assert.Single(table.All).LastSeq);
    }

    [Fact]
    public void Observe_PoUplywieOknaAntyPowtorzeniowego_PrzyjmujeStarszyNumer()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        table.Observe(Heartbeat("PC-A", 500, clock.UtcNow));

        clock.Advance(TimeSpan.FromSeconds(601));

        Assert.True(table.Observe(Heartbeat("PC-A", 2, clock.UtcNow)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(61, 5000)]
    [InlineData(301, 5000)]
    [InlineData(600, 5000)]
    public void Replay_WithinClockTolerance_DoesNotRevivePeer(int delaySeconds, long lastSeq)
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        var hb = Heartbeat("PC-A", lastSeq, clock.UtcNow) with { InstanceId = "run-a" };
        Assert.True(table.Observe(hb));
        clock.Advance(TimeSpan.FromSeconds(delaySeconds));
        Assert.False(table.Observe(hb with { Seq = 1 }));
        Assert.Equal(hb.SentAt, Assert.Single(table.All).LastSeen);
    }

    [Fact]
    public void Replay_PreviousInstance_DoesNotUndoRestart()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        var hb = Heartbeat("PC-A", 10, clock.UtcNow) with { InstanceId = "old" };
        Assert.True(table.Observe(hb));
        Assert.True(table.Observe(hb with { InstanceId = "new", Seq = 1 }));
        Assert.False(table.Observe(hb with { Seq = 11 }));
        Assert.Equal("new", Assert.Single(table.All).InstanceId);
    }

    [Fact]
    public void Alive_PoUplywieCzasuOczekiwania_NieZwracaJuzKomputera()
    {
        var clock = new FakeClock();
        var cfg = new ClusterConfig();
        var table = new PeerTable(clock, cfg);
        table.Observe(Heartbeat("PC-A", 1, clock.UtcNow));

        clock.Advance(TimeSpan.FromMilliseconds(cfg.PeerTimeoutMs - 1));
        Assert.Single(table.Alive());

        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Empty(table.Alive());
        Assert.Single(table.All);
    }

    [Fact]
    public void Forget_UsuwaKomputerZTabeli()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        table.Observe(Heartbeat("PC-A", 1, clock.UtcNow));

        table.Forget("PC-A");

        Assert.Empty(table.All);
    }

    [Fact]
    public void Prune_UsuwaKomputeryNiewidzianeOdDziesieciuMinut()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        table.Observe(Heartbeat("PC-A", 1, clock.UtcNow));

        clock.Advance(TimeSpan.FromMinutes(5));
        table.Prune();
        Assert.Single(table.All);

        clock.Advance(TimeSpan.FromMinutes(6));
        table.Prune();
        Assert.Empty(table.All);
    }

    [Fact]
    public void IsAlive_PeerInfo_PorownujeCzasZTolerancja()
    {
        var peer = new PeerInfo("PC-A", 100, PeerRole.Ui, "1.0", FakeClock.DefaultStart, 1, null, 0, 0);

        Assert.True(peer.IsAlive(FakeClock.DefaultStart.AddSeconds(10), TimeSpan.FromSeconds(15)));
        Assert.False(peer.IsAlive(FakeClock.DefaultStart.AddSeconds(20), TimeSpan.FromSeconds(15)));
    }
}
