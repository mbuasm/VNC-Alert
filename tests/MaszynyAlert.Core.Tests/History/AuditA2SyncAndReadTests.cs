using System.Globalization;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class AuditA2SyncAndReadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
    private static DowntimeEpisode Episode(string machine, DateTimeOffset from, DateTimeOffset to) =>
        new(machine, machine, "Servo", from, to, to, EpisodeId: Guid.NewGuid(), Category: "inny");

    [Fact]
    public void A2_04_DowntimeReadFailureMustNotReplaceANewerPersistedEpisodeWithAStaleCopy()
    {
        using var dir = new TempDir("audit-a2");
        var path = dir.File("downtime.jsonl");
        var complete = Episode("aidite1", T0, T0.AddMinutes(10));
        new DowntimeJournal(path).Merge(complete);
        DowntimeJournal reloading;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            reloading = new DowntimeJournal(path);
            Assert.NotNull(reloading.Error);
            Assert.Empty(reloading.Snapshot());
        }
        var stale = complete with { LastObservedAt = T0.AddMinutes(5), EndedAt = T0.AddMinutes(5) };
        reloading.Apply([stale]); // Test trwałości nie narzuca wartości zwracanej po doczytaniu pliku.
        reloading.Flush();

        var restarted = Assert.Single(new DowntimeJournal(path).Snapshot());
        Assert.Equal(complete.End, restarted.End);
    }

    [Fact]
    public async Task A2_03_ReconnectBackfillsTheOfflineGapAfterANewLocalObservation()
    {
        using var dir = new TempDir("audit-a2");
        var clock = new FakeClock(T0);
        var group = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = group.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "0.4.148") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B"), clock, "main") { Downtime = bJournal };
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default);
        await Tick(a, b, clock, 25);
        hub.Partition("A", "B");
        var missed = Episode("aidite1", T0.AddHours(1), T0.AddHours(2));
        aJournal.Merge(missed);
        clock.Advance(TimeSpan.FromDays(3));
        bJournal.Merge(Episode("aidite2", clock.UtcNow.AddMinutes(-2), clock.UtcNow));
        hub.Heal();
        await Tick(a, b, clock, 700);

        Assert.Contains(bJournal.Snapshot(), e => e.EpisodeId == missed.EpisodeId);
    }

    [Theory]
    [InlineData("0.4.134", "0.4.148")]
    [InlineData("0.4.134", "main")]
    [InlineData("0.4.148", "main")]
    public async Task VersionLabelsDoNotBlockInitialBackfillToAnEmptyJournal(string sourceVersion, string receiverVersion)
    {
        // To jest symulacja wersji w niezmienionym protokole, nie uruchomienie starego EXE.
        using var dir = new TempDir("audit-a2");
        var clock = new FakeClock(T0);
        var group = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = group.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        var missed = Episode("aidite1", T0.AddDays(-3), T0.AddDays(-3).AddMinutes(10));
        aJournal.Merge(missed);
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, sourceVersion) { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B"), clock, receiverVersion) { Downtime = bJournal };
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default);
        await Tick(a, b, clock, 25);

        Assert.Contains(bJournal.Snapshot(), e => e.EpisodeId == missed.EpisodeId);
        var peer = Assert.Single(b.Peers, p => p.Host == "A");
        Assert.True(GroupPeerStatus.Cooperates(peer));
        Assert.Contains("inna wersja programu", GroupPeerStatus.Describe(peer, receiverVersion));
    }

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
    public void ConcurrentCallsOnOneJournalPreserveAllIndependentRecords()
    {
        using var dir = new TempDir("audit-a2");
        var observations = new ObservationJournal(dir.File("observation.jsonl"));
        var downtime = new DowntimeJournal(dir.File("downtime.jsonl"));
        var history = new HistoryWriter(dir.File("history.csv"), new FakeClock(T0));
        Parallel.For(0, 64, i =>
        {
            var machine = "aidite" + i.ToString(CultureInfo.InvariantCulture);
            observations.Apply([new ObservationInterval(machine, ObservationState.Observed, T0, T0.AddMinutes(10))]);
            downtime.Merge(Episode(machine, T0, T0.AddMinutes(10)));
            var alert = new Alert { Key = AlertKey.ForRule(machine, "servo"), MachineId = machine, MachineName = machine,
                Kind = AlertKind.Error, ShortText = "Servo", Category = "inny", StartedAt = T0, LastSeenAt = T0 };
            history.Append(new AlertEvent(AlertEventKind.Raised, alert));
        });
        downtime.Flush(); observations.CloseAll(); history.Flush();
        Assert.Equal(64, new ObservationJournal(dir.File("observation.jsonl")).Snapshot().Count);
        Assert.Equal(64, new DowntimeJournal(dir.File("downtime.jsonl")).Snapshot().Count);
        Assert.Equal(65, File.ReadAllLines(dir.File("history.csv")).Length);
    }

    [Theory]
    [InlineData(-301, false)]
    [InlineData(-300, true)]
    [InlineData(120, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void ClockSkewAtTheConfiguredBoundaryIsExplicitlyAcceptedOrRejected(int seconds, bool expected)
    {
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        var message = new HeartbeatMessage { Sender = "A", Version = "main", Seq = 1, SentAt = T0.AddSeconds(seconds) };
        var accepted = MessageCodec.TryDecode(MessageCodec.Encode(message, signer), signer, new FakeClock(T0),
            TimeSpan.FromSeconds(new ClusterConfig().ClockSkewToleranceSec), out _, out var rejection);
        Assert.Equal(expected, accepted);
        if (!expected) Assert.Contains("sprawdź zegary komputerów", rejection);
    }

    [Fact]
    public void AcceptedTwoMinuteClockSkewWidensTheUtcUnionOfSimultaneousObservations()
    {
        // Dowód ograniczenia: system przyjmuje rozjazd 2 min, nie rekonstruuje wspólnego czasu rzeczywistego.
        using var dir = new TempDir("audit-a2");
        var clockA = new FakeClock(T0);
        var clockB = new FakeClock(T0.AddMinutes(2));
        var a = new ObservationJournal(dir.File("a.jsonl"));
        var b = new ObservationJournal(dir.File("b.jsonl"));
        for (int second = 0; second <= 600; second++)
        {
            a.Observe([("aidite1", ObservationState.Observed)], clockA.UtcNow);
            b.Observe([("aidite1", ObservationState.Observed)], clockB.UtcNow);
            clockA.Advance(TimeSpan.FromSeconds(1)); clockB.Advance(TimeSpan.FromSeconds(1));
        }
        a.CloseAll(); b.CloseAll();
        a.Apply(b.Snapshot());
        var merged = Assert.Single(a.Snapshot());
        Assert.Equal(TimeSpan.FromMinutes(12), merged.End - merged.Start);
        var stamp = ObservationTime.Read(clockA);
        clockA.UtcNow = clockA.UtcNow.AddMinutes(-5);
        clockA.JumpMonotonic(1000);
        Assert.Equal(TimeSpan.FromSeconds(1), stamp.Age(clockA));
    }

    private static async Task Tick(GroupClusterNode a, GroupClusterNode b, FakeClock clock, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            await a.TickAsync(default); await b.TickAsync(default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }
}
