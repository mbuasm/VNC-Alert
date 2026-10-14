using System.Text.Json;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerTestKonfiguracjaTests
{
    private const string Falownik520 = "HOLDING [1240] Spindle inverter alarm";

    private static readonly JsonDocumentOptions KomentarzeIPrzecinki = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Wzorcowy <c>rules.json</c> jako drzewo JSON (do przerobienia na wariant brzegowy).</summary>
    private static JsonObject WzorcowyRulesJson() =>
        JsonNode.Parse(File.ReadAllText(TestPaths.RulesExample), documentOptions: KomentarzeIPrzecinki)!.AsObject();

    private static AppConfig Z520Wlaczona() => TestPaths.ExampleConfig with
    {
        Machines = [.. TestPaths.ExampleConfig.Machines.Select(m => m.Id == "amm520-1" ? m with { Enabled = true } : m)],
    };

    private static AppConfig StanowiskoSprzed0_4_249()
    {
        AppConfig przyklad = TestPaths.ExampleConfig;
        return przyklad with
        {
            Profiles = przyklad.Profiles.Where(p => p.Key is not ("cameo1" or "premile"))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Machines = [.. przyklad.Machines.Select(m => m.Id is "cameo1" or "premile"
                ? m with { Profile = "aidite-light2", RequiresPassword = true }
                : m)],
        };
    }

    private static Alert AlertZ(string machineId, MappedMessage mapped) => new()
    {
        Key = new AlertKey(machineId, mapped.Discriminator),
        MachineId = machineId,
        MachineName = machineId,
        Kind = AlertKind.Error,
        ShortText = mapped.ShortText,
        Category = mapped.Category,
    };

    // =====================================================================================================================
    // 1. Wielkość liter nazwy bazy (słownik z pliku)
    // =====================================================================================================================

    /// <summary>
    /// Fakt wyjściowy: słownik baz po wczytaniu z tekstu JSON rozróżnia wielkość liter (deserializator tworzy nowy słownik
    /// z domyślnym komparatorem — inicjalizator <c>OrdinalIgnoreCase</c> z <see cref="RuleSet.Databases"/> nie obowiązuje).
    /// </summary>
    [Fact]
    public void TK00_SlownikBazZPliku_RozrozniaWielkoscLiter()
    {
        RuleSet rules = ConfigLoader.ParseRules("""{ "databases": { "aidite5x": { "codeIdPrefix": "aidite5x-" } } }""");

        Assert.True(rules.Databases.ContainsKey("aidite5x"));
        Assert.False(rules.Databases.ContainsKey("AIDITE5X"));

        // Inicjalizator obiektu (tak budują słownik testy BazaRoznaOdProfiluTylkoWielkosciaLiter_… i Validate_BazyDla…) — bez wielkości liter.
        var zKodu = new RuleSet { Databases = { ["aidite5x"] = new RuleDatabase() } };
        Assert.True(zKodu.Databases.ContainsKey("AIDITE5X"));
    }

    [Fact]
    public void TK01_BazaRoznaOdProfiluTylkoWielkosciaLiter_ZPliku_RaportKlasyfikujeTakJakAlertAlboJestOstrzezenie()
    {
        JsonObject json = WzorcowyRulesJson();
        JsonObject bazy = json["databases"]!.AsObject();
        JsonNode baza520 = bazy["aidite5x"]!.DeepClone();
        bazy.Remove("aidite5x");
        bazy["AIDITE5X"] = baza520;
        RuleSet rules = ConfigLoader.ParseRules(json.ToJsonString());
        AppConfig cfg = Z520Wlaczona();

        ConfigLoader.Validate(rules, cfg);
        IReadOnlyList<string> ostrzezenia = ConfigLoader.Warnings(rules, cfg);

        MappedMessage alert = new MessageMapper(rules).Map(Falownik520, "aidite5x");
        Assert.Equal("aidite5x-spindle-inverter", alert.RuleId);
        Assert.Equal("restart", alert.Category);

        string rodzajWRaporcie = new DowntimeCategoryClassifier(rules, cfg.Machines).Classify("amm520-1", alert.ShortText);
        bool ostrzezenieOBazie = ostrzezenia.Any(w => w.Contains("'AIDITE5X'", StringComparison.Ordinal));

        Assert.True(rodzajWRaporcie == alert.Category || ostrzezenieOBazie,
            $"Alert: „{alert.ShortText}” = {alert.Category} (baza 'AIDITE5X'); raport postojów: {rodzajWRaporcie}; " +
            $"ostrzeżeń słownika: {ostrzezenia.Count}. Baza różna od profilu wielkością liter jest używana przez alerty, a nie przez " +
            "klasyfikator raportu — i nikt o tym nie ostrzega (do 0.4.249 taki plik zatrzymywał start).");
    }

    [Fact]
    public void TK02_DwieBazyRozneTylkoWielkosciaLiter_ZatrzymujaStart_ABezWalidacjiAlertIRaportBioraTeSamaBaze()
    {
        JsonObject json = WzorcowyRulesJson();
        JsonObject bazy = json["databases"]!.AsObject();
        JsonNode prawdziwa = bazy["aidite5x"]!.DeepClone();
        bazy.Remove("aidite5x");
        bazy["AIDITE5X"] = prawdziwa;
        bazy["aidite5x"] = new JsonObject { ["codeIdPrefix"] = "aidite5x-kopia-", ["codes"] = new JsonArray(), ["rules"] = new JsonArray() };
        RuleSet rules = ConfigLoader.ParseRules(json.ToJsonString());
        AppConfig cfg = Z520Wlaczona();

        Assert.True(rules.Databases.ContainsKey("AIDITE5X") && rules.Databases.ContainsKey("aidite5x"));
        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, cfg));
        Assert.Contains("Bazy błędów 'AIDITE5X' i 'aidite5x' w rules.json różnią się tylko wielkością liter", ex.Message,
            StringComparison.Ordinal);

        var mapper = new MessageMapper(rules);
        Assert.Contains(mapper.Warnings, w => w.Contains("\"aidite5x\" powtarza się", StringComparison.Ordinal));
        MappedMessage alert = mapper.Map(Falownik520, "aidite5x");
        Assert.Equal("aidite5x-spindle-inverter", alert.RuleId); // pierwsza baza w pliku
        Assert.Equal(alert.Category,
            new DowntimeCategoryClassifier(rules, cfg.Machines).Classify("amm520-1", alert.ShortText)); // raport — ta sama baza
    }

    // =====================================================================================================================
    // 2. Złagodzenie jest wąskie — inne błędy budowy bazy bez profilu nadal zatrzymują start
    // =====================================================================================================================

    /// <summary>
    /// Uzupełnienie theory <c>BazaBezProfilu_ZBlednaBudowa_NadalZatrzymujeStart</c>: pozostałe kontrole budowy
    /// obowiązują także bazę, której nazwa nie jest profilem (<c>literowka</c>).
    /// </summary>
    [Theory]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "rules": [ { "id": "r1", "profile": "aidite5x", "phrases": [], "shortText": "A", "category": "inny" } ] } } }""", "ma pole 'profile'", true)]
    [InlineData("""{ "databases": { "amm580": { "codeIdPrefix": "amm580-", "rules": [ { "id": "r1", "phrases": [], "shortText": "A", "category": "inny" } ] }, "literowka": { "codeIdPrefix": "literowka-", "rules": [ { "id": "r1", "phrases": [], "shortText": "B", "category": "inny" } ] } } }""", "'r1' powtarza się", false)]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "rules": [ { "id": "r1", "pattern": "(niedomkniety", "phrases": [], "shortText": "A", "category": "inny" } ] } } }""", "w polu 'pattern'", false)]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "codes": [ { "from": 700, "to": 719, "shortText": "A" }, { "code": 705, "shortText": "B" } ] } } }""", "nakładają się", true)]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "codes": [ { "shortText": "A" } ] } } }""", "nie ma numeru", true)]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "codes": [ { "code": 1, "shortText": " " } ] } } }""", "pusty opis", true)]
    [InlineData("""{ "databases": { "opencnc": { }, "literowka": { } } }""", "nie ma własnego przedrostka", true)]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "rules": [ null ] } } }""", "element null", true)]
    [InlineData("""{ "databases": { " ": { "codeIdPrefix": "spacja-" } } }""", "pusty klucz", false)]
    public void TK03_BazaBezProfilu_InneBledyBudowy_NadalZatrzymujaStart(string json, string fragment, bool zNazwaBazy)
    {
        RuleSet rules = ConfigLoader.ParseRules(json);

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, TestPaths.ExampleConfig));

        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
        if (zNazwaBazy) Assert.Contains("literowka", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TK04_RegulaListyWspolnejZProfilemSpozaConfigJson_NadalZatrzymujeStart()
    {
        RuleSet rules = ConfigLoader.ParseRules(
            """{ "rules": [ { "id": "r1", "profile": "premile", "phrases": ["abc"], "shortText": "A", "category": "inny" } ] }""");

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, StanowiskoSprzed0_4_249()));

        Assert.Contains("wskazuje profil 'premile'", ex.Message, StringComparison.Ordinal);
    }

    // =====================================================================================================================
    // 3. Ścieżka grupy: zapisana generacja (start z grupą) i plik grupy
    // =====================================================================================================================

    [Fact]
    public void TK05_ZapisanaGeneracjaGrupyZBazamiBezProfilu_ReadForStartupPrzyjmuje_OstrzezeniaDlaSlownikaGrupy()
    {
        GroupProfile profil = GroupProfile.Capture(StanowiskoSprzed0_4_249(), TestPaths.ExampleRules, new Dictionary<string, string>());
        GroupFile zaproszenie = GroupFile.Create("Hala", profil);

        using var dir = new TempDir("wer-tk-grupa");
        var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());
        Guid id = store.Stage(new GroupSettingsProposal(GroupFileCodec.Parse(GroupFileCodec.Serialize(zaproszenie)),
            new ClusterConfig { Priority = 57 }, enabled: true), expectedCurrent: null);
        store.Commit(id);
        GroupSettingsRecovery recovery = new GroupSettingsStore(dir.Path, new InMemorySecretStore()).ReadForStartup();

        GroupSettingsData data = Assert.IsType<GroupSettingsData>(recovery.Data);
        Assert.Equal(zaproszenie.Identity.ProfileHash, data.Cluster.Group!.ProfileHash);
        AppConfig lokalny = TestPaths.ExampleConfig; // lokalny config.json bez znaczenia — profil grupy go zastępuje
        AppConfig zGrupa = data.ApplyTo(lokalny);
        ConfigLoader.Validate(zGrupa);
        ConfigLoader.Validate(data.Profile.Rules, zGrupa);
        Assert.Collection(ConfigLoader.Warnings(data.Profile.Rules, zGrupa),
            w => Assert.StartsWith("Baza błędów 'cameo1' ", w, StringComparison.Ordinal),
            w => Assert.StartsWith("Baza błędów 'premile' ", w, StringComparison.Ordinal));
    }

    /// <summary>
    /// Plik grupy z bazą różniącą się od profilu tylko wielkością liter przechodzi kontrolę <see cref="GroupFileCodec"/>
    /// (<c>RejectDuplicateProperties</c> porównuje klucze z wielkością liter).
    /// </summary>
    [Fact]
    public void TK06_PlikGrupyZBazaRoznaOdProfiluWielkosciaLiter_PrzechodziKontrole()
    {
        JsonObject json = WzorcowyRulesJson();
        JsonObject bazy = json["databases"]!.AsObject();
        JsonNode baza520 = bazy["aidite5x"]!.DeepClone();
        bazy.Remove("aidite5x");
        bazy["AIDITE5X"] = baza520;
        RuleSet rules = ConfigLoader.ParseRules(json.ToJsonString());

        GroupFile file = GroupFile.Create("Hala", GroupProfile.Capture(Z520Wlaczona(), rules, new Dictionary<string, string>()));
        GroupFile parsed = GroupFileCodec.Parse(GroupFileCodec.Serialize(file));

        Assert.True(parsed.Profile.Rules.Databases.ContainsKey("AIDITE5X"));
        Assert.Empty(ConfigLoader.Warnings(parsed.Profile.Rules, parsed.Profile.ApplyTo(new AppConfig())));
    }


    [Fact]
    public void TK07_PK02_ProfilePrzeniesioneBezBazPremileCameo1_NumerAlarmuDzieliKluczInstrukcjiZAidite_IBrakOstrzezenia()
    {
        JsonObject json = WzorcowyRulesJson();
        JsonObject bazy = json["databases"]!.AsObject();
        bazy.Remove("premile");
        bazy.Remove("cameo1");
        RuleSet dotychczasowy = ConfigLoader.ParseRules(json.ToJsonString());
        AppConfig poKrokach1Do4 = TestPaths.ExampleConfig;

        ConfigLoader.Validate(dotychczasowy, poKrokach1Do4);
        Assert.Empty(ConfigLoader.Warnings(dotychczasowy, poKrokach1Do4));
        Assert.Empty(new MessageMapper(dotychczasowy).Warnings);

        Alert premile = AlertZ("premile", new MessageMapper(dotychczasowy).Map("No.705", "premile"));
        Alert aidite = AlertZ("aidite1", new MessageMapper(dotychczasowy).Map("No.705", "opencnc"));
        Assert.Equal("alarm:705", premile.Key.Discriminator);
        Assert.Equal("rule:code-705", aidite.Key.Discriminator);
        Assert.Equal("code:705", AlarmHelp.InstructionKey(premile));
        Assert.Equal(AlarmHelp.InstructionKey(aidite), AlarmHelp.InstructionKey(premile));
        Assert.Equal(AlarmHelp.InstructionsFor(aidite), AlarmHelp.InstructionsFor(premile));

        Alert premileZBaza = AlertZ("premile", new MessageMapper(TestPaths.ExampleRules).Map("No.705", "premile"));
        Assert.Equal("code:premile-705", AlarmHelp.InstructionKey(premileZBaza));
        Assert.NotEqual(AlarmHelp.InstructionsFor(aidite), AlarmHelp.InstructionsFor(premileZBaza));
    }
}
