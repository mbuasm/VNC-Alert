using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineDisabledMachineTests
{
    private static MachineConfig Machine(string id, string name) => new()
    {
        Id = id, Name = name, Host = "192.168.1.108", Port = 9100, Profile = "opencnc",
    };

    private static MappedMessage TrayMessage() =>
        new("tray-grab", "Nie udało się pobrać tacki", "tacka", "Failed to grab a tray", "FAILED TO GRAB A TRAY", true);

    [Fact]
    public void OnMachineDisabled_ClosesEveryOpenAlertOfThatMachineOnly_AndPublishesClosedEvents()
    {
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        var aidite1 = Machine("aidite1", "Aidite 1");
        var aidite2 = Machine("aidite2", "Aidite 2");
        sm.OnRedConfirmed(aidite1, TrayMessage(), null);
        sm.OnMainScreenState(aidite1, mainScreenVisible: false); // „inny ekran HMI” — od razu (wrongScreenAlertAfterMs = 0)
        sm.Tick();
        sm.OnRedConfirmed(aidite2, TrayMessage(), null);
        int openOnAidite1 = sm.ActiveAlerts.Count(a => a.MachineId == "aidite1" && a.Status != AlertStatus.Closed);
        Assert.Equal(2, openOnAidite1);
        sm.Changed += events.Add;

        sm.OnMachineDisabled("aidite1");

        Assert.DoesNotContain(sm.ActiveAlerts, a => a.MachineId == "aidite1" && a.Status != AlertStatus.Closed);
        Alert other = Assert.Single(sm.ActiveAlerts, a => a.Status != AlertStatus.Closed);
        Assert.Equal("aidite2", other.MachineId);
        Assert.Equal(openOnAidite1, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(AlertEventKind.Closed, e.Kind);
            Assert.Equal(AlertStatus.Closed, e.Alert.Status);
            Assert.Equal(clock.UtcNow, e.Alert.ClosedAt);
            Assert.Equal("aidite1", e.Alert.MachineId);
        });
        Assert.Contains(sm.RecentlyClosedKeys(TimeSpan.FromMinutes(1)), k => k.MachineId == "aidite1");
    }

    [Fact]
    public void OnMachineDisabled_ForAMachineWithoutAlerts_DoesNothing()
    {
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;

        sm.OnMachineDisabled("aidite7");

        Assert.Empty(events);
        Assert.Empty(sm.ActiveAlerts);
    }

    /// <summary>Po ponownym włączeniu maszyna startuje od zera: licznik postoju nie pamięta czasu sprzed wyłączenia.</summary>
    [Fact]
    public void AfterReEnabling_TheIdleCounterStartsFromZero()
    {
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        var aidite1 = Machine("aidite1", "Aidite 1");
        sm.OnProgressState(aidite1, atFull: true);
        clock.Advance(TimeSpan.FromSeconds(100));

        sm.OnMachineDisabled("aidite1");
        clock.Advance(TimeSpan.FromSeconds(30));
        sm.OnProgressState(aidite1, atFull: true);
        sm.Tick();

        // 100 s + 30 s > 2 min, ale licznik ruszył od nowa po włączeniu — alertu postoju jeszcze nie ma.
        Assert.DoesNotContain(sm.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        clock.Advance(TimeSpan.FromMinutes(2));
        sm.OnProgressState(aidite1, atFull: true);
        sm.Tick();
        Assert.Contains(sm.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle && a.MachineId == "aidite1");
    }
}
