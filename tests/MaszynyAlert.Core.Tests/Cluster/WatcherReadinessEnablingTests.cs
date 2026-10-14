using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class WatcherReadinessEnablingTests
{
    private static AppConfig HallLikeGroup() => TestPaths.ExampleConfig with
    {
        Machines = TestPaths.ExampleConfig.Machines
            .Select(m => m.Id is "premile" or "cameo1" ? m with { Profile = "aidite-light2", RequiresPassword = true } : m).ToList(),
    };

    private static AppConfig Switch(AppConfig config, params (string Id, bool On)[] switches) => config with
    {
        Machines = config.Machines.Select(m => switches.Any(s => s.Id == m.Id) ? m with { Enabled = switches.Single(s => s.Id == m.Id).On } : m)
            .ToList(),
    };

    private static bool AiditePasswordsOnly(string id) => id.StartsWith("aidite", StringComparison.Ordinal);

    [Fact]
    public void WlaczenieMaszynyWymagajacejHasla_BezHaslaNaTymKomputerze_JestNaLiscie_ITakSamoWEvaluate()
    {
        AppConfig current = HallLikeGroup();
        Assert.True(WatcherReadiness.Evaluate(current, AiditePasswordsOnly, true, GroupCompatibility.Compatible).CanObserve);
        AppConfig next = Switch(current, ("premile", true));

        IReadOnlyList<string> missing = WatcherReadiness.MissingPasswordsAfterEnabling(current, next, AiditePasswordsOnly);

        Assert.Equal(["premile"], missing);
        WatcherReadiness after = WatcherReadiness.Evaluate(next, AiditePasswordsOnly, true, GroupCompatibility.Compatible);
        Assert.False(after.CanObserve);
        Assert.Equal(["premile"], after.MissingPasswordMachineIds);
    }

    [Fact]
    public void ZapisaneHaslo_AlboMaszynaBezHasla_NieJestNaLiscie()
    {
        AppConfig current = HallLikeGroup();

        Assert.Empty(WatcherReadiness.MissingPasswordsAfterEnabling(current, Switch(current, ("premile", true)),
            id => AiditePasswordsOnly(id) || id == "premile"));
        Assert.False(current.Machines.Single(m => m.Id == "amm520-2").RequiresPassword);
        Assert.Empty(WatcherReadiness.MissingPasswordsAfterEnabling(current, Switch(current, ("amm520-2", true)), AiditePasswordsOnly));
    }

    [Fact]
    public void MaszynaWlaczonaJuzWczesniej_BezHasla_NieJestSkutkiemTejZmiany()
    {
        AppConfig current = Switch(HallLikeGroup(), ("amm580", true));
        AppConfig next = Switch(current, ("cameo1", true), ("aidite7", false));

        IReadOnlyList<string> missing = WatcherReadiness.MissingPasswordsAfterEnabling(current, next, AiditePasswordsOnly);

        Assert.Equal(["cameo1"], missing);
        Assert.Equal(["amm580", "cameo1"], WatcherReadiness.Evaluate(next, AiditePasswordsOnly, true, GroupCompatibility.Compatible)
            .MissingPasswordMachineIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WylaczenieMaszyny_AlboBrakZmiany_DajePustaListe()
    {
        AppConfig current = Switch(HallLikeGroup(), ("amm580", true));

        Assert.Empty(WatcherReadiness.MissingPasswordsAfterEnabling(current, current, AiditePasswordsOnly));
        Assert.Empty(WatcherReadiness.MissingPasswordsAfterEnabling(current, Switch(current, ("amm580", false)), AiditePasswordsOnly));
    }

    [Fact]
    public void MonitorowanieWylaczoneGlobalnie_NieZagladaDoHasel()
    {
        AppConfig current = HallLikeGroup() with { Enabled = false };
        int asked = 0;

        Assert.Empty(WatcherReadiness.MissingPasswordsAfterEnabling(current, Switch(current, ("premile", true)), _ => { asked++; return false; }));
        Assert.Equal(0, asked);
    }
}
