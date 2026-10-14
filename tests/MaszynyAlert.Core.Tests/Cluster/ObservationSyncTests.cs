using System.Text;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class ObservationSyncTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPeerHub _hub;
    private readonly GroupFile _file;
    private readonly ClusterConfig _config;
    private readonly List<GroupClusterNode> _nodes = [];
    private readonly Dictionary<string, InMemoryPeerBus> _buses = [];
    private readonly List<TempDir> _dirs = [];
    private readonly List<PeerMessage> _sniffed = [];

    public ObservationSyncTests()
    {
        _file = GroupFileTests.Invitation();
        _config = new ClusterConfig { Enabled = true, Group = _file.Identity };
        _hub = new InMemoryPeerHub(HmacSigner.FromBase64(_file.NetworkKey), _clock, lossSeed: 20261005);
        var sniffer = _hub.CreateBus("S");
        sniffer.Received += m => { lock (_sniffed) _sniffed.Add(m); };
    }

    private GroupClusterNode Node(string host, int priority, ObservationJournal? journal, ClusterConfig? config = null)
    {
        var bus = _hub.CreateBus(host, config ?? _config);
        _buses[host] = bus;
        var node = new GroupClusterNode((config ?? _config) with { Priority = priority }, bus, _clock, "test");
        node.SetReadiness(new(true, false, []));
        node.Observation = journal;
        _nodes.Add(node);
        return node;
    }

    private ObservationJournal Journal()
    {
        var dir = new TempDir("observation-sync");
        _dirs.Add(dir);
        return new ObservationJournal(dir.File("observation.jsonl"));
    }

    private async Task Start()
    {
        foreach (var node in _nodes) await node.StartAsync(default);
        await Tick(23);
    }

    private async Task Tick(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            foreach (var node in _nodes.ToArray()) await node.TickAsync(default);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private T[] Sniffed<T>(Func<T, bool>? filter = null) where T : PeerMessage
    {
        lock (_sniffed) return _sniffed.OfType<T>().Where(m => filter?.Invoke(m) ?? true).ToArray();
    }

    private ObservationInterval Past(string machine, double minutesAgo, double lengthMinutes = 5,
        ObservationState state = ObservationState.Observed)
    {
        var start = _clock.UtcNow - TimeSpan.FromMinutes(minutesAgo);
        return new(machine, state, start, start + TimeSpan.FromMinutes(lengthMinutes));
    }

    private Task SendFrom(string host, PeerMessage message) => _buses[host].SendAsync(message, default);

    private ObservationSyncReply Reply(string from, string to, IReadOnlyList<ObservationInterval> intervals,
        Guid? requestId = null, bool more = false, DateTimeOffset? cursor = null) => new()
    {
        Sender = from, Version = "test", Group = _config.Group, RequestId = requestId ?? Guid.NewGuid(),
        ReplyTo = to, Intervals = intervals, More = more, Cursor = cursor,
    };

    private ObservationSyncRequest Request(string from, string? to, DateTimeOffset since) => new()
    {
        Sender = from, Version = "test", Group = _config.Group, RequestId = Guid.NewGuid(), To = to, Since = since,
    };

    // ── Podstawowa wymiana ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PeerGetsTheObserversPastIntervalsSoonAfterTheFirstHeartbeat()
    {
        var aJournal = Journal(); var bJournal = Journal();
        var a = Node("A", 100, aJournal); var b = Node("B", 200, bJournal);
        var past = new[] { Past("aidite1", 300, 60), Past("aidite2", 200, 30, ObservationState.NoConnection) };
        aJournal.Apply(past);
        int changed = 0;
        b.ObservationChanged += () => changed++;

        foreach (var node in _nodes) await node.StartAsync(default);
        await Tick(12); // prośba po heartbeacie zgodnego partnera, pierwsza bywa stracona — druga po 10 s

        Assert.Equal(aJournal.Snapshot(), bJournal.Snapshot());
        Assert.True(changed > 0);
        // Odbiorca zapisał je też na dysk.
        Assert.Equal(2, bJournal.Snapshot().Count);
    }

    [Fact]
    public async Task ExchangeIsSymmetricAndRepeatedSyncNeverChangesTheSum()
    {
        var aJournal = Journal(); var bJournal = Journal();
        Node("A", 100, aJournal); Node("B", 200, bJournal);
        aJournal.Apply([Past("aidite1", 300, 60)]);
        bJournal.Apply([Past("aidite1", 270, 60), Past("aidite3", 100, 10)]); // nakłada się z A

        await Start();
        await Tick(700); // kolejne prośby co 10 min

        Assert.Equal(aJournal.Snapshot(), bJournal.Snapshot());
        Assert.Equal(TimeSpan.FromMinutes(90),
            ObservationReport.Summarize(bJournal.Snapshot(), "aidite1", _clock.UtcNow.AddDays(-1), _clock.UtcNow).Observed);
    }

    [Fact]
    public async Task TwoObserversDuringASplitNeverCountTheSameTimeTwice()
    {
        // Podział sieci: A i B obserwowali naraz tę samą maszynę (każdy u siebie), potem sieć wraca.
        var aJournal = Journal(); var bJournal = Journal();
        Node("A", 100, aJournal); Node("B", 200, bJournal);
        var t0 = _clock.UtcNow - TimeSpan.FromHours(3);
        for (int s = 0; s <= 3600; s++)
        {
            aJournal.Observe([("aidite1", ObservationState.Observed)], t0 + TimeSpan.FromSeconds(s));
            if (s >= 1800) bJournal.Observe([("aidite1", ObservationState.Observed)], t0 + TimeSpan.FromSeconds(s + 0.5));
        }
        aJournal.CloseAll(); bJournal.CloseAll();

        await Start();
        await Tick(5);

        foreach (var journal in new[] { aJournal, bJournal })
        {
            var summary = ObservationReport.Summarize(journal.Snapshot(), "aidite1", t0, _clock.UtcNow);
            Assert.Equal(TimeSpan.FromSeconds(3600.5), summary.Observed);
        }
    }

    [Fact]
    public async Task LargeJournalArrivesInSignedPortionsWithinLimitsAndCompletely()
    {
        var aJournal = Journal(); var bJournal = Journal();
        Node("A", 100, aJournal); Node("B", 200, bJournal);
        var origin = _clock.UtcNow - TimeSpan.FromDays(3);
        var data = Enumerable.Range(0, 30_000).Select(i => new ObservationInterval("aidite" + (i % 9 + 1),
            ObservationState.Observed, origin + TimeSpan.FromSeconds(8 * i), origin + TimeSpan.FromSeconds(8 * i + 3))).ToArray();
        aJournal.Apply(data);
        Assert.Equal(30_000, aJournal.Snapshot().Count);

        await Start();
        await Tick(400);

        Assert.Equal(30_000, bJournal.Snapshot().Count);
        var replies = Sniffed<ObservationSyncReply>(r => r.Sender == "A" && r.ReplyTo == "B");
        Assert.All(replies, r => Assert.InRange(r.Intervals.Count, 0, 256));
        Assert.All(replies, r => Assert.InRange(MessageCodec.Encode(r, _hub.Signer).Length, 1, MessageCodec.MaxDatagramSizeBytes));
        Assert.All(replies.GroupBy(r => r.RequestId), series => Assert.InRange(series.Count(), 1, 50));
        Assert.True(replies.GroupBy(r => r.RequestId).Count() >= 3, "30 000 przedziałów to co najmniej trzy serie");
        // Ostatnia porcja serii uciętej limitem mówi „jest dalej”.
        Assert.Contains(replies, r => r.More && r.Cursor is not null);
        // Odbiorca ma to też na dysku.
        Assert.Equal(30_000, new ObservationJournal(_dirs[1].File("observation.jsonl")).Snapshot().Count);
    }

    [Fact]
    public async Task OpenIntervalOfTheObserverKeepsGrowingAtThePeer()
    {
        var aJournal = Journal(); var bJournal = Journal();
        Node("A", 100, aJournal, _config with { DowntimeSyncIntervalMs = 60_000 });
        Node("B", 200, bJournal, _config with { DowntimeSyncIntervalMs = 60_000 });
        foreach (var node in _nodes) await node.StartAsync(default);
        var start = _clock.UtcNow;
        for (int s = 0; s < 200; s++)
        {
            aJournal.Observe([("aidite1", ObservationState.Observed)], _clock.UtcNow);
            await Tick(1);
        }

        var atB = Assert.Single(bJournal.Snapshot());
        Assert.Equal(start, atB.Start);
        Assert.True(atB.End > start + TimeSpan.FromSeconds(100), $"koniec u B: {atB.End:O}");
        Assert.True(atB.End <= aJournal.LatestEnd);
    }

    // ── Adresat i limit serii ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestAddressedToAnotherComputerIsIgnored()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        bJournal.Apply([Past("aidite1", 60)]);
        await Start();

        await SendFrom("A", Request("A", "C", _clock.UtcNow.AddDays(-1)));
        await SendFrom("A", Request("A", null, _clock.UtcNow.AddDays(-1)));
        await Tick(1);
        Assert.Empty(Sniffed<ObservationSyncReply>(r => r.Sender == "B"));

        await SendFrom("A", Request("A", "B", _clock.UtcNow.AddDays(-1)));
        await Tick(1);
        Assert.Single(Sniffed<ObservationSyncReply>(r => r.Sender == "B" && r.ReplyTo == "A"));
    }

    [Fact]
    public async Task SecondRequestFromTheSameComputerWithinSixtySecondsGetsNoReply()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        bJournal.Apply([Past("aidite1", 60)]);
        await Start();

        var first = Request("A", "B", _clock.UtcNow.AddDays(-1));
        await SendFrom("A", first);
        await Tick(30);
        await SendFrom("A", Request("A", "B", _clock.UtcNow.AddDays(-1)));
        await Tick(29);
        await SendFrom("A", Request("A", "B", _clock.UtcNow.AddDays(-1)));
        Assert.Equal([first.RequestId], Sniffed<ObservationSyncReply>(r => r.Sender == "B").Select(r => r.RequestId).Distinct());

        await Tick(2); // ponad 60 s od pierwszej
        await SendFrom("A", Request("A", "B", _clock.UtcNow.AddDays(-1)));
        Assert.Equal(2, Sniffed<ObservationSyncReply>(r => r.Sender == "B").Select(r => r.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task EmptyJournalAnswersWithOneEmptyFinalPortion()
    {
        Node("A", 100, null); Node("B", 200, Journal());
        await Start();

        var request = Request("A", "B", _clock.UtcNow.AddDays(-1));
        await SendFrom("A", request);

        var reply = Assert.Single(Sniffed<ObservationSyncReply>(r => r.Sender == "B"));
        Assert.Empty(reply.Intervals);
        Assert.False(reply.More);
        Assert.Null(reply.Cursor);
        Assert.Equal(request.RequestId, reply.RequestId);
    }

    [Fact]
    public async Task ReplyContainsOnlyIntervalsEndingAfterSince()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        bJournal.Apply([Past("old", 600, 10), Past("new", 30, 10)]);
        await Start();

        await SendFrom("A", Request("A", "B", _clock.UtcNow - TimeSpan.FromMinutes(100)));

        var reply = Assert.Single(Sniffed<ObservationSyncReply>(r => r.Sender == "B"));
        Assert.Equal(["new"], reply.Intervals.Select(i => i.MachineId));
    }

    // ── Walidacja odpowiedzi ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReplyWithMoreThan256IntervalsIsRejectedWhole()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        await Start();

        var many = Enumerable.Range(0, 257).Select(i => Past("aidite1", 2000 - 3 * i, 1)).ToArray();
        await SendFrom("A", Reply("A", "B", many));
        await Tick(1);
        Assert.Empty(bJournal.Snapshot());

        await SendFrom("A", Reply("A", "B", many.Take(256).ToArray()));
        await Tick(1);
        Assert.Equal(256, bJournal.Snapshot().Count);
    }

    [Fact]
    public async Task IntervalEndingAfterNowPlusClockToleranceIsRejectedAndTheRestAccepted()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        await Start();
        var now = _clock.UtcNow;

        await SendFrom("A", Reply("A", "B",
        [
            new("future", ObservationState.Observed, now, now + TimeSpan.FromSeconds(301)),
            new("edge", ObservationState.Observed, now, now + TimeSpan.FromSeconds(300)),
            new("ok", ObservationState.Observed, now - TimeSpan.FromHours(1), now),
        ]));

        Assert.Equal(["edge", "ok"], bJournal.Snapshot().Select(i => i.MachineId));
    }

    [Fact]
    public async Task HostileIntervalsFromAGroupMemberNeverEnterTheJournal()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        await Start();
        var now = _clock.UtcNow;
        ObservationInterval[] rubbish =
        [
            new("aidite1", ObservationState.Observed, now, now - TimeSpan.FromMinutes(5)),         // start > end
            new("aidite1", ObservationState.Observed, now, now),                                   // chwila
            new("", ObservationState.Observed, now - TimeSpan.FromMinutes(5), now),                // brak maszyny
            new("test", ObservationState.Observed, now - TimeSpan.FromMinutes(5), now),            // maszyna testowa
            new(new string('x', 101), ObservationState.Observed, now - TimeSpan.FromMinutes(5), now),
            new("aidite1", ObservationState.None, now - TimeSpan.FromMinutes(5), now),
            new("aidite1", (ObservationState)9, now - TimeSpan.FromMinutes(5), now),
            new("aidite1", ObservationState.Observed, default, now),                                 // brak daty startu
            new("aidite1", ObservationState.Observed, DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            new("aidite1", ObservationState.Observed, now, now + TimeSpan.FromDays(3650)),          // przyszłość
        ];
        foreach (var bad in rubbish)
        {
            await SendFrom("A", Reply("A", "B", [bad]));
            await Tick(1);
        }

        Assert.Empty(bJournal.Snapshot());
        Assert.Null(bJournal.Error);
    }

    [Fact]
    public async Task HugeIntervalAndForeignMachineFromAGroupMemberAreAccepted_DocumentsCurrentBehaviour()
    {
        // Nie błąd sam w sobie (komputer z kluczem grupy jest zaufany), ale granica walidacji: przedział od
        // 2020-01-01 do teraz i maszyna spoza konfiguracji przechodzą, choć proszący pytał o okno 90 dni.
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        await Start();
        var now = _clock.UtcNow;

        await SendFrom("A", Reply("A", "B",
        [
            new("aidite1", ObservationState.Observed, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), now),
            new("maszyna-spoza-konfiguracji", ObservationState.NoConnection, now - TimeSpan.FromHours(1), now),
        ]));

        Assert.Equal(2, bJournal.Snapshot().Count);
    }

    [Fact]
    public async Task ReplyAddressedToAnotherComputerIsIgnored()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        await Start();

        await SendFrom("A", Reply("A", "C", [Past("aidite1", 60)]));
        await SendFrom("A", Reply("A", null!, [Past("aidite1", 60)]));
        await Tick(1);

        Assert.Empty(bJournal.Snapshot());
    }

    [Fact]
    public async Task ReplyFromAComputerThatNeverSentAHeartbeatIsIgnored()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        _buses["X"] = _hub.CreateBus("X");
        await Start();

        await SendFrom("X", Reply("X", "B", [Past("aidite1", 60)]));
        await Tick(1);

        Assert.Empty(bJournal.Snapshot());
    }

    [Fact]
    public async Task ReplyWithNullIntervalsListOrNullEntriesIsHandled()
    {
        Node("A", 100, null); var bJournal = Journal(); Node("B", 200, bJournal);
        await Start();

        await SendFrom("A", Reply("A", "B", null!));
        await SendFrom("A", Reply("A", "B", [null!, Past("aidite1", 60)]));
        await Tick(1);

        Assert.Single(bJournal.Snapshot());
    }

    [Fact]
    public async Task NoObservationToApplyWhenTheNodeHasNoJournal()
    {
        var b = Node("B", 200, null); Node("A", 100, null);
        int changed = 0; b.ObservationChanged += () => changed++;
        await Start();

        await SendFrom("A", Reply("A", "B", [Past("aidite1", 60)]));
        await Tick(1);

        Assert.Equal(0, changed);
    }

    // ── Seria, kursor, stary RequestId ───────────────────────────────────────────────────

    [Fact]
    public async Task StaleRequestIdAddsIntervalsButNeverMovesTheSeriesThenCursorAndMarginWork()
    {
        // A nie odpowiada sam (bez dziennika); odpowiedzi „od A” wysyłamy ręcznie i patrzymy na prośby B.
        var interval = _config with { DowntimeSyncIntervalMs = 120_000 };
        Node("A", 100, null, interval); var bJournal = Journal(); Node("B", 200, bJournal, interval);
        await Start();
        var window = TimeSpan.FromDays(_config.DowntimeSyncWindowDays);
        ObservationSyncRequest[] BRequests() => Sniffed<ObservationSyncRequest>(r => r.Sender == "B" && r.To == "A");
        int asked = BRequests().Length;
        Assert.True(asked >= 1);

        // 1) Odpowiedź ze starym RequestId: przedziały przyjęte, ale kursor nie — kolejna prośba bez kursora.
        var cursor = _clock.UtcNow - TimeSpan.FromDays(10);
        await SendFrom("A", Reply("A", "B", [Past("aidite1", 60)], Guid.NewGuid(), more: true, cursor: cursor));
        Assert.Single(bJournal.Snapshot());
        await Tick(125);
        var next = BRequests()[asked..];
        var afterStale = Assert.Single(next);
        Assert.NotEqual(cursor, afterStale.Since);
        Assert.Equal(afterStale.SentAt - window, afterStale.Since);
        asked = BRequests().Length;

        // 2) Odpowiedź na NASZĄ ostatnią prośbę z More + kursor: dalszy ciąg po 65 s, od kursora.
        await SendFrom("A", Reply("A", "B", [], afterStale.RequestId, more: true, cursor: cursor));
        await Tick(70);
        var continuation = Assert.Single(BRequests()[asked..]);
        Assert.Equal(cursor, continuation.Since);
        Assert.InRange((continuation.SentAt - afterStale.SentAt).TotalSeconds, 65, 66);
        asked = BRequests().Length;

        // 3) Ostatnia porcja serii (bez More): kolejna prośba po odstępie, od początku serii minus doba.
        await SendFrom("A", Reply("A", "B", [], continuation.RequestId));
        await Tick(125);
        var resync = Assert.Single(BRequests()[asked..]);
        Assert.Equal(afterStale.SentAt - TimeSpan.FromDays(1), resync.Since);
    }

    [Fact]
    public async Task SecondRequestAfterACompleteSeriesStartsOneDayBeforeThatSeries()
    {
        var interval = _config with { DowntimeSyncIntervalMs = 120_000 };
        var aJournal = Journal();
        Node("A", 100, aJournal, interval); Node("B", 200, Journal(), interval);
        aJournal.Apply([Past("aidite1", 60)]);
        await Start();
        await Tick(130);

        var requests = Sniffed<ObservationSyncRequest>(r => r.Sender == "B" && r.To == "A");
        var answered = Sniffed<ObservationSyncReply>(r => r.Sender == "A" && r.ReplyTo == "B").Select(r => r.RequestId).ToHashSet();
        var firstAnswered = requests.First(r => answered.Contains(r.RequestId));
        var after = requests.Where(r => r.SentAt > firstAnswered.SentAt).ToArray();

        var nextRequest = Assert.Single(after);
        Assert.Equal(firstAnswered.SentAt - TimeSpan.FromDays(1), nextRequest.Since);
        Assert.InRange((nextRequest.SentAt - firstAnswered.SentAt).TotalSeconds, 119, 121);
    }

    // ── Partner, który nie odpowiada (0.4.113) ──────────────────────────────────────────────

    [Fact]
    public async Task PartnerThatNeverAnswersGetsSixQuickRequestsThenOnePerSyncInterval()
    {
        var interval = _config with { DowntimeSyncIntervalMs = 120_000 };
        Node("A", 100, null, interval); // jak 0.4.113: nie zna komunikatu, nie odpowiada
        Node("B", 200, Journal(), interval);
        foreach (var node in _nodes) await node.StartAsync(default);
        await Tick(420);

        var times = Sniffed<ObservationSyncRequest>(r => r.Sender == "B" && r.To == "A").Select(r => r.SentAt).ToArray();
        var gaps = times.Zip(times.Skip(1), (x, y) => (y - x).TotalSeconds).ToArray();

        Assert.True(times.Length >= 8, $"prośby: {times.Length}");
        Assert.All(gaps.Take(5), g => Assert.Equal(10, g));
        Assert.All(gaps.Skip(5), g => Assert.Equal(120, g));
        // Każda prośba to pełne okno — nic nie zostało zsynchronizowane.
        Assert.All(Sniffed<ObservationSyncRequest>(r => r.Sender == "B"),
            r => Assert.Equal(r.SentAt - TimeSpan.FromDays(_config.DowntimeSyncWindowDays), r.Since));
    }

    [Fact]
    public async Task MixedGroupKeepsHeartbeatsPeerTableAndObserverChoiceUnchanged()
    {
        // Ten sam układ dwa razy: bez dziennika na żadnym komputerze i z dziennikiem tylko na B
        // (nowa wersja obok 0.4.113). Rola, tabela komputerów i zgodność muszą wyjść identycznie.
        async Task<(bool AWatcher, bool BWatcher, string?, string[])> Run(bool newOnB)
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
            var hub = new InMemoryPeerHub(HmacSigner.FromBase64(_file.NetworkKey), clock);
            await using var a = new GroupClusterNode(_config, hub.CreateBus("A"), clock, "0.4.113");
            await using var b = new GroupClusterNode(_config with { Priority = 200 }, hub.CreateBus("B"), clock, "nowa");
            a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
            if (newOnB) b.Observation = Journal();
            await a.StartAsync(default); await b.StartAsync(default);
            for (int i = 0; i < 120; i++) { await a.TickAsync(default); await b.TickAsync(default); clock.Advance(TimeSpan.FromSeconds(1)); }
            return (a.IsWatcher, b.IsWatcher, b.WatcherHost,
                b.Peers.Select(p => $"{p.Host}|{p.Role}|{p.Compatibility}|{p.BelievedWatcher}").Concat(
                    a.Peers.Select(p => $"{p.Host}|{p.Role}|{p.Compatibility}|{p.BelievedWatcher}")).ToArray());
        }

        var baseline = await Run(newOnB: false);
        var mixed = await Run(newOnB: true);

        Assert.True(baseline.AWatcher); Assert.False(baseline.BWatcher);
        Assert.Equal((baseline.AWatcher, baseline.BWatcher, baseline.Item3), (mixed.AWatcher, mixed.BWatcher, mixed.Item3));
        Assert.Equal(baseline.Item4, mixed.Item4);
    }

    // ── Restart partnera ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PartnerRestartTriggersAnImmediateRequestForTheWholeWindow()
    {
        var interval = _config with { DowntimeSyncIntervalMs = 120_000 };
        var aJournal = Journal();
        var a = Node("A", 100, aJournal, interval); Node("B", 200, Journal(), interval);
        aJournal.Apply([Past("aidite1", 60)]);
        await Start();
        await Tick(30); // pełna seria za nami — kolejna prośba dopiero za 120 s, od „seria − doba”

        // Restart A: nowy proces = nowy InstanceId, ten sam host.
        await a.DisposeAsync(); _nodes.Remove(a);
        var restarted = Node("A", 100, aJournal, interval);
        await restarted.StartAsync(default);
        var restartAt = _clock.UtcNow;
        await Tick(3);

        var request = Sniffed<ObservationSyncRequest>(r => r.Sender == "B" && r.To == "A" && r.SentAt >= restartAt).First();
        Assert.InRange((request.SentAt - restartAt).TotalSeconds, 0, 2);
        Assert.Equal(request.SentAt - TimeSpan.FromDays(_config.DowntimeSyncWindowDays), request.Since);
    }

    // ── Bez dziennika / niezgodny partner ───────────────────────────────────────────────────

    [Fact]
    public async Task NodeWithoutJournalNeitherAsksNorAnswers()
    {
        Node("A", 100, null); Node("B", 200, Journal());
        await Start();
        await Tick(60);

        Assert.Empty(Sniffed<ObservationSyncRequest>(r => r.Sender == "A"));
        Assert.NotEmpty(Sniffed<ObservationSyncRequest>(r => r.Sender == "B"));
        Assert.Empty(Sniffed<ObservationSyncReply>(r => r.Sender == "A"));
    }

    [Fact]
    public async Task IncompatiblePartnerExchangesNothing()
    {
        var aJournal = Journal(); var bJournal = Journal();
        Node("A", 100, aJournal);
        Node("B", 200, bJournal, _config with { Group = _file.Identity with { ProfileHash = new string('0', 64) } });
        aJournal.Apply([Past("aidite1", 60)]);
        bJournal.Apply([Past("aidite2", 60)]);
        await Start();
        await Tick(60);

        Assert.Empty(Sniffed<ObservationSyncRequest>());
        Assert.Empty(Sniffed<ObservationSyncReply>());
        Assert.Single(aJournal.Snapshot()); Assert.Single(bJournal.Snapshot());
    }

    [Fact]
    public async Task ExchangeContinuesAfterANetworkPartitionHeals()
    {
        var aJournal = Journal(); var bJournal = Journal();
        var interval = _config with { DowntimeSyncIntervalMs = 60_000 };
        Node("A", 100, aJournal, interval); Node("B", 200, bJournal, interval);
        await Start();
        _hub.Partition("A", "B");
        aJournal.Apply([Past("aidite1", 30)]);
        await Tick(40);
        Assert.Empty(bJournal.Snapshot());

        _hub.Heal();
        await Tick(90);
        Assert.Single(bJournal.Snapshot());
    }

    // ── Nieznany typ komunikatu (grupa mieszana z 0.4.113) ──────────────────────────────────

    /// <summary>Podpisany pakiet grupy z prawdziwego heartbeatu, w którym podmieniono wartość <c>type</c>.</summary>
    private byte[] UnknownTypePacket(string type, string sender = "X")
    {
        var heartbeat = new HeartbeatMessage
        {
            Sender = sender, Version = "9.9.9", Group = _config.Group, Seq = 1, SentAt = _clock.UtcNow,
            InstanceId = Guid.NewGuid().ToString("N"), Readiness = new(true, false, []), RecentAcks = [],
        };
        string json = Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<PeerMessage>(heartbeat, MessageCodec.JsonOptions));
        Assert.StartsWith("{\"type\":\"heartbeat\"", json);
        return _hub.Signer.Seal(Encoding.UTF8.GetBytes(json.Replace("\"type\":\"heartbeat\"", $"\"type\":\"{type}\"")));
    }

    [Fact]
    public async Task SignedPacketWithUnknownTypeIsRejectedWithoutTouchingPeersOrObserver()
    {
        var a = Node("A", 100, Journal()); var b = Node("B", 200, Journal());
        var x = _hub.CreateBus("X");
        await Start();
        int received = 0, rejected = 0;
        _buses["B"].Received += _ => received++;
        var reasons = new List<string>();
        _buses["B"].Rejected += reason => { rejected++; reasons.Add(reason); };
        var peersBefore = b.Peers.Select(p => (p.Host, p.Role, p.Compatibility)).ToArray();
        string? watcherBefore = b.WatcherHost;

        for (int i = 0; i < 20; i++)
        {
            _hub.Dispatch(x, UnknownTypePacket(i % 2 == 0 ? "observationFuture" : "somethingNew"));
            _hub.Dispatch(x, UnknownTypePacket("observationRequest2", sender: "A"));
        }

        Assert.Equal(0, received);
        Assert.Equal(40, rejected);
        Assert.All(reasons, r => Assert.StartsWith("niepoprawny JSON", r));
        Assert.Equal(peersBefore, b.Peers.Select(p => (p.Host, p.Role, p.Compatibility)).ToArray());
        Assert.Equal(watcherBefore, b.WatcherHost);
        Assert.True(a.IsWatcher);
        Assert.DoesNotContain(b.Peers, p => p.Host == "X");
    }

    [Fact]
    public void TryDecodeRejectsUnknownTypeAsInvalidJson()
    {
        bool ok = MessageCodec.TryDecode(UnknownTypePacket("observationRequest3"), _hub.Signer, _clock,
            TimeSpan.FromSeconds(300), out var message, out var reason);

        Assert.False(ok);
        Assert.Null(message);
        Assert.StartsWith("niepoprawny JSON", reason);
    }

    // ── Serializacja ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestAndReplySurviveEncodeAndDecodeWithTypeFirstAndStatesAsCamelCaseText()
    {
        var now = _clock.UtcNow;
        var request = Request("A", "B", now.AddDays(-3)) with { Seq = 7, SentAt = now, InstanceId = Guid.NewGuid().ToString("N") };
        var reply = Reply("B", "A",
        [
            new("aidite1", ObservationState.Observed, now.AddHours(-2), now.AddHours(-1)),
            new("aidite1", ObservationState.ScreenHidden, now.AddHours(-1), now.AddMinutes(-30)),
            new("aidite2", ObservationState.NoConnection, now.AddMinutes(-30), now),
        ], more: true, cursor: now) with { Seq = 8, SentAt = now, InstanceId = Guid.NewGuid().ToString("N") };

        foreach (PeerMessage original in new PeerMessage[] { request, reply })
        {
            byte[] datagram = MessageCodec.Encode(original, _hub.Signer);
            Assert.True(_hub.Signer.TryOpen(datagram, out byte[] payload, out _));
            string json = Encoding.UTF8.GetString(payload);
            Assert.StartsWith(original is ObservationSyncRequest ? "{\"type\":\"observationRequest\"" : "{\"type\":\"observationReply\"", json);

            Assert.True(MessageCodec.TryDecode(datagram, _hub.Signer, _clock, TimeSpan.FromSeconds(300), out var decoded, out var reason), reason);
            if (original is ObservationSyncReply sent)
            {
                Assert.Contains("\"state\":\"observed\"", json);
                Assert.Contains("\"state\":\"screenHidden\"", json);
                Assert.Contains("\"state\":\"noConnection\"", json);
                var got = Assert.IsType<ObservationSyncReply>(decoded);
                Assert.Equal(sent.Intervals, got.Intervals);
                Assert.Equal(sent with { Intervals = got.Intervals }, got);
            }
            else Assert.Equal(original, Assert.IsType<ObservationSyncRequest>(decoded));
        }
    }

    [Fact]
    public void ReplyWithUnknownStateIsRejectedWhole_DocumentsForwardCompatibility()
    {
        // Przyszła wersja z nowym stanem: obecna odrzuci CAŁĄ porcję (niepoprawny JSON), nie tylko ten wpis.
        var now = _clock.UtcNow;
        var reply = Reply("B", "A", [new("aidite1", ObservationState.Observed, now.AddHours(-1), now)])
            with { Seq = 1, SentAt = now, InstanceId = Guid.NewGuid().ToString("N") };
        Assert.True(_hub.Signer.TryOpen(MessageCodec.Encode(reply, _hub.Signer), out byte[] payload, out _));
        string json = Encoding.UTF8.GetString(payload).Replace("\"state\":\"observed\"", "\"state\":\"paused\"");

        Assert.False(MessageCodec.TryDecode(_hub.Signer.Seal(Encoding.UTF8.GetBytes(json)), _hub.Signer, _clock,
            TimeSpan.FromSeconds(300), out _, out var reason));
        Assert.StartsWith("niepoprawny JSON", reason);
    }

    [Fact]
    public void TenThousandIntervalsCannotEvenBeSentAsOnePacket()
    {
        var now = _clock.UtcNow;
        var huge = Reply("A", "B", Enumerable.Range(0, 10_000).Select(i =>
            new ObservationInterval("aidite1", ObservationState.Observed, now.AddSeconds(-3 * i - 2), now.AddSeconds(-3 * i))).ToArray())
            with { Seq = 1, SentAt = now, InstanceId = Guid.NewGuid().ToString("N") };

        Assert.Throws<InvalidDataException>(() => MessageCodec.Encode(huge, _hub.Signer));
        // Ręcznie zapieczętowany za duży pakiet odbiorca odrzuca przed parsowaniem.
        byte[] raw = _hub.Signer.Seal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<PeerMessage>(huge, MessageCodec.JsonOptions));
        Assert.False(MessageCodec.TryDecode(raw, _hub.Signer, _clock, TimeSpan.FromSeconds(300), out _, out var reason));
        Assert.Equal("pakiet przekracza limit UDP", reason);
    }

    // ── Rozmiar porcji UDP (MessageCodec.Chunk z nową listą) ────────────────────────────────────

    /// <summary>
    /// Porcje budowane jak w <c>GroupClusterNode.ObservationReplies</c> i ostemplowane tak, jak robi to bus
    /// (<c>InstanceId</c>, <c>Seq</c>, <c>SentAt</c>) — każda musi się zmieścić w datagramie.
    /// </summary>
    [Theory]
    [InlineData('a')]
    [InlineData('ą')] // znak spoza ASCII: System.Text.Json zapisuje go jako ą (6 B)
    [InlineData('<')] // znak „HTML”: też <
    public void EveryPortionFitsInOneDatagramAfterTheBusStampsIt(char letter)
    {
        var now = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        var failures = new List<string>();
        for (int length = 1; length <= 100; length++)
        {
            string machine = new(letter, length);
            var intervals = Enumerable.Range(0, 600).Select(i => new ObservationInterval(machine, ObservationState.NoConnection,
                now.AddSeconds(-10 * i - 5).AddTicks(1_234_567), now.AddSeconds(-10 * i).AddTicks(7_654_321))).ToArray();
            var chunks = MessageCodec.Chunk(intervals, (items, more) => new ObservationSyncReply
            {
                Sender = "AIDITEKOMP1", Version = "0.4.113-pr117", Group = _config.Group,
                RequestId = Guid.NewGuid(), ReplyTo = "DESKTOP-CD8GBG4", Intervals = [.. items], More = more,
                Cursor = items.Count > 0 ? items[^1].End : null,
            }, 256);
            foreach (var chunk in chunks)
            {
                var stamped = chunk with { Seq = 9_999_999, SentAt = now, InstanceId = Guid.NewGuid().ToString("N") };
                try { MessageCodec.Encode(stamped, _hub.Signer); }
                catch (InvalidDataException) { failures.Add($"długość {length}: {chunk.Intervals.Count} wpisów"); }
            }
        }

        Assert.Empty(failures);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes) await node.DisposeAsync();
        foreach (var dir in _dirs) dir.Dispose();
    }
}
