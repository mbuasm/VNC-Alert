using System.IO;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;
using static MaszynyAlert.App.Tests.SettingsChangeConfirmationTests;

namespace MaszynyAlert.App.Tests;

public sealed class MachinesPanelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "machines-panel-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private string FilePath => Path.Combine(_directory, "shared-config", "shared-config.json");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }

    private static AppConfig ExampleConfig() =>
        ConfigLoader.LoadAppConfig(Path.Combine(RepositoryRoot(), "config", "config.example.json"));

    private static SharedConfigViewInput Input(AppConfig @base, SharedConfigDocument? document = null) =>
        new(@base, document?.ApplyTo(@base) ?? @base, document, "", null, false, null);

    private sealed class Recorder
    {
        public List<(TimingConfig? Timing, OverlayConfig? Overlay, IReadOnlyDictionary<string, bool>? Machines)> Calls { get; } = [];

        public Task Apply(TimingConfig? timing, OverlayConfig? overlay, IReadOnlyDictionary<string, bool>? machines, CancellationToken ct)
        {
            Calls.Add((timing, overlay, machines));
            return Task.CompletedTask;
        }
    }

    private static SharedConfigViewModel Create(SharedConfigViewInput input, Recorder? recorder = null, ISettingsChangeConfirmation? confirmation = null) =>
        new(() => input, (recorder ?? new Recorder()).Apply, confirmation);

    private static SharedConfigSection MachinesSection(SharedConfigViewModel vm) =>
        vm.Sections.Single(s => s.Title == SharedConfigViewModel.MachinesSectionTitle);

    private static SharedConfigField Row(SharedConfigViewModel vm, string machineId) =>
        vm.MachineFields.Single(f => f.Key == SharedConfigViewModel.MachineKey(machineId));

    /// <summary>Złożenie jak w programie: stan z <see cref="SharedConfigSync"/>, zapis przez <see cref="SharedConfigSync.Edit"/>.</summary>
    private static SharedConfigViewModel Composed(SharedConfigSync sync, ISettingsChangeConfirmation confirmation, AppConfig @base) =>
        new(() => new SharedConfigViewInput(@base, sync.Current?.ApplyTo(@base) ?? @base, sync.Current, "", sync.LoadError, false, sync.Path),
            (timing, overlay, machines, _) => { sync.Edit(timing, overlay, DateTimeOffset.UtcNow, machines); return Task.CompletedTask; },
            confirmation);

    private SharedConfigSync Sync()
    {
        var sync = new SharedConfigSync(new SharedConfigStore(FilePath), "PC-A");
        sync.Reload();
        return sync;
    }

    // ============================== Wiersze ==============================

    [Fact]
    public void MachinesSection_IsAfterOverlay_WithOneRowPerMachineFromTheConfiguration()
    {
        AppConfig config = ExampleConfig();
        var vm = Create(Input(config));

        Assert.Equal(["Nakładka", "Maszyny"], vm.Sections.Skip(4).Select(s => s.Title));
        SharedConfigSection section = MachinesSection(vm);
        Assert.Equal(12, section.Fields.Count);
        Assert.Equal(config.Machines.Select(m => m.Name), section.Fields.Select(f => f.Label));
        Assert.Equal(config.Machines.Select(m => SharedConfigViewModel.MachineKey(m.Id)), section.Fields.Select(f => f.Key));
        Assert.All(section.Fields, f =>
        {
            Assert.Equal(SharedConfigFieldKind.Machine, f.Kind);
            Assert.True(f.IsMachine);
            Assert.False(f.IsFlag);
            Assert.Equal(SharedConfigViewModel.MachineDescription, f.Description);
            Assert.Equal("", f.Unit);
            Assert.False(f.IsChanged);
        });
        SharedConfigField amm580 = Row(vm, "amm580");
        Assert.Equal("580", amm580.Label);
        Assert.Equal("192.168.1.110:5900 · amm580", amm580.Detail);
        Assert.Equal("false", amm580.Text);
        Assert.False(amm580.Flag);
        Assert.Equal("wyłączona", amm580.Display(amm580.Text));
        Assert.True(Row(vm, "aidite1").Flag);
        Assert.Equal("włączona", Row(vm, "aidite1").Display("true"));
        Assert.Contains("bez nadpisania", section.SourceText, StringComparison.Ordinal);
        Assert.Equal("", section.EmptyText);
        Assert.Equal("machines.amm580.enabled", SharedConfigViewModel.MachineKey("amm580"));
        // Wiersze maszyn są wśród wszystkich wierszy (lista zmian, zaznaczenie), ale nie wśród sekcji czasu/nakładki.
        Assert.Contains(amm580, vm.Fields);
        Assert.DoesNotContain(amm580, vm.TimingFields);
    }

    [Fact]
    public void RowsShowTheEffectiveSwitch_AndTheFallbackFromConfigJson_WhenTheLayerOverridesThem()
    {
        AppConfig config = ExampleConfig();
        var document = new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(new Dictionary<string, bool> { ["amm580"] = true, ["aidite1"] = false }),
            EditedAt = Noon, EditedBy = "PC-B",
        };
        var vm = Create(Input(config, document));

        Assert.Equal("true", Row(vm, "amm580").Text);
        Assert.Equal("false", Row(vm, "amm580").DefaultText);
        Assert.Equal("false", Row(vm, "aidite1").Text);
        Assert.Equal("true", Row(vm, "aidite1").DefaultText);
        Assert.Equal("true", Row(vm, "aidite2").Text);
        Assert.Contains("PC-B", MachinesSection(vm).SourceText, StringComparison.Ordinal);
        Assert.All(vm.Sections.Where(s => s.Title != SharedConfigViewModel.MachinesSectionTitle),
            s => Assert.Contains("bez nadpisania", s.SourceText, StringComparison.Ordinal));
    }

    [Fact]
    public void RowsAreRebuiltWhenTheMachineListChanges_AndKeptWhenItDoesNot()
    {
        AppConfig config = ExampleConfig();
        AppConfig current = config;
        var vm = new SharedConfigViewModel(() => Input(current), new Recorder().Apply);
        SharedConfigField before = Row(vm, "amm580");
        vm.SelectedSection = MachinesSection(vm);
        vm.SelectedField = before;

        vm.RefreshFromOutside(changedElsewhere: false);
        Assert.Same(before, Row(vm, "amm580")); // ta sama lista = te same obiekty (zaznaczenie zostaje)
        Assert.Same(before, vm.SelectedField);

        current = config with { Machines = config.Machines.Take(3).ToList() };
        vm.RefreshFromOutside(changedElsewhere: false);

        Assert.Equal(3, MachinesSection(vm).Fields.Count);
        Assert.Equal(3, vm.MachineFields.Count);
        Assert.Same(MachinesSection(vm).Fields[0], vm.SelectedField);
        current = config with { Machines = [] };
        vm.RefreshFromOutside(changedElsewhere: false);
        Assert.Empty(vm.MachineFields);
        Assert.Contains("Brak maszyn", MachinesSection(vm).EmptyText, StringComparison.Ordinal);
    }

    // ============================== Zapis ==============================

    [Fact]
    public async Task SwitchingOn580_IsConfirmedAsMaszynaBeforeAfter_AndPassesTheSnapshotOfAllSwitches()
    {
        AppConfig config = ExampleConfig();
        var recorder = new Recorder();
        var confirmation = new RecordingConfirmation(true);
        var vm = Create(Input(config), recorder, confirmation);

        Row(vm, "amm580").Flag = true;
        Assert.True(Row(vm, "amm580").IsChanged);
        Assert.Equal("zmiany", MachinesSection(vm).StatusText);
        Assert.True(vm.HasUnsavedChanges);
        await vm.SaveCommand.ExecuteAsync(null);

        IReadOnlyList<SettingChange> asked = Assert.Single(confirmation.Asked);
        Assert.Equal([
            new SettingChange("Maszyna: 580", "wyłączona", "włączona"),
            new SettingChange("Źródło wartości: maszyny", "config.json / profil grupy", "wspólna konfiguracja"),
        ], asked);
        var call = Assert.Single(recorder.Calls);
        Assert.Null(call.Timing);
        Assert.Null(call.Overlay);
        Assert.NotNull(call.Machines);
        Assert.Equal(12, call.Machines!.Count);
        Assert.True(call.Machines["amm580"]);
        Assert.False(call.Machines["premile"]);
        Assert.True(call.Machines["aidite1"]);
        Assert.StartsWith("Zapisano.", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_SavesNothing_AndTheSwitchStaysInTheRow()
    {
        var recorder = new Recorder();
        var vm = Create(Input(ExampleConfig()), recorder, new RecordingConfirmation(false));

        Row(vm, "amm580").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Calls);
        Assert.StartsWith("Nie zapisano — anulowano", vm.Message, StringComparison.Ordinal);
        Assert.True(Row(vm, "amm580").Flag);
        Assert.True(Row(vm, "amm580").IsChanged);
    }

    [Fact]
    public async Task UnchangedSwitches_PassNull_SoTheValueFromTheFileStays()
    {
        var recorder = new Recorder();
        var vm = Create(Input(ExampleConfig()), recorder);

        vm.Fields.Single(f => f.Key == "timing.alertSoundRepeatMs").Text = "0.5";
        await vm.SaveCommand.ExecuteAsync(null);

        var call = Assert.Single(recorder.Calls);
        Assert.NotNull(call.Timing);
        Assert.Null(call.Machines);
    }

    [Fact]
    public async Task SwitchedBackToTheLoadedValue_IsNotAChange()
    {
        var recorder = new Recorder();
        var vm = Create(Input(ExampleConfig()), recorder);

        Row(vm, "amm580").Flag = true;
        Row(vm, "amm580").Flag = false;
        Assert.False(Row(vm, "amm580").IsChanged);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Calls);
        Assert.Equal("Brak zmian do zapisania.", vm.Message);
    }

    [Fact]
    public async Task DisablingEveryMachine_IsRefusedWithAMessageAtTheField_AndNothingIsSaved()
    {
        var recorder = new Recorder();
        var confirmation = new RecordingConfirmation(true);
        var vm = Create(Input(ExampleConfig()), recorder, confirmation);
        vm.SelectedSection = vm.Sections[0];

        foreach (SharedConfigField row in vm.MachineFields) row.Flag = false;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Calls);
        Assert.Empty(confirmation.Asked);
        Assert.Equal(SharedConfigViewModel.NoEnabledMachinesMessage, Row(vm, "aidite1").Error);
        Assert.Equal("błąd", MachinesSection(vm).StatusText);
        Assert.StartsWith("Nie zapisano — popraw", vm.Message, StringComparison.Ordinal);
        // Zakładka sama przechodzi do sekcji z błędem i zaznacza pole.
        Assert.Same(MachinesSection(vm), vm.SelectedSection);
        Assert.Same(Row(vm, "aidite1"), vm.SelectedField);

        // Po włączeniu jednej z powrotem zapis przechodzi i błąd znika.
        Row(vm, "aidite1").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(recorder.Calls);
        Assert.Equal("", Row(vm, "aidite1").Error);
    }

    [Fact]
    public async Task SwitchAndTimingChangedTogether_GoInOneWrite_WithBothSourceRows()
    {
        var recorder = new Recorder();
        var confirmation = new RecordingConfirmation(true);
        var vm = Create(Input(ExampleConfig()), recorder, confirmation);

        Row(vm, "cameo1").Flag = true;
        vm.Fields.Single(f => f.Key == "timing.alertSoundRepeatMs").Text = "0.5";
        await vm.SaveCommand.ExecuteAsync(null);

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(30000, call.Timing!.AlertSoundRepeatMs);
        Assert.NotNull(call.Overlay);
        Assert.True(call.Machines!["cameo1"]);
        IReadOnlyList<SettingChange> asked = Assert.Single(confirmation.Asked);
        Assert.Contains(asked, c => c.Name == "Maszyna: Cameo 1" && c.After == "włączona");
        Assert.Contains(asked, c => c.Name == "Źródło wartości: czas i progi");
        Assert.Contains(asked, c => c.Name == "Źródło wartości: maszyny");
    }

    [Fact]
    public async Task OldApplyFunctionWithoutMachines_ThrowsWhenAMachineIsSwitched_InsteadOfLosingIt()
    {
        var vm = new SharedConfigViewModel(() => Input(ExampleConfig()), (_, _, _) => Task.CompletedTask);

        Row(vm, "amm580").Flag = true;

        await Assert.ThrowsAsync<NotSupportedException>(() => vm.SaveCommand.ExecuteAsync(null));
    }

    // ============================== Złożenie z plikiem warstwy ==============================

    [Fact]
    public async Task Composed_SaveWritesTheSnapshotToTheFile_KeepsTheRest_AndReloadsTheRows()
    {
        AppConfig config = ExampleConfig();
        var store = new SharedConfigStore(FilePath);
        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon, "PC-B");
        SharedConfigSync sync = Sync();
        var vm = Composed(sync, new RecordingConfirmation(true), config);

        Row(vm, "amm580").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        SharedConfigDocument saved = new SharedConfigStore(FilePath).Load()!;
        Assert.Equal(12, saved.Machines!.Count);
        Assert.True(saved.Machines["amm580"].Enabled);
        Assert.False(saved.Machines["premile"].Enabled);
        Assert.Equal(30000, saved.Timing!.AlertSoundRepeatMs);
        Assert.Equal("PC-A", saved.EditedBy);
        Assert.True(saved.ApplyTo(config).Machines.Single(m => m.Id == "amm580").Enabled);
        Assert.True(Row(vm, "amm580").Flag);
        Assert.False(Row(vm, "amm580").IsChanged);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Contains("PC-A", MachinesSection(vm).SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchChangedInPanel_WhileAnotherComputerSwitchedMachinesDuringTheQuestion_IsNotSavedOverIt()
    {
        AppConfig config = ExampleConfig();
        SharedConfigSync sync = Sync();
        var confirmation = new RecordingConfirmation(true, whileOpen: () =>
        {
            new SharedConfigStore(FilePath).Save(null, null, DateTimeOffset.UtcNow.AddSeconds(5), "PC-B",
                machines: new Dictionary<string, bool> { ["amm580"] = false, ["premile"] = true });
            sync.Reload();
        });
        var vm = Composed(sync, confirmation, config);

        Row(vm, "amm580").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(SharedConfigViewModel.ChangedElsewhereMessage, vm.Message);
        SharedConfigDocument file = new SharedConfigStore(FilePath).Load()!;
        Assert.Equal("PC-B", file.EditedBy);
        Assert.True(file.Machines!["premile"].Enabled);
        Assert.True(Row(vm, "amm580").IsChanged); // wpis zostaje
    }

    [Fact]
    public async Task TimingSaved_WhileAnotherComputerSwitchedAMachineDuringTheQuestion_KeepsThatSwitch()
    {
        AppConfig config = ExampleConfig();
        SharedConfigSync sync = Sync();
        var confirmation = new RecordingConfirmation(true, whileOpen: () =>
        {
            new SharedConfigStore(FilePath).Save(null, null, DateTimeOffset.UtcNow.AddSeconds(5), "PC-B",
                machines: new Dictionary<string, bool> { ["amm580"] = true });
            sync.Reload();
        });
        var vm = Composed(sync, confirmation, config);

        vm.Fields.Single(f => f.Key == "timing.alertSoundRepeatMs").Text = "0.5";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Zapisano.", vm.Message, StringComparison.Ordinal);
        SharedConfigDocument file = new SharedConfigStore(FilePath).Load()!;
        Assert.Equal("PC-A", file.EditedBy);
        Assert.Equal(30000, file.Timing!.AlertSoundRepeatMs);
        Assert.True(file.Machines!["amm580"].Enabled);
        Assert.True(Row(vm, "amm580").Flag); // po wczytaniu widać przełączenie z PC-B
    }

    [Fact]
    public async Task ResetTiming_DoesNotTouchTheSwitches()
    {
        AppConfig config = ExampleConfig();
        var store = new SharedConfigStore(FilePath);
        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon, "PC-B", machines: new Dictionary<string, bool> { ["amm580"] = true });
        SharedConfigSync sync = Sync();
        var vm = Composed(sync, new RecordingConfirmation(true), config);

        await vm.ResetTimingCommand.ExecuteAsync(null);

        SharedConfigDocument file = new SharedConfigStore(FilePath).Load()!;
        Assert.Null(file.Timing);
        Assert.True(file.Machines!["amm580"].Enabled);
        Assert.True(Row(vm, "amm580").Flag);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
