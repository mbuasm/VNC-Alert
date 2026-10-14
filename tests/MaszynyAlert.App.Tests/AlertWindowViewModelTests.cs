using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.Tests;

public sealed class AlertWindowViewModelTests
{
    private static Alert SampleAlert(string shortText, string fullText) => new()
    {
        Key = AlertKey.ForRule("aidite1", "tray-grab"),
        MachineId = "aidite1",
        MachineName = "Aidite 1",
        Kind = AlertKind.Error,
        ShortText = shortText,
        FullText = fullText,
        Category = "tacka",
        StartedAt = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero),
        LastSeenAt = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero),
        Status = AlertStatus.Active,
        Sound = true,
    };

    [Fact]
    public void ZmianaTekstuTegoSamegoAlarmu_ZachowujeRozwinietaPomoc()
    {
        var vm = new AlertWindowViewModel(_ => Task.CompletedTask, _ => "pomoc");
        vm.Refresh([SampleAlert("Nie pobrano tacki", "Failed to grab a tray")]);
        vm.Items[0].IsHelpExpanded = true;

        vm.Refresh([SampleAlert("Nie pobrano tacki", "Failed to grab a tray (2)")]);

        Assert.True(vm.Items[0].IsHelpExpanded);
        Assert.Equal("Failed to grab a tray (2)", vm.Items[0].Line2);
    }

    private static Alert Offline(string id, int minute, AlertStatus status = AlertStatus.Active) => new()
    {
        Key = AlertKey.Offline(id),
        MachineId = id,
        MachineName = id,
        Kind = AlertKind.Offline,
        ShortText = "Brak łączności",
        Category = "lacznosc",
        StartedAt = new DateTimeOffset(2026, 10, 4, 3, minute, 0, TimeSpan.Zero),
        LastSeenAt = new DateTimeOffset(2026, 10, 4, 3, minute, 0, TimeSpan.Zero),
        Status = status,
        Sound = true,
    };

    [Fact]
    public void NajnowszyDzwoniacyAlarm_JestNaGorzeListy()
    {
        var vm = new AlertWindowViewModel(_ => Task.CompletedTask, _ => "pomoc");

        vm.Refresh([Offline("test02", 1), Offline("test04", 5), Offline("test08", 3), Offline("test09", 9, AlertStatus.Acknowledged)]);

        Assert.Equal(["test04", "test08", "test02"], vm.Items.Select(i => i.Key.MachineId).ToArray());
    }

    [Fact]
    public void Refresh_ZglaszaNowyAlarmTylkoRazINieDlaZmianyTekstu()
    {
        var vm = new AlertWindowViewModel(_ => Task.CompletedTask, _ => "pomoc");

        Assert.True(vm.Refresh([SampleAlert("Nie pobrano tacki", "Failed to grab a tray")]));
        Assert.False(vm.Refresh([SampleAlert("Nie pobrano tacki", "Failed to grab a tray (2)")]));
        Assert.True(vm.Refresh([SampleAlert("Nie pobrano tacki", "Failed to grab a tray (2)"), Offline("test04", 5)]));
        Assert.False(vm.Refresh([Offline("test04", 5)]));
    }

    [Fact]
    public void NowyAlarm_MaPomocZwinieta()
    {
        var vm = new AlertWindowViewModel(_ => Task.CompletedTask, _ => "pomoc");

        vm.Refresh([SampleAlert("Nie pobrano tacki", "Failed to grab a tray")]);

        Assert.False(vm.Items[0].IsHelpExpanded);
    }
}
