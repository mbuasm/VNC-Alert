using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class A2ObservationReplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A2_05_ReorderedObservationChunksDoNotPermanentlySkipTheEarlierHistory()
    {
        using var dir = new TempDir("audit-a2");
        var clock = new FakeClock(T0);
        var group = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = group.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), clock);
        var remote = hub.CreateBus("A");
        var receiver = hub.CreateBus("B");
        var journal = new ObservationJournal(dir.File("b.jsonl"));
        await using var b = new GroupClusterNode(config, receiver, clock, "main") { Observation = journal };
        b.SetReadiness(new(true, false, []));
        var requests = new List<ObservationSyncRequest>();
        var instance = Guid.NewGuid().ToString("N");
        long wireSequence = 30;
        var old = new ObservationInterval("aidite1", ObservationState.Observed, T0.AddDays(-3), T0.AddDays(-3).AddMinutes(10));
        var recent = new ObservationInterval("aidite2", ObservationState.Observed, T0.AddMinutes(-10), T0.AddMinutes(-1));
        var later = new ObservationInterval("aidite3", ObservationState.Observed, T0.AddSeconds(1), T0.AddSeconds(2));
        bool laterRequests = false;
        void Deliver(PeerMessage message, long sequence) => hub.Dispatch(remote, MessageCodec.Encode(message with
        {
            Sender = "A", Version = "0.4.148", Group = group.Identity, Seq = sequence, InstanceId = instance, SentAt = clock.UtcNow,
        }, hub.Signer));
        remote.Received += message =>
        {
            if (message is not ObservationSyncRequest request || request.To != "A") return;
            requests.Add(request);
            if (laterRequests)
                Deliver(new ObservationSyncReply { Sender = "A", Version = "0.4.148",
                    ReplyTo = "B", RequestId = request.RequestId,
                    Intervals = new[] { old, recent, later }.Where(i => i.End >= request.Since).ToArray(), More = false, Cursor = recent.End,
                }, ++wireSequence);
        };
        await b.StartAsync(default);
        Deliver(new HeartbeatMessage { Sender = "A", Version = "0.4.148", Role = PeerRole.Watcher, Readiness = new(true, false, []) }, 1);
        var initial = Assert.Single(requests);
        // Obie porcje zostały wysłane; sieć dostarcza ostatnią (seq 30) przed pierwszą (seq 29).
        Deliver(new ObservationSyncReply { Sender = "A", Version = "0.4.148", RequestId = initial.RequestId, ReplyTo = "B", Intervals = [recent], More = false, Cursor = recent.End }, 30);
        Deliver(new ObservationSyncReply { Sender = "A", Version = "0.4.148", RequestId = initial.RequestId, ReplyTo = "B", Intervals = [old], More = true, Cursor = old.End }, 29);
        laterRequests = true;
        for (int second = 0; second < 700; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await b.TickAsync(default);
            Deliver(new HeartbeatMessage { Sender = "A", Version = "0.4.148", Role = PeerRole.Watcher, Readiness = new(true, false, []) }, ++wireSequence);
        }
        Assert.True(requests.Count >= 2);
        Assert.Contains(later, journal.Snapshot()); // dowod: odpowiedz na kolejna prosbe zostala przyjeta
        Assert.Contains(old, journal.Snapshot());
    }

    [Fact]
    public async Task SeveralFullSeriesCanArriveBackwardsDespiteGlobalSequenceGapsAndOtherRecipients()
    {
        await using var rig = new Rig(); await rig.Start();
        for (int series = 0; series < 3; series++)
        {
            var started = rig.Clock.UtcNow;
            long first = (series + 1) * 1_000_000L;
            var final = rig.Reply(first + 49_000, machine: $"s{series}-49");
            // Seq jest globalne: heartbeat i odpowiedź dla C wyprzedzają spóźnione porcje dla B.
            rig.Heartbeat(first + 100_000);
            rig.Reply(first + 100_001, recipient: "C", machine: "foreign");
            for (int chunk = 48; chunk >= 0; chunk--)
                rig.Reply(first + chunk * 1_000, more: true, cursor: T0.AddDays(-3).AddSeconds(chunk), machine: $"s{series}-{chunk}");
            rig.Replay(final);
            Assert.Equal((series + 1) * 50, rig.Sync.Applied.Count);
            Assert.Equal((series + 1) * 50, rig.Changed);
            rig.Advance(TimeSpan.FromSeconds(65));
            Assert.Equal(series + 1, rig.Requests.Count); // starsze More nie otwiera zamkniętej serii
            rig.Advance(TimeSpan.FromSeconds(535));
            Assert.Equal(series + 2, rig.Requests.Count);
            Assert.Equal(started.AddDays(-1), rig.Requests[^1].Since);
        }
    }

    [Fact]
    public async Task DuplicateDatagramsNeverReachApplyButUnseenEarlierDatagramDoes()
    {
        await using var rig = new Rig(); await rig.Start();
        var final = rig.Reply(30, machine: "final");
        var earlier = rig.Reply(29, more: true, cursor: T0.AddDays(-3), machine: "earlier");
        rig.Replay(earlier); rig.Replay(final);
        rig.Reply(29, machine: "changed-payload-same-seq");
        Assert.Equal(new[] { "final", "earlier" }, rig.Sync.Applied.Select(x => Assert.Single(x).MachineId));
        Assert.Equal(2, rig.Changed);
    }

    [Fact]
    public async Task EarlierMoreDoesNotMoveTheCurrentCursorBackwards()
    {
        await using var rig = new Rig(); await rig.Start();
        var cursor = T0.AddDays(-2);
        rig.Reply(30, more: true, cursor: cursor);
        rig.Reply(29, more: true, cursor: T0.AddDays(-3));
        rig.Advance(TimeSpan.FromSeconds(65));
        Assert.Equal(2, rig.Sync.Applied.Count);
        Assert.Equal(cursor, rig.Requests[^1].Since);
    }

    [Fact]
    public async Task StaleRequestDataCannotPoisonProgressOfTheNewRequestEvenWithHigherSequence()
    {
        await using var rig = new Rig(); await rig.Start();
        var first = rig.Requests[^1].RequestId;
        rig.Reply(10, more: true, cursor: T0.AddDays(-2));
        rig.Advance(TimeSpan.FromSeconds(65));
        Assert.Equal(2, rig.Requests.Count);
        var second = rig.Requests[^1].RequestId;
        rig.Reply(1_000, request: first, machine: "stale-final");
        rig.Reply(900, request: second, machine: "current-final");
        rig.Reply(899, request: second, more: true, cursor: T0.AddDays(-4), machine: "current-earlier");
        Assert.Equal(4, rig.Sync.Applied.Count);
        rig.Advance(TimeSpan.FromSeconds(65));
        Assert.Equal(2, rig.Requests.Count);
        rig.Advance(TimeSpan.FromSeconds(535));
        Assert.Equal(3, rig.Requests.Count);
        Assert.Equal(T0.AddDays(-1), rig.Requests[^1].Since);
    }

    [Fact]
    public async Task WindowRetains256AcceptedNumbersAndItsEvictionFloorNeverMovesBackwards()
    {
        await using var rig = new Rig(); await rig.Start();
        for (int i = 1; i <= 256; i++) rig.Reply(i * 1_000L);
        Assert.Equal(256, rig.Sync.Applied.Count);
        rig.Reply(500); // niewidziana starsza porcja mieści się przed pierwszym wyrzuceniem
        Assert.Equal(257, rig.Sync.Applied.Count);
        rig.Reply(500); rig.Reply(499);
        Assert.Equal(257, rig.Sync.Applied.Count);
        rig.Reply(257_000); // wyrzuca zapamiętane 1000 i podnosi dolną granicę
        rig.Reply(1_000); rig.Reply(501);
        Assert.Equal(258, rig.Sync.Applied.Count);
        rig.Reply(1_500); // luka pomiędzy Seq nadal dozwolona; nie liczymy różnicy numerów
        rig.Reply(1_500); rig.Reply(1_000);
        Assert.Equal(259, rig.Sync.Applied.Count);
        rig.Reply(1_000_000_000); // następne wyrzucenie: granica 2000
        rig.Reply(1_501); rig.Reply(2_000);
        Assert.Equal(260, rig.Sync.Applied.Count);
        rig.Advance(TimeSpan.FromMinutes(10)); // zwykła nowa prośba nie czyści ochrony
        rig.Reply(1_000); rig.Reply(1_500); rig.Reply(2_000);
        Assert.Equal(260, rig.Sync.Applied.Count);
    }

    [Fact]
    public async Task OtherRecipientsCannotFillOrAdvanceTheReplayWindow()
    {
        await using var rig = new Rig(); await rig.Start();
        rig.Reply(10);
        for (int i = 0; i < 300; i++) rig.Reply(1_000_000 + i, recipient: "C");
        rig.Reply(9);
        Assert.Equal(2, rig.Sync.Applied.Count);
        rig.Reply(10); rig.Reply(9);
        Assert.Equal(2, rig.Sync.Applied.Count);
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("oversized-list")]
    [InlineData("other-group")]
    [InlineData("other-profile")]
    [InlineData("unknown-instance")]
    [InlineData("bad-hmac")]
    [InlineData("expired")]
    [InlineData("dead-peer")]
    [InlineData("sync-disabled")]
    public async Task RejectedEnvelopeCannotAdvanceTheObservationReplayWindow(string rejection)
    {
        await using var rig = new Rig(); await rig.Start();
        var reply = rig.Answer(machine: "must-not-apply");
        string? instance = null;
        DateTimeOffset? sent = null;
        switch (rejection)
        {
            case "null-list": reply = reply with { Intervals = null! }; break;
            case "oversized-list": reply = reply with { Intervals = Enumerable.Repeat(Interval("x"), 257).ToArray() }; break;
            case "other-group": reply = reply with { Group = rig.Config.Group! with { GroupId = Guid.NewGuid() } }; break;
            case "other-profile": reply = reply with { Group = rig.Config.Group! with { ProfileRevision = 2 } }; break;
            case "unknown-instance": instance = Guid.NewGuid().ToString("N"); break;
            case "expired": sent = T0.AddSeconds(-301); break;
            case "dead-peer": rig.Clock.Advance(TimeSpan.FromSeconds(16)); break;
            case "sync-disabled": rig.Node.Observation = null; break;
        }
        for (int i = 0; i < 300; i++)
            rig.Deliver(reply, 1_000 + i, instance, sent, corruptHmac: rejection == "bad-hmac");
        Assert.Empty(rig.Sync.Applied);
        if (rejection == "dead-peer") rig.Heartbeat();
        if (rejection == "sync-disabled") rig.Node.Observation = rig.Sync;
        rig.Reply(2, machine: "valid-earlier");
        Assert.Equal("valid-earlier", Assert.Single(Assert.Single(rig.Sync.Applied)).MachineId);
    }

    [Fact]
    public async Task NewInstanceResetsOnlyItsWindowAndRetiredInstanceRemainsBlocked()
    {
        await using var rig = new Rig(); await rig.Start();
        string retired = rig.Instance;
        for (int i = 1; i <= 257; i++) rig.Reply(i * 1_000L);
        rig.Instance = Guid.NewGuid().ToString("N");
        rig.Reply(2); // nowa instancja przed heartbeatem jest obca
        Assert.Equal(257, rig.Sync.Applied.Count);
        rig.Heartbeat(1);
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(T0.AddDays(-90), rig.Requests[^1].Since);
        rig.Reply(2);
        Assert.Equal(258, rig.Sync.Applied.Count);
        rig.Deliver(rig.Answer(), 300_000, instance: retired);
        rig.Deliver(Rig.HeartbeatMessage(), 300_001, instance: retired);
        rig.Reply(3);
        Assert.Equal(259, rig.Sync.Applied.Count);
        Assert.Equal(rig.Instance, Assert.Single(rig.Node.Peers, p => p.Host == "A").InstanceId);
    }

    [Fact]
    public async Task StateAndAcknowledgementsKeepStrictSequenceChecks()
    {
        await using var rig = new Rig(); await rig.Start();
        int states = 0, acknowledgements = 0;
        rig.Node.EpisodeStateReceived += (_, _) => states++;
        rig.Node.AckReceived += _ => acknowledgements++;
        var state = new StateMessage { Sender = "A", Version = "test" };
        rig.Deliver(state, 100);
        rig.Deliver(state, 99); rig.Deliver(state, 100);
        rig.Reply(10_000);
        rig.Deliver(state, 101);
        Assert.Equal(2, states);
        AckMessage Ack() => new() { Sender = "A", Version = "test", Key = new("aidite1", "servo"),
            EpisodeId = Guid.NewGuid(), User = "operator", AckedAt = rig.Clock.UtcNow };
        rig.Deliver(Ack(), 200);
        rig.Deliver(Ack(), 199); rig.Deliver(Ack(), 200);
        rig.Deliver(Ack(), 201);
        Assert.Equal(2, acknowledgements);
    }

    [Fact]
    public async Task SequenceMemoryAndInstanceResetAreIsolatedBetweenPeers()
    {
        await using var rig = new Rig(); await rig.Start();
        for (int i = 1; i <= 257; i++) rig.Reply(i * 1_000L);
        string instanceC = Guid.NewGuid().ToString("N");
        rig.Deliver(Rig.HeartbeatMessage() with { Sender = "C" }, 1, instance: instanceC);
        var fromC = rig.Answer(machine: "peer-c") with { Sender = "C", RequestId = Guid.NewGuid() };
        rig.Deliver(fromC, 2, instance: instanceC);
        rig.Reply(1_000); // nowy peer C nie resetuje dolnej granicy A
        Assert.Equal(258, rig.Sync.Applied.Count);
        rig.Instance = Guid.NewGuid().ToString("N");
        rig.Heartbeat(1); rig.Reply(2);
        rig.Deliver(fromC, 2, instance: instanceC); // restart A nie otwiera replay dla C
        Assert.Equal(259, rig.Sync.Applied.Count);
    }
    private static ObservationInterval Interval(string machine) => new(machine, ObservationState.Observed, T0.AddDays(-3), T0.AddDays(-3).AddMinutes(1));

    private sealed class CountingSync : IObservationSync
    {
        public readonly List<IReadOnlyList<ObservationInterval>> Applied = [];
        public DateTimeOffset? LatestEnd => Applied.SelectMany(x => x).Select(x => (DateTimeOffset?)x.End).Max();
        public IReadOnlyList<ObservationInterval> IntervalsSince(DateTimeOffset since, int limit) => [];
        public bool Apply(IReadOnlyList<ObservationInterval> intervals) { Applied.Add(intervals); return true; }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new(T0);
        public readonly CountingSync Sync = new();
        public readonly GroupClusterNode Node;
        public readonly InMemoryPeerHub Hub;
        public readonly InMemoryPeerBus Peer;
        public readonly ClusterConfig Config;
        public readonly List<ObservationSyncRequest> Requests = [];
        public string Instance = Guid.NewGuid().ToString("N");
        private long _nextSequence;
        public int Changed;

        public Rig()
        {
            var group = GroupFileTests.Invitation();
            Config = new() { Enabled = true, Group = group.Identity };
            Hub = new(HmacSigner.FromBase64(group.NetworkKey), Clock);
            Peer = Hub.CreateBus("A");
            Peer.Received += m => { if (m is ObservationSyncRequest r && r.To == "A") Requests.Add(r); };
            Node = new(Config, Hub.CreateBus("B"), Clock, "test") { Observation = Sync };
            Node.ObservationChanged += () => Changed++;
        }
        public async Task Start() { await Node.StartAsync(default); Heartbeat(); }
        public void Advance(TimeSpan span) { Clock.Advance(span); Heartbeat(); }
        public static HeartbeatMessage HeartbeatMessage() => new()
            { Sender = "A", Version = "test", Role = PeerRole.Watcher, Readiness = new(true, false, []) };
        public void Heartbeat(long? sequence = null) => Deliver(HeartbeatMessage(), sequence ?? ++_nextSequence);
        public ObservationSyncReply Answer(Guid? request = null, bool more = false, DateTimeOffset? cursor = null,
            string machine = "machine", string recipient = "B") => new()
            { Sender = "A", Version = "test", Group = Config.Group, RequestId = request ?? Requests[^1].RequestId,
              ReplyTo = recipient, More = more, Cursor = cursor, Intervals = [Interval(machine)] };
        public byte[] Reply(long sequence, Guid? request = null, bool more = false, DateTimeOffset? cursor = null,
            string machine = "machine", string recipient = "B") => Deliver(Answer(request, more, cursor, machine, recipient), sequence);
        public byte[] Deliver(PeerMessage message, long sequence, string? instance = null, DateTimeOffset? sent = null, bool corruptHmac = false)
        {
            _nextSequence = Math.Max(_nextSequence, sequence);
            var bytes = MessageCodec.Encode(message with { Group = message.Group ?? Config.Group,
                InstanceId = instance ?? Instance, Seq = sequence, SentAt = sent ?? Clock.UtcNow }, Hub.Signer);
            if (message is ObservationSyncReply { Intervals: null })
            {
                // Encode pomija null; tutaj potrzebny jest jawny null z obcego JSON, nie pusta lista.
                var json = System.Text.Json.Nodes.JsonNode.Parse(bytes.AsSpan(HmacSigner.SealOverheadBytes))!;
                json["intervals"] = null;
                bytes = Hub.Signer.Seal(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
            }
            if (corruptHmac) bytes[4] ^= 1;
            Hub.Dispatch(Peer, bytes); return bytes;
        }
        public void Replay(byte[] bytes) => Hub.Dispatch(Peer, bytes);
        public async ValueTask DisposeAsync() { await Node.DisposeAsync(); await Peer.DisposeAsync(); }
    }
}
