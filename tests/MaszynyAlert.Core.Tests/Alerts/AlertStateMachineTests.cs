using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineTests
{
    private static readonly TimingConfig Timing = new();

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

    private static MappedMessage SpindleMessage() =>
        new("spindle", "Błąd wrzeciona", "restart", "Spindle error", "SPINDLE ERROR", true);

    private static MappedMessage EmptyMessage() =>
        new(null, "Błąd (nie odczytano treści)", "inny", "", "", false);

    private sealed record Fixture(FakeClock Clock, AckMemory Acks, AlertStateMachine Machine, List<AlertEvent> Events);

    private static Fixture NewFixture(TimingConfig? timing = null, AckMemory? acks = null, FakeClock? clock = null)
    {
        FakeClock c = clock ?? new FakeClock();
        AckMemory memory = acks ?? new AckMemory(c, TimeSpan.FromHours(2));
        var sm = new AlertStateMachine(c, timing ?? Timing, memory);
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        return new Fixture(c, memory, sm, events);
    }

    [Fact]
    public void OnRedConfirmed_PierwszaCzerwien_TworzyAktywnyAlertZDzwiekiemIZdarzeniemRaised()
    {
        Fixture f = NewFixture();

        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), "captures/blad.png");

        Assert.Equal(AlertStatus.Active, alert.Status);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.True(alert.Sound);
        Assert.Equal("Aidite 1", alert.MachineName);
        Assert.Equal("Nie udało się pobrać tacki", alert.ShortText);
        Assert.Equal("Failed to grab a tray", alert.FullText);
        Assert.Equal("captures/blad.png", alert.CaptureFile);
        Assert.Equal(f.Clock.UtcNow, alert.StartedAt);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), alert.Key);

        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Raised, e.Kind);
        Assert.Single(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void OnRedConfirmed_TenSamKluczPonownie_NieGenerujeNowychZdarzenIOdswiezaLastSeen()
    {
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(5));
        Alert again = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Empty(f.Events);
        Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(f.Clock.UtcNow, again.LastSeenAt);
        Assert.NotEqual(again.StartedAt, again.LastSeenAt);
    }

    [Fact]
    public void Acknowledge_AktywnegoAlertu_ZmieniaStanIZapisujePotwierdzenieWPamieci()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Events.Clear();

        bool ok = f.Machine.Acknowledge(alert.Key, "PC-3", "jkowalski", f.Clock.UtcNow);

        Assert.True(ok);
        Alert acked = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, acked.Status);
        Assert.Equal("PC-3", acked.AckedByHost);
        Assert.Equal("jkowalski", acked.AckedByUser);
        Assert.Equal(f.Clock.UtcNow, acked.AcknowledgedAt);
        Assert.True(f.Acks.IsAcked(alert.Key));
        Assert.Equal(AlertEventKind.Acknowledged, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void Acknowledge_NieznanegoKlucza_ZwracaFalszAlePamietaPotwierdzenie()
    {
        Fixture f = NewFixture();
        AlertKey key = AlertKey.ForRule("aidite5", "spindle");

        bool ok = f.Machine.Acknowledge(key, "PC-1", "anna", f.Clock.UtcNow);

        Assert.False(ok);
        Assert.True(f.Acks.IsAcked(key));
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnRedConfirmed_GdyPamiecZnaJuzPotwierdzenie_TworzyAlertOdRazuPotwierdzonyBezZdarzeniaRaised()
    {
        Fixture f = NewFixture();
        AlertKey key = AlertKey.ForRule("aidite1", "tray-grab");
        f.Acks.Record(new AckRecord(key, "PC-2", "anna", f.Clock.UtcNow));

        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Equal(AlertStatus.Acknowledged, alert.Status);
        Assert.Equal("anna", alert.AckedByUser);
        Assert.Equal(AlertEventKind.Updated, Assert.Single(f.Events).Kind);
        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Fact]
    public void PrzejecieRoli_NowaMaszynaStanowZTaSamaPamieciaAckow_NieWznawiaPotwierdzonegoAlertu()
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        Fixture stary = NewFixture(acks: acks, clock: clock);
        Alert alert = stary.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        stary.Machine.Acknowledge(alert.Key, "PC-1", "anna", clock.UtcNow);

        // Inny komputer przejmuje rolę obserwatora: nowa maszyna stanów, ta sama pamięć potwierdzeń.
        Fixture nowy = NewFixture(acks: acks, clock: clock);
        clock.Advance(TimeSpan.FromSeconds(3));
        Alert wznowiony = nowy.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Equal(AlertStatus.Acknowledged, wznowiony.Status);
        Assert.DoesNotContain(nowy.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Fact]
    public void Tick_PoPietnastuMinutachCzerwieniOdPotwierdzenia_EskalujeAlert()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.Acknowledge(alert.Key, "PC-1", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        var escalations = new List<AlertEvent>();
        for (int i = 0; i < 60; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(20));
            f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
            escalations.AddRange(f.Machine.Tick().Where(e => e.Kind == AlertEventKind.Escalated));
            if (escalations.Count > 0)
            {
                break;
            }
        }

        AlertEvent escalated = Assert.Single(escalations);
        Assert.Equal(AlertStatus.Escalated, escalated.Alert.Status);
        Assert.NotNull(escalated.Alert.EscalatedAt);
        Assert.True(
            escalated.Alert.EscalatedAt - escalated.Alert.AcknowledgedAt >= TimeSpan.FromMilliseconds(Timing.EscalationAfterAckMs));
    }

    [Fact]
    public void Acknowledge_AlertuPoEskalacji_PrzywracaStanPotwierdzony()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.Acknowledge(alert.Key, "PC-1", "anna", f.Clock.UtcNow);

        for (int i = 0; i < 60 && f.Machine.Get(alert.Key)!.Status != AlertStatus.Escalated; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(20));
            f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
            f.Machine.Tick();
        }

        Assert.Equal(AlertStatus.Escalated, f.Machine.Get(alert.Key)!.Status);

        bool ok = f.Machine.Acknowledge(alert.Key, "PC-2", "bartek", f.Clock.UtcNow);

        Assert.True(ok);
        Assert.Equal(AlertStatus.Acknowledged, f.Machine.Get(alert.Key)!.Status);
        Assert.Equal("bartek", f.Machine.Get(alert.Key)!.AckedByUser);
    }

    [Fact]
    public void Tick_PoTrzydziestuSekundachBezCzerwieni_ZamykaEpizodIWpisujeGoDoHistorii()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Events.Clear();

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(f.Machine.Tick());

        f.Clock.Advance(TimeSpan.FromSeconds(2));
        IReadOnlyList<AlertEvent> events = f.Machine.Tick();

        AlertEvent closed = Assert.Single(events);
        Assert.Equal(AlertEventKind.Closed, closed.Kind);
        Assert.Equal(AlertStatus.Closed, closed.Alert.Status);
        Assert.NotNull(closed.Alert.ClosedAt);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Equal(alert.Key, Assert.Single(f.Machine.RecentHistory).Key);
        Assert.Equal(AlertStatus.Closed, f.Machine.Get(alert.Key)!.Status);
    }

    [Fact]
    public void Tick_GdyCzerwienJestStaleOdswiezana_NieZamykaEpizodu()
    {
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        for (int i = 0; i < 20; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
            Assert.Empty(f.Machine.Tick());
        }

        Assert.Single(f.Machine.ActiveAlerts);
        Assert.Empty(f.Machine.RecentHistory);
    }

    [Fact]
    public void OnRedConfirmed_GdyOcrPoprawiTekst_ZostajeJedenAlertZZachowanymCzasemRozpoczecia()
    {
        Fixture f = NewFixture();
        Alert pierwszy = f.Machine.OnRedConfirmed(Machine(), EmptyMessage(), "captures/blad.png");
        Assert.Equal("text:empty", pierwszy.Key.Discriminator);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Alert poprawiony = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), poprawiony.Key);
        Assert.Equal(pierwszy.StartedAt, poprawiony.StartedAt);
        Assert.Equal("Nie udało się pobrać tacki", poprawiony.ShortText);
        Assert.Equal("captures/blad.png", poprawiony.CaptureFile);
        Assert.Equal(AlertEventKind.Updated, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnRedConfirmed_DlaDwochRoznychRegulWTymSamymNieprzerwanymEpizodzie_ZostajeJedenAlert()
    {
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Machine.OnRedConfirmed(Machine(), SpindleMessage(), null);

        Alert alert = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), alert.Key);
        Assert.Equal(1, f.Events.Count(e => e.Kind == AlertEventKind.Raised));
    }

    [Fact]
    public void OnMachineOffline_PrzedUplywemTrzechMinut_NieTworzyAlertu()
    {
        Fixture f = NewFixture();
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);

        f.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(f.Machine.Tick());
        Assert.Empty(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void OnMachineOffline_PoTrzechMinutach_TworzyAlertBrakLacznosciBezDzwieku()
    {
        Fixture f = NewFixture();
        DateTimeOffset since = f.Clock.UtcNow;
        f.Machine.OnMachineOffline(Machine(), since);

        f.Clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        IReadOnlyList<AlertEvent> events = f.Machine.Tick();

        AlertEvent raised = Assert.Single(events);
        Assert.Equal(AlertEventKind.Raised, raised.Kind);
        Assert.Equal(AlertKind.Offline, raised.Alert.Kind);
        Assert.Equal(AlertKey.Offline("aidite1"), raised.Alert.Key);
        Assert.Equal("Brak łączności z maszyną", raised.Alert.ShortText);
        Assert.Equal("offline", raised.Alert.Category);
        Assert.Equal(since, raised.Alert.StartedAt);
        Assert.False(raised.Alert.Sound);
    }

    [Fact]
    public void OnMachineOffline_GdyWlaczonoDzwiekAlertowOffline_AlertMaDzwiek()
    {
        Fixture f = NewFixture(Timing with { OfflineAlertSound = true });
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);

        f.Clock.Advance(TimeSpan.FromMinutes(4));
        AlertEvent raised = Assert.Single(f.Machine.Tick());

        Assert.True(raised.Alert.Sound);
    }

    [Fact]
    public void OnMachineOffline_PowtorzonyTick_NieDuplikujeAlertu()
    {
        // MonitorSupervisor woła OnMachineOffline co cykl, dopóki maszyna jest offline (ten sam "since") — to
        // odświeża LastSeenAt istniejącego alertu, więc Tick go nie zamyka mimo przerwy.
        Fixture f = NewFixture();
        DateTimeOffset since = f.Clock.UtcNow;
        f.Machine.OnMachineOffline(Machine(), since);
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        f.Machine.Tick();

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Machine.OnMachineOffline(Machine(), since);
        Assert.Empty(f.Machine.Tick());
        Assert.Single(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void OnMachineOnline_ZamykaAlertBrakuLacznosci()
    {
        Fixture f = NewFixture();
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        f.Machine.Tick();
        f.Events.Clear();

        f.Machine.OnMachineOnline("aidite1");

        AlertEvent closed = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Closed, closed.Kind);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Single(f.Machine.RecentHistory);
    }

    [Fact]
    public void Tick_AlertBrakuLacznosciOdswiezanyCoCykl_NieJestEskalowaniAniZamykanyPoCzasie()
    {
        // Tick zamyka alert Offline, którego LastSeenAt nie było odświeżane przez
        // EpisodeCloseAfterMs — jak w prawdziwym użyciu (MonitorSupervisor woła OnMachineOffline co cykl, dopóki
        // maszyna jest offline), LastSeenAt jest stale świeże, więc alert przetrwa 30 minut bez zamknięcia; wciąż
        // nie jest przy tym eskalowany (eskalacja dotyczy tylko błędów Error, nigdy braku łączności).
        Fixture f = NewFixture();
        DateTimeOffset since = f.Clock.UtcNow;
        f.Machine.OnMachineOffline(Machine(), since);
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        Alert offline = Assert.Single(f.Machine.Tick()).Alert;
        f.Machine.Acknowledge(offline.Key, "PC-1", "anna", f.Clock.UtcNow);

        for (int i = 0; i < 30; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            f.Machine.OnMachineOffline(Machine(), since);
            f.Machine.Tick();
        }

        Alert current = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, current.Status);
    }

    [Fact]
    public void ActiveAlerts_SortujeAktywneIEskalowanePrzedPotwierdzonymiPotemWgCzasu()
    {
        Fixture f = NewFixture();
        MachineConfig m1 = Machine("aidite1", "Aidite 1");
        MachineConfig m2 = Machine("aidite2", "Aidite 2");
        MachineConfig m3 = Machine("aidite3", "Aidite 3");

        Alert pierwszy = f.Machine.OnRedConfirmed(m1, TrayMessage(), null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnRedConfirmed(m2, SpindleMessage(), null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnRedConfirmed(m3, TrayMessage(), null);

        // Najstarszy alert potwierdzamy — powinien wylądować na końcu listy.
        f.Machine.Acknowledge(pierwszy.Key, "PC-1", "anna", f.Clock.UtcNow);

        IReadOnlyList<Alert> alerts = f.Machine.ActiveAlerts;

        Assert.Equal(3, alerts.Count);
        Assert.Equal("aidite2", alerts[0].MachineId);
        Assert.Equal("aidite3", alerts[1].MachineId);
        Assert.Equal("aidite1", alerts[2].MachineId);
    }

    [Fact]
    public void Changed_JestZglaszaneDlaKazdegoRodzajuZdarzenia()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), EmptyMessage(), null);
        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.Acknowledge(AlertKey.ForRule("aidite1", "tray-grab"), "PC-1", "anna", f.Clock.UtcNow);
        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        f.Machine.Tick();

        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Updated);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Acknowledged);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Closed);
        Assert.Equal("aidite1", alert.MachineId);
    }

    [Fact]
    public void ApplyRemoteAck_PotwierdzaAlertDanymiZInnegoKomputera()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        f.Machine.ApplyRemoteAck(new AckRecord(alert.Key, "PC-5", "zdalny", f.Clock.UtcNow));

        Alert acked = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, acked.Status);
        Assert.Equal("PC-5", acked.AckedByHost);
    }

    [Fact]
    public void DisplayLine_ZawieraNazweMaszynyOpisIGodzine()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        string line = alert.DisplayLine(TimeZoneInfo.Utc);

        Assert.Equal("Aidite 1 — Nie udało się pobrać tacki — od 08:00", line);
    }

    [Fact]
    public async Task RownolegleOperacje_NaWieluWatkach_NieRzucajaWyjatkow()
    {
        Fixture f = NewFixture();
        var machines = Enumerable.Range(0, 8).Select(i => Machine($"m{i}", $"Maszyna {i}")).ToArray();

        Task[] tasks = machines.Select(m => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                Alert a = f.Machine.OnRedConfirmed(m, i % 2 == 0 ? TrayMessage() : SpindleMessage(), null);
                f.Machine.Acknowledge(a.Key, "PC", "u", f.Clock.UtcNow);
                f.Machine.Tick();
                f.Machine.OnMachineOffline(m, f.Clock.UtcNow);
                f.Machine.OnMachineOnline(m.Id);
                f.Machine.OnRedCleared(m.Id);
                _ = f.Machine.ActiveAlerts;
                _ = f.Machine.RecentHistory;
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.All(f.Machine.ActiveAlerts, a => Assert.NotEqual(AlertStatus.Closed, a.Status));
    }


    [Fact]
    public void OnRedConfirmed_TenSamKomunikatPoKrotkiejPrzerwie_DolaczaDoEpizoduBezDzwiekuIBezWpisuNowy()
    {
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), "captures/blad.png");
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(11));   // dłużej niż episodeBreakAfterMs (10 s)
        f.Clock.Advance(TimeSpan.FromSeconds(20));   // ... i po zamknięciu epizodu przez Tick (30 s)
        f.Machine.Tick();
        Assert.Empty(f.Machine.ActiveAlerts);
        f.Events.Clear();

        Alert again = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Equal(first.EpisodeId, again.EpisodeId);
        Assert.Equal(first.StartedAt, again.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, again.Status);
        Assert.Equal(FakeClock.DefaultStart, again.AcknowledgedAt);   // potwierdzenie sprzed przerwy
        Assert.Equal("PC-1", again.AckedByHost);
        Assert.Equal("captures/blad.png", again.CaptureFile);
        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Updated, e.Kind);   // w history.csv „aktualizacja”, nie „nowy”
        Assert.DoesNotContain(f.Events, x => x.Kind == AlertEventKind.Raised);
    }

    [Fact]
    public void OnRedConfirmed_TenSamKomunikatPoDlugiejPrzerwie_ToNowyAlertZDzwiekiem()
    {
        // Ochrona przed cichym wznowieniem zostaje: po oknie
        // sameMessageRejoinMs ten sam błąd jest NOWYM epizodem — dzwoni i czeka na nowe potwierdzenie.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromMilliseconds(Timing.SameMessageRejoinMs + 1000));
        f.Machine.Tick();
        f.Events.Clear();

        Alert again = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.NotEqual(first.EpisodeId, again.EpisodeId);
        Assert.Equal(AlertStatus.Active, again.Status);
        Assert.True(again.Sound);
        Assert.Equal(f.Clock.UtcNow, again.StartedAt);
        // skoro ta maszyna miała niedawno alert, powód ma być jawny.
        AlertEvent raised = Assert.Single(f.Events, x => x.Kind == AlertEventKind.Raised);
        Assert.NotNull(raised.Reason);
        Assert.Contains("przerwa", raised.Reason);
        Assert.False(f.Acks.IsAcked(first.Key));
    }

    [Fact]
    public void OnRedConfirmed_InnyKomunikatPoPrzerwie_NadalDajeNowyAlertZPowodemWLogu()
    {
        // Inny klucz po przerwie to nadal NOWY błąd (np. „Request home alarm” po restarcie) — łączenie
        // dotyczy wyłącznie tego samego komunikatu. Zdarzenie niesie powód do history.csv.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        f.Machine.Tick();
        f.Events.Clear();

        Alert other = f.Machine.OnRedConfirmed(Machine(), SpindleMessage(), null);

        Assert.NotEqual(first.Key, other.Key);
        Assert.Equal(AlertStatus.Active, other.Status);
        Assert.True(other.Sound);
        AlertEvent raised = Assert.Single(f.Events, x => x.Kind == AlertEventKind.Raised);
        Assert.NotNull(raised.Reason);
        Assert.Contains("inny klucz po przerwie", raised.Reason);
    }

    [Fact]
    public void OnRedConfirmed_TenSamKomunikatPoUtracieLacznosci_ToNowyAlertMimoKrotkiejPrzerwy()
    {
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        f.Clock.Advance(TimeSpan.FromSeconds(5));
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        f.Machine.Tick();
        f.Machine.OnMachineOnline("aidite1");
        f.Events.Clear();

        Alert again = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.NotEqual(first.EpisodeId, again.EpisodeId);
        Assert.Equal(AlertStatus.Active, again.Status);
        Assert.Contains(f.Events, x => x.Kind == AlertEventKind.Raised && x.Alert.Sound);
    }

    [Fact]
    public void OnRedConfirmed_TenSamKomunikatWracaPrzedZamknieciem_NieTworzyDrugiegoEpizodu()
    {
        // Ta sama sytuacja, ale przerwa mieści się w episodeCloseAfterMs — alert jeszcze żyje.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(11));
        f.Events.Clear();

        Alert again = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Equal(first.EpisodeId, again.EpisodeId);
        Assert.Equal(AlertStatus.Acknowledged, again.Status);
        Assert.DoesNotContain(f.Events, x => x.Kind is AlertEventKind.Raised or AlertEventKind.Closed);
    }

    [Fact]
    public void Tick_EskalacjaPrzyNierozstrzygnietymEkranie_PodajePowodDoHistorii()
    {
        // gdy alarm wraca, z samego logu i history.csv ma być
        // widać dlaczego — tu: eskalacja po ACK, a przy okazji nierozstrzygnięta obserwacja ekranu HMI,
        // która blokowała samoczynne zamknięcie.
        Fixture f = NewFixture(new TimingConfig { EscalationAfterAckMs = 60000 });
        Alert first = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");
        f.Machine.SetScreenObservationAvailable("aidite1", false);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        f.Machine.Tick();

        AlertEvent escalated = Assert.Single(f.Events, x => x.Kind == AlertEventKind.Escalated);
        Assert.NotNull(escalated.Reason);
        Assert.Contains("eskalacja po", escalated.Reason);
        Assert.Contains("nierozstrzygnięty ekran blokował zamknięcie od", escalated.Reason);
    }
}
