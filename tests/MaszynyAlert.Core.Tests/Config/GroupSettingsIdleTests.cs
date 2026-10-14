using System.Text;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.Cluster;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class GroupSettingsIdleTests
{
    private static readonly RuleSet Rules = TestPaths.ExampleRules;

    private static GroupFile Invitation(AppConfig config, RuleSet? rules = null) =>
        GroupFile.Create("Hala — Aidite", GroupProfile.Capture(config, rules ?? Rules, new Dictionary<string, string>()));

    // --- 1. Kiedy i czym warstwa grupy nadpisuje config.json ----------------------------------------------

    [Fact]
    public void ZatwierdzonaGeneracja_NadpisujeClusterEnabledFalseZConfigJson()
    {
        // Dlaczego: Program.cs stosuje zatwierdzoną generację z group-settings\ ZAWSZE, gdy istnieje —
        // `cluster.enabled:false` w config.json niczego tu nie blokuje, bo sekcja cluster też pochodzi
        // z generacji. Kreator zapisuje tylko generacje z enabled:true i nie ma ścieżki „wyjdź z grupy”.
        var local = TestPaths.ExampleConfig;
        Assert.False(local.Cluster.Enabled);
        var data = new GroupSettingsProposal(Invitation(local), local.Cluster, enabled: true).GetData();

        var applied = data.ApplyTo(local);

        Assert.True(applied.Cluster.Enabled);
        Assert.NotNull(applied.Cluster.Group);
    }

    [Fact]
    public void GeneracjaZWylaczonaGrupa_NadalZastepujeMaszynyProfileEkranuTimingIOcr()
    {
        // Dlaczego: nawet generacja „grupa wyłączona” niesie cały profil grupy — maszyny, profile ekranu,
        // timing i OCR z config.json przestają działać, póki generacja istnieje.
        var local = TestPaths.ExampleConfig;
        var groupConfig = local with
        {
            Timing = local.Timing with { IdleAtZeroAfterMs = 45000, IdleAlertAfterMs = 20000 },
            Ocr = local.Ocr with { Language = "de-DE" },
        };
        var data = new GroupSettingsProposal(Invitation(groupConfig), local.Cluster, enabled: false).GetData();

        var applied = data.ApplyTo(local);

        Assert.False(applied.Cluster.Enabled);
        Assert.Equal(45000, applied.Timing.IdleAtZeroAfterMs);
        Assert.Equal(20000, applied.Timing.IdleAlertAfterMs);
        Assert.Equal("de-DE", applied.Ocr.Language);
    }

    [Fact]
    public void ProfilGrupy_ZastepujeSekcjePostojuProfiluEkranuIMapowanieMaszyn()
    {
        // Dlaczego: obszar liczby procentu, skala OCR, odwrócenie jasności i tryb odczytu leżą w profilu
        // ekranu, a ten w całości pochodzi z grupy — lokalna poprawka obszaru w config.json nie działa.
        var local = TestPaths.ExampleConfig;
        var groupIdle = local.Profiles["aidite5x"].Idle! with
        {
            ProgressRoi = new RectInt(10, 20, 100, 30), OcrScale = 5, InvertForOcr = true, FullAtPercent = 99,
        };
        var groupConfig = local with
        {
            Profiles = new(local.Profiles) { ["aidite5x"] = local.Profiles["aidite5x"] with { Idle = groupIdle } },
            Machines = local.Machines.Select(m => m.Id == "amm520-1" ? m with { Profile = "opencnc" } : m).ToList(),
        };

        var applied = Invitation(groupConfig).Profile.ApplyTo(local);

        var idle = applied.Profiles["aidite5x"].Idle!;
        Assert.Equal(new RectInt(10, 20, 100, 30), idle.ProgressRoi);
        Assert.Equal(5, idle.OcrScale);
        Assert.True(idle.InvertForOcr);
        Assert.Equal(99, idle.FullAtPercent);
        Assert.Equal(ProgressMode.Number, idle.Mode);
        Assert.Equal("opencnc", applied.Machines.Single(m => m.Id == "amm520-1").Profile);
        // Warstwa grupy nie ma dostępu do lokalnych ścieżek i UI.
        Assert.Equal(local.Paths, applied.Paths);
        Assert.Equal(local.Ui, applied.Ui);
    }

    // --- 2. Wspólna konfiguracja z Panelu (działa także bez grupy) ----------------------------------------

    [Fact]
    public void WspolnaKonfiguracja_NadpisujeCalaSekcjeTiming_AleNieProfileEkranuMaszynAniOcr()
    {
        // Dlaczego: Panel → Ustawienia → Konfiguracja zapisuje sekcję timing W CAŁOŚCI. Późniejsza ręczna
        // zmiana `timing` w config.json (tu idleAlertAfterMs) nie działa, dopóki nie klikniesz „Przywróć”.
        var baseConfig = TestPaths.ExampleConfig with
        {
            Timing = TestPaths.ExampleConfig.Timing with { IdleAlertAfterMs = 300000 },
        };
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig { IdleAtZeroAfterMs = 60000 },
            EditedAt = FakeClock.DefaultStart, EditedBy = "PC1",
        };
        Assert.True(document.IsValid());

        var effective = document.ApplyTo(baseConfig);

        Assert.Equal(60000, effective.Timing.IdleAtZeroAfterMs);
        Assert.Equal(new TimingConfig().IdleAlertAfterMs, effective.Timing.IdleAlertAfterMs);
        Assert.Same(baseConfig.Profiles, effective.Profiles);
        Assert.Same(baseConfig.Machines, effective.Machines);
        Assert.Equal(baseConfig.Ocr, effective.Ocr);
    }

    [Fact]
    public void WspolnaKonfiguracjaBezPolPostoju_WczytujeWartosciDomyslne()
    {
        // Dlaczego: plik shared-config.json zapisany przez inną wersję programu może nie mieć wszystkich
        // pól timing. Brakujące pole to wartość domyślna (2 min / 3 min), a nie zero ani błąd startu.
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "shared-config.json");
        File.WriteAllText(path, """
            { "version": 1, "document": {
                "timing": { "pollIntervalMs": 1000 },
                "editedAt": "2026-09-18T10:00:00+00:00", "editedBy": "PC1" } }
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var document = new SharedConfigStore(path).Load();

        Assert.NotNull(document?.Timing);
        Assert.Equal(120000, document!.Timing!.IdleAlertAfterMs);
        Assert.Equal(180000, document.Timing.IdleAtZeroAfterMs);
    }


    private static byte[] PilotGroupFile()
    {
        var current = TestPaths.ExampleConfig;
        var pilot = current with
        {
            Machines = current.Machines.Select(m => m.Id switch
            {
                "amm520-1" or "amm520-2" => m with { Profile = "opencnc", Enabled = false, RequiresPassword = true },
                "amm580" => m with { Enabled = false },
                _ => m,
            }).ToList(),
            Profiles = current.Profiles.Where(p => p.Key != "aidite5x").ToDictionary(p => p.Key, p => p.Value),
        };
        var aidite = TestPaths.ExampleAidite;
        var legacyRules = new RuleSet { Codes = aidite.Codes, Rules = aidite.Rules };
        var json = JsonNode.Parse(GroupFileCodec.Serialize(Invitation(pilot, legacyRules)))!;
        Strip(json, "requiresPassword", "username", "statusBarAlwaysVisible", "databases");
        return Encoding.UTF8.GetBytes(json.ToJsonString());

        static void Strip(JsonNode? node, params string[] names)
        {
            if (node is JsonObject obj)
            {
                foreach (string name in names) obj.Remove(name);
                foreach (var (_, child) in obj.ToArray()) Strip(child, names);
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array) Strip(child, names);
            }
        }
    }

    [Fact]
    public void StaryPlikGrupyZPilotazu_WczytujeSieIZabieraProfilAidite5xOrazWylacza520I580()
    {
        var file = GroupFileCodec.Parse(PilotGroupFile());
        var local = TestPaths.ExampleConfig;

        var applied = file.Profile.ApplyTo(local);

        Assert.False(applied.Profiles.ContainsKey("aidite5x"));
        Assert.All(applied.Machines.Where(m => m.Id.StartsWith("amm", StringComparison.Ordinal)),
            m => Assert.False(m.Enabled));
        Assert.Equal("opencnc", applied.Machines.Single(m => m.Id == "amm520-1").Profile);
        Assert.True(applied.Machines.Single(m => m.Id == "amm520-1").RequiresPassword);
        Assert.Empty(file.Profile.Rules.Databases);
        Assert.True(local.Profiles["amm580"].MainScreen!.StatusBarAlwaysVisible);
        Assert.False(applied.Profiles["amm580"].MainScreen!.StatusBarAlwaysVisible);
    }

    [Fact]
    public void StaryPlikGrupyZPilotazu_NieZmieniaOdczytuPostepuAidite1Do7()
    {
        var file = GroupFileCodec.Parse(PilotGroupFile());
        var local = TestPaths.ExampleConfig;

        var applied = file.Profile.ApplyTo(local);

        Assert.Equal(local.Profiles["opencnc"].Idle, applied.Profiles["opencnc"].Idle);
        Assert.Equal(local.Timing.IdleAtZeroAfterMs, applied.Timing.IdleAtZeroAfterMs);
        Assert.Equal(local.Timing.IdleAlertAfterMs, applied.Timing.IdleAlertAfterMs);
        Assert.All(applied.Machines.Where(m => m.Id.StartsWith("aidite", StringComparison.Ordinal)),
            m => Assert.Equal("opencnc", m.Profile));
    }

    // --- 4. Progi z grupy/Panelu a zamykanie alertu „stoi na 0 %” -----------------------------------------

    private static MachineConfig Machine => TestPaths.ExampleConfig.Machines.First(m => m.Enabled);

    [Fact]
    public void KrotszyProgZGrupyAlboPanelu_PrzyspieszaAlertNa0Procent()
    {
        // Dlaczego: najniższy dopuszczalny próg (30 s) z grupy/Panelu sprawia, że rozruch nowej pracy
        // odczytany jako „0 %” szybciej daje alert — to jedyny sposób, w jaki te warstwy POGARSZAJĄ objaw.
        var clock = new FakeClock();
        var shortTiming = new TimingConfig { IdleAtZeroAfterMs = 30000 };
        var withGroup = new AlertStateMachine(clock, shortTiming, new AckMemory(clock, TimeSpan.FromHours(2)));
        var withDefaults = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));

        withGroup.OnProgressState(Machine, ProgressState.Zero);
        withDefaults.OnProgressState(Machine, ProgressState.Zero);
        clock.Advance(TimeSpan.FromSeconds(30));
        withGroup.OnProgressState(Machine, ProgressState.Zero);
        withDefaults.OnProgressState(Machine, ProgressState.Zero);

        Assert.Single(withGroup.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.DoesNotContain(withDefaults.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
    }

    [Theory]
    [InlineData(30000)]
    [InlineData(180000)]
    [InlineData(3600000)]
    public void OdczytRuchu_ZamykaAlertNa0Procent_NiezaleznieOdProguZGrupy(int idleAtZeroAfterMs)
    {
        // Dlaczego: żaden próg z grupy/Panelu nie trzyma alertu przy życiu — potwierdzony odczyt 1–99 %
        // (ProgressState.Running) zamyka go od razu. Alert zostaje tylko wtedy, gdy „0,2 %” jest czytane
        // jako 0 % (parser procentu), a tego żadna warstwa konfiguracji nie ustawia.
        var clock = new FakeClock();
        var state = new AlertStateMachine(clock, new TimingConfig { IdleAtZeroAfterMs = idleAtZeroAfterMs },
            new AckMemory(clock, TimeSpan.FromHours(2)));
        state.OnProgressState(Machine, ProgressState.Zero);
        clock.AdvanceMs(idleAtZeroAfterMs);
        state.OnProgressState(Machine, ProgressState.Zero);
        Assert.Single(state.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);

        state.OnProgressState(Machine, ProgressState.Running);

        Assert.DoesNotContain(state.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
    }

    // --- 5. Scalanie stanu między komputerami ---------------------------------------------------------------

    private static Alert IdleAlert(AlertStatus status, int lastSeenOffsetSec) => new()
    {
        Key = AlertKey.Idle("aidite1"),
        MachineId = "aidite1",
        MachineName = "Aidite 1",
        Kind = AlertKind.MachineIdle,
        ShortText = AlertStateMachine.MachineIdleAtZeroShortText,
        Category = AlertStateMachine.IdleAtZeroCategory,
        StartedAt = FakeClock.DefaultStart,
        LastSeenAt = FakeClock.DefaultStart.AddSeconds(lastSeenOffsetSec),
        Status = status,
        ClosedAt = status == AlertStatus.Closed ? FakeClock.DefaultStart.AddSeconds(lastSeenOffsetSec) : null,
    };

    [Fact]
    public void StateMerger_ZamknietyPostojOdJedynegoObserwatora_ZnikaZListy()
    {
        var merged = StateMerger.Merge([[IdleAlert(AlertStatus.Closed, 10)]],
            new AckMemory(new FakeClock(), TimeSpan.FromHours(2)));

        Assert.Empty(merged);
    }

    [Fact]
    public void StateMerger_AktywnyOdDrugiegoObserwatora_WygrywaZZamknietym()
    {
        // Dlaczego (zachowanie udokumentowane w StateMerger, starszy protokół bez tożsamości grupy): gdy
        // DWA komputery naraz obserwują (partycja sieci), Active bije Closed. ClusterNode wpuszcza do
        // scalenia tylko STATE żywego komputera, nie starszy niż max(5 s, 5 × stateIntervalMs), więc to
        // stan przejściowy — i wymaga, żeby drugi obserwator sam wciąż czytał „0 %”.
        var merged = StateMerger.Merge(
            [[IdleAlert(AlertStatus.Closed, 10)], [IdleAlert(AlertStatus.Active, 5)]],
            new AckMemory(new FakeClock(), TimeSpan.FromHours(2)));

        Assert.Equal(AlertStatus.Active, Assert.Single(merged).Status);
    }

    [Fact]
    public async Task PracaBezGrupy_ZamknietyPostojZnikaZeScalonejListy()
    {
        // Dlaczego: przy cluster.enabled:false (i bez zatwierdzonej generacji) ClusterRuntime działa „solo”
        // na prywatnej magistrali — żaden cudzy STATE nie może podtrzymać alertu postoju.
        var clock = new FakeClock();
        await using var runtime = new ClusterRuntime(new ClusterConfig { Enabled = false }, null, clock,
            new AckMemory(clock, TimeSpan.FromHours(2)), "PC1", "test");
        await runtime.StartAsync(default);
        var state = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        state.OnProgressState(Machine, ProgressState.Zero);
        clock.AdvanceMs(new TimingConfig().IdleAtZeroAfterMs);
        state.OnProgressState(Machine, ProgressState.Zero);
        runtime.SetLocalState(state.ActiveAlerts, Array.Empty<MachineStatusSnapshot>());
        await runtime.TickAsync(default);
        Assert.Single(runtime.MergedAlerts, a => a.Kind == AlertKind.MachineIdle);

        state.OnProgressState(Machine, ProgressState.Running);
        runtime.SetLocalState(state.ActiveAlerts, Array.Empty<MachineStatusSnapshot>());
        await runtime.TickAsync(default);

        Assert.DoesNotContain(runtime.MergedAlerts, a => a.Kind == AlertKind.MachineIdle);
    }

    [Fact]
    public async Task Grupa_PostojZamknietyUObserwatora_ZnikaNaKomputerzeOdbierajacymINieWraca()
    {
        // Dlaczego: w grupie zamknięcie epizodu idzie jako closure z UUID; spóźniony STATE ze starym
        // epizodem nie może go wskrzesić na komputerze, który tylko odbiera alerty.
        await using var pair = new GroupRuntimeTests.Pair(); await pair.Start();
        int zeroSeconds = new TimingConfig().IdleAtZeroAfterMs / 1000 + 5;
        for (int i = 0; i < zeroSeconds; i++)
        {
            if (pair.A.IsWatcher) pair.AState.OnProgressState(pair.Machine, ProgressState.Zero);
            pair.AState.Tick();
            await pair.Tick(1);
        }
        Assert.True(pair.A.IsWatcher); Assert.False(pair.B.IsWatcher);
        Assert.Single(pair.B.MergedAlerts, a => a.Kind == AlertKind.MachineIdle);

        for (int i = 0; i < 20; i++)
        {
            pair.AState.OnProgressState(pair.Machine, ProgressState.Running);
            pair.AState.Tick();
            await pair.Tick(1);
        }

        Assert.DoesNotContain(pair.A.MergedAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.DoesNotContain(pair.B.MergedAlerts, a => a.Kind == AlertKind.MachineIdle);
    }
}
