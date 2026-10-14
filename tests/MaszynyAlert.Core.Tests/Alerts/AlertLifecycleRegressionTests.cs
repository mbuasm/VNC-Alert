using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertLifecycleRegressionTests
{
    private static readonly TimingConfig Timing = new();
    private static readonly DateTimeOffset T0 = FakeClock.DefaultStart;
    private static readonly AlertKey TrayKey = AlertKey.ForRule("aidite1", "tray-grab");

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
        new("spindle", "Błąd wrzeciona", "restart", "Spindle error", "SPINDLE ERROR", true) { Priority = 10 };

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
    public void Tick_PoZamknieciuEpizodu_ZapominaPotwierdzenieWPamieciAckow()
    {
        Fixture f = NewFixture();
        Alert alert = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.Acknowledge(alert.Key, "PC-1", "anna", f.Clock.UtcNow);
        Assert.True(f.Acks.IsAcked(TrayKey));

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs + 1000));
        AlertEvent closed = Assert.Single(f.Machine.Tick());

        Assert.Equal(AlertEventKind.Closed, closed.Kind);
        Assert.False(f.Acks.IsAcked(TrayKey));
        Assert.Null(f.Acks.Get(TrayKey));
    }

    [Fact]
    public void OnRedConfirmed_TegoSamegoBleduPoZamknietymPotwierdzonymEpizodzie_ZnowuDzwoni()
    {
        Fixture f = NewFixture();
        Alert pierwszy = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.Acknowledge(pierwszy.Key, "PC-1", "anna", f.Clock.UtcNow);
        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs + 1000));
        f.Machine.Tick();
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Alert kolejny = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Equal(TrayKey, kolejny.Key);
        Assert.Equal(AlertStatus.Active, kolejny.Status);
        Assert.True(kolejny.Sound);
        Assert.Null(kolejny.AcknowledgedAt);
        Assert.Equal(f.Clock.UtcNow, kolejny.StartedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnMachineOnline_ZamykajacAlertBrakuLacznosci_ZapominaPotwierdzenie()
    {
        Fixture f = NewFixture();
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        Alert offline = Assert.Single(f.Machine.Tick()).Alert;
        f.Machine.Acknowledge(offline.Key, "PC-1", "anna", f.Clock.UtcNow);
        Assert.True(f.Acks.IsAcked(AlertKey.Offline("aidite1")));

        f.Machine.OnMachineOnline("aidite1");

        Assert.False(f.Acks.IsAcked(AlertKey.Offline("aidite1")));
    }


    [Fact]
    public void Merge_AlertuEskalowanegoZeStarszymWpisemWPamieciAckow_ZostawiaStanEskalowany()
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        acks.Record(new AckRecord(TrayKey, "PC-1", "anna", T0.AddSeconds(100)));

        Alert eskalowany = Alert(AlertStatus.Escalated) with
        {
            AcknowledgedAt = T0.AddSeconds(100),
            AckedByHost = "PC-1",
            AckedByUser = "anna",
            EscalatedAt = T0.AddSeconds(1000),
            LastSeenAt = T0.AddSeconds(1010),
        };

        Alert wynik = Assert.Single(StateMerger.Merge([[eskalowany]], acks));

        Assert.Equal(AlertStatus.Escalated, wynik.Status);
        Assert.Equal(T0.AddSeconds(1000), wynik.EscalatedAt);
    }

    [Fact]
    public void Merge_PotwierdzeniaNowszegoNizEskalacjaInnegoObserwatora_WygrywaPotwierdzenie()
    {
        Alert eskalowany = Alert(AlertStatus.Escalated) with
        {
            AcknowledgedAt = T0.AddSeconds(100),
            EscalatedAt = T0.AddSeconds(400),
            LastSeenAt = T0.AddSeconds(410),
        };
        Alert potwierdzonyPozniej = Alert(AlertStatus.Acknowledged) with
        {
            AcknowledgedAt = T0.AddSeconds(600),
            AckedByHost = "PC-3",
            AckedByUser = "bartek",
            LastSeenAt = T0.AddSeconds(605),
        };

        Alert wynik = Assert.Single(StateMerger.Merge([[eskalowany], [potwierdzonyPozniej]], PustaPamiec()));
        Assert.Equal(AlertStatus.Acknowledged, wynik.Status);
        Assert.Equal("bartek", wynik.AckedByUser);

        Alert odwrotnie = Assert.Single(StateMerger.Merge([[potwierdzonyPozniej], [eskalowany]], PustaPamiec()));
        Assert.Equal(AlertStatus.Acknowledged, odwrotnie.Status);

        // Kierunek odwrotny:
        // eskalacja PO potwierdzeniu musi wygrać, inaczej komputer z wyciszonym alertem nigdy by nie zadzwonił.
        Alert eskalowanyPoPotwierdzeniu = eskalowany with
        {
            AcknowledgedAt = T0.AddSeconds(600),
            EscalatedAt = T0.AddSeconds(900),
            LastSeenAt = T0.AddSeconds(905),
        };

        Alert wygranaEskalacji = Assert.Single(
            StateMerger.Merge([[potwierdzonyPozniej], [eskalowanyPoPotwierdzeniu]], PustaPamiec()));
        Assert.Equal(AlertStatus.Escalated, wygranaEskalacji.Status);
        Assert.Equal(
            AlertStatus.Escalated,
            Assert.Single(StateMerger.Merge([[eskalowanyPoPotwierdzeniu], [potwierdzonyPozniej]], PustaPamiec())).Status);
    }

    [Fact]
    public void Merge_AlertuAktywnegoZPotwierdzeniemSprzedJegoPoczatku_ZostawiaStanAktywny()
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        acks.Record(new AckRecord(TrayKey, "PC-1", "anna", T0.AddSeconds(100)));

        Alert nowyEpizod = Alert() with { StartedAt = T0.AddSeconds(300), LastSeenAt = T0.AddSeconds(305) };

        Alert wynik = Assert.Single(StateMerger.Merge([[nowyEpizod]], acks));

        Assert.Equal(AlertStatus.Active, wynik.Status);
        Assert.Null(wynik.AcknowledgedAt);
    }


    [Fact]
    public void OnMachineOffline_WolaneWRytmieBackoffuPrzezCzteryMinuty_TworzyDokladnieJedenAlertBezMigotania()
    {
        // Pętla nadzorcy tyka CO SEKUNDĘ, ale zgłoszenie offline pada tylko po kolejnej nieudanej próbie
        // połączenia: odstęp = backoff (do 30 s) + czas samej próby.
        const int probaSekund = 3;
        Fixture f = NewFixture();
        DateTimeOffset since = f.Clock.UtcNow;
        int[] backoff = Timing.ReconnectBackoffMs;
        int cykl = 0;
        int doNastepnegoZgloszenia = 0;

        for (int sekunda = 0; sekunda < 300; sekunda++)
        {
            if (doNastepnegoZgloszenia <= 0)
            {
                f.Machine.OnMachineOffline(Machine(), since);
                doNastepnegoZgloszenia = (backoff[Math.Min(cykl, backoff.Length - 1)] / 1000) + probaSekund;
                cykl++;
            }

            f.Machine.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            doNastepnegoZgloszenia--;
        }

        Assert.Equal(1, f.Events.Count(e => e.Kind == AlertEventKind.Raised));
        Assert.Equal(0, f.Events.Count(e => e.Kind == AlertEventKind.Closed));
        Alert offline = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKind.Offline, offline.Kind);
        Assert.Equal(since, offline.StartedAt);
        Assert.Empty(f.Machine.RecentHistory);
    }

    [Fact]
    public void Tick_GdyZgloszeniaOffline_UstajaBezPowrotuOnline_ZamykaAlertDopieroPoProguZZapasem()
    {
        Fixture f = NewFixture();
        DateTimeOffset since = f.Clock.UtcNow;
        f.Machine.OnMachineOffline(Machine(), since);
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Single(f.Machine.Tick());
        f.Events.Clear();

        // Zwykły próg zamknięcia epizodu (30 s) to za mało — inaczej alert migotałby przy backoffie 30 s.
        f.Clock.Advance(TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs + 1000));
        Assert.Empty(f.Machine.Tick());
        Assert.Single(f.Machine.ActiveAlerts);

        long progMs = Math.Max(Timing.EpisodeCloseAfterMs, (Timing.ReconnectBackoffMs.Max() * 2) + 5000);
        f.Clock.AdvanceMs(progMs);
        AlertEvent closed = Assert.Single(f.Machine.Tick());

        Assert.Equal(AlertEventKind.Closed, closed.Kind);
        Assert.Equal(AlertKind.Offline, closed.Alert.Kind);
        Assert.Empty(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void OnMachineOnline_PoZgloszeniachOffline_ZamykaAlertNatychmiast()
    {
        Fixture f = NewFixture();
        DateTimeOffset since = f.Clock.UtcNow;
        f.Machine.OnMachineOffline(Machine(), since);
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Single(f.Machine.Tick());
        f.Events.Clear();

        f.Machine.OnMachineOnline("aidite1");

        AlertEvent closed = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Closed, closed.Kind);
        Assert.Equal(f.Clock.UtcNow, closed.Alert.ClosedAt);
        Assert.Empty(f.Machine.ActiveAlerts);
    }


    [Fact]
    public void ActiveAlerts_DlaAlertowOTymSamymCzasie_MajaTaSamaKolejnoscNiezaleznieOdKolejnosciZgloszen()
    {
        Fixture pierwsza = NewFixture();
        pierwsza.Machine.OnRedConfirmed(Machine("aidite2", "Aidite 2"), TrayMessage(), null);
        pierwsza.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        pierwsza.Machine.OnRedConfirmed(Machine(), SpindleMessage(), null);

        Fixture druga = NewFixture();
        druga.Machine.OnRedConfirmed(Machine(), SpindleMessage(), null);
        druga.Machine.OnRedConfirmed(Machine("aidite2", "Aidite 2"), TrayMessage(), null);
        druga.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        string[] oczekiwana = ["aidite1|rule:spindle", "aidite2|rule:tray-grab"];

        Assert.Equal(oczekiwana, Klucze(pierwsza.Machine.ActiveAlerts));
        Assert.Equal(oczekiwana, Klucze(druga.Machine.ActiveAlerts));
        Assert.Equal(Klucze(pierwsza.Machine.ActiveAlerts), Klucze(pierwsza.Machine.ActiveAlerts));
    }

    [Fact]
    public void Merge_DlaAlertowOTymSamymCzasie_ZwracaTaSamaKolejnoscNiezaleznieOdKolejnosciWejscia()
    {
        Alert a1 = Alert() with { Key = AlertKey.ForRule("aidite1", "spindle"), MachineId = "aidite1" };
        Alert a2 = Alert() with { Key = AlertKey.ForRule("aidite1", "tray-grab"), MachineId = "aidite1" };
        Alert a3 = Alert() with { Key = AlertKey.ForRule("aidite2", "tray-grab"), MachineId = "aidite2" };
        string[] oczekiwana = ["aidite1|rule:spindle", "aidite1|rule:tray-grab", "aidite2|rule:tray-grab"];

        Assert.Equal(oczekiwana, Klucze(StateMerger.Merge([[a3, a2, a1]], PustaPamiec())));
        Assert.Equal(oczekiwana, Klucze(StateMerger.Merge([[a1], [a3], [a2]], PustaPamiec())));
        Assert.Equal(oczekiwana, Klucze(StateMerger.Merge([[a2, a3], [a1, a2]], PustaPamiec())));
    }

    [Fact]
    public void ActiveAlerts_WywolaneWielokrotnie_ZwracajaIdentycznaKolejnosc()
    {
        Fixture f = NewFixture();
        foreach (string id in new[] { "aidite3", "aidite1", "aidite2", "amm580" })
        {
            f.Machine.OnRedConfirmed(Machine(id, id), TrayMessage(), null);
        }

        IReadOnlyList<string> pierwsze = Klucze(f.Machine.ActiveAlerts);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(pierwsze, Klucze(f.Machine.ActiveAlerts));
            Assert.Equal(pierwsze, Klucze(StateMerger.Merge([f.Machine.ActiveAlerts], PustaPamiec())));
        }

        Assert.Equal(["aidite1|rule:tray-grab", "aidite2|rule:tray-grab", "aidite3|rule:tray-grab", "amm580|rule:tray-grab"], pierwsze);
    }

    private static AckMemory PustaPamiec() => new(new FakeClock(), TimeSpan.FromHours(2));

    private static IReadOnlyList<string> Klucze(IEnumerable<Alert> alerts) =>
        alerts.Select(a => a.Key.ToString()).ToList();

    private static Alert Alert(AlertStatus status = AlertStatus.Active) => new()
    {
        Key = TrayKey,
        MachineId = "aidite1",
        MachineName = "Aidite 1",
        Kind = AlertKind.Error,
        ShortText = "Nie udało się pobrać tacki",
        Category = "tacka",
        StartedAt = T0,
        LastSeenAt = T0,
        Status = status,
        Sound = true,
    };
}
