using System.IO;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace MaszynyAlert.App.Tests;

public sealed class SettingsChangeConfirmationTests
{
    internal sealed class RecordingConfirmation(bool answer, Action? whileOpen = null) : ISettingsChangeConfirmation
    {
        public List<IReadOnlyList<SettingChange>> Asked { get; } = [];

        public bool Confirm(IReadOnlyList<SettingChange> changes)
        {
            Asked.Add(changes);
            whileOpen?.Invoke();
            return answer;
        }
    }

    private static AppConfig BaseConfig(TimingConfig? timing = null, OverlayConfig? overlay = null) =>
        new() { Enabled = false, Timing = timing ?? new TimingConfig(), Ui = new UiConfig { Overlay = overlay ?? new OverlayConfig() } };

    private static SharedConfigViewInput Input(AppConfig? @base = null, AppConfig? effective = null, SharedConfigDocument? document = null)
    {
        AppConfig b = @base ?? BaseConfig();
        return new SharedConfigViewInput(b, effective ?? document?.ApplyTo(b) ?? b, document, "", null, false, null);
    }

    private static SharedConfigDocument Document(TimingConfig? timing = null, OverlayConfig? overlay = null) => new()
    {
        Timing = timing, Overlay = overlay, EditedAt = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), EditedBy = "PC-B",
    };

    private static SharedConfigField Field(SharedConfigViewModel vm, string key) => vm.Fields.Single(f => f.Key == key);

    // =============================================================================================
    // SetupChanges.Build — ustawienia tego komputera
    // =============================================================================================

    private static SetupChangeInput NoChange() => new()
    {
        UpdatesIdBefore = "id.access", PriorityBefore = 100, PriorityAfter = 100,
        NotificationsBefore = true, NotificationsAfter = true, AutostartBefore = false, AutostartAfter = false,
    };

    [Fact]
    public void Setup_NothingChanged_EmptyList()
    {
        Assert.Empty(SetupChanges.Build(NoChange()));
        Assert.Empty(SetupChanges.Build(NoChange() with { UpdatesIdAfter = "id.access" }));
    }

    public static TheoryData<string, string, string?, string?> SingleSetupChanges() => new()
    {
        { "SharedVnc", "Hasło VNC (wszystkie maszyny)", null, null },
        { "MachineVnc", "Hasło VNC — Aidite 3", null, null },
        { "Key", "Klucz sieciowy", null, null },
        { "Admin", "Hasło Panelu administracyjnego", null, null },
        { "Secret", "Sekret tokenu dostępu (katalog wydań)", null, null },
        { "Priority", "Priorytet tego komputera", "100", "50" },
        { "Autostart", "Uruchamiaj przy logowaniu", "wyłączone", "włączone" },
        { "Notifications", "Pokazuj powiadomienia Windows", "włączone", "wyłączone" },
        { "IdChanged", "Identyfikator tokenu dostępu (katalog wydań)", "id.access", "nowy.access" },
        { "IdRemoved", "Identyfikator tokenu dostępu (katalog wydań)", "id.access", "(brak)" },
    };

    [Theory]
    [MemberData(nameof(SingleSetupChanges))]
    public void Setup_EachChangeAlone_OneRowWithTheRightValues(string what, string name, string? before, string? after)
    {
        SetupChangeInput input = what switch
        {
            "SharedVnc" => NoChange() with { SharedVncPasswordEntered = true },
            "MachineVnc" => NoChange() with { VncPasswordMachines = ["Aidite 3"] },
            "Key" => NoChange() with { NetworkKeyChanged = true },
            "Admin" => NoChange() with { AdminPasswordChanged = true },
            "Secret" => NoChange() with { UpdatesSecretChanged = true },
            "Priority" => NoChange() with { PriorityAfter = 50 },
            "Autostart" => NoChange() with { AutostartAfter = true },
            "Notifications" => NoChange() with { NotificationsAfter = false },
            "IdChanged" => NoChange() with { UpdatesIdAfter = "nowy.access" },
            _ => NoChange() with { UpdatesIdAfter = "" },
        };

        SettingChange change = Assert.Single(SetupChanges.Build(input));

        Assert.Equal(name, change.Name);
        Assert.Equal(before, change.Before);
        Assert.Equal(after, change.After);
        if (before is null)
        {
            Assert.True(change.IsSecret);
            Assert.Equal("", change.BeforeText);
            Assert.Equal("zostanie zmienione", change.AfterText);
        }
        else
        {
            Assert.False(change.IsSecret);
            Assert.Equal("Było: " + before, change.BeforeText);
            Assert.Equal("Po zapisaniu będzie: " + after, change.AfterText);
        }
    }

    [Fact]
    public void Setup_IdentifierAddedWhereThereWasNone_ShowsBrak()
    {
        SettingChange change = Assert.Single(SetupChanges.Build(NoChange() with { UpdatesIdBefore = "", UpdatesIdAfter = "abc.access" }));
        Assert.Equal("(brak)", change.Before);
        Assert.Equal("abc.access", change.After);
    }

    [Fact]
    public void Setup_AllChanges_InFixedOrder()
    {
        var input = NoChange() with
        {
            SharedVncPasswordEntered = true, VncPasswordMachines = ["520 (1)", "580"], NetworkKeyChanged = true, AdminPasswordChanged = true,
            PriorityAfter = 10, AutostartAfter = true, NotificationsAfter = false, UpdatesIdAfter = "x.access", UpdatesSecretChanged = true,
        };

        Assert.Equal(
        [
            "Hasło VNC (wszystkie maszyny)", "Hasło VNC — 520 (1)", "Hasło VNC — 580", "Klucz sieciowy", "Hasło Panelu administracyjnego",
            "Priorytet tego komputera", "Uruchamiaj przy logowaniu", "Pokazuj powiadomienia Windows",
            "Identyfikator tokenu dostępu (katalog wydań)", "Sekret tokenu dostępu (katalog wydań)",
        ], SetupChanges.Build(input).Select(c => c.Name));
    }

    // =============================================================================================
    // Konfiguracja — PendingChanges
    // =============================================================================================

    [Fact]
    public void Pending_ShowsFieldUnitsFlagsChoicesAndLists()
    {
        var vm = new SharedConfigViewModel(() => Input(), (_, _, _) => Task.CompletedTask);
        Field(vm, "timing.escalationAfterAckMs").Text = "2.5";
        Field(vm, "timing.offlineAlertSound").Flag = true;
        Field(vm, "ui.overlay.layout").Text = "two-rows";
        Field(vm, "timing.reconnectBackoffMs").Text = "1; 4";
        Field(vm, "timing.pollIntervalMs").Text = "1"; // przepisane na ten sam tekst — nie jest zmianą

        IReadOnlyList<SettingChange> changes = vm.PendingChanges();

        Assert.Equal(4, changes.Count(c => !c.Name.StartsWith("Źródło wartości", StringComparison.Ordinal)));
        Assert.Contains(new SettingChange("Źródło wartości: czas i progi", "config.json / profil grupy", "wspólna konfiguracja"), changes);
        Assert.Contains(new SettingChange("Źródło wartości: nakładka", "config.json / profil grupy", "wspólna konfiguracja"), changes);
        Assert.Equal(6, changes.Count);
        Assert.Contains(new SettingChange("Eskalacja po „Idę sprawdzić”", "4 min", "2.5 min"), changes);
        Assert.Contains(new SettingChange("Dźwięk przy braku łączności", "wyłączone", "włączone"), changes);
        Assert.Contains(new SettingChange("Układ kafelków", "one-row", "two-rows"), changes);
        Assert.Contains(new SettingChange("Odstępy ponawiania połączenia", "2; 5; 15; 30 s", "1; 4 s"), changes);
        Assert.All(changes, c => Assert.False(c.IsSecret));
    }

    /// <summary>
    /// „Po zapisaniu będzie” ma mówić, CO zostanie zapisane — w postaci pola, nie surowym wpisem. „0.0004” s zapisuje się
    /// jako 0 ms (lista: „0.0004 s”), „1e3” s jako 1000 s (lista: „1e3 s”), „ 2 ” min z odstępami.
    /// </summary>
    [Theory]
    [InlineData("timing.ocrRetryIntervalMs", "0.0004", "0 s")]
    [InlineData("timing.ocrRetryIntervalMs", "1e3", "1000 s")]
    [InlineData("timing.escalationAfterAckMs", " 2,5 ", "2.5 min")]
    public async Task Pending_AfterValueIsWhatWillBeSaved(string key, string entered, string expected)
    {
        var confirmation = new RecordingConfirmation(false);
        var vm = new SharedConfigViewModel(() => Input(), (_, _, _) => Task.CompletedTask, confirmation);

        Field(vm, key).Text = entered;
        await vm.SaveCommand.ExecuteAsync(null);

        SettingChange change = Assert.Single(Assert.Single(confirmation.Asked), c => !c.Name.StartsWith("Źródło wartości", StringComparison.Ordinal));
        Assert.Equal(expected, change.After);
    }

    // =============================================================================================
    // Konfiguracja — kolejność, Anuluj, brak zmian
    // =============================================================================================

    [Fact]
    public async Task Save_AsksWithTheSameListAsPendingChanges_ThenApplies()
    {
        int applied = 0;
        var confirmation = new RecordingConfirmation(true);
        var vm = new SharedConfigViewModel(() => Input(), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);
        Field(vm, "timing.idlePopupSeconds").Text = "10";
        IReadOnlyList<SettingChange> expected = vm.PendingChanges();

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, Assert.Single(confirmation.Asked));
        Assert.Equal(1, applied);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Save_NoChanges_NoQuestionNoApply()
    {
        int applied = 0;
        var confirmation = new RecordingConfirmation(true);
        var vm = new SharedConfigViewModel(() => Input(), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(confirmation.Asked);
        Assert.Equal(0, applied);
        Assert.Equal("Brak zmian do zapisania.", vm.Message);
    }

    [Theory]
    [InlineData("0.05")] // błąd zakresu (walidacja)
    [InlineData("abc")]  // błąd formatu
    [InlineData("1e26")] // „za duża”
    public async Task Save_WithAnErrorNeverAsks(string entered)
    {
        var confirmation = new RecordingConfirmation(true);
        var vm = new SharedConfigViewModel(() => Input(), (_, _, _) => Task.CompletedTask, confirmation);

        Field(vm, "timing.pollIntervalMs").Text = entered;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(confirmation.Asked);
        Assert.True(Field(vm, "timing.pollIntervalMs").HasError);
    }

    [Fact]
    public async Task Cancel_SaveAndBothResets_ApplyNothingAndKeepTheEntries()
    {
        int applied = 0;
        var confirmation = new RecordingConfirmation(false);
        var document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, new OverlayConfig { Scale = 1.4 });
        var vm = new SharedConfigViewModel(() => Input(document: document), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);

        Field(vm, "timing.idlePopupSeconds").Text = "10";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Nie zapisano — anulowano. Wpisy zostały w polach.", vm.Message);
        Assert.Equal("10", Field(vm, "timing.idlePopupSeconds").Text);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(Field(vm, "timing.idlePopupSeconds").IsChanged);

        await vm.ResetTimingCommand.ExecuteAsync(null);
        Assert.Equal("Nie przywrócono — anulowano.", vm.Message);
        await vm.ResetOverlayCommand.ExecuteAsync(null);
        Assert.Equal("Nie przywrócono — anulowano.", vm.Message);

        Assert.Equal(0, applied);
        Assert.Equal(3, confirmation.Asked.Count);
        Assert.Equal("10", Field(vm, "timing.idlePopupSeconds").Text);
        Assert.False(vm.IsBusy);
    }

    // =============================================================================================
    // Konfiguracja — ResetChanges
    // =============================================================================================

    [Fact]
    public async Task Reset_SectionNotOverriddenAndEqualToConfigJson_NoQuestionNoApply()
    {
        int applied = 0;
        var confirmation = new RecordingConfirmation(true);
        var vm = new SharedConfigViewModel(() => Input(), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);

        Assert.Empty(vm.ResetChanges(timing: true));
        Assert.Empty(vm.ResetChanges(timing: false));
        await vm.ResetTimingCommand.ExecuteAsync(null);
        Assert.Equal("Brak zmian — wartości już pochodzą z config.json / profilu grupy.", vm.Message);
        await vm.ResetOverlayCommand.ExecuteAsync(null);

        Assert.Empty(confirmation.Asked);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void Reset_OverriddenWithEqualValues_OnlyTheSourceRow()
    {
        var vm = new SharedConfigViewModel(() => Input(document: Document(new TimingConfig(), new OverlayConfig())), (_, _, _) => Task.CompletedTask);

        Assert.Equal([new SettingChange("Źródło wartości: czas i progi", "wspólna konfiguracja", "config.json / profil grupy")], vm.ResetChanges(true));
        Assert.Equal([new SettingChange("Źródło wartości: nakładka", "wspólna konfiguracja", "config.json / profil grupy")], vm.ResetChanges(false));
    }

    [Fact]
    public async Task Reset_DifferentValues_RowsPlusSource_AndOnlyThatSection()
    {
        var confirmation = new RecordingConfirmation(true);
        var calls = new List<(TimingConfig? Timing, OverlayConfig? Overlay)>();
        var document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, new OverlayConfig { Scale = 1.4 });
        var vm = new SharedConfigViewModel(() => Input(document: document), (t, o, _) => { calls.Add((t, o)); return Task.CompletedTask; }, confirmation);

        await vm.ResetTimingCommand.ExecuteAsync(null);

        IReadOnlyList<SettingChange> asked = Assert.Single(confirmation.Asked);
        Assert.Equal(
        [
            new SettingChange("Eskalacja po „Idę sprawdzić”", "1 min", "4 min"),
            new SettingChange("Źródło wartości: czas i progi", "wspólna konfiguracja", "config.json / profil grupy"),
        ], asked);
        Assert.Null(calls.Single().Timing);
        Assert.Equal(1.4, calls.Single().Overlay!.Scale);
    }

    // =============================================================================================
    // Konfiguracja — lista zgodna z tym, co zostaje zapisane
    // =============================================================================================

    /// <summary>
    /// Zapis jednego pola czasu bez wspólnej warstwy zapisuje do warstwy CAŁE dwie sekcje (czas i nakładkę) — od tej chwili
    /// cała grupa przestaje brać te wartości z config.json. „Przywróć…” pokazuje to wierszem „Źródło wartości”, a lista
    /// przed zapisem — nie.
    /// </summary>
    [Fact]
    public async Task Save_ListSaysThatSectionsStartComingFromTheSharedLayer()
    {
        var confirmation = new RecordingConfirmation(true);
        var calls = new List<(TimingConfig? Timing, OverlayConfig? Overlay)>();
        var vm = new SharedConfigViewModel(() => Input(), (t, o, _) => { calls.Add((t, o)); return Task.CompletedTask; }, confirmation);

        Field(vm, "timing.idlePopupSeconds").Text = "10";
        await vm.SaveCommand.ExecuteAsync(null);

        (TimingConfig? timing, OverlayConfig? overlay) = calls.Single();
        Assert.NotNull(timing);
        Assert.NotNull(overlay); // nakładka (nietknięta) też idzie do warstwy
        IReadOnlyList<SettingChange> asked = confirmation.Asked.Single();
        Assert.Contains(asked, c => c.Name.StartsWith("Źródło wartości", StringComparison.Ordinal) && c.Name.Contains("nakładka", StringComparison.Ordinal));
    }

    /// <summary>
    /// Okno potwierdzenia jest modalne: zmiana z innego komputera grupy przychodzi w trakcie (Dispatcher → RefreshFromOutside →
    /// Load, bo przy „Przywróć…” zwykle nie ma niezapisanych wpisów). Po „Zapisz” „Przywróć timing” zapisuje nakładkę
    /// sprzed pokazania okna — cofa zmianę nakładki zrobioną w tym czasie na innym komputerze.
    /// </summary>
    [Fact]
    public async Task Reset_ChangeFromAnotherComputerWhileTheDialogIsOpen_IsNotReverted()
    {
        SharedConfigDocument document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, new OverlayConfig { Scale = 1.4 });
        var calls = new List<(TimingConfig? Timing, OverlayConfig? Overlay)>();
        SharedConfigViewModel? vm = null;
        var confirmation = new RecordingConfirmation(true, whileOpen: () =>
        {
            document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, new OverlayConfig { Scale = 1.9 });
            vm!.RefreshFromOutside(changedElsewhere: true);
        });
        vm = new SharedConfigViewModel(() => Input(document: document), (t, o, _) => { calls.Add((t, o)); return Task.CompletedTask; }, confirmation);

        await vm.ResetTimingCommand.ExecuteAsync(null);

        Assert.Equal(1.9, calls.Single().Overlay!.Scale);
    }


    /// <summary>Kontener wybiera konstruktor z orkiestratorem i WSTRZYKNIĘTYM potwierdzeniem (nie „zgoda bez pytania”).</summary>
    [Fact]
    public async Task Container_ResolvesViewModelWithTheRegisteredConfirmation()
    {
        var orchestrator = (AppOrchestrator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AppOrchestrator));
        var config = new AppConfig { Enabled = false };
        SetField(orchestrator, "_cfg", config);
        SetField(orchestrator, "_baseCfg", config);
        string dir = Path.Combine(Path.GetTempPath(), "u1-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            SetField(orchestrator, "<SharedConfig>k__BackingField", new SharedConfigSync(new SharedConfigStore(Path.Combine(dir, "s.json")), "PC-A"));
            var confirmation = new RecordingConfirmation(false);
            using ServiceProvider provider = new ServiceCollection()
                .AddSingleton(orchestrator)
                .AddSingleton<ISettingsChangeConfirmation>(confirmation)
                .AddSingleton<SharedConfigViewModel>()
                .BuildServiceProvider();

            var vm = provider.GetRequiredService<SharedConfigViewModel>();
            Field(vm, "timing.idlePopupSeconds").Text = "10";
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Single(confirmation.Asked);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Program_RegistersTheDialogAndPassesItToBothSetupViews()
    {
        string program = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Program.cs"));
        Assert.Contains("services.AddSingleton<ISettingsChangeConfirmation, DialogSettingsChangeConfirmation>();", program, StringComparison.Ordinal);
        Assert.Matches(@"new SetupWindow\([^;]*sp\.GetRequiredService<ISettingsChangeConfirmation>\(\),\s*sp\.GetRequiredService<IUserMessages>\(\)\)\);", program);
        Assert.Matches(@"includeSecrets: true,\s*sp\.GetRequiredService<ISettingsChangeConfirmation>\(\),\s*sp\.GetRequiredService<IUserMessages>\(\)\)\);", program);
        Assert.DoesNotContain("AlwaysConfirm", program, StringComparison.Ordinal);

        string installed = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Services", "InstalledSetup.cs"));
        Assert.DoesNotContain("AlwaysConfirm", installed, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(installed, "new DialogSettingsChangeConfirmation\\(\\)").Count);
    }

    [Fact]
    public void TrayIcon_UsesTheSameKeyRuleAsInitialSetup()
    {
        string tray = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Services", "TrayIconService.cs"));
        Assert.Contains("bool noKey = MissingLegacyNetworkKey();", tray, StringComparison.Ordinal);
        Assert.Contains("bool noNetworkKey = MissingLegacyNetworkKey();", tray, StringComparison.Ordinal);
        // Definicja + dwa użycia; stary klucz czytany tylko w tej jednej metodzie, razem z warunkiem „bez grupy”.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(tray, @"MissingLegacyNetworkKey\(\)").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(tray, @"SecretKeys\.ClusterHmacKey"));
        Assert.Matches(@"MissingLegacyNetworkKey\(\) =>\s+_orchestrator\.Config\.Cluster\.Enabled && _orchestrator\.Config\.Cluster\.Group is null", tray);
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(target, value);
}
