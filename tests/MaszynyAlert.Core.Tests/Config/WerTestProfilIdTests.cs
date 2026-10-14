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

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerTestProfilIdTests
{
    private static readonly JsonDocumentOptions KomentarzeIPrzecinki = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const string ToolLife580 = "{T4} Tool life was reached. Please change the tool.";

    private static string GoldenDir => Path.Combine(TestPaths.RepositoryRoot, "tests", "MaszynyAlert.Core.Tests", "Config",
        "OdciskProfilu-0.4.79");

    private static JsonObject Wzorzec() =>
        JsonNode.Parse(File.ReadAllText(TestPaths.ConfigExample), documentOptions: KomentarzeIPrzecinki)!.AsObject();

    /// <summary>Słownik znaczników: baza każdego profilu ma kod 705 „Opis &lt;klucz&gt;” kategorii „kat-&lt;klucz&gt;”.</summary>
    private static RuleSet Znaczniki(IEnumerable<string> klucze)
    {
        var bazy = new JsonObject();
        foreach (string k in klucze)
        {
            bazy[k] = new JsonObject
            {
                ["codeIdPrefix"] = k + "-",
                ["codes"] = new JsonArray(new JsonObject { ["code"] = 705, ["shortText"] = $"Opis {k}", ["category"] = $"kat-{k}" }),
                ["rules"] = new JsonArray(),
            };
        }

        return ConfigLoader.ParseRules(new JsonObject { ["databases"] = bazy }.ToJsonString());
    }

    /// <summary>
    /// Każda maszyna (także wyłączona): alarm na żywo — <c>Map("No.705", cfg.Profiles[maszyna.Profile].Id)</c>, tak jak profil trafia do
    /// <see cref="MachineMonitor"/> w <c>MonitorSupervisor.StartAsync</c> — i raport postojów (<see cref="DowntimeCategoryClassifier"/>)
    /// wskazują bazę KLUCZA profilu maszyny; ostrzeżenia słownika puste. Zwraca listę rozbieżności (pusta = OK).
    /// </summary>
    private static List<string> RozbieznosciBaz(AppConfig cfg)
    {
        RuleSet rules = Znaczniki(cfg.Profiles.Keys);
        ConfigLoader.Validate(cfg);
        ConfigLoader.Validate(rules, cfg);
        var rozne = new List<string>();
        if (ConfigLoader.Warnings(rules, cfg).Count != 0) rozne.Add("ostrzeżenia słownika: " + string.Join(" | ", ConfigLoader.Warnings(rules, cfg)));
        var mapper = new MessageMapper(rules);
        var klasyfikator = new DowntimeCategoryClassifier(rules, cfg.Machines);
        foreach (MachineConfig m in cfg.Machines)
        {
            string id = cfg.Profiles[m.Profile].Id;
            MappedMessage alarm = mapper.Map("No.705", id);
            string raport = klasyfikator.Classify(m.Id, $"Opis {m.Profile}");
            if (id != m.Profile || alarm.ShortText != $"Opis {m.Profile}" || raport != $"kat-{m.Profile}"
                || alarm.CodeNamespace != m.Profile + "-")
                rozne.Add($"{m.Id} (profil '{m.Profile}', Id '{id}'): alarm „{alarm.ShortText}” ({alarm.CodeNamespace}), raport „{raport}”");
        }

        return rozne;
    }

    // =====================================================================================================================
    // Nazwa pola "id" w innej pisowni i wartości z białymi znakami
    // =====================================================================================================================

    /// <summary>
    /// Opcje JSON konfiguracji nie rozróżniają wielkości liter w nazwach pól (<c>PropertyNameCaseInsensitive</c>), więc
    /// <c>"ID"</c>/<c>"Id"</c>/<c>"iD"</c> to to samo pole co <c>"id"</c> (Id = klucz, jedno ostrzeżenie).
    /// Wartość z samych spacji albo z tabulatorem na końcu jest „niepusta” i inna niż klucz — ostrzeżenie, Id = klucz. <c>"ID"</c> równe
    /// kluczowi — bez ostrzeżenia. W każdym wariancie cała konfiguracja = przykład, a alarm/raport/ostrzeżenia biorą bazę klucza.
    /// </summary>
    [Theory]
    [InlineData("ID", "aidite5x", true)]
    [InlineData("Id", "aidite5x", true)]
    [InlineData("iD", "cameo1", true)]
    [InlineData("id", "   ", true)]
    [InlineData("id", "premile\t", true)]
    [InlineData("ID", "premile", false)]
    public void TP01_PoleIdWInnejPisowniLubZBialymiZnakami_IdRowneKluczowi_TaSamaBazaWszedzie(string nazwaPola, string wartosc, bool ostrzezenie)
    {
        JsonObject json = Wzorzec();
        JsonObject premile = json["profiles"]!["premile"]!.AsObject();
        premile.Remove("id");
        premile[nazwaPola] = wartosc;
        var ostrzezenia = new List<string>();

        AppConfig cfg = ConfigLoader.ParseAppConfig(json.ToJsonString(), ostrzezenia);

        Assert.Equal("premile", cfg.Profiles["premile"].Id);
        if (ostrzezenie) Assert.Equal(ConfigLoader.ProfileIdWarning("premile", wartosc), Assert.Single(ostrzezenia));
        else Assert.Empty(ostrzezenia);
        Assert.Equal(ConfigLoader.Serialize(TestPaths.ExampleConfig), ConfigLoader.Serialize(cfg));
        List<string> rozne = RozbieznosciBaz(cfg);
        Assert.True(rozne.Count == 0, string.Join("\n", rozne));
    }


    [Theory]
    [InlineData(null)]
    [InlineData("AMM580")]
    [InlineData("aidite5x")]
    [InlineData("amm580-stary")]
    public async Task TP02_580ZPolemIdInnymNizKlucz_IdleAbortKluczRuleAmm580_ToolLifeWyciszone(string? poleId)
    {
        JsonObject json = Wzorzec();
        if (poleId is not null) json["profiles"]!["amm580"]!["id"] = poleId;
        AppConfig cfg = ConfigLoader.ParseAppConfig(json.ToJsonString());
        RuleSet rules = TestPaths.ExampleRules;
        ConfigLoader.Validate(cfg);
        ConfigLoader.Validate(rules, cfg);

        MachineConfig machine = cfg.Machines.Single(m => m.Id == "amm580") with { Host = "127.0.0.1", Enabled = true };
        ScreenProfile profile = cfg.Profiles[machine.Profile] with { MainScreen = null };
        RgbaImage frame = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, "ocr-580-postep", "580-2026-09-25-104520-idle-abort-25.png"));
        var clock = new FakeClock();
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult("Progress:", ["Progress:"]) };
        var source = new FakeScreenSource(machine.Id, frame);
        int cykle = 0;
        using var cts = new CancellationTokenSource();
        var monitor = new MachineMonitor(machine, profile, () => source, testOverride: null, ocr, new MessageMapper(rules), alerts, clock,
            timing, new NullCaptureStore(), logger: null,
            (interval, _) =>
            {
                clock.Advance(interval);
                if (++cykle >= 5) cts.Cancel();
                return Task.CompletedTask;
            });

        await monitor.RunAsync(cts.Token);

        Assert.True(monitor.Snapshot.ProgramAborted);
        Alert alert = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal("rule:amm580" + AlarmHelp.ProgramAbortRuleSuffix, alert.Key.Discriminator);
        Assert.Contains("BreakPoint Resume", AlarmHelp.For(alert), StringComparison.Ordinal);
        Assert.Equal(alert.Category, new DowntimeCategoryClassifier(rules, cfg.Machines).Classify("amm580", alert.ShortText));

        var mapper = new MessageMapper(rules);
        Assert.True(mapper.Map(ToolLife580, profile.Id).Suppressed);
        Assert.True(mapper.HasSuppressRules(profile.Id));
    }

    // =====================================================================================================================
    // Grupa komputerów: zaproszenie z config.json z obcymi "id", zapisana generacja, profil grupy na lokalnym config.json
    // =====================================================================================================================

    /// <summary>KAŻDY profil przykładu z polem <c>"id"</c> następnego profilu w pliku (rotacja) — najgorszy przypadek kopiowania bloków.</summary>
    private static string ConfigZRotacjaId()
    {
        JsonObject json = Wzorzec();
        JsonObject profile = json["profiles"]!.AsObject();
        string[] klucze = [.. profile.Select(p => p.Key)];
        for (int i = 0; i < klucze.Length; i++) profile[klucze[i]]!["id"] = klucze[(i + 1) % klucze.Length];
        return json.ToJsonString();
    }

    /// <summary>
    /// Kreator grupy na stanowisku z config.json, w którym każdy profil ma obce <c>"id"</c>: zaproszenie powstaje, ma odcisk identyczny z zaproszeniem z czystego przykładu, a zapisana generacja ma w KAŻDYM profilu
    /// <c>"id"</c> = klucz (plik czytelny także dla 0.4.134/0.4.148 — <c>GroupFileCodec</c> wymaga równości). Start w grupie: profil grupy
    /// nałożony na lokalny config.json z obcymi <c>"id"</c> daje Id = klucz w każdym profilu i jedną bazę na maszynę (alarm = raport).
    /// Generacja zepsuta ręcznie (<c>"id"</c> ≠ klucz) nie przechodzi kontroli — <c>Program.RunInstance</c> przechodzi wtedy w obserwację
    /// lokalną (lokalny config.json, także znormalizowany).
    /// </summary>
    [Fact]
    public void TP03_Grupa_ZaproszenieZConfigJsonZObcymiId_GeneracjaZIdRownymKluczowi_StartWGrupieJednaBaza()
    {
        var ostrzezenia = new List<string>();
        AppConfig lokalny = ConfigLoader.ParseAppConfig(ConfigZRotacjaId(), ostrzezenia);
        Assert.Equal(lokalny.Profiles.Count, ostrzezenia.Count);
        var instrukcje = new Dictionary<string, string>();
        GroupFile zaproszenie = GroupFile.Create("Hala", GroupProfile.Capture(lokalny, TestPaths.ExampleRules, instrukcje));
        GroupFile wzorcowe = GroupFile.Create("Hala", GroupProfile.Capture(TestPaths.ExampleConfig, TestPaths.ExampleRules, instrukcje));
        Assert.Equal(wzorcowe.Identity.ProfileHash, zaproszenie.Identity.ProfileHash);

        using var dir = new TempDir("wer-profil-id-grupa");
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        Guid gen = store.Stage(new GroupSettingsProposal(zaproszenie, new ClusterConfig { Priority = 57 }, enabled: true), expectedCurrent: null);
        store.Commit(gen);

        string plik = Directory.GetFiles(dir.Path, "generation-*.json").Single();
        JsonObject naDysku = JsonNode.Parse(File.ReadAllText(plik))!["data"]!["profile"]!["profiles"]!.AsObject();
        Assert.All(naDysku, p => Assert.Equal(p.Key, p.Value!["id"]!.GetValue<string>()));

        GroupSettingsData committed = new GroupSettingsStore(dir.Path, secrets).ReadForStartup().Data!;
        AppConfig wGrupie = committed.ApplyTo(lokalny);
        Assert.All(wGrupie.Profiles, p => Assert.Equal(p.Key, p.Value.Id));
        List<string> rozne = RozbieznosciBaz(wGrupie);
        Assert.True(rozne.Count == 0, string.Join("\n", rozne));

        JsonNode generacja = JsonNode.Parse(File.ReadAllText(plik))!;
        generacja["data"]!["profile"]!["profiles"]!["premile"]!["id"] = "aidite5x";
        File.WriteAllText(plik, generacja.ToJsonString());
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => new GroupSettingsStore(dir.Path, secrets).ReadForStartup());
        Assert.Contains("profil ekranu ma zły identyfikator", ex.Message, StringComparison.Ordinal);
    }

    // =====================================================================================================================
    // Złoty odcisk 0.4.79 przy obcym "id" w KAŻDYM profilu
    // =====================================================================================================================

    [Theory]
    [MemberData(nameof(ProfileHashCompatibilityTests.GroupFileProfiles), MemberType = typeof(ProfileHashCompatibilityTests))]
    public void TP04_OdciskJak0_4_79_GdyKazdyProfilMaObceId(string fixture, string hash0479)
    {
        GroupProfile hala = JsonSerializer.Deserialize<GroupProfile>(
            File.ReadAllBytes(Path.Combine(GoldenDir, fixture + ".json")), GroupFileCodec.Options)!;
        string zapis = ConfigLoader.Serialize(hala.ApplyTo(new AppConfig()));
        string[] klucze = [.. hala.Profiles.Keys];
        var warianty = new Dictionary<string, Action<string, JsonObject>>
        {
            ["wielkie-litery"] = (k, p) => p["id"] = k.ToUpperInvariant() == k ? k + "-X" : k.ToUpperInvariant(),
            ["pole-ID-inny-profil"] = (k, p) => { p.Remove("id"); p["ID"] = klucze[(Array.IndexOf(klucze, k) + 1) % klucze.Length] + (klucze.Length == 1 ? "-inny" : ""); },
            ["spacja-na-poczatku"] = (k, p) => p["id"] = " " + k,
        };

        foreach ((string nazwa, Action<string, JsonObject> zmiana) in warianty)
        {
            JsonObject json = JsonNode.Parse(zapis)!.AsObject();
            foreach ((string k, JsonNode? p) in json["profiles"]!.AsObject()) zmiana(k, p!.AsObject());
            var ostrzezenia = new List<string>();
            AppConfig cfg = ConfigLoader.ParseAppConfig(json.ToJsonString(), ostrzezenia);

            Assert.True(klucze.Length == ostrzezenia.Count, $"{fixture}, {nazwa}: ostrzeżeń {ostrzezenia.Count}, profili {klucze.Length}.");
            GroupProfile przechwycony = GroupProfile.Capture(cfg, hala.Rules, hala.Instructions);
            Assert.True(hash0479 == GroupFileCodec.ProfileHash(przechwycony), $"{fixture}, {nazwa}: odcisk inny niż 0.4.79.");
        }
    }

    // =====================================================================================================================
    // Lista ostrzeżeń: dopisywanie, kolejność, plik = tekst
    // =====================================================================================================================

    /// <summary>
    /// Przeciążenia z listą DOPISUJĄ do niej (nie czyszczą wpisów wywołującego), w kolejności profili w pliku; wczytanie z pliku daje
    /// tę samą konfigurację i te same ostrzeżenia co parsowanie tekstu; pusta sekcja <c>"profiles"</c> nie rzuca przy wczytaniu (o braku
    /// profilu maszyny decyduje dalej <see cref="ConfigLoader.Validate(AppConfig)"/>).
    /// </summary>
    [Fact]
    public void TP05_ListaOstrzezen_DopisywanaWKolejnosciProfili_PlikJakTekst()
    {
        JsonObject json = Wzorzec();
        json["profiles"]!["cameo1"]!["id"] = "x1";
        json["profiles"]!["opencnc"]!["id"] = "x2";
        string tekst = json.ToJsonString();
        string[] kolejnosc = [.. json["profiles"]!.AsObject().Select(p => p.Key).Where(k => k is "cameo1" or "opencnc")];

        var lista = new List<string> { "wpis wywołującego" };
        AppConfig zTekstu = ConfigLoader.ParseAppConfig(tekst, lista);
        Assert.Equal(3, lista.Count);
        Assert.Equal("wpis wywołującego", lista[0]);
        Assert.StartsWith($"Profil ekranu '{kolejnosc[0]}'", lista[1], StringComparison.Ordinal);
        Assert.StartsWith($"Profil ekranu '{kolejnosc[1]}'", lista[2], StringComparison.Ordinal);

        using var dir = new TempDir("wer-profil-id-plik");
        string sciezka = dir.File("config.json");
        File.WriteAllText(sciezka, tekst);
        var zPliku = new List<string>();
        AppConfig wczytany = ConfigLoader.LoadAppConfig(sciezka, zPliku);
        Assert.Equal(lista.Skip(1), zPliku);
        Assert.Equal(ConfigLoader.Serialize(zTekstu), ConfigLoader.Serialize(wczytany));

        JsonObject pusty = Wzorzec();
        pusty["profiles"] = new JsonObject();
        var bezProfili = new List<string>();
        AppConfig cfg = ConfigLoader.ParseAppConfig(pusty.ToJsonString(), bezProfili);
        Assert.Empty(bezProfili);
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
    }

    private sealed class NullCaptureStore : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

        public void Prune(TimeSpan retention)
        {
        }
    }
}
