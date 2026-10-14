using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class ClusterNodeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleComputer_ResumeRequestsReconnect_WithoutChangingRole(bool clockJump)
    {
        var clock = new FakeClock();
        var cfg = new ClusterConfig { Enabled = false };
        var hub = new InMemoryPeerHub(new HmacSigner(HmacSigner.GenerateKey()), clock);
        await using var node = new ClusterNode(cfg, hub.CreateBus("PC", cfg), clock,
            new AckMemory(clock, TimeSpan.FromHours(2)), "0.2.0");
        int sleeps = 0;
        int roles = 0;
        node.SleepDetected += () => sleeps++;
        node.RoleChanged += _ => roles++;
        await node.TickAsync(CancellationToken.None);
        if (clockJump) clock.AdvanceMs(cfg.SleepJumpThresholdMs + 1);
        else node.ForceYield();
        await node.TickAsync(CancellationToken.None);
        await node.TickAsync(CancellationToken.None);
        Assert.Equal(1, sleeps);
        Assert.Equal(0, roles);
        Assert.True(node.IsWatcher);
        Assert.Equal("PC", Assert.Single(node.Peers).Host);
    }

    private static Alert SampleAlert(AlertStatus status = AlertStatus.Active, DateTimeOffset? at = null) => new()
    {
        Key = AlertKey.ForRule("aidite1", "tray-grab"),
        MachineId = "aidite1",
        MachineName = "Aidite 1",
        Kind = AlertKind.Error,
        ShortText = "Nie udało się pobrać tacki",
        FullText = "Failed to grab a tray",
        Category = "tacka",
        StartedAt = at ?? FakeClock.DefaultStart,
        LastSeenAt = at ?? FakeClock.DefaultStart,
        Status = status,
        AcknowledgedAt = status == AlertStatus.Acknowledged ? at ?? FakeClock.DefaultStart : null,
        AckedByHost = status == AlertStatus.Acknowledged ? "PC-B" : null,
        AckedByUser = status == AlertStatus.Acknowledged ? "operator" : null,
        Sound = true,
    };

    private sealed class Cluster : IAsyncDisposable
    {
        public Cluster(int priorityA = 10, int priorityB = 20)
        {
            Clock = new FakeClock();
            Hub = new InMemoryPeerHub(new HmacSigner(HmacSigner.GenerateKey()), Clock);
            ConfigA = new ClusterConfig { Enabled = true, Priority = priorityA };
            ConfigB = new ClusterConfig { Enabled = true, Priority = priorityB };
            BusA = Hub.CreateBus("PC-A", ConfigA);
            BusB = Hub.CreateBus("PC-B", ConfigB);
            AcksA = new AckMemory(Clock, TimeSpan.FromHours(2));
            AcksB = new AckMemory(Clock, TimeSpan.FromHours(2));
            A = new ClusterNode(ConfigA, BusA, Clock, AcksA, "0.1.0");
            B = new ClusterNode(ConfigB, BusB, Clock, AcksB, "0.1.0");
        }

        public FakeClock Clock { get; }

        public InMemoryPeerHub Hub { get; }

        public ClusterConfig ConfigA { get; }

        public ClusterConfig ConfigB { get; }

        public InMemoryPeerBus BusA { get; }

        public InMemoryPeerBus BusB { get; }

        public AckMemory AcksA { get; }

        public AckMemory AcksB { get; }

        public ClusterNode A { get; }

        public ClusterNode B { get; }

        public async Task StartAsync()
        {
            await A.StartAsync(CancellationToken.None);
            await B.StartAsync(CancellationToken.None);
        }

        /// <summary>Wykonuje <paramref name="seconds"/> sekundowych tyknięć obu węzłów.</summary>
        public async Task TickSecondsAsync(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                await A.TickAsync(CancellationToken.None);
                await B.TickAsync(CancellationToken.None);
                Clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
        }
    }

    [Fact]
    public async Task DwaWezly_PoDwudziestuPieciuSekundach_ObserwatoremZostajeTenONizszymPriorytecie()
    {
        await using var c = new Cluster();
        await c.StartAsync();

        await c.TickSecondsAsync(25);

        Assert.True(c.A.IsWatcher);
        Assert.False(c.B.IsWatcher);
        Assert.Equal("PC-A", c.A.WatcherHost);
        Assert.Equal("PC-A", c.B.WatcherHost);
        Assert.False(c.B.NoWatcher);
        Assert.Contains(c.B.Peers, p => p.Host == "PC-A" && p.Role == PeerRole.Watcher);
    }

    [Fact]
    public async Task Watcher_PoUstawieniuStanuLokalnego_PrzekazujeAlertDrugiemuKomputerowi()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);

        c.A.SetLocalState([SampleAlert()], [SnapshotOf("aidite1")]);
        await c.TickSecondsAsync(1);

        Alert alert = Assert.Single(c.B.MergedAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), alert.Key);
        Assert.Equal("Nie udało się pobrać tacki", alert.ShortText);
        Assert.Single(c.A.MergedAlerts);
        Assert.Equal("aidite1", Assert.Single(c.B.RemoteMachines).MachineId);
    }

    [Fact]
    public async Task SendAckAsync_ZDrugiegoKomputera_DocieraDoObserwatora()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);
        c.A.SetLocalState([SampleAlert()], []);
        await c.TickSecondsAsync(1);

        var odebraneNaA = new List<AckRecord>();
        c.A.AckReceived += odebraneNaA.Add;

        await c.B.SendAckAsync(AlertKey.ForRule("aidite1", "tray-grab"), "operator", CancellationToken.None);

        AckRecord ack = Assert.Single(odebraneNaA);
        Assert.Equal("PC-B", ack.Host);
        Assert.Equal("operator", ack.User);
        Assert.True(c.AcksA.IsAcked(ack.Key));
        Assert.True(c.AcksB.IsAcked(ack.Key));
    }

    [Fact]
    public async Task SendAckAsync_NatychmiastGasiAlertNaKomputerzePotwierdzajacym()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);
        c.A.SetLocalState([SampleAlert()], []);
        await c.TickSecondsAsync(1);

        await c.B.SendAckAsync(AlertKey.ForRule("aidite1", "tray-grab"), "operator", CancellationToken.None);

        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(c.B.MergedAlerts).Status);
    }

    [Fact]
    public async Task Watcher_PoOpublikowaniuAlertuPotwierdzonego_DrugiKomputerWidziStanPotwierdzony()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);
        c.A.SetLocalState([SampleAlert()], []);
        await c.TickSecondsAsync(1);

        c.A.SetLocalState([SampleAlert(AlertStatus.Acknowledged, c.Clock.UtcNow)], []);
        await c.TickSecondsAsync(1);

        Alert alert = Assert.Single(c.B.MergedAlerts);
        Assert.Equal(AlertStatus.Acknowledged, alert.Status);
        Assert.Equal("PC-B", alert.AckedByHost);
        Assert.Equal("operator", alert.AckedByUser);
    }

    [Fact]
    public async Task AlertsChanged_JestZglaszaneGdyZmieniSieZbiorAlertow()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);

        var zmiany = new List<int>();
        c.B.AlertsChanged += alerts => zmiany.Add(alerts.Count);

        c.A.SetLocalState([SampleAlert()], []);
        await c.TickSecondsAsync(1);

        Assert.Contains(1, zmiany);
    }

    [Fact]
    public async Task PoPodzialeSieci_DrugiKomputerPrzejmujeRoleDopieroPoOkresieOczekiwania()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);
        Assert.True(c.A.IsWatcher);

        c.Hub.Partition("PC-A", "PC-B");

        // Przez pierwsze ~15 s PC-A jest wciąż „żywy” w tabeli PC-B, potem znika i nikt nie obserwuje.
        await c.TickSecondsAsync(20);
        Assert.False(c.B.IsWatcher);
        Assert.True(c.B.NoWatcher, "W oknie przejmowania roli PC-B powinien zgłaszać brak obserwatora.");

        await c.TickSecondsAsync(20);

        Assert.True(c.B.IsWatcher);
        Assert.True(c.A.IsWatcher, "Odcięty PC-A nadal obserwuje maszyny (local-first).");
    }

    [Fact]
    public async Task PoNaprawieSieci_KomputerOWyzszymPriorytecieUstepuje()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);
        c.Hub.Partition("PC-A", "PC-B");
        await c.TickSecondsAsync(40);
        Assert.True(c.B.IsWatcher);

        c.Hub.Heal();
        await c.TickSecondsAsync(2);

        Assert.False(c.B.IsWatcher);
        Assert.True(c.A.IsWatcher);
        Assert.Equal("PC-A", c.B.WatcherHost);
    }

    [Fact]
    public async Task RoleChanged_JestZglaszanePrzyPrzejeciuRoli()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        var zmiany = new List<bool>();
        c.A.RoleChanged += zmiany.Add;

        await c.TickSecondsAsync(25);

        Assert.Equal([true], zmiany);
    }

    [Fact]
    public async Task WylaczonyKlaster_DzialaJakPojedynczyKomputerINieUzywaSieci()
    {
        var clock = new FakeClock();
        var hub = new InMemoryPeerHub(new HmacSigner(HmacSigner.GenerateKey()), clock);
        var cfg = new ClusterConfig { Enabled = false };
        InMemoryPeerBus busA = hub.CreateBus("PC-A", cfg);
        InMemoryPeerBus busB = hub.CreateBus("PC-B", new ClusterConfig());
        var odebranePrzezB = new List<PeerMessage>();
        busB.Received += odebranePrzezB.Add;

        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        await using var node = new ClusterNode(cfg, busA, clock, acks, "0.1.0");

        Assert.True(node.IsWatcher);
        Assert.Equal("PC-A", node.WatcherHost);
        Assert.False(node.NoWatcher);

        await node.StartAsync(CancellationToken.None);
        await node.TickAsync(CancellationToken.None);
        node.SetLocalState([SampleAlert()], []);
        await node.SendAckAsync(AlertKey.ForRule("aidite1", "tray-grab"), "operator", CancellationToken.None);

        Assert.Empty(odebranePrzezB);
        Assert.True(node.IsWatcher);
        Assert.Single(node.MergedAlerts);
        await busB.DisposeAsync();
    }

    [Fact]
    public async Task Wezel_IgnorujeWlasneWiadomosci()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        var odebranePrzezA = new List<PeerMessage>();
        c.BusA.Received += odebranePrzezA.Add;

        await c.A.TickAsync(CancellationToken.None);

        Assert.Empty(odebranePrzezA);
    }

    [Fact]
    public async Task Peers_ZawieraWpisOSobieIOSasiadach()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);

        Assert.Equal(2, c.A.Peers.Count);
        Assert.Contains(c.A.Peers, p => p.Host == "PC-A" && p.Role == PeerRole.Watcher && p.Priority == 10);
        Assert.Contains(c.A.Peers, p => p.Host == "PC-B" && p.Priority == 20);
    }

    [Fact]
    public async Task StopAsync_PrzestajePrzyjmowacWiadomosciZSieci()
    {
        await using var c = new Cluster();
        await c.StartAsync();
        await c.TickSecondsAsync(25);
        c.A.SetLocalState([SampleAlert()], []);
        await c.TickSecondsAsync(1);
        Assert.Single(c.B.MergedAlerts);

        await c.B.StopAsync();
        c.A.SetLocalState([SampleAlert(), SampleAlert() with { Key = AlertKey.ForRule("amm580", "spindle") }], []);
        await c.TickSecondsAsync(1);

        Assert.Single(c.B.MergedAlerts);
    }

    private static MachineStatusSnapshot SnapshotOf(string machineId) => new()
    {
        MachineId = machineId,
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
        State = MachineConnectionState.Connected,
        Source = "vnc",
        UpdatedAt = FakeClock.DefaultStart,
    };
}
