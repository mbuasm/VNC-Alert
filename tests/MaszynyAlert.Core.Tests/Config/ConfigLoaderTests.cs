using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ConfigLoaderTests
{
    private const string MinimalConfig = """
        {
          "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
          "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 } } }
        }
        """;

    [Fact]
    public void LoadAppConfig_PrzykladowaKonfiguracja_Ma12MaszynZUnikalnymiIdentyfikatorami()
    {
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        Assert.Equal(12, cfg.Machines.Count);
        Assert.Equal(12, cfg.Machines.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cfg.Machines, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
    }

    [Fact]
    public void LoadAppConfig_PrzykladowaKonfiguracja_Ma6ProfiliZIdentyfikatoremZKlucza()
    {
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        Assert.Equal(6, cfg.Profiles.Count);
        Assert.Equal(["aidite-light2", "aidite5x", "amm580", "cameo1", "opencnc", "premile"], cfg.Profiles.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach ((string key, ScreenProfile profile) in cfg.Profiles)
        {
            Assert.Equal(key, profile.Id);
            Assert.False(profile.StatusBarRoi.IsEmpty);
        }

        Assert.True(cfg.Profiles["amm580"].DarkTheme);
        Assert.False(cfg.Profiles["opencnc"].DarkTheme);
    }

    [Fact]
    public void LoadAppConfig_PrzykladowaKonfiguracja_KazdaMaszynaWskazujeIstniejacyProfilIDozwolonyPort()
    {
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        foreach (MachineConfig m in cfg.Machines)
        {
            Assert.True(cfg.Profiles.ContainsKey(m.Profile), $"Maszyna '{m.Id}' wskazuje nieznany profil '{m.Profile}'.");
            Assert.Contains(m.Port, new[] { 9100, 5900 });
        }
    }

    [Fact]
    public void Validate_PrzykladowaKonfiguracja_NieRzucaWyjatku()
    {
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);
        ConfigLoader.Validate(cfg);
    }

    [Fact]
    public void LoadRules_PrzykladowySlownik_MaConajmniej8RegulIPrzechodziWalidacje()
    {
        RuleSet rules = ConfigLoader.LoadRules(TestPaths.RulesExample);
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        RuleDatabase aidite = rules.Databases["opencnc"];

        Assert.True(aidite.Rules.Count >= 8, $"Oczekiwano co najmniej 8 reguł, jest {aidite.Rules.Count}.");
        Assert.Equal(aidite.Rules.Count, aidite.Rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(aidite.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.ShortText)));
        Assert.All(aidite.Rules, r => Assert.Contains(r.Category, new[] { "narzedzie", "tacka", "restart", "inny", Rule.IgnoredCategory }));
        Assert.Contains(aidite.Rules, r => r.Ignored);

        Assert.True(aidite.Codes.Count >= 100, $"Oczekiwano co najmniej 100 kodów alarmów, jest {aidite.Codes.Count}.");
        Assert.All(aidite.Codes, c => Assert.False(string.IsNullOrWhiteSpace(c.ShortText)));
        Assert.All(aidite.Codes, c => Assert.Contains(c.Category, new[] { "narzedzie", "tacka", "restart", "inny", Rule.IgnoredCategory }));
        Assert.Contains(aidite.Codes, c => c.Code == 1200);
        Assert.Contains(aidite.Codes, c => c.From == 700 && c.To == 719);

        ConfigLoader.Validate(rules, cfg);
    }

    [Fact]
    public void LoadRules_PrzykladowySlownik_KazdyTypMaszynMaWlasnaBazeBledow()
    {
        RuleSet rules = ConfigLoader.LoadRules(TestPaths.RulesExample);
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        foreach (string profile in new[] { "opencnc", "aidite5x", "amm580", "cameo1", "premile" })
        {
            Assert.True(rules.Databases.ContainsKey(profile), $"Brak bazy błędów dla profilu '{profile}'.");
        }

        foreach ((string key, RuleDatabase database) in rules.Databases)
        {
            Assert.True(cfg.Profiles.ContainsKey(key), $"Baza błędów '{key}' nie odpowiada żadnemu profilowi z config.json.");
            Assert.False(string.IsNullOrWhiteSpace(database.Name), $"Baza '{key}' nie ma nazwy dla Panelu.");
            Assert.All(database.Rules, r => Assert.Null(r.Profile));
        }

        // Listy wspólne zostają puste — służą wyłącznie zgodności ze starszym rules.json bez sekcji "databases".
        Assert.Empty(rules.Codes);
        Assert.Empty(rules.Rules);

        Assert.Equal(string.Empty, rules.Databases["opencnc"].CodeIdPrefix);
        Assert.Equal("aidite5x-", rules.Databases["aidite5x"].CodeIdPrefix);
        Assert.Equal("amm580-", rules.Databases["amm580"].CodeIdPrefix);
    }

    [Fact]
    public void ParseRules_SekcjaCodes_WczytujeNumeryZakresyIDomyslneWartosci()
    {
        const string json = """
            {
              // komentarz
              "codes": [
                { "code": 1200, "shortText": "Bazowanie (Home)", "category": "restart", "priority": 50, "description": "Request for zero return alarm" },
                { "from": 700, "to": 719, "shortText": "Servo {n}" },
              ],
              "rules": []
            }
            """;

        RuleSet rules = ConfigLoader.ParseRules(json);

        Assert.Equal(2, rules.Codes.Count);
        AlarmCode single = rules.Codes[0];
        Assert.Equal(1200, single.Code);
        Assert.False(single.IsRange);
        Assert.Equal("restart", single.Category);
        Assert.Equal(50, single.Priority);
        Assert.Equal("Request for zero return alarm", single.Description);
        AlarmCode range = rules.Codes[1];
        Assert.True(range.IsRange);
        Assert.Equal(700, range.From);
        Assert.Equal(719, range.To);
        Assert.Equal("inny", range.Category);
        Assert.Equal(0, range.Priority);
        Assert.Null(range.Description);
    }

    [Fact]
    public void ParseRules_StaryPlikBezSekcjiCodes_WczytujeSieZPustaListaKodow()
    {
        RuleSet rules = ConfigLoader.ParseRules("""{ "rules": [ { "id": "r1", "phrases": ["x"], "shortText": "X", "category": "inny" } ] }""");

        Assert.Empty(rules.Codes);
        Assert.Single(rules.Rules);
        ConfigLoader.Validate(rules, null);
    }

    [Fact]
    public void ParseAppConfig_ZKomentarzamiIPrzecinkiemKoncowym_WczytujeSiePoprawnie()
    {
        const string json = """
            {
              // komentarz jednoliniowy
              "enabled": true,
              /* komentarz
                 wieloliniowy */
              "machines": [
                { "id": "m1", "name": "M 1", "host": "10.0.0.1", "profile": "p1", },
              ],
              "profiles": {
                "p1": { "statusBarRoi": { "x": 1, "y": 2, "width": 3, "height": 4 }, },
              },
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        Assert.Single(cfg.Machines);
        Assert.Equal("m1", cfg.Machines[0].Id);
        Assert.Equal(new RectInt(1, 2, 3, 4), cfg.Profiles["p1"].StatusBarRoi);
    }

    [Fact]
    public void ParseAppConfig_TrybPolaczeniaJakoTekst_MapujeSieNaEnum()
    {
        const string json = """
            {
              "machines": [
                { "id": "m1", "name": "M 1", "host": "h", "profile": "p1", "mode": "polling" },
                { "id": "m2", "name": "M 2", "host": "h", "profile": "p1", "mode": "persistent" }
              ],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 10, "height": 10 } } }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        Assert.Equal(ConnectionMode.Polling, cfg.Machines[0].Mode);
        Assert.Equal(ConnectionMode.Persistent, cfg.Machines[1].Mode);
    }

    [Fact]
    public void ParseAppConfig_NieznanePole_JestIgnorowane()
    {
        const string json = """
            {
              "nieznanePole": 123,
              "machines": [ { "id": "m1", "name": "M 1", "host": "h", "profile": "p1", "cosDziwnego": true } ],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 10, "height": 10 }, "xyz": "abc" } }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        Assert.Single(cfg.Machines);
        Assert.Equal("m1", cfg.Machines[0].Id);
    }

    [Fact]
    public void ParseAppConfig_ZlaSkladniaJson_RzucaConfigException()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.ParseAppConfig("{ to nie jest json"));
    }

    [Fact]
    public void ParseAppConfig_PustegoDokumentu_RzucaConfigException()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.ParseAppConfig("null"));
    }

    [Fact]
    public void SerializeIParse_AppConfig_ZachowujaWartosciIProstokatJakoObiekt()
    {
        AppConfig original = ConfigLoader.ParseAppConfig(MinimalConfig) with { RulesFile = "moje-reguly.json" };

        string json = ConfigLoader.Serialize(original);
        AppConfig roundTripped = ConfigLoader.ParseAppConfig(json);

        Assert.Contains("\"statusBarRoi\"", json, StringComparison.Ordinal);
        Assert.Contains("\"width\": 100", json, StringComparison.Ordinal);
        Assert.Equal("moje-reguly.json", roundTripped.RulesFile);
        Assert.Equal(original.Machines[0].Id, roundTripped.Machines[0].Id);
        Assert.Equal(original.Machines[0].Port, roundTripped.Machines[0].Port);
        Assert.Equal(new RectInt(0, 0, 100, 20), roundTripped.Profiles["p1"].StatusBarRoi);
        Assert.Equal(original.Timing.PollIntervalMs, roundTripped.Timing.PollIntervalMs);
        Assert.Equal(original.Cluster.UdpPort, roundTripped.Cluster.UdpPort);
    }

    [Fact]
    public void SerializeIParse_PelnaPrzykladowaKonfiguracja_ZachowujeWszystkieMaszynyIProfile()
    {
        AppConfig original = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        AppConfig roundTripped = ConfigLoader.ParseAppConfig(ConfigLoader.Serialize(original));

        Assert.Equal(original.Machines.Count, roundTripped.Machines.Count);
        Assert.Equal(original.Machines.Select(m => m.Id), roundTripped.Machines.Select(m => m.Id));
        Assert.Equal(original.Profiles.Count, roundTripped.Profiles.Count);
        Assert.Equal(
            original.Profiles["amm580"].StatusBarRoi,
            roundTripped.Profiles["amm580"].StatusBarRoi);
        Assert.Equal(original.Timing.ReconnectBackoffMs, roundTripped.Timing.ReconnectBackoffMs);
        ConfigLoader.Validate(roundTripped);
    }

    [Fact]
    public void SerializeIParse_RuleSet_ZachowujeRegulyIBazyBledow()
    {
        // Zapis i ponowny odczyt to droga, którą słownik błędów jedzie do pozostałych komputerów grupy
        // (profil grupy niesie cały RuleSet), więc bazy błędów po typie maszyn muszą ją przeżyć w całości:
        // reguły, kody, przedrostek numerów i zapis numeru alarmu.
        RuleSet original = ConfigLoader.LoadRules(TestPaths.RulesExample);

        RuleSet roundTripped = ConfigLoader.ParseRules(ConfigLoader.Serialize(original));

        Assert.Equal(original.Rules.Count, roundTripped.Rules.Count);
        Assert.Equal(original.Rules.Select(r => r.Id), roundTripped.Rules.Select(r => r.Id));
        Assert.Equal(original.Databases.Keys.OrderBy(k => k, StringComparer.Ordinal), roundTripped.Databases.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach ((string key, RuleDatabase database) in original.Databases)
        {
            RuleDatabase after = roundTripped.Databases[key];
            Assert.Equal(database.Rules.Select(r => r.Id), after.Rules.Select(r => r.Id));
            Assert.Equal(database.Codes.Count, after.Codes.Count);
            Assert.Equal(database.CodeIdPrefix, after.CodeIdPrefix);
            Assert.Equal(database.CodePattern, after.CodePattern);
            Assert.Equal(database.Name, after.Name);
        }

        RuleDatabase aidite = roundTripped.Databases[TestPaths.AiditeProfile];
        Assert.Equal(original.Databases[TestPaths.AiditeProfile].Rules[0].Phrases, aidite.Rules[0].Phrases);
    }

    [Fact]
    public void ParseRules_ZKomentarzami_WczytujeReguly()
    {
        const string json = """
            {
              // słownik testowy
              "rules": [
                { "id": "r1", "pattern": "abc", "shortText": "Test", "category": "inny", },
              ],
            }
            """;

        RuleSet rules = ConfigLoader.ParseRules(json);

        Assert.Single(rules.Rules);
        Assert.Equal("r1", rules.Rules[0].Id);
        Assert.Equal(2, rules.Rules[0].MaxDistance);
    }

    [Fact]
    public void LoadAppConfig_GdyPlikuNieMa_RzucaConfigExceptionZPolskimKomunikatem()
    {
        using var dir = new TempDir("cfg");
        string path = dir.File("nie-ma-config.json");

        // Zachowanie utrwalone: FileNotFoundException dziedziczy po IOException, więc loader zamienia go
        // na ConfigException z komunikatem zrozumiałym dla użytkownika.
        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadAppConfig(path));
        Assert.Contains("Nie można odczytać pliku konfiguracyjnego", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRules_GdyPlikuNieMa_RzucaConfigException()
    {
        using var dir = new TempDir("rules");
        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadRules(dir.File("brak.json")));
        Assert.Contains("Nie można odczytać pliku reguł", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAppConfig_BezPolaOcrRetryIntervalMs_UzywaDomyslnychDwochSekund()
    {
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "h", "profile": "p1" } ],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 10, "height": 10 } } },
              "timing": { "ocrMaxAttempts": 3 }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        Assert.Equal(2000, cfg.Timing.OcrRetryIntervalMs);
        Assert.Equal(2000, new TimingConfig().OcrRetryIntervalMs);
    }

    [Fact]
    public void ParseAppConfig_ZPolemOcrRetryIntervalMs_WczytujeJegoWartosc()
    {
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "h", "profile": "p1" } ],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 10, "height": 10 } } },
              "timing": { "ocrRetryIntervalMs": 5000 }
            }
            """;

        Assert.Equal(5000, ConfigLoader.ParseAppConfig(json).Timing.OcrRetryIntervalMs);
        // Przykładowa konfiguracja dostarczana z programem też musi to pole mieć opisane.
        Assert.Equal(2000, TestPaths.ExampleConfig.Timing.OcrRetryIntervalMs);
    }

    [Fact]
    public void ParseRules_RegulyBezPolaPriority_DajaPriorytetZero()
    {
        // Dlaczego: priorytet jest opcjonalny — stary rules.json (i wszystkie reguły spoza cyklu servo/makr)
        // ma dalej działać jak dotąd: reguły równe, wygrywa ta zobaczona w epizodzie pierwsza.
        const string json = """
            {
              "rules": [
                { "id": "bez", "pattern": "ALARM", "shortText": "Alarm", "category": "inny" },
                { "id": "z", "pattern": "SERVO", "shortText": "Serwo", "category": "restart", "priority": 90 }
              ]
            }
            """;

        RuleSet rules = ConfigLoader.ParseRules(json);

        Assert.Equal(0, rules.Rules.Single(r => r.Id == "bez").Priority);
        Assert.Equal(90, rules.Rules.Single(r => r.Id == "z").Priority);
    }
}
