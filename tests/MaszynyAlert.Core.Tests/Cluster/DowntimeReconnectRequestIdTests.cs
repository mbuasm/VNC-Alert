using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class DowntimeReconnectRequestIdTests
{
    // Wtorek 06:00 UTC: cały scenariusz (doba podziału + 2 h) leży poza weekendem raportu (sob 06:00 – pon 06:00).
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(90); // domyślne cluster.downtimeSyncWindowDays
    private static readonly TimeSpan SplitLength = TimeSpan.FromHours(26);


    [Fact]
    public async Task ReplyToTailRequestTriggeredByAnotherPeer_DoesNotEndReconnectBackfill_OutageOlderThanADayArrives()
    {
        // B (obserwator, sam z maszynami) był odcięty od A i C przez 26 h. Wraca 3 s przed kolejną prośbą ogona A do C.
        var outcome = await RunSplitAsync(healSecondsBeforeTailRequest: 3);

        // Warunki scenariusza: B zna dane z podziału, a jego pierwsza odpowiedź do A po
        // powrocie łączności dotyczyła prośby ogona (Since = najnowszy wpis A − 1 doba), nie prośby o pełne okno.
        Assert.Contains(outcome.B, e => e.EpisodeId == outcome.OutageOld.EpisodeId);
        Assert.NotNull(outcome.FirstAnsweredByB);
        Assert.False(outcome.FirstAnsweredWasFullWindow,
            $"Pierwsza odpowiedź B dotyczyła pełnego okna (Since {outcome.FirstAnsweredByB!.Since:u}) — scenariusz się nie odtworzył.");
        // Ogon przyniósł świeższą część przerwy…
        Assert.Contains(outcome.A, e => e.EpisodeId == outcome.OutageRecent.EpisodeId);
        Assert.True(outcome.A.Any(e => e.EpisodeId == outcome.OutageOld.EpisodeId),
            $"A po {(outcome.End - outcome.HealAt).TotalHours:0.#} h od powrotu łączności nie ma postoju B sprzed "
            + $"{(outcome.HealAt - outcome.OutageOld.LastObservedAt).TotalHours:0.##} h (podział {SplitLength.TotalHours:0} h). "
            + $"Raport postojów za okres podziału: A {outcome.ReportA.Rows} wierszy / {outcome.ReportA.Minutes:0.#} min, "
            + $"B {outcome.ReportB.Rows} wierszy / {outcome.ReportB.Minutes:0.#} min. Pierwsza odpowiedź B po powrocie łączności: "
            + $"prośba ogona (Since {outcome.FirstAnsweredByB!.Since:u}); prośby A o pełne okno po powrocie łączności "
            + $"(sekundy od powrotu): [{string.Join(", ", outcome.FullRequestOffsets.Select(s => s.TotalSeconds.ToString("0")))}]. "
            + $"C {(outcome.CHasOutageOld ? "MA" : "nie ma")} tego postoju — prośby A do C to ogon po LastObservedAt.");
    }

    [Fact]
    public async Task WhenTheFullWindowRequestIsAnsweredFirst_OutageOlderThanADayArrives()
    {
        // Kontrola tej samej sytuacji: B wraca 30 s PO prośbie ogona A do C — pierwszą odpowiedzią B jest odpowiedź na
        // ponowioną prośbę o pełne okno (co 10 s), więc A dostaje całą przerwę. Wynik zależy od kolejności pakietów.
        var outcome = await RunSplitAsync(healSecondsBeforeTailRequest: 570);

        Assert.True(outcome.FirstAnsweredWasFullWindow);
        Assert.Contains(outcome.A, e => e.EpisodeId == outcome.OutageOld.EpisodeId);
        Assert.Contains(outcome.A, e => e.EpisodeId == outcome.OutageRecent.EpisodeId);
        Assert.Equal(outcome.ReportB.Minutes, outcome.ReportA.Minutes, 6);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2. Dosłowny scenariusz (dwa komputery): odpowiedź B na prośbę ogona sprzed przerwy dociera dopiero po
    //    heartbeacie, który otworzył pełne okno, a nowa prośba o pełne okno ginie (UDP).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StaleReplyToARequestFromBeforeTheOutage_DoesNotEndReconnectBackfill()
    {
        var outcome = await RunLiteralAsync(outage: TimeSpan.FromSeconds(20), observeAfter: TimeSpan.FromMinutes(2));

        // Spóźniona odpowiedź (Seq wyższy od ostatniej przyjętej, SentAt 20 s wstecz) przechodzi wszystkie filtry.
        Assert.True(outcome.StaleAccepted, "Spóźniona odpowiedź nie została przyjęta — scenariusz się nie odtworzył.");
        Assert.Empty(outcome.RejectsAtA);
        // Odpowiedź na prośbę sprzed przerwy nie jest odpowiedzią na pełne okno: A ma dalej o nie prosić (co 10 s).
        Assert.True(outcome.RequestsAfterStale.Any(r => IsFullWindow(r.Request)),
            $"W {outcome.ObservedFor.TotalSeconds:0} s po przyjęciu spóźnionej odpowiedzi A nie poprosił o pełne okno; "
            + $"próśb: {outcome.RequestsAfterStale.Count}, pierwsza po "
            + (outcome.RequestsAfterStale.Count == 0 ? "—" : $"{(outcome.RequestsAfterStale[0].At - outcome.StaleDeliveredAt).TotalSeconds:0} s")
            + ". Uzupełnianie od B po powrocie łączności zakończyła odpowiedź na prośbę ogona sprzed przerwy.");
    }

    [Fact]
    public async Task StaleReplyOlderThanClockSkewTolerance_IsRejectedByTransport_AndFullWindowRetryContinues()
    {
        // Dlaczego dosłowny scenariusz nie dotyczy przerwy dłuższej niż doba: odpowiedź sprzed przerwy starsza niż
        // cluster.clockSkewToleranceSec (domyślnie 300 s) odpada w MessageCodec.TryDecode, zanim dotrze do węzła.
        var outcome = await RunLiteralAsync(outage: TimeSpan.FromSeconds(310), observeAfter: TimeSpan.FromMinutes(2));

        Assert.False(outcome.StaleAccepted);
        Assert.Contains(outcome.RejectsAtA, r => r.Contains("spoza okna czasowego", StringComparison.Ordinal));
        Assert.Contains(outcome.RequestsAfterStale, r => IsFullWindow(r.Request));
        Assert.True(outcome.OutageArrived);
    }

    [Fact]
    public async Task StaleReplyWithinClockSkewTolerance_OutageShorterThanADayStillArrivesWithTheNextTailRequest()
    {
        // Skutek dosłownego scenariusza dla samej przerwy: mieści się ona w 300 s, więc zawsze jest w dobowym ogonie —
        // następna zwykła prośba (po cluster.downtimeSyncIntervalMs = 10 min) ją przynosi. Opóźnienie, nie utrata.
        var outcome = await RunLiteralAsync(outage: TimeSpan.FromSeconds(20), observeAfter: TimeSpan.FromMinutes(11));

        Assert.True(outcome.StaleAccepted);
        Assert.True(outcome.OutageArrived, "Postój z przerwy nie dotarł w 11 min po przyjęciu spóźnionej odpowiedzi.");
        Assert.True(outcome.OutageArrivedAfter <= TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30),
            $"Postój z przerwy dotarł po {outcome.OutageArrivedAfter}.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Scenariusz 1: trzy prawdziwe węzły.
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record ReportTotals(int Rows, double Minutes);

    private sealed record SplitOutcome(
        DowntimeEpisode OutageOld, DowntimeEpisode OutageRecent,
        IReadOnlyList<DowntimeEpisode> A, IReadOnlyList<DowntimeEpisode> B,
        DateTimeOffset HealAt, DateTimeOffset End,
        DowntimeSyncRequest? FirstAnsweredByB, bool FirstAnsweredWasFullWindow, IReadOnlyList<TimeSpan> FullRequestOffsets,
        ReportTotals ReportA, ReportTotals ReportB, bool CHasOutageOld);

    /// <summary>
    /// A (priorytet 100) i C (200) pracują razem; B (10, obserwator z dostępem do maszyn) jest od nich odcięty przez
    /// <see cref="SplitLength"/> i w tym czasie zapisuje dwa postoje. Powrót łączności następuje
    /// <paramref name="healSecondsBeforeTailRequest"/> s przed kolejną prośbą ogona, którą A wysyła z powodu heartbeatu C
    /// (co <c>cluster.downtimeSyncIntervalMs</c> = 10 min). W sekundzie powrotu kolejność: B, A, C (heartbeat B dociera do A,
    /// zanim heartbeat A dotrze do B — przy losowych fazach to połowa przypadków).
    /// </summary>
    private static async Task<SplitOutcome> RunSplitAsync(int healSecondsBeforeTailRequest)
    {
        using var dir = new TempDir("wer-grupa-requestid");
        var clock = new FakeClock(T0);
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        var cJournal = new DowntimeJournal(dir.File("c.jsonl"));
        var history = Episode("aidite4", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        aJournal.Merge(history); bJournal.Merge(history); cJournal.Merge(history);
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "test") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 10 }, hub.CreateBus("B"), clock, "test") { Downtime = bJournal };
        await using var c = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("C"), clock, "test") { Downtime = cJournal };
        // Replikacja na żywo ze STATE obserwatora — ta sama reguła, co AppOrchestrator.OnRemoteEpisodesReceived.
        a.EpisodeStateReceived += (alerts, closures) => Replicate(aJournal, alerts, closures, a.WatcherHost);
        b.EpisodeStateReceived += (alerts, closures) => Replicate(bJournal, alerts, closures, b.WatcherHost);
        c.EpisodeStateReceived += (alerts, closures) => Replicate(cJournal, alerts, closures, c.WatcherHost);

        var requestsFromA = new Dictionary<Guid, DowntimeSyncRequest>();
        var requestLog = new List<(DateTimeOffset At, DowntimeSyncRequest Request)>();
        var repliesFromBToA = new List<(DateTimeOffset At, DowntimeSyncReply Reply)>();
        var sniffer = hub.CreateBus("S");
        sniffer.Received += m =>
        {
            if (m is DowntimeSyncRequest request && request.Sender == "A")
            { requestsFromA[request.RequestId] = request; requestLog.Add((clock.UtcNow, request)); }
            else if (m is DowntimeSyncReply reply && reply.Sender == "B" && reply.ReplyTo == "A")
                repliesFromBToA.Add((clock.UtcNow, reply));
        };

        foreach (var node in new[] { a, b, c }) node.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default); await c.StartAsync(default);

        // Faza 1: grupa razem; B zostaje obserwatorem, wszyscy wymieniają pełne okna i przechodzą na ogon.
        for (int i = 0; i < 30; i++) await TickAsync(clock, TimeSpan.FromSeconds(1), a, b, c);
        Assert.True(b.IsWatcher);

        // Faza 2: podział {A, C} | {B}, ciągła praca co 5 s (heartbeat < cluster.peerTimeoutMs = 15 s). Po 2 h ciszy A i C
        // usuwają B z tabeli (a B ich) — powrót łączności to dla nich „nowy partner”.
        hub.Partition("A", "B"); hub.Partition("B", "C");
        var splitAt = clock.UtcNow;
        while (clock.UtcNow - splitAt < SplitLength) await TickAsync(clock, TimeSpan.FromSeconds(5), a, b, c);
        int seen = requestLog.Count;
        while (requestLog.Count == seen) await TickAsync(clock, TimeSpan.FromSeconds(5), a, b, c);
        // W czasie podziału A pyta wyłącznie C (B jest martwy/usunięty), więc to jest prośba ogona z powodu heartbeatu C.
        var lastTail = requestLog[^1];
        Assert.False(IsFullWindow(lastTail.Request));
        var nextTailAt = lastTail.At + TimeSpan.FromMilliseconds(config.DowntimeSyncIntervalMs);
        var healAt = nextTailAt - TimeSpan.FromSeconds(healSecondsBeforeTailRequest);
        Assert.True(healAt - clock.UtcNow >= TimeSpan.FromSeconds(10));
        while (healAt - clock.UtcNow > TimeSpan.FromSeconds(10)) await TickAsync(clock, TimeSpan.FromSeconds(5), a, b, c);
        while (clock.UtcNow < healAt) await TickAsync(clock, TimeSpan.FromSeconds(1), a, b, c);
        Assert.Equal(healAt, clock.UtcNow);

        // Postoje, które w czasie podziału widział tylko B: jeden starszy niż doba przed powrotem łączności, jeden świeższy.
        var outageOld = Episode("aidite1", healAt - TimeSpan.FromHours(25), TimeSpan.FromMinutes(15), "B");
        var outageRecent = Episode("aidite2", healAt - TimeSpan.FromHours(2), TimeSpan.FromMinutes(15), "B");
        Assert.True(outageOld.StartedAt > splitAt);
        bJournal.Merge(outageOld); bJournal.Merge(outageRecent);
        // Trwający błąd u obserwatora B: po powrocie łączności jego STATE daje A świeży wpis (najnowsza obserwacja A).
        var live = new Alert
        {
            Key = AlertKey.ForRule("aidite3", "code-705"), MachineId = "aidite3", MachineName = "Aidite 3",
            ShortText = "Servo 6", Category = "restart", Kind = AlertKind.Error,
            StartedAt = healAt - TimeSpan.FromMinutes(10), LastSeenAt = healAt, Status = AlertStatus.Active,
        };

        // Faza 3: powrót łączności; w każdej sekundzie najpierw B, potem A, potem C.
        hub.Heal();
        var afterHeal = TimeSpan.Zero;
        while (afterHeal < TimeSpan.FromHours(2))
        {
            var step = afterHeal < TimeSpan.FromMinutes(2) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
            b.SetLocalState([live with { LastSeenAt = clock.UtcNow }], []);
            await TickAsync(clock, step, b, a, c);
            afterHeal += step;
        }

        var first = repliesFromBToA.Where(r => r.At >= healAt).Select(r => r.Reply).FirstOrDefault();
        DowntimeSyncRequest? firstAnswered = first is not null && requestsFromA.TryGetValue(first.RequestId, out var asked) ? asked : null;
        return new SplitOutcome(outageOld, outageRecent, aJournal.Snapshot(), bJournal.Snapshot(), healAt, clock.UtcNow,
            firstAnswered, firstAnswered is not null && IsFullWindow(firstAnswered),
            requestLog.Where(r => r.At >= healAt && IsFullWindow(r.Request)).Select(r => r.At - healAt).ToArray(),
            Report(aJournal.Snapshot(), splitAt, healAt, clock.UtcNow), Report(bJournal.Snapshot(), splitAt, healAt, clock.UtcNow),
            cJournal.Snapshot().Any(e => e.EpisodeId == outageOld.EpisodeId));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Scenariusz 2: dwa prawdziwe węzły, opóźnienie i utrata pojedynczych pakietów przez ControlledBus.
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record LiteralOutcome(
        bool StaleAccepted, DateTimeOffset StaleDeliveredAt, TimeSpan ObservedFor,
        IReadOnlyList<(DateTimeOffset At, DowntimeSyncRequest Request)> RequestsAfterStale,
        IReadOnlyList<string> RejectsAtA, bool OutageArrived, TimeSpan? OutageArrivedAfter);

    /// <summary>
    /// A i B pracują razem. Odpowiedź B na kolejną prośbę ogona A zostaje „w sieci” (przetrzymany, podpisany datagram),
    /// po czym łączność A–B znika na <paramref name="outage"/> (B zapisuje w tym czasie postój). Po powrocie heartbeat B
    /// otwiera u A pełne okno, nowa prośba o pełne okno ginie, a przetrzymana odpowiedź dociera — z pierwotnym Seq i SentAt.
    /// </summary>
    private static async Task<LiteralOutcome> RunLiteralAsync(TimeSpan outage, TimeSpan observeAfter)
    {
        using var dir = new TempDir("wer-grupa-requestid");
        var clock = new FakeClock(T0);
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var aBus = new ControlledBus(hub, "A", clock);
        var bBus = new ControlledBus(hub, "B", clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        // Świeży lokalny wpis A: ogon (najnowszy wpis − doba) różni się od pełnego okna.
        aJournal.Merge(Episode("aidite5", T0 - TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2), "A"));
        await using var a = new GroupClusterNode(config, aBus, clock, "test") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, bBus, clock, "test") { Downtime = bJournal };
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));

        var requestLog = new List<(DateTimeOffset At, DowntimeSyncRequest Request)>();
        hub.CreateBus("S").Received += m =>
        {
            if (m is DowntimeSyncRequest request && request.Sender == "A") requestLog.Add((clock.UtcNow, request));
        };
        await a.StartAsync(default); await b.StartAsync(default);
        for (int i = 0; i < 30; i++) await TickAsync(clock, TimeSpan.FromSeconds(1), a, b);
        Assert.NotNull(Assert.Single(a.Peers, p => p.Host == "B").DowntimeSyncedAt);
        // Znacznik: postój, który B widział po pierwszej wymianie. Jedyną drogą do A jest odpowiedź na następną prośbę
        // ogona — po nim (dane, nie księgowanie węzła) widać, że spóźniona odpowiedź przeszła wszystkie filtry.
        var marker = Episode("aidite6", clock.UtcNow - TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), "B");
        bJournal.Merge(marker);

        // Odpowiedź B na następną prośbę ogona A (po 10 min) zostaje w sieci.
        bBus.Filter = m => m is DowntimeSyncReply { ReplyTo: "A" } ? Delivery.Hold : Delivery.Pass;
        while (bBus.HeldCount == 0) await TickAsync(clock, TimeSpan.FromSeconds(1), a, b);
        bBus.Filter = null;
        var heldAt = clock.UtcNow;
        Assert.False(IsFullWindow(requestLog[^1].Request));
        Assert.DoesNotContain(aJournal.Snapshot(), e => e.EpisodeId == marker.EpisodeId);

        // Przerwa łączności A–B; B widzi w tym czasie postój.
        hub.Partition("A", "B");
        var outageEpisode = Episode("aidite2", heldAt + TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), "B");
        bJournal.Merge(outageEpisode);
        while (clock.UtcNow - heldAt < outage) await TickAsync(clock, TimeSpan.FromSeconds(1), a, b);

        // Powrót: heartbeat B dociera do A (pełne okno otwarte), a pierwsza prośba A o pełne okno ginie w sieci (UDP).
        aBus.Filter = m => m is DowntimeSyncRequest request && IsFullWindow(request) ? Delivery.Drop : Delivery.Pass;
        hub.Heal();
        await TickAsync(clock, TimeSpan.FromSeconds(1), b, a);
        Assert.Single(aBus.Dropped);
        aBus.Filter = null;

        // Spóźniona odpowiedź dociera teraz, z pierwotnym Seq i SentAt.
        var deliveredAt = clock.UtcNow;
        int requestsBefore = requestLog.Count;
        bBus.ReleaseHeld();
        bool accepted = aJournal.Snapshot().Any(e => e.EpisodeId == marker.EpisodeId);

        TimeSpan? arrivedAfter = null;
        var observed = TimeSpan.Zero;
        while (observed < observeAfter)
        {
            await TickAsync(clock, TimeSpan.FromSeconds(1), b, a);
            observed += TimeSpan.FromSeconds(1);
            if (arrivedAfter is null && aJournal.Snapshot().Any(e => e.EpisodeId == outageEpisode.EpisodeId))
                arrivedAfter = clock.UtcNow - deliveredAt;
        }

        return new LiteralOutcome(accepted, deliveredAt, observeAfter, requestLog.Skip(requestsBefore).ToArray(),
            aBus.RejectReasons.ToArray(), arrivedAfter is not null, arrivedAfter);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Pomocnicze.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Prośba o całe skonfigurowane okno (pierwsza po poznaniu partnera albo po powrocie łączności).</summary>
    private static bool IsFullWindow(DowntimeSyncRequest request) => request.Since == request.SentAt - Window;

    private static async Task TickAsync(FakeClock clock, TimeSpan step, params GroupClusterNode[] order)
    {
        foreach (var node in order) await node.TickAsync(default);
        clock.Advance(step);
    }

    private static DowntimeEpisode Episode(string machine, DateTimeOffset start, TimeSpan length, string origin) =>
        new(machine, "Aidite " + machine[^1], "Servo 6", start, start + length, start + length,
            EpisodeId: Guid.NewGuid(), Origin: origin, Category: "restart");

    /// <summary>Ta sama reguła, co <c>AppOrchestrator.OnRemoteEpisodesReceived</c>.</summary>
    private static void Replicate(DowntimeJournal journal, IReadOnlyList<Alert> alerts, IReadOnlyList<EpisodeClosure> closures,
        string? origin)
    {
        foreach (var alert in alerts)
            if (DowntimeJournal.IsRecordedKind(alert.Kind)) journal.Merge(DowntimeEpisode.FromAlert(alert, origin));
        foreach (var closure in closures)
            if (DowntimeJournal.IsRecordedKind(closure.Kind)) journal.MergeClosure(closure.EpisodeId, closure.At, origin);
    }

    /// <summary>Wiersze raportu postojów Panelu (ten sam <see cref="DowntimeReport.Build"/>) zaczynające się w [from, to).</summary>
    private static ReportTotals Report(IReadOnlyList<DowntimeEpisode> journal, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var rows = DowntimeReport.Build(journal, TimeZoneInfo.Utc, now: now).Where(s => s.Start >= from && s.Start < to).ToArray();
        return new ReportTotals(rows.Length, rows.Sum(s => s.Minutes));
    }

    private enum Delivery { Pass, Drop, Hold }

    /// <summary>
    /// Magistrala testowa nad <see cref="InMemoryPeerBus"/>: stempluje wiadomość dokładnie jak prawdziwa (Seq, SentAt,
    /// InstanceId, podpis), a potem według <see cref="Filter"/> doręcza od razu, gubi (utrata UDP) albo przetrzymuje gotowy
    /// datagram (opóźnienie w sieci — doręczony później niesie PIERWOTNE Seq i SentAt i przechodzi zwykłe TryDecode odbiorcy).
    /// </summary>
    private sealed class ControlledBus : IPeerBus
    {
        private readonly InMemoryPeerHub _hub;
        private readonly InMemoryPeerBus _inner;
        private readonly IClock _clock;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private readonly List<byte[]> _held = [];
        private long _seq;

        public ControlledBus(InMemoryPeerHub hub, string host, IClock clock)
        {
            _hub = hub; _clock = clock; _inner = hub.CreateBus(host);
            _inner.Received += m => Received?.Invoke(m);
            _inner.Rejected += reason => { RejectReasons.Add(reason); Rejected?.Invoke(reason); };
        }

        public Func<PeerMessage, Delivery>? Filter { get; set; }
        public List<string> RejectReasons { get; } = [];
        public List<PeerMessage> Dropped { get; } = [];
        public int HeldCount => _held.Count;
        public string LocalHost => _inner.LocalHost;
        public event Action<PeerMessage>? Received;
        public event Action<string>? Rejected;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(PeerMessage msg, CancellationToken ct)
        {
            var stamped = msg with { Sender = LocalHost, Seq = Interlocked.Increment(ref _seq), SentAt = _clock.UtcNow, InstanceId = _instanceId };
            var datagram = MessageCodec.Encode(stamped, _hub.Signer);
            switch (Filter?.Invoke(stamped) ?? Delivery.Pass)
            {
                case Delivery.Drop: Dropped.Add(stamped); break;
                case Delivery.Hold: _held.Add(datagram); break;
                default: _hub.Dispatch(_inner, datagram); break;
            }
            return Task.CompletedTask;
        }

        public void ReleaseHeld()
        {
            var held = _held.ToArray();
            _held.Clear();
            foreach (var datagram in held) _hub.Dispatch(_inner, datagram);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
