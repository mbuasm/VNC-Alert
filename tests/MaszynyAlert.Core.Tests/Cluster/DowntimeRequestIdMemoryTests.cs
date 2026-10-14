using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using Xunit.Abstractions;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class DowntimeRequestIdMemoryTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task SixtyFourPartnersRestartingEveryMinute_RememberedRequestsNeverExceedTheCap_AndExpireAfterTheLifetime()
    {
        await using var crowd = await Crowd.StartAsync();
        var hosts = Enumerable.Range(1, 64).Select(i => $"P{i:00}").ToArray();
        int max = 0;
        // 30 min: każdy partner co 5 s heartbeat, co 60 s restart (nowa instancja = znowu pełne okno); nikt nie odpowiada.
        for (int second = 0; second < 30 * 60; second++)
        {
            crowd.Clock.Advance(Second);
            for (int i = 0; i < hosts.Length; i++)
            {
                if ((second + i) % 60 == 0) crowd.Restart(hosts[i]);
                if ((second + i) % 5 == 0) crowd.Heartbeat(hosts[i]);
            }
            if (second % 5 == 0) await crowd.Node.TickAsync(default);
            max = Math.Max(max, crowd.Node.RememberedFullDowntimeRequests);
        }
        int sent = crowd.Requests.Count;
        Assert.True(sent > 10 * GroupClusterNode.MaxRememberedFullDowntimeRequests, $"Tylko {sent} próśb — za mało, by sprawdzić limit.");
        Assert.Equal(GroupClusterNode.MaxRememberedFullDowntimeRequests, max);

        // Wszyscy odpowiadają na swoją ostatnią prośbę: pełne okna się kończą.
        foreach (string host in hosts)
        {
            crowd.Heartbeat(host);
            crowd.Reply(host, crowd.Requests.Last(r => r.Trigger == host).Request.RequestId);
        }
        Assert.All(hosts, host => Assert.NotNull(crowd.SyncedAt(host)));
        int afterAnswers = crowd.Node.RememberedFullDowntimeRequests;
        int answerPhaseRequests = crowd.Requests.Count;

        // 11 min stanu ustalonego (ogon co 10 min, odpowiedzi): nowych wpisów nie przybywa.
        for (int second = 0; second < 11 * 60; second++)
        {
            crowd.Clock.Advance(Second);
            for (int i = 0; i < hosts.Length; i++)
                if ((second + i) % 5 == 0)
                {
                    int before = crowd.Requests.Count;
                    crowd.Heartbeat(hosts[i]);
                    if (crowd.Requests.Count > before) crowd.Reply(hosts[i], crowd.Requests[^1].Request.RequestId);
                }
            if (second % 5 == 0) await crowd.Node.TickAsync(default);
        }
        int afterLifetime = crowd.Node.RememberedFullDowntimeRequests;
        Assert.True(crowd.Requests.Count > answerPhaseRequests, "W stanie ustalonym nie poszła żadna prośba ogona.");
        Assert.DoesNotContain(crowd.Requests.Skip(answerPhaseRequests), r => crowd.IsFull(r.Request));
        Assert.True(afterLifetime <= afterAnswers, $"Przybyło wpisów w stanie ustalonym: {afterAnswers} → {afterLifetime}.");

        // Restart jednego partnera = nowa prośba o pełne okno; wszystkie wpisy starsze niż 10 min znikają przy jej zapisie.
        crowd.Restart("P01");
        crowd.Heartbeat("P01");
        output.WriteLine($"Pamięć próśb: max {max}, po odpowiedziach {afterAnswers}, po 11 min stanu ustalonego {afterLifetime} "
            + $"(czas życia egzekwowany przy zapisie i sprawdzeniu), po nowej prośbie {crowd.Node.RememberedFullDowntimeRequests}; próśb {sent}.");
        Assert.Equal(1, crowd.Node.RememberedFullDowntimeRequests);
    }

    [Fact]
    public async Task HundredsOfDistinctComputersComingAndGoing_MemoryAndPeerTableStayBounded()
    {
        await using var crowd = await Crowd.StartAsync();
        int maxRemembered = 0, maxPeers = 0, appeared = 0;
        var present = new List<(string Host, DateTimeOffset Until)>();
        // 12 h: co 2 min pojawia się nowy komputer, pracuje 20 min (heartbeat co 5 s), nie odpowiada na prośby i znika.
        for (int second = 0; second < 12 * 60 * 60; second++)
        {
            crowd.Clock.Advance(Second);
            if (second % 120 == 0) present.Add(($"K{++appeared:000}", crowd.Clock.UtcNow + TimeSpan.FromMinutes(20)));
            present.RemoveAll(p => p.Until <= crowd.Clock.UtcNow);
            if (second % 5 == 0)
            {
                foreach (var (host, _) in present) crowd.Heartbeat(host);
                await crowd.Node.TickAsync(default);
                maxRemembered = Math.Max(maxRemembered, crowd.Node.RememberedFullDowntimeRequests);
                maxPeers = Math.Max(maxPeers, crowd.Node.Peers.Count - 1);
            }
        }
        // Po ponad 2 h ciszy wszyscy wypadają z tabeli; nowy komputer = jedna zapamiętana prośba.
        present.Clear();
        crowd.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        await crowd.Node.TickAsync(default);
        Assert.Single(crowd.Node.Peers);
        crowd.Heartbeat("NOWY");
        output.WriteLine($"{appeared} różnych komputerów w 12 h: max zapamiętanych próśb {maxRemembered}, max partnerów w tabeli {maxPeers}, "
            + $"próśb {crowd.Requests.Count}.");
        Assert.True(appeared >= 300);
        Assert.InRange(maxRemembered, 1, GroupClusterNode.MaxRememberedFullDowntimeRequests);
        Assert.InRange(maxPeers, 1, 64);
        Assert.Equal(1, crowd.Node.RememberedFullDowntimeRequests);
    }

    /// <summary>
    /// <c>cluster.downtimeSyncWindowDays = 1</c>: ogon (najnowszy wpis − doba) jest zawsze przycięty do okna, więc KAŻDA prośba jest
    /// prośbą o pełne okno i trafia do pamięci. Stan ustalony nadal: jedna prośba na partnera co 10 min, pamięć — kilka wpisów.
    /// </summary>
    [Fact]
    public async Task WindowOfOneDay_EveryRequestIsAFullWindowRequest_SteadyStateKeepsTheTenMinuteInterval()
    {
        await using var lab = new DowntimeRequestIdLabTests.Lab(new ClusterConfig { DowntimeSyncWindowDays = 1 });
        var history = new DowntimeEpisode("hist", "Maszyna hist", "Servo 6", T0 - TimeSpan.FromDays(3), T0 - TimeSpan.FromDays(3) + TimeSpan.FromMinutes(20),
            T0 - TimeSpan.FromDays(3) + TimeSpan.FromMinutes(20), EpisodeId: Guid.NewGuid(), Origin: "B", Category: "restart");
        await lab.AddAsync("A", 100, 0, history);
        await lab.AddAsync("B", 10, 0, history);
        await lab.AddAsync("C", 200, 0, history);
        await lab.AddAsync("D", 150, 0, history);
        await lab.RunAsync(TimeSpan.FromMinutes(30), Second);
        long start = lab.Mark();
        int maxRemembered = 0;
        lab.AfterStep = () => maxRemembered = Math.Max(maxRemembered, lab.Pcs.Values.Max(pc => pc.Node.RememberedFullDowntimeRequests));
        await lab.RunAsync(TimeSpan.FromHours(2), TimeSpan.FromSeconds(5));

        var measured = lab.Log.Where(s => s.Index > start && s.Message is DowntimeSyncRequest).ToArray();
        foreach (string host in lab.Pcs.Keys)
        {
            var mine = measured.Where(s => s.From == host).Select(s => (DowntimeSyncRequest)s.Message).ToArray();
            Assert.Equal(36, mine.Length);
            Assert.All(mine, r => Assert.Equal(r.SentAt - TimeSpan.FromDays(1), r.Since));
        }
        output.WriteLine($"Okno 1 doby: max zapamiętanych próśb na komputerze {maxRemembered}.");
        Assert.InRange(maxRemembered, 1, 12);
    }

    /// <summary>
    /// Jeden prawdziwy węzeł „A”; dowolnie wielu partnerów nadaje przez JEDNĄ wspólną magistralę (pole Sender w datagramie mówi, kto),
    /// więc prośba A jest dekodowana raz, nie 64 razy. Koperta, Seq, HMAC i okno czasu jak w UDP.
    /// </summary>
    private sealed class Crowd : IAsyncDisposable
    {
        private readonly TempDir _dir = new("test-requestid-crowd");
        private readonly Dictionary<string, (string Instance, long Seq)> _peers = new(StringComparer.Ordinal);
        private readonly InMemoryPeerBus _partners;
        private string? _trigger;

        private Crowd()
        {
            var group = GroupFileTests.Invitation();
            Config = new ClusterConfig { Enabled = true, Group = group.Identity };
            Hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), Clock);
            _partners = Hub.CreateBus("PARTNERZY", Config);
            _partners.Received += m =>
            {
                if (m is DowntimeSyncRequest request && request.Sender == "A") Requests.Add((Clock.UtcNow, _trigger, request));
            };
            Journal = new DowntimeJournal(_dir.File("a.jsonl"));
            // Świeży wpis: ogon (najnowszy wpis − doba) różni się od pełnego okna (90 dni).
            Journal.Merge(new DowntimeEpisode("aidite7", "Aidite 7", "Servo 6", T0.AddMinutes(-3), T0.AddMinutes(-1), T0.AddMinutes(-1),
                EpisodeId: Guid.NewGuid(), Origin: "A", Category: "restart"));
            Node = new GroupClusterNode(Config, Hub.CreateBus("A", Config), Clock, "test") { Downtime = Journal };
        }

        public FakeClock Clock { get; } = new(T0);
        public ClusterConfig Config { get; }
        public InMemoryPeerHub Hub { get; }
        public GroupClusterNode Node { get; }
        public DowntimeJournal Journal { get; }
        public List<(DateTimeOffset At, string? Trigger, DowntimeSyncRequest Request)> Requests { get; } = [];

        public static async Task<Crowd> StartAsync()
        {
            var crowd = new Crowd();
            crowd.Node.SetReadiness(new(true, false, []));
            await crowd.Node.StartAsync(default);
            return crowd;
        }

        public bool IsFull(DowntimeSyncRequest request) => request.Since == request.SentAt - TimeSpan.FromDays(Config.DowntimeSyncWindowDays);

        public DateTimeOffset? SyncedAt(string host) => Node.Peers.Single(p => p.Host == host).DowntimeSyncedAt;

        public void Restart(string host) => _peers[host] = (Guid.NewGuid().ToString("N"), 0);

        public void Heartbeat(string host)
        {
            _trigger = host;
            try { Deliver(host, new HeartbeatMessage { Sender = host, Version = "test", Role = PeerRole.Ui, Readiness = new(false, false, []) }); }
            finally { _trigger = null; }
        }

        public void Reply(string host, Guid requestId) =>
            Deliver(host, new DowntimeSyncReply { Sender = host, Version = "test", ReplyTo = "A", RequestId = requestId, Episodes = [] });

        private void Deliver(string host, PeerMessage message)
        {
            if (!_peers.TryGetValue(host, out var peer)) peer = (Guid.NewGuid().ToString("N"), 0);
            _peers[host] = peer = (peer.Instance, peer.Seq + 1);
            Hub.Dispatch(_partners, MessageCodec.Encode(message with
                { Group = Config.Group, InstanceId = peer.Instance, Seq = peer.Seq, SentAt = Clock.UtcNow }, Hub.Signer));
        }

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            _dir.Dispose();
        }
    }
}
