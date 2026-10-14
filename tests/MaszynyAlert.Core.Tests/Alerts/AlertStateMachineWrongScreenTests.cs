using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineWrongScreenTests
{
    private static readonly TimingConfig Timing = new() { WrongScreenAlertAfterMs = 120000 };

    /// <summary>Próg „inny ekran” użyty w testach zwłoki (2 minuty).</summary>
    private static readonly TimeSpan Prog = TimeSpan.FromMilliseconds(Timing.WrongScreenAlertAfterMs);

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

    /// <summary>Podnosi alert: pierwsze „inny ekran”, upływ progu, drugie „inny ekran” (jak kolejne sprawdzenia obserwatora).</summary>
    private static Alert RaiseWrongScreen(Fixture f, string? captureFile = null)
    {
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false, captureFile);
        f.Clock.Advance(Prog);
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false, captureFile);
        return Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.WrongScreen);
    }

    [Fact]
    public void OnMainScreenState_PrzedUplywemProgu_NieTworzyAlertu()
    {
        // Dlaczego: operator ma prawo na chwilę zajrzeć do „Production info” — przypominamy dopiero, gdy
        // wygląda na to, że zapomniał wrócić.
        Fixture f = NewFixture();

        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        f.Clock.Advance(Prog - TimeSpan.FromSeconds(1));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnMainScreenState_ProgZero_TworzyAlertPrzyPierwszymOdczycieInnegoEkranu()
    {
        Fixture f = NewFixture(Timing with { WrongScreenAlertAfterMs = 0 });
        DateTimeOffset poczatek = f.Clock.UtcNow;

        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);

        Alert alert = Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.WrongScreen);
        Assert.Equal(AlertStatus.Active, alert.Status);
        Assert.Equal(poczatek, alert.StartedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnMainScreenUnknown_ProgZero_NieTworzyAlertu()
    {
        Fixture f = NewFixture(Timing with { WrongScreenAlertAfterMs = 0 });

        for (int i = 0; i < 5; i++)
        {
            f.Machine.OnMainScreenUnknown("aidite1");
            f.Clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.DoesNotContain(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.WrongScreen);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnMainScreenState_PoUplywieProguInnegoEkranu_TworzyAlertBezDzwiekuOdPoczatkuInnegoEkranu()
    {
        Fixture f = NewFixture();
        DateTimeOffset poczatek = f.Clock.UtcNow;

        Alert alert = RaiseWrongScreen(f, "captures/aidite1_ekran.png");

        Assert.Equal(AlertKind.WrongScreen, alert.Kind);
        Assert.Equal(AlertKey.WrongScreen("aidite1"), alert.Key);
        Assert.Equal(AlertStatus.Active, alert.Status);
        Assert.Equal(AlertStateMachine.WrongScreenShortText, alert.ShortText);
        Assert.Equal("ekran", alert.Category);
        Assert.Equal(poczatek, alert.StartedAt);
        Assert.Equal("Aidite 1", alert.MachineName);
        Assert.Equal("captures/aidite1_ekran.png", alert.CaptureFile);
        Assert.False(alert.Sound);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnMainScreenState_GdyWlaczonoDzwiekWKonfiguracji_AlertMaSound()
    {
        Fixture f = NewFixture(Timing with { WrongScreenAlertSound = true });

        Alert alert = RaiseWrongScreen(f);

        Assert.True(alert.Sound);
    }

    [Fact]
    public void OnMainScreenState_GdyEkranGlownyWroci_ZamykaAlertNatychmiast()
    {
        // Dlaczego: powrót etykiety to jednoznaczny sygnał „operator wrócił na okno stanu” — nie czekamy
        // na EpisodeCloseAfterMs jak przy błędach.
        Fixture f = NewFixture();
        Alert alert = RaiseWrongScreen(f);
        f.Machine.Acknowledge(alert.Key, "PC-1", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(20));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: true);

        Assert.Empty(f.Machine.ActiveAlerts);
        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Closed, e.Kind);
        Assert.Equal(f.Clock.UtcNow, e.Alert.ClosedAt);
        Assert.False(f.Acks.IsAcked(alert.Key));
        Assert.Contains(f.Machine.RecentHistory, a => a.Key == alert.Key && a.Status == AlertStatus.Closed);
    }

    [Fact]
    public void OnMainScreenState_GdyEkranGlownyWrociPrzedProgiem_LicznikLiczySieOdNowa()
    {
        Fixture f = NewFixture();

        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        f.Clock.Advance(TimeSpan.FromSeconds(100));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: true);

        f.Clock.Advance(TimeSpan.FromSeconds(10));
        DateTimeOffset nowyPoczatek = f.Clock.UtcNow;
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        f.Clock.Advance(Prog - TimeSpan.FromSeconds(1));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);

        Assert.Equal(nowyPoczatek, Assert.Single(f.Machine.ActiveAlerts).StartedAt);
    }

    [Fact]
    public void OnMainScreenState_KolejneSprawdzeniaInnegoEkranu_TylkoOdswiezajaAlertBezZdarzen()
    {
        Fixture f = NewFixture();
        Alert alert = RaiseWrongScreen(f);
        f.Events.Clear();

        for (int i = 0; i < 5; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(20));
            f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false, "captures/aidite1_ekran.png");
        }

        Assert.Empty(f.Events);
        Alert odswiezony = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(alert.StartedAt, odswiezony.StartedAt);
        Assert.Equal(f.Clock.UtcNow, odswiezony.LastSeenAt);
        Assert.Equal("captures/aidite1_ekran.png", odswiezony.CaptureFile);
    }

    [Fact]
    public void Tick_PoZamknieciuKrzyzykiem_PonawiaPrzypomnienieGdyEkranNadalNiewlasciwy()
    {
        // Dlaczego: „✕” nie może wyciszyć przypomnienia na zawsze — dopóki HMI pokazuje inny ekran,
        // program nie widzi błędów tej maszyny. Po WrongScreenRemindAfterMs alert wraca jako Escalated
        // (pop-up pokazuje Active/Escalated).
        Fixture f = NewFixture(Timing with { WrongScreenRemindAfterMs = 300000 });
        Alert alert = RaiseWrongScreen(f);
        f.Machine.Acknowledge(alert.Key, "PC-1", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromMinutes(4));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        f.Machine.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(f.Machine.ActiveAlerts).Status);
        Assert.Empty(f.Events);

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        f.Machine.Tick();

        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Escalated, e.Kind);
        Assert.Equal(AlertStatus.Escalated, Assert.Single(f.Machine.ActiveAlerts).Status);
    }

    [Fact]
    public void Tick_GdyPonawianieWylaczone_NigdyNieEskalujeZamknietegoPrzypomnienia()
    {
        Fixture f = NewFixture(Timing with { WrongScreenRemindAfterMs = 0 });
        Alert alert = RaiseWrongScreen(f);
        f.Machine.Acknowledge(alert.Key, "PC-1", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        for (int i = 0; i < 60; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
            f.Machine.Tick();
        }

        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Escalated);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(f.Machine.ActiveAlerts).Status);
    }

    [Fact]
    public void OnMachineOffline_ZamykaPrzypomnienieIZerujeLicznik()
    {
        // Dlaczego: bez obrazu nie wiemy, jaki ekran pokazuje HMI, a restart maszyny (typowa przyczyna
        // utraty łączności) i tak przywraca ekran główny.
        Fixture f = NewFixture();
        RaiseWrongScreen(f);
        f.Events.Clear();

        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);

        Assert.DoesNotContain(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.WrongScreen);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);

        // Po powrocie: inny ekran musi znów trwać PEŁNY próg, zanim powstanie nowy alert.
        f.Machine.OnMachineOnline("aidite1");
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        f.Clock.Advance(Prog - TimeSpan.FromSeconds(1));
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: false);
        Assert.DoesNotContain(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.WrongScreen);
    }

    [Fact]
    public void Tick_BezPotwierdzonegoPowrotuEkranu_NieZamykaAlertu()
    {
        Fixture f = NewFixture();
        RaiseWrongScreen(f);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(40));
        f.Machine.Tick();
        Assert.Single(f.Machine.ActiveAlerts);

        f.Clock.Advance(TimeSpan.FromSeconds(10));   // 50 s > 2 × 20 s + 5 s
        f.Machine.Tick();
        Assert.Single(f.Machine.ActiveAlerts);
        Assert.Empty(f.Events);

        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: true);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnMainScreenState_GdyPamiecAckZnaKlucz_TworzyAlertOdRazuPotwierdzonyZeZdarzeniemUpdated()
    {
        Fixture f = NewFixture();
        AlertKey key = AlertKey.WrongScreen("aidite1");
        f.Acks.Record(new AckRecord(key, "PC-5", "zdalny", f.Clock.UtcNow));

        Alert alert = RaiseWrongScreen(f);

        Assert.Equal(AlertStatus.Acknowledged, alert.Status);
        Assert.Equal("PC-5", alert.AckedByHost);
        Assert.Equal(AlertEventKind.Updated, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnMainScreenState_NieWplywaNaAlertBleduTejSamejMaszyny()
    {
        // Dlaczego: to niezależne rodzaje — błąd (czerwień) i przypomnienie o ekranie mogą (teoretycznie)
        // współistnieć; przypomnienie nie może zamykać ani wyciszać błędu.
        Fixture f = NewFixture();
        Alert blad = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        RaiseWrongScreen(f);

        Assert.Equal(2, f.Machine.ActiveAlerts.Count);
        f.Machine.OnMainScreenState(Machine(), mainScreenVisible: true);

        Alert pozostaly = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(blad.Key, pozostaly.Key);
        Assert.Equal(AlertStatus.Active, pozostaly.Status);
    }

    [Fact]
    public void Tick_GdyEskalacjaBleduWylaczonaZerem_PotwierdzonyBladNigdyNieDzwoniPonownie()
    {
        Fixture f = NewFixture(Timing with { EscalationAfterAckMs = 0 });
        Alert blad = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Machine.Acknowledge(blad.Key, "PC-1", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        for (int i = 0; i < 60; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);   // błąd wciąż na ekranie
            f.Machine.Tick();
        }

        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Escalated);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(f.Machine.ActiveAlerts).Status);
    }
}
