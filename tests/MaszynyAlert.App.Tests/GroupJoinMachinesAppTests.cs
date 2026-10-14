using System.IO;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using static MaszynyAlert.App.Tests.SettingsChangeConfirmationTests;

namespace MaszynyAlert.App.Tests;

public sealed class GroupJoinMachinesAppTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static AppConfig Example()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return ConfigLoader.LoadAppConfig(Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium."), "config", "config.example.json"));
    }

    private static string Enabled(AppConfig config) =>
        string.Join(",", config.Machines.Where(m => m.Enabled).Select(m => m.Id).Order(StringComparer.Ordinal));

    private static GroupIdentity Identity(string name) => new()
    {
        GroupId = Guid.NewGuid(), GroupName = name, KeyId = Guid.NewGuid(), ProtocolVersion = GroupIdentity.CurrentProtocolVersion,
        ProfileRevision = 1, ProfileHash = new string('a', 64),
    };

    private static SharedConfigDocument StationLayer(AppConfig example) => new()
    {
        Timing = example.Timing with { AlertSoundRepeatMs = 30000 },
        Machines = SharedConfigDocument.CanonicalMachines(example.Machines.ToDictionary(m => m.Id, m => m.Id is "amm580" or "amm520-2")),
        EditedAt = T0, EditedBy = "STANOWISKO-520",
    };

    // ============================== konfiguracja efektywna po zmianie grupy ==============================

    [Fact]
    public void ZmianaGrupy_KonfiguracjaEfektywnaBezLokalnychPrzelacznikow_ResztaWarstwyZostaje()
    {
        AppConfig example = Example();
        AppConfig groupBase = example;
        SharedConfigDocument layer = StationLayer(example);
        var groupA = new ClusterConfig { Enabled = true, Group = Identity("Hala — Aidite") };
        var groupB = new ClusterConfig { Enabled = true, Group = Identity("Frezownia") };

        AppConfig fromStandalone = AppOrchestrator.EffectiveAfterGroupSettings(layer, groupBase, new ClusterConfig(), groupA);
        AppConfig fromOtherGroup = AppOrchestrator.EffectiveAfterGroupSettings(layer, groupBase, groupA, groupB);
        AppConfig sameGroup = AppOrchestrator.EffectiveAfterGroupSettings(layer, groupBase, groupA, groupA with { Priority = 7 });

        Assert.Equal("aidite1,aidite2,aidite3,aidite4,aidite5,aidite6,aidite7", Enabled(fromStandalone));
        Assert.Equal(Enabled(fromStandalone), Enabled(fromOtherGroup));
        Assert.Equal(30000, fromStandalone.Timing.AlertSoundRepeatMs); // reszta warstwy (czas) obowiązuje dalej
        Assert.Equal("amm520-2,amm580", Enabled(sameGroup));          // ta sama grupa: przełączniki zostają
        Assert.Same(groupBase, AppOrchestrator.EffectiveAfterGroupSettings(null, groupBase, new ClusterConfig(), groupA));
    }

    /// <summary>
    /// Komputer, który ZAKŁADA nową grupę kreatorem (nowy GroupId — też przejście do innej grupy), nie traci maszyn: profil nowej grupy
    /// powstaje z jego konfiguracji EFEKTYWNEJ (<c>MultiComputerStatusInput.From</c> → <see cref="GroupProfile.Capture"/>), więc
    /// przełączniki są w nim jako <c>enabled</c> maszyn, a zdjęcie ich z warstwy niczego nie zmienia.
    /// </summary>
    [Fact]
    public void ZalozenieNowejGrupyKreatorem_ZbiorMaszynObserwowanychBezZmian()
    {
        AppConfig example = Example();
        SharedConfigDocument layer = StationLayer(example);
        AppConfig effectiveBefore = layer.ApplyTo(example);
        RuleSet rules = ConfigLoader.LoadRules(WpfTestHost.SourceFile("config", "rules.example.json"));
        GroupProfile profile = GroupProfile.Capture(effectiveBefore, rules, new Dictionary<string, string>());
        AppConfig nextBase = profile.ApplyTo(example);

        AppConfig after = AppOrchestrator.EffectiveAfterGroupSettings(layer, nextBase, new ClusterConfig(),
            new ClusterConfig { Enabled = true, Group = Identity("Nowa grupa") });

        Assert.Equal("amm520-2,amm580", Enabled(effectiveBefore));
        Assert.Equal(Enabled(effectiveBefore), Enabled(after));
    }

    [Fact]
    public void Orkiestrator_ZmianaGrupyLiczyKonfiguracjeBezLokalnychPrzelacznikow_ARollbackJeOddaje()
    {
        string source = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Services", "AppOrchestrator.cs"));

        Assert.Contains("var nextConfig = EffectiveAfterGroupSettings(SharedConfig.Current, nextBase, _cluster.Configuration, data.Cluster);",
            source, StringComparison.Ordinal);
        Assert.Contains("_cluster.RevertSwitchAsync(previousConfig.Cluster, previousKey, CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_cluster.SwitchAsync(previousConfig.Cluster", source, StringComparison.Ordinal);
    }

    // ============================== Panel ==============================

    private sealed class Recorder
    {
        public int Saves { get; private set; }

        public Task Apply(TimingConfig? timing, OverlayConfig? overlay, IReadOnlyDictionary<string, bool>? machines, CancellationToken ct)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private static SharedConfigViewModel Create(AppConfig config, bool inGroup, SharedConfigDocument? document, Recorder recorder,
        RecordingConfirmation confirmation, Func<string, bool>? hasPassword = null) =>
        new(() => new SharedConfigViewInput(config, document?.ApplyTo(config) ?? config, document,
                inGroup ? "Konfiguracja jest wspólna dla grupy; zgodna z 3 z 3 komputerami." : "", null, inGroup, null,
                hasPassword ?? (id => id.StartsWith("aidite", StringComparison.Ordinal))),
            recorder.Apply, confirmation);

    /// <summary>
    /// Poza grupą zachowanie bez zmian: komputer z wyłączonym monitorowaniem (<c>enabled:false</c>) nikogo nie obserwuje, więc włączenie
    /// maszyny bez hasła nie dostaje ostrzeżenia (reguła gotowości przy wyłączonym monitorowaniu o hasła nie pyta) i zapis przechodzi.
    /// W grupie ten sam komputer dostaje odmowę (<c>WerTestD3GrupaPanelTests</c>).
    /// </summary>
    [Fact]
    public async Task PozaGrupa_MonitorowanieWylaczone_WlaczenieMaszynyBezHasla_BezOstrzezeniaIZapisPrzechodzi()
    {
        AppConfig thisPc = Example() with
        {
            Enabled = false,
            Machines = Example().Machines.Select(m => m.Id == "premile" ? m with { RequiresPassword = true } : m).ToList(),
        };
        var recorder = new Recorder();
        var confirmation = new RecordingConfirmation(true);
        var vm = Create(thisPc, inGroup: false, null, recorder, confirmation);

        vm.MachineFields.Single(f => f.Key == SharedConfigViewModel.MachineKey("premile")).Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, recorder.Saves);
        Assert.DoesNotContain(Assert.Single(confirmation.Asked), c => c.IsWarning);
    }

    [Fact]
    public void ZrodloWartosciMaszyn_PodajeOstatniaZmianeCalejWspolnejKonfiguracji_NiePrzypisujeJejPrzelacznikom()
    {
        AppConfig example = Example();
        // Dokument po scaleniu z zapisem starszej wersji: przełączniki z pliku tego komputera, czas i autor — z PC-STARY.
        SharedConfigDocument merged = StationLayer(example) with { EditedAt = T0.AddHours(1), EditedBy = "PC-STARY" };
        var vm = Create(example, inGroup: true, merged, new Recorder(), new RecordingConfirmation(true));

        string machines = vm.Sections.Single(s => s.Title == SharedConfigViewModel.MachinesSectionTitle).SourceText;

        Assert.StartsWith("Źródło wartości: wspólna konfiguracja", machines, StringComparison.Ordinal);
        Assert.Contains("ostatnia zmiana wspólnej konfiguracji (dowolnego ustawienia)", machines, StringComparison.Ordinal);
        Assert.Contains("PC-STARY", machines, StringComparison.Ordinal);
        Assert.DoesNotContain("zmieniono", machines, StringComparison.Ordinal);
        // Ta sama zasada we wszystkich sekcjach — każda bierze czas i autora z jednego dokumentu.
        Assert.All(vm.Sections.Where(s => s.SourceText.Contains("wspólna konfiguracja", StringComparison.Ordinal)),
            s => Assert.Contains("ostatnia zmiana wspólnej konfiguracji (dowolnego ustawienia)", s.SourceText, StringComparison.Ordinal));
    }
}
