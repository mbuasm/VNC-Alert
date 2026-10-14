using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class DowntimeFullWindowRequestIdTests
{
    // Wtorek 06:00 UTC: scenariusze z podziałem (doba i dwie godziny) leżą poza weekendem raportu (sob 06:00 – pon 06:00).
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(90); // domyślne cluster.downtimeSyncWindowDays
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    // ---------------------------------------------------------------------------------------------------------------
    // 1. Reguły korelacji.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplyToATailRequest_KeepsTheFullWindowOpen_ButItsEntriesAreMerged()
    {
        await using var rig = await Rig.StartAsync();
        // C już odpowiedział na pełne okno — A pyta go dalej ogonem co 10 min (cluster.downtimeSyncIntervalMs).
        rig.Heartbeat("C");
        var cFull = rig.Last("C");
        Assert.True(rig.IsFull(cFull.Request));
        rig.Reply("C", cFull.Request.RequestId);
        Assert.Equal(T0, rig.SyncedAt("C"));

        // B pojawia się 4 s przed kolejną prośbą ogona wywołaną heartbeatem C; pierwsza prośba o pełne okno do B ginie
        // (B jeszcze nie zna A).
        rig.Pump(596, "C");
        rig.Heartbeat("B");
        Assert.True(rig.IsFull(rig.Last("B").Request));
        rig.Pump(4, "C", "B");
        var tail = rig.Last("C");
        Assert.Equal(T0.AddSeconds(600), tail.At);
        Assert.True(rig.IsTail(tail.Request));

        // B odpowiada na prośbę ogona: wpisy trafiają do dziennika A (także starszy niż ogon), ale pełne okno B zostaje
        // otwarte, a Panel nadal pokazuje „brak”.
        var old = Episode("aidite1", T0.AddDays(-3), TimeSpan.FromMinutes(15), "B");
        rig.Reply("B", tail.Request.RequestId, old);
        Assert.Contains(rig.Journal.Snapshot(), e => e.EpisodeId == old.EpisodeId);
        Assert.Null(rig.SyncedAt("B"));

        // Ponowienie prośby o pełne okno 10 s po pierwszej.
        rig.Pump(6, "C", "B");
        var retry = rig.Last("B");
        Assert.Equal(T0.AddSeconds(606), retry.At);
        Assert.True(rig.IsFull(retry.Request));

        // Dopiero odpowiedź na prośbę o pełne okno je kończy: Panel dostaje czas, następna prośba do B to ogon po 10 min.
        rig.Reply("B", retry.Request.RequestId);
        Assert.Equal(T0.AddSeconds(606), rig.SyncedAt("B"));
        rig.Pump(600, "C", "B");
        var next = Assert.Single(rig.TriggeredBy("B"), r => r.At > retry.At);
        Assert.Equal(T0.AddSeconds(1206), next.At);
        Assert.True(rig.IsTail(next.Request));
    }

    [Fact]
    public async Task ReplyToAFullWindowRequestTriggeredByAnotherPeer_EndsTheFullWindow()
    {
        await using var rig = await Rig.StartAsync();
        rig.Heartbeat("B"); // prośba o pełne okno wywołana przez B — ginie
        rig.Heartbeat("C"); // druga, wywołana przez C
        var byC = rig.Last("C");
        Assert.True(rig.IsFull(byC.Request));

        // Prośba nie ma adresata: B odpowiada na prośbę wywołaną heartbeatem C — ona też niesie całe okno.
        rig.Reply("B", byC.Request.RequestId);
        Assert.Equal(T0, rig.SyncedAt("B"));
        Assert.Null(rig.SyncedAt("C"));
        rig.Pump(10, "B", "C");
        Assert.Single(rig.TriggeredBy("B")); // B: następna prośba dopiero po 10 min
        var cRetry = rig.Last("C");          // C: nadal pełne okno co 10 s
        Assert.Equal(T0.AddSeconds(10), cRetry.At);
        Assert.True(rig.IsFull(cRetry.Request));
    }

    [Fact]
    public async Task ReplyToARequestThisComputerNeverSent_DoesNotEndTheFullWindow()
    {
        await using var rig = await Rig.StartAsync();
        rig.Heartbeat("B");
        var old = Episode("aidite1", T0.AddDays(-3), TimeSpan.FromMinutes(15), "B");
        rig.Reply("B", Guid.NewGuid(), old);
        Assert.Contains(rig.Journal.Snapshot(), e => e.EpisodeId == old.EpisodeId);
        Assert.Null(rig.SyncedAt("B"));
        rig.Pump(10, "B");
        var retry = rig.Last("B");
        Assert.Equal(T0.AddSeconds(10), retry.At);
        Assert.True(rig.IsFull(retry.Request));
    }

    /// <summary>
    /// Ogon przycięty do początku okna (stary dziennik) jest prośbą o pełne okno — odpowiedź na nią kończy pełne okno partnera.
    /// Prośba C w 600. sekundzie: Since = najnowszy wpis − doba; wpis sprzed 89 dni sięga dokładnie początku okna (90 dni).
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task TailClampedToTheWindowStart_CountsAsAFullWindowRequest(int millisecondsNewer, bool full)
    {
        var requestAt = T0.AddSeconds(600);
        await using var rig = await Rig.StartAsync(
            latest: requestAt - Window + TimeSpan.FromDays(1) + TimeSpan.FromMilliseconds(millisecondsNewer));
        rig.Heartbeat("C");
        rig.Reply("C", rig.Last("C").Request.RequestId);
        rig.Pump(596, "C");
        rig.Heartbeat("B"); // B czeka na pełne okno; ta prośba ginie
        rig.Pump(4, "C", "B");
        var byC = rig.Last("C");
        Assert.Equal(requestAt, byC.At);
        Assert.Equal(full, rig.IsFull(byC.Request));

        rig.Reply("B", byC.Request.RequestId);

        Assert.Equal(full, rig.SyncedAt("B") is not null);
    }

    /// <summary>
    /// spóźniona odpowiedź na PEŁNĄ prośbę sprzed przerwy kończy pełne okno — bez utraty. Transport przepuszcza
    /// ją najwyżej ~2 × <c>cluster.clockSkewToleranceSec</c> po prośbie, więc to, co partner zapisał po jej wysłaniu, jest świeższe
    /// niż doba i przychodzi zwykłym ogonem (jak <c>StaleReplyWithinClockSkewTolerance_…</c> dla prośby ogona).
    /// Prawdziwe A i B; C to nowy partner, którego heartbeat wywołuje u A prośbę o pełne okno.
    /// </summary>
    [Fact]
    public async Task StaleReplyToAFullWindowRequestFromBeforeTheOutage_EndsTheWindow_AndTheOutageStillArrivesWithTheTail()
    {
        using var dir = new TempDir("napr-requestid-stale-full");
        var clock = new FakeClock(T0);
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        aJournal.Merge(Episode("aidite7", T0.AddMinutes(-3), TimeSpan.FromMinutes(2), "A")); // świeży wpis A: ogon ≠ pełne okno
        var aBus = new HoldingBus(hub, "A", clock);
        var bBus = new HoldingBus(hub, "B", clock);
        var cBus = hub.CreateBus("C");
        await using var a = new GroupClusterNode(config, aBus, clock, "test") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, bBus, clock, "test") { Downtime = bJournal };
        await a.StartAsync(default);
        await b.StartAsync(default);
        async Task Tick(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                await b.TickAsync(default);
                await a.TickAsync(default);
                clock.Advance(Second);
            }
        }
        await Tick(100);
        Assert.NotNull(Assert.Single(a.Peers, p => p.Host == "B").DowntimeSyncedAt); // B już na ogonie

        // Heartbeat nowego C: A prosi o pełne okno; B też na nią odpowiada (prośba bez adresata), ale odpowiedź zostaje w sieci.
        bBus.Filter = m => m is DowntimeSyncReply { ReplyTo: "A" } ? Delivery.Hold : Delivery.Pass;
        hub.Dispatch(cBus, MessageCodec.Encode(new HeartbeatMessage
        {
            Sender = "C", Version = "test", Role = PeerRole.Ui, Readiness = new(false, false, []), Group = config.Group,
            InstanceId = Guid.NewGuid().ToString("N"), Seq = 1, SentAt = clock.UtcNow,
        }, hub.Signer));
        Assert.True(IsFullRequest(Assert.Single(aBus.Log, s => s.At == clock.UtcNow && s.Message is DowntimeSyncRequest).Message));
        Assert.Equal(1, bBus.HeldCount);
        bBus.Filter = null;

        // Przerwa A–B (20 s); B zapisuje w tym czasie postój.
        hub.Partition("A", "B");
        var outage = Episode("aidite2", clock.UtcNow.AddSeconds(2), TimeSpan.FromSeconds(10), "B");
        bJournal.Merge(outage);
        await Tick(20);

        // Powrót: nowa prośba A o pełne okno ginie, a spóźniona odpowiedź B na pełną prośbę sprzed przerwy dociera.
        aBus.Filter = m => IsFullRequest(m) ? Delivery.Drop : Delivery.Pass;
        hub.Heal();
        await Tick(1);
        Assert.Contains(aBus.Log, s => s.Delivery == Delivery.Drop);
        aBus.Filter = null;
        var releasedAt = clock.UtcNow;
        Assert.Equal(1, bBus.ReleaseHeld());
        Assert.DoesNotContain(aJournal.Snapshot(), e => e.EpisodeId == outage.EpisodeId); // odpowiedź sprzed postoju

        // Odpowiedź na naszą pełną prośbę kończy pełne okno: ponowień co 10 s już nie ma…
        await Tick(30);
        Assert.DoesNotContain(aBus.Log, s => s.At >= releasedAt && IsFullRequest(s.Message));
        // …a postój z przerwy przychodzi zwykłym ogonem (prośba 10 min po tej z powrotu łączności).
        TimeSpan? arrivedAfter = null;
        for (int second = 0; second < 11 * 60 && arrivedAfter is null; second++)
        {
            await Tick(1);
            if (aJournal.Snapshot().Any(e => e.EpisodeId == outage.EpisodeId)) arrivedAfter = clock.UtcNow - releasedAt;
        }
        Assert.NotNull(arrivedAfter);
        Assert.True(arrivedAfter <= TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30), $"Postój z przerwy dotarł po {arrivedAfter}.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Pamięć próśb o pełne okno: czas życia, limit liczby, usunięcie partnera.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Czas życia wpisu: max(10 min, 2 × cluster.clockSkewToleranceSec) — dłużej transport i tak nie przepuści odpowiedzi.</summary>
    [Theory]
    [InlineData(300, 0, true)]
    [InlineData(300, 1, false)]
    [InlineData(400, 0, true)]
    [InlineData(400, 1, false)]
    public async Task FullWindowRequestIsRememberedForTwoClockSkewTolerances(int clockSkewToleranceSec, int afterLifetimeMs, bool ends)
    {
        int lifetimeSec = Math.Max(600, 2 * clockSkewToleranceSec);
        await using var rig = await Rig.StartAsync(clockSkewToleranceSec);
        rig.Heartbeat("B");
        var first = rig.Last("B").Request;
        // B żyje (heartbeat co 5 s), ale nie odpowiada — A ponawia prośbę o pełne okno co 10 s.
        for (int second = 5; second <= lifetimeSec - 5; second += 5)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            rig.Heartbeat("B");
        }
        Assert.Equal(lifetimeSec / 10, rig.Node.RememberedFullDowntimeRequests);
        rig.Clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(afterLifetimeMs));

        rig.Reply("B", first.RequestId);

        Assert.Equal(ends, rig.SyncedAt("B") is not null);
        Assert.Equal(lifetimeSec / 10 - (ends ? 0 : 1), rig.Node.RememberedFullDowntimeRequests);
    }

    [Fact]
    public async Task FullWindowRequestMemoryIsCapped_TheOldestRequestsAreForgotten()
    {
        await using var rig = await Rig.StartAsync();
        var hosts = Enumerable.Range(1, 40).Select(i => $"P{i:00}").ToArray();
        var firstRound = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (int round = 0; round < 7; round++)
        {
            foreach (string host in hosts)
            {
                rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
                rig.Heartbeat(host);
                var request = rig.Last(host);
                Assert.Equal(rig.Clock.UtcNow, request.At);
                Assert.True(rig.IsFull(request.Request));
                if (round == 0) firstRound[host] = request.Request.RequestId;
            }
            rig.Clock.Advance(TimeSpan.FromMilliseconds(10_000 - hosts.Length));
        }
        Assert.Equal(280, rig.Requests.Count);
        Assert.Equal(GroupClusterNode.MaxRememberedFullDowntimeRequests, rig.Node.RememberedFullDowntimeRequests);

        rig.Reply("P01", firstRound["P01"]);
        rig.Reply("P24", firstRound["P24"]);
        rig.Reply("P25", firstRound["P25"]);

        Assert.Null(rig.SyncedAt("P01"));
        Assert.Null(rig.SyncedAt("P24"));
        Assert.NotNull(rig.SyncedAt("P25"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingAPeerFromTheTable_ForgetsTheFullWindowRequestsItTriggered(bool removed)
    {
        // Czas życia wpisu 6 h (2 × 3 h tolerancji) — dłuższy niż 2 h ciszy, po których partner wypada z tabeli, więc o wyniku
        // decyduje wyłącznie usunięcie partnera.
        await using var rig = await Rig.StartAsync(clockSkewToleranceSec: 3 * 60 * 60);
        rig.Heartbeat("C");
        var byC = rig.Last("C").Request; // C czeka na pełne okno i milknie
        rig.Heartbeat("B");
        rig.Reply("B", rig.Last("B").Request.RequestId); // B odpowiada — dalej ogon co 10 min, bez nowych wpisów w pamięci
        Assert.Equal(2, rig.Node.RememberedFullDowntimeRequests);
        for (int second = 5; second <= 2 * 60 * 60 + 5; second += 5)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            rig.Heartbeat("B");
        }
        if (removed) await rig.Node.TickAsync(default); // porządki węzła: partner bez heartbeatu ponad 2 h wypada z tabeli
        Assert.Equal(!removed, rig.Node.Peers.Any(p => p.Host == "C"));
        Assert.Equal(removed ? 1 : 2, rig.Node.RememberedFullDowntimeRequests);

        // B traci łączność na 16 s (pełne okno otwiera się ponownie) i odpowiada na starą prośbę o pełne okno wywołaną przez C.
        rig.Clock.Advance(TimeSpan.FromSeconds(16));
        rig.Heartbeat("B");
        var reopened = rig.Last("B");
        Assert.True(rig.IsFull(reopened.Request));
        rig.Reply("B", byC.RequestId);
        rig.Pump(10, "B");

        // Zapomniana prośba nie kończy pełnego okna (ponowienie po 10 s); zapamiętana kończy (następna prośba dopiero po 10 min).
        var last = rig.Last("B");
        Assert.Equal(removed, last.At == reopened.At.AddSeconds(10) && rig.IsFull(last.Request));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2. Odpowiedź bez RequestId.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplyWithoutRequestId_KeepsRetryingTheFullWindowForTwoMinutes_ThenTheNormalTail()
    {
        await using var rig = await Rig.StartAsync();
        rig.Heartbeat("B"); // t = 0 s: B nowy, pełne okno otwarte
        var old = Episode("aidite1", T0.AddDays(-3), TimeSpan.FromMinutes(15), "B");
        rig.Reply("B", Guid.Empty, old); // wpisy scalone, okno otwarte
        Assert.Contains(rig.Journal.Snapshot(), e => e.EpisodeId == old.EpisodeId);
        Assert.Null(rig.SyncedAt("B"));

        rig.Pump(119, "B");
        var retries = rig.TriggeredBy("B");
        Assert.Equal(12, retries.Count); // 0, 10, …, 110 s — prośba o pełne okno co 10 s
        Assert.All(retries, r => Assert.True(rig.IsFull(r.Request)));
        rig.Clock.Advance(TimeSpan.FromMilliseconds(999));
        rig.Reply("B", Guid.Empty); // 119,999 s oczekiwania — jeszcze nie
        Assert.Null(rig.SyncedAt("B"));
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        rig.Reply("B", Guid.Empty); // 120 s — koniec pełnego okna
        Assert.Equal(T0.AddSeconds(120), rig.SyncedAt("B"));

        rig.Pump(590, "B"); // potem zwykły ogon: 10 min po ostatniej prośbie (110 s)
        var next = Assert.Single(rig.TriggeredBy("B"), r => r.At > T0.AddSeconds(110));
        Assert.Equal(T0.AddSeconds(710), next.At);
        Assert.True(rig.IsTail(next.Request));

        // Po odzyskaniu łączności 2 min liczą się od nowa.
        rig.Clock.Advance(TimeSpan.FromSeconds(16));
        rig.Heartbeat("B"); // t = 726 s
        var reopened = rig.Last("B");
        Assert.Equal(T0.AddSeconds(726), reopened.At);
        Assert.True(rig.IsFull(reopened.Request));
        rig.Pump(119, "B"); // t = 845 s
        rig.Reply("B", Guid.Empty);
        Assert.Equal(T0.AddSeconds(120), rig.SyncedAt("B")); // bez zmian — okno nadal otwarte
        rig.Pump(1, "B"); // t = 846 s: ponowienie pełnego okna
        Assert.Equal(T0.AddSeconds(846), rig.Last("B").At);
        Assert.True(rig.IsFull(rig.Last("B").Request));
        rig.Reply("B", Guid.Empty); // 120 s po odzyskaniu łączności
        Assert.Equal(T0.AddSeconds(846), rig.SyncedAt("B"));
        int asked = rig.TriggeredBy("B").Count;
        rig.Pump(30, "B");
        Assert.Equal(asked, rig.TriggeredBy("B").Count); // już bez ponowień co 10 s
    }

    /// <summary>
    /// Dwa prawdziwe węzły; odpowiadający B (hipotetyczny — żadna wersja programu tak nie robi) wysyła odpowiedzi bez
    /// <c>RequestId</c>. Synchronizacja się nie zawiesza: A dostaje postój sprzed 3 dni (tylko z pełnego okna), ponawia pełne okno
    /// przez 2 min, potem przechodzi na zwykły ogon co 10 min, a Panel pokazuje czas uzupełnienia.
    /// </summary>
    [Fact]
    public async Task PeerThatNeverSendsRequestId_DoesNotStallTheSync_FullWindowRetriedThenTail()
    {
        using var dir = new TempDir("napr-requestid-empty");
        var clock = new FakeClock(T0);
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var wire = new Wire();
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        aJournal.Merge(Episode("aidite7", T0.AddMinutes(-3), TimeSpan.FromMinutes(2), "A")); // świeży wpis A: ogon ≠ pełne okno
        var old = Episode("aidite1", T0.AddDays(-3), TimeSpan.FromMinutes(15), "B");          // dojdzie tylko pełnym oknem
        bJournal.Merge(old);
        var aBus = new RecordingBus(hub.CreateBus("A"), clock, wire);
        var bBus = new RecordingBus(hub.CreateBus("B"), clock, wire)
        {
            Transform = m => m is DowntimeSyncReply reply ? reply with { RequestId = Guid.Empty } : m,
        };
        await using var a = new GroupClusterNode(config, aBus, clock, "test") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, bBus, clock, "test") { Downtime = bJournal };
        await a.StartAsync(default);
        await b.StartAsync(default);

        for (int second = 0; second < 15 * 60; second++)
        {
            await b.TickAsync(default);
            await a.TickAsync(default);
            clock.Advance(Second);
        }

        Assert.Contains(aJournal.Snapshot(), e => e.EpisodeId == old.EpisodeId);
        var replies = bBus.Log.Where(s => s.Message is DowntimeSyncReply { ReplyTo: "A" }).ToArray();
        Assert.NotEmpty(replies);
        Assert.All(replies, s => Assert.Equal(Guid.Empty, ((DowntimeSyncReply)s.Message).RequestId));
        var requests = aBus.Log.Where(s => s.Message is DowntimeSyncRequest).ToArray();
        var lastFull = requests.Last(IsFullWindow);
        // Pełne okno ponawiane 2 min po pierwszej odpowiedzi bez identyfikatora (partner odpowiada raz na 60 s), potem koniec.
        Assert.InRange(lastFull.At - replies[0].At, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
        Assert.True(replies.Count(s => s.Index <= lastFull.Index) >= 2,
            $"Przed końcem pełnego okna tylko {replies.Count(s => s.Index <= lastFull.Index)} odpowiedź B.");
        var afterwards = requests.Where(s => s.Index > lastFull.Index).ToArray();
        Assert.NotEmpty(afterwards);
        Assert.DoesNotContain(afterwards, IsFullWindow);
        Assert.True(afterwards[0].At - lastFull.At >= TimeSpan.FromMinutes(10),
            $"Pierwszy ogon {afterwards[0].At - lastFull.At} po ostatniej prośbie o pełne okno.");
        Assert.NotNull(Assert.Single(a.Peers, p => p.Host == "B").DowntimeSyncedAt);
    }


    private static readonly TimeSpan SplitLength = TimeSpan.FromHours(26);

    /// <summary>
    /// Podział (strona 1 | strona 2), kolejność komputerów w każdej sekundzie po powrocie łączności, czyja prośba ogona wypada
    /// tuż po powrocie i ile sekund po nim, czy dochodzi do wyścigu (pierwszą przyjętą po powrocie
    /// odpowiedzią komputera z drugiej strony jest odpowiedź na prośbę ogona).
    /// Fazy próśb ogona wszystkich par z jednej strony są zbliżone (pary kończą pierwsze pełne okna w tych samych sekundach
    /// fazy 1), więc w wierszu „AC|BD, ACBD” wyścig dotyczy B (ogon B wywołany przez D wypada razem z ogonem A). Wiersze bez
    /// wyścigu to kontrola: komputery z drugiej strony poznają proszącego przed jego pierwszą prośbą albo ogon wypada dopiero
    /// po pierwszym ponowieniu pełnego okna (20 s).
    /// </summary>
    public static TheoryData<string, string, string, int, bool> FourComputerSplits() => new()
    {
        { "ACD|B", "BACD", "A", 3, true },
        { "ACD|B", "BDCA", "C", 5, true },
        { "ACD|B", "BCDA", "D", 9, true },
        { "ACD|B", "ACDB", "A", 3, false },
        { "ACD|B", "BACD", "A", 20, false },
        { "AC|BD", "BDAC", "A", 3, true },
        { "AC|BD", "DBCA", "C", 7, true },
        { "AC|BD", "ACBD", "A", 3, true },
        { "AC|BD", "BDAC", "A", 20, false },
    };

    /// <summary>
    /// A (priorytet 100), B (10, obserwator), C (200) i D (150) — prawdziwe węzły i dzienniki, replikacja STATE jak
    /// <c>AppOrchestrator.OnRemoteEpisodesReceived</c>. Po 30 s razem podział na 26 h z ciągłą pracą co 5 s (po 2 h komputery
    /// z drugiej strony wypadają z tabel); każda strona ma swoje postoje z czasu podziału: starszy niż doba i świeży. Powrót
    /// łączności tuż przed prośbą ogona wskazanego komputera, w każdej sekundzie wskazana kolejność; po 15 min każdy komputer
    /// ma mieć wszystkie postoje, a raport postojów za okres podziału ma być ten sam na wszystkich.
    /// </summary>
    [Theory]
    [MemberData(nameof(FourComputerSplits))]
    public async Task FourComputers_SplitLongerThanADay_NoComputerLosesDowntime(
        string split, string healOrder, string tailOf, int tailSecondsAfterHeal, bool race)
    {
        var sides = split.Split('|');
        using var dir = new TempDir("napr-requestid-4pc");
        var clock = new FakeClock(T0);
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var interval = TimeSpan.FromMilliseconds(config.DowntimeSyncIntervalMs);
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var wire = new Wire();
        // Wspólna historia sprzed scenariusza — bez niej pusty dziennik zawsze pyta o całe okno i ogona w ogóle by nie było.
        var history = Episode("aidite4", T0 - TimeSpan.FromDays(3), TimeSpan.FromMinutes(20), "B");
        var pcs = new Dictionary<string, Pc>(StringComparer.Ordinal);
        try
        {
            foreach (var (host, priority) in new[] { ("A", 100), ("B", 10), ("C", 200), ("D", 150) })
            {
                var journal = new DowntimeJournal(dir.File(host + ".jsonl"));
                journal.Merge(history);
                var bus = new RecordingBus(hub.CreateBus(host), clock, wire);
                var node = new GroupClusterNode(config with { Priority = priority }, bus, clock, "test") { Downtime = journal };
                node.EpisodeStateReceived += (alerts, closures) => Replicate(journal, alerts, closures, node.WatcherHost);
                node.SetReadiness(new(true, false, []));
                pcs.Add(host, new Pc(node, journal, bus));
            }
            foreach (var pc in pcs.Values) await pc.Node.StartAsync(default);

            // Faza 1: grupa razem; B obserwuje, wszyscy kończą pełne okna i przechodzą na ogon.
            for (int i = 0; i < 30; i++) await TickAsync(clock, Second, pcs, "ABCD");
            Assert.True(pcs["B"].Node.IsWatcher);

            // Faza 2: podział dłuższy niż doba, ciągła praca co 5 s (heartbeat < cluster.peerTimeoutMs = 15 s).
            foreach (char x in sides[0])
                foreach (char y in sides[1]) hub.Partition(x.ToString(), y.ToString());
            var splitAt = clock.UtcNow;
            while (clock.UtcNow - splitAt < SplitLength) await TickAsync(clock, TimeSpan.FromSeconds(5), pcs, "ABCD");
            DateTimeOffset healAt;
            while (true)
            {
                // Kolejna prośba ogona komputera tailOf: każdy partner z jego strony wywołuje ją co 10 min.
                var cycle = pcs[tailOf].Bus.Log.Where(s => s.Message is DowntimeSyncRequest
                    && s.At > clock.UtcNow - interval - TimeSpan.FromSeconds(5)).ToArray();
                Assert.NotEmpty(cycle);
                Assert.DoesNotContain(cycle, IsFullWindow);
                healAt = cycle.Min(s => s.At) + interval - TimeSpan.FromSeconds(tailSecondsAfterHeal);
                if (healAt - clock.UtcNow >= TimeSpan.FromSeconds(10)) break;
                await TickAsync(clock, TimeSpan.FromSeconds(5), pcs, "ABCD");
            }
            while (healAt - clock.UtcNow > TimeSpan.FromSeconds(10)) await TickAsync(clock, TimeSpan.FromSeconds(5), pcs, "ABCD");
            while (clock.UtcNow < healAt) await TickAsync(clock, Second, pcs, "ABCD");
            Assert.Equal(healAt, clock.UtcNow);

            // Postoje, które w czasie podziału widziała tylko jedna strona (w jej obrębie — replikacja na żywo).
            DowntimeEpisode[] outage =
            [
                Episode("aidite1", healAt - TimeSpan.FromHours(25), TimeSpan.FromMinutes(15), sides[0][..1]),
                Episode("aidite5", healAt - TimeSpan.FromHours(2), TimeSpan.FromMinutes(15), sides[0][..1]),
                Episode("aidite2", healAt - TimeSpan.FromHours(25.5), TimeSpan.FromMinutes(15), "B"),
                Episode("aidite6", healAt - TimeSpan.FromHours(1.5), TimeSpan.FromMinutes(15), "B"),
            ];
            Assert.All(outage, e => Assert.True(e.StartedAt > splitAt));
            foreach (char h in sides[0]) { pcs[h.ToString()].Journal.Merge(outage[0]); pcs[h.ToString()].Journal.Merge(outage[1]); }
            foreach (char h in sides[1]) { pcs[h.ToString()].Journal.Merge(outage[2]); pcs[h.ToString()].Journal.Merge(outage[3]); }
            // Trwający błąd u obserwatora B: po powrocie łączności jego STATE daje wszystkim świeży wpis (ogon = teraz − doba).
            var live = new Alert
            {
                Key = AlertKey.ForRule("aidite3", "code-705"), MachineId = "aidite3", MachineName = "Aidite 3",
                ShortText = "Servo 6", Category = "restart", Kind = AlertKind.Error,
                StartedAt = healAt - TimeSpan.FromMinutes(10), LastSeenAt = healAt, Status = AlertStatus.Active,
            };

            // Faza 3: powrót łączności, 15 min (pierwsze 3 min co sekundę) w kolejności healOrder.
            foreach (var pc in pcs.Values) pc.Bus.LogHeartbeats = true;
            long healIndex = wire.Next();
            hub.Heal();
            for (var after = TimeSpan.Zero; after < TimeSpan.FromMinutes(15);)
            {
                var step = after < TimeSpan.FromMinutes(3) ? Second : TimeSpan.FromSeconds(5);
                pcs["B"].Node.SetLocalState([live with { LastSeenAt = clock.UtcNow }], []);
                await TickAsync(clock, step, pcs, healOrder);
                after += step;
            }

            var races = Races(pcs, sides, healIndex, healAt);
            Assert.True(race == races.Count > 0, $"Wyścig grupa-requestid-01: oczekiwano {(race ? "tak" : "nie")}, "
                + $"jest [{string.Join(", ", races)}] — scenariusz nie odtworzył się tak, jak opisuje wiersz testu.");
            var expected = outage.Append(history).Select(e => (e.EpisodeId, e.MachineId)).Append((live.EpisodeId, live.MachineId)).ToArray();
            var lost = pcs.Select(p => (Host: p.Key, Ids: p.Value.Journal.Snapshot().Select(e => e.EpisodeId).ToHashSet()))
                .Select(p => (p.Host, Missing: expected.Where(x => !p.Ids.Contains(x.EpisodeId)).Select(x => x.MachineId).ToArray()))
                .Where(p => p.Missing.Length > 0).Select(p => $"{p.Host} nie ma {string.Join(", ", p.Missing)}").ToArray();
            var reports = pcs.ToDictionary(p => p.Key, p => Report(p.Value.Journal.Snapshot(), splitAt, healAt, clock.UtcNow));
            Assert.True(lost.Length == 0, $"15 min po powrocie łączności: {string.Join("; ", lost)}. Wyścigi -01: "
                + $"[{string.Join(", ", races)}]. Raport postojów za okres podziału: "
                + string.Join(", ", reports.Select(r => $"{r.Key} {r.Value.Rows} wierszy / {r.Value.Minutes:0.#} min")) + ".");
            Assert.All(reports.Values, r =>
            {
                Assert.Equal(reports["B"].Rows, r.Rows);
                Assert.Equal(reports["B"].Minutes, r.Minutes, 6);
            });
        }
        finally
        {
            foreach (var pc in pcs.Values) await pc.Node.DisposeAsync();
        }
    }

    /// <summary>
    /// Pary „proszący ← odpowiadający z drugiej strony”, w których pierwszą PRZYJĘTĄ po powrocie łączności odpowiedzią była
    /// odpowiedź na prośbę ogona. Przyjęta = wysłana po pierwszym
    /// heartbeacie odpowiadającego po powrocie (magistrala w pamięci doręcza od razu, więc proszący już go zna).
    /// </summary>
    private static List<string> Races(IReadOnlyDictionary<string, Pc> pcs, string[] sides, long healIndex, DateTimeOffset healAt)
    {
        var races = new List<string>();
        foreach (var (requesters, responders) in new[] { (sides[0], sides[1]), (sides[1], sides[0]) })
            foreach (char x in requesters)
                foreach (char y in responders)
                {
                    string requester = x.ToString(), responder = y.ToString();
                    var log = pcs[responder].Bus.Log;
                    var heartbeat = log.FirstOrDefault(s => s.Index > healIndex && s.Message is HeartbeatMessage);
                    var reply = heartbeat is null ? null
                        : log.FirstOrDefault(s => s.Index > heartbeat.Index && s.Message is DowntimeSyncReply r && r.ReplyTo == requester);
                    if (reply is null) continue;
                    var id = ((DowntimeSyncReply)reply.Message).RequestId;
                    var request = pcs[requester].Bus.Log.FirstOrDefault(s => s.Message is DowntimeSyncRequest q && q.RequestId == id);
                    if (request is not null && !IsFullWindow(request))
                        races.Add($"{requester}←{responder} +{(reply.At - healAt).TotalSeconds:0} s");
                }
        return races;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Pomocnicze.
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record Pc(GroupClusterNode Node, DowntimeJournal Journal, RecordingBus Bus);

    private sealed record ReportTotals(int Rows, double Minutes);

    private static async Task TickAsync(FakeClock clock, TimeSpan step, IReadOnlyDictionary<string, Pc> pcs, string order)
    {
        foreach (char host in order) await pcs[host.ToString()].Node.TickAsync(default);
        clock.Advance(step);
    }

    /// <summary>Prośba o całe skonfigurowane okno (zapis z magistrali: czas wysłania = <c>SentAt</c>).</summary>
    private static bool IsFullWindow(Sent sent) => sent.Message is DowntimeSyncRequest request && request.Since == sent.At - Window;

    /// <summary>Prośba o całe skonfigurowane okno (komunikat ostemplowany przez magistralę).</summary>
    private static bool IsFullRequest(PeerMessage message) => message is DowntimeSyncRequest request && request.Since == request.SentAt - Window;

    private static DowntimeEpisode Episode(string machine, DateTimeOffset start, TimeSpan length, string origin) =>
        new(machine, "Aidite " + machine[^1], "Servo 6", start, start + length, start + length,
            EpisodeId: Guid.NewGuid(), Origin: origin, Category: "restart");

    /// <summary>Ta sama reguła, co <c>AppOrchestrator.OnRemoteEpisodesReceived</c> (jak w <see cref="DowntimeReconnectRequestIdTests"/>).</summary>
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

    /// <summary>Wspólna numeracja wysłanych komunikatów wszystkich magistral — kolejność doręczeń w magistrali w pamięci.</summary>
    private sealed class Wire
    {
        private long _index;
        public long Next() => Interlocked.Increment(ref _index);
    }

    private sealed record Sent(long Index, DateTimeOffset At, PeerMessage Message);

    /// <summary>
    /// Magistrala nad <see cref="InMemoryPeerBus"/>, która zapisuje wysyłane prośby i odpowiedzi o postoje (oraz, na żądanie,
    /// heartbeaty) z numerem z <see cref="Wire"/> — bez dodatkowego dekodowania każdego pakietu. <see cref="Transform"/> zmienia
    /// komunikat przed wysłaniem (atrapa odpowiadającego, który nie odsyła <c>RequestId</c>).
    /// </summary>
    private sealed class RecordingBus(InMemoryPeerBus inner, IClock clock, Wire wire) : IPeerBus
    {
        public List<Sent> Log { get; } = [];
        public bool LogHeartbeats { get; set; }
        public Func<PeerMessage, PeerMessage>? Transform { get; init; }
        public string LocalHost => inner.LocalHost;

        public event Action<PeerMessage>? Received
        {
            add => inner.Received += value;
            remove => inner.Received -= value;
        }

        public event Action<string>? Rejected
        {
            add => inner.Rejected += value;
            remove => inner.Rejected -= value;
        }

        public Task StartAsync(CancellationToken ct) => inner.StartAsync(ct);

        public Task SendAsync(PeerMessage msg, CancellationToken ct)
        {
            if (Transform is { } transform) msg = transform(msg);
            if (msg is DowntimeSyncRequest or DowntimeSyncReply || LogHeartbeats && msg is HeartbeatMessage)
                Log.Add(new Sent(wire.Next(), clock.UtcNow, msg));
            return inner.SendAsync(msg, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private enum Delivery { Pass, Drop, Hold }

    /// <summary>
    /// Magistrala testowa (jak <c>ControlledBus</c>): stempluje komunikat dokładnie jak prawdziwa (Seq, SentAt,
    /// InstanceId, podpis), a potem według <see cref="Filter"/> doręcza od razu, gubi (utrata UDP) albo przetrzymuje gotowy datagram
    /// (opóźnienie w sieci — doręczony później niesie PIERWOTNE Seq i SentAt i przechodzi zwykłe TryDecode odbiorcy).
    /// </summary>
    private sealed class HoldingBus : IPeerBus
    {
        private readonly InMemoryPeerHub _hub;
        private readonly InMemoryPeerBus _inner;
        private readonly IClock _clock;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private readonly List<byte[]> _held = [];
        private long _seq;

        public HoldingBus(InMemoryPeerHub hub, string host, IClock clock)
        {
            _hub = hub;
            _clock = clock;
            _inner = hub.CreateBus(host);
        }

        public Func<PeerMessage, Delivery>? Filter { get; set; }
        public List<(DateTimeOffset At, PeerMessage Message, Delivery Delivery)> Log { get; } = [];
        public int HeldCount => _held.Count;
        public string LocalHost => _inner.LocalHost;

        public event Action<PeerMessage>? Received
        {
            add => _inner.Received += value;
            remove => _inner.Received -= value;
        }

        public event Action<string>? Rejected
        {
            add => _inner.Rejected += value;
            remove => _inner.Rejected -= value;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(PeerMessage msg, CancellationToken ct)
        {
            var stamped = msg with { Sender = LocalHost, Seq = Interlocked.Increment(ref _seq), SentAt = _clock.UtcNow, InstanceId = _instanceId };
            var datagram = MessageCodec.Encode(stamped, _hub.Signer);
            var delivery = Filter?.Invoke(stamped) ?? Delivery.Pass;
            if (stamped is DowntimeSyncRequest or DowntimeSyncReply) Log.Add((_clock.UtcNow, stamped, delivery));
            if (delivery == Delivery.Hold) _held.Add(datagram);
            else if (delivery == Delivery.Pass) _hub.Dispatch(_inner, datagram);
            return Task.CompletedTask;
        }

        public int ReleaseHeld()
        {
            var held = _held.ToArray();
            _held.Clear();
            foreach (var datagram in held) _hub.Dispatch(_inner, datagram);
            return held.Length;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>
    /// Jeden prawdziwy węzeł „A” z prawdziwym dziennikiem; partnerzy to gołe magistrale, z których test wysyła heartbeaty
    /// i odpowiedzi (koperta, Seq, HMAC i okno czasu jak w UDP). Prośby A zapisuje podsłuch „S” razem z tym, czyj heartbeat
    /// je wywołał. Dziennik A ma świeży wpis (<see cref="Latest"/>), więc ogon różni się od pełnego okna.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDir _dir = new("napr-requestid");
        private readonly Dictionary<string, (InMemoryPeerBus Bus, string Instance)> _peers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
        private string? _trigger;

        private Rig(int clockSkewToleranceSec, DateTimeOffset latest)
        {
            var group = GroupFileTests.Invitation();
            Config = new ClusterConfig { Enabled = true, Group = group.Identity, ClockSkewToleranceSec = clockSkewToleranceSec };
            Hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), Clock);
            Hub.CreateBus("S", Config).Received += m =>
            {
                if (m is DowntimeSyncRequest request && request.Sender == "A") Requests.Add((Clock.UtcNow, _trigger, request));
            };
            Latest = latest;
            Journal = new DowntimeJournal(_dir.File("a.jsonl"));
            Journal.Merge(Episode("aidite7", latest - TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), "A"));
            Node = new GroupClusterNode(Config, Hub.CreateBus("A", Config), Clock, "test") { Downtime = Journal };
        }

        public FakeClock Clock { get; } = new(T0);
        public ClusterConfig Config { get; }
        public InMemoryPeerHub Hub { get; }
        public GroupClusterNode Node { get; }
        public DowntimeJournal Journal { get; }
        public DateTimeOffset Latest { get; }

        /// <summary>Prośby A o postoje: kiedy, czyj heartbeat ją wywołał, treść (z <c>SentAt</c> nadanym przez magistralę).</summary>
        public List<(DateTimeOffset At, string? Trigger, DowntimeSyncRequest Request)> Requests { get; } = [];

        public static async Task<Rig> StartAsync(int clockSkewToleranceSec = 300, DateTimeOffset? latest = null)
        {
            var rig = new Rig(clockSkewToleranceSec, latest ?? T0.AddMinutes(-1));
            await rig.Node.StartAsync(default);
            return rig;
        }

        public void Heartbeat(string host)
        {
            _trigger = host;
            try
            {
                Deliver(host, new HeartbeatMessage { Sender = host, Version = "test", Role = PeerRole.Ui, Readiness = new(false, false, []) });
            }
            finally { _trigger = null; }
        }

        /// <summary>Co sekundę (przez <paramref name="seconds"/> s): zegar +1 s, potem heartbeat podanych partnerów w tej kolejności.</summary>
        public void Pump(int seconds, params string[] hosts)
        {
            for (int i = 0; i < seconds; i++)
            {
                Clock.Advance(Second);
                foreach (string host in hosts) Heartbeat(host);
            }
        }

        public void Reply(string host, Guid requestId, params DowntimeEpisode[] episodes) =>
            Deliver(host, new DowntimeSyncReply { Sender = host, Version = "test", ReplyTo = "A", RequestId = requestId, Episodes = episodes });

        public (DateTimeOffset At, string? Trigger, DowntimeSyncRequest Request) Last(string trigger) => Requests.Last(r => r.Trigger == trigger);

        public IReadOnlyList<(DateTimeOffset At, string? Trigger, DowntimeSyncRequest Request)> TriggeredBy(string trigger) =>
            Requests.Where(r => r.Trigger == trigger).ToArray();

        /// <summary>Czas uzupełnienia, który Panel pokazuje dla partnera (<see cref="PeerInfo.DowntimeSyncedAt"/>).</summary>
        public DateTimeOffset? SyncedAt(string host) => Node.Peers.Single(p => p.Host == host).DowntimeSyncedAt;

        public bool IsFull(DowntimeSyncRequest request) =>
            request.Since == request.SentAt - TimeSpan.FromDays(Config.DowntimeSyncWindowDays);

        public bool IsTail(DowntimeSyncRequest request) => request.Since == Latest - TimeSpan.FromDays(1);

        private void Deliver(string host, PeerMessage message)
        {
            if (!_peers.TryGetValue(host, out var peer)) _peers[host] = peer = (Hub.CreateBus(host), Guid.NewGuid().ToString("N"));
            long sequence = _sequences[host] = _sequences.GetValueOrDefault(host) + 1;
            Hub.Dispatch(peer.Bus, MessageCodec.Encode(message with
                { Group = Config.Group, InstanceId = peer.Instance, Seq = sequence, SentAt = Clock.UtcNow }, Hub.Signer));
        }

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            _dir.Dispose();
        }
    }
}
