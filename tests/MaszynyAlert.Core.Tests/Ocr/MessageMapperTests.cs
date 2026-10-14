using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class MessageMapperTests
{
    private static MessageMapper ExampleMapper() => new(TestPaths.ExampleRules);

    /// <summary>Dla każdej z 8 reguł przykładowego słownika: tekst dokładny i tekst „przekręcony” przez OCR.</summary>
    public static TheoryData<string, string, string, string> RuleSamples() => new()
    {
        { "tray-grab", "tacka", "Failed to grab a tray", "Faled to grab a trav" },
        { "tool-length", "narzedzie", "Tool length error", "T00l lenght" },
        { "tool-broken", "narzedzie", "Tool broken", "T00l br0ken" },
        { "spindle", "restart", "Spindle error", "Sp1ndle err0r" },
        { "emergency", "restart", "Emergency stop", "EMERGENCY!!!" },
        { "door", "inny", "Door open", "Dooor 0pen" },
        { "air-pressure", "inny", "Air pressure low", "A1r pressure l0w" },
        { "limit", "restart", "X axis limit", "0VER TRAVEL" },
    };

    [Theory]
    [MemberData(nameof(RuleSamples))]
    public void Map_TekstuDokladnegoIPrzekreconego_TrafiaWTeSamaRegule(
        string ruleId, string category, string exactText, string fuzzyText)
    {
        MessageMapper mapper = ExampleMapper();

        MappedMessage exact = mapper.MapAidite(exactText);
        MappedMessage fuzzy = mapper.MapAidite(fuzzyText);

        Assert.True(exact.Matched, $"Tekst dokładny \"{exactText}\" nie trafił w regułę (wyszło: {exact.ShortText}).");
        Assert.Equal(ruleId, exact.RuleId);
        Assert.Equal(category, exact.Category);

        Assert.True(fuzzy.Matched, $"Tekst przekręcony \"{fuzzyText}\" nie trafił w regułę (wyszło: {fuzzy.ShortText}).");
        Assert.Equal(ruleId, fuzzy.RuleId);
        Assert.Equal(exact.ShortText, fuzzy.ShortText);
        Assert.Equal($"rule:{ruleId}", fuzzy.Discriminator);
    }

    [Fact]
    public void Map_ZnanegoBleduTacki_ZwracaPolskiOpisIKategorie()
    {
        MappedMessage m = ExampleMapper().MapAidite("ERROR: Failed to grab a tray");

        Assert.True(m.Matched);
        Assert.Equal("tray-grab", m.RuleId);
        Assert.Equal("Tacka nie pobrana", m.ShortText);
        Assert.Equal("tacka", m.Category);
        Assert.Equal("ERROR: Failed to grab a tray", m.RawText);
        Assert.Equal("ERROR FAILED TO GRAB A TRAY", m.NormalizedText);
    }

    [Fact]
    public void Map_NieznanegoTekstu_ZwracaOpisZPrefiksemBlad()
    {
        MappedMessage m = ExampleMapper().MapAidite("Zupelnie nieznany komunikat XYZ");

        Assert.False(m.Matched);
        Assert.Null(m.RuleId);
        Assert.StartsWith("Błąd: ", m.ShortText, StringComparison.Ordinal);
        Assert.Equal("inny", m.Category);
        Assert.True(m.ShortText.Length <= 66, $"Skrót ma {m.ShortText.Length} znaków: \"{m.ShortText}\".");
        Assert.StartsWith("text:", m.Discriminator, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_BardzoDlugiegoNieznanegoTekstu_SkracaOpisIDodajeWielokropek()
    {
        string longText = new('X', 200);

        MappedMessage m = ExampleMapper().MapAidite(longText);

        Assert.False(m.Matched);
        Assert.StartsWith("Błąd: ", m.ShortText, StringComparison.Ordinal);
        Assert.EndsWith("…", m.ShortText, StringComparison.Ordinal);
        // Zachowanie utrwalone: "Błąd: " (6 znaków) + 60 znaków treści + "…" = 67 znaków.
        Assert.Equal(67, m.ShortText.Length);
    }

    [Fact]
    public void Map_PustegoTekstu_ZwracaKomunikatONieodczytanejTresci()
    {
        MessageMapper mapper = ExampleMapper();

        foreach (string? empty in new[] { null, "", "   ", "!!!" })
        {
            MappedMessage m = mapper.MapAidite(empty);

            Assert.False(m.Matched);
            Assert.Equal("Błąd (nie odczytano treści)", m.ShortText);
            Assert.Equal("inny", m.Category);
            Assert.Equal("text:empty", m.Discriminator);
            Assert.Equal(string.Empty, m.NormalizedText);
        }
    }

    [Fact]
    public void Map_ZRegulaProfilowa_PrzedkladaJaNadRegulaGlobalna()
    {
        var rules = new RuleSet
        {
            Rules =
            [
                new Rule { Id = "globalna", Pattern = "ALARM", ShortText = "Alarm ogólny", Category = "inny" },
                new Rule { Id = "profilowa", Pattern = "ALARM", ShortText = "Alarm 580", Category = "restart", Profile = "amm580" },
            ],
        };
        var mapper = new MessageMapper(rules);

        Assert.Equal("profilowa", mapper.Map("ALARM", "amm580").RuleId);
        Assert.Equal("globalna", mapper.Map("ALARM", "opencnc").RuleId);
        Assert.Equal("globalna", mapper.Map("ALARM").RuleId);
    }

    [Fact]
    public void Map_ZRegulaOPustymProfilu_DzialaJakGlobalna()
    {
        var rules = new RuleSet
        {
            Rules =
            [
                new Rule { Id = "pusty-profil", Pattern = "ALARM", ShortText = "Alarm ogólny", Category = "inny", Profile = "" },
            ],
        };
        var mapper = new MessageMapper(rules);

        Assert.Equal("pusty-profil", mapper.Map("ALARM", "amm580").RuleId);
        Assert.Equal("pusty-profil", mapper.Map("ALARM").RuleId);
    }

    [Fact]
    public void Map_GdyRegulaMaZlyRegex_ZglaszaOstrzezenieIDzialaDalej()
    {
        var rules = new RuleSet
        {
            Rules =
            [
                new Rule { Id = "zepsuta", Pattern = "(niedomkniety", ShortText = "Nieważne", Category = "inny" },
                new Rule { Id = "dobra", Pattern = "SPINDLE", ShortText = "Błąd wrzeciona", Category = "restart" },
            ],
        };

        var mapper = new MessageMapper(rules);

        Assert.Single(mapper.Warnings);
        Assert.Contains("zepsuta", mapper.Warnings[0], StringComparison.Ordinal);
        Assert.Equal("dobra", mapper.Map("SPINDLE ERROR").RuleId);
    }

    [Fact]
    public void Discriminator_DlaTegoSamegoTekstu_JestPowtarzalnyIRozniSieMiedzyTekstami()
    {
        MessageMapper mapper = ExampleMapper();

        MappedMessage a1 = mapper.MapAidite("Nieznany blad ABC");
        MappedMessage a2 = new MessageMapper(TestPaths.ExampleRules).MapAidite("Nieznany blad ABC");
        MappedMessage b = mapper.MapAidite("Nieznany blad DEF");

        Assert.Equal(a1.Discriminator, a2.Discriminator);
        Assert.NotEqual(a1.Discriminator, b.Discriminator);
        Assert.Equal(17, a1.Discriminator.Length); // "text:" + 12 znaków skrótu SHA-256
    }

    [Fact]
    public void Discriminator_DlaTrafionejReguly_MaPrefiksRule()
    {
        MappedMessage m = ExampleMapper().MapAidite("Spindle error");
        Assert.Equal("rule:spindle", m.Discriminator);
    }

    [Fact]
    public void Map_TekstuESTOP_TrafiaWRegule_MimoZeNormalizacjaUsuwaMyslnik()
    {
        // Wzorzec "emergency|e[ -]?stop" dopasowuje wariant "e-stop" mimo że TextNormalizer
        // zamienia "-" na spację ("E-STOP" → "E STOP").
        MappedMessage estop = ExampleMapper().MapAidite("E-STOP pressed");
        Assert.True(estop.Matched);
        Assert.Equal("emergency", estop.RuleId);
        Assert.Equal("E STOP PRESSED", estop.NormalizedText);

        MappedMessage emergency = ExampleMapper().MapAidite("Emergency stop");
        Assert.True(emergency.Matched);
        Assert.Equal("emergency", emergency.RuleId);
    }

    [Fact]
    public void Map_ZPustegoSlownikaRegul_ZawszeZwracaBrakDopasowania()
    {
        var mapper = new MessageMapper(new RuleSet());

        MappedMessage m = mapper.Map("Failed to grab a tray");

        Assert.False(m.Matched);
        Assert.Empty(mapper.Warnings);
    }


    /// <summary>
    /// Komunikaty odczytane ze zdjęć HMI (OpenCnc): tekst OCR → oczekiwany
    /// <see cref="MappedMessage.RuleId"/> (<c>code-&lt;numer&gt;</c> dla trafienia po numerze alarmu w sekcji
    /// "codes", id reguły dla trafienia po treści) i priorytet.
    /// </summary>
    public static TheoryData<string, string, int> KomunikatyZHali() => new()
    {
        { "No.520 [N]宏程序执行错误", "code-520", 40 },
        { "No.705 [N]Axis 6 servo alarm", "code-705", 90 },
        { "No.725 [N]Axis 6 servo isn't enabled", "code-725", 80 },
        { "No.2502 [N]<00>启动执行暂停宏超时,宏文件号320", "code-2502", 30 },
        { "No.1200 [p]Request home alarm", "code-1200", 50 },
        { "ool setting value exceeds error, please check whether too", "tool-setting-exceeds", 65 },
        { "[C]Tool setting value exceeds error, please check whether tool is dam", "tool-setting-exceeds", 65 },
    };

    /// <summary>Polski opis reguły o podanym identyfikatorze, wzięty WPROST z config/rules.example.json.</summary>
    private static string ShortTextOf(string ruleId) =>
        TestPaths.ExampleAidite.Rules.Single(r => r.Id == ruleId).ShortText;

    /// <summary>Polski opis kodu alarmu wzięty WPROST z sekcji "codes" pliku config/rules.example.json.</summary>
    private static string CodeShortText(int code)
    {
        var table = new AlarmCodeTable(TestPaths.ExampleAidite.Codes);
        Assert.True(table.TryResolve(code, out ResolvedAlarmCode resolved), $"Kodu {code} nie ma w rules.example.json.");
        return resolved.ShortText;
    }

    [Theory]
    [MemberData(nameof(KomunikatyZHali))]
    public void Map_KomunikatuZHali_TrafiaWOczekiwanaRegulePrawdziwegoSlownika(
        string ocrText, string ruleId, int priority)
    {
        MappedMessage m = ExampleMapper().MapAidite(ocrText);

        Assert.True(m.Matched, $"Komunikat \"{ocrText}\" nie trafił w żadną regułę (wyszło: {m.ShortText}).");
        Assert.Equal(ruleId, m.RuleId);
        // Opis porównujemy z tym, co stoi w samym słowniku — tu pilnujemy, że do alertu trafia POLSKI
        // opis TEGO wpisu, a nie „Błąd: <surowy tekst OCR>”.
        string expectedShortText = ruleId.StartsWith(MessageMapper.CodeRuleIdPrefix, StringComparison.Ordinal)
            ? CodeShortText(int.Parse(ruleId[MessageMapper.CodeRuleIdPrefix.Length..]))
            : ShortTextOf(ruleId);
        Assert.Equal(expectedShortText, m.ShortText);
        Assert.NotEmpty(m.ShortText);
        Assert.DoesNotContain("Błąd: ", m.ShortText, StringComparison.Ordinal);
        Assert.Equal(priority, m.Priority);
        Assert.Equal($"rule:{ruleId}", m.Discriminator);
    }

    [Fact]
    public void Map_AlarmuSerwonapedu_DajeKrotkaNazweZNumeremOsi()
    {
        MappedMessage m = ExampleMapper().MapAidite("No.705 [N]Axis 6 servo alarm");

        Assert.Equal("code-705", m.RuleId);
        Assert.Equal("Servo 6", m.ShortText);
        Assert.Equal("restart", m.Category);
        Assert.Equal("705", m.AlarmNumber);
        Assert.Equal("rule:code-705", m.Discriminator);
    }

    [Theory]
    [InlineData("No.7O5 [N]Ax1s 6 serv0 alarm")]     // O zamiast 0 w numerze, przekręcona treść
    [InlineData("N0.705 [N]宏程序执行错误")]            // N0 zamiast No, treść po chińsku
    [InlineData("No 705 N")]                          // bez kropki, treść ucięta
    [InlineData("•No.705[N]•••")]                      // śmieci OCR wokół numeru
    public void Map_PrzekreconegoPrzezOcrNumeruAlarmu_NadalTrafiaWKod(string ocrText)
    {
        // Dlaczego: sens słownika kodów to odporność na słaby OCR — numer jest krótki i czyta się pewniej
        // niż treść, ale i w nim OCR potrafi pomylić 0/O; bez tolerancji „No.7O5” dałoby „Błąd: No.7O5…”.
        MappedMessage m = ExampleMapper().MapAidite(ocrText);

        Assert.True(m.Matched, $"\"{ocrText}\" nie trafił w kod 705 (wyszło: {m.ShortText}).");
        Assert.Equal("code-705", m.RuleId);
        Assert.Equal("Servo 6", m.ShortText);
    }

    [Fact]
    public void Map_KoduZZakresu_PodstawiaPozycjeWZakresieZaZnacznik()
    {
        // Dlaczego: producent daje po jednym numerze na oś (700–719 = osie 1–20). Operator ma zobaczyć,
        // KTÓRA oś — „Servo 1”, „Servo 6”, „Servo 20” — a nie jeden wspólny opis dla całego zakresu.
        MessageMapper mapper = ExampleMapper();

        Assert.Equal("Servo 1", mapper.MapAidite("No.700 [N]Axis 1 servo alarm").ShortText);
        Assert.Equal("Servo 6", mapper.MapAidite("No.705").ShortText);
        Assert.Equal("Servo 20", mapper.MapAidite("No.719").ShortText);
        Assert.Equal("Servo 6 niewłączone", mapper.MapAidite("No.725 [N]Axis 6 servo isn't enabled").ShortText);
        Assert.Equal("Zużyte narzędzie 3", mapper.MapAidite("No.1227 [N]Tool life up to").ShortText);
    }

    [Fact]
    public void Map_RoznychOsiZTegoSamegoZakresu_DajeRozneKluczeAlertu()
    {
        // Dlaczego: „Servo 3” i „Servo 6” to dwa różne błędy (różne osie) — klucz alertu musi nieść numer,
        // nie tylko zakres, inaczej zlałyby się w jeden alert.
        MessageMapper mapper = ExampleMapper();

        Assert.NotEqual(mapper.MapAidite("No.702").Discriminator, mapper.MapAidite("No.705").Discriminator);
        Assert.Equal(mapper.MapAidite("No.705 [N]Axis 6 servo alarm").Discriminator, mapper.MapAidite("No.7O5 xyz").Discriminator);
    }

    [Fact]
    public void Map_NumeruWSlownikuKodow_WygrywaZRegulaTekstowa()
    {
        // Dlaczego: gdy numer JEST widoczny, to on jest źródłem prawdy — treść może pasować do ogólnej reguły
        // („limit”, „spindle”), ale kod mówi dokładnie, o który alarm chodzi.
        var rules = new RuleSet
        {
            Codes = [new AlarmCode { Code = 1240, ShortText = "Falownik wrzeciona", Category = "restart", Priority = 85 }],
            Rules = [new Rule { Id = "spindle", Phrases = ["spindle"], ShortText = "Wrzeciono", Category = "restart" }],
        };

        MappedMessage m = new MessageMapper(rules).Map("No.1240 [N]Spindle inverter alarm");

        Assert.Equal("code-1240", m.RuleId);
        Assert.Equal("Falownik wrzeciona", m.ShortText);
        Assert.Equal(85, m.Priority);
    }

    [Fact]
    public void Map_NumeruSpozaSlownikaKodow_ProbujeRegulTekstowychAPotemDajeBladNr()
    {
        // Dlaczego: słownik kodów nie jest kompletny na zawsze (nowe wersje oprogramowania maszyny) —
        // nieznany numer z czytelną treścią ma nadal trafić w regułę tekstową, a bez niej dać „Błąd nr”.
        var rules = new RuleSet
        {
            Codes = [new AlarmCode { Code = 1240, ShortText = "Falownik wrzeciona", Category = "restart" }],
            Rules = [new Rule { Id = "spindle", Phrases = ["spindle"], ShortText = "Wrzeciono", Category = "restart" }],
        };
        var mapper = new MessageMapper(rules);

        MappedMessage zRegula = mapper.Map("No.9999 [N]Spindle overheat");
        Assert.Equal("spindle", zRegula.RuleId);
        Assert.Null(zRegula.AlarmNumber);

        MappedMessage bezReguly = mapper.Map("No.9999 [N]宏程序");
        Assert.False(bezReguly.Matched);
        Assert.Equal("Błąd nr 9999 (brak opisu w słowniku)", bezReguly.ShortText);
        Assert.Equal("alarm:9999", bezReguly.Discriminator);
    }

    [Fact]
    public void Map_ZNiepoprawnymWpisemKodu_ZglaszaOstrzezenieIDzialaDalej()
    {
        // Dlaczego: jak przy błędnym regexie — literówka w rules.json (Notatnik) nie może położyć programu
        // na sześciu komputerach; zły wpis jest pomijany z ostrzeżeniem w logu.
        var rules = new RuleSet
        {
            Codes =
            [
                new AlarmCode { From = 719, To = 700, ShortText = "Odwrócony zakres" },
                new AlarmCode { Code = 1200, ShortText = "Bazowanie (Home)", Category = "restart" },
            ],
        };

        var mapper = new MessageMapper(rules);

        Assert.Single(mapper.Warnings);
        Assert.Contains("719", mapper.Warnings[0], StringComparison.Ordinal);
        Assert.Equal(1, mapper.CodeCount);
        Assert.Equal("code-1200", mapper.Map("No.1200 [p]Request home alarm").RuleId);
        Assert.False(mapper.Map("No.705").Matched);
    }

    [Fact]
    public void Map_KomunikatowJednegoEpizodu_NadajeImKolejnoscPriorytetow()
    {
        MessageMapper mapper = ExampleMapper();

        int servoAlarm = mapper.MapAidite("No.705 [N]Axis 6 servo alarm").Priority;
        int servoNotEnabled = mapper.MapAidite("No.725 [N]Axis 6 servo isn't enabled").Priority;
        int homeRequest = mapper.MapAidite("No.1200 [p]Request home alarm").Priority;
        int macroExec = mapper.MapAidite("No.520 [N]宏程序执行错误").Priority;
        int macroPause = mapper.MapAidite("No.2502 [N]<00>启动执行暂停宏超时,宏文件号320").Priority;

        Assert.True(servoAlarm > servoNotEnabled, $"servo-alarm ({servoAlarm}) musi bić servo-not-enabled ({servoNotEnabled}).");
        Assert.True(servoNotEnabled > homeRequest, $"servo-not-enabled ({servoNotEnabled}) musi bić home-request ({homeRequest}).");
        Assert.True(homeRequest > macroExec, $"home-request ({homeRequest}) musi bić macro-exec-error ({macroExec}).");
        Assert.True(macroExec > macroPause, $"macro-exec-error ({macroExec}) musi bić macro-pause-timeout ({macroPause}).");
    }

    [Fact]
    public void Map_WzorcaZNumeremAlarmu_DopasowujeGoNaWariancieZachowujacymCyfry()
    {
        // Dlaczego: treść komunikatu bywa po chińsku — jedyne, co OCR z niej wyciągnie, to numer alarmu.
        // Zwykła normalizacja robi z „No.1200” tekst „NO I2OO”, więc gdyby mapper sprawdzał wzorzec tylko
        // na niej, reguła „home-request” nigdy by nie zadziałała dla maszyny po restarcie.
        var rules = new RuleSet
        {
            Rules =
            [
                new Rule
                {
                    Id = "tylko-numer",
                    Pattern = @"\bNO\s*12[0O][0O]\b",
                    ShortText = "Wymaga bazowania (Home) po restarcie",
                    Category = "restart",
                    Priority = 50,
                },
            ],
        };

        MappedMessage m = new MessageMapper(rules).Map("No.1200 [p]Request home alarm");

        Assert.True(m.Matched);
        Assert.Equal("tylko-numer", m.RuleId);
        Assert.Equal(50, m.Priority);
        // Wariant zapisany w MappedMessage.NormalizedText to nadal ten „zwykły” (z podstawieniem cyfr) —
        // dopasowanie udało się TYLKO dzięki drugiemu wariantowi, sprawdzanemu wewnątrz mappera.
        Assert.Equal("NO I2OO P REQUEST HOME ALARM", m.NormalizedText);
    }

    [Fact]
    public void Map_FrazyNadalDopasowywaneNaWariancieZPodstawieniemCyfr()
    {
        var rules = new RuleSet
        {
            Rules = [new Rule { Id = "fraza", Phrases = ["tool broken"], ShortText = "Złamane narzędzie", Category = "narzedzie" }],
        };

        MappedMessage m = new MessageMapper(rules).Map("T00l br0ken");

        Assert.True(m.Matched);
        Assert.Equal("fraza", m.RuleId);
    }

    [Fact]
    public void Map_RegulyBezPolaPriority_DajePriorytetZero()
    {
        // Dlaczego: priorytet jest opcjonalny — stary rules.json (bez tego pola) ma działać jak dotąd,
        // czyli „wszystkie reguły równe, wygrywa zobaczona pierwsza”.
        var rules = new RuleSet
        {
            Rules = [new Rule { Id = "bez-priorytetu", Pattern = "SPINDLE", ShortText = "Błąd wrzeciona", Category = "restart" }],
        };

        Assert.Equal(0, new MessageMapper(rules).Map("Spindle error").Priority);
    }

    [Fact]
    public void Map_TekstuNiedopasowanegoIPustego_DajePriorytetZero()
    {
        // Dlaczego: „Błąd: <surowy tekst>” i „Błąd (nie odczytano treści)” muszą przegrywać z KAŻDĄ
        // dopasowaną regułą (patrz MappedMessage.IsBetterThan) — ich priorytet nie może być przypadkowy.
        MessageMapper mapper = ExampleMapper();

        Assert.Equal(0, mapper.MapAidite("Zupelnie nieznany komunikat XYZ").Priority);
        Assert.Equal(0, mapper.MapAidite("").Priority);
    }

    [Theory]
    [InlineData("All procedures have been processed according to the production plan")]
    [InlineData("A I PROCEDURES HAVE BEEN PROCESSED ACCORDING TO THE PRODUCTION PLAN")]
    [InlineData("AII procedures have been processed according to the production plan")]
    public void Map_ZoltegoKomunikatuPlanProdukcjiWykonany_JestOznaczonyJakoNieBlad(string text)
    {
        MappedMessage m = ExampleMapper().MapAidite(text);

        Assert.True(m.Matched, "Żółty komunikat ma trafić w regułę ignorującą.");
        Assert.True(m.Ignored, $"Reguła \"{m.RuleId}\" nie jest oznaczona jako „nie błąd”.");
        Assert.Equal(Rule.IgnoredCategory, m.Category);
        Assert.False(m.IsBetterThan(null), "Komunikat „nie błąd” nie może zostać najlepszym komunikatem epizodu.");
    }

    [Fact]
    public void Map_ZoltegoKomunikatuPlanProdukcjiWykonany_NieTrafiaWZadenBlad()
    {
        MappedMessage m = ExampleMapper().MapAidite("All procedures have been processed according to the production plan");

        Assert.True(m.Ignored, $"Żółty komunikat trafił w regułę BŁĘDU \"{m.RuleId}\" ({m.ShortText}).");
    }

    [Theory]
    // Prawdziwe słowa, które NIE są słowem „door”.
    [InlineData("Tool life was reached. Please change the tool.")]
    [InlineData("All procedures have been processed")]
    [InlineData("Duration exceeded")]
    public void Map_SlowPodobnychDoKrotkiejFrazy_NieTrafiaWRegule(string tekst)
    {
        // Dlaczego osobno od testu wyżej: tam pierwsza pasująca reguła („plan wykonany”) i tak jest
        // ignorująca, więc sama w sobie nie dowodzi, że „door” przestało łapać cudze słowa.
        MessageMapper mapper = ExampleMapper();

        Assert.NotEqual("door", mapper.MapAidite(tekst).RuleId);
    }

    [Theory]
    [InlineData("door", 2, 1)]
    [InlineData("door", 0, 0)]
    [InlineData("spindle", 2, 2)]
    [InlineData("setting value exceeds", 2, 2)]
    [InlineData("ab", 2, 0)]
    public void EffectiveDistance_PrzycinaTolerancjeDoDlugosciFrazy(string fraza, int zReguly, int oczekiwana)
    {
        // Kontrakt jednym zdaniem: najwyżej jedna dopuszczalna pomyłka OCR na 3 znaki frazy, nigdy więcej
        // niż podano w regule. Czteroliterowe „door” = 1 (to przepuszcza zmierzone „DOOOR”, a odcina „TOOL”).
        Assert.Equal(oczekiwana, MessageMapper.EffectiveDistance(TextNormalizer.Normalize(fraza), zReguly));
    }
}
