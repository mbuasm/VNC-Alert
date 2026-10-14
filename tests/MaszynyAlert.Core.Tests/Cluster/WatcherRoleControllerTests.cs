using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class WatcherRoleControllerTests
{
    private static readonly ClusterConfig Cfg = new();

    private static PeerInfo Peer(string host, int priority, PeerRole role, FakeClock clock) =>
        new(host, priority, role, "1.0", clock.UtcNow, 1, null, 0, 0);

    private static RoleDecision TickSeconds(
        WatcherRoleController controller, FakeClock clock, int seconds, Func<IReadOnlyList<PeerInfo>> peers, PeerInfo self)
    {
        RoleDecision decision = controller.Evaluate(peers(), self);
        for (int i = 0; i < seconds; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            decision = controller.Evaluate(peers(), self);
        }

        return decision;
    }

    [Fact]
    public void Evaluate_GdyKomputerJestSam_PrzejmujeRoleDopieroPoDwudziestuSekundach()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);

        RoleDecision po19 = TickSeconds(controller, clock, 19, () => [], self);
        Assert.False(po19.IsWatcher);
        Assert.True(po19.NoWatcher);

        clock.Advance(TimeSpan.FromSeconds(1));
        RoleDecision po20 = controller.Evaluate([], self);

        Assert.True(po20.IsWatcher);
        Assert.Equal("PC-A", po20.WatcherHost);
        Assert.False(po20.NoWatcher);
        Assert.False(po20.SleepDetected);
    }

    [Fact]
    public void Evaluate_GdyPojawiSieLepszyKomputer_UstepujeNatychmiast()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);

        Assert.True(TickSeconds(controller, clock, 25, () => [], self).IsWatcher);

        clock.Advance(TimeSpan.FromSeconds(1));
        RoleDecision decision = controller.Evaluate([Peer("PC-B", 10, PeerRole.Ui, clock)], self);

        Assert.False(decision.IsWatcher);
        Assert.True(decision.NoWatcher, "PC-B nie deklaruje jeszcze roli obserwatora, więc nikt nie obserwuje.");
    }

    [Fact]
    public void Evaluate_GdyLepszyKomputerDeklarujeRoleObserwatora_PokazujeGoJakoObserwatora()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);

        RoleDecision decision = controller.Evaluate([Peer("PC-B", 10, PeerRole.Watcher, clock)], self);

        Assert.False(decision.IsWatcher);
        Assert.Equal("PC-B", decision.WatcherHost);
        Assert.False(decision.NoWatcher);
    }

    [Fact]
    public void Evaluate_GdyLepszyKomputerZniknie_PrzejmujeRoleDopieroPoDwudziestuSekundach()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);
        List<PeerInfo> peers = [Peer("PC-B", 10, PeerRole.Watcher, clock)];

        Assert.False(TickSeconds(controller, clock, 30, () => peers, self).IsWatcher);

        // PC-B przestał wysyłać heartbeat — po PeerTimeoutMs znika z listy żywych komputerów.
        peers.Clear();
        Assert.False(TickSeconds(controller, clock, 19, () => peers, self).IsWatcher);

        clock.Advance(TimeSpan.FromSeconds(1));
        RoleDecision decision = controller.Evaluate(peers, self);

        Assert.True(decision.IsWatcher);
    }

    [Fact]
    public void Evaluate_PoSkokuZegaraMonotonicznego_WykrywaUsypianieIOddajeRole()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);

        Assert.True(TickSeconds(controller, clock, 25, () => [], self).IsWatcher);

        clock.JumpMonotonic(30_000);
        RoleDecision poObudzeniu = controller.Evaluate([], self);

        Assert.True(poObudzeniu.SleepDetected);
        Assert.False(poObudzeniu.IsWatcher);

        // Po wybudzeniu licznik startuje od nowa — rola wraca dopiero po kolejnych 20 s.
        Assert.False(TickSeconds(controller, clock, 19, () => [], self).IsWatcher);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(controller.Evaluate([], self).IsWatcher);
    }

    [Fact]
    public void Evaluate_SkokPonizejProgu_NieJestUznawanyZaUsypianie()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);
        controller.Evaluate([], self);

        clock.JumpMonotonic(Cfg.SleepJumpThresholdMs);

        Assert.False(controller.Evaluate([], self).SleepDetected);
    }

    [Fact]
    public void ForceYield_DzialaTakSamoJakWykrycieUsypiania()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);
        Assert.True(TickSeconds(controller, clock, 25, () => [], self).IsWatcher);

        controller.ForceYield();
        clock.Advance(TimeSpan.FromSeconds(1));
        RoleDecision decision = controller.Evaluate([], self);

        Assert.True(decision.SleepDetected);
        Assert.False(decision.IsWatcher);
    }

    [Fact]
    public void Evaluate_SleepDetected_JestZglaszaneTylkoRaz()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-A");
        PeerInfo self = Peer("PC-A", 100, PeerRole.Ui, clock);
        controller.Evaluate([], self);

        clock.JumpMonotonic(30_000);
        Assert.True(controller.Evaluate([], self).SleepDetected);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(controller.Evaluate([], self).SleepDetected);
    }

    [Fact]
    public void Evaluate_GdyNiktNieJestObserwatorem_ZglaszaBrakObserwatora()
    {
        var clock = new FakeClock();
        var controller = new WatcherRoleController(clock, Cfg, "PC-Z");
        PeerInfo self = Peer("PC-Z", 100, PeerRole.Ui, clock);

        RoleDecision decision = controller.Evaluate([Peer("PC-A", 10, PeerRole.Ui, clock)], self);

        Assert.False(decision.IsWatcher);
        Assert.Null(decision.WatcherHost);
        Assert.True(decision.NoWatcher);
    }

    [Fact]
    public void Konstruktor_ZPustaNazwaKomputera_RzucaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new WatcherRoleController(new FakeClock(), Cfg, "  "));
    }
}
