using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeCategoryClassifierTests
{
    private static DowntimeCategoryClassifier Example() =>
        new(TestPaths.ExampleRules, TestPaths.ExampleConfig.Machines);

    private static MachineConfig Machine(string id, string profile) => new()
    {
        Id = id, Name = id, Host = "127.0.0.1", Port = 5900, Profile = profile,
    };

    // --- Bazy typów maszyn z rules.example.json ---

    [Theory]
    [InlineData("aidite1", "Tacka nie pobrana", "tacka")]
    [InlineData("aidite1", "Servo 6", "restart")]
    [InlineData("aidite1", "Servo 6 niewłączone", "restart")]
    [InlineData("aidite1", "Zużyte narzędzie 3", "narzedzie")]
    [InlineData("aidite1", "Oś 2 – krańcówka (−)", "restart")]
    [InlineData("aidite1", "Otwarte drzwi", "inny")]
    [InlineData("amm520-1", "Falownik wrzeciona — alarm", "restart")]
    [InlineData("amm520-1", "Zużyte narzędzie — wymień narzędzie", "narzedzie")]
    [InlineData("amm580", "Magazyn narzędzi — błąd; zresetuj magazyn", "narzedzie")]
    public void TekstZeSlownikaTypuMaszyny_DajeKategorieWpisu(string machine, string reason, string expected)
    {
        Assert.Equal(expected, Example().Classify(machine, reason));
    }

    [Theory]
    [InlineData("amm580", "Tacka nie pobrana")]
    [InlineData("amm520-1", "Servo 6")]
    [InlineData("aidite1", "Falownik wrzeciona — alarm")]
    [InlineData("amm580", "Zużyte narzędzie — wymień narzędzie")]
    public void TekstZInnejBazy_Nierozpoznany(string machine, string reason)
    {
        Assert.Equal(DowntimeCategories.Unrecognized, Example().Classify(machine, reason));
    }

    [Theory]
    [InlineData("opencnc", "aidite1")]
    [InlineData("aidite5x", "amm520-1")]
    [InlineData("amm580", "amm580")]
    public void KazdaRegulaBazy_TekstWracaDoJejKategorii(string profile, string machine)
    {
        var classifier = Example();
        var database = TestPaths.ExampleRules.Databases[profile];
        var wrong = database.Rules
            .Where(r => !r.Ignored && !r.Suppressed)
            .Where(r => classifier.Classify(machine, r.ShortText) != r.Category)
            .Select(r => $"{r.Id}: „{r.ShortText}” → {classifier.Classify(machine, r.ShortText)} zamiast {r.Category}")
            .ToArray();
        Assert.True(wrong.Length == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void KazdyKodAidite_TekstZMessageMapperaWracaDoKategoriiZTejSamejObserwacji()
    {
        // Zgodność z rozpoznawaniem na żywo: dla każdego numeru z bazy opencnc (pojedyncze i każdy numer zakresu)
        // MessageMapper daje opis i kategorię alertu — klasyfikacja tego opisu ma dać tę samą kategorię.
        var mapper = new MessageMapper(TestPaths.ExampleRules);
        var classifier = Example();
        var numbers = TestPaths.ExampleAidite.Codes
            .SelectMany(c => c.IsRange ? Enumerable.Range(c.From!.Value, c.To!.Value - c.From.Value + 1) : [c.Code!.Value])
            .Distinct().ToArray();
        Assert.True(numbers.Length > 100);
        var wrong = new List<string>();
        foreach (int number in numbers)
        {
            var mapped = mapper.Map($"No.{number} [N]", TestPaths.AiditeProfile);
            if (mapped.Ignored) continue;
            string category = classifier.Classify("aidite1", mapped.ShortText);
            if (category != mapped.Category) wrong.Add($"No.{number} „{mapped.ShortText}”: {category} zamiast {mapped.Category}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    [Theory]
    [InlineData("amm580", "Zużyte narzędzie (Tool life) — bez alarmu")]
    [InlineData("aidite1", "Plan produkcji wykonany (żółty komunikat – nie błąd)")]
    [InlineData("amm520-1", "Przypomnienie przed pracą (M024.01) — bez alarmu")]
    public void TekstReguly_BezAlarmu_LubNieBlad_Nierozpoznany(string machine, string reason)
    {
        Assert.Equal(DowntimeCategories.Unrecognized, Example().Classify(machine, reason));
    }

    // --- Teksty zastępcze i specjalne ---

    [Theory]
    [InlineData("aidite1", "Błąd nr 1532 (brak opisu w słowniku)")]
    [InlineData("aidite1", "Błąd: Some unknown text")]
    [InlineData("aidite1", "Błąd (nie odczytano treści)")]
    [InlineData("nie-ma-takiej", "Błąd nr 1 (brak opisu w słowniku)")]
    [InlineData("nie-ma-takiej", "Błąd: x")]
    [InlineData("nie-ma-takiej", "Błąd (nie odczytano treści)")]
    public void TekstyZastepcze_Inny(string machine, string reason)
    {
        Assert.Equal(DowntimeCategories.Other, Example().Classify(machine, reason));
    }

    [Fact]
    public void TekstyZastepcze_TakieJakieDajeMessageMapper()
    {
        var mapper = new MessageMapper(TestPaths.ExampleRules);
        var classifier = Example();
        foreach (string raw in new[] { "No.1532 [N]", "", "Something readable but unknown here", "#@!" })
        {
            var mapped = mapper.Map(raw, TestPaths.AiditeProfile);
            Assert.Equal("inny", mapped.Category);
            Assert.Equal(DowntimeCategories.Other, classifier.Classify("aidite1", mapped.ShortText));
        }
    }

    [Fact]
    public void ProgramPrzerwany_Narzedzie()
    {
        Assert.Equal("narzedzie", Example().Classify("amm580", MachineMonitor.ProgramAbortShortText));
        Assert.Equal("narzedzie", Example().Classify("nie-ma-takiej", MachineMonitor.ProgramAbortShortText));
    }

    [Theory]
    [InlineData("nie-ma-takiej", "Tacka nie pobrana", "nierozpoznany")]
    [InlineData("aidite1", "", "nierozpoznany")]
    [InlineData("aidite1", "   ", "nierozpoznany")]
    [InlineData("aidite1", null, "nierozpoznany")]
    [InlineData("aidite1", "  Tacka nie pobrana  ", "tacka")]
    [InlineData("aidite1", "tacka nie pobrana", "nierozpoznany")]
    [InlineData("aidite1", "Servo", "nierozpoznany")]
    [InlineData("aidite1", "Servo x", "nierozpoznany")]
    [InlineData("aidite1", "Servo 6 extra", "nierozpoznany")]
    public void BrzegoweDaneWejsciowe(string machine, string? reason, string expected)
    {
        Assert.Equal(expected, Example().Classify(machine, reason));
    }

    [Fact]
    public void PustaKonfiguracjaIPusteWpisy_NieRzucaja()
    {
        var rules = new RuleSet
        {
            Codes = [new AlarmCode { Code = 1, ShortText = "  " }, new AlarmCode { From = 10, To = 5, ShortText = "Zły {n}" }],
            Rules = [new Rule { Id = "x", ShortText = "", Category = "inny" }],
        };
        var classifier = new DowntimeCategoryClassifier(rules, [null!, Machine("", "p"), Machine("m", "p")]);
        Assert.Equal(DowntimeCategories.Unrecognized, classifier.Classify("m", "Zły 3"));
        Assert.Equal(DowntimeCategories.Unrecognized, classifier.Classify(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => new DowntimeCategoryClassifier(null!, []));
        Assert.Throws<ArgumentNullException>(() => new DowntimeCategoryClassifier(rules, null!));
    }

    // --- Listy wspólne (profil bez własnej bazy) ---

    private static DowntimeCategoryClassifier Shared() => new(new RuleSet
    {
        Codes =
        [
            new AlarmCode { Code = 100, ShortText = "Wrzeciono {n} stop", Category = "restart" },
            new AlarmCode { Code = 705, ShortText = "Servo 6", Category = "narzedzie" },
            new AlarmCode { From = 700, To = 719, ShortText = "Servo {n}", Category = "restart" },
            new AlarmCode { From = 900, To = 905, ShortText = "Bez numeru", Category = "tacka" },
            new AlarmCode { From = 3300, To = 3307, ShortText = "Oś {n} – limit (−) [x]*", Category = "restart" },
            new AlarmCode { Code = 4534, ShortText = "Plan wykonany", Category = Rule.IgnoredCategory },
        ],
        Rules =
        [
            new Rule { Id = "glob", ShortText = "Wspólny tekst", Category = "inny" },
            new Rule { Id = "prof", ShortText = "Wspólny tekst", Category = "narzedzie", Profile = "P1" },
            new Rule { Id = "dup1", ShortText = "Podwójny", Category = "tacka" },
            new Rule { Id = "dup2", ShortText = "Podwójny", Category = "restart" },
        ],
    }, [Machine("m1", "p1"), Machine("m2", "p2")]);

    [Theory]
    [InlineData("m1", "Wrzeciono ? stop", "restart")]
    [InlineData("m1", "Servo 7", "restart")]
    [InlineData("m1", "Servo 25", "restart")] // świadome uproszczenie: numer pozycji spoza zakresu też pasuje
    [InlineData("m1", "Bez numeru", "tacka")]
    [InlineData("m1", "Oś 3 – limit (−) [x]*", "restart")]
    [InlineData("m1", "Oś 3 – limit (−) [x]x", "nierozpoznany")]
    [InlineData("m1", "Plan wykonany", "nierozpoznany")]
    public void ListyWspolne_KodyIZakresy(string machine, string reason, string expected)
    {
        Assert.Equal(expected, Shared().Classify(machine, reason));
    }

    [Fact]
    public void ListyWspolne_PojedynczyKodWygrywaZZakresemOTymSamymTekscie()
    {
        Assert.Equal("narzedzie", Shared().Classify("m1", "Servo 6"));
    }

    [Fact]
    public void ListyWspolne_RegulaProfiluPrzedGlobalna_ProfilBezWielkosciLiter()
    {
        var classifier = Shared();
        Assert.Equal("narzedzie", classifier.Classify("m1", "Wspólny tekst"));
        Assert.Equal("inny", classifier.Classify("m2", "Wspólny tekst"));
    }

    [Fact]
    public void ListyWspolne_DwaWpisyOTymSamymTekscie_PierwszyWKolejnosci()
    {
        Assert.Equal("tacka", Shared().Classify("m1", "Podwójny"));
    }

    // --- CategoryOf ---

    [Fact]
    public void CategoryOf_ZapisanaKategoriaWygrywaZTekstem()
    {
        var classifier = Example();
        var start = DateTimeOffset.Parse("2026-09-15T08:00:00Z");
        var episode = new DowntimeEpisode("aidite1", "Aidite 1", "Tacka nie pobrana", start, start.AddMinutes(5), null,
            EpisodeId: Guid.NewGuid(), Category: "restart");

        Assert.Equal("restart", classifier.CategoryOf(episode));
        Assert.Equal("tacka", classifier.CategoryOf(episode with { Category = null }));
        Assert.Equal("tacka", classifier.CategoryOf(episode with { Category = " " }));
        Assert.Throws<ArgumentNullException>(() => classifier.CategoryOf(null!));
    }

    [Fact]
    public void Classify_PowtorzenieDajeTenSamWynik()
    {
        var classifier = Example();
        Assert.Equal(classifier.Classify("aidite1", "Servo 6"), classifier.Classify("aidite1", " Servo 6 "));
        Assert.Equal("restart", classifier.Classify("aidite1", "Servo 6"));
    }
}
