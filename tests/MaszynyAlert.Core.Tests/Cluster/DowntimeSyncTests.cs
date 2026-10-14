using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class DowntimeSyncTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private readonly InMemoryPeerHub _hub;
    private readonly ClusterConfig _config;
    private readonly GroupClusterNode _a, _b;
    private readonly InMemoryPeerBus _aBus, _bBus;
    private readonly string _aJournalPath = Path.Combine(Path.GetTempPath(), "downtime-sync-a-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly string _bJournalPath = Path.Combine(Path.GetTempPath(), "downtime-sync-b-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly DowntimeJournal _aJournal, _bJournal;

    public DowntimeSyncTests()
    {
        var file = GroupFileTests.Invitation();
        _config = new ClusterConfig { Enabled = true, Group = file.Identity };
        // Ziarno strat podane zawsze; domyślnie LossRate = 0, więc zwykłe testy nic nie tracą, a test
        // porcjowania może włączyć gubienie datagramów bez budowania własnego hubu.
        _hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), _clock, lossSeed: 20260915);
        _aBus = _hub.CreateBus("A"); _bBus = _hub.CreateBus("B");
        _a = new GroupClusterNode(_config, _aBus, _clock, "test");
        _b = new GroupClusterNode(_config with { Priority = 200 }, _bBus, _clock, "test");
        _a.SetReadiness(new(true, false, [])); _b.SetReadiness(new(true, false, []));
        _aJournal = new DowntimeJournal(_aJournalPath);
        _bJournal = new DowntimeJournal(_bJournalPath);
        _a.EpisodeStateReceived += (alerts, closures) => Replicate(_aJournal, alerts, closures);
        _b.EpisodeStateReceived += (alerts, closures) => Replicate(_bJournal, alerts, closures);
        // dziennik jest jednocześnie źródłem odpowiedzi dla partnera i miejscem na jego
        // uzupełnienia — dokładnie tak, jak wpina go AppOrchestrator.
        _a.Downtime = _aJournal; _b.Downtime = _bJournal;
    }

    /// <summary>Ta sama reguła, co <c>AppOrchestrator.OnRemoteEpisodesReceived</c> (bez wykluczenia testu z pliku — nieistotne tutaj).</summary>
    private static void Replicate(DowntimeJournal journal, IReadOnlyList<Alert> alerts, IReadOnlyList<EpisodeClosure> closures)
    {
        foreach (var alert in alerts)
        {
            if (alert.Kind != AlertKind.Error) continue;
            journal.Merge(new DowntimeEpisode(alert.MachineId, alert.MachineName, alert.ShortText,
                alert.StartedAt, alert.LastSeenAt, alert.Status == AlertStatus.Closed ? alert.LastSeenAt : null,
                EpisodeId: alert.EpisodeId,
                Category: string.IsNullOrWhiteSpace(alert.Category) ? null : alert.Category));
        }
        foreach (var closure in closures)
        {
            if (closure.Kind != AlertKind.Error) continue;
            journal.MergeClosure(closure.EpisodeId, closure.At);
        }
    }

    private Alert Error(string reason = "Servo 6") => new()
    {
        Key = AlertKey.ForRule("aidite2", "code-705"), MachineId = "aidite2", MachineName = "Aidite 2",
        ShortText = reason, Category = "restart", Kind = AlertKind.Error,
        StartedAt = _clock.UtcNow, LastSeenAt = _clock.UtcNow, Status = AlertStatus.Active,
    };

    /// <summary>Start + tyle tyknięć, ile trzeba, żeby wybór obserwatora się rozstrzygnął (TakeoverDelayMs 20 s).</summary>
    private async Task Start()
    {
        await _a.StartAsync(default); await _b.StartAsync(default);
        await Tick(23);
    }

    private async Task Tick(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            await _a.TickAsync(default);
            await _b.TickAsync(default);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ObserverErrorAlertReachesThePeersDowntimeJournalWithinTwoSeconds()
    {
        // A jest obserwatorem (niższy priorytet): ustawia lokalny stan PRZED startem, tak jak
        // AppOrchestrator robi to co cykl przez ClusterRuntime.SetLocalState.
        var alert = Error();
        _a.SetLocalState([alert], []);
        await Start();
        await Tick(2);

        var episode = Assert.Single(_bJournal.Snapshot());
        Assert.Equal(alert.EpisodeId, episode.EpisodeId);
        Assert.Equal("aidite2", episode.MachineId);
        Assert.Null(episode.EndedAt);
        // Obserwator sam też ma wpis (SetLocalState woła EpisodeStateReceived także lokalnie) — no-op
        // względem AppOrchestrator.RecordDowntime, ale tutaj to jedyne źródło danych A.
        Assert.Single(_aJournal.Snapshot());
    }

    [Fact]
    public async Task ClosureReachesThePeersDowntimeJournal()
    {
        var alert = Error();
        _a.SetLocalState([alert], []);
        await Start();
        await Tick(2);
        Assert.Null(Assert.Single(_bJournal.Snapshot()).EndedAt);

        // Mały skok zegara (nie przez bezpośredni _clock.Advance) — większy niż PeerTimeoutMs zresetowałby
        // wybór obserwatora i test czekałby kolejne 20 s na ponowne rozstrzygnięcie.
        await Tick(5);
        var closed = alert with { Status = AlertStatus.Closed, LastSeenAt = _clock.UtcNow, ClosedAt = _clock.UtcNow };
        _a.RecordClosure(closed);
        await Tick(2);

        var episode = Assert.Single(_bJournal.Snapshot());
        Assert.NotNull(episode.EndedAt);
        // Koniec z ostatniej CZERWONEJ obserwacji, nie ze znacznika zamknięcia: ClosedAt niesie
        // opóźnienie potwierdzenia naprawy, a obserwator zapisuje u siebie LastSeenAt. Raport tego
        // samego postoju ma wyjść identycznie na obu komputerach.
        Assert.Equal(episode.LastObservedAt, episode.EndedAt);
        Assert.True(episode.EndedAt < closed.ClosedAt);
        Assert.False(episode.Interrupted);
    }

    [Fact]
    public async Task UpdatedTextOfTheSameEpisodeReachesThePeerWithoutANewEntry()
    {
        var alert = Error();
        _a.SetLocalState([alert], []);
        await Start();
        await Tick(2);

        await Tick(5);
        _a.SetLocalState([alert with { ShortText = "Servo 1", LastSeenAt = _clock.UtcNow }], []);
        await Tick(2);

        var episode = Assert.Single(_bJournal.Snapshot());
        Assert.Equal("Servo 1", episode.Reason);
    }

    [Fact]
    public async Task CategoryOfTheObserversAlertReachesThePeersJournal()
    {
        _a.SetLocalState([Error() with { Category = "tacka" }], []);
        await Start();
        await Tick(2);

        Assert.Equal("tacka", Assert.Single(_bJournal.Snapshot()).Category);
    }

    [Fact]
    public async Task CategoryTravelsWithTheSyncReplyAndAPeerWithoutItNeverErasesIt()
    {
        // Uzupełnianie po włączeniu (DowntimeSyncReply) niesie kategorię; wpis tego samego epizodu bez kategorii
        // (np. przekazany dalej przez 0.4.113) z NOWSZĄ obserwacją nie kasuje jej u odbiorcy.
        var past = Past(minutesAgo: 120) with { Category = "restart" };
        _aJournal.Merge(past);
        await Start();
        await Tick(2);
        Assert.Equal("restart", Assert.Single(_bJournal.Snapshot()).Category);

        _bJournal.Apply([past with { Category = null, LastObservedAt = past.LastObservedAt.AddSeconds(30) }]);

        Assert.Equal("restart", Assert.Single(_bJournal.Snapshot()).Category);
    }

    /// <summary>Epizod z przeszłości, jakiego bieżący <c>StateMessage</c> już nie niesie.</summary>
    private DowntimeEpisode Past(int minutesAgo, int lengthMinutes = 7, string machineId = "aidite2")
    {
        var start = _clock.UtcNow - TimeSpan.FromMinutes(minutesAgo);
        var end = start + TimeSpan.FromMinutes(lengthMinutes);
        return new DowntimeEpisode(machineId, "Aidite 2", "Servo 6", start, end, end, EpisodeId: Guid.NewGuid(),
            Origin: "A");
    }

    [Fact]
    public async Task ComputerThatWasOffDuringTheEpisodeGetsItFromThePeerThatSawIt()
    {
        // epizod jest WYŁĄCZNIE w dzienniku A i już się skończył, więc replikacja na
        // żywo (alerty w STATE) nie ma czym go dostarczyć. Jedyną drogą jest uzupełnianie na żądanie.
        var past = Past(minutesAgo: 120);
        _aJournal.Merge(past);
        Assert.Empty(_bJournal.Snapshot());

        await Start();
        await Tick(2);

        var episode = Assert.Single(_bJournal.Snapshot());
        Assert.Equal(past.EpisodeId, episode.EpisodeId);
        Assert.Equal(past.StartedAt, episode.StartedAt);
        Assert.Equal(past.EndedAt, episode.EndedAt);
        Assert.False(episode.Interrupted);
        // Uzupełnianie jest symetryczne: A pyta B dokładnie tak samo, więc epizod nie zniknął z A.
        Assert.Single(_aJournal.Snapshot());
    }

    [Fact]
    public async Task RepeatedSyncNeverDuplicatesAnEntry()
    {
        _aJournal.Merge(Past(minutesAgo: 120));
        await Start();
        await Tick(2);
        Assert.Single(_bJournal.Snapshot());

        // Kolejne prośby lecą dalej (odstęp pierwszej prośby to 10 s) — scalanie jest idempotentne,
        // więc nie mogą rozmnożyć wpisu ani u proszącego, ani u odpowiadającego.
        await Tick(30);

        Assert.Single(_bJournal.Snapshot());
        Assert.Single(_aJournal.Snapshot());
    }

    [Fact]
    public async Task ManyEpisodesArriveInChunksEvenWhenEveryThirdDatagramIsLost()
    {
        // Porcjowanie: 400 wpisów nie mieści się w limicie 256 wpisów na porcję, więc odpowiedź musi
        // pójść kilkoma. Zgubioną porcję naprawia kolejna prośba — nie ma retransmisji na poziomie UDP.
        for (int i = 0; i < 400; i++) _aJournal.Merge(Past(minutesAgo: 600 + i, machineId: "aidite" + (i % 7 + 1)));
        await Start();
        _hub.LossRate = 1.0 / 3.0;

        await Tick(180);
        _hub.LossRate = 0;
        await Tick(30);

        Assert.Equal(400, _bJournal.Snapshot().Count);
    }

    [Fact]
    public async Task ReplyAddressedToAnotherComputerIsIgnored()
    {
        // ReplyTo pilnuje, żeby porcja adresowana do trzeciego komputera nie trafiła do dziennika B —
        // ta sama zasada, co w GroupProbeMessage i InstructionsSyncReply.
        await Start();
        await _aBus.SendAsync(new DowntimeSyncReply
        {
            Sender = "A", Version = "test", Group = _config.Group, RequestId = Guid.NewGuid(), ReplyTo = "C",
            Episodes = [Past(minutesAgo: 300)],
        }, default);
        await Tick(2);

        Assert.Empty(_bJournal.Snapshot());
    }

    [Fact]
    public async Task InvalidEpisodeFromTheNetworkNeverEntersTheJournal()
    {
        // Dziennik czyta się w całości przy każdym starcie programu: wpis bez tożsamości, bez maszyny,
        // z końcem przed początkiem albo z tekstem na kilobajty nie ma prawa się w nim znaleźć — choćby
        // przyszedł poprawnie podpisany, od komputera tej samej grupy.
        await Start();
        var now = _clock.UtcNow;
        DowntimeEpisode[] rubbish =
        [
            new("aidite2", "Aidite 2", "Servo", now, now, null),                       // brak EpisodeId
            new("", "Aidite 2", "Servo", now, now, null, EpisodeId: Guid.NewGuid()),   // brak maszyny
            new("aidite2", "Aidite 2", "Servo", now, now - TimeSpan.FromMinutes(5), null, EpisodeId: Guid.NewGuid()),
            new("aidite2", "Aidite 2", new string('x', 501), now, now, null, EpisodeId: Guid.NewGuid()),
            new("test", "Test", "Servo", now, now, null, EpisodeId: Guid.NewGuid()),   // maszyna testowa
        ];
        foreach (var bad in rubbish)
        {
            await _aBus.SendAsync(new DowntimeSyncReply
            {
                Sender = "A", Version = "test", Group = _config.Group, RequestId = Guid.NewGuid(), ReplyTo = "B",
                Episodes = [bad],
            }, default);
            await Tick(1);
        }

        Assert.Empty(_bJournal.Snapshot());
    }

    [Fact]
    public async Task PanelLearnsWhenEachComputerLastSuppliedItsEpisodes()
    {
        // Panel administracyjny pokazuje czas ostatniego uzupełnienia od każdego komputera.
        // Dopóki partner nie odpowiedział, ma być pusto — „brak” nie jest tym samym, co „zero postojów”.
        _aJournal.Merge(Past(minutesAgo: 120));
        Assert.Null(_b.Peers.SingleOrDefault(p => p.Host == "A")?.DowntimeSyncedAt);

        await Start();
        await Tick(2);

        var a = Assert.Single(_b.Peers, p => p.Host == "A");
        Assert.NotNull(a.DowntimeSyncedAt);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        // Sprzątanie plików tymczasowych nie może wywalić testu (jak TempDir); dziennik nie trzyma pliku otwartego.
        foreach (string path in new[] { _aJournalPath, _bJournalPath })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
