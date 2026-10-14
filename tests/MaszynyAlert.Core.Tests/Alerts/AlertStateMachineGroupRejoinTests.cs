using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.Cluster;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineGroupRejoinTests
{
    private static readonly TimingConfig Hall = new() { EscalationAfterAckMs = 120000 };

    private static readonly MachineConfig Aidite = new()
    {
        Id = "aidite2", Name = "Aidite 2", Host = "192.168.1.105", Port = 9100, Profile = "opencnc",
    };

    private static readonly MappedMessage Home =
        new("code-1200", "Bazowanie (Home)", "restart", "No.1200 [p]Request home alarm", "NO I2OO P REQUEST HOME ALARM", true)
        { Priority = 50 };

    private static readonly MappedMessage Servo =
        new("code-705", "Servo 6", "restart", "No. 705 [N]Axis 6 servo alarm", "NO 7O5 N AXIS 6 SERVO ALARM", true)
        { Priority = 90 };

    private static readonly MappedMessage Contentless =
        new(null, "Błąd (nie odczytano treści)", "inny", string.Empty, string.Empty, false);

    private sealed record Fixture(FakeClock Clock, AlertStateMachine Machine, List<AlertEvent> Events);

    private static Fixture Create(bool group = true, TimingConfig? timing = null)
    {
        var clock = new FakeClock();
        var machine = new AlertStateMachine(clock, timing ?? Hall, new AckMemory(clock, TimeSpan.FromHours(2)));
        machine.UseGroupProtocol(group);
        var events = new List<AlertEvent>();
        machine.Changed += events.Add;
        return new Fixture(clock, machine, events);
    }

    private static void TickFor(Fixture f, TimeSpan span)
    {
        for (int i = 0; i < (int)span.TotalSeconds; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Machine.Tick();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // A. Łączenie epizodów także w grupie
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Grupa_Aidite2_MrugniecieZPustymOdczytemPoPotwierdzeniu_NieDajeNowegoAlarmu()
    {
        Fixture f = Create();
        Alert first = f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        f.Machine.AcknowledgeLocal(first.Key, "PSZEMEK", "scanl");
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromSeconds(19));
        f.Events.Clear();

        f.Machine.OnRedConfirmed(Aidite, Contentless, null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.OnRedCleared(Aidite.Id);

        Alert current = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(first.EpisodeId, current.EpisodeId);   // alert był jeszcze otwarty — ta sama tożsamość
        Assert.Equal(first.StartedAt, current.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, current.Status);

        TickFor(f, TimeSpan.FromMilliseconds(Hall.EpisodeCloseAfterMs + 2000));

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.DoesNotContain(f.Events, e => e.Kind is AlertEventKind.Raised or AlertEventKind.Escalated);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Alert closed = Assert.Single(f.Machine.RecentHistory);
        Assert.Equal(first.EpisodeId, closed.EpisodeId);
    }

    [Fact]
    public void Grupa_TenSamKomunikatPoZamknieciuPotwierdzonego_WznawiaEpizodZNowymUuidBezDzwieku()
    {
        Fixture f = Create();
        Alert first = f.Machine.OnRedConfirmed(Aidite, Home, "captures/home.png");
        f.Machine.AcknowledgeLocal(first.Key, "PSZEMEK", "scanl");
        Alert acked = Assert.Single(f.Machine.ActiveAlerts);
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromMilliseconds(Hall.EpisodeCloseAfterMs + 1000));
        Assert.Empty(f.Machine.ActiveAlerts);
        f.Events.Clear();

        Alert again = f.Machine.OnRedConfirmed(Aidite, Home, null);

        // Stary UUID poszedł do grupy jako zamknięty — wznowienie ma nowy UUID i wskazuje poprzednika.
        Assert.NotEqual(first.EpisodeId, again.EpisodeId);
        Assert.Equal([first.EpisodeId], again.SupersededEpisodeIds);
        Assert.Null(again.ReconciledAt);   // nie „teraz”: to granica ACK w grupie (patrz RejoinRetiredEpisodeLocked)
        Assert.Equal(first.StartedAt, again.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, again.Status);
        Assert.Equal(acked.AcknowledgedAt, again.AcknowledgedAt);
        Assert.Equal("PSZEMEK", again.AckedByHost);
        Assert.Equal("scanl", again.AckedByUser);
        Assert.Equal("captures/home.png", again.CaptureFile);
        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Updated, e.Kind);   // history.csv: „aktualizacja”, nie „nowy”
        Assert.Contains("epizod połączony", e.Reason);

        // Ponowne zamknięcie i kolejne wznowienie: łańcuch poprzedników rośnie, bez powtórzeń.
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromMilliseconds(Hall.EpisodeCloseAfterMs + 1000));
        Alert third = f.Machine.OnRedConfirmed(Aidite, Home, null);
        Assert.Equal([again.EpisodeId, first.EpisodeId], third.SupersededEpisodeIds);
        Assert.Equal(first.StartedAt, third.StartedAt);
        Assert.DoesNotContain(f.Events, x => x.Kind == AlertEventKind.Raised);
    }

    [Fact]
    public void Grupa_BezPotwierdzenia_WznowienieZostajeAktywneBezNowegoZdarzeniaIZTymSamymPoczatkiem()
    {
        Fixture f = Create();
        Alert first = f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromSeconds(19));
        f.Events.Clear();

        // Mrugnięcie przed zamknięciem: pusty odczyt, potem ten sam komunikat.
        f.Machine.OnRedConfirmed(Aidite, Contentless, null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Alert blink = f.Machine.OnRedConfirmed(Aidite, Home, null);
        Assert.Equal(first.EpisodeId, blink.EpisodeId);
        Assert.Equal(AlertStatus.Active, blink.Status);

        // Mrugnięcie po zamknięciu: wznowienie.
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromMilliseconds(Hall.EpisodeCloseAfterMs + 1000));
        Alert again = f.Machine.OnRedConfirmed(Aidite, Home, null);

        Assert.Equal(AlertStatus.Active, again.Status);
        Assert.Equal(first.StartedAt, again.StartedAt);
        Assert.Contains(first.EpisodeId, again.SupersededEpisodeIds);
        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Fact]
    public void Grupa_InnyKomunikatPoPrzerwie_NadalNowyAlarmZDzwiekiem()
    {
        Fixture f = Create();
        Alert servo = f.Machine.OnRedConfirmed(Aidite, Servo, null);
        f.Machine.AcknowledgeLocal(servo.Key, "PSZEMEK", "scanl");
        f.Machine.OnRedCleared(Aidite.Id);
        f.Clock.Advance(TimeSpan.FromMilliseconds(Hall.EpisodeBreakAfterMs + 1000));
        f.Events.Clear();

        Alert home = f.Machine.OnRedConfirmed(Aidite, Home, null);

        Assert.NotEqual(servo.Key, home.Key);
        Assert.NotEqual(servo.EpisodeId, home.EpisodeId);
        Assert.Equal(AlertStatus.Active, home.Status);
        Assert.Equal(f.Clock.UtcNow, home.StartedAt);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Key == home.Key);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Key == servo.Key);

    }

    [Fact]
    public void Grupa_InnyKomunikatPoZamknieciuPoprzedniego_NieWznawiaCudzegoEpizodu()
    {
        Fixture f = Create();
        Alert servo = f.Machine.OnRedConfirmed(Aidite, Servo, null);
        f.Machine.AcknowledgeLocal(servo.Key, "PSZEMEK", "scanl");
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromMilliseconds(Hall.EpisodeCloseAfterMs + 1000));
        Assert.Empty(f.Machine.ActiveAlerts);
        f.Events.Clear();

        Alert home = f.Machine.OnRedConfirmed(Aidite, Home, null);

        Assert.Equal(AlertStatus.Active, home.Status);
        Assert.Empty(home.SupersededEpisodeIds);
        Assert.Equal(f.Clock.UtcNow, home.StartedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void BezGrupy_WznowienieZachowujeTenSamUuid()
    {
        Fixture f = Create(group: false);
        Alert first = f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromMilliseconds(Hall.EpisodeCloseAfterMs + 1000));

        Alert again = f.Machine.OnRedConfirmed(Aidite, Home, null);

        Assert.Equal(first.EpisodeId, again.EpisodeId);
        Assert.Empty(again.SupersededEpisodeIds);
        Assert.Null(again.ReconciledAt);
    }

    [Fact]
    public async Task Grupa_WznowionyEpizod_PartnerPrzyjmujeGoPotwierdzonego_AStaryUuidJestZamkniety()
    {
        await using var pair = new GroupRuntimeTests.Pair();
        await pair.Start();
        Assert.True(pair.A.IsWatcher);

        Alert first = pair.Raise();
        pair.AState.AcknowledgeLocal(first.Key, "A", "operator");
        await pair.Tick(2);
        Alert seen = Assert.Single(pair.B.MergedAlerts);
        Assert.Equal(first.EpisodeId, seen.EpisodeId);
        Assert.Equal(AlertStatus.Acknowledged, seen.Status);

        pair.AState.OnRedCleared(pair.Machine.Id);
        for (int i = 0; i < 32; i++) { pair.AState.Tick(); await pair.Tick(1); }
        Assert.Empty(pair.AState.ActiveAlerts);
        Assert.Empty(pair.B.MergedAlerts);

        Alert again = pair.AState.OnRedConfirmed(pair.Machine, pair.Message, null);
        await pair.Tick(2);

        Alert remote = Assert.Single(pair.B.MergedAlerts);
        Assert.Equal(again.EpisodeId, remote.EpisodeId);
        Assert.Contains(first.EpisodeId, remote.SupersededEpisodeIds);
        Assert.Equal(first.StartedAt, remote.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, remote.Status);   // u partnera bez dźwięku (okno alertu go nie pokazuje)
        Assert.Contains(pair.B.CaptureState().Closed, c => c.EpisodeId == first.EpisodeId);
        // Obieg przez węzeł obserwatora (SetLocalState → ReconcileGroupState) nie cofa potwierdzenia.
        Alert local = Assert.Single(pair.AState.ActiveAlerts);
        Assert.Equal(again.EpisodeId, local.EpisodeId);
        Assert.Equal(AlertStatus.Acknowledged, local.Status);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(pair.A.MergedAlerts).Status);

        // Dźwięk u partnera: harmonogram nie widzi nowego epizodu (ta sama maszyna, początek, eskalacja).
        var schedule = new AlertSoundSchedule(60000);
        var before = seen with { Status = AlertStatus.Escalated };
        var after = remote with { Status = AlertStatus.Escalated };
        Assert.True(schedule.Update([before], 0));
        Assert.False(schedule.Update([after], 1000));
    }

    // ------------------------------------------------------------------------------------------------
    // B. Eskalacja tylko przy trwającym błędzie
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Aidite1_CzerwienZniklaPrzedUplywemEskalacji_NieMaEskalacjiAlertSieZamyka()
    {
        Fixture f = Create();
        Alert alert = f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Clock.Advance(TimeSpan.FromSeconds(13));
        f.Machine.AcknowledgeLocal(alert.Key, "DESKTOP-CD8GBG4", "Admin");
        for (int i = 0; i < 97; i++) { f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Machine.OnRedConfirmed(Aidite, Home, null); f.Machine.Tick(); }
        f.Machine.OnRedCleared(Aidite.Id);
        f.Events.Clear();

        TickFor(f, TimeSpan.FromSeconds(40));

        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Escalated);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);
        Assert.Empty(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void Eskalacja_CzerwienWracaPrzedZamknieciem_EskalujeWtedy()
    {
        Fixture f = Create();
        Alert alert = f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.AcknowledgeLocal(alert.Key, "PC", "operator");
        f.Clock.Advance(TimeSpan.FromSeconds(100));
        f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromSeconds(25));   // 125 s od ACK, czerwieni brak od 25 s — eskalacja czeka
        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Escalated);

        f.Machine.OnRedConfirmed(Aidite, Home, null);   // ten sam błąd wraca przed zamknięciem
        f.Machine.Tick();

        AlertEvent escalated = Assert.Single(f.Events, e => e.Kind == AlertEventKind.Escalated);
        Assert.Equal(alert.EpisodeId, escalated.Alert.EpisodeId);
        Assert.Equal(AlertStatus.Escalated, Assert.Single(f.Machine.ActiveAlerts).Status);
    }

    [Fact]
    public void Eskalacja_CzerwienTrwa_EskalujeJakDotad_TakzeBezGrupy()
    {
        foreach (bool group in new[] { true, false })
        {
            Fixture f = Create(group);
            Alert alert = f.Machine.OnRedConfirmed(Aidite, Home, null);
            f.Machine.AcknowledgeLocal(alert.Key, "PC", "operator");
            for (int i = 0; i < 121; i++) { f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Machine.OnRedConfirmed(Aidite, Home, null); f.Machine.Tick(); }
            Assert.Single(f.Events, e => e.Kind == AlertEventKind.Escalated);
        }
    }

    [Fact]
    public void Eskalacja_MrugniecieKrotszeNizPrzerwaEpizodu_NieWstrzymujeEskalacji()
    {
        Fixture f = Create();
        Alert alert = f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.AcknowledgeLocal(alert.Key, "PC", "operator");
        f.Clock.Advance(TimeSpan.FromSeconds(115));
        f.Machine.OnRedConfirmed(Aidite, Home, null);
        f.Machine.OnRedCleared(Aidite.Id);
        TickFor(f, TimeSpan.FromSeconds(6));   // 121 s od ACK, czerwieni brak od 6 s (< 10 s)
        Assert.Single(f.Events, e => e.Kind == AlertEventKind.Escalated);
    }

    [Fact]
    public void Eskalacja_BezObserwacjiEkranu_DecydujeOstatniOdczytPrzedUtrata()
    {
        // Błąd widoczny tuż przed utratą obserwacji → eskalacja jak dotąd (z dopiskiem o nierozstrzygniętym ekranie).
        Fixture visible = Create();
        Alert a = visible.Machine.OnRedConfirmed(Aidite, Home, null);
        visible.Machine.AcknowledgeLocal(a.Key, "PC", "operator");
        visible.Clock.Advance(TimeSpan.FromSeconds(60));
        visible.Machine.OnRedConfirmed(Aidite, Home, null);
        visible.Machine.SetScreenObservationAvailable(Aidite.Id, false);
        TickFor(visible, TimeSpan.FromSeconds(70));
        AlertEvent escalated = Assert.Single(visible.Events, e => e.Kind == AlertEventKind.Escalated);
        Assert.Contains("nierozstrzygnięty ekran", escalated.Reason);

        // Czerwień zniknęła ≥ 10 s PRZED utratą obserwacji → eskalacja czeka na rzeczywisty odczyt.
        Fixture gone = Create();
        Alert b = gone.Machine.OnRedConfirmed(Aidite, Home, null);
        gone.Machine.AcknowledgeLocal(b.Key, "PC", "operator");
        gone.Clock.Advance(TimeSpan.FromSeconds(60));
        gone.Machine.OnRedConfirmed(Aidite, Home, null);
        gone.Machine.OnRedCleared(Aidite.Id);
        gone.Clock.Advance(TimeSpan.FromSeconds(15));
        gone.Machine.SetScreenObservationAvailable(Aidite.Id, false);
        TickFor(gone, TimeSpan.FromSeconds(120));
        Assert.DoesNotContain(gone.Events, e => e.Kind == AlertEventKind.Escalated);
        Assert.Single(gone.Machine.ActiveAlerts);   // bez obserwacji nie zamyka się (bez zmian)

        // Powrót ekranu z tym samym błędem → eskalacja od razu.
        gone.Machine.SetScreenObservationAvailable(Aidite.Id, true);
        gone.Machine.OnRedConfirmed(Aidite, Home, null);
        gone.Machine.Tick();
        Assert.Single(gone.Events, e => e.Kind == AlertEventKind.Escalated);
    }
}
