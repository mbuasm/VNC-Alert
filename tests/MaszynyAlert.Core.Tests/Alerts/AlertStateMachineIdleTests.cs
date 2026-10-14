using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineIdleTests
{
    /// <summary>Treść alertu postoju — musi być identyczna jak stała w <see cref="AlertStateMachine"/>.</summary>
    private const string IdleText = "Stoi bez pracy (100 %) – dołóż prace";

    private static readonly TimingConfig Timing = new();

    /// <summary>Próg „stoi bez pracy” (domyślnie 2 minuty).</summary>
    private static readonly TimeSpan Prog = TimeSpan.FromMilliseconds(Timing.IdleAlertAfterMs);

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

    private sealed record Fixture(FakeClock Clock, AckMemory Acks, AlertStateMachine Machine, List<AlertEvent> Events);

    private static Fixture NewFixture(TimingConfig? timing = null)
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var sm = new AlertStateMachine(clock, timing ?? Timing, acks);
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        return new Fixture(clock, acks, sm, events);
    }

    /// <summary>Podnosi alert postoju: pierwsze 100 %, upływ progu, drugie 100 % (jak kolejne cykle obserwatora).</summary>
    private static Alert RaiseIdle(Fixture f, string? captureFile = null)
    {
        f.Machine.OnProgressState(Machine(), atFull: true, captureFile);
        f.Clock.Advance(Prog);
        f.Machine.OnProgressState(Machine(), atFull: true, captureFile);
        return Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
    }

    [Fact]
    public void OnProgressState_PrzedUplywemDwochMinut_NieTworzyZadnegoAlertu()
    {
        // Dlaczego: maszyna z pełną kolejką normalnie stoi na 100 % około minuty przed startem kolejnej
        // pracy — alert przed upływem progu byłby fałszywy przy każdej zwykłej zmianie pracy.
        Fixture f = NewFixture();

        f.Machine.OnProgressState(Machine(), atFull: true);
        f.Clock.Advance(Prog - TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), atFull: true);

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnProgressState_PoDwochMinutachCiaglego100Procent_TworzyAlertPostojuBezDzwieku()
    {
        // Dlaczego: to jest istota funkcji — 100 % nieprzerwanie przez próg oznacza pustą kolejkę prac.
        // StartedAt musi wskazywać POCZĄTEK postoju („od HH:MM”), a nie moment powstania alertu.
        Fixture f = NewFixture();
        DateTimeOffset poczatek = f.Clock.UtcNow;

        Alert alert = RaiseIdle(f, "captures/aidite1_postoj.png");

        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertKey.Idle("aidite1"), alert.Key);
        Assert.Equal(AlertStatus.Active, alert.Status);
        Assert.Equal(IdleText, alert.ShortText);
        Assert.Null(alert.FullText);
        Assert.Equal("postoj", alert.Category);
        Assert.Equal(poczatek, alert.StartedAt);
        Assert.Equal(f.Clock.UtcNow, alert.LastSeenAt);
        Assert.Equal("Aidite 1", alert.MachineName);
        Assert.Equal("captures/aidite1_postoj.png", alert.CaptureFile);
        Assert.False(alert.Sound);

        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Raised, e.Kind);
    }

    [Fact]
    public void OnProgressState_GdyPasekSpadlPonizej100_ZerujeLicznikIProgLiczySieOdNowa()
    {
        // Dlaczego: przerwa w 100 % oznacza, że maszyna ruszyła — po niej znów musi minąć pełne 2 minuty,
        // inaczej kolejny, zupełnie normalny koniec pracy natychmiast zapalałby alert.
        Fixture f = NewFixture();

        f.Machine.OnProgressState(Machine(), atFull: true);
        f.Clock.Advance(TimeSpan.FromSeconds(100));
        f.Machine.OnProgressState(Machine(), atFull: false);   // maszyna ruszyła — licznik wyzerowany

        f.Clock.Advance(TimeSpan.FromSeconds(10));
        DateTimeOffset nowyPoczatek = f.Clock.UtcNow;
        f.Machine.OnProgressState(Machine(), atFull: true);
        f.Clock.Advance(Prog - TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), atFull: true);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), atFull: true);

        Alert alert = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(nowyPoczatek, alert.StartedAt);
    }

    [Fact]
    public void OnProgressState_GdyMaszynaRuszyPrzyOtwartymAlercie_ZamykaGoNatychmiast()
    {
        // Dlaczego: epizod postoju kończy się w chwili, gdy pasek spada poniżej 100 % — nie czekamy na
        // EpisodeCloseAfterMs jak przy błędach, bo mamy jednoznaczny sygnał „maszyna pracuje”.
        Fixture f = NewFixture();
        Alert alert = RaiseIdle(f);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.Machine.OnProgressState(Machine(), atFull: false);

        Assert.Empty(f.Machine.ActiveAlerts);
        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Closed, e.Kind);
        Assert.Equal(AlertStatus.Closed, e.Alert.Status);
        Assert.Equal(f.Clock.UtcNow, e.Alert.ClosedAt);
        Assert.Contains(f.Machine.RecentHistory, a => a.Key == alert.Key && a.Status == AlertStatus.Closed);
    }

    [Fact]
    public void OnProgressState_PrzyZamknieciuEpizodu_ZapominaPotwierdzenieWPamieciAck()
    {
        // Dlaczego: ACK jest ważny tylko „do końca epizodu” — inaczej kolejny postój tej samej maszyny
        // startowałby od razu jako potwierdzony i nikt by go nie zobaczył.
        Fixture f = NewFixture();
        Alert alert = RaiseIdle(f);
        f.Machine.Acknowledge(alert.Key, "PC-3", "jkowalski", f.Clock.UtcNow);
        Assert.True(f.Acks.IsAcked(alert.Key));

        f.Machine.OnProgressState(Machine(), atFull: false);

        Assert.False(f.Acks.IsAcked(alert.Key));
    }

    [Fact]
    public void OnProgressState_PoZamknieciuEpizodu_KolejnyPostojDajeNowyAlertRaised()
    {
        // Dlaczego: po wykonaniu następnej pracy maszyna może znów stanąć bez pracy — to nowy epizod,
        // który ma znowu powiadomić operatorów (a nie zostać wyciszony poprzednim epizodem).
        Fixture f = NewFixture();
        RaiseIdle(f);
        f.Machine.OnProgressState(Machine(), atFull: false);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromMinutes(20));
        Alert kolejny = RaiseIdle(f);

        Assert.Equal(AlertStatus.Active, kolejny.Status);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnProgressState_PrzyKolejnychCyklachNa100Procent_TylkoOdswiezaAlertBezNowychZdarzen()
    {
        // Dlaczego: alert postoju jest JEDNORAZOWY — trwający postój nie może dzwonić w kółko co cykl.
        Fixture f = NewFixture();
        Alert alert = RaiseIdle(f);
        f.Events.Clear();

        for (int i = 0; i < 5; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Machine.OnProgressState(Machine(), atFull: true);
        }

        Assert.Empty(f.Events);
        Alert odswiezony = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(alert.StartedAt, odswiezony.StartedAt);
        Assert.Equal(f.Clock.UtcNow, odswiezony.LastSeenAt);
    }

    [Fact]
    public void OnProgressState_GdyZrzutEkranuPojawiSiePozniej_UzupelniaGoWAlercie()
    {
        // Dlaczego: monitor zapisuje zrzut „postoj” raz na epizod i przekazuje ścieżkę w kolejnych
        // wywołaniach — alert utworzony bez zrzutu musi go przyjąć przy najbliższym odświeżeniu.
        Fixture f = NewFixture();
        Alert alert = RaiseIdle(f);
        Assert.Null(alert.CaptureFile);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), atFull: true, "captures/aidite1_postoj.png");

        Assert.Equal("captures/aidite1_postoj.png", Assert.Single(f.Machine.ActiveAlerts).CaptureFile);
    }

    [Fact]
    public void Acknowledge_AlertuPostoju_WyciszaGoIDalszeCyklieNieDajaNowychZdarzen()
    {
        // Dlaczego: „✕ Skasuj” to potwierdzenie rozgłaszane na wszystkie komputery — po nim ten sam
        // epizod ma już milczeć, mimo że maszyna nadal stoi na 100 %.
        Fixture f = NewFixture();
        Alert alert = RaiseIdle(f);
        f.Machine.Acknowledge(alert.Key, "PC-2", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(5));
        f.Machine.OnProgressState(Machine(), atFull: true);

        Alert acked = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, acked.Status);
        Assert.Equal("PC-2", acked.AckedByHost);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void Tick_PotwierdzonegoAlertuPostoju_NigdyNieEskaluje()
    {
        // Dlaczego: postój to informacja („dołóż prace”), a nie awaria — eskalacja (ponowny alarm po
        // 15 minutach od ACK) dotyczy wyłącznie alertów błędu.
        Fixture f = NewFixture();
        Alert alert = RaiseIdle(f);
        f.Machine.Acknowledge(alert.Key, "PC-2", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        for (int i = 0; i < 20; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            f.Machine.OnProgressState(Machine(), atFull: true);   // alert stale odświeżany, więc Tick go nie zamknie
            f.Machine.Tick();
        }

        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Escalated);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(f.Machine.ActiveAlerts).Status);
    }

    [Fact]
    public void OnProgressState_GdyPamiecAckZnaKlucz_TworzyAlertOdRazuPotwierdzonyZeZdarzeniemUpdated()
    {
        // Dlaczego: po przejęciu roli obserwatora przez inny komputer alert nie może „odżyć” jako nowy —
        // pamięć potwierdzeń sprawia, że powstaje od razu jako Acknowledged (Updated, nie Raised).
        Fixture f = NewFixture();
        AlertKey key = AlertKey.Idle("aidite1");
        f.Acks.Record(new AckRecord(key, "PC-5", "zdalny", f.Clock.UtcNow));

        Alert alert = RaiseIdle(f);

        Assert.Equal(AlertStatus.Acknowledged, alert.Status);
        Assert.Equal("PC-5", alert.AckedByHost);
        Assert.Equal("zdalny", alert.AckedByUser);
        Assert.Equal(AlertEventKind.Updated, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnMachineOffline_ZerujeLicznik100Procent()
    {
        // Dlaczego: w czasie braku łączności nie wiemy, co działo się z paskiem postępu — po powrocie
        // musimy odliczyć 2 minuty od nowa, a nie zapalić alert na podstawie starych odczytów.
        Fixture f = NewFixture();
        f.Machine.OnProgressState(Machine(), atFull: true);

        f.Clock.Advance(TimeSpan.FromSeconds(110));
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);

        f.Clock.Advance(TimeSpan.FromSeconds(30));
        DateTimeOffset poPowrocie = f.Clock.UtcNow;
        f.Machine.OnProgressState(Machine(), atFull: true);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(Prog);
        f.Machine.OnProgressState(Machine(), atFull: true);

        Alert alert = Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.Equal(poPowrocie, alert.StartedAt);
    }

    [Fact]
    public void Tick_AlertuPostojuBezOdswiezenia_ZamykaGoIZerujeLicznik()
    {
        // Dlaczego: siatka bezpieczeństwa — gdy monitor w ogóle przestanie zgłaszać stan paska, alert nie
        // może wisieć wiecznie; po takim zamknięciu próg 2 minut musi być liczony od nowa.
        Fixture f = NewFixture();
        RaiseIdle(f);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs + 1000));
        f.Machine.Tick();

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);

        // Licznik wyzerowany: pierwszy odczyt 100 % po zamknięciu zaczyna odliczanie od zera.
        f.Events.Clear();
        f.Machine.OnProgressState(Machine(), atFull: true);
        f.Clock.Advance(Prog - TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), atFull: true);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), atFull: true);
        Assert.Equal(AlertKind.MachineIdle, Assert.Single(f.Machine.ActiveAlerts).Kind);
    }

    [Fact]
    public void OnProgressState_PrzyOtwartymAlercieBledu_NieTworzyAlertuPostojuINieRuszaAlertuBledu()
    {
        Fixture f = NewFixture();
        Alert blad = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), "captures/aidite1_blad.png");
        f.Events.Clear();

        for (int i = 0; i < 6; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(30));   // łącznie 3 minuty, czyli 1,5 × próg postoju
            f.Machine.OnProgressState(Machine(), atFull: true);
        }

        Assert.Empty(f.Events);
        Alert pozostaly = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(blad.Key, pozostaly.Key);
        Assert.Equal(AlertKind.Error, pozostaly.Kind);
        Assert.Equal(AlertStatus.Active, pozostaly.Status);
        Assert.Equal(blad.LastSeenAt, pozostaly.LastSeenAt);
        Assert.True(pozostaly.Sound);

        // I odwrotnie (pierwotny cel tego testu): sygnał „maszyna ruszyła” też nie rusza alertu błędu.
        f.Machine.OnProgressState(Machine(), atFull: false);

        Assert.Equal(blad.Key, Assert.Single(f.Machine.ActiveAlerts).Key);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnRedConfirmed_PrzyOtwartymAlerciePostoju_TworzyAlertBleduIZamykaPostojNatychmiast()
    {
        Fixture f = NewFixture();
        Alert postoj = RaiseIdle(f);
        f.Events.Clear();

        Alert blad = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Alert aktywny = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(blad.Key, aktywny.Key);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), blad.Key);
        Assert.Equal(AlertKind.Error, blad.Kind);
        Assert.NotEqual(postoj.Key, blad.Key);

        // Jedna publikacja, dwa zdarzenia: najpierw powstanie błędu, zaraz po nim zamknięcie postoju.
        Assert.Equal(2, f.Events.Count);
        Assert.Equal(AlertEventKind.Raised, f.Events[0].Kind);
        Assert.Equal(AlertKind.Error, f.Events[0].Alert.Kind);
        Assert.Equal(AlertEventKind.Closed, f.Events[1].Kind);
        Assert.Equal(postoj.Key, f.Events[1].Alert.Key);
        Assert.Equal(IdleText, f.Events[1].Alert.ShortText);
        Assert.Equal(AlertStatus.Closed, f.Events[1].Alert.Status);
        Assert.Equal(f.Clock.UtcNow, f.Events[1].Alert.ClosedAt);

        Assert.Contains(f.Machine.RecentHistory, a => a.Key == postoj.Key && a.Status == AlertStatus.Closed);
        Assert.False(f.Acks.IsAcked(postoj.Key));
    }

    [Fact]
    public void ActiveAlerts_PrzyMieszanceRodzajowAlertow_SortujeBezWyjatku()
    {
        Fixture f = NewFixture();
        f.Machine.OnMachineOffline(Machine("amm580", "580"), f.Clock.UtcNow - TimeSpan.FromMinutes(10));
        Alert postoj = RaiseIdle(f);
        f.Machine.Acknowledge(postoj.Key, "PC-1", "anna", f.Clock.UtcNow);
        f.Machine.OnRedConfirmed(Machine("aidite2", "Aidite 2"), TrayMessage(), null);   // świeży błąd na INNEJ maszynie
        f.Machine.Tick();                                             // tworzy alert „brak łączności”

        IReadOnlyList<Alert> aktywne = f.Machine.ActiveAlerts;

        Assert.Equal(3, aktywne.Count);
        // Najpierw dzwoniące (Active) wg StartedAt: offline (sprzed 10 minut), potem błąd; potwierdzony postój na końcu.
        Assert.Equal([AlertKind.Offline, AlertKind.Error, AlertKind.MachineIdle], aktywne.Select(a => a.Kind));
        Assert.Equal(AlertStatus.Acknowledged, aktywne[^1].Status);
    }
}
