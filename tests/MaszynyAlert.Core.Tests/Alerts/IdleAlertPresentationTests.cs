using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class IdleAlertPresentationTests
{
    private static readonly TimingConfig Timing = new();
    private static readonly TimeSpan ProgZera = TimeSpan.FromMilliseconds(Timing.IdleAtZeroAfterMs);
    private static readonly TimeSpan ProgPelny = TimeSpan.FromMilliseconds(Timing.IdleAlertAfterMs);
    private static readonly TimeSpan ZamkniecieBezOdswiezenia = TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs);

    private static MachineConfig Machine() => new()
    {
        Id = "amm520-1",
        Name = "520 (1)",
        Host = "192.168.1.120",
        Port = 5900,
        Profile = "aidite5x",
    };

    private sealed record Fixture(FakeClock Clock, AlertStateMachine Machine, List<AlertEvent> Events);

    private static Fixture NewFixture()
    {
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, Timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        return new Fixture(clock, sm, events);
    }

    /// <summary>
    /// To samo, co <c>AppOrchestrator.IdleAlerts</c> w trybie jednego komputera — źródło pop-upu postoju,
    /// sekcji „Maszyny bez pracy” w Podglądzie i w oknie statusu oraz licznika „stoją: N” w zasobniku.
    /// </summary>
    private static IReadOnlyList<Alert> WidokPostojow(Fixture f) => f.Machine.ActiveAlerts
        .Where(a => a.Kind == AlertKind.MachineIdle && a.Status is AlertStatus.Active or AlertStatus.Escalated)
        .ToList();

    /// <summary>Migawka pracującej maszyny: ekran główny widoczny, świeża klatka, postęp z OCR.</summary>
    private static MachineStatusSnapshot Migawka(Fixture f, int? procent,
        DateTimeOffset? zeroOd = null, DateTimeOffset? pelnyOd = null, bool? ekranGlowny = true) => new()
    {
        MachineId = Machine().Id, Name = Machine().Name, Host = Machine().Host, Profile = Machine().Profile,
        State = MachineConnectionState.Connected, Source = "vnc", OcrAvailable = true,
        MainScreenWatchEnabled = true, MainScreenWatchRequired = true, MainScreenVisible = ekranGlowny,
        IdleWatchEnabled = true, LastFrameAt = f.Clock.UtcNow.AddSeconds(-1), UpdatedAt = f.Clock.UtcNow,
        LastProgressPercent = procent, ProgressZeroSince = zeroOd, ProgressFullSince = pelnyOd,
    };

    /// <summary>Karta Podglądu i kafelek nakładki (<c>MachineCardViewModel.ForEnabledMachines</c> bierze niezamknięte alerty).</summary>
    private static MachineCardState Karta(Fixture f, MachineStatusSnapshot migawka) =>
        MachineCardState.From(Machine().Id, migawka,
            f.Machine.ActiveAlerts.Where(a => a.Status != AlertStatus.Closed).ToList(), Timing, f.Clock.UtcNow);

    private static Alert PodniesZero(Fixture f)
    {
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        f.Clock.Advance(ProgZera);
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        return Assert.Single(WidokPostojow(f));
    }

    private static Alert PodniesPelny(Fixture f)
    {
        f.Machine.OnProgressState(Machine(), ProgressState.Full);
        f.Clock.Advance(ProgPelny);
        f.Machine.OnProgressState(Machine(), ProgressState.Full);
        return Assert.Single(WidokPostojow(f));
    }

    [Fact]
    public void RuszylaPoStaniuNaZerze_ZdarzenieClosed_IPostojZnikaZeWszystkichWidokow()
    {
        // Dlaczego: pop-up i sekcje „Maszyny bez pracy” odświeżają się po zdarzeniu rdzenia
        // (AppOrchestrator.AlertsChanged). Zamknięcie musi przyjść jako Closed tego samego klucza,
        // a lista, z której liczą się widoki, ma być w tym momencie już pusta.
        Fixture f = NewFixture();
        Alert postoj = PodniesZero(f);
        f.Events.Clear();

        f.Machine.OnProgressState(Machine(), ProgressState.Running);

        AlertEvent zamkniecie = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Closed, zamkniecie.Kind);
        Assert.Equal(postoj.Key, zamkniecie.Alert.Key);
        Assert.Equal(AlertStatus.Closed, zamkniecie.Alert.Status);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Empty(WidokPostojow(f));

        MachineCardState karta = Karta(f, Migawka(f, procent: 1));
        Assert.Equal(MachineCardState.WatchedState, karta.State);
        Assert.Equal(MachineCardTone.Ok, karta.Tone);
        Assert.Equal("1 %", karta.Progress);
    }

    [Fact]
    public void RuszylaPoStaniuNaStuProcentach_ZdarzenieClosed_IKartaWracaDoObserwacji()
    {
        Fixture f = NewFixture();
        PodniesPelny(f);
        f.Events.Clear();

        f.Machine.OnProgressState(Machine(), ProgressState.Running);

        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Empty(WidokPostojow(f));
        Assert.Equal(MachineCardState.WatchedState, Karta(f, Migawka(f, procent: 12)).State);
    }

    [Fact]
    public void KolejnaPracaPoStuProcentach_ZeroZamykaPostoj_ANowyPostojNiePowstajeGdyMaszynaRuszy()
    {
        // Dlaczego: 100 % → 0 % zamyka stary epizod (ten sam klucz, inna
        // kategoria). Widoki mają być puste od razu, a nie dopiero po progu 0 %; pop-up nie może wyglądać
        // na ciągły. Gdy maszyna zafrezuje pierwszy procent, żaden nowy postój nie powstaje.
        Fixture f = NewFixture();
        PodniesPelny(f);
        f.Events.Clear();

        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Empty(WidokPostojow(f));
        Assert.Equal(MachineCardState.WatchedState, Karta(f, Migawka(f, procent: 0, zeroOd: f.Clock.UtcNow)).State);

        f.Clock.Advance(ProgZera - TimeSpan.FromSeconds(5));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Assert.Empty(WidokPostojow(f));

        f.Machine.OnProgressState(Machine(), ProgressState.Running);
        f.Clock.Advance(ProgZera);
        f.Machine.OnProgressState(Machine(), ProgressState.Running);
        f.Machine.Tick();

        Assert.Empty(WidokPostojow(f));
        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Fact]
    public void ZmianaOdmianyPostoju_ToZamkniecieIPozniejszeRaisedTegoSamegoKlucza()
    {
        // Dlaczego: pop-up wykrywa „nową” pozycję po kluczu (IdlePopupWindow._knownKeys). Klucz 100 %
        // i 0 % jest ten sam, więc między epizodami rdzeń MUSI zgłosić Closed — inaczej pop-up
        // pokazałby nową odmianę jako tę samą, nieprzerwaną pozycję „od HH:MM” starego postoju.
        Fixture f = NewFixture();
        Alert pelny = PodniesPelny(f);
        f.Events.Clear();

        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        f.Clock.Advance(ProgZera);
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Equal(new[] { AlertEventKind.Closed, AlertEventKind.Raised }, f.Events.Select(e => e.Kind));
        Alert zero = Assert.Single(WidokPostojow(f));
        Assert.Equal(pelny.Key, zero.Key);
        Assert.NotEqual(pelny.EpisodeId, zero.EpisodeId);
        Assert.Equal(AlertStateMachine.IdleAtZeroCategory, zero.Category);
        Assert.True(zero.StartedAt > pelny.StartedAt);
    }

    [Fact]
    public void KartaNieMowiStoiZSamejMigawki_BezOtwartegoAlertuPostoju()
    {
        // Dlaczego: ProgressZeroSince/ProgressFullSince w migawce to
        // tylko diagnostyka kolumny „Postęp”. Napis „Stoi …” na karcie i kafelku wynika WYŁĄCZNIE
        // z otwartego alertu postoju, więc po zamknięciu epizodu nie może się utrzymać.
        Fixture f = NewFixture();
        DateTimeOffset dawno = f.Clock.UtcNow.AddMinutes(-20);

        Assert.Equal(MachineCardState.WatchedState, Karta(f, Migawka(f, procent: 0, zeroOd: dawno)).State);
        Assert.Equal(MachineCardState.WatchedState, Karta(f, Migawka(f, procent: 100, pelnyOd: dawno)).State);
    }

    [Fact]
    public void SkasowanyPostoj_ZnikaZWidokuIKartyOdRazu_APowrotDoPracyGoZamyka()
    {
        Fixture f = NewFixture();
        Alert postoj = PodniesZero(f);

        Assert.True(f.Machine.AcknowledgeLocal(postoj.Key, "PC-HALA", "operator"));

        Assert.Empty(WidokPostojow(f));
        Assert.Equal(MachineCardState.WatchedState, Karta(f, Migawka(f, procent: 0)).State);

        f.Events.Clear();
        f.Machine.OnProgressState(Machine(), ProgressState.Running);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Empty(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void PostojNigdyNieTrafiaDoOknaAlertuINieDzwoniCoMinute()
    {
        // Dlaczego: sygnał co minutę dotyczy tylko czerwonych alarmów. Postój ma jednorazowy „ping”
        // przy nowej pozycji pop-upu; nie może dzwonić ani trwając, ani po zamknięciu.
        Fixture f = NewFixture();
        Alert postoj = PodniesZero(f);
        var dzwiek = new AlertSoundSchedule(Timing.AlertSoundRepeatMs);

        Assert.False(postoj.Sound);
        Assert.False(AlertVisibility.IsVisibleInAlertWindow(postoj));
        Assert.False(dzwiek.Update(f.Machine.ActiveAlerts, f.Clock.MonotonicMs));
        Assert.False(dzwiek.HasRingingAlerts);

        f.Machine.OnProgressState(Machine(), ProgressState.Running);
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        Alert zamkniety = Assert.Single(f.Machine.RecentHistory);
        Assert.False(AlertVisibility.IsVisibleInAlertWindow(zamkniety));
        Assert.False(dzwiek.Update(f.Machine.ActiveAlerts, f.Clock.MonotonicMs));
    }

    [Fact]
    public void NieczytelnyPostep_TrzymaPostoj_AleTickZamykaGoPoProguBezOdswiezenia()
    {
        // Dlaczego: gdy parser nie rozpozna liczby (OnProgressUnknown), rdzeń celowo NIE zamyka postoju
        // od razu. Siatką bezpieczeństwa jest Tick po EpisodeCloseAfterMs bez odświeżenia — po nim
        // widoki mają być puste, bez żadnego dodatkowego kroku po stronie UI.
        Fixture f = NewFixture();
        PodniesZero(f);
        f.Events.Clear();

        for (int i = 0; i < 29; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Machine.OnProgressUnknown(Machine().Id);
            f.Machine.Tick();
        }

        Assert.Single(WidokPostojow(f));

        f.Clock.Advance(ZamkniecieBezOdswiezenia);
        f.Machine.OnProgressUnknown(Machine().Id);
        f.Machine.Tick();

        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Empty(WidokPostojow(f));
    }

    [Fact]
    public void BrakObserwacjiEkranu_TrzymaPostojBezTerminu_IKartaNadalMowiStoi()
    {
        // Kontrakt rdzenia (AlertStateMachine.Tick, „observationMissing”): bez potwierdzonego ekranu
        // głównego postój czeka na rzeczywisty odczyt i NIE zamyka się po czasie. Karta pokazuje wtedy
        // postój przed „Ograniczoną obserwacją”. To druga, obok parsera,
        // droga do „alert zostaje, choć maszyna ruszyła” — kończy ją dopiero pierwszy odczyt postępu
        // po powrocie ekranu głównego.
        Fixture f = NewFixture();
        PodniesZero(f);
        f.Events.Clear();

        f.Machine.SetScreenObservationAvailable(Machine().Id, false);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        f.Machine.Tick();

        Assert.Empty(f.Events);
        Assert.Single(WidokPostojow(f));
        Assert.Equal("Stoi na 0 %", Karta(f, Migawka(f, procent: null, ekranGlowny: null)).State);

        f.Machine.SetScreenObservationAvailable(Machine().Id, true);
        f.Machine.OnProgressState(Machine(), ProgressState.Running);

        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Empty(WidokPostojow(f));
        Assert.Equal(MachineCardState.WatchedState, Karta(f, Migawka(f, procent: 3)).State);
    }
}
