using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class RuleDatabaseTests
{
    private const string Aidite = "opencnc";
    private const string Aidite5X = "aidite5x";
    private const string Amm580 = "amm580";

    private const string Komunikat580 = "{T1} Tool life was reached. Please change the tool.";

    private const string Komunikat520 =
        "[1099] MO23.02 Milling fluid is abnormal and the milling has been suspended. Add the milling fluid, " +
        "and then click the AUTO START button to continue milling.";

    private const string Komunikat520Ocr =
        "HOLD ING [1699] M023. 62 Mil ling fluid is abnormal and the mill ing has been suspended, Add";

    private static MessageMapper ExampleMapper() => new(TestPaths.ExampleRules);

    [Fact]
    public void Map_KomunikatuZuzytegoNarzedzia580_DajeOpisZBazy580_ANieOtwartychDrzwiZListyAidite()
    {
        MappedMessage m = ExampleMapper().Map(Komunikat580, Amm580);

        Assert.True(m.Matched, $"Komunikat 580 nie trafił w żadną regułę jej bazy (wyszło: {m.ShortText}).");
        Assert.Equal("amm580-tool-life", m.RuleId);
        Assert.Equal("bez alarmu", m.Category);
        Assert.True(m.Suppressed);
        Assert.True(m.Ignored);
        Assert.DoesNotContain("drzwi", m.ShortText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_KomunikatuZuzytegoNarzedzia580_WBazieAidite_NieUdajeZadnegoBleduZTamtejListy()
    {
        MappedMessage m = ExampleMapper().Map(Komunikat580, Aidite);

        Assert.False(m.Matched, $"Komunikat 580 trafił w regułę Aidite \"{m.RuleId}\" ({m.ShortText}).");
        Assert.Contains("Tool life", m.RawText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Komunikat520)]
    [InlineData(Komunikat520Ocr)]
    public void Map_KomunikatuPlynuObrobkowego520_DajeOpisZBazy520(string tekst)
    {
        MappedMessage m = ExampleMapper().Map(tekst, Aidite5X);

        Assert.True(m.Matched, $"Komunikat 520 nie trafił w regułę jej bazy (wyszło: {m.ShortText}).");
        Assert.Equal("aidite5x-milling-fluid", m.RuleId);
    }

    [Fact]
    public void Map_NumeruAlarmuAidite_NaMaszynie580_NieBierzeOpisuZListyAidite()
    {
        MessageMapper mapper = ExampleMapper();

        MappedMessage aidite = mapper.Map("No.705 [N]Axis 6 alarm", Aidite);
        MappedMessage obca = mapper.Map("No.705 [N]Axis 6 alarm", Amm580);

        Assert.Equal("Servo 6", aidite.ShortText);
        Assert.Equal("rule:code-705", aidite.Discriminator);

        Assert.False(obca.Matched);
        Assert.Contains("705", obca.ShortText, StringComparison.Ordinal);
        Assert.Equal("alarm:amm580-705", obca.Discriminator);
        Assert.NotEqual(aidite.Discriminator, obca.Discriminator);
    }

    [Fact]
    public void Map_TegoSamegoNumeruWDwochBazach_DajeDwaOpisyIDwaKlucze()
    {
        // Dlaczego osobny klucz: z niego powstaje klucz alertu ORAZ klucz zapisanej instrukcji naprawy.
        // Wspólny klucz oznaczałby, że instrukcja napisana dla jednej maszyny wyświetla się przy błędzie
        // zupełnie innej maszyny.
        var rules = new RuleSet
        {
            Databases =
            {
                ["opencnc"] = new RuleDatabase
                {
                    CodeIdPrefix = "",
                    Codes = [new AlarmCode { Code = 705, ShortText = "Servo 6", Category = "restart" }],
                },
                ["amm580"] = new RuleDatabase
                {
                    CodeIdPrefix = "amm580-",
                    Codes = [new AlarmCode { Code = 705, ShortText = "Zupełnie inny błąd", Category = "inny" }],
                },
            },
        };
        var mapper = new MessageMapper(rules);

        MappedMessage a = mapper.Map("No.705", "opencnc");
        MappedMessage b = mapper.Map("No.705", "amm580");

        Assert.Equal("Servo 6", a.ShortText);
        Assert.Equal("rule:code-705", a.Discriminator);
        Assert.Equal("Zupełnie inny błąd", b.ShortText);
        Assert.Equal("rule:amm580-code-705", b.Discriminator);
    }

    [Fact]
    public void Map_MaszynyZWlasnaBaza_NieWidziListWspolnych()
    {
        var rules = new RuleSet
        {
            Rules = [new Rule { Id = "wspolna", Phrases = ["alarm"], ShortText = "Wspólna", Category = "inny" }],
            Databases =
            {
                ["amm580"] = new RuleDatabase { CodeIdPrefix = "amm580-" },
            },
        };
        var mapper = new MessageMapper(rules);

        Assert.Equal("wspolna", mapper.Map("ALARM", "opencnc").RuleId);
        Assert.False(mapper.Map("ALARM", "amm580").Matched);
    }

    [Fact]
    public void Map_StaregoPlikuBezSekcjiDatabases_DzialaJakPrzed19092026()
    {
        // Instalator NIE nadpisuje rules.json, więc na stanowisku może zostać plik w starym układzie:
        // ma dalej działać dokładnie tak samo dla każdej maszyny.
        RuleSet rules = ConfigLoader.ParseRules("""
            {
              "codes": [ { "code": 705, "shortText": "Servo 6", "category": "restart" } ],
              "rules": [ { "id": "door", "phrases": ["door"], "shortText": "Otwarte drzwi", "category": "inny" } ]
            }
            """);
        var mapper = new MessageMapper(rules);

        Assert.Equal("Servo 6", mapper.Map("No.705", "opencnc").ShortText);
        Assert.Equal("Servo 6", mapper.Map("No.705", "amm580").ShortText);
        Assert.Equal("door", mapper.Map("Door open", "amm580").RuleId);
    }

    [Fact]
    public void Map_WlasnegoZapisuNumeruAlarmu_CzytaNumerWNawiasachKwadratowych()
    {
        var rules = new RuleSet
        {
            Databases =
            {
                ["aidite5x"] = new RuleDatabase
                {
                    CodeIdPrefix = "aidite5x-",
                    CodePattern = @"\[\s*([0-9OIL]{2,4})\s*\]",
                    Codes = [new AlarmCode { Code = 1099, ShortText = "Płyn obróbkowy", Category = "inny" }],
                },
            },
        };
        var mapper = new MessageMapper(rules);

        MappedMessage m = mapper.Map("[1099] MO23.02 Milling fluid is abnormal", "aidite5x");

        Assert.Equal("Płyn obróbkowy", m.ShortText);
        Assert.Equal("rule:aidite5x-code-1099", m.Discriminator);
        Assert.Equal("1099", m.AlarmNumber);
    }

    [Fact]
    public void Map_WlasnegoZapisuNumeruAlarmu_NieSzukaJuzZapisuAidite()
    {
        // Baza z własnym zapisem numeru nie może przypadkiem łapać cudzej konwencji — inaczej „No.705”
        // w cudzysłowie z innego pola ekranu stałoby się numerem alarmu tej maszyny.
        var rules = new RuleSet
        {
            Databases = { ["aidite5x"] = new RuleDatabase { CodePattern = @"\[\s*([0-9OIL]{2,4})\s*\]" } },
        };

        MappedMessage m = new MessageMapper(rules).Map("No.705 [N]Axis 6 servo alarm", "aidite5x");

        Assert.Null(m.AlarmNumber);
    }

    [Fact]
    public void MessageMapper_NiepoprawnegoZapisuNumeru_NieWysadzaProgramuTylkoOstrzega()
    {
        var rules = new RuleSet
        {
            Databases = { ["amm580"] = new RuleDatabase { CodePattern = "(niedomkniety" } },
        };

        var mapper = new MessageMapper(rules);

        Assert.Contains(mapper.Warnings, w => w.Contains("amm580", StringComparison.Ordinal));
        Assert.False(mapper.Map("cokolwiek", "amm580").Matched);
    }

    [Fact]
    public void Validate_BazyDlaNieistniejacegoProfilu_NieRzuca_TylkoOstrzegaZNazwaBazy()
    {
        AppConfig cfg = TestPaths.ExampleConfig;
        var rules = new RuleSet { Databases = { ["nie-ma-takiego"] = new RuleDatabase() } };

        ConfigLoader.Validate(rules, cfg);

        string warning = Assert.Single(ConfigLoader.Warnings(rules, cfg));
        Assert.Contains("'nie-ma-takiego'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_PowtorzonegoIdReguyMiedzyBazami_RzucaConfigException()
    {
        var rules = new RuleSet
        {
            Databases =
            {
                ["opencnc"] = new RuleDatabase { Rules = [new Rule { Id = "dubel", ShortText = "A", Category = "inny" }] },
                ["amm580"] = new RuleDatabase { Rules = [new Rule { Id = "dubel", ShortText = "B", Category = "inny" }] },
            },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
        Assert.Contains("dubel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DwochBazZKodamiBezPrzedrostka_RzucaConfigException()
    {
        var rules = new RuleSet
        {
            Databases =
            {
                ["opencnc"] = new RuleDatabase { Codes = [new AlarmCode { Code = 705, ShortText = "A" }] },
                ["amm580"] = new RuleDatabase { Codes = [new AlarmCode { Code = 705, ShortText = "B" }] },
            },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
        Assert.Contains("codeIdPrefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ReguyZPolemProfileWewnatrzBazy_RzucaConfigException()
    {
        var rules = new RuleSet
        {
            Databases =
            {
                ["amm580"] = new RuleDatabase
                {
                    Rules = [new Rule { Id = "r1", ShortText = "A", Category = "inny", Profile = "opencnc" }],
                },
            },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
        Assert.Contains("profile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TychSamychNumerowWDwochBazach_JestDozwolone()
    {
        // Numery powtarzają się MIĘDZY producentami i to jest normalne — zakaz nakładania się dotyczy
        // tylko wnętrza jednej bazy.
        var rules = new RuleSet
        {
            Databases =
            {
                ["opencnc"] = new RuleDatabase { Codes = [new AlarmCode { Code = 705, ShortText = "Servo 6" }] },
                ["amm580"] = new RuleDatabase
                {
                    CodeIdPrefix = "amm580-",
                    Codes = [new AlarmCode { Code = 705, ShortText = "Inny błąd" }],
                },
            },
        };

        ConfigLoader.Validate(rules, null);
    }

    [Theory]
    [InlineData("""{ "databases": null }""")]
    [InlineData("""{ "codes": null }""")]
    [InlineData("""{ "rules": null }""")]
    [InlineData("""{ "databases": { "amm580": { "codeIdPrefix": "amm580-", "codes": null } } }""")]
    [InlineData("""{ "databases": { "amm580": { "codeIdPrefix": "amm580-", "rules": null } } }""")]
    public void Validate_SekcjiONullu_RzucaConfigException_ANieNullReference(string json)
    {
        RuleSet rules = ConfigLoader.ParseRules(json);

        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
    }

    [Fact]
    public void Map_WlasnegoZapisuNumeru_ZAlternatywaDwochGrup_BierzeGrupeKtoraTrafila()
    {
        // Wzorzec z dwiema gałęziami: grupa 1 ISTNIEJE zawsze, ale jest pusta, gdy trafiła druga gałąź.
        var rules = new RuleSet
        {
            Databases =
            {
                ["amm580"] = new RuleDatabase
                {
                    CodeIdPrefix = "amm580-",
                    CodePattern = @"\[\s*([0-9]{2,4})\s*\]|ERR\s*([0-9]{2,4})",
                    Codes = [new AlarmCode { Code = 321, ShortText = "Test", Category = "inny" }],
                },
            },
        };
        var mapper = new MessageMapper(rules);

        Assert.Equal("321", mapper.Map("ERR 321 something", "amm580").AlarmNumber);
        Assert.Equal("321", mapper.Map("[321] something", "amm580").AlarmNumber);
    }
}
