using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineErrorPriorityTests
{
    private static readonly TimingConfig Timing = new();

    /// <summary>Próg „stoi bez pracy” (domyślnie 2 minuty).</summary>
    private static readonly TimeSpan Prog = TimeSpan.FromMilliseconds(Timing.IdleAlertAfterMs);

    /// <summary>Czas bez świeżej czerwieni, po którym <see cref="AlertStateMachine.Tick"/> zamyka epizod błędu.</summary>
    private static readonly TimeSpan ZamkniecieBledu = TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs);

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

    private static Fixture NewFixture()
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var sm = new AlertStateMachine(clock, Timing, acks);
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        return new Fixture(clock, acks, sm, events);
    }

    /// <summary>Podnosi alert postoju danej maszyny: pierwsze 100 %, upływ progu, drugie 100 %.</summary>
    private static Alert RaiseIdle(Fixture f, MachineConfig machine)
    {
        f.Machine.OnProgressState(machine, atFull: true);
        f.Clock.Advance(Prog);
        f.Machine.OnProgressState(machine, atFull: true);
        return Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle && a.MachineId == machine.Id);
    }

    /// <summary>
    /// Domyka epizod błędu tak, jak robi to prawdziwa pętla obserwatora: czerwień znika
    /// (<see cref="AlertStateMachine.OnRedCleared"/>), a po <c>EpisodeCloseAfterMs</c> bez odświeżenia
    /// alert zamyka <see cref="AlertStateMachine.Tick"/>.
    /// </summary>
    private static void ZamknijBlad(Fixture f, MachineConfig machine)
    {
        f.Machine.OnRedCleared(machine.Id);
        f.Clock.Advance(ZamkniecieBledu + TimeSpan.FromSeconds(1));
        f.Machine.Tick();
        Assert.DoesNotContain(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.Error);
    }

    [Fact]
    public void OnProgressState_PodczasOtwartegoBledu_LicznikPostojuNieBiegnie_APoZamknieciuBleduPotrzebaPelnych2Minut()
    {
        // Dlaczego: to jest sedno reguły. Gdyby licznik „100 % nieprzerwanie od” biegł podczas błędu,
        // operator zaraz po naprawieniu awarii dostałby natychmiast pop-up „dołóż prace” — za czas, w
        // którym maszyna stała przez błąd, a nie przez brak pracy. Po zamknięciu błędu odliczanie musi
        // zacząć się od zera, dokładnie jak przy zwykłym, świeżym epizodzie postoju.
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        // 3 minuty na 100 % przy otwartym błędzie — półtora progu, a mimo to ani jednego alertu postoju.
        for (int i = 0; i < 6; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(30));
            f.Machine.OnProgressState(Machine(), atFull: true);
        }

        Assert.DoesNotContain(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.DoesNotContain(f.Events, e => e.Alert.Kind == AlertKind.MachineIdle);

        ZamknijBlad(f, Machine());
        f.Events.Clear();

        DateTimeOffset poczatekPostoju = f.Clock.UtcNow;
        f.Machine.OnProgressState(Machine(), atFull: true);

        // Po minucie na 100 % nadal nic — licznik naprawdę wystartował od zera, a nie sprzed błędu.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Machine.OnProgressState(Machine(), atFull: true);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Empty(f.Events);

        // Po pełnych dwóch minutach — dopiero teraz alert, z początkiem liczonym od zamknięcia błędu.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Machine.OnProgressState(Machine(), atFull: true);

        Alert postoj = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, postoj.Kind);
        Assert.Equal(poczatekPostoju, postoj.StartedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnRedConfirmed_PrzyPOTWIERDZONYMAlerciePostoju_TezGoZamyka_APoBledzieKolejnyPostojToNowyAlertRaised()
    {
        // Dlaczego: potwierdzenie („✕ Skasuj”) tylko wycisza alert — nie zmienia faktu, że maszyna ma teraz
        // czerwony błąd, więc potwierdzony postój też musi zgasnąć. A skoro epizod postoju się zamknął,
        // pamięć potwierdzeń ma o nim ZAPOMNIEĆ: kolejny postój po naprawie błędu to nowy epizod, który
        // znów ma pokazać pop-up (zdarzenie Raised), a nie odżyć po cichu jako już potwierdzony (Updated).
        Fixture f = NewFixture();
        Alert postoj = RaiseIdle(f, Machine());
        f.Machine.Acknowledge(postoj.Key, "PC-2", "anna", f.Clock.UtcNow);
        Assert.True(f.Acks.IsAcked(postoj.Key));
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(f.Machine.ActiveAlerts).Status);
        f.Events.Clear();

        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.Equal(AlertKind.Error, Assert.Single(f.Machine.ActiveAlerts).Kind);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Key == postoj.Key);
        Assert.False(f.Acks.IsAcked(postoj.Key));

        ZamknijBlad(f, Machine());
        f.Events.Clear();

        Alert kolejnyPostoj = RaiseIdle(f, Machine());

        Assert.Equal(postoj.Key, kolejnyPostoj.Key);            // ten sam klucz (id maszyny)…
        Assert.Equal(AlertStatus.Active, kolejnyPostoj.Status); // …ale NOWY, niepotwierdzony epizod
        Assert.Null(kolejnyPostoj.AcknowledgedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnRedConfirmed_BezOtwartegoAlertuPostoju_NieEmitujeZadnegoZdarzeniaZamkniecia()
    {
        // Dlaczego: zamykanie postoju „na wszelki wypadek” przy każdym potwierdzeniu czerwieni nie może
        // produkować fałszywych zdarzeń Closed — te idą po sieci do pozostałych komputerów (i do historii),
        // więc każde zbędne zdarzenie myliłoby operatorów i kasowało cudze potwierdzenia.
        Fixture f = NewFixture();

        // (a) czysta sytuacja: żadnego postoju nigdy nie było.
        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        AlertEvent tylkoBlad = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Raised, tylkoBlad.Kind);
        Assert.Equal(AlertKind.Error, tylkoBlad.Alert.Kind);

        // (b) odświeżenia tego samego epizodu błędu też nie mają nic zamykać.
        f.Events.Clear();
        for (int i = 0; i < 3; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        }

        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Closed);

        // (c) licznik postoju biegł, ale alert jeszcze nie powstał (mniej niż 2 minuty na 100 %) — też cisza.
        Fixture g = NewFixture();
        g.Machine.OnProgressState(Machine(), atFull: true);
        g.Clock.Advance(TimeSpan.FromSeconds(30));
        g.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);

        Assert.DoesNotContain(g.Events, e => e.Kind == AlertEventKind.Closed);
        Assert.Equal(AlertKind.Error, Assert.Single(g.Machine.ActiveAlerts).Kind);
    }

    [Fact]
    public void OnProgressState_BladNaJednejMaszynie_NieBlokujePostojuNaInnej()
    {
        Fixture f = NewFixture();
        MachineConfig zBledem = Machine();
        MachineConfig bezBledu = Machine("aidite2", "Aidite 2");
        f.Machine.OnRedConfirmed(zBledem, TrayMessage(), null);

        Alert postoj = RaiseIdle(f, bezBledu);

        Assert.Equal(AlertKey.Idle("aidite2"), postoj.Key);
        Assert.Equal("Aidite 2", postoj.MachineName);
        Assert.Equal(2, f.Machine.ActiveAlerts.Count);
        Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.Error && a.MachineId == "aidite1");
        Assert.DoesNotContain(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle && a.MachineId == "aidite1");
    }

    [Fact]
    public void OnProgressState_PrzyOtwartymAlercieBrakuLacznosci_AlertPostojuNadalPowstaje()
    {
        Fixture f = NewFixture();
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow - TimeSpan.FromMinutes(5));
        f.Machine.Tick();
        Assert.Equal(AlertKind.Offline, Assert.Single(f.Machine.ActiveAlerts).Kind);

        Alert postoj = RaiseIdle(f, Machine());

        Assert.Equal(AlertKind.MachineIdle, postoj.Kind);
        Assert.Equal(2, f.Machine.ActiveAlerts.Count);
        Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.Offline);
    }
}
