using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerProfileKonfiguracjaUwagiTests
{
    internal static AppConfig StanowiskoSprzed0_4_249(AppConfig przyklad) => przyklad with
    {
        Profiles = przyklad.Profiles.Where(p => p.Key is not ("cameo1" or "premile"))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        Machines = przyklad.Machines.Select(m => m.Id is "cameo1" or "premile"
            ? m with { Profile = "aidite-light2", RequiresPassword = true }
            : m).ToList(),
    };

    [Fact]
    public void PK01_PodmianaRulesJsonNaWzorcowyZ0_4_249_NaStanowiskuZDotychczasowymConfigJson_NieZatrzymujeStartu()
    {
        AppConfig stanowisko = StanowiskoSprzed0_4_249(TestPaths.ExampleConfig);
        ConfigLoader.Validate(stanowisko); // sam config.json stanowiska jest poprawny

        // Ten sam słownik bez dwóch nowych baz przechodzi.
        var bezNowychBaz = TestPaths.ExampleRules with
        {
            Databases = new Dictionary<string, RuleDatabase>(
                TestPaths.ExampleRules.Databases.Where(p => p.Key is not ("cameo1" or "premile")), StringComparer.OrdinalIgnoreCase),
        };
        ConfigLoader.Validate(bezNowychBaz, stanowisko);
        Assert.Empty(TestPaths.ExampleRules.Databases["cameo1"].Rules); // nowe bazy są puste — niczego nie wnoszą do dopasowania
        Assert.Empty(TestPaths.ExampleRules.Databases["premile"].Rules);

        Exception? blad = Record.Exception(() => ConfigLoader.Validate(TestPaths.ExampleRules, stanowisko));

        Assert.True(blad is null,
            "Podmiana rules.json na wzorcowy z 0.4.249 zatrzymuje start stanowiska, którego config.json "
            + "nie ma profili 'cameo1'/'premile' (każde istniejące stanowisko — instalator nie nadpisuje config.json): " + blad?.Message);
    }
}
