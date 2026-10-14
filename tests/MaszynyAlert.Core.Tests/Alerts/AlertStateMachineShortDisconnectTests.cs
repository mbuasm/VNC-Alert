using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineShortDisconnectTests
{
    private static readonly TimingConfig Timing = new();

    private static MachineConfig Machine() => new()
    {
        Id = "fake3", Name = "Fake 3", Host = "127.0.0.1", Port = 5903, Profile = "aidite5x",
    };

    private static MappedMessage Coolant() =>
        new("coolant", "Płyn obróbkowy — praca wstrzymana", "plyn", "Coolant low", "COOLANT LOW", true);

    private static MappedMessage Servo() =>
        new("servo", "Servo 6", "serwo", "No. 705 [N]Axis 6 servo alarm", "NO. 705 [N]AXIS 6 SERVO ALARM", true);

    private static MappedMessage Empty() =>
        new(null, "Błąd (nie odczytano treści)", "inny", "", "", false);

    private sealed record Fixture(FakeClock Clock, AlertStateMachine Machine, List<AlertEvent> Events);

    private static Fixture NewFixture()
    {
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, Timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        return new Fixture(clock, sm, events);
    }

    /// <summary>Zerwanie VNC o długości <paramref name="gap"/>: monitor zgłasza offline od razu, potem powrót.</summary>
    private static void Drop(Fixture f, TimeSpan gap)
    {
        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);
        f.Clock.Advance(gap);
        f.Machine.Tick();
        f.Machine.OnMachineOnline("fake3");
    }

    [Fact]
    public void T06_ZerwanieTrzySekundyPrzyTymSamymBledzie_ZostajeTenSamAlertZPotwierdzeniemBezDzwieku()
    {
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");
        f.Clock.Advance(TimeSpan.FromSeconds(12));

        Drop(f, TimeSpan.FromSeconds(3));
        f.Events.Clear();
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Alert again = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);

        Assert.Equal(first.EpisodeId, again.EpisodeId);
        Assert.Equal(first.StartedAt, again.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, again.Status);
        Assert.DoesNotContain(f.Events, e => e.Kind is AlertEventKind.Raised or AlertEventKind.Closed);
        Assert.Single(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void T06_PierwszyOdczytPoZerwaniuBezTresci_NieOtwieraNowegoAlertu()
    {
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        Drop(f, TimeSpan.FromSeconds(3));
        f.Events.Clear();
        f.Machine.OnRedConfirmed(Machine(), Empty(), null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Alert again = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);

        Assert.Equal(first.EpisodeId, again.EpisodeId);
        Assert.Equal(AlertStatus.Acknowledged, again.Status);
        Assert.DoesNotContain(f.Events, e => e.Kind is AlertEventKind.Raised or AlertEventKind.Closed);
        Assert.Single(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void T06_PietnascieZerwanWJednymBledzie_JedenAlertJedenDzwiek()
    {
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
        for (int i = 0; i < 15; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(20));
            f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
            Drop(f, TimeSpan.FromSeconds(3));
            f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
        }

        Assert.Equal(1, f.Events.Count(e => e.Kind == AlertEventKind.Raised));
        Assert.Single(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void InnyKomunikatPoKrotkimZerwaniu_NadalNowyAlarmZDzwiekiem()
    {
        // Zasada nadrzędna: krótka przerwa nie może wyciszyć prawdziwie nowego błędu.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        Drop(f, TimeSpan.FromSeconds(3));
        f.Events.Clear();
        Alert other = f.Machine.OnRedConfirmed(Machine(), Servo(), null);

        Assert.NotEqual(first.EpisodeId, other.EpisodeId);
        Assert.Equal(AlertStatus.Active, other.Status);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Sound);
    }

    [Fact]
    public void ZerwanieDziesiecSekund_ToNowyEpizodJakPoRestarcie()
    {
        // Granica = episodeBreakAfterMs (10 s): od niej przerwa w łączności przerywa epizod jak dotąd.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
        f.Machine.AcknowledgeLocal(first.Key, "PC-1", "operator");

        Drop(f, TimeSpan.FromMilliseconds(Timing.EpisodeBreakAfterMs));
        f.Events.Clear();
        Alert again = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);

        Assert.NotEqual(first.EpisodeId, again.EpisodeId);
        Assert.Equal(AlertStatus.Active, again.Status);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Sound);
    }

    [Fact]
    public void ZerwanieTuzPonizejGranicy_ToTenSamEpizod()
    {
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);

        Drop(f, TimeSpan.FromMilliseconds(Timing.EpisodeBreakAfterMs - 1));
        Alert again = f.Machine.OnRedConfirmed(Machine(), Coolant(), null);

        Assert.Equal(first.EpisodeId, again.EpisodeId);
    }

    [Fact]
    public void W3B03_NowyBladPoZamknieciuPoprzedniego_PierwszyOdczytBezTresci_JedenAlertUzupelnionyTrescia()
    {
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), Coolant(), null);
        f.Machine.OnRedCleared("fake3");
        f.Clock.Advance(TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs + 1000));
        f.Machine.Tick();
        Assert.Empty(f.Machine.ActiveAlerts);
        f.Clock.Advance(TimeSpan.FromSeconds(37));
        f.Events.Clear();

        f.Machine.OnRedConfirmed(Machine(), Empty(), null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Alert servo = f.Machine.OnRedConfirmed(Machine(), Servo(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(servo.Key, only.Key);
        Assert.Equal("Servo 6", only.ShortText);
        Assert.Equal(1, f.Events.Count(e => e.Kind == AlertEventKind.Raised));
        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Closed);
    }
}
