using System.Text.Json;
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

public sealed class BazaBledowBezProfiluTests
{
    private const string MillingFluid520 =
        "HOLD ING [1699] M023. 62 Mil ling fluid is abnormal and the mill ing has been suspended, Add";

    private const string BeforeUsing520 = "EDIT READY M024. 01 Before using machi ne. AUTO USB PROTECT 2026.09.25 68";

    private static string GoldenDir => Path.Combine(TestPaths.RepositoryRoot, "tests", "MaszynyAlert.Core.Tests", "Config",
        "OdciskProfilu-0.4.79");

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

    /// <summary>Wzorcowy słownik z literówką w kluczu jednej bazy (kopia — współdzielony <see cref="TestPaths.ExampleRules"/> zostaje nietknięty).</summary>
    private static RuleSet ZLiterowkaWNazwieBazy(string poprawna, string zLiterowka) => TestPaths.ExampleRules with
    {
        Databases = new Dictionary<string, RuleDatabase>(
            TestPaths.ExampleRules.Databases.Select(p => p.Key == poprawna ? new KeyValuePair<string, RuleDatabase>(zLiterowka, p.Value) : p),
            StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void StanowiskoSprzed0_4_249_ZWzorcowymRulesJson_StartujeZOstrzezeniemZNazwaKazdejBazyBezProfilu()
    {
        AppConfig stanowisko = StanowiskoSprzed0_4_249();
        ConfigLoader.Validate(stanowisko);

        ConfigLoader.Validate(TestPaths.ExampleRules, stanowisko);
        IReadOnlyList<string> warnings = ConfigLoader.Warnings(TestPaths.ExampleRules, stanowisko);

        Assert.Collection(warnings,
            w => Assert.StartsWith("Baza błędów 'cameo1' ", w, StringComparison.Ordinal),
            w => Assert.StartsWith("Baza błędów 'premile' ", w, StringComparison.Ordinal));
        Assert.All(warnings, w =>
        {
            Assert.Contains("program ją pomija i działa dalej", w, StringComparison.Ordinal);
            Assert.Contains("alarm z koloru działa bez zmian", w, StringComparison.Ordinal);
            Assert.Contains("profil przenieś z nowszego config.example.json", w, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(warnings, w => w.Contains("'opencnc'", StringComparison.Ordinal)
            || w.Contains("'aidite5x'", StringComparison.Ordinal) || w.Contains("'amm580'", StringComparison.Ordinal));
    }

    /// <summary>Pliki wzorcowe są spójne: każda baza ma swój profil, więc słownik nie daje żadnego ostrzeżenia.</summary>
    [Fact]
    public void Przyklad_ConfigIRules_SlownikBezOstrzezen()
    {
        ConfigLoader.Validate(TestPaths.ExampleRules, TestPaths.ExampleConfig);

        Assert.Empty(ConfigLoader.Warnings(TestPaths.ExampleRules, TestPaths.ExampleConfig));
        Assert.Empty(ConfigLoader.Warnings(TestPaths.ExampleConfig));
    }

    [Fact]
    public async Task BazaZLiterowka_MaszynaBezBazy_AlarmZKoloruZOpisemNierozpoznanym_NieCisza()
    {
        RuleSet literowka = ZLiterowkaWNazwieBazy("aidite5x", "aidte5x");
        AppConfig cfg = TestPaths.ExampleConfig with
        {
            Machines = [.. TestPaths.ExampleConfig.Machines.Select(m => m.Id == "amm520-1" ? m with { Enabled = true } : m)],
        };

        ConfigLoader.Validate(literowka, cfg);
        string warning = Assert.Single(ConfigLoader.Warnings(literowka, cfg));
        Assert.StartsWith("Baza błędów 'aidte5x' ", warning, StringComparison.Ordinal);

        var mapper = new MessageMapper(literowka);
        MappedMessage opis = mapper.Map(MillingFluid520, "aidite5x");
        Assert.Null(opis.RuleId);
        Assert.False(opis.Matched);
        Assert.StartsWith("Błąd: ", opis.ShortText, StringComparison.Ordinal);
        Assert.False(mapper.HasSuppressRules("aidite5x"));
        Assert.False(mapper.Map(BeforeUsing520, "aidite5x").Suppressed);
        Assert.True(new MessageMapper(TestPaths.ExampleRules).Map(BeforeUsing520, "aidite5x").Suppressed); // kontrola: bez literówki wyciszone

        ScreenProfile profile = cfg.Profiles["aidite5x"] with { MainScreen = null, Idle = null };
        RgbaImage frame = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, "production-aidite5x", "auto-awaria-1099.png"));
        Assert.True(new RedTextDetector(profile).AnalyzeFullFrame(frame).IsRed);
        var clock = new FakeClock();
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(MillingFluid520, [MillingFluid520]) };
        var source = new FakeScreenSource("amm520-1", frame);
        int cycles = 0;
        using var cts = new CancellationTokenSource();
        MachineConfig machine = cfg.Machines.Single(m => m.Id == "amm520-1") with { Host = "127.0.0.1" };
        var monitor = new MachineMonitor(machine, profile, () => source, testOverride: null, ocr, mapper, alerts, clock, timing,
            new NullCaptureStore(), new RecordingLoggerFactory().CreateLogger("MachineMonitor"),
            (interval, _) =>
            {
                clock.Advance(interval);
                if (++cycles >= 12) cts.Cancel();
                return Task.CompletedTask;
            });

        await monitor.RunAsync(cts.Token);

        Alert alert = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal(1, events.Count(e => e.Kind == AlertEventKind.Raised));
        Assert.Equal("amm520-1", alert.MachineId);
        Assert.StartsWith("text:", alert.Key.Discriminator, StringComparison.Ordinal);
        Assert.StartsWith("Błąd: ", alert.ShortText, StringComparison.Ordinal);

        Assert.Equal("restart",
            new DowntimeCategoryClassifier(TestPaths.ExampleRules, cfg.Machines).Classify("amm520-1", "Falownik wrzeciona — alarm"));
        Assert.Equal(DowntimeCategories.Unrecognized,
            new DowntimeCategoryClassifier(literowka, cfg.Machines).Classify("amm520-1", "Falownik wrzeciona — alarm"));
    }

    /// <summary>
    /// „Bez profilu” = bez profilu, którego bazę wybrałby <c>MessageMapper</c> (porównanie bez wielkości liter): baza różniąca się od
    /// profilu tylko wielkością liter JEST używana, więc nie dostaje ostrzeżenia „pomija”.
    /// </summary>
    [Fact]
    public void BazaRoznaOdProfiluTylkoWielkosciaLiter_JestUzywana_IBezOstrzezenia()
    {
        var rules = new RuleSet
        {
            Databases =
            {
                ["AIDITE5X"] = new RuleDatabase
                {
                    CodeIdPrefix = "aidite5x-",
                    Codes = [new AlarmCode { Code = 705, ShortText = "Numer z bazy 520", Category = "inny" }],
                },
            },
        };

        ConfigLoader.Validate(rules, TestPaths.ExampleConfig);

        Assert.Empty(ConfigLoader.Warnings(rules, TestPaths.ExampleConfig));
        Assert.Equal("Numer z bazy 520", new MessageMapper(rules).Map("No.705", "aidite5x").ShortText);
    }

    /// <summary>
    /// Złagodzenie dotyczy wyłącznie dopasowania klucza do profilu: baza bez profilu ze złą BUDOWĄ dalej zatrzymuje start
    /// z dotychczasowym komunikatem (null, sekcje null, zły <c>codePattern</c>, powtórzony przedrostek, reguła z <c>phrases: null</c>).
    /// </summary>
    [Theory]
    [InlineData("""{ "databases": { "literowka": null } }""", "jest pusta (null)")]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "codes": null } } }""", "'codes' albo 'rules' o wartości null")]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "rules": null } } }""", "'codes' albo 'rules' o wartości null")]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "codePattern": "(niedomkniety" } } }""", "'codePattern'")]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "amm580-" }, "amm580": { "codeIdPrefix": "amm580-" } } }""", "powtarza się")]
    [InlineData("""{ "databases": { "literowka": { "codeIdPrefix": "literowka-", "rules": [ { "id": "r1", "phrases": null, "shortText": "A", "category": "inny" } ] } } }""", "'phrases'")]
    public void BazaBezProfilu_ZBlednaBudowa_NadalZatrzymujeStart(string json, string fragment)
    {
        RuleSet rules = ConfigLoader.ParseRules(json);

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, TestPaths.ExampleConfig));

        Assert.Contains("literowka", ex.Message, StringComparison.Ordinal);
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Grupa_LokalnyRulesJsonZBazamiBezProfilu_NieZatrzymujeStartu_ASlownikZGrupyBezOstrzezen()
    {
        AppConfig lokalny = StanowiskoSprzed0_4_249();
        ConfigLoader.Validate(TestPaths.ExampleRules, lokalny);
        Assert.Equal(2, ConfigLoader.Warnings(TestPaths.ExampleRules, lokalny).Count);

        GroupProfile hala = JsonSerializer.Deserialize<GroupProfile>(
            File.ReadAllBytes(Path.Combine(GoldenDir, "przyklad.json")), GroupFileCodec.Options)!;
        AppConfig efektywny = hala.ApplyTo(lokalny);

        ConfigLoader.Validate(hala.Rules, efektywny);
        Assert.Empty(ConfigLoader.Warnings(hala.Rules, efektywny));
    }

    /// <summary>
    /// Plik grupy (zaproszenie, nowa generacja) z bazą bez profilu przechodzi tę samą kontrolę co lokalny start — kreator grupy na
    /// takim stanowisku nie kończy się „profil nie przechodzi kontroli ustawień”. Odcisk po zapisie i odczycie bez zmian; baza bez profilu
    /// zostaje w pliku i daje ostrzeżenie na każdym komputerze, który go zastosuje.
    /// </summary>
    [Fact]
    public void PlikGrupy_ZBazaBezProfilu_PrzechodziKontrole_AOstrzezenieZostaje()
    {
        GroupFile file = GroupFile.Create("Hala",
            GroupProfile.Capture(StanowiskoSprzed0_4_249(), TestPaths.ExampleRules, new Dictionary<string, string>()));

        GroupFile parsed = GroupFileCodec.Parse(GroupFileCodec.Serialize(file));

        Assert.Equal(file.Identity.ProfileHash, parsed.Identity.ProfileHash);
        Assert.Equal(2, ConfigLoader.Warnings(parsed.Profile.Rules, parsed.Profile.ApplyTo(new AppConfig())).Count);
    }

    /// <summary>
    /// Okablowanie w <c>Program.RunInstance</c> (testu procesu nie ma — kod startu wymaga plików w ProgramData): walidacja budowy
    /// lokalnego słownika zostaje, ostrzeżenia słownika liczone są PO nałożeniu profilu grupy (dla słownika, którego program użyje)
    /// i trafiają do logu tą samą pętlą co ostrzeżenia konfiguracji.
    /// </summary>
    [Fact]
    public void Program_OstrzezeniaSlownikaPoNalozeniuProfiluGrupy_TrafiajaDoLogu()
    {
        string program = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "MaszynyAlert.App", "Program.cs"));
        int start = program.IndexOf("private static void RunInstance", StringComparison.Ordinal);
        int end = program.IndexOf("private static int RunStartupSmokeTest", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "Nie znaleziono metody RunInstance w Program.cs.");
        string run = program[start..end];

        int validate = run.IndexOf("ConfigLoader.Validate(ruleSet, appConfig);", StringComparison.Ordinal);
        int group = run.IndexOf("ruleSet = committed.Profile.Rules;", StringComparison.Ordinal);
        int warnings = run.IndexOf("ConfigLoader.Warnings(ruleSet, appConfig)", StringComparison.Ordinal);
        int logged = run.IndexOf("foreach (string warning in configWarnings) Log.Warning(", StringComparison.Ordinal);

        Assert.True(validate > 0, "Walidacja budowy lokalnego słownika (ConfigLoader.Validate(ruleSet, appConfig)) zniknęła ze startu.");
        Assert.True(group > validate, "Słownik z profilu grupy ma zastępować lokalny po walidacji.");
        Assert.True(warnings > group, "Ostrzeżenia słownika mają być liczone dla słownika po nałożeniu profilu grupy.");
        Assert.True(logged > warnings, "Ostrzeżenia słownika mają trafiać do logu (Log.Warning) razem z ostrzeżeniami konfiguracji.");
        Assert.Contains("configWarnings = [.. ConfigLoader.Warnings(appConfig), .. ConfigLoader.Warnings(ruleSet, appConfig)]", run,
            StringComparison.Ordinal);
    }

    private sealed class NullCaptureStore : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

        public void Prune(TimeSpan retention)
        {
        }
    }
}
