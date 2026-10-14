using System.Text.Json;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class NazwaBazyBledowWielkoscLiterTests
{
    private const string Falownik520 = "HOLDING [1240] Spindle inverter alarm";

    private static readonly JsonDocumentOptions KomentarzeIPrzecinki = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static JsonObject WzorcowyRulesJson() =>
        JsonNode.Parse(File.ReadAllText(TestPaths.RulesExample), documentOptions: KomentarzeIPrzecinki)!.AsObject();

    /// <summary>Wzorcowy rules.json z kluczem bazy <paramref name="baza"/> zapisanym jako <paramref name="nowaNazwa"/> (kolejność baz bez zmian).</summary>
    private static RuleSet ZNazwaBazy(string baza, string nowaNazwa)
    {
        JsonObject json = WzorcowyRulesJson();
        JsonObject bazy = json["databases"]!.AsObject();
        var poZmianie = new JsonObject();
        foreach ((string klucz, JsonNode? wartosc) in bazy.ToList())
        {
            bazy.Remove(klucz);
            poZmianie[klucz == baza ? nowaNazwa : klucz] = wartosc;
        }

        json["databases"] = poZmianie;
        return ConfigLoader.ParseRules(json.ToJsonString());
    }

    private static string[] OdczytyOcr(RuleDatabase baza) =>
    [
        .. baza.Codes.Select(c => $"No.{c.Code ?? c.From} [N]"),
        .. baza.Rules.SelectMany(r => r.Phrases),
        Falownik520,
        "EDIT READY M024. 01 Before using machi ne. AUTO USB PROTECT 2026.09.25 68",
        "{T1} Tool life was reached. Please change the tool.",
        "No.705",
        "No.1099 [N]",
        "",
    ];

    /// <summary>Opisy (<c>shortText</c>) wszystkich kodów i reguł WSZYSTKICH baz wzorca + teksty zastępcze — wejście klasyfikatora raportu.</summary>
    private static string[] OpisyWszystkichBaz() =>
    [
        .. TestPaths.ExampleRules.Databases.Values.SelectMany(b => b.Codes.Select(c => c.ShortText)),
        .. TestPaths.ExampleRules.Databases.Values.SelectMany(b => b.Rules.Select(r => r.ShortText)),
        "Błąd nr 705 (brak opisu w słowniku)",
        "Błąd: HOLDING [1240] Spindle inverter alarm",
        "Błąd (nie odczytano treści)",
        "Tekst spoza słownika",
    ];

    [Theory]
    [InlineData("opencnc", "OPENCNC")]
    [InlineData("aidite5x", "AIDITE5X")]
    [InlineData("aidite5x", "Aidite5X")]
    [InlineData("amm580", "AMM580")]
    [InlineData("premile", "Premile")]
    [InlineData("cameo1", "CAMEO1")]
    public void JednaBazaInnaWielkosciaLiterNizProfil_AlarmIRaportJakBazaProfilu_BezOstrzezenia(string baza, string nowaNazwa)
    {
        RuleSet wariant = ZNazwaBazy(baza, nowaNazwa);
        AppConfig cfg = TestPaths.ExampleConfig;
        Assert.True(wariant.Databases.ContainsKey(nowaNazwa));
        Assert.False(wariant.Databases.ContainsKey(baza)); // słownik z pliku rozróżnia wielkość liter

        ConfigLoader.Validate(wariant, cfg);
        Assert.Empty(ConfigLoader.Warnings(wariant, cfg));

        var alarmWzorcowy = new MessageMapper(TestPaths.ExampleRules);
        var alarm = new MessageMapper(wariant);
        Assert.Empty(alarm.Warnings);
        var raportWzorcowy = new DowntimeCategoryClassifier(TestPaths.ExampleRules, cfg.Machines);
        var raport = new DowntimeCategoryClassifier(wariant, cfg.Machines);

        MachineConfig[] maszyny = [.. cfg.Machines.Where(m => m.Profile == baza)];
        Assert.NotEmpty(maszyny);
        string[] odczyty = OdczytyOcr(TestPaths.ExampleRules.Databases[baza]);
        string[] opisy = OpisyWszystkichBaz();
        var rozne = new List<string>();
        int dopasowaneZBazy = 0;
        foreach (MachineConfig maszyna in maszyny)
        {
            if (alarm.HasSuppressRules(maszyna.Profile) != alarmWzorcowy.HasSuppressRules(maszyna.Profile))
                rozne.Add($"{maszyna.Id}: HasSuppressRules różne");

            foreach (string odczyt in odczyty)
            {
                MappedMessage oczekiwany = alarmWzorcowy.Map(odczyt, maszyna.Profile);
                MappedMessage wynik = alarm.Map(odczyt, maszyna.Profile);
                if (wynik != oczekiwany)
                    rozne.Add($"{maszyna.Id} alarm „{odczyt}”: {wynik.Discriminator} „{wynik.ShortText}” zamiast {oczekiwany.Discriminator} „{oczekiwany.ShortText}”");
                if (oczekiwany.Matched || oczekiwany.CodeNamespace.Length > 0) dopasowaneZBazy++;
            }

            foreach (string opis in opisy)
            {
                string rodzaj = raport.Classify(maszyna.Id, opis);
                string oczekiwanyRodzaj = raportWzorcowy.Classify(maszyna.Id, opis);
                if (rodzaj != oczekiwanyRodzaj) rozne.Add($"{maszyna.Id} raport „{opis}”: {rodzaj} zamiast {oczekiwanyRodzaj}");
            }
        }

        Assert.True(rozne.Count == 0, string.Join("\n", rozne.Take(20)));
        Assert.True(dopasowaneZBazy > 0, "Odczyty nie sięgają bazy profilu — porównanie nie rozróżniłoby bazy od list wspólnych.");
    }

    [Fact]
    public void Kontrola_BazaPodInnaNazwa_RaportIAlarmRozniaSieOdWzorca()
    {
        RuleSet literowka = ZNazwaBazy("aidite5x", "aidte5x");
        var raport = new DowntimeCategoryClassifier(literowka, TestPaths.ExampleConfig.Machines);

        Assert.Equal("restart",
            new DowntimeCategoryClassifier(TestPaths.ExampleRules, TestPaths.ExampleConfig.Machines).Classify("amm520-1", "Falownik wrzeciona — alarm"));
        Assert.Equal(DowntimeCategories.Unrecognized, raport.Classify("amm520-1", "Falownik wrzeciona — alarm"));
        Assert.NotEqual(new MessageMapper(TestPaths.ExampleRules).Map(Falownik520, "aidite5x"), new MessageMapper(literowka).Map(Falownik520, "aidite5x"));
    }

    /// <summary>
    /// Dwie bazy, których nazwy różnią się tylko wielkością liter, są niejednoznaczne: <see cref="ConfigLoader.Validate(RuleSet, AppConfig?)"/>
    /// rzuca <see cref="ConfigException"/> z nazwami obu baz i wskazówką — niezależnie od tego, czy któraś ma profil, od kolejności
    /// w pliku, od zawartości drugiej bazy (także <c>null</c>) i od tego, czy podano config.json.
    /// </summary>
    [Theory]
    [InlineData("""{ "databases": { "aidite5x": { "codeIdPrefix": "a-" }, "AIDITE5X": { "codeIdPrefix": "b-" } } }""", "aidite5x", "AIDITE5X")]
    [InlineData("""{ "databases": { "AMM580": { "codeIdPrefix": "a-" }, "amm580": { "codeIdPrefix": "b-" } } }""", "AMM580", "amm580")]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "a-" }, "LiteRowka": { "codeIdPrefix": "b-" } } }""", "literowka", "LiteRowka")]
    [InlineData("""{ "databases": { "opencnc": { }, "OpenCNC": null } }""", "opencnc", "OpenCNC")]
    public void DwieBazyRozneTylkoWielkosciaLiter_BladKonfiguracjiZNazwamiObu(string json, string pierwsza, string druga)
    {
        RuleSet rules = ConfigLoader.ParseRules(json);
        Assert.Equal(2, rules.Databases.Count);

        foreach (AppConfig? cfg in new[] { TestPaths.ExampleConfig, null })
        {
            ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, cfg));
            Assert.Contains($"Bazy błędów '{pierwsza}' i '{druga}' w rules.json różnią się tylko wielkością liter", ex.Message, StringComparison.Ordinal);
            Assert.Contains("nie wie, której z nich użyć", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Zostaw jedną", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Ten sam warunek obowiązuje profil grupy (kodek pliku grupy woła tę samą walidację): zaproszenie z dwiema bazami różnymi tylko
    /// wielkością liter nie powstaje. Jedna baza inną wielkością liter przechodzi.
    /// </summary>
    [Fact]
    public void ProfilGrupyZDwiemaBazamiRoznymiTylkoWielkosciaLiter_NiePrzechodziKontroli()
    {
        JsonObject json = WzorcowyRulesJson();
        json["databases"]!.AsObject()["AIDITE5X"] = new JsonObject { ["codeIdPrefix"] = "aidite5x-kopia-" };
        RuleSet rules = ConfigLoader.ParseRules(json.ToJsonString());
        GroupProfile profil = GroupProfile.Capture(TestPaths.ExampleConfig, rules, new Dictionary<string, string>());

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => GroupFile.Create("Hala", profil));

        Assert.Contains("profil nie przechodzą kontroli ustawień", ex.Message, StringComparison.Ordinal);
        GroupFile poprawny = GroupFile.Create("Hala", GroupProfile.Capture(TestPaths.ExampleConfig, ZNazwaBazy("aidite5x", "AIDITE5X"),
            new Dictionary<string, string>()));
        Assert.True(GroupFileCodec.Parse(GroupFileCodec.Serialize(poprawny)).Profile.Rules.Databases.ContainsKey("AIDITE5X"));
    }

    /// <summary>
    /// Słownik, który ominąłby walidację (np. zbudowany w kodzie), i tak nie rozjeżdża alarmu z raportem: klasyfikator dobiera bazę
    /// według zasad <see cref="MessageMapper"/> — pusty klucz i baza <c>null</c> pominięte, z nazw różnych wielkością liter pierwsza
    /// w pliku. Dla każdego wariantu rodzaj w raporcie = kategoria alertu tej samej maszyny.
    /// </summary>
    [Theory]
    [InlineData("pelna-pierwsza")]
    [InlineData("pusta-pierwsza")]
    [InlineData("null-pierwsza")]
    public void BezWalidacji_BazyRozneWielkosciaLiter_RaportBierzeTeSamaBazeCoAlarm(string wariant)
    {
        JsonObject json = WzorcowyRulesJson();
        JsonObject bazy = json["databases"]!.AsObject();
        JsonNode pelna = bazy["aidite5x"]!.DeepClone();
        bazy.Remove("aidite5x");
        JsonNode pusta = new JsonObject { ["codeIdPrefix"] = "aidite5x-kopia-" };
        if (wariant == "pelna-pierwsza")
        {
            bazy["AIDITE5X"] = pelna;
            bazy["aidite5x"] = pusta;
        }
        else if (wariant == "pusta-pierwsza")
        {
            bazy["AIDITE5X"] = pusta;
            bazy["aidite5x"] = pelna;
        }
        else
        {
            bazy["AIDITE5X"] = null;
            bazy["aidite5x"] = pelna;
        }

        RuleSet rules = ConfigLoader.ParseRules(json.ToJsonString());
        AppConfig cfg = TestPaths.ExampleConfig;

        var alarm = new MessageMapper(rules);
        var raport = new DowntimeCategoryClassifier(rules, cfg.Machines);
        MappedMessage falownik = alarm.Map(Falownik520, "aidite5x");

        Assert.Equal(falownik.Category, raport.Classify("amm520-1", falownik.ShortText));
        string oczekiwany = wariant == "pusta-pierwsza" ? DowntimeCategories.Unrecognized : "restart";
        Assert.Equal(oczekiwany, raport.Classify("amm520-1", "Falownik wrzeciona — alarm"));
        Assert.Equal(wariant == "pusta-pierwsza" ? null : "aidite5x-spindle-inverter", falownik.RuleId);
    }
}
