using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class GroupRuntimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndependentPartitionEpisodes_ConvergeAndNeverReturnAfterClosure(bool acknowledgeOld, bool reverseDelivery)
    {
        await using var pair = new Pair(); await pair.Start();
        pair.Hub.Partition("A", "B");
        var old = pair.Raise(); await pair.Tick(36);
        if (acknowledgeOld) pair.AState.AcknowledgeLocal(old.Key, "A", "operator");
        var fresh = pair.BState.OnRedConfirmed(pair.Machine, pair.Message, null);
        Assert.NotEqual(old.EpisodeId, fresh.EpisodeId);
        pair.ReverseDelivery = reverseDelivery;
        await pair.Tick(2); pair.Hub.Heal();
        pair.BBus.DropState = true; await pair.Tick(3);
        Assert.True(pair.B.IsWatcher); // retain observation until the replacement knows this episode
        pair.BBus.DropState = false; await pair.Tick(5);
        Assert.True(pair.A.IsWatcher); Assert.False(pair.B.IsWatcher);
        var current = Assert.Single(pair.A.MergedAlerts);
        Assert.Equal(current.EpisodeId, Assert.Single(pair.B.MergedAlerts).EpisodeId);
        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Equal(current.EpisodeId, Assert.Single(pair.AState.ActiveAlerts).EpisodeId);
        var retired = Assert.Single(current.SupersededEpisodeIds);
        await pair.BBus.SendAsync(new AckMessage { Sender = "B", Version = "test", Group = pair.Config.Group,
            Key = old.Key, EpisodeId = retired, User = "late", AckedAt = pair.Clock.UtcNow }, default);
        Assert.Equal(AlertStatus.Active, Assert.Single(pair.AState.ActiveAlerts).Status);
        await pair.B.SendAckAsync(current.Key, "operator", default); await pair.Tick(2);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(pair.AState.ActiveAlerts).Status);
        pair.AState.OnRedCleared(pair.Machine.Id);
        await pair.Tick(31); pair.AState.Tick(); await pair.Tick(2);
        Assert.Empty(pair.A.MergedAlerts); Assert.Empty(pair.B.MergedAlerts);
        Assert.Empty(pair.A.CaptureState().Alerts); Assert.Empty(pair.B.CaptureState().Alerts);
        // A delayed STATE from a former observer must not resurrect its retired UUID.
        await pair.BBus.SendAsync(new HeartbeatMessage { Sender = "B", Version = "test", Group = pair.Config.Group,
            Role = PeerRole.Watcher, Readiness = new(true, false, []) }, default);
        await pair.BBus.SendAsync(new StateMessage { Sender = "B", Version = "test", Group = pair.Config.Group,
            Alerts = [old, fresh] }, default);
        Assert.Empty(pair.A.MergedAlerts);
        await pair.Tick((int)(new TimingConfig().SameMessageRejoinMs / 1000) + 1);
        var next = pair.Raise(); await pair.Tick(2);
        await pair.BBus.SendAsync(new AckMessage { Sender = "B", Version = "test", Group = pair.Config.Group,
            Key = old.Key, EpisodeId = old.EpisodeId, User = "late", AckedAt = pair.Clock.UtcNow }, default);
        Assert.Equal(next.EpisodeId, Assert.Single(pair.A.MergedAlerts).EpisodeId);
        Assert.Equal(AlertStatus.Active, Assert.Single(pair.AState.ActiveAlerts).Status);
    }

    [Fact]
    public async Task ThreeIndependentUuids_ConvergeInAnyOrder_AndRetirementSurvivesRuntimeSnapshot()
    {
        var clock = new FakeClock(); var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var alerts = Enumerable.Range(1, 3).Select(i => new Alert { EpisodeId = Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
            Key = new("machine", $"code-{i}"), MachineId = "machine", MachineName = "Machine",
            Kind = AlertKind.Error, StartedAt = clock.UtcNow, LastSeenAt = clock.UtcNow,
            ShortText = "Error", Category = "error", Status = AlertStatus.Acknowledged,
            AcknowledgedAt = clock.UtcNow, AckedByHost = "old" }).ToArray();
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var order in orders)
        {
            await using var node = new GroupClusterNode(config, hub.CreateBus("A"), clock, "test");
            node.RestoreState(new(order.Select(i => alerts[i]).ToArray(), [], [], false));
            var current = Assert.Single(node.MergedAlerts);
            Assert.Equal(alerts[2].EpisodeId, current.EpisodeId);
            Assert.Equal(AlertStatus.Active, current.Status);
            Assert.Equal(2, current.SupersededEpisodeIds.Count);
            await using var restored = new GroupClusterNode(config, hub.CreateBus("restored"), clock, "test");
            restored.RestoreState(node.CaptureState());
            restored.RecordClosure(current with { Status = AlertStatus.Closed, ClosedAt = clock.UtcNow });
            restored.RestoreState(new(alerts, [], [], false));
            Assert.Empty(restored.MergedAlerts);
            Assert.Empty(restored.CaptureState().Alerts);
        }
    }

    [Fact]
    public async Task StateOnlyAcknowledgement_UpdatesLocalObservationBeforeNextTakeover()
    {
        await using var pair = new Pair(); await pair.Start();
        var original = pair.Raise(); await pair.Tick(3);
        pair.Hub.Partition("A", "B"); await pair.Tick(36);
        pair.AState.AcknowledgeLocal(original.Key, "A", "operator");
        pair.ABus.DropAck = true; pair.ABus.DropAckHistory = true;
        pair.Hub.Heal(); await pair.Tick(3);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(pair.BState.ActiveAlerts).Status);
        pair.Hub.Partition("A", "B"); await pair.Tick(36);
        var adopted = pair.BState.OnRedConfirmed(pair.Machine, pair.Message, null);
        Assert.Equal(original.EpisodeId, adopted.EpisodeId);
        Assert.Equal(AlertStatus.Acknowledged, adopted.Status);
    }

    [Fact]
    public async Task StateOnlyClosure_RemovesLocalEpisode_AndNextOccurrenceAlarms()
    {
        await using var pair = new Pair(); await pair.Start();
        var original = pair.Raise(); await pair.Tick(3);
        pair.Hub.Partition("A", "B"); await pair.Tick(36);
        pair.AState.OnRedCleared(pair.Machine.Id);
        pair.AState.Tick();
        Assert.Empty(pair.AState.ActiveAlerts);
        pair.Hub.Heal(); await pair.Tick(3);
        Assert.Empty(pair.BState.ActiveAlerts);
        pair.Hub.Partition("A", "B"); await pair.Tick(36);
        var next = pair.BState.OnRedConfirmed(pair.Machine, pair.Message, null);
        Assert.NotEqual(original.EpisodeId, next.EpisodeId);
        Assert.Equal(AlertStatus.Active, next.Status);
        await pair.Tick(2);
        Assert.Equal(next.EpisodeId, Assert.Single(pair.B.MergedAlerts).EpisodeId);
    }

    [Fact]
    public void GroupOfflineEpisode_DoesNotInheritLegacyKeyAcknowledgement()
    {
        var clock = new FakeClock();
        var machine = TestPaths.ExampleConfig.Machines.First(m => m.Enabled);
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        memory.RecordLocal(new(AlertKey.Offline(machine.Id), "old", "operator", clock.UtcNow));
        var state = new AlertStateMachine(clock, new TimingConfig(), memory);
        state.UseGroupProtocol(true);
        state.OnMachineOffline(machine, clock.UtcNow);
        clock.Advance(TimeSpan.FromMinutes(4)); state.Tick();
        Assert.Equal(AlertStatus.Active, Assert.Single(state.ActiveAlerts).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Takeover_PreservesUuidStartAndAcknowledgement(bool acknowledge)
    {
        await using var pair = new Pair(); await pair.Start();
        var original = pair.Raise(); await pair.Tick(3);
        if (acknowledge) await pair.B.SendAckAsync(original.Key, "operator", default);
        pair.Hub.Partition("A", "B"); await pair.Tick(36);
        Assert.True(pair.B.IsWatcher);
        var fresh = pair.BState.OnRedConfirmed(pair.Machine, pair.Message, null);
        Assert.Equal(original.EpisodeId, fresh.EpisodeId);
        Assert.Equal(original.StartedAt, fresh.StartedAt);
        Assert.Equal(acknowledge ? AlertStatus.Acknowledged : AlertStatus.Active, fresh.Status);
    }

    [Fact]
    public async Task HeartbeatWithoutState_RetainsKnownAlarm_AndMarksDataStale()
    {
        await using var pair = new Pair(); await pair.Start();
        var original = pair.Raise(); await pair.Tick(3);
        pair.ABus.DropState = true; await pair.Tick(8);
        Assert.False(pair.B.StateFresh);
        Assert.Equal(original.EpisodeId, Assert.Single(pair.B.MergedAlerts).EpisodeId);
        Assert.Contains(pair.B.Peers, p => p.Host == "A" && p.IsAlive(pair.Clock.UtcNow, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task LostAck_IsRecoveredByHeartbeat_AndOcrAliasDoesNotChangeEpisode()
    {
        await using var pair = new Pair(); await pair.Start();
        var original = pair.Raise(); await pair.Tick(3);
        pair.BBus.DropAck = true;
        await pair.B.SendAckAsync(original.Key, "operator", default);
        Assert.Equal(AlertStatus.Active, Assert.Single(pair.AState.ActiveAlerts).Status);
        await pair.Tick(2);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(pair.AState.ActiveAlerts).Status);
        var improved = pair.AState.OnRedConfirmed(pair.Machine, pair.Message with { RuleId = "code-725", Priority = 90 }, null);
        Assert.Equal(original.EpisodeId, improved.EpisodeId);
        await pair.Tick(2);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(pair.B.MergedAlerts).Status);
    }

    [Fact]
    public async Task ClosureAndOldAck_CannotSilenceNewOccurrenceOfSameCode()
    {
        await using var pair = new Pair(); await pair.Start();
        var old = pair.Raise(); await pair.Tick(3);
        await pair.B.SendAckAsync(old.Key, "operator", default);
        pair.AState.OnMachineOffline(pair.Machine, pair.Clock.UtcNow);
        await pair.Tick((int)(new TimingConfig().EpisodeBreakAfterMs / 1000));
        var next = pair.Raise(); Assert.NotEqual(old.EpisodeId, next.EpisodeId);
        await pair.Tick(2);
        await pair.BBus.SendAsync(new AckMessage { Sender = "B", Version = "test", Group = pair.Config.Group,
            Key = old.Key, EpisodeId = old.EpisodeId, User = "old", AckedAt = pair.Clock.UtcNow }, default);
        Assert.Equal(AlertStatus.Active, Assert.Single(pair.AState.ActiveAlerts).Status);
        Assert.Equal(next.EpisodeId, Assert.Single(pair.B.MergedAlerts).EpisodeId);
    }

    [Fact]
    public async Task LongGap_DoesNotAdoptUnverifiableAcknowledgedEpisode()
    {
        await using var pair = new Pair(); await pair.Start();
        var old = pair.Raise(); await pair.Tick(3);
        await pair.B.SendAckAsync(old.Key, "operator", default);
        pair.Hub.Partition("A", "B");
        pair.B.SetReadiness(new(false, false, [])); await pair.Tick(100);
        pair.B.SetReadiness(new(true, false, [])); await pair.Tick(21);
        var next = pair.BState.OnRedConfirmed(pair.Machine, pair.Message, null);
        Assert.NotEqual(old.EpisodeId, next.EpisodeId);
        Assert.Equal(AlertStatus.Active, next.Status);
    }

    [Fact]
    public async Task PartitionAndHeal_ConvergesToOneWatcherWithoutRevivingAck()
    {
        await using var pair = new Pair(); await pair.Start(); pair.Raise(); await pair.Tick(3);
        pair.Hub.Partition("A", "B"); await pair.Tick(36);
        Assert.True(pair.A.IsWatcher); Assert.True(pair.B.IsWatcher);
        pair.BState.OnRedConfirmed(pair.Machine, pair.Message, null);
        await pair.B.SendAckAsync(pair.MessageKey, "operator", default);
        pair.Hub.Heal(); await pair.Tick(5);
        Assert.True(pair.A.IsWatcher); Assert.False(pair.B.IsWatcher);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(pair.A.MergedAlerts).Status);
    }

    [Fact]
    public async Task MissingPasswordsExcludeCandidate_OcrOnlyLimitsCapabilities()
    {
        await using var pair = new Pair();
        pair.A.SetReadiness(new(false, false, ["missing"]));
        pair.B.SetReadiness(new(true, true, []));
        await pair.Start();
        Assert.False(pair.A.IsWatcher); Assert.True(pair.B.IsWatcher);
        Assert.True(pair.B.Readiness.OcrLimited);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("key")]
    [InlineData("profile")]
    [InlineData("protocol")]
    [InlineData("legacy")]
    public async Task IncompatibleTrafficCannotAffectElectionOrAlerts(string mismatch)
    {
        await using var pair = new Pair(); await pair.Start();
        var identity = pair.Config.Group!;
        var changed = mismatch switch
        {
            "group" => identity with { GroupId = Guid.NewGuid() }, "key" => identity with { KeyId = Guid.NewGuid() },
            "profile" => identity with { ProfileHash = new string('f', 64) },
            "protocol" => identity with { ProtocolVersion = 1 }, _ => null
        };
        await using var foreign = pair.Hub.CreateBus("FOREIGN");
        await foreign.SendAsync(new HeartbeatMessage { Sender = "FOREIGN", Version = "bad", Group = changed,
            Priority = 0, Role = PeerRole.Watcher, Readiness = new(true, false, []) }, default);
        var alert = pair.Raise();
        await foreign.SendAsync(new StateMessage { Sender = "FOREIGN", Version = "bad", Group = changed, Alerts = [alert] }, default);
        await pair.Tick(2);
        Assert.Equal("A", pair.B.WatcherHost);
        Assert.DoesNotContain(pair.B.Peers, p => p.Host == "FOREIGN" && p.Compatibility == GroupCompatibility.Compatible);
    }

    [Fact]
    public async Task DiagnosticsRequiresSignedMatchingNonceReply_AndTimeoutIsNotWrongKeyClaim()
    {
        await using var pair = new Pair(); await pair.Start();
        Assert.Equal("B", await pair.A.CheckConnectionAsync(TimeSpan.FromMilliseconds(100), default));
        pair.Hub.Partition("A", "B");
        Assert.Null(await pair.A.CheckConnectionAsync(TimeSpan.FromMilliseconds(30), default));
    }

    [Fact]
    public async Task RuntimeReplacement_ClosesOldBus_RollsBackFailedBind_AndDisabledUsesNoNetwork()
    {
        var clock = new FakeClock(); var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        int live = 0, maximum = 0, calls = 0;
        IPeerBus Factory(ClusterConfig c, string k)
        {
            calls++; if (c.UdpPort == 50001) throw new IOException("bind failure");
            live++; maximum = Math.Max(maximum, live);
            return new FilterBus(hub.CreateBus("A", c), () => live--);
        }
        await using var runtime = new ClusterRuntime(config, file.NetworkKey, clock,
            new AckMemory(clock, TimeSpan.FromHours(2)), "A", "test", Factory);
        runtime.SetReadiness(new(true, false, [])); await runtime.StartAsync(default);
        await Assert.ThrowsAsync<IOException>(() => runtime.SwitchAsync(config with { UdpPort = 50001 }, file.NetworkKey, default));
        Assert.Equal(config, runtime.Configuration); Assert.True(runtime.NetworkActive); Assert.Equal(1, live);
        await runtime.SwitchAsync(config with { Enabled = false }, file.NetworkKey, default);
        Assert.Equal(0, live); Assert.False(runtime.NetworkActive); Assert.True(runtime.IsWatcher);
        await runtime.TickAsync(default); Assert.Equal(3, calls); Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task RuntimeForwardsStateTransitionsToObservation_WithoutRepublishingOldState()
    {
        var clock = new FakeClock(); var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        var state = new AlertStateMachine(clock, new TimingConfig(), memory);
        state.UseGroupProtocol(true);
        var machine = TestPaths.ExampleConfig.Machines.First(m => m.Enabled);
        var original = state.OnRedConfirmed(machine,
            new("code-705", "Servo 6", "restart", "No.705", "NO 705", true), null);
        await using var runtime = new ClusterRuntime(config, file.NetworkKey, clock, memory,
            "A", "test", (c, _) => hub.CreateBus("A", c));
        runtime.SetReadiness(new(true, false, [])); runtime.SetLocalState(state.ActiveAlerts, []);
        runtime.EpisodeStateReceived += state.ReconcileGroupState;
        state.Changed += e =>
        {
            if (e.Kind == AlertEventKind.Closed) runtime.RecordClosure(e.Alert);
            if (runtime.IsWatcher) runtime.SetLocalState(state.ActiveAlerts, []);
        };
        await runtime.StartAsync(default);
        await using var peer = hub.CreateBus("B");
        await peer.SendAsync(new HeartbeatMessage { Sender = "B", Version = "test", Group = config.Group,
            Role = PeerRole.Watcher, Readiness = new(true, false, []) }, default);
        var ack = original with { Status = AlertStatus.Acknowledged, AcknowledgedAt = clock.UtcNow,
            AckedByHost = "B", AckedByUser = "operator" };
        await peer.SendAsync(new StateMessage { Sender = "B", Version = "test", Group = config.Group, Alerts = [ack] }, default);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(runtime.MergedAlerts).Status);
        // The local detector can submit a pre-ACK snapshot after the receive callback.
        runtime.SetLocalState([original], []);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(runtime.MergedAlerts).Status);
        await peer.SendAsync(new StateMessage { Sender = "B", Version = "test", Group = config.Group,
            ClosedEpisodes = [new(original.EpisodeId, machine.Id, original.Kind, clock.UtcNow)] }, default);
        Assert.Empty(state.ActiveAlerts); Assert.Empty(runtime.MergedAlerts);
        runtime.SetLocalState([original], []);
        Assert.Empty(runtime.MergedAlerts);
    }

    [Fact]
    public async Task FailedStartup_ReportsLocalFallbackAndObservesImmediately()
    {
        var clock = new FakeClock(); var file = GroupFileTests.Invitation();
        await using var runtime = new ClusterRuntime(new ClusterConfig { Enabled = true, Group = file.Identity },
            file.NetworkKey, clock, new AckMemory(clock, TimeSpan.FromHours(2)), "A", "test",
            (_, _) => throw new IOException("UDP unavailable"));
        await runtime.StartAsync(default);
        Assert.True(runtime.IsWatcher); Assert.False(runtime.NetworkActive); Assert.NotNull(runtime.Failure);
        Assert.True(runtime.Configuration.Enabled);
    }

    internal sealed class Pair : IAsyncDisposable
    {
        public bool ReverseDelivery { get; set; }
        /// <summary>Migawki maszyn publikowane przez dany węzeł ("A"/"B"); domyślnie jedna maszyna Connected.</summary>
        public Func<string, IReadOnlyList<MachineStatusSnapshot>>? MachinesFor { get; set; }
        public FakeClock Clock { get; } = new();
        public InMemoryPeerHub Hub { get; }
        public ClusterConfig Config { get; }
        public GroupClusterNode A { get; }
        public GroupClusterNode B { get; }
        public FilterBus ABus { get; }
        public FilterBus BBus { get; }
        public AlertStateMachine AState { get; }
        public AlertStateMachine BState { get; }
        public MachineConfig Machine { get; } = TestPaths.ExampleConfig.Machines.First(m => m.Enabled);
        public MappedMessage Message { get; } = new("code-705", "Servo 6", "restart", "No.705", "NO 705", true);
        public AlertKey MessageKey => AlertKey.ForMessage(Machine.Id, Message);
        public Pair(int bPriority = 100)
        {
            var file = GroupFileTests.Invitation(); Config = new() { Enabled = true, Group = file.Identity };
            Hub = new(HmacSigner.FromBase64(file.NetworkKey), Clock);
            ABus = new(Hub.CreateBus("A")); BBus = new(Hub.CreateBus("B"));
            A = new(Config, ABus, Clock, "test"); B = new(Config with { Priority = bPriority }, BBus, Clock, "test");
            A.SetReadiness(new(true, false, [])); B.SetReadiness(new(true, false, []));
            AState = new(Clock, new TimingConfig(), new AckMemory(Clock, TimeSpan.FromHours(2)));
            BState = new(Clock, new TimingConfig(), new AckMemory(Clock, TimeSpan.FromHours(2)));
            AState.UseGroupProtocol(true); BState.UseGroupProtocol(true);
            A.AckReceived += AState.ApplyRemoteAck; B.AckReceived += BState.ApplyRemoteAck;
            A.EpisodeStateReceived += AState.ReconcileGroupState;
            B.EpisodeStateReceived += BState.ReconcileGroupState;
            A.RoleChanged += active => { if (active) AState.RestoreObservation(A.TakeoverEpisodes()); };
            B.RoleChanged += active => { if (active) BState.RestoreObservation(B.TakeoverEpisodes()); };
            AState.Changed += e => { if (e.Kind == AlertEventKind.Closed) A.RecordClosure(e.Alert); };
            BState.Changed += e => { if (e.Kind == AlertEventKind.Closed) B.RecordClosure(e.Alert); };
        }
        private bool _bStarted = true;
        public async Task Start() { await A.StartAsync(default); await B.StartAsync(default); await Tick(23); }
        /// <summary>Uruchamia tylko A; B nie tyka, dopóki nie zostanie uruchomione przez <see cref="StartB"/>.</summary>
        public async Task StartA() { _bStarted = false; await A.StartAsync(default); await Tick(23); }
        public async Task StartB() { await B.StartAsync(default); _bStarted = true; }
        public MachineStatusSnapshot Snapshot(MachineConfig m, MachineConnectionState state) => new()
        {
            MachineId = m.Id, Name = m.Name, Host = m.Host, Port = m.Port, Profile = m.Profile, State = state,
            LastFrameAt = state == MachineConnectionState.Connected ? Clock.UtcNow : null, UpdatedAt = Clock.UtcNow,
        };
        public Alert Raise() => AState.OnRedConfirmed(Machine, Message, null);
        public async Task Tick(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                var machines = new[] { new MachineStatusSnapshot { MachineId = Machine.Id, Name = Machine.Name,
                    Host = Machine.Host, Port = Machine.Port, Profile = Machine.Profile, State = MachineConnectionState.Connected,
                    LastFrameAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow } };
                if (A.IsWatcher) A.SetLocalState(AState.ActiveAlerts, MachinesFor?.Invoke("A") ?? machines);
                if (B.IsWatcher) B.SetLocalState(BState.ActiveAlerts, MachinesFor?.Invoke("B") ?? machines);
                if (ReverseDelivery) { if (_bStarted) await B.TickAsync(default); await A.TickAsync(default); }
                else { await A.TickAsync(default); if (_bStarted) await B.TickAsync(default); }
                Clock.Advance(TimeSpan.FromSeconds(1));
            }
        }
        public async ValueTask DisposeAsync() { await A.DisposeAsync(); await B.DisposeAsync(); }
    }
    internal sealed class FilterBus(IPeerBus inner, Action? disposed = null) : IPeerBus
    {
        public bool DropState, DropAck, DropAckHistory;
        private bool _disposed;
        public string LocalHost => inner.LocalHost;
        public event Action<PeerMessage>? Received { add => inner.Received += value; remove => inner.Received -= value; }
        public event Action<string>? Rejected { add => inner.Rejected += value; remove => inner.Rejected -= value; }
        public Task StartAsync(CancellationToken ct) => inner.StartAsync(ct);
        public Task SendAsync(PeerMessage msg, CancellationToken ct) => DropState && msg is StateMessage || DropAck && msg is AckMessage
            ? Task.CompletedTask : inner.SendAsync(DropAckHistory && msg is HeartbeatMessage hb
                ? hb with { RecentAcks = [] } : msg, ct);
        public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await inner.DisposeAsync(); disposed?.Invoke(); }
    }
}
