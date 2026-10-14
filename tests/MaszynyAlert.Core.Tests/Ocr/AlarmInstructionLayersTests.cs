using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class AlarmInstructionLayersTests
{
    private static Dictionary<string, string> Map(params (string Key, string Text)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Text, StringComparer.Ordinal);

    [Fact]
    public void LocalOverlayWinsForSameKeyAndProfileKeysStayVisible()
    {
        var layers = new AlarmInstructionLayers();
        layers.SetProfile(Map(("code:705", "profil 705"), ("code:1200", "profil 1200")));
        layers.SetLocal(Map(("code:705", "lokalnie 705"), ("offline", "lokalnie offline")));

        Assert.Equal("lokalnie 705", layers.Effective["code:705"]);
        Assert.Equal("profil 1200", layers.Effective["code:1200"]);
        Assert.Equal("lokalnie offline", layers.Effective["offline"]);
        Assert.Equal(3, layers.Effective.Count);
        Assert.Equal(["code:705", "offline"], layers.LocalOverrides);
    }

    [Fact]
    public void RemovingLocalEntryRestoresProfileTextInsteadOfDeletingIt()
    {
        var layers = new AlarmInstructionLayers();
        layers.SetProfile(Map(("code:705", "profil 705")));
        layers.SetLocal(Map(("code:705", "lokalnie 705")));

        layers.SetLocal(Map());

        Assert.Equal("profil 705", layers.Effective["code:705"]);
        Assert.Empty(layers.LocalOverrides);
    }

    [Fact]
    public void ProfileImportedAfterLocalFileKeepsLocalEdits()
    {
        // Kolejność startu: plik lokalny wczytany w konstruktorze, potem UseProfile z zatwierdzonej generacji.
        var layers = new AlarmInstructionLayers();
        layers.SetLocal(Map(("code:705", "lokalnie 705")));
        layers.SetProfile(Map(("code:705", "profil 705"), ("code:1200", "profil 1200")));

        Assert.Equal("lokalnie 705", layers.Effective["code:705"]);
        Assert.Equal("profil 1200", layers.Effective["code:1200"]);
        Assert.Equal("profil 705", layers.Profile["code:705"]);
        Assert.Equal(["code:705"], layers.LocalOverrides);
    }

    [Fact]
    public void LocalEntryEqualToProfileIsNotReportedAsOverride()
    {
        var layers = new AlarmInstructionLayers();
        layers.SetProfile(Map(("code:705", "ta sama treść")));
        layers.SetLocal(Map(("code:705", "ta sama treść")));

        Assert.Empty(layers.LocalOverrides);
        Assert.Equal("ta sama treść", layers.Effective["code:705"]);
    }

    [Fact]
    public void ReplacingProfileDropsOldProfileKeysButNotLocalOnes()
    {
        var layers = new AlarmInstructionLayers();
        layers.SetProfile(Map(("code:705", "stary profil"), ("code:1200", "stary 1200")));
        layers.SetLocal(Map(("offline", "lokalnie offline")));

        layers.SetProfile(Map(("code:705", "nowy profil")));

        Assert.Equal("nowy profil", layers.Effective["code:705"]);
        Assert.False(layers.Effective.ContainsKey("code:1200"));
        Assert.Equal("lokalnie offline", layers.Effective["offline"]);
    }

    [Fact]
    public void WithoutProfileEffectiveEqualsLocalAndLayersAreIsolatedCopies()
    {
        var local = Map(("offline", "lokalnie offline"));
        var layers = new AlarmInstructionLayers();
        layers.SetLocal(local);
        local["offline"] = "zmienione po wczytaniu";

        Assert.Equal("lokalnie offline", layers.Effective["offline"]);
        Assert.Empty(layers.Profile);
        Assert.Equal(["offline"], layers.LocalOverrides);
    }
}
