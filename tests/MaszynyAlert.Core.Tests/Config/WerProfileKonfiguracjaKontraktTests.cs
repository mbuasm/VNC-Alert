using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerProfileKonfiguracjaKontraktTests
{
    private const string MillingFluid520 =
        "HOLD ING [1699] M023. 62 Mil ling fluid is abnormal and the mill ing has been suspended, Add";

    private const string BeforeUsing520 = "EDIT READY M024. 01 Before using machi ne. AUTO USB PROTECT 2026.09.25 68";

    private static string GoldenDir => Path.Combine(TestPaths.RepositoryRoot, "tests", "MaszynyAlert.Core.Tests", "Config",
        "OdciskProfilu-0.4.79");

    private static GroupProfile Golden(string name) => JsonSerializer.Deserialize<GroupProfile>(
        File.ReadAllBytes(Path.Combine(GoldenDir, name + ".json")), GroupFileCodec.Options)!;

    private static string GoldenHash(string name) => File.ReadAllLines(Path.Combine(GoldenDir, "odciski.txt"))
        .Select(l => l.Split(' ')).Single(p => p[0] == name)[1];

    private static RgbaImage Alarm520Frame() =>
        PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, "production-aidite5x", "auto-awaria-1099.png"));


    /// <summary>Oba pliki wzorcowe przechodzą każdą kontrolę programu bez ostrzeżeń — także jako profil pliku grupy.</summary>
    [Fact]
    public void Przyklad_ConfigIRules_LadujaSieBezBledowIBezOstrzezen()
    {
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);
        RuleSet rules = ConfigLoader.LoadRules(TestPaths.RulesExample);

        ConfigLoader.Validate(cfg);
        ConfigLoader.Validate(rules, cfg);
        Assert.Empty(ConfigLoader.Warnings(cfg));
        var mapper = new MessageMapper(rules);
        Assert.Empty(mapper.Warnings);
        Assert.Contains("cameo1: 0 kodów, 0 reguł", mapper.DatabasesSummary, StringComparison.Ordinal);
        Assert.Contains("premile: 0 kodów, 0 reguł", mapper.DatabasesSummary, StringComparison.Ordinal);
        Assert.Equal("cameo1-", rules.Databases["cameo1"].CodeIdPrefix);
        Assert.Equal("premile-", rules.Databases["premile"].CodeIdPrefix);

        // Profil grupy utworzony z przykładu przechodzi kontrolę pliku grupy.
        GroupFile file = GroupFile.Create("Hala", GroupProfile.Capture(cfg, rules, new Dictionary<string, string>()));
        Assert.Equal(file.Identity.ProfileHash, GroupFileCodec.Parse(GroupFileCodec.Serialize(file)).Identity.ProfileHash);
    }

    [Theory]
    [InlineData("premile")]
    [InlineData("cameo1")]
    public void ProfilePremileICameo1_PoleWPoleRowneAidite5x_PozaIdentyfikatoremIOpisem(string id)
    {
        ScreenProfile reference = TestPaths.Profile("aidite5x");
        ScreenProfile profile = TestPaths.Profile(id);

        Assert.Equal(id, profile.Id);
        Assert.Equal(
            JsonSerializer.Serialize(reference with { Id = "x", Description = null }, ConfigLoader.JsonOptions),
            JsonSerializer.Serialize(profile with { Id = "x", Description = null }, ConfigLoader.JsonOptions));

        Assert.Equal(new RectInt(35, 709, 954, 48), profile.StatusBarRoi);
        Assert.Equal(new RedThresholds { HueMinDeg = 340, HueMaxDeg = 70, MinSaturation = 0.55, MinValue = 0.35 }, profile.Red);
        Assert.Equal(0.02, profile.MinRedFraction);
        Assert.Equal(new RectInt(540, 402, 365, 28), profile.Idle!.ProgressRoi);
        Assert.Equal(ProgressMode.Number, profile.Idle.Mode);
        Assert.Null(profile.Idle.ProgressBar);
        Assert.Null(profile.ProgramState);
        Assert.True(profile.MainScreen!.StatusBarAlwaysVisible);
        Assert.Equal([["Tool", "Leg"], ["Spi", "ndle"], ["Tool", "NO"], ["Spindle"], ["Progress"]], profile.MainScreen.AnchorLabels);
        Assert.Empty(profile.MainScreen.OtherScreenLabels);
    }

    [Theory]
    [InlineData("premile")]
    [InlineData("cameo1")]
    public void PustaBaza_KomunikatZ520_NieDostajeOpisuZBazyAidite5x(string profileId)
    {
        var mapper = new MessageMapper(TestPaths.ExampleRules);

        MappedMessage na520 = mapper.Map(MillingFluid520, "aidite5x");
        Assert.StartsWith("aidite5x-", na520.RuleId, StringComparison.Ordinal);

        MappedMessage tutaj = mapper.Map(MillingFluid520, profileId);
        Assert.Null(tutaj.RuleId);
        Assert.False(tutaj.Matched);
        Assert.Equal("inny", tutaj.Category);
        Assert.StartsWith("Błąd: ", tutaj.ShortText, StringComparison.Ordinal);
        Assert.StartsWith("text:", tutaj.Discriminator, StringComparison.Ordinal);
        Assert.False(tutaj.Suppressed);
        Assert.False(mapper.HasSuppressRules(profileId));
    }

    [Theory]
    [InlineData("premile")]
    [InlineData("cameo1")]
    public void PustaBaza_NumerAlarmu_WlasnaPrzestrzenKluczy_BezOpisuZOpencnc(string profileId)
    {
        var mapper = new MessageMapper(TestPaths.ExampleRules);

        Assert.Equal("rule:code-705", mapper.Map("No.705 Servo 6 alarm", "opencnc").Discriminator);

        MappedMessage tutaj = mapper.Map("No.705 Servo 6 alarm", profileId);
        Assert.Null(tutaj.RuleId);
        Assert.Equal("705", tutaj.AlarmNumber);
        Assert.Equal($"alarm:{profileId}-705", tutaj.Discriminator);
        Assert.Equal("Błąd nr 705 (brak opisu w słowniku)", tutaj.ShortText);
    }

    [Theory]
    [InlineData("premile")]
    [InlineData("cameo1")]
    public void Charakterystyka_PrzypomnienieM024_NaPremileICameo1_NieJestWyciszane(string profileId)
    {
        var mapper = new MessageMapper(TestPaths.ExampleRules);

        Assert.True(mapper.Map(BeforeUsing520, "aidite5x").Suppressed);
        Assert.False(mapper.Map(BeforeUsing520, profileId).Suppressed);
        Assert.False(mapper.Map(BeforeUsing520, profileId).Ignored);
    }

    [Theory]
    [InlineData("premile")]
    [InlineData("cameo1")]
    public void RaportPostojow_TekstZBazy520PrzyMaszyniePremileICameo1_Nierozpoznany(string machineId)
    {
        var classifier = new DowntimeCategoryClassifier(TestPaths.ExampleRules, TestPaths.ExampleConfig.Machines);

        Assert.Equal("restart", classifier.Classify("amm520-1", "Falownik wrzeciona — alarm"));
        Assert.Equal(DowntimeCategories.Unrecognized, classifier.Classify(machineId, "Falownik wrzeciona — alarm"));
        Assert.Equal(DowntimeCategories.Other, classifier.Classify(machineId, "Błąd: HOLD ING [1699] M023. 62 Mil ling fluid"));
    }

    [Theory]
    [InlineData("cameo1", "text:")]
    [InlineData("premile", "text:")]
    [InlineData("aidite5x", "rule:aidite5x-")]
    public async Task Monitor_PrawdziwaKlatkaAwarii520NaProfiluMaszyny_AlarmZKoloru_OpisTylkoZWlasnejBazy(string profileId,
        string expectedDiscriminator)
    {
        ScreenProfile profile = TestPaths.Profile(profileId) with { MainScreen = null, Idle = null };
        RgbaImage frame = Alarm520Frame();
        Assert.True(new RedTextDetector(profile).AnalyzeFullFrame(frame).IsRed);

        var clock = new FakeClock();
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(MillingFluid520, [MillingFluid520]) };
        var source = new FakeScreenSource(profileId, frame);
        int cycles = 0;
        using var cts = new CancellationTokenSource();
        var monitor = new MachineMonitor(
            new MachineConfig { Id = profileId, Name = profileId, Host = "127.0.0.1", Port = 5900, Profile = profileId },
            profile, () => source, testOverride: null, ocr, new MessageMapper(TestPaths.ExampleRules), alerts, clock, timing,
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
        Assert.StartsWith(expectedDiscriminator, alert.Key.Discriminator, StringComparison.Ordinal);
    }

    [Fact]
    public void Charakterystyka_PK02_PremileWlaczonaWPaneluNaStarymConfigJson_ProfilWstepnyNieWidziAlarmuUDolu()
    {
        AppConfig stanowisko = WerProfileKonfiguracjaUwagiTests.StanowiskoSprzed0_4_249(TestPaths.ExampleConfig);
        var panel = new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(stanowisko.Machines.Select(m =>
                new KeyValuePair<string, bool>(m.Id, m.Enabled || m.Id == "premile"))),
            EditedAt = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), EditedBy = "PC-2",
        };
        AppConfig effective = panel.ApplyTo(stanowisko);
        ConfigLoader.Validate(effective);

        MachineConfig premile = effective.Machines.Single(m => m.Id == "premile");
        Assert.True(premile.Enabled);
        Assert.Equal("aidite-light2", premile.Profile);
        ScreenProfile used = effective.Profiles[premile.Profile];
        RgbaImage frame = Alarm520Frame();
        Assert.Equal(new RectInt(0, 0, 1024, 48), used.StatusBarRoi.Clamp(frame.Width, frame.Height)); // nie „poza ekranem”

        Assert.False(new RedTextDetector(used).AnalyzeFullFrame(frame).IsRed);
        Assert.True(new RedTextDetector(TestPaths.Profile("premile")).AnalyzeFullFrame(frame).IsRed);
    }

    // =====================================================================================================================
    // 2. Odcisk profilu grupy 0.4.79 a przykład z 0.4.249
    // =====================================================================================================================

    [Fact]
    public void NowaInstalacja0_4_249_DolaczonaZaproszeniemDoGrupyZ0_4_79_MaOdciskGrupy_AProfilGrupyZastepujePrzyklad()
    {
        GroupProfile hala = Golden("przyklad");
        string odcisk0479 = GoldenHash("przyklad");
        GroupFile zaproszenie = GroupFile.Create("Hala", hala);
        Assert.Equal(odcisk0479, zaproszenie.Identity.ProfileHash);

        using var dir = new TempDir("wer-pk-grupa");
        var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());
        Guid id = store.Stage(new GroupSettingsProposal(GroupFileCodec.Parse(GroupFileCodec.Serialize(zaproszenie)),
            new ClusterConfig { Priority = 57 }, enabled: true), expectedCurrent: null);
        store.Commit(id);
        GroupSettingsData data = new GroupSettingsStore(dir.Path, new InMemorySecretStore()).ReadForStartup().Data!;

        Assert.Equal(odcisk0479, data.Cluster.Group!.ProfileHash);
        AppConfig lokalnyPrzyklad = TestPaths.ExampleConfig;
        AppConfig applied = data.ApplyTo(lokalnyPrzyklad);
        Assert.Equal(["aidite-light2", "aidite5x", "amm580", "opencnc"], applied.Profiles.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("aidite-light2", applied.Machines.Single(m => m.Id == "cameo1").Profile);
        Assert.True(applied.Machines.Single(m => m.Id == "amm520-1").Enabled); // z grupy, nie z nowego przykładu
        Assert.Equal(odcisk0479, GroupFileCodec.ProfileHash(GroupProfile.Capture(applied, data.Profile.Rules, new Dictionary<string, string>())));

        var panel = new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines([new("amm520-1", false), new("amm580", true)]),
            EditedAt = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), EditedBy = "PC-A",
        };
        AppConfig effective = panel.ApplyTo(applied);
        Assert.False(effective.Machines.Single(m => m.Id == "amm520-1").Enabled);
        Assert.Equal(odcisk0479, GroupFileCodec.ProfileHash(data.Profile));
    }

    [Fact]
    public void NowaGrupaZPrzykladu0_4_249_MaInnyOdciskNizProfilZ0_4_79()
    {
        string nowy = GroupFileCodec.ProfileHash(GroupProfile.Capture(TestPaths.ExampleConfig, TestPaths.ExampleRules,
            new Dictionary<string, string>()));

        Assert.NotEqual(GoldenHash("przyklad"), nowy);
        Assert.Empty(ProfileHashSchema.AddedAfter0479);
    }


    private static AppConfig StanowiskoPc1()
    {
        AppConfig stare = WerProfileKonfiguracjaUwagiTests.StanowiskoSprzed0_4_249(TestPaths.ExampleConfig);
        return stare with
        {
            Machines = stare.Machines.Select(m => m with { Enabled = m.Id == "amm580" }).ToList(),
        };
    }

    [Fact]
    public void D3_PC1_WlaczenieDrugiej520WPanelu_DzialaMimoEnabledFalseWConfigJson_APotemConfigJsonNieMaSkutku()
    {
        AppConfig pc1 = StanowiskoPc1();
        Assert.False(pc1.Machines.Single(m => m.Id == "amm520-2").Enabled);
        var panel = new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(pc1.Machines.Select(m =>
                new KeyValuePair<string, bool>(m.Id, m.Enabled || m.Id == "amm520-2"))),
            EditedAt = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), EditedBy = "MAROAI",
        };
        Assert.True(panel.IsValid());
        Assert.Equal(12, panel.Machines!.Count);

        AppConfig effective = panel.ApplyTo(pc1);
        ConfigLoader.Validate(effective);
        MachineConfig druga = effective.Machines.Single(m => m.Id == "amm520-2");
        Assert.True(druga.Enabled);
        Assert.Equal("aidite5x", druga.Profile);
        Assert.False(druga.RequiresPassword);
        Assert.Equal(["amm520-2", "amm580"], effective.Machines.Where(m => m.Enabled).Select(m => m.Id));

        AppConfig poEdycjiPliku = pc1 with
        {
            Machines = pc1.Machines.Select(m => m.Id == "amm520-1" ? m with { Enabled = true } : m).ToList(),
        };
        Assert.False(panel.ApplyTo(poEdycjiPliku).Machines.Single(m => m.Id == "amm520-1").Enabled);
    }

    [Fact]
    public async Task Charakterystyka_PK03_GrupaNaStarymProfilu_WlaczeniePremileWPanelu_ZostawiaGrupeBezObserwatora()
    {
        GroupProfile hala = Golden("przyklad");
        GroupFile file = GroupFile.Create("Hala", hala);
        var clock = new FakeClock();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "0.4.249");
        await using var b = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B"), clock, "0.4.249");
        AppConfig grupa = hala.ApplyTo(TestPaths.ExampleConfig) with { Cluster = config };
        static bool MaHaslo(string id) => id.StartsWith("aidite", StringComparison.Ordinal);

        WatcherReadiness przed = WatcherReadiness.Evaluate(grupa, MaHaslo, true, GroupCompatibility.Compatible);
        Assert.True(przed.CanObserve);
        a.SetReadiness(przed); b.SetReadiness(przed);
        await a.StartAsync(default); await b.StartAsync(default);
        for (int s = 0; s < 25; s++) { await a.TickAsync(default); await b.TickAsync(default); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.True(a.IsWatcher);
        Assert.False(b.NoWatcher);

        var panel = new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(grupa.Machines.Select(m =>
                new KeyValuePair<string, bool>(m.Id, m.Enabled || m.Id == "premile"))),
            EditedAt = clock.UtcNow, EditedBy = "A",
        };
        AppConfig poPrzelaczeniu = panel.ApplyTo(grupa);
        Assert.Equal(GroupFileCodec.ProfileHash(hala), file.Identity.ProfileHash);
        WatcherReadiness po = WatcherReadiness.Evaluate(poPrzelaczeniu, MaHaslo, true, GroupCompatibility.Compatible);
        Assert.False(po.CanObserve);
        Assert.Equal(["premile"], po.MissingPasswordMachineIds);

        a.SetReadiness(po); b.SetReadiness(po);
        for (int s = 0; s < 5; s++) { await a.TickAsync(default); await b.TickAsync(default); clock.Advance(TimeSpan.FromSeconds(1)); }

        Assert.False(a.IsWatcher);
        Assert.False(b.IsWatcher);
        Assert.True(a.NoWatcher);
        Assert.True(b.NoWatcher);
        Assert.Equal("ten komputer nie jest gotowy do obserwacji maszyn", a.LastRoleChangeReason);
    }

    [Theory]
    [InlineData("opencnc", 1)]
    [InlineData("aidite5x", 0)]
    public async Task Charakterystyka_PK03_PodgladBezMonitora_520NaProfiluZGrupyHali_OtwieraDrugiePolaczenie(string profil,
        int oczekiwanePolaczenia)
    {
        AppConfig grupa = TestPaths.ExampleConfig with
        {
            Machines = [.. TestPaths.ExampleConfig.Machines.Select(m => m.Id == "amm520-1" ? m with { Profile = profil, Enabled = true } : m)],
        };
        int polaczenia = 0;
        var service = new MachinePreviewService(() => grupa, _ => null, (_, machine) =>
        {
            polaczenia++;
            return new FakeScreenSource(machine.Id, Alarm520Frame());
        }, new FakeClock(), _ => null);

        Exception? blad = await Record.ExceptionAsync(() => service.CaptureAsync("amm520-1", CancellationToken.None));

        Assert.Equal(oczekiwanePolaczenia, polaczenia);
        if (oczekiwanePolaczenia == 0)
            Assert.Equal(MachinePreviewService.QtPreviewUnavailable, Assert.IsType<InvalidOperationException>(blad).Message);
        else
            Assert.Null(blad);
    }


    [Fact]
    public void Gotowosc_MaszynyBezHaslaWlaczoneJakNaStanowisku520_NieBlokujaObserwacjiINieSaSprawdzaneWSekretach()
    {
        AppConfig config = TestPaths.ExampleConfig with
        {
            Machines = [.. TestPaths.ExampleConfig.Machines.Select(m => m.RequiresPassword ? m : m with { Enabled = true })],
        };
        string[] bezHasla = config.Machines.Where(m => m.Enabled && !m.RequiresPassword).Select(m => m.Id).ToArray();
        Assert.Equal(["amm520-1", "amm520-2", "premile", "cameo1"], bezHasla);

        var pytane = new List<string>();
        WatcherReadiness wszystkie = WatcherReadiness.Evaluate(config, id => { pytane.Add(id); return true; }, true,
            GroupCompatibility.Compatible);
        Assert.True(wszystkie.CanObserve);
        Assert.Empty(pytane.Intersect(bezHasla));
        Assert.Equal(config.Machines.Where(m => m.Enabled && m.RequiresPassword).Select(m => m.Id), pytane);

        AppConfig tylkoQt = config with { Machines = [.. config.Machines.Select(m => m with { Enabled = !m.RequiresPassword })] };
        WatcherReadiness qt = WatcherReadiness.Evaluate(tylkoQt, _ => false, true, GroupCompatibility.Compatible);
        Assert.True(qt.CanObserve);
        Assert.Empty(qt.MissingPasswordMachineIds);
    }

    private sealed class NullCaptureStore : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

        public void Prune(TimeSpan retention)
        {
        }
    }
}
