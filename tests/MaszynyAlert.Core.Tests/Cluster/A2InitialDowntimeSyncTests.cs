using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class A2InitialDowntimeSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A2_03_FirstDowntimeSyncBackfillsAnOlderGapEvenWhenALocalEpisodeIsRecent()
    {
        using var dir = new TempDir("audit-a2");
        var clock = new FakeClock(T0);
        var group = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = group.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        var missed = new DowntimeEpisode("aidite1", "Aidite 1", "Servo", T0.AddDays(-3), T0.AddDays(-3).AddMinutes(15),
            T0.AddDays(-3).AddMinutes(15), EpisodeId: Guid.NewGuid());
        aJournal.Merge(missed);
        bJournal.Merge(new DowntimeEpisode("aidite2", "Aidite 2", "Tool", T0.AddMinutes(-10), T0.AddMinutes(-1),
            T0.AddMinutes(-1), EpisodeId: Guid.NewGuid()));
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "test") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B"), clock, "test") { Downtime = bJournal };
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default);
        for (int second = 0; second < 700; second++)
        {
            await a.TickAsync(default); await b.TickAsync(default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Contains(bJournal.Snapshot(), episode => episode.EpisodeId == missed.EpisodeId);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(7, true)]
    [InlineData(7, false)]
    [InlineData(0, true)]
    public async Task FirstRequestUsesConfiguredWindowRegardlessOfLocalFreshness(int days, bool freshLocal)
    {
        await using var rig = new Rig(days);
        if (freshLocal) rig.Journal.Merge(Episode("local", T0.AddMinutes(-1)));
        await rig.Start();
        Assert.Equal(T0.AddDays(-Math.Max(1, days)), Assert.Single(rig.Requests).Since);
    }

    [Fact]
    public async Task UnansweredRequestsKeepTheFullWindowAndTenSecondRetry()
    {
        await using var rig = new Rig(7);
        rig.Journal.Merge(Episode("local", T0.AddMinutes(-1)));
        await rig.Start();
        rig.AdvanceAndHeartbeat(TimeSpan.FromMilliseconds(9_999));
        Assert.Single(rig.Requests);
        rig.AdvanceAndHeartbeat(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-7), rig.Requests[^1].Since);
        rig.AdvanceAndHeartbeat(TimeSpan.FromSeconds(10));
        Assert.Equal(3, rig.Requests.Count);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-7), rig.Requests[^1].Since);
    }

    [Theory]
    [InlineData(600_000)]
    [InlineData(120_000)]
    [InlineData(1_000)]
    public async Task EmptyAnswerRestoresExistingTailAndConfiguredInterval(int interval)
    {
        await using var rig = new Rig(interval: interval);
        var latest = T0.AddMinutes(-1);
        rig.Journal.Merge(Episode("local", latest));
        await rig.Start();
        // nawet pusta odpowiedź kończy początkowe pełne okno.
        rig.Reply();
        rig.AdvanceWithHeartbeats(TimeSpan.FromMilliseconds(Math.Max(10_000, interval) - 1));
        Assert.Single(rig.Requests);
        rig.AdvanceAndHeartbeat(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(latest.AddDays(-1), rig.Requests[^1].Since);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TailStillClampsToWindowForAnOldOrEmptyJournal(bool empty)
    {
        await using var rig = new Rig(7);
        if (!empty) rig.Journal.Merge(Episode("old-local", T0.AddDays(-20)));
        await rig.Start();
        rig.Reply();
        rig.AdvanceWithHeartbeats(TimeSpan.FromMinutes(10));
        Assert.Equal(rig.Clock.UtcNow.AddDays(-7), rig.Requests[^1].Since);
    }

    [Fact]
    public async Task NewPeerInstanceRestoresFullWindowWhileContinuousPeerRetainsTail()
    {
        await using var rig = new Rig(7);
        var latest = T0.AddMinutes(-1);
        rig.Journal.Merge(Episode("local", latest));
        await rig.Start();
        rig.Reply();
        rig.AdvanceWithHeartbeats(TimeSpan.FromMinutes(10));
        Assert.Equal(latest.AddDays(-1), rig.Requests[^1].Since);
        rig.Instance = Guid.NewGuid().ToString("N");
        rig.Sequence = 0;
        rig.Heartbeat();
        Assert.Equal(3, rig.Requests.Count);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-7), rig.Requests[^1].Since);
    }

    [Fact]
    public async Task LocalNodeRestartBackfillsAgainDespitePersistedFreshEpisode()
    {
        await using var rig = new Rig(7);
        rig.Journal.Merge(Episode("local", T0.AddMinutes(-1)));
        await rig.Start();
        rig.Reply();
        await rig.Node.DisposeAsync();
        await using var restarted = new GroupClusterNode(rig.Config, rig.Hub.CreateBus("B"), rig.Clock, "test")
            { Downtime = new DowntimeJournal(rig.Path) };
        await restarted.StartAsync(default);
        rig.Heartbeat();
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(T0.AddDays(-7), rig.Requests[^1].Since);
    }

    [Fact]
    public async Task DeliveredSeriesBackfillsManyChunksAndUsesLastObservedAtInclusiveBoundary()
    {
        using var dir = new TempDir("a2-window-boundary");
        var clock = new FakeClock(T0);
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var source = new DowntimeJournal(dir.File("source.jsonl"));
        var destination = new DowntimeJournal(dir.File("destination.jsonl"));
        destination.Merge(Episode("local", T0.AddMinutes(-1)));
        var entries = Enumerable.Range(0, 300).Select(i => Episode("machine-" + i, T0.AddDays(-89).AddSeconds(i))).ToArray();
        foreach (var entry in entries) source.Merge(entry);
        var boundary = Episode("boundary", T0.AddDays(-90)) with { StartedAt = T0.AddDays(-91) };
        var tooOld = Episode("outside", T0.AddDays(-90).AddTicks(-1));
        source.Merge(boundary); source.Merge(tooOld);
        var replies = new List<DowntimeSyncReply>();
        hub.CreateBus("sniffer").Received += m => { if (m is DowntimeSyncReply r && r.ReplyTo == "B") replies.Add(r); };
        var sourceBus = hub.CreateBus("A");
        var destinationBus = hub.CreateBus("B");
        await using var a = new GroupClusterNode(config, sourceBus, clock, "test") { Downtime = source };
        await using var b = new GroupClusterNode(config, destinationBus, clock, "test") { Downtime = destination };
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default);
        await b.TickAsync(default); // responder musi najpierw poznać odbiorcę
        await a.TickAsync(default);
        var received = destination.Snapshot().Select(e => e.EpisodeId).ToHashSet();
        Assert.All(entries, e => Assert.Contains(e.EpisodeId, received));
        Assert.Contains(boundary.EpisodeId, received);
        Assert.DoesNotContain(tooOld.EpisodeId, received);
        Assert.True(replies.Count > 1);
        Assert.All(replies, reply => Assert.True(MessageCodec.Encode(reply, hub.Signer).Length <= MessageCodec.MaxDatagramSizeBytes));
    }

    [Fact]
    public async Task SameInstanceReconnectBackfillsOnceThenContinuousHeartbeatsKeepTheTail()
    {
        await using var rig = new Rig(7);
        var latest = T0.AddMinutes(-1);
        rig.Journal.Merge(Episode("local", latest));
        await rig.Start();
        rig.Reply();

        rig.AdvanceAndHeartbeat(TimeSpan.FromMilliseconds(rig.Config.PeerTimeoutMs));
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-7), rig.Requests[^1].Since);
        rig.Reply();

        rig.AdvanceWithHeartbeats(TimeSpan.FromMinutes(10));
        Assert.Equal(3, rig.Requests.Count);
        Assert.Equal(latest.AddDays(-1), rig.Requests[^1].Since);
    }

    [Fact]
    public async Task ReconnectDoesNotBypassTheTenSecondRequestMinimum()
    {
        await using var rig = new Rig(7, peerTimeout: 5_000);
        rig.Journal.Merge(Episode("local", T0.AddMinutes(-1)));
        await rig.Start();
        rig.Reply();

        rig.AdvanceAndHeartbeat(TimeSpan.FromSeconds(5));
        Assert.Single(rig.Requests);
        rig.AdvanceWithHeartbeats(TimeSpan.FromMilliseconds(4_999));
        Assert.Single(rig.Requests);
        rig.AdvanceAndHeartbeat(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-7), rig.Requests[^1].Since);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedHeartbeatDoesNotReviveDeadPeerOrRequestBackfill(bool replay)
    {
        await using var rig = new Rig(7);
        rig.Journal.Merge(Episode("local", T0.AddMinutes(-1)));
        await rig.Start();
        rig.Reply();
        var before = Assert.Single(rig.Node.Peers, p => p.Host == "A");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(rig.Config.PeerTimeoutMs));
        rig.RejectedHeartbeat(replay);

        Assert.Equal(before, Assert.Single(rig.Node.Peers, p => p.Host == "A"));
        Assert.False(Assert.Single(rig.Node.Peers, p => p.Host == "A").IsAlive(rig.Clock.UtcNow,
            TimeSpan.FromMilliseconds(rig.Config.PeerTimeoutMs)));
        Assert.Single(rig.Requests);
        rig.Heartbeat();
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-7), rig.Requests[^1].Since);
    }

    private static DowntimeEpisode Episode(string machine, DateTimeOffset observed) =>
        new(machine, machine, "Servo", observed.AddMinutes(-2), observed, observed, EpisodeId: Guid.NewGuid());

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDir _dir = new("a2-initial-window");
        public readonly FakeClock Clock = new(T0);
        public readonly ClusterConfig Config;
        public readonly InMemoryPeerHub Hub;
        public readonly InMemoryPeerBus Peer;
        public readonly GroupClusterNode Node;
        public readonly DowntimeJournal Journal;
        public readonly List<DowntimeSyncRequest> Requests = [];
        public string Instance = Guid.NewGuid().ToString("N");
        public long Sequence;
        public string Path => _dir.File("journal.jsonl");

        public Rig(int days = 90, int interval = 600_000, int peerTimeout = 15_000)
        {
            var group = GroupFileTests.Invitation();
            Config = new() { Enabled = true, Group = group.Identity, DowntimeSyncWindowDays = days, DowntimeSyncIntervalMs = interval, PeerTimeoutMs = peerTimeout };
            Hub = new(HmacSigner.FromBase64(group.NetworkKey), Clock);
            Peer = Hub.CreateBus("A");
            Peer.Received += m => { if (m is DowntimeSyncRequest request) Requests.Add(request); };
            Journal = new(Path);
            Node = new(Config, Hub.CreateBus("B"), Clock, "test") { Downtime = Journal };
        }

        public async Task Start() { await Node.StartAsync(default); Heartbeat(); }
        public void AdvanceAndHeartbeat(TimeSpan elapsed) { Clock.Advance(elapsed); Heartbeat(); }
        public void AdvanceWithHeartbeats(TimeSpan elapsed)
        {
            while (elapsed > TimeSpan.Zero)
            {
                var step = elapsed > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : elapsed;
                AdvanceAndHeartbeat(step);
                elapsed -= step;
            }
        }
        public void RejectedHeartbeat(bool replay) => Hub.Dispatch(Peer, MessageCodec.Encode(new HeartbeatMessage
        {
            Sender = "A", Version = "test", Role = PeerRole.Watcher,
            Readiness = replay ? new(true, false, []) : null!,
            Group = Config.Group, InstanceId = Instance, Seq = replay ? 1 : ++Sequence, SentAt = Clock.UtcNow,
        }, Hub.Signer));
        public void Heartbeat() => Deliver(new HeartbeatMessage { Sender = "A", Version = "test", Role = PeerRole.Watcher, Readiness = new(true, false, []) });
        public void Reply() => Deliver(new DowntimeSyncReply { Sender = "A", Version = "test", ReplyTo = "B", RequestId = Requests[^1].RequestId, Episodes = [] });
        private void Deliver(PeerMessage message) => Hub.Dispatch(Peer, MessageCodec.Encode(message with
            { Group = Config.Group, InstanceId = Instance, Seq = ++Sequence, SentAt = Clock.UtcNow }, Hub.Signer));
        public async ValueTask DisposeAsync() { await Node.DisposeAsync(); await Peer.DisposeAsync(); _dir.Dispose(); }
    }
}
