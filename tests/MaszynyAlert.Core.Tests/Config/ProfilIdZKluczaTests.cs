using System.Text.Json;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ProfilIdZKluczaTests
{
    private static readonly JsonDocumentOptions KomentarzeIPrzecinki = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const string BeforeUsing520 = "EDIT READY M024. 01 Before using machi ne. AUTO USB PROTECT 2026.09.25 68";

    private static string GoldenDir => Path.Combine(TestPaths.RepositoryRoot, "tests", "MaszynyAlert.Core.Tests", "Config",
        "OdciskProfilu-0.4.79");

    /// <summary>Tekst config.json z przykładu po zmianie każdego obiektu profilu przez <paramref name="profil"/> (klucz, obiekt).</summary>
    private static string ConfigJson(Action<string, JsonObject> profil)
    {
        JsonObject json = JsonNode.Parse(File.ReadAllText(TestPaths.ConfigExample), documentOptions: KomentarzeIPrzecinki)!.AsObject();
        foreach ((string klucz, JsonNode? wartosc) in json["profiles"]!.AsObject())
            profil(klucz, wartosc!.AsObject());
        return json.ToJsonString();
    }

    /// <summary>Słownik znaczników: baza każdego profilu ma kod 705 „Opis &lt;klucz&gt;” kategorii „kat-&lt;klucz&gt;”.</summary>
    private static RuleSet SlownikZnacznikow(IEnumerable<string> profile)
    {
        var databases = new JsonObject();
        foreach (string klucz in profile)
        {
            databases[klucz] = new JsonObject
            {
                ["codeIdPrefix"] = klucz + "-",
                ["codes"] = new JsonArray(new JsonObject { ["code"] = 705, ["shortText"] = $"Opis {klucz}", ["category"] = $"kat-{klucz}" }),
                ["rules"] = new JsonArray(),
            };
        }

        return ConfigLoader.ParseRules(new JsonObject { ["databases"] = databases }.ToJsonString());
    }

    // =====================================================================================================================
    // 1. Wczytanie: Id = klucz zawsze; ostrzeżenie tylko dla innej, niepustej wartości
    // =====================================================================================================================

    /// <summary>
    /// Poprawne pliki — bez pola <c>"id"</c> (przykład), z <c>"id"</c> = klucz w każdym profilu (kształt config.json zapisanego
    /// przez program), z <c>"id": null</c> i z pustym tekstem — dają konfigurację identyczną z przykładem (porównanie całego
    /// zapisu JSON), <c>Id</c> = klucz i zero ostrzeżeń.
    /// </summary>
    [Theory]
    [InlineData("bez-pola")]
    [InlineData("rowne-kluczowi")]
    [InlineData("null")]
    [InlineData("pusty-tekst")]
    public void PoprawnyPlik_IdRowneKluczowi_BezOstrzezen_KonfiguracjaJakDotad(string wariant)
    {
        Action<string, JsonObject> zmiana = wariant switch
        {
            "bez-pola" => (_, p) => p.Remove("id"),
            "rowne-kluczowi" => (k, p) => p["id"] = k,
            "null" => (_, p) => p["id"] = null,
            "pusty-tekst" => (_, p) => p["id"] = "",
            _ => throw new ArgumentOutOfRangeException(nameof(wariant)),
        };
        var ostrzezenia = new List<string>();

        AppConfig cfg = ConfigLoader.ParseAppConfig(ConfigJson(zmiana), ostrzezenia);

        Assert.Empty(ostrzezenia);
        Assert.All(cfg.Profiles, p => Assert.Equal(p.Key, p.Value.Id));
        Assert.Equal(ConfigLoader.Serialize(TestPaths.ExampleConfig), ConfigLoader.Serialize(cfg));
        ConfigLoader.Validate(cfg);
        Assert.Empty(ConfigLoader.Warnings(cfg));
        Assert.Empty(ConfigLoader.Warnings(TestPaths.ExampleRules, cfg));
    }

    [Theory]
    [InlineData("aidite5x")]
    [InlineData("premile-stary")]
    [InlineData("PREMILE")]
    [InlineData(" premile")]
    [InlineData("cameo1")]
    public void JawneIdInneNizKlucz_IdRowneKluczowi_JednoOstrzezenieZNazwaKluczaIPodanymId(string podaneId)
    {
        string json = ConfigJson((k, p) => { if (k == "premile") p["id"] = podaneId; });
        var ostrzezenia = new List<string>();

        AppConfig cfg = ConfigLoader.ParseAppConfig(json, ostrzezenia);

        Assert.Equal("premile", cfg.Profiles["premile"].Id);
        Assert.All(cfg.Profiles, p => Assert.Equal(p.Key, p.Value.Id));
        string ostrzezenie = Assert.Single(ostrzezenia);
        Assert.Equal(ConfigLoader.ProfileIdWarning("premile", podaneId), ostrzezenie);
        Assert.StartsWith($"Profil ekranu 'premile' w config.json ma pole \"id\": \"{podaneId}\", inne niż nazwa profilu",
            ostrzezenie, StringComparison.Ordinal);
        Assert.Contains("Program używa nazwy 'premile'", ostrzezenie, StringComparison.Ordinal);
        Assert.Contains("Popraw pole na \"id\": \"premile\" albo je usuń", ostrzezenie, StringComparison.Ordinal);
        Assert.Contains($"po polu \"id\" ('{podaneId}')", ostrzezenie, StringComparison.Ordinal);

        Assert.Equal(ConfigLoader.Serialize(TestPaths.ExampleConfig), ConfigLoader.Serialize(cfg));
        Assert.Equal(ConfigLoader.Serialize(cfg), ConfigLoader.Serialize(ConfigLoader.ParseAppConfig(json)));
        ConfigLoader.Validate(cfg);
        ConfigLoader.Validate(TestPaths.ExampleRules, cfg);
        Assert.Empty(ConfigLoader.Warnings(TestPaths.ExampleRules, cfg));
    }

    /// <summary>Lista ostrzeżeń jest obowiązkowa w przeciążeniach z listą (brak listy = przeciążenie bez niej).</summary>
    [Fact]
    public void PrzeciazeniaZListaOstrzezen_BezListy_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigLoader.ParseAppConfig(ConfigJson((_, _) => { }), null!));
        Assert.Throws<ArgumentNullException>(() => ConfigLoader.LoadAppConfig(TestPaths.ConfigExample, null!));
    }

    // =====================================================================================================================
    // 2. Jedna tożsamość profilu: alarm (po ScreenProfile.Id), raport (po kluczu) i ostrzeżenia słownika — ta sama baza
    // =====================================================================================================================

    [Theory]
    [InlineData("zamiana-premile-cameo1", 2)]
    [InlineData("kopia-bloku-520", 2)]
    [InlineData("id-nastepnego-profilu", 6)]
    [InlineData("id-nieistniejacego-profilu", 6)]
    public void ProfileZInnymId_AlarmRaportIOstrzezeniaSlownika_BazaKluczaProfilu(string wariant, int oczekiwaneOstrzezenia)
    {
        string[] klucze = [.. TestPaths.ExampleConfig.Profiles.Keys];
        Assert.Equal(6, klucze.Length);
        Func<string, string?> id = wariant switch
        {
            "zamiana-premile-cameo1" => k => k switch { "premile" => "cameo1", "cameo1" => "premile", _ => null },
            "kopia-bloku-520" => k => k is "premile" or "cameo1" ? "aidite5x" : null,
            "id-nastepnego-profilu" => k => klucze[(Array.IndexOf(klucze, k) + 1) % klucze.Length],
            "id-nieistniejacego-profilu" => k => "x-" + k,
            _ => throw new ArgumentOutOfRangeException(nameof(wariant)),
        };
        var ostrzezenia = new List<string>();
        AppConfig cfg = ConfigLoader.ParseAppConfig(ConfigJson((k, p) => { if (id(k) is { } v) p["id"] = v; }), ostrzezenia);
        RuleSet rules = SlownikZnacznikow(klucze);
        ConfigLoader.Validate(cfg);
        ConfigLoader.Validate(rules, cfg);

        Assert.Equal(oczekiwaneOstrzezenia, ostrzezenia.Count);
        Assert.Empty(ConfigLoader.Warnings(rules, cfg));
        var mapper = new MessageMapper(rules);
        var klasyfikator = new DowntimeCategoryClassifier(rules, cfg.Machines);
        var rozne = new List<string>();
        foreach (MachineConfig maszyna in cfg.Machines)
        {
            string alarm = mapper.Map("No.705", cfg.Profiles[maszyna.Profile].Id).ShortText;
            string raport = klasyfikator.Classify(maszyna.Id, $"Opis {maszyna.Profile}");
            if (alarm != $"Opis {maszyna.Profile}" || raport != $"kat-{maszyna.Profile}")
                rozne.Add($"{maszyna.Id} (profil '{maszyna.Profile}'): alarm „{alarm}”, raport „{raport}”");
        }

        Assert.True(rozne.Count == 0, string.Join("\n", rozne));
    }

    [Fact]
    public void Premile_ZKopiaBloku520_BezWyciszen520_OpisJakWRaporcie()
    {
        AppConfig cfg = ConfigLoader.ParseAppConfig(ConfigJson((k, p) => { if (k == "premile") p["id"] = "aidite5x"; }));
        var mapper = new MessageMapper(TestPaths.ExampleRules);
        string premile = cfg.Profiles["premile"].Id;

        Assert.Equal("premile", premile);
        Assert.False(mapper.HasSuppressRules(premile));
        Assert.False(mapper.Map(BeforeUsing520, premile).Suppressed);
        Assert.True(mapper.Map(BeforeUsing520, cfg.Profiles["aidite5x"].Id).Suppressed);

        MappedMessage falownik = mapper.Map("HOLDING [1240] Spindle inverter alarm", premile);
        Assert.Null(falownik.RuleId);
        Assert.StartsWith("Błąd: ", falownik.ShortText, StringComparison.Ordinal);
        Assert.Equal(falownik.Category,
            new DowntimeCategoryClassifier(TestPaths.ExampleRules, cfg.Machines).Classify("premile", falownik.ShortText));
    }

    // =====================================================================================================================
    // 3. Odcisk profilu grupy 0.4.79 i zapis config.json przez program
    // =====================================================================================================================

    /// <summary>
    /// Profil grupy przechwycony z konfiguracji wczytanej z config.json (droga kreatora grupy: <see cref="GroupProfile.Capture"/>)
    /// ma DOKŁADNIE złoty odcisk 0.4.79 — gdy profile mają pole <c>"id"</c> równe kluczowi (config.json zapisany przez program),
    /// gdy go nie mają, i gdy pierwszy profil ma <c>"id"</c> innego profilu (<c>GroupFileCodec</c> wymaga <c>id</c> = klucz). Zaproszenie z każdego wariantu niesie ten sam odcisk.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfileHashCompatibilityTests.GroupFileProfiles), MemberType = typeof(ProfileHashCompatibilityTests))]
    public void ProfilGrupyZConfigJson_OdciskJak0_4_79_NiezaleznieOdPolaId(string fixture, string hash0479)
    {
        GroupProfile hala = JsonSerializer.Deserialize<GroupProfile>(
            File.ReadAllBytes(Path.Combine(GoldenDir, fixture + ".json")), GroupFileCodec.Options)!;
        string zapisanyPrzezProgram = ConfigLoader.Serialize(hala.ApplyTo(new AppConfig()));
        string pierwszy = hala.Profiles.Keys.First();
        string inny = hala.Profiles.Keys.Skip(1).FirstOrDefault() ?? "inny-profil";
        var warianty = new Dictionary<string, Action<string, JsonObject>>
        {
            ["id-rowne-kluczowi"] = (k, p) => Assert.Equal(k, p["id"]!.GetValue<string>()),
            ["bez-pola-id"] = (_, p) => p.Remove("id"),
            ["pierwszy-z-id-innego-profilu"] = (k, p) => { if (k == pierwszy) p["id"] = inny; },
        };

        foreach ((string nazwa, Action<string, JsonObject> zmiana) in warianty)
        {
            JsonObject json = JsonNode.Parse(zapisanyPrzezProgram)!.AsObject();
            foreach ((string klucz, JsonNode? profil) in json["profiles"]!.AsObject())
                zmiana(klucz, profil!.AsObject());
            AppConfig cfg = ConfigLoader.ParseAppConfig(json.ToJsonString());

            GroupProfile przechwycony = GroupProfile.Capture(cfg, hala.Rules, hala.Instructions);

            Assert.True(hash0479 == GroupFileCodec.ProfileHash(przechwycony), $"{fixture}, {nazwa}: odcisk inny niż 0.4.79.");
            Assert.Equal(hash0479, GroupFile.Create("Hala", przechwycony).Identity.ProfileHash);
        }
    }

    /// <summary>
    /// Pierwszy zapis config.json przez program (okno Ustawień, pomocnik UAC — <see cref="ConfigLoader.SaveAppConfig"/> po
    /// <see cref="ConfigLoader.LoadAppConfig(string)"/>) zapisuje <c>"id"</c> = klucz: plik z innym <c>"id"</c> naprawia się sam,
    /// a kolejne wczytanie nie ma już ostrzeżenia. Schemat pliku bez zmian — pole <c>"id"</c> zostaje.
    /// </summary>
    [Fact]
    public void ZapisPrzezProgram_PlikZInnymId_ZapisujeIdRowneKluczowi_KolejneWczytanieBezOstrzezen()
    {
        using var dir = new TempDir("wer-profil-id");
        string path = dir.File("config.json");
        File.WriteAllText(path, ConfigJson((k, p) => { if (k == "premile") p["id"] = "aidite5x"; }));
        var przedZapisem = new List<string>();
        AppConfig cfg = ConfigLoader.LoadAppConfig(path, przedZapisem);
        Assert.Single(przedZapisem);

        ConfigLoader.SaveAppConfig(path, cfg);

        var poZapisie = new List<string>();
        AppConfig wczytany = ConfigLoader.LoadAppConfig(path, poZapisie);
        Assert.Empty(poZapisie);
        Assert.Equal("premile", wczytany.Profiles["premile"].Id);
        JsonObject zapisany = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("premile", zapisany["profiles"]!["premile"]!["id"]!.GetValue<string>());
        Assert.Equal("aidite5x", zapisany["profiles"]!["aidite5x"]!["id"]!.GetValue<string>());
    }

    // =====================================================================================================================
    // 4. Okablowanie startu (Program.RunInstance — testu procesu nie ma: kod startu wymaga plików w ProgramData)
    // =====================================================================================================================

    /// <summary>
    /// <c>Program.RunInstance</c> wczytuje config.json z listą ostrzeżeń, czyści ją, gdy profile ekranu zastępuje zatwierdzony profil
    /// grupy (lokalnych program wtedy nie używa — jak ostrzeżenia słownika liczone po nałożeniu profilu grupy), i loguje ją po starcie
    /// hosta tą samą drogą (<c>Log.Warning("{Warning}", …)</c>) co pozostałe ostrzeżenia konfiguracji. Przy uszkodzonych ustawieniach
    /// grupy (obserwacja lokalna) lista zostaje.
    /// </summary>
    [Fact]
    public void Program_OstrzezeniaZWczytaniaConfigJson_DoLoguGdyDzialajaProfileZPliku()
    {
        string program = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "MaszynyAlert.App", "Program.cs"));
        int start = program.IndexOf("private static void RunInstance", StringComparison.Ordinal);
        int end = program.IndexOf("private static int RunStartupSmokeTest", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "Nie znaleziono metody RunInstance w Program.cs.");
        string run = program[start..end];

        int load = run.IndexOf("appConfig = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath, configFileWarnings);", StringComparison.Ordinal);
        int group = run.IndexOf("ruleSet = committed.Profile.Rules;", StringComparison.Ordinal);
        int clear = run.IndexOf("configFileWarnings.Clear();", StringComparison.Ordinal);
        int damaged = run.IndexOf("catch (InvalidDataException ex)", StringComparison.Ordinal);
        int hostStart = run.IndexOf("host.Start();", StringComparison.Ordinal);
        int logged = run.IndexOf("foreach (string warning in configFileWarnings) Log.Warning(\"{Warning}\", warning);", StringComparison.Ordinal);

        Assert.True(load > 0, "config.json ma być wczytywany z listą ostrzeżeń (configFileWarnings).");
        Assert.DoesNotContain("ConfigLoader.LoadAppConfig(AppPaths.ConfigPath);", run, StringComparison.Ordinal);
        Assert.True(group > load && clear > group && clear < damaged,
            "Lista ma być czyszczona tylko po nałożeniu zatwierdzonego profilu grupy (przed obsługą uszkodzonych ustawień grupy).");
        Assert.Equal(clear, run.LastIndexOf("configFileWarnings.Clear();", StringComparison.Ordinal));
        Assert.True(logged > hostStart, "Ostrzeżenia z wczytania config.json mają trafiać do logu po starcie hosta.");
    }
}
