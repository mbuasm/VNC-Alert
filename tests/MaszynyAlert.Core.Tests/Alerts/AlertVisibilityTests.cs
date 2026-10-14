using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertVisibilityTests
{
    private static readonly DateTimeOffset Start = FakeClock.DefaultStart;

    private static Alert Alert(AlertStatus status, AlertKind kind = AlertKind.Error, DateTimeOffset? acknowledgedAt = null) => new()
    {
        Key = new AlertKey("aidite1", "rule:servo-alarm"),
        MachineId = "aidite1",
        MachineName = "Aidite 1",
        Kind = kind,
        ShortText = "Alarm serwonapędu osi (servo alarm)",
        Category = "restart",
        Status = status,
        StartedAt = Start,
        LastSeenAt = Start,
        AcknowledgedAt = acknowledgedAt,
    };

    [Fact]
    public void IsVisibleInAlertWindow_AlertuAktywnego_ZwracaPrawde()
    {
        // Dlaczego: po to jest całe okno — trwający, niepotwierdzony błąd maszyny musi być widoczny na
        // wszystkich sześciu komputerach, dopóki ktoś nie kliknie „Idę sprawdzić”.
        Assert.True(AlertVisibility.IsVisibleInAlertWindow(Alert(AlertStatus.Active)));
    }

    [Fact]
    public void IsVisibleInAlertWindow_AlertuPoEskalacji_ZwracaPrawde()
    {
        Assert.True(AlertVisibility.IsVisibleInAlertWindow(Alert(AlertStatus.Escalated, acknowledgedAt: Start)));
    }

    [Fact]
    public void IsVisibleInAlertWindow_AlertuPotwierdzonegoSekundeTemu_ZwracaFalsz()
    {
        Alert acked = Alert(AlertStatus.Acknowledged, acknowledgedAt: Start.AddSeconds(-1));

        Assert.False(AlertVisibility.IsVisibleInAlertWindow(acked));
    }

    [Fact]
    public void IsVisibleInAlertWindow_AlertuZamknietego_ZwracaFalsz()
    {
        // Dlaczego: epizod się skończył (czerwień zniknęła) — nie ma już czego sprawdzać ani pokazywać.
        Assert.False(AlertVisibility.IsVisibleInAlertWindow(Alert(AlertStatus.Closed)));
    }

    [Fact]
    public void IsVisibleInAlertWindow_AlertuPostoju_ZwracaFalszNawetGdyAktywny()
    {
        // Dlaczego: „maszyna stoi bez pracy – dołóż prace” to nie awaria — ma własny, mały pop-up bez
        // dźwięku. W dużym czerwonym oknie błędu wyglądałby jak alarm i uczyłby operatorów ignorować okno.
        Assert.False(AlertVisibility.IsVisibleInAlertWindow(Alert(AlertStatus.Active, AlertKind.MachineIdle)));
    }

    [Fact]
    public void IsVisibleInAlertWindow_AlertuBrakuLacznosci_ZwracaPrawde()
    {
        // Dlaczego: brak łączności z maszyną też jedzie do okna alertu (to jedyny rodzaj poza błędem, który
        // ma tam trafiać) — maszyna może właśnie stać z awarią, której program nie widzi.
        Assert.True(AlertVisibility.IsVisibleInAlertWindow(Alert(AlertStatus.Active, AlertKind.Offline)));
    }

    [Fact]
    public void IsVisibleInAlertWindow_BezAlertu_RzucaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AlertVisibility.IsVisibleInAlertWindow(null!));
    }
}
