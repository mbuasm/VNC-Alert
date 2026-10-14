using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineIdleRecoveryTests
{
    private static readonly TimingConfig Timing = new();

    /// <summary>Próg „stoi na 100 %” (domyślnie 2 minuty).</summary>
    private static readonly TimeSpan ProgPelny = TimeSpan.FromMilliseconds(Timing.IdleAlertAfterMs);

    /// <summary>Próg „stoi na 0 %” (domyślnie 3 minuty).</summary>
    private static readonly TimeSpan ProgZera = TimeSpan.FromMilliseconds(Timing.IdleAtZeroAfterMs);

    /// <summary>Po jakim czasie bez odświeżenia <see cref="AlertStateMachine.Tick"/> zamyka epizod (domyślnie 30 s).</summary>
    private static readonly TimeSpan ProgZamkniecia = TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs);

    /// <summary>Odstęp między kolejnymi cyklami obserwatora w symulacji.</summary>
    private static readonly TimeSpan Cykl = TimeSpan.FromSeconds(5);

    private static readonly AlertKey Klucz = AlertKey.Idle("aidite1");

    private static MachineConfig Machine() => new()
    {
        Id = "aidite1",
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
    };

    private sealed record Fixture(FakeClock Clock, AckMemory Acks, AlertStateMachine Machine, List<AlertEvent> Events);

    private static Fixture NewFixture(bool grupa = false)
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var sm = new AlertStateMachine(clock, Timing, acks);
        sm.UseGroupProtocol(grupa);
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        return new Fixture(clock, acks, sm, events);
    }

    /// <summary>
    /// Pozycje „Maszyny bez pracy” tak, jak je widzi pop-up i okno statusu (<c>AppOrchestrator.IdleAlerts</c>):
    /// alerty postoju w stanie Active albo Escalated — potwierdzony („✕”) postój jest z listy schowany.
    /// </summary>
    private static List<Alert> WidocznePostoje(Fixture f) => f.Machine.ActiveAlerts
        .Where(a => a.Kind == AlertKind.MachineIdle && a.Status is AlertStatus.Active or AlertStatus.Escalated)
        .ToList();

    private static Alert? Postoj(Fixture f) =>
        f.Machine.ActiveAlerts.SingleOrDefault(a => a.Kind == AlertKind.MachineIdle);

    /// <summary>Stan postępu co <see cref="Cykl"/> przez <paramref name="czas"/>, z krokiem zegara i <c>Tick</c> — jak pętla obserwatora.</summary>
    private static void Cykle(Fixture f, TimeSpan czas, ProgressState stan)
    {
        for (TimeSpan t = TimeSpan.Zero; t < czas; t += Cykl)
        {
            f.Clock.Advance(Cykl);
            f.Machine.OnProgressState(Machine(), stan);
            f.Machine.Tick();
        }
    }

    /// <summary>Nieczytelne odczyty co <see cref="Cykl"/> przez <paramref name="czas"/>, z krokiem zegara i <c>Tick</c>.</summary>
    private static void CykleNieczytelne(Fixture f, TimeSpan czas)
    {
        for (TimeSpan t = TimeSpan.Zero; t < czas; t += Cykl)
        {
            f.Clock.Advance(Cykl);
            f.Machine.OnProgressUnknown("aidite1");
            f.Machine.Tick();
        }
    }

    private static Alert RaiseFull(Fixture f)
    {
        f.Machine.OnProgressState(Machine(), ProgressState.Full);
        Cykle(f, ProgPelny, ProgressState.Full);
        Alert alert = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStateMachine.IdleCategory, alert.Category);
        return alert;
    }

    private static Alert RaiseZero(Fixture f)
    {
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Cykle(f, ProgZera, ProgressState.Zero);
        Alert alert = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStateMachine.IdleAtZeroCategory, alert.Category);
        return alert;
    }

    // ---------------------------------------------------------------------------------------------
    // (a) Postój na 100 % → nowa praca (0 %) → praca
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Postoj100_NowaPracaNaZeroIRuch_ZamykaAlertOdRazuIPotemNicNieWraca()
    {
        // Dlaczego: maszyna stała na 100 %, wzięła kolejną pracę
        // (0 %), a potem frezuje (0,2 %, 1 %, 5 % → Running). Alert „dołóż prace” ma zgasnąć już przy 0 %
        // (powód przestał być prawdziwy), a praca nie może zostawić ani jego, ani „wciśnij Start”.
        Fixture f = NewFixture();
        Alert postoj = RaiseFull(f);
        f.Events.Clear();

        f.Clock.Advance(Cykl);
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Null(Postoj(f));
        AlertEvent zamkniecie = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Closed, zamkniecie.Kind);
        Assert.Equal(postoj.EpisodeId, zamkniecie.Alert.EpisodeId);

        Cykle(f, TimeSpan.FromSeconds(20), ProgressState.Zero);   // rozruch nowej pracy, krócej niż próg 0 %
        Cykle(f, TimeSpan.FromMinutes(10), ProgressState.Running);

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Single(f.Events);   // tylko to jedno zamknięcie — żadnego nowego alertu
    }

    [Fact]
    public void Postoj100_ZeroKrocejNizProgPotemRuch_LicznikZeraLiczySieOdNowaPrzyKolejnymZerze()
    {
        // Dlaczego: 2,5 minuty rozruchu na 0 % i praca nie mogą „zostać w liczniku” — kolejne 0 % (np. po
        // następnej pracy) liczy pełne 3 minuty od własnego początku, inaczej „wciśnij Start” wyskoczyłby
        // po kilku sekundach i wisiał przy pracującej maszynie.
        Fixture f = NewFixture();
        RaiseFull(f);

        Cykle(f, TimeSpan.FromSeconds(150), ProgressState.Zero);
        Cykle(f, TimeSpan.FromSeconds(30), ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(Cykl);
        DateTimeOffset drugieZero = f.Clock.UtcNow;
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Cykle(f, ProgZera - Cykl, ProgressState.Zero);
        Assert.Empty(f.Machine.ActiveAlerts);

        Cykle(f, Cykl, ProgressState.Zero);
        Alert alert = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStateMachine.IdleAtZeroCategory, alert.Category);
        Assert.Equal(drugieZero, alert.StartedAt);
    }

    [Fact]
    public void Postoj100_NowaPracaZNieczytelnymiOdczytamiPomiedzy_NieZostawiaAlertu()
    {
        // Dlaczego: przy zmianie pracy cyfry na HMI się zmieniają i OCR bywa nierozstrzygnięty — nieczytelne
        // odczyty przeplatane z 0 % i pracą nie mogą ani podtrzymać starego „dołóż prace”, ani otworzyć
        // nowego postoju.
        Fixture f = NewFixture();
        RaiseFull(f);
        f.Events.Clear();

        CykleNieczytelne(f, TimeSpan.FromSeconds(10));
        Cykle(f, Cykl, ProgressState.Zero);
        CykleNieczytelne(f, TimeSpan.FromSeconds(10));
        for (int i = 0; i < 40; i++)
        {
            Cykle(f, Cykl, ProgressState.Running);
            CykleNieczytelne(f, Cykl);
        }

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.DoesNotContain(f.Events, e => e.Kind is AlertEventKind.Raised or AlertEventKind.Updated);
    }

    // ---------------------------------------------------------------------------------------------
    // (b) Postój na 0 % → praca
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Postoj0_Ruch_ZamykaNatychmiastIDalszaPracaGoNieOtwiera()
    {
        // Dlaczego: pierwszy potwierdzony procent pracy to jednoznaczny koniec „wciśnij Start” — nie wolno
        // czekać na wygaśnięcie w Tick ani otwierać alertu ponownie w kolejnych cyklach pracy.
        Fixture f = NewFixture();
        Alert postoj = RaiseZero(f);
        f.Events.Clear();

        Cykle(f, Cykl, ProgressState.Running);

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Contains(f.Machine.RecentHistory, a => a.EpisodeId == postoj.EpisodeId && a.Status == AlertStatus.Closed);

        Cykle(f, TimeSpan.FromMinutes(10), ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Single(f.Events);
    }

    [Fact]
    public void Postoj0_NieczytelnePotemRuch_RuchZamykaAlertOdRazu()
    {
        // Dlaczego: nieczytelne odczyty między 0 % a pracą nie mogą „zablokować” zamknięcia — decyduje
        // pierwszy czytelny odczyt pracy, niezależnie od tego, co było przed nim.
        Fixture f = NewFixture();
        RaiseZero(f);

        CykleNieczytelne(f, TimeSpan.FromSeconds(20));
        Assert.NotNull(Postoj(f));   // nieczytelny odczyt nie dowodzi, że maszyna ruszyła

        Cykle(f, Cykl, ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);
    }

    // ---------------------------------------------------------------------------------------------
    // (c) Odczyty nieczytelne przeplatane z pracą
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProgressState.Full)]
    [InlineData(ProgressState.Zero)]
    public void TylkoNieczytelneOdczyty_NieTrzymajaAlertuWiecznie_TickZamykaPoProguBezOdswiezenia(ProgressState stan)
    {
        // Dlaczego: OnProgressUnknown celowo nie zamyka postoju, ale też go NIE odświeża — gdy po postoju
        // przychodzą same nieczytelne odczyty (np. cyfry pracy, których OCR nie rozstrzyga), alert musi
        // wygasnąć po EpisodeCloseAfterMs od ostatniego czytelnego odczytu, a nie wisieć bez końca.
        Fixture f = NewFixture();
        Alert postoj = stan == ProgressState.Full ? RaiseFull(f) : RaiseZero(f);
        f.Events.Clear();

        CykleNieczytelne(f, ProgZamkniecia - Cykl);
        Alert trwa = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(postoj.LastSeenAt, trwa.LastSeenAt);   // nieczytelny odczyt nie jest odświeżeniem

        CykleNieczytelne(f, Cykl);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void RzadkiOdczytPracyWsrodNieczytelnych_ZamykaPrzyPierwszymIPotemPostojLiczySieOdZera()
    {
        // Dlaczego: nawet jeden czytelny odczyt pracy wśród wielu nieczytelnych zamyka postój, a kolejne
        // nieczytelne odczyty go nie wskrzeszają; następne 100 % zaczyna próg 2 minut od zera.
        Fixture f = NewFixture();
        RaiseFull(f);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Machine.OnProgressUnknown("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Machine.OnProgressState(Machine(), ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);

        CykleNieczytelne(f, TimeSpan.FromMinutes(5));
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(Cykl);
        f.Machine.OnProgressState(Machine(), ProgressState.Full);
        Cykle(f, ProgPelny - Cykl, ProgressState.Full);
        Assert.Empty(f.Machine.ActiveAlerts);
        Cykle(f, Cykl, ProgressState.Full);
        Assert.Single(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void NierozstrzygnietyEkran_TrzymaPostojDoPowrotuObserwacji_PotemPracaAlboTickGoZamyka()
    {
        // Dlaczego: bez obserwacji ekranu (inny ekran HMI, nierozstrzygnięty OCR) Tick celowo czeka na
        // rzeczywisty odczyt. Po powrocie obserwacji blokada musi zniknąć: brak czytelnych odczytów przez
        // EpisodeCloseAfterMs zamyka postój tak samo jak zawsze.
        Fixture f = NewFixture();
        RaiseFull(f);

        f.Machine.OnMainScreenUnknown("aidite1");
        CykleNieczytelne(f, TimeSpan.FromMinutes(3));
        Assert.NotNull(Postoj(f));

        f.Machine.SetScreenObservationAvailable("aidite1", true);
        CykleNieczytelne(f, ProgZamkniecia - Cykl);
        Assert.NotNull(Postoj(f));
        CykleNieczytelne(f, Cykl);
        Assert.Null(Postoj(f));
    }

    [Fact]
    public void NierozstrzygnietyEkran_CzytelnyOdczytPracyZamykaPostojOdRazu()
    {
        // Dlaczego: blokada Tick przy braku obserwacji dotyczy tylko wygasania — jawny odczyt pracy zamyka
        // postój zawsze, także wtedy.
        Fixture f = NewFixture();
        RaiseZero(f);

        f.Machine.OnMainScreenUnknown("aidite1");
        f.Machine.SetScreenObservationAvailable("aidite1", true);
        Cykle(f, Cykl, ProgressState.Running);

        Assert.Empty(f.Machine.ActiveAlerts);
    }

    // ---------------------------------------------------------------------------------------------
    // (d) Potwierdzony („✕”) postój → praca → kolejny postój
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SkasowanyPostoj_PotemPracaIKolejnyPostoj_WracaJakoNowyWidocznyAlert()
    {
        // Dlaczego: „✕ Skasuj” dotyczy jednego epizodu. Po pracy kolejny postój to nowy epizod — ma być
        // widoczny (Active, Raised), a nie po cichu potwierdzony starym kliknięciem.
        Fixture f = NewFixture();
        Alert pierwszy = RaiseFull(f);
        Assert.True(f.Machine.AcknowledgeLocal(Klucz, "PC-1", "operator"));
        Assert.Empty(WidocznePostoje(f));

        Cykle(f, TimeSpan.FromMinutes(5), ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.False(f.Acks.IsAcked(Klucz));

        f.Events.Clear();
        Alert drugi = RaiseFull(f);

        Assert.NotEqual(pierwszy.EpisodeId, drugi.EpisodeId);
        Assert.Equal(AlertStatus.Active, drugi.Status);
        Assert.Single(WidocznePostoje(f));
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void SkasowanyPostoj100_NowaPracaStoiNaZero_WcisnijStartJestWidoczny()
    {
        // Dlaczego: potwierdzenie „dołóż prace” nie może przejść na „wciśnij Start” — to inny powód postoju
        // i operator ma go zobaczyć, gdy nowa praca naprawdę nie ruszyła.
        Fixture f = NewFixture();
        RaiseFull(f);
        f.Machine.AcknowledgeLocal(Klucz, "PC-1", "operator");

        f.Events.Clear();
        RaiseZero(f);

        Alert zero = Assert.Single(WidocznePostoje(f));
        Assert.Equal(AlertStateMachine.IdleAtZeroCategory, zero.Category);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.EpisodeId == zero.EpisodeId);
    }

    [Fact]
    public void SkasowanyPostoj_NieczytelneOdczytyWTrakcie_ZostajeTymSamymPotwierdzonymEpizodem()
    {
        // Dlaczego: w odwrotną stronę — dopóki maszyna naprawdę stoi, nieczytelne odczyty nie mogą
        // zrobić z potwierdzonego postoju nowego pop-upu.
        Fixture f = NewFixture();
        Alert postoj = RaiseFull(f);
        f.Machine.AcknowledgeLocal(Klucz, "PC-1", "operator");

        for (int i = 0; i < 10; i++)
        {
            CykleNieczytelne(f, Cykl);
            Cykle(f, Cykl, ProgressState.Full);
        }

        Alert trwa = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(postoj.EpisodeId, trwa.EpisodeId);
        Assert.Equal(AlertStatus.Acknowledged, trwa.Status);
        Assert.Empty(WidocznePostoje(f));
    }

    [Fact]
    public void ZdalnePotwierdzeniePowtorzonePoZamknieciuPrzezPrace_NieUciszaKolejnegoPostoju()
    {
        // Dlaczego: w grupie bez protokołu v2 potwierdzenie z innego komputera wraca jeszcze w heartbeatach
        // (RecentAcks).
        // Zamknięcie przez pracę ma kończyć epizod tak samo jak Tick.
        Fixture f = NewFixture();
        RaiseFull(f);
        var zdalne = new AckRecord(Klucz, "PC-5", "zdalny", f.Clock.UtcNow);
        f.Machine.ApplyRemoteAck(zdalne);
        Assert.Equal(AlertStatus.Acknowledged, Postoj(f)!.Status);

        Cykle(f, TimeSpan.FromSeconds(30), ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Machine.ApplyRemoteAck(zdalne);   // retransmisja w heartbeacie po zamknięciu
        Cykle(f, TimeSpan.FromSeconds(30), ProgressState.Running);

        f.Events.Clear();
        Alert kolejny = RaiseFull(f);
        Assert.Equal(AlertStatus.Active, kolejny.Status);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
        Assert.Single(WidocznePostoje(f));
    }

    [Fact]
    public void ZdalnePotwierdzeniePowtorzonePoZamknieciuPrzezNowaPraceNaZero_NieUciszaWcisnijStart()
    {
        // Dlaczego: to samo przy zmianie 100 % → 0 % (nowa praca): stare potwierdzenie „dołóż prace”
        // powtórzone po zamknięciu nie może schować „wciśnij Start”, gdy nowa praca faktycznie nie ruszyła.
        Fixture f = NewFixture();
        RaiseFull(f);
        var zdalne = new AckRecord(Klucz, "PC-5", "zdalny", f.Clock.UtcNow);
        f.Machine.ApplyRemoteAck(zdalne);

        f.Clock.Advance(Cykl);
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Assert.Empty(f.Machine.ActiveAlerts);
        f.Machine.ApplyRemoteAck(zdalne);

        Cykle(f, ProgZera, ProgressState.Zero);
        Alert zero = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStateMachine.IdleAtZeroCategory, zero.Category);
        Assert.Equal(AlertStatus.Active, zero.Status);
    }

    [Fact]
    public void ZdalnePotwierdzeniePowtorzonePoWygasnieciuWTick_NieUciszaKolejnegoPostoju()
    {
        // Dlaczego: ścieżka porównawcza —
        // obie ścieżki zamknięcia mają działać tak samo.
        Fixture f = NewFixture();
        RaiseFull(f);
        var zdalne = new AckRecord(Klucz, "PC-5", "zdalny", f.Clock.UtcNow);
        f.Machine.ApplyRemoteAck(zdalne);

        CykleNieczytelne(f, ProgZamkniecia + Cykl);
        Assert.Empty(f.Machine.ActiveAlerts);
        f.Machine.ApplyRemoteAck(zdalne);

        Alert kolejny = RaiseFull(f);
        Assert.Equal(AlertStatus.Active, kolejny.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // (e) Wygasanie w Tick
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Postoj0_WygaslyWTick_KolejneZeroLiczyPelnyProgOdNowa()
    {
        // Dlaczego: bliźniak testu Tick dla 100 % z AlertStateMachineIdleTests — po wygaśnięciu „wciśnij
        // Start” licznik 0 % jest wyzerowany, więc stary początek postoju nie wskrzesi alertu od razu.
        Fixture f = NewFixture();
        RaiseZero(f);

        f.Clock.Advance(ProgZamkniecia + Cykl);
        f.Machine.Tick();
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Cykle(f, ProgZera - Cykl, ProgressState.Zero);
        Assert.Empty(f.Machine.ActiveAlerts);
        Cykle(f, Cykl, ProgressState.Zero);
        Assert.Single(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void Tick_PoZamknieciuPrzezPrace_NieEmitujeDrugiegoZamknieciaAniNowegoAlertu()
    {
        // Dlaczego: zamknięcie przez pracę i siatka bezpieczeństwa w Tick nie mogą się dublować (drugi wpis
        // w historii, drugie zdarzenie) ani „odtwarzać” alertu z zapamiętanego stanu.
        Fixture f = NewFixture();
        RaiseFull(f);
        f.Events.Clear();

        Cykle(f, Cykl, ProgressState.Running);
        for (int i = 0; i < 20; i++)
        {
            f.Clock.Advance(ProgZamkniecia);
            f.Machine.Tick();
        }

        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Single(f.Machine.RecentHistory, a => a.Kind == AlertKind.MachineIdle);
    }

    // ---------------------------------------------------------------------------------------------
    // (f) Protokół grupy
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Grupa_PostojZamknietyPrzezPrace_StareDaneEpizoduNieOtwierajaGoAniNieUciszajaNowego()
    {
        // Dlaczego: w protokole grupy stan i potwierdzenia przychodzą z sieci z opóźnieniem. Spóźniony
        // epizod, potwierdzenie i zamknięcie STAREGO postoju nie mogą ani otworzyć go z powrotem po pracy,
        // ani potwierdzić czy zamknąć NOWEGO postoju tej maszyny.
        Fixture f = NewFixture(grupa: true);
        Alert stary = RaiseFull(f);
        Alert staryPotwierdzony = stary with
        {
            Status = AlertStatus.Acknowledged, AcknowledgedAt = f.Clock.UtcNow, AckedByHost = "PC-5", AckedByUser = "zdalny",
        };

        Cykle(f, Cykl, ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Machine.ReconcileGroupState([staryPotwierdzony], []);
        Assert.Empty(f.Machine.ActiveAlerts);

        Alert nowy = RaiseFull(f);
        Assert.NotEqual(stary.EpisodeId, nowy.EpisodeId);

        f.Machine.ApplyRemoteAck(new AckRecord(Klucz, "PC-5", "zdalny", f.Clock.UtcNow) { EpisodeId = stary.EpisodeId });
        f.Machine.ReconcileGroupState([staryPotwierdzony],
            [new EpisodeClosure(stary.EpisodeId, stary.MachineId, AlertKind.MachineIdle, f.Clock.UtcNow)]);

        Alert trwa = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(nowy.EpisodeId, trwa.EpisodeId);
        Assert.Equal(AlertStatus.Active, trwa.Status);
    }

    [Fact]
    public void Grupa_Postoj100NowaPracaIRuch_ZamykaJakBezGrupy()
    {
        Fixture f = NewFixture(grupa: true);
        RaiseFull(f);

        Cykle(f, Cykl, ProgressState.Zero);
        Assert.Empty(f.Machine.ActiveAlerts);
        Cykle(f, TimeSpan.FromMinutes(5), ProgressState.Running);
        Assert.Empty(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void Grupa_ZamkniecieObecnegoEpizoduZSieci_ZamykaPostojIZerujeLicznik()
    {
        // Dlaczego: gdy inny komputer grupy zamknął ten epizod postoju (np. przejął obserwację i widział
        // pracę), lokalny alert znika, a kolejne 100 % liczy próg od zera.
        Fixture f = NewFixture(grupa: true);
        Alert postoj = RaiseFull(f);

        f.Machine.ReconcileGroupState([],
            [new EpisodeClosure(postoj.EpisodeId, postoj.MachineId, AlertKind.MachineIdle, f.Clock.UtcNow)]);
        Assert.Empty(f.Machine.ActiveAlerts);

        Cykle(f, ProgPelny - Cykl, ProgressState.Full);
        Assert.Empty(f.Machine.ActiveAlerts);
    }
}
