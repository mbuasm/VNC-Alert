using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertSoundScheduleTests
{
    private static Alert Error(string machine = "a") => new()
    {
        Key = new(machine, "code-705"), MachineId = machine, MachineName = machine,
        Kind = AlertKind.Error, ShortText = "Servo", Category = "inny", Sound = true,
        Status = AlertStatus.Active, StartedAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void PlaysImmediatelyAndEveryMinute_RegardlessOfRepeatedRefreshes()
    {
        var schedule = new AlertSoundSchedule(60000);
        var alert = Error();
        Assert.True(schedule.Update([alert], 1000));
        for (long t = 2000; t < 61000; t += 1000) Assert.False(schedule.Update([alert], t));
        Assert.True(schedule.Update([alert], 61000));
        Assert.False(schedule.Update([alert], 62000));
        Assert.True(schedule.Update([alert], 121000));
    }

    [Fact]
    public void BetterCodeInSameEpisode_DoesNotReplayButAnotherMachineDoes()
    {
        var schedule = new AlertSoundSchedule(60000);
        var alert = Error();
        Assert.True(schedule.Update([alert], 0));
        var better = alert with { Key = new("a", "code-706"), ShortText = "Servo 7" };
        Assert.False(schedule.Update([better], 1000));
        Assert.True(schedule.Update([better, Error("b")], 2000));
    }

    [Theory]
    [InlineData(AlertStatus.Acknowledged)]
    [InlineData(AlertStatus.Closed)]
    public void AcknowledgedOrClosed_StopsRepeating(AlertStatus status)
    {
        var schedule = new AlertSoundSchedule(60000);
        var alert = Error();
        schedule.Update([alert], 0);
        Assert.False(schedule.Update([alert with { Status = status }], 60000));
        Assert.False(schedule.HasRingingAlerts);
        Assert.True(schedule.Update([alert with { StartedAt = alert.StartedAt.AddMinutes(5) }], 61000));
    }

    [Fact]
    public void NewEpisodeWithSameKeyBetweenRefreshes_ReplaysImmediately()
    {
        var schedule = new AlertSoundSchedule(60000);
        var alert = Error();
        schedule.Update([alert], 0);
        Assert.True(schedule.Update([alert with { StartedAt = alert.StartedAt.AddSeconds(10) }], 10000));
    }

    [Fact]
    public void SilentAlertsAndSeparatePopups_DoNotUseErrorSoundSchedule()
    {
        var schedule = new AlertSoundSchedule(60000);
        var alert = Error();
        Assert.False(schedule.Update([
            alert with { Sound = false },
            alert with { Kind = AlertKind.WrongScreen },
            alert with { Kind = AlertKind.MachineIdle },
        ], 0));
        Assert.False(schedule.HasRingingAlerts);
        Assert.False(AlertVisibility.IsVisibleInAlertWindow(alert with { Kind = AlertKind.WrongScreen }));
    }

    [Fact]
    public void EscalationAfterAcknowledgment_PlaysImmediately()
    {
        var schedule = new AlertSoundSchedule(60000);
        var alert = Error();
        schedule.Update([alert with { Status = AlertStatus.Acknowledged }], 0);
        Assert.True(schedule.Update([alert with { Status = AlertStatus.Escalated, EscalatedAt = alert.StartedAt.AddMinutes(15) }], 1));
    }
}
