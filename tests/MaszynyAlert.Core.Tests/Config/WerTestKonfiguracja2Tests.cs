using System.Text.Json;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerTestKonfiguracja2Tests
{
    private static readonly JsonDocumentOptions KomentarzeIPrzecinki = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const string Falownik520 = "HOLDING [1240] Spindle inverter alarm";

    private static JsonObject WzorcowyConfigJson() =>
        JsonNode.Parse(File.ReadAllText(TestPaths.ConfigExample), documentOptions: KomentarzeIPrzecinki)!.AsObject();

    private static JsonObject WzorcowyRulesJson() =>
        JsonNode.Parse(File.ReadAllText(TestPaths.RulesExample), documentOptions: KomentarzeIPrzecinki)!.AsObject();

    /// <summary>
    /// config.json z przykładu z kluczami profili zmienionymi przez <paramref name="nowyKlucz"/> (maszyny wskazują nowe klucze)
    /// i z jawnym polem <c>"id"</c> profilu, gdy <paramref name="jawneId"/> je zwraca (plik
    /// zapisany przez program). Czytany jak przez program (<see cref="ConfigLoader.ParseAppConfig"/>).
    /// </summary>
    private static AppConfig Konfiguracja(Func<string, string> nowyKlucz, Func<string, string?>? jawneId = null) =>
        ConfigLoader.ParseAppConfig(KonfiguracjaJson(nowyKlucz, jawneId));

    /// <summary>Tekst config.json dla <see cref="Konfiguracja"/> (bez wczytania — do testów, w których odrzucenie przy wczytaniu jest wynikiem).</summary>
    private static string KonfiguracjaJson(Func<string, string> nowyKlucz, Func<string, string?>? jawneId = null)
    {
        JsonObject json = WzorcowyConfigJson();
        JsonObject profile = json["profiles"]!.AsObject();
        var poZmianie = new JsonObject();
        foreach ((string klucz, JsonNode? wartosc) in profile.ToList())
        {
            profile.Remove(klucz);
            if (jawneId?.Invoke(klucz) is { } id) wartosc!["id"] = id;
            poZmianie[nowyKlucz(klucz)] = wartosc;
        }

        json["profiles"] = poZmianie;
        foreach (JsonNode? maszyna in json["machines"]!.AsArray())
            maszyna!["profile"] = nowyKlucz(maszyna["profile"]!.GetValue<string>());
        return json.ToJsonString();
    }

    /// <summary>Słownik znaczników: baza <c>bazy[i]</c> ma kod 705 „Opis i” kategorii „kat-i” i przedrostek „z&lt;i&gt;-”.</summary>
    private static RuleSet SlownikZnacznikow(IReadOnlyList<string> bazy)
    {
        var databases = new JsonObject();
        for (int i = 0; i < bazy.Count; i++)
        {
            databases[bazy[i]] = new JsonObject
            {
                ["codeIdPrefix"] = $"z{i}-",
                ["codes"] = new JsonArray(new JsonObject { ["code"] = 705, ["shortText"] = $"Opis {i}", ["category"] = $"kat-{i}" }),
                ["rules"] = new JsonArray(),
            };
        }

        return ConfigLoader.ParseRules(new JsonObject { ["databases"] = databases }.ToJsonString());
    }

    /// <summary>Baza, z której alarm na żywo opisał numer 705 dla profilu <paramref name="profileId"/>; <c>null</c> = listy wspólne.</summary>
    private static string? BazaAlarmu(MessageMapper mapper, string profileId, IReadOnlyList<string> bazy)
    {
        MappedMessage mapped = mapper.Map("No.705", profileId);
        for (int i = 0; i < bazy.Count; i++)
            if (mapped.ShortText == $"Opis {i}") return bazy[i];
        return null;
    }

    /// <summary>Baza, z której raport postojów rozpoznaje opisy maszyny <paramref name="machineId"/>; <c>null</c> = żadna.</summary>
    private static string? BazaRaportu(DowntimeCategoryClassifier klasyfikator, string machineId, IReadOnlyList<string> bazy)
    {
        string? wynik = null;
        for (int i = 0; i < bazy.Count; i++)
        {
            if (klasyfikator.Classify(machineId, $"Opis {i}") != $"kat-{i}") continue;
            Assert.True(wynik is null, $"Raport maszyny {machineId} rozpoznaje opisy dwóch baz: '{wynik}' i '{bazy[i]}'.");
            wynik = bazy[i];
        }

        return wynik;
    }

    // =====================================================================================================================
    // 1. Jedna maszyna — jedna baza: alarm, raport postojów i ostrzeżenia zgodne dla wariantów nazw
    // =====================================================================================================================

    [Theory]
    [InlineData("wzorzec")]
    [InlineData("bazy-wielkimi")]
    [InlineData("profile-wielkimi")]
    [InlineData("jawne-id-jak-pc1")]
    [InlineData("spacja-w-nazwie-bazy")]
    [InlineData("polskie-litery")]
    [InlineData("kropkowane-I")]
    public void TK2_01_KazdaMaszyna_AlarmIRaportTaSamaBaza_OstrzezeniaZgodneZAlarmem(string wariant)
    {
        Func<string, string> kluczProfilu = wariant switch
        {
            "profile-wielkimi" => k => k.ToUpperInvariant(),
            "polskie-litery" => k => k == "premile" ? "żółw" : k,
            "kropkowane-I" => k => k == "premile" ? "i-maszyna" : k,
            _ => k => k,
        };
        Func<string, string?>? jawneId = wariant == "jawne-id-jak-pc1" ? k => k : null;
        AppConfig cfg = Konfiguracja(kluczProfilu, jawneId);

        Func<string, string> nazwaBazy = wariant switch
        {
            "bazy-wielkimi" => p => p.ToUpperInvariant(),
            "profile-wielkimi" => p => p.ToLowerInvariant(),
            "spacja-w-nazwie-bazy" => p => p == "aidite5x" ? "aidite5x " : p,
            "polskie-litery" => p => p == "żółw" ? "ŻÓŁW" : p,
            "kropkowane-I" => p => p == "i-maszyna" ? "İ-MASZYNA" : p,
            _ => p => p,
        };
        string[] bazy = [.. cfg.Profiles.Keys.Select(nazwaBazy)];
        RuleSet rules = SlownikZnacznikow(bazy);
        // Same lambdy (nie grupa metod nazwaBazy): switch bierze typ docelowy Func<string, string?>, więc null bez CS8603.
        Func<string, string?> oczekiwana = wariant switch
        {
            "spacja-w-nazwie-bazy" => p => p == "aidite5x" ? null : nazwaBazy(p),
            "kropkowane-I" => p => p == "i-maszyna" ? null : nazwaBazy(p),
            _ => p => nazwaBazy(p),
        };

        ConfigLoader.Validate(cfg);
        ConfigLoader.Validate(rules, cfg);
        var mapper = new MessageMapper(rules);
        Assert.Empty(mapper.Warnings);
        var klasyfikator = new DowntimeCategoryClassifier(rules, cfg.Machines);

        var rozne = new List<string>();
        foreach (MachineConfig maszyna in cfg.Machines)
        {
            string? alarm = BazaAlarmu(mapper, cfg.Profiles[maszyna.Profile].Id, bazy); // jak MachineMonitor
            string? raport = BazaRaportu(klasyfikator, maszyna.Id, bazy);
            string? spodziewana = oczekiwana(maszyna.Profile);
            if (alarm != spodziewana || raport != spodziewana)
                rozne.Add($"{maszyna.Id} (profil '{maszyna.Profile}'): alarm '{alarm}', raport '{raport}', oczekiwana '{spodziewana}'");
        }

        IReadOnlyList<string> ostrzezenia = ConfigLoader.Warnings(rules, cfg);
        foreach (string baza in bazy)
        {
            bool uzywana = cfg.Profiles.Values.Any(p => BazaAlarmu(mapper, p.Id, bazy) == baza);
            bool ostrzezona = ostrzezenia.Any(w => w.StartsWith($"Baza błędów '{baza}' ", StringComparison.Ordinal));
            if (uzywana == ostrzezona)
                rozne.Add($"baza '{baza}': używana przez alarm = {uzywana}, ostrzeżenie „nie używa jej żadna maszyna” = {ostrzezona}");
        }

        Assert.True(rozne.Count == 0, string.Join("\n", rozne));
        Assert.Equal(wariant is "spacja-w-nazwie-bazy" or "kropkowane-I" ? 1 : 0, ostrzezenia.Count);
    }


    [Fact]
    public void TK2_02_DwaProfileIDwieBazyRozneTylkoWielkosciaLiter_Odrzucone_ADo0_4_249AlarmIUtakBralCudzaBaze()
    {
        JsonObject json = WzorcowyConfigJson();
        json["profiles"]!["AIDITE5X"] = json["profiles"]!["aidite5x"]!.DeepClone();
        foreach (JsonNode? maszyna in json["machines"]!.AsArray())
            if (maszyna!["id"]!.GetValue<string>() == "amm520-2") maszyna["profile"] = "AIDITE5X";
        AppConfig cfg = ConfigLoader.ParseAppConfig(json.ToJsonString());
        ConfigLoader.Validate(cfg); // dwa profile różne tylko wielkością liter — dozwolone
        Assert.True(cfg.Profiles.ContainsKey("aidite5x") && cfg.Profiles.ContainsKey("AIDITE5X"));

        string[] dwieBazy = [.. cfg.Profiles.Keys];
        Assert.True(Array.IndexOf(dwieBazy, "aidite5x") < Array.IndexOf(dwieBazy, "AIDITE5X"));
        RuleSet rules = SlownikZnacznikow(dwieBazy);

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, cfg));
        Assert.Contains("Bazy błędów 'aidite5x' i 'AIDITE5X' w rules.json różnią się tylko wielkością liter", ex.Message, StringComparison.Ordinal);

        var mapper = new MessageMapper(rules);
        Assert.Contains(mapper.Warnings, w => w.Contains("\"AIDITE5X\" powtarza się", StringComparison.Ordinal));
        Assert.Equal("aidite5x", BazaAlarmu(mapper, cfg.Profiles["AIDITE5X"].Id, dwieBazy));
        Assert.Equal("aidite5x", BazaRaportu(new DowntimeCategoryClassifier(rules, cfg.Machines), "amm520-2", dwieBazy));

        string[] jednaBaza = [.. dwieBazy.Where(b => b != "AIDITE5X")];
        RuleSet jedna = SlownikZnacznikow(jednaBaza);
        ConfigLoader.Validate(jedna, cfg);
        Assert.Empty(ConfigLoader.Warnings(jedna, cfg));
        var klasyfikator = new DowntimeCategoryClassifier(jedna, cfg.Machines);
        foreach (string id in new[] { "amm520-1", "amm520-2" })
        {
            string profil = cfg.Machines.Single(m => m.Id == id).Profile;
            Assert.Equal("aidite5x", BazaAlarmu(new MessageMapper(jedna), cfg.Profiles[profil].Id, jednaBaza));
            Assert.Equal("aidite5x", BazaRaportu(klasyfikator, id, jednaBaza));
        }
    }

    [Theory]
    [InlineData("AIDITE5X", true)]
    [InlineData("aidite5x-b", false)]
    public void TK2_03_ZapisanaGeneracjaStarszejWersjiZBazamiRoznymiTylkoWielkosciaLiter_ObserwacjaLokalna(string drugaBaza, bool odrzucona)
    {
        JsonObject cfgJson = WzorcowyConfigJson();
        cfgJson["profiles"]!["AIDITE5X"] = cfgJson["profiles"]!["aidite5x"]!.DeepClone();
        AppConfig cfg = ConfigLoader.ParseAppConfig(cfgJson.ToJsonString());
        GroupFile zaproszenie = GroupFile.Create("Hala", GroupProfile.Capture(cfg, TestPaths.ExampleRules, new Dictionary<string, string>()));

        using var dir = new TempDir("wer-tk2-grupa");
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        Guid id = store.Stage(new GroupSettingsProposal(zaproszenie, new ClusterConfig { Priority = 57 }, enabled: true), expectedCurrent: null);
        store.Commit(id);

        // „Starsza wersja” zapisała w profilu grupy drugą bazę (pustą, z własnym przedrostkiem).
        string path = Directory.GetFiles(dir.Path, "generation-*.json").Single();
        JsonNode generacja = JsonNode.Parse(File.ReadAllText(path))!;
        JsonObject bazy = generacja["data"]!["profile"]!["rules"]!["databases"]!.AsObject();
        JsonNode kopia = bazy["cameo1"]!.DeepClone();
        kopia["codeIdPrefix"] = "aidite5x-b-";
        bazy[drugaBaza] = kopia;
        File.WriteAllText(path, generacja.ToJsonString());

        var poAktualizacji = new GroupSettingsStore(dir.Path, secrets);
        if (!odrzucona)
        {
            Assert.NotNull(poAktualizacji.ReadForStartup().Data);
            return;
        }

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => poAktualizacji.ReadForStartup());
        Assert.Contains("profil nie przechodzą kontroli ustawień", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => poAktualizacji.Stage(
            new GroupSettingsProposal(GroupFileTests.Invitation(), new ClusterConfig { Priority = 57 }, enabled: true), expectedCurrent: id));
    }

    // =====================================================================================================================
    // 3. Raport postojów od wpisu dziennika do wiersza (tę samą tablicę wierszy biorą Panel, wykresy i eksport)
    // =====================================================================================================================

    [Theory]
    [InlineData("AIDITE5X")]
    [InlineData("Aidite5X")]
    public void TK2_04_EpizodBezKategorii_BazaInnaWielkosciaLiter_WierszRaportuMaRodzajAlertu(string nazwaBazy)
    {
        JsonObject json = WzorcowyRulesJson();
        JsonObject bazy = json["databases"]!.AsObject();
        var poZmianie = new JsonObject();
        foreach ((string klucz, JsonNode? wartosc) in bazy.ToList())
        {
            bazy.Remove(klucz);
            poZmianie[klucz == "aidite5x" ? nazwaBazy : klucz] = wartosc;
        }

        json["databases"] = poZmianie;
        RuleSet rules = ConfigLoader.ParseRules(json.ToJsonString());
        AppConfig cfg = TestPaths.ExampleConfig;
        ConfigLoader.Validate(rules, cfg);
        Assert.Empty(ConfigLoader.Warnings(rules, cfg));

        MappedMessage alert = new MessageMapper(rules).Map(Falownik520, cfg.Profiles["aidite5x"].Id);
        Assert.Equal("restart", alert.Category);

        var start = new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero); // środa — poza weekendem raportu
        DowntimeEpisode bezKategorii = new("amm520-1", "520 (1)", alert.ShortText, start, start.AddMinutes(20), start.AddMinutes(20),
            EpisodeId: Guid.NewGuid());
        DowntimeEpisode zKategoria = bezKategorii with { MachineId = "amm520-2", MachineName = "520 (2)", EpisodeId = Guid.NewGuid(), Category = "tacka" };

        IReadOnlyList<DowntimeSlice> wiersze = DowntimeReport.Build([bezKategorii, zKategoria], TimeZoneInfo.Utc,
            new DowntimeCategoryClassifier(rules, cfg.Machines), now: start.AddDays(1));

        Assert.Equal("restart", Assert.Single(wiersze, w => w.MachineId == "amm520-1").Category);
        Assert.Equal("tacka", Assert.Single(wiersze, w => w.MachineId == "amm520-2").Category);
    }

    // =====================================================================================================================
    // 4. profil z jawnym "id" innym niż klucz — alarm wybiera bazę po ScreenProfile.Id,
    //    raport postojów i ostrzeżenia słownika po kluczu profilu (MachineConfig.Profile / sekcja "profiles")
    // =====================================================================================================================

    private static async Task<Alert> AlarmZPetliObserwatora(AppConfig cfg, RuleSet rules, string machineId, string odczytOcr)
    {
        MachineConfig machine = cfg.Machines.Single(m => m.Id == machineId) with { Host = "127.0.0.1", Enabled = true };
        ScreenProfile profile = cfg.Profiles[machine.Profile] with { MainScreen = null, Idle = null };
        RgbaImage frame = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, "production-aidite5x", "auto-awaria-1099.png"));
        Assert.True(new RedTextDetector(profile).AnalyzeFullFrame(frame).IsRed);
        var clock = new FakeClock();
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(odczytOcr, [odczytOcr]) };
        var source = new FakeScreenSource(machine.Id, frame);
        int cycles = 0;
        using var cts = new CancellationTokenSource();
        var monitor = new MachineMonitor(machine, profile, () => source, testOverride: null, ocr, new MessageMapper(rules), alerts, clock,
            timing, new NullCaptureStore(), new RecordingLoggerFactory().CreateLogger("MachineMonitor"),
            (interval, _) =>
            {
                clock.Advance(interval);
                if (++cycles >= 12) cts.Cancel();
                return Task.CompletedTask;
            });

        await monitor.RunAsync(cts.Token);

        return Assert.Single(alerts.ActiveAlerts);
    }

    /// <summary>
    /// config.json wczytany i sprawdzony jak przy starcie (<see cref="ConfigLoader.ParseAppConfig"/>, <c>Validate</c> konfiguracji
    /// i słownika) albo <c>null</c>, gdy któryś krok zgłosił <see cref="ConfigException"/> — odrzucenie jest dopuszczalnym wynikiem.
    /// </summary>
    private static AppConfig? PrzyjetaKonfiguracja(string json, RuleSet rules)
    {
        try
        {
            AppConfig cfg = ConfigLoader.ParseAppConfig(json);
            ConfigLoader.Validate(cfg);
            ConfigLoader.Validate(rules, cfg);
            return cfg;
        }
        catch (ConfigException)
        {
            return null;
        }
    }

    [Fact]
    public async Task TK2_05_ProfilZJawnymIdInnymNizKlucz_AlarmBierzeBazeInnegoTypu_RaportInnaBaze_BezOstrzezen()
    {
        JsonObject json = WzorcowyConfigJson();
        json["profiles"]!["premile"]!["id"] = "aidite5x";
        RuleSet rules = TestPaths.ExampleRules;
        AppConfig? cfg = PrzyjetaKonfiguracja(json.ToJsonString(), rules);
        if (cfg is null) return; // odrzucony przy wczytaniu albo walidacji
        IReadOnlyList<string> ostrzezenia = ConfigLoader.Warnings(rules, cfg);

        Alert alert = await AlarmZPetliObserwatora(cfg, rules, "premile", Falownik520);
        string raport = new DowntimeCategoryClassifier(rules, cfg.Machines).Classify("premile", alert.ShortText);
        bool bazaInnegoTypu = alert.Key.Discriminator.Contains("aidite5x", StringComparison.Ordinal);
        bool wyciszenia520 = new MessageMapper(rules).HasSuppressRules(cfg.Profiles["premile"].Id);

        Assert.True(!bazaInnegoTypu && raport == alert.Category,
            $"Premile (profil 'premile' z \"id\": \"{cfg.Profiles["premile"].Id}\"): alarm „{alert.ShortText}” = {alert.Category}, klucz " +
            $"{alert.Key.Discriminator} (baza innego typu: {bazaInnegoTypu}); raport postojów tego tekstu: {raport}; wyciszenia „bez alarmu” " +
            $"z bazy 520 działają na Premile: {wyciszenia520}; ostrzeżeń słownika: {ostrzezenia.Count}; config.json przyjęty bez błędu.");
    }

    [Fact]
    public async Task TK2_06_ProfilZJawnymIdInnymNizKlucz_OstrzezenieOPominieciuBazyKtorejUzywaAlarm()
    {
        RuleSet rules = TestPaths.ExampleRules;
        AppConfig? cfg = PrzyjetaKonfiguracja(KonfiguracjaJson(k => k == "premile" ? "premile-v2" : k, k => k == "premile" ? "premile" : null), rules);
        if (cfg is null) return; // odrzucony przy wczytaniu albo walidacji
        Assert.Equal("premile-v2", cfg.Machines.Single(m => m.Id == "premile").Profile);
        IReadOnlyList<string> ostrzezenia = ConfigLoader.Warnings(rules, cfg);

        Alert alert = await AlarmZPetliObserwatora(cfg, rules, "premile", "No.705");
        bool alarmZBazyPremile = alert.Key.Discriminator == "alarm:premile-705";
        string? ostrzezenieOPremile = ostrzezenia.FirstOrDefault(w => w.StartsWith("Baza błędów 'premile' ", StringComparison.Ordinal));

        Assert.True(!(alarmZBazyPremile && ostrzezenieOPremile is not null),
            $"Alarm Premile: klucz {alert.Key.Discriminator} — z bazy 'premile'; ostrzeżenie w logu: „{ostrzezenieOPremile}”.");
    }

    private sealed class NullCaptureStore : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

        public void Prune(TimeSpan retention)
        {
        }
    }
}
