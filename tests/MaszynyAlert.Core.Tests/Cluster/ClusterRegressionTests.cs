using System.Net;
using System.Net.Sockets;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class ClusterRegressionTests
{
    private static readonly AlertKey TrayKey = AlertKey.ForRule("aidite1", "tray-grab");
    private static readonly AlertKey SpindleKey = AlertKey.ForRule("aidite2", "spindle");

    private static MachineConfig Machine(string id = "aidite1", string name = "Aidite 1") => new()
    {
        Id = id,
        Name = name,
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
    };

    private static MappedMessage TrayMessage() =>
        new("tray-grab", "Nie udało się pobrać tacki", "tacka", "Failed to grab a tray", "FAILED TO GRAB A TRAY", true);

    private static Alert SampleAlert(AlertKey? key = null, DateTimeOffset? at = null, AlertStatus status = AlertStatus.Active) => new()
    {
        Key = key ?? TrayKey,
        MachineId = (key ?? TrayKey).MachineId,
        MachineName = "Aidite 1",
        Kind = AlertKind.Error,
        ShortText = "Nie udało się pobrać tacki",
        FullText = "Failed to grab a tray",
        Category = "tacka",
        StartedAt = at ?? FakeClock.DefaultStart,
        LastSeenAt = at ?? FakeClock.DefaultStart,
        Status = status,
        Sound = true,
    };

    private static MachineStatusSnapshot Snapshot(string machineId = "aidite1") => new()
    {
        MachineId = machineId,
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
        State = MachineConnectionState.Connected,
        Source = "vnc",
        UpdatedAt = FakeClock.DefaultStart,
    };


    [Fact]
    public async Task DolaczenieLepszegoObserwatora_GdyPoprzedniUstepujeIGasiAlert_AlertZnikaNaWszystkichKomputerach()
    {
        await using var c = new TestCluster();
        TestNode a = await c.AddAsync("PC-A", priority: 10);
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        await c.TickSecondsAsync(25);
        Assert.True(a.Node.IsWatcher);

        a.Node.SetLocalState([SampleAlert()], [Snapshot()]);
        await c.TickSecondsAsync(2);
        Assert.Single(b.Node.MergedAlerts);

        // PC-A zachowuje się jak prawdziwa aplikacja: tracąc rolę, zatrzymuje nadzorcę i czyści swój stan lokalny.
        a.Node.RoleChanged += isWatcher =>
        {
            if (!isWatcher)
            {
                a.Node.SetLocalState([], []);
            }
        };

        TestNode nowy = await c.AddAsync("PC-C", priority: 0);
        int sekundDoPrzejecia = await c.TickUntilAsync(() => nowy.Node.IsWatcher, maxSeconds: 40);

        Assert.InRange(sekundDoPrzejecia, 1, 40);
        Assert.False(a.Node.IsWatcher);

        await c.TickSecondsAsync(5);

        Assert.Equal("PC-C", b.Node.WatcherHost);
        Assert.Empty(b.Node.MergedAlerts);
        Assert.Empty(nowy.Node.MergedAlerts);
        Assert.Empty(a.Node.MergedAlerts);
    }

    [Fact]
    public async Task ZapamietanyStanObserwatora_GdyPrzestajePrzychodzicSTATE_WypadaZeScaleniaPoPieciuSekundach()
    {
        await using var c = new TestCluster();
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        const string ghost = "PC-OBSERWATOR";
        const string instancja = "instancja-1";
        long seq = 0;

        void Heartbeat() => c.Inject(b, new HeartbeatMessage
        {
            Sender = ghost,
            Version = "0.1.0",
            Seq = ++seq,
            InstanceId = instancja,
            Priority = 5,
            Role = PeerRole.Watcher,
        });

        void State() => c.Inject(b, new StateMessage
        {
            Sender = ghost,
            Version = "0.1.0",
            Seq = ++seq,
            InstanceId = instancja,
            Alerts = [SampleAlert()],
        });

        for (int i = 0; i < 3; i++)
        {
            Heartbeat();
            State();
            await c.TickSecondsAsync(1);
        }

        Assert.Single(b.Node.MergedAlerts);

        // Obserwator milczy (tylko heartbeaty): przez pierwsze sekundy stan jest jeszcze świeży.
        Heartbeat();
        await c.TickSecondsAsync(2);
        Assert.Single(b.Node.MergedAlerts);

        for (int i = 0; i < 5; i++)
        {
            Heartbeat();
            await c.TickSecondsAsync(1);
        }

        Assert.Empty(b.Node.MergedAlerts);
        Assert.Contains(b.Node.Peers, p => p.Host == ghost);
    }


    [Fact]
    public async Task StateZKluczamiZamknietychEpizodow_ZapominaPotwierdzeniaTylkoKluczyJuzNieaktywnych()
    {
        await using var c = new TestCluster();
        TestNode a = await c.AddAsync("PC-A", priority: 10);
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        await c.TickSecondsAsync(25);
        Assert.True(a.Node.IsWatcher);

        a.Node.SetLocalState([SampleAlert(TrayKey), SampleAlert(SpindleKey)], []);
        await c.TickSecondsAsync(1);

        await b.Node.SendAckAsync(TrayKey, "operator", CancellationToken.None);
        await b.Node.SendAckAsync(SpindleKey, "operator", CancellationToken.None);
        Assert.True(b.Acks.IsAcked(TrayKey));
        Assert.True(b.Acks.IsAcked(SpindleKey));

        // Obserwator zamknął oba epizody, ale „spindle” natychmiast wystąpił ponownie i jest znowu aktywny.
        a.Node.SetLocalState([SampleAlert(SpindleKey, at: c.Clock.UtcNow)], [], recentlyClosed: [TrayKey, SpindleKey]);
        await c.TickSecondsAsync(1);

        Assert.False(b.Acks.IsAcked(TrayKey));
        Assert.True(b.Acks.IsAcked(SpindleKey));
    }


    [Fact]
    public async Task OdebranyStateZAlertemPotwierdzonym_ZapisujePotwierdzenieWPamieciOdbiorcy()
    {
        await using var c = new TestCluster();
        TestNode a = await c.AddAsync("PC-A", priority: 10);
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        await c.TickSecondsAsync(25);
        Assert.False(b.Acks.IsAcked(TrayKey));

        Alert potwierdzony = SampleAlert() with
        {
            Status = AlertStatus.Acknowledged,
            AcknowledgedAt = c.Clock.UtcNow,
            AckedByHost = "PC-Z",
            AckedByUser = "zenek",
        };
        a.Node.SetLocalState([potwierdzony], []);
        await c.TickSecondsAsync(1);

        Assert.True(b.Acks.IsAcked(TrayKey));
        AckRecord? zapamietany = b.Acks.Get(TrayKey);
        Assert.NotNull(zapamietany);
        Assert.Equal("PC-Z", zapamietany.Host);
        Assert.Equal("zenek", zapamietany.User);
        Assert.Equal(potwierdzony.AcknowledgedAt, zapamietany.At);
    }

    [Fact]
    public async Task PoPrzejeciuRoli_MaszynaStanowZPamieciaAckowZeState_NieWznawiaPotwierdzonegoAlertu()
    {
        await using var c = new TestCluster();
        TestNode a = await c.AddAsync("PC-A", priority: 10);
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        await c.TickSecondsAsync(25);

        a.Node.SetLocalState(
            [
                SampleAlert() with
                {
                    Status = AlertStatus.Acknowledged,
                    AcknowledgedAt = c.Clock.UtcNow,
                    AckedByHost = "PC-Z",
                    AckedByUser = "zenek",
                },
            ],
            []);
        await c.TickSecondsAsync(1);

        // PC-A znika z sieci — PC-B po okresie oczekiwania przejmuje rolę obserwatora.
        c.Hub.Partition("PC-A", "PC-B");
        int sekund = await c.TickUntilAsync(() => b.Node.IsWatcher, maxSeconds: 60);
        Assert.InRange(sekund, 1, 60);

        var maszynaStanow = new AlertStateMachine(c.Clock, new TimingConfig(), b.Acks);
        var zdarzenia = new List<AlertEvent>();
        maszynaStanow.Changed += zdarzenia.Add;

        Alert wznowiony = maszynaStanow.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Equal(TrayKey, wznowiony.Key);
        Assert.Equal(AlertStatus.Acknowledged, wznowiony.Status);
        Assert.Equal("PC-Z", wznowiony.AckedByHost);
        Assert.Equal(AlertEventKind.Updated, Assert.Single(zdarzenia).Kind);
        Assert.DoesNotContain(zdarzenia, e => e.Kind == AlertEventKind.Raised);
    }


    [Fact]
    public async Task ZgubionyPakietAck_DocieraWKolejnymHeartbeacie_DokladnieRaz()
    {
        await using var c = new TestCluster();
        TestNode a = await c.AddAsync("PC-A", priority: 10);
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        await c.TickSecondsAsync(25);
        a.Node.SetLocalState([SampleAlert()], []);
        await c.TickSecondsAsync(1);

        var odebranePrzezA = new List<AckRecord>();
        a.Node.AckReceived += odebranePrzezA.Add;

        c.Hub.Deliver = false; // pakiet ACK ginie w całości
        await b.Node.SendAckAsync(TrayKey, "operator", CancellationToken.None);
        c.Hub.Deliver = true;

        Assert.Empty(odebranePrzezA);
        Assert.False(a.Acks.IsAcked(TrayKey));

        await c.TickSecondsAsync(3);

        AckRecord ack = Assert.Single(odebranePrzezA);
        Assert.Equal("PC-B", ack.Host);
        Assert.Equal("operator", ack.User);
        Assert.True(a.Acks.IsAcked(TrayKey));
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(a.Node.MergedAlerts).Status);
    }

    [Fact]
    public async Task Heartbeat_GdyPamiecAckowMaTrzydziesciWpisow_NiesieNajwyzejDwadziescia()
    {
        await using var c = new TestCluster();
        TestNode a = await c.AddAsync("PC-A", priority: 10);
        TestSniffer podsluch = c.AddSniffer();
        await c.TickSecondsAsync(1);

        for (int i = 0; i < 30; i++)
        {
            a.Acks.Record(new AckRecord(AlertKey.ForRule($"aidite{i}", "tray-grab"), "PC-B", "operator", c.Clock.UtcNow));
        }

        await a.Node.TickAsync(CancellationToken.None);

        HeartbeatMessage hb = podsluch.LastHeartbeatFrom("PC-A");
        Assert.Equal(20, hb.RecentAcks.Count);
        Assert.Equal(20, hb.RecentAcks.Select(r => r.Key).Distinct().Count());
    }


    [Fact]
    public async Task WyjatekWHandlerzeOdbioru_NiePrzerywaDoreczaniaPozostalymKomputeromAniNadawcy()
    {
        var clock = new FakeClock();
        var hub = new InMemoryPeerHub(new HmacSigner(HmacSigner.GenerateKey()), clock);
        await using InMemoryPeerBus nadawca = hub.CreateBus("PC-NADAWCA");
        await using InMemoryPeerBus rzucajacy = hub.CreateBus("PC-RZUCAJACY");
        await using InMemoryPeerBus kolejny = hub.CreateBus("PC-KOLEJNY");

        int prob = 0;
        rzucajacy.Received += _ =>
        {
            prob++;
            throw new InvalidOperationException("celowy błąd obsługi pakietu w kodzie UI");
        };
        var odebranePrzezKolejnego = new List<PeerMessage>();
        kolejny.Received += odebranePrzezKolejnego.Add;

        await nadawca.SendAsync(NowyHeartbeat(clock), CancellationToken.None);
        await nadawca.SendAsync(NowyHeartbeat(clock), CancellationToken.None);

        Assert.Equal(2, prob);
        Assert.Equal(2, odebranePrzezKolejnego.Count);
    }

    [Fact]
    public async Task WyjatekPrzyPierwszymPakiecie_NieZatrzymujeOdbieraniaNastepnych()
    {
        var clock = new FakeClock();
        var hub = new InMemoryPeerHub(new HmacSigner(HmacSigner.GenerateKey()), clock);
        await using InMemoryPeerBus nadawca = hub.CreateBus("PC-NADAWCA");
        await using InMemoryPeerBus odbiorca = hub.CreateBus("PC-ODBIORCA");

        int wywolania = 0;
        var poWyjatku = new List<PeerMessage>();
        odbiorca.Received += msg =>
        {
            wywolania++;
            if (wywolania == 1)
            {
                throw new InvalidOperationException("celowy błąd obsługi pierwszego pakietu");
            }

            poWyjatku.Add(msg);
        };

        for (int i = 0; i < 3; i++)
        {
            await nadawca.SendAsync(NowyHeartbeat(clock), CancellationToken.None);
        }

        Assert.Equal(3, wywolania);
        Assert.Equal(2, poWyjatku.Count);
    }

    [Fact]
    public async Task WyjatekWHandlerzeOdrzucenia_NieZatrzymujeMagistrali()
    {
        var clock = new FakeClock();
        var hub = new InMemoryPeerHub(new HmacSigner(HmacSigner.GenerateKey()), clock);
        await using InMemoryPeerBus nadawca = hub.CreateBus("PC-NADAWCA");
        await using InMemoryPeerBus odbiorca = hub.CreateBus("PC-ODBIORCA");

        int odrzucenia = 0;
        odbiorca.Rejected += _ =>
        {
            odrzucenia++;
            throw new InvalidOperationException("celowy błąd obsługi odrzuconego pakietu");
        };
        var odebrane = new List<PeerMessage>();
        odbiorca.Received += odebrane.Add;

        odbiorca.DeliverIncoming([1, 2, 3, 4, 5]); // pakiet nie do odszyfrowania

        await nadawca.SendAsync(NowyHeartbeat(clock), CancellationToken.None);

        Assert.Equal(1, odrzucenia);
        Assert.Single(odebrane);
    }

    [Fact]
    public async Task SzynaUdp_PoWyjatkuWHandlerze_NadalOdbieraKolejnePakiety()
    {
        int port = WolnyPortUdp();
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        IClock clock = SystemClock.Instance;
        ClusterConfig Cfg() => new() { UdpPort = port, Peers = ["127.0.0.1"] };

        UdpPeerBus? nadawca = null;
        UdpPeerBus? odbiorca = null;
        try
        {
            odbiorca = new UdpPeerBus(Cfg(), signer, clock, "PC-ODBIORCA");
            nadawca = new UdpPeerBus(Cfg(), signer, clock, "PC-NADAWCA");
        }
        catch (SocketException ex)
        {
            await ZamknijAsync(nadawca, odbiorca);
            Assert.Fail(
                "To środowisko nie pozwala dwóm gniazdom UDP nasłuchiwać na tym samym porcie " +
                $"(SO_REUSEADDR), więc testu szyny UDP nie da się tu wykonać: {ex.Message}");
            return;
        }

        try
        {
            int wywolania = 0;
            int poWyjatku = 0;
            odbiorca.Received += _ =>
            {
                if (Interlocked.Increment(ref wywolania) == 1)
                {
                    throw new InvalidOperationException("celowy błąd obsługi pierwszego pakietu");
                }

                Interlocked.Increment(ref poWyjatku);
            };

            await odbiorca.StartAsync(CancellationToken.None);
            await nadawca.StartAsync(CancellationToken.None);

            var heartbeat = new HeartbeatMessage
            {
                Sender = "PC-NADAWCA",
                Version = "0.1.0",
                SentAt = clock.UtcNow,
                Priority = 42,
                Role = PeerRole.Watcher,
            };

            for (int i = 0; i < 12 && Volatile.Read(ref poWyjatku) == 0; i++)
            {
                await nadawca.SendAsync(heartbeat, CancellationToken.None);
                await Task.Delay(250);
            }

            if (Volatile.Read(ref wywolania) == 0)
            {
                Assert.Fail(
                    "Żaden pakiet UDP nie dotarł przez pętlę zwrotną — to środowisko blokuje rozgłoszenia UDP " +
                    "albo nie doręcza pakietów do dwóch gniazd na tym samym porcie. Test wymaga zwykłej sieci LAN.");
            }

            Assert.True(
                Volatile.Read(ref poWyjatku) > 0,
                "Po wyjątku rzuconym z handlera Received pętla nasłuchu przestała odbierać pakiety.");
        }
        finally
        {
            await ZamknijAsync(nadawca, odbiorca);
        }
    }


    [Fact]
    public void Observe_PoRestarcieNadawcy_NowyInstanceIdZNumeremJedenJestPrzyjmowany()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        table.Observe(Heartbeat("PC-A", seq: 500, clock.UtcNow, instanceId: "instancja-1"));

        Assert.True(table.Observe(Heartbeat("PC-A", seq: 1, clock.UtcNow, instanceId: "instancja-2")));

        PeerInfo peer = Assert.Single(table.All);
        Assert.Equal(1, peer.LastSeq);
        Assert.Equal("instancja-2", peer.InstanceId);
    }

    [Fact]
    public void Observe_TejSamejInstancjiZNiewiekszymNumerem_JestOdrzucany()
    {
        var clock = new FakeClock();
        var table = new PeerTable(clock, new ClusterConfig());
        table.Observe(Heartbeat("PC-A", seq: 500, clock.UtcNow, instanceId: "instancja-1"));

        Assert.False(table.Observe(Heartbeat("PC-A", seq: 499, clock.UtcNow, instanceId: "instancja-1")));
        Assert.False(table.Observe(Heartbeat("PC-A", seq: 500, clock.UtcNow, instanceId: "instancja-1")));
        Assert.True(table.Observe(Heartbeat("PC-A", seq: 501, clock.UtcNow, instanceId: "instancja-1")));
        Assert.Equal(501, Assert.Single(table.All).LastSeq);
    }

    [Fact]
    public async Task PoRestarcieProgramuNaKomputerze_SasiadWidziGoOdPierwszegoHeartbeatu()
    {
        await using var c = new TestCluster();
        TestNode a = await c.AddAsync("PC-A", priority: 10);
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        await c.TickSecondsAsync(25);
        Assert.True(a.Node.Peers.Single(p => p.Host == "PC-B").LastSeq > 20);

        await c.RestartAsync(b);
        await c.TickSecondsAsync(1);

        PeerInfo pcB = a.Node.Peers.Single(p => p.Host == "PC-B");
        Assert.Equal(1, pcB.LastSeq);
        Assert.Equal(c.Clock.UtcNow.AddSeconds(-1), pcB.LastSeen);
    }


    [Fact]
    public async Task StareStateZNizszymNumerem_JestIgnorowane()
    {
        await using var c = new TestCluster();
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        const string obserwator = "PC-OBSERWATOR";

        c.Inject(b, new HeartbeatMessage
        {
            Sender = obserwator,
            Version = "0.1.0",
            Seq = 1,
            InstanceId = "instancja-1",
            Priority = 5,
            Role = PeerRole.Watcher,
        });

        c.Inject(b, new StateMessage
        {
            Sender = obserwator,
            Version = "0.1.0",
            Seq = 10,
            InstanceId = "instancja-1",
            Alerts = [],
        });

        c.Inject(b, new StateMessage
        {
            Sender = obserwator,
            Version = "0.1.0",
            Seq = 5,
            InstanceId = "instancja-1",
            Alerts = [SampleAlert()],
        });

        Assert.Empty(b.Node.MergedAlerts);
    }

    [Fact]
    public async Task StatePoRestarcieObserwatora_ZNowymInstanceId_JestPrzyjmowane()
    {
        await using var c = new TestCluster();
        TestNode b = await c.AddAsync("PC-B", priority: 20);
        const string obserwator = "PC-OBSERWATOR";

        c.Inject(b, new HeartbeatMessage
        {
            Sender = obserwator,
            Version = "0.1.0",
            Seq = 1,
            InstanceId = "instancja-1",
            Priority = 5,
            Role = PeerRole.Watcher,
        });
        c.Inject(b, new StateMessage
        {
            Sender = obserwator,
            Version = "0.1.0",
            Seq = 10,
            InstanceId = "instancja-1",
            Alerts = [],
        });

        c.Inject(b, new StateMessage
        {
            Sender = obserwator,
            Version = "0.1.0",
            Seq = 1,
            InstanceId = "instancja-2",
            Alerts = [SampleAlert()],
        });

        Assert.Single(b.Node.MergedAlerts);
    }

    private static HeartbeatMessage Heartbeat(string sender, long seq, DateTimeOffset sentAt, string instanceId) => new()
    {
        Sender = sender,
        Version = "0.1.0",
        Seq = seq,
        SentAt = sentAt,
        InstanceId = instanceId,
        Priority = 100,
        Role = PeerRole.Ui,
    };

    private static HeartbeatMessage NowyHeartbeat(FakeClock clock) => new()
    {
        Sender = "PC-NADAWCA",
        Version = "0.1.0",
        SentAt = clock.UtcNow,
        Priority = 100,
        Role = PeerRole.Ui,
    };

    private static int WolnyPortUdp()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task ZamknijAsync(UdpPeerBus? a, UdpPeerBus? b)
    {
        if (a is not null)
        {
            await a.DisposeAsync();
        }

        if (b is not null)
        {
            await b.DisposeAsync();
        }
    }
}
