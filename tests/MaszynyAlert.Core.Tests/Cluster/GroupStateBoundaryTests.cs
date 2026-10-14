using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>
/// Granice pakietów STATE v2 dla zastąpionych UUID: sprzeczne relacje, powtórzenia, limity 128/512/2048,
/// replay po retencji i rozmiar UDP. Zasada: wadliwy pakiet jest odrzucany w całości, wpis sprzeczny
/// z lokalną wiedzą (cudza maszyna/rodzaj) jest pomijany, dowód tożsamości alertu nigdy nie jest przycinany.
/// </summary>
public sealed class GroupStateBoundaryTests
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static Alert Make(FakeClock clock, string machine = "aidite1", AlertKind kind = AlertKind.Error,
        Guid? id = null, IReadOnlyList<Guid>? superseded = null, int keys = 0, string? fullText = null) => new()
    {
        EpisodeId = id ?? Guid.NewGuid(), SupersededEpisodeIds = superseded ?? [],
        EpisodeKeys = Enumerable.Range(0, keys).Select(i => new AlertKey(machine, $"rule:code-{i:000}")).ToArray(),
        Key = new AlertKey(machine, "rule:code-705"), MachineId = machine, MachineName = machine.ToUpperInvariant(),
        Kind = kind, ShortText = fullText is null ? "Servo 6" : new string('S', 200), FullText = fullText, Category = "restart",
        Status = AlertStatus.Active, StartedAt = clock.UtcNow, LastSeenAt = clock.UtcNow, Sound = true,
    };

    private static Guid[] Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

    // ---- MessageCodec: rozmiar UDP przy pełnych listach ------------------------------------------------

    [Fact]
    public void GroupState_WithFullLists_FitsUdpLimitWithoutCuttingIdentityEvidence()
    {
        var clock = new FakeClock(); var signer = new HmacSigner(HmacSigner.GenerateKey());
        var identity = GroupFileTests.Invitation().Identity;
        var alerts = Enumerable.Range(0, 128).Select(i => Make(clock, $"m{i:000}", superseded: Ids(128), keys: 128,
            fullText: new string('F', 500))).ToArray();
        var closures = Enumerable.Range(0, 256).Select(i => new EpisodeClosure(Guid.NewGuid(), $"m{i:000}",
            AlertKind.Error, clock.UtcNow)).ToArray();
        // 128 machine snapshots alone are ~59 KB, so the receiver's 128 cap is a guard, not a capacity promise;
        // twelve is the largest configured fleet and must survive intact.
        var machines = Enumerable.Range(0, 12).Select(i => new MachineStatusSnapshot { MachineId = $"m{i:000}", Name = $"m{i:000}", Host = "127.0.0.1", Profile = "test",
            State = MachineConnectionState.Connected }).ToArray();
        var state = new StateMessage { Sender = "PC-A", Version = "test", Seq = 1, SentAt = clock.UtcNow,
            InstanceId = Guid.NewGuid().ToString(), Group = identity, Alerts = alerts, ClosedEpisodes = closures,
            Machines = machines };

        byte[] datagram = MessageCodec.Encode(state, signer);

        Assert.True(datagram.Length <= MessageCodec.MaxDatagramSizeBytes, $"{datagram.Length} B");
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason), reason);
        var sent = Assert.IsType<StateMessage>(decoded);
        Assert.NotEmpty(sent.Alerts);
        Assert.Equal(12, sent.Machines.Count);
        foreach (var a in sent.Alerts)
        {
            var original = alerts.Single(o => o.EpisodeId == a.EpisodeId);
            Assert.Equal(original.SupersededEpisodeIds, a.SupersededEpisodeIds);
            Assert.Equal(original.EpisodeKeys, a.EpisodeKeys);
            Assert.Equal(original.ShortText, a.ShortText);
        }
        // Whole entries go, never the identity of a kept alert; closures are the first to be dropped.
        Assert.True(sent.ClosedEpisodes.Count < 256);
    }

    [Fact]
    public void GroupState_ThatFits_IsSentUnchanged()
    {
        var clock = new FakeClock(); var signer = new HmacSigner(HmacSigner.GenerateKey());
        var state = new StateMessage { Sender = "PC-A", Version = "test", Seq = 1, SentAt = clock.UtcNow,
            InstanceId = Guid.NewGuid().ToString(), Group = GroupFileTests.Invitation().Identity,
            Alerts = [Make(clock, superseded: Ids(128), keys: 128, fullText: new string('F', 500))],
            ClosedEpisodes = Enumerable.Range(0, 256).Select(_ => new EpisodeClosure(Guid.NewGuid(), "aidite1",
                AlertKind.Error, clock.UtcNow)).ToArray() };
        Assert.True(MessageCodec.TryDecode(MessageCodec.Encode(state, signer), signer, clock, Skew, out var decoded, out _));
        var sent = Assert.IsType<StateMessage>(decoded);
        Assert.Equal(256, sent.ClosedEpisodes.Count);
        Assert.Equal(new string('F', 500), Assert.Single(sent.Alerts).FullText);
    }

    // ---- GroupClusterNode: odbiór -----------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForeignClosureForUnknownId_InSameState_DoesNotSuppressEpisode(bool sameMachine, bool predecessor)
    {
        await using var rig = new Rig(); await rig.Start();
        var id = Guid.NewGuid();
        var alert = Make(rig.Clock, id: predecessor ? Guid.NewGuid() : id,
            superseded: predecessor ? [id] : []);
        var delivered = new List<EpisodeClosure>();
        rig.Node.EpisodeStateReceived += (_, closures) => delivered.AddRange(closures);
        await rig.State([alert], [new(id, sameMachine ? alert.MachineId : "aidite2",
            sameMachine ? AlertKind.Offline : alert.Kind, rig.Clock.UtcNow)]);

        Assert.Equal(alert.EpisodeId, Assert.Single(rig.Node.MergedAlerts).EpisodeId);
        Assert.DoesNotContain(delivered, c => c.MachineId != alert.MachineId || c.Kind != alert.Kind);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.State([alert with { Status = AlertStatus.Acknowledged, AcknowledgedAt = rig.Clock.UtcNow,
            AckedByHost = "B", AckedByUser = "operator" }]);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(rig.Node.MergedAlerts).Status);
    }

    [Fact]
    public async Task MatchingClosureForUnknownId_InSameState_PreventsReplay()
    {
        await using var rig = new Rig(); await rig.Start();
        var alert = Make(rig.Clock);
        await rig.State([alert], [new(alert.EpisodeId, alert.MachineId, alert.Kind, rig.Clock.UtcNow)]);
        Assert.Empty(rig.Node.MergedAlerts);
        await rig.State([alert]);
        Assert.Empty(rig.Node.MergedAlerts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignClosureForRetiredId_AfterRetention_DoesNotPoisonKnownSuccessor(bool sameMachine)
    {
        await using var rig = new Rig(); await rig.Start();
        var retired = Guid.NewGuid();
        var alert = Make(rig.Clock, superseded: [retired]);
        await rig.State([alert]);
        rig.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        await rig.Node.TickAsync(default);
        Assert.DoesNotContain(rig.Node.CaptureState().Closed, c => c.EpisodeId == retired);
        await rig.Heartbeat();
        await rig.State([], [new(retired, sameMachine ? alert.MachineId : "aidite2",
            sameMachine ? AlertKind.Offline : alert.Kind, rig.Clock.UtcNow)]);
        Assert.DoesNotContain(rig.Node.CaptureState().Closed, c => c.EpisodeId == retired);
        await rig.State([alert with { LastSeenAt = rig.Clock.UtcNow, Status = AlertStatus.Acknowledged,
            AcknowledgedAt = rig.Clock.UtcNow, AckedByHost = "B", AckedByUser = "operator" }]);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(rig.Node.MergedAlerts).Status);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly InMemoryPeerHub Hub;
        public readonly ClusterConfig Config;
        public readonly GroupClusterNode Node;
        public readonly InMemoryPeerBus Bus;
        public readonly InMemoryPeerBus Peer;
        public readonly List<PeerMessage> PeerReceived = [];

        public Rig()
        {
            var file = GroupFileTests.Invitation(); Config = new() { Enabled = true, Group = file.Identity };
            Hub = new(HmacSigner.FromBase64(file.NetworkKey), Clock);
            Bus = Hub.CreateBus("A"); Node = new(Config, Bus, Clock, "test");
            Node.SetReadiness(new(true, false, []));
            Peer = Hub.CreateBus("B"); Peer.Received += m => PeerReceived.Add(m);
        }

        public async Task Start() { await Node.StartAsync(default); await Heartbeat(); }

        public Task Heartbeat() => Peer.SendAsync(new HeartbeatMessage { Sender = "B", Version = "test",
            Group = Config.Group, Role = PeerRole.Watcher, Readiness = new(true, false, []) }, default);

        public Task State(IReadOnlyList<Alert> alerts, IReadOnlyList<EpisodeClosure>? closed = null) =>
            Peer.SendAsync(new StateMessage { Sender = "B", Version = "test", Group = Config.Group, Alerts = alerts,
                ClosedEpisodes = closed ?? [] }, default);

        public async Task Tick(int seconds)
        {
            for (int i = 0; i < seconds; i++) { await Node.TickAsync(default); Clock.Advance(TimeSpan.FromSeconds(1)); }
        }

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync(); await Peer.DisposeAsync();
        }
    }

    [Fact]
    public async Task MutualSupersession_InOnePacket_RejectsWholeState()
    {
        await using var rig = new Rig(); await rig.Start();
        var x = Guid.NewGuid(); var y = Guid.NewGuid();
        await rig.State([Make(rig.Clock, id: x, superseded: [y]), Make(rig.Clock, id: y, superseded: [x]),
            Make(rig.Clock, "aidite2")]);
        Assert.Empty(rig.Node.MergedAlerts);
    }

    [Fact]
    public async Task SupersededIdListedAsActiveInSamePacket_RejectsWholeState()
    {
        await using var rig = new Rig(); await rig.Start();
        var y = Make(rig.Clock);
        await rig.State([Make(rig.Clock, superseded: [y.EpisodeId]), y, Make(rig.Clock, "aidite2")]);
        Assert.Empty(rig.Node.MergedAlerts);
    }

    [Fact]
    public async Task DuplicateSupersededIds_RejectsWholeState()
    {
        await using var rig = new Rig(); await rig.Start();
        var old = Guid.NewGuid();
        await rig.State([Make(rig.Clock, superseded: [old, old]), Make(rig.Clock, "aidite2")]);
        Assert.Empty(rig.Node.MergedAlerts);
    }

    [Fact]
    public async Task DuplicateEpisodeIds_InOnePacket_RejectsWholeState()
    {
        await using var rig = new Rig(); await rig.Start();
        var id = Guid.NewGuid();
        await rig.State([Make(rig.Clock, id: id), Make(rig.Clock, id: id) with { ShortText = "other" },
            Make(rig.Clock, "aidite2")]);
        Assert.Empty(rig.Node.MergedAlerts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupersedingForeignEpisode_IgnoresEntry_AndKeepsForeignEpisodeUpdatable(bool sameMachine)
    {
        await using var rig = new Rig(); await rig.Start();
        var foreign = Make(rig.Clock, "aidite2", AlertKind.Error);
        await rig.State([foreign]);
        Assert.Single(rig.Node.MergedAlerts);

        var claimant = Make(rig.Clock, sameMachine ? "aidite2" : "aidite1", sameMachine ? AlertKind.Offline : AlertKind.Error,
            superseded: [foreign.EpisodeId]);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        var acked = foreign with { Status = AlertStatus.Acknowledged, AcknowledgedAt = rig.Clock.UtcNow,
            AckedByHost = "B", AckedByUser = "operator" };
        // Sent separately: one packet carrying F both retired and active would be rejected as contradictory.
        await rig.State([claimant]);
        Assert.Equal(foreign.EpisodeId, Assert.Single(rig.Node.MergedAlerts).EpisodeId);
        await rig.State([acked]);

        var merged = Assert.Single(rig.Node.MergedAlerts);
        Assert.Equal(foreign.EpisodeId, merged.EpisodeId);
        Assert.Equal(AlertStatus.Acknowledged, merged.Status);
        Assert.Empty(merged.SupersededEpisodeIds);
    }

    [Fact]
    public async Task SupersedingForeignClosedEpisode_IgnoresEntry()
    {
        await using var rig = new Rig(); await rig.Start();
        var closedForeign = Guid.NewGuid();
        await rig.State([], [new EpisodeClosure(closedForeign, "aidite2", AlertKind.Error, rig.Clock.UtcNow)]);
        await rig.State([Make(rig.Clock, "aidite1", superseded: [closedForeign]), Make(rig.Clock, "aidite3")]);
        Assert.Equal("aidite3", Assert.Single(rig.Node.MergedAlerts).MachineId);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task SupersededListLimit_128(int count, bool accepted)
    {
        await using var rig = new Rig(); await rig.Start();
        await rig.State([Make(rig.Clock, superseded: Ids(count)), Make(rig.Clock, "aidite2")]);
        Assert.Equal(accepted ? 2 : 0, rig.Node.MergedAlerts.Count);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task AlertListLimit_128(int count, bool accepted)
    {
        await using var rig = new Rig(); await rig.Start();
        await rig.State(Enumerable.Range(0, count).Select(i => Make(rig.Clock, $"m{i:000}")).ToArray());
        Assert.Equal(accepted ? count : 0, rig.Node.MergedAlerts.Count);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task ClosureListLimit_256(int count, bool accepted)
    {
        await using var rig = new Rig(); await rig.Start();
        var active = Make(rig.Clock); await rig.State([active]);
        var closures = Enumerable.Range(0, count - 1).Select(_ => new EpisodeClosure(Guid.NewGuid(), "aidite9",
            AlertKind.Error, rig.Clock.UtcNow)).Append(new EpisodeClosure(active.EpisodeId, active.MachineId,
            active.Kind, rig.Clock.UtcNow)).ToArray();
        await rig.State([], closures);
        Assert.Equal(accepted ? 0 : 1, rig.Node.MergedAlerts.Count);
    }

    [Fact]
    public async Task KnownEpisodeCap_512_IgnoresFurtherEpisodes()
    {
        await using var rig = new Rig(); await rig.Start();
        for (int batch = 0; batch < 4; batch++)
            await rig.State(Enumerable.Range(0, 128).Select(i => Make(rig.Clock, $"m{batch}-{i:000}")).ToArray());
        Assert.Equal(512, rig.Node.MergedAlerts.Count);
        await rig.State([Make(rig.Clock, "extra")]);
        Assert.Equal(512, rig.Node.MergedAlerts.Count);
        Assert.DoesNotContain(rig.Node.MergedAlerts, a => a.MachineId == "extra");
    }

    [Fact]
    public async Task ClosureCap_2048_KeepsRingingInsteadOfDroppingEvidence()
    {
        await using var rig = new Rig(); await rig.Start();
        var active = Make(rig.Clock); await rig.State([active]);
        for (int batch = 0; batch < 8; batch++)
            await rig.State([active], Enumerable.Range(0, 256).Select(_ => new EpisodeClosure(Guid.NewGuid(), "aidite9",
                AlertKind.Error, rig.Clock.UtcNow)).ToArray());
        await rig.State([], [new EpisodeClosure(active.EpisodeId, active.MachineId, active.Kind, rig.Clock.UtcNow)]);
        Assert.Equal(active.EpisodeId, Assert.Single(rig.Node.MergedAlerts).EpisodeId);
        // After retention frees the table the same closure is honoured (the peer must be fresh again).
        rig.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)); await rig.Tick(1); await rig.Heartbeat();
        await rig.State([], [new EpisodeClosure(active.EpisodeId, active.MachineId, active.Kind, rig.Clock.UtcNow)]);
        Assert.Empty(rig.Node.MergedAlerts);
    }

    [Fact]
    public async Task ReplayOfSupersededUuid_AfterRetention_DoesNotResurrectWhileWinnerIsKnown()
    {
        await using var rig = new Rig(); await rig.Start();
        var loser = Make(rig.Clock);
        var winner = Make(rig.Clock, superseded: [loser.EpisodeId]) with { StartedAt = rig.Clock.UtcNow.AddMinutes(1) };
        await rig.State([winner]);
        Assert.Equal(winner.EpisodeId, Assert.Single(rig.Node.MergedAlerts).EpisodeId);

        rig.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)); await rig.Tick(1); await rig.Heartbeat();
        // A stale peer re-announcing the retired UUID alone is the classic replay; the winner stays the identity.
        await rig.State([loser with { LastSeenAt = rig.Clock.UtcNow }]);
        Assert.Equal(winner.EpisodeId, Assert.Single(rig.Node.MergedAlerts).EpisodeId);
        await rig.State([winner, loser with { LastSeenAt = rig.Clock.UtcNow }]);
        var merged = Assert.Single(rig.Node.MergedAlerts);
        Assert.Equal(winner.EpisodeId, merged.EpisodeId);
        Assert.Equal([loser.EpisodeId], merged.SupersededEpisodeIds);
    }

    // ---- GroupClusterNode: wysyłka ----------------------------------------------------------------------

    [Fact]
    public async Task WatcherWithOversizedLocalState_StillPublishesStateWithinUdpLimit()
    {
        await using var rig = new Rig(); await rig.Node.StartAsync(default);
        await rig.Tick(25);
        Assert.True(rig.Node.IsWatcher);
        var local = Enumerable.Range(0, 24).Select(i => Make(rig.Clock, $"m{i:000}", superseded: Ids(128), keys: 128,
            fullText: new string('F', 500))).ToArray();
        rig.Node.SetLocalState(local, local.Select(a => new MachineStatusSnapshot { MachineId = a.MachineId, Name = a.MachineId, Host = "127.0.0.1", Profile = "test",
            State = MachineConnectionState.Connected }).ToArray());
        rig.PeerReceived.Clear();

        await rig.Node.TickAsync(default);

        Assert.Null(rig.Node.TransportError);
        var state = Assert.Single(rig.PeerReceived.OfType<StateMessage>());
        Assert.NotEmpty(state.Alerts);
        Assert.All(state.Alerts, a => Assert.Equal(128, a.SupersededEpisodeIds.Count));
        Assert.Equal(24, state.Machines.Count);
    }
}
