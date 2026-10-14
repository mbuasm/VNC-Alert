using System.ComponentModel;
using System.Reflection;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.App.Tests;

public sealed class PeerRowViewModelTests
{
    private const string Sha40 = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("0.3.412+" + Sha40, "0.3.412")]
    [InlineData("0.3.413-pr78+" + Sha40, "0.3.413-pr78")]
    [InlineData("0.3.0-dev+" + Sha40, "0.3.0-dev")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("", "")]
    public void VersionColumnShowsNumberWithoutCommit(string reported, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var peer = new PeerInfo("PC-A", 100, PeerRole.Watcher, reported, now, 1, null, 3, 3);

        var row = new PeerRowViewModel(peer, now, TimeSpan.FromSeconds(15), isThisComputer: false);

        Assert.Equal(expected, row.Version);
    }
}

public sealed class PeerRowCooperationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncompatibleComputer_ShowsOtherVersionAndRemedy()
    {
        var peer = new PeerInfo("PSZEMEK", 100, PeerRole.Watcher, "0.4.32+abc", Now, 1, null, 9, 9)
            { Compatibility = GroupCompatibility.OtherProfile };

        var row = new PeerRowViewModel(peer, Now, TimeSpan.FromSeconds(15), isThisComputer: false, localVersion: "0.4.79+def");

        Assert.Equal("inne ustawienia grupy albo inna wersja programu (0.4.32) — nie współpracuje z tym komputerem; zainstaluj tę samą wersję, a jeśli to nie pomoże — wczytaj na obu komputerach ten sam, aktualny plik grupy",
            row.CooperationText);
        Assert.True(row.IsIncompatible);
    }

    [Fact]
    public void CompatibleComputer_Cooperates()
    {
        var peer = new PeerInfo("AIDITEKOMP2", 100, PeerRole.Ui, "0.4.79+def", Now, 1, null, 9, 9);

        var row = new PeerRowViewModel(peer, Now, TimeSpan.FromSeconds(15), isThisComputer: false, localVersion: "0.4.79+def");

        Assert.Equal("współpracuje", row.CooperationText);
        Assert.False(row.IsIncompatible);
    }
}

public sealed class PeerRowRefreshInPlaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    private static PeerRowViewModel Row(GroupCompatibility compatibility, string version, PeerRole role, int connected) =>
        new(new PeerInfo("PSZEMEK", 100, role, version, Now, 1, null, connected, 9) { Compatibility = compatibility },
            Now, TimeSpan.FromSeconds(15), isThisComputer: false, localVersion: "0.4.79");

    [Fact]
    public void RowRefreshedInPlace_ShowsTheNewCooperationState()
    {
        var row = Row(GroupCompatibility.Compatible, "0.4.79", PeerRole.Ui, 9);
        var fresh = Row(GroupCompatibility.OtherProfile, "0.4.32", PeerRole.Watcher, 3);
        Assert.Equal("współpracuje", row.CooperationText);
        Assert.False(row.IsIncompatible);
        Assert.True(fresh.IsIncompatible);

        var properties = typeof(PeerRowViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(PeerRowViewModel.Host)).ToArray();
        Assert.Contains(properties, p => p.Name == nameof(PeerRowViewModel.CooperationText));
        Assert.Contains(properties, p => p.Name == nameof(PeerRowViewModel.IsIncompatible));

        MethodInfo? copyFrom = typeof(PeerRowViewModel).GetMethod("CopyFrom", [typeof(PeerRowViewModel)]);
        if (copyFrom is null)
        {
            Assert.All(properties, p => Assert.True(p.SetMethod is null || !p.SetMethod.IsPublic, p.Name));
            return;
        }

        var raised = new List<string?>();
        if ((object)row is INotifyPropertyChanged notify) notify.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        copyFrom.Invoke(row, [fresh]);

        Assert.All(properties, p => Assert.True(Equals(p.GetValue(fresh), p.GetValue(row)),
            $"CopyFrom nie przepisuje „{p.Name}” — dopisz je."));
        Assert.IsAssignableFrom<INotifyPropertyChanged>(row);
        Assert.Contains(nameof(PeerRowViewModel.CooperationText), raised);
        Assert.Contains(nameof(PeerRowViewModel.IsIncompatible), raised);
    }
}
