using System.IO;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Updates;
using static MaszynyAlert.App.Tests.SettingsChangeConfirmationTests;

namespace MaszynyAlert.App.Tests;

public sealed class SettingsChangeRound3Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "u1-round3-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static AppConfig BaseConfig() => new() { Enabled = false };

    private static SharedConfigViewInput Input(SharedConfigDocument? document)
    {
        AppConfig b = BaseConfig();
        return new SharedConfigViewInput(b, document?.ApplyTo(b) ?? b, document, "", null, false, null);
    }

    private static SharedConfigDocument Document(TimingConfig? timing, OverlayConfig? overlay, int minute = 0) => new()
    {
        Timing = timing, Overlay = overlay, EditedAt = Noon.AddMinutes(minute), EditedBy = "PC-B",
    };

    private static SharedConfigField Field(SharedConfigViewModel vm, string key) => vm.Fields.Single(f => f.Key == key);

    private static bool IsSource(SettingChange c) => c.Name.StartsWith("Źródło wartości", StringComparison.Ordinal);


    [Fact]
    public async Task Reset_ChangeDuringQuestion_AsksAgainWithTheNewList_AndAppliesTheNewSecondSection()
    {
        SharedConfigDocument document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, new OverlayConfig { Scale = 1.4 });
        var calls = new List<(TimingConfig? Timing, OverlayConfig? Overlay)>();
        int asked = 0;
        var confirmation = new RecordingConfirmation(true, whileOpen: () =>
        {
            if (++asked == 1)
                document = Document(new TimingConfig { EscalationAfterAckMs = 120000, PollIntervalMs = 2000 }, new OverlayConfig { Scale = 1.9 }, 1);
        });
        var vm = new SharedConfigViewModel(() => Input(document), (t, o, _) => { calls.Add((t, o)); return Task.CompletedTask; }, confirmation);

        await vm.ResetTimingCommand.ExecuteAsync(null);

        Assert.Equal(2, confirmation.Asked.Count);
        Assert.Contains(new SettingChange("Eskalacja po „Idę sprawdzić”", "1 min", "4 min"), confirmation.Asked[0]);
        Assert.Contains(new SettingChange("Eskalacja po „Idę sprawdzić”", "2 min", "4 min"), confirmation.Asked[1]);
        Assert.Contains(confirmation.Asked[1], c => c.Name == "Odstęp odczytów paska statusu");
        (TimingConfig? timing, OverlayConfig? overlay) = Assert.Single(calls);
        Assert.Null(timing);
        Assert.Equal(1.9, overlay!.Scale);
    }

    [Fact]
    public async Task Reset_ThreeChangesInARow_GivesUpWithoutApply()
    {
        int minute = 0;
        SharedConfigDocument document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, null);
        int applied = 0;
        var confirmation = new RecordingConfirmation(true, whileOpen: () =>
        {
            ++minute;
            document = Document(new TimingConfig { EscalationAfterAckMs = 60000 + minute * 1000 }, null, minute);
        });
        var vm = new SharedConfigViewModel(() => Input(document), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);

        await vm.ResetTimingCommand.ExecuteAsync(null);

        Assert.Equal(3, confirmation.Asked.Count);
        Assert.Equal(0, applied);
        Assert.Equal(SharedConfigViewModel.ChangedElsewhereMessage, vm.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Reset_AnotherComputerAlreadyResetDuringQuestion_NoChangesNoApply()
    {
        SharedConfigDocument? document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, null);
        int applied = 0;
        var confirmation = new RecordingConfirmation(true, whileOpen: () => document = null);
        var vm = new SharedConfigViewModel(() => Input(document), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);

        await vm.ResetTimingCommand.ExecuteAsync(null);

        Assert.Single(confirmation.Asked);
        Assert.Equal(0, applied);
        Assert.Equal("Brak zmian — wartości już pochodzą z config.json / profilu grupy.", vm.Message);
    }

    [Fact]
    public async Task Reset_CancelAtTheSecondQuestion_NoApply()
    {
        SharedConfigDocument document = Document(new TimingConfig { EscalationAfterAckMs = 60000 }, null);
        int asked = 0, applied = 0;
        var confirmation = new ScriptedConfirmation([true, false], () => { if (++asked == 1) document = Document(new TimingConfig { EscalationAfterAckMs = 30000 }, null, 1); });
        var vm = new SharedConfigViewModel(() => Input(document), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);

        await vm.ResetTimingCommand.ExecuteAsync(null);

        Assert.Equal(2, asked);
        Assert.Equal(0, applied);
        Assert.Equal("Nie przywrócono — anulowano.", vm.Message);
    }


    [Fact]
    public async Task Save_ChangeDuringDialog_AbortsAndKeepsEntries()
    {
        SharedConfigDocument? document = null;
        int applied = 0;
        var confirmation = new RecordingConfirmation(true, whileOpen: () => document = Document(null, new OverlayConfig { Scale = 1.9 }, 1));
        var vm = new SharedConfigViewModel(() => Input(document), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);

        Field(vm, "timing.idlePopupSeconds").Text = "10";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, applied);
        Assert.Equal(SharedConfigViewModel.ChangedElsewhereMessage, vm.Message);
        Assert.Equal("10", Field(vm, "timing.idlePopupSeconds").Text);
        Assert.True(vm.HasUnsavedChanges);
    }

    /// <summary>
    /// Zmiana przyszła PRZED zapisem, przy niezapisanych wpisach (komunikat „Odśwież”) — zapis też przerwany. „Odśwież”
    /// wczytuje wartości z innego komputera i KASUJE wpisy administratora (trzeba je wpisać ponownie); potem zapis działa.
    /// </summary>
    [Fact]
    public async Task Save_ChangeBeforeSaveWithUnsavedEntries_Aborts_ThenRefreshAndRetypeWorks()
    {
        SharedConfigDocument? document = null;
        int applied = 0;
        var vm = new SharedConfigViewModel(() => Input(document), (_, _, _) => { applied++; return Task.CompletedTask; }, new RecordingConfirmation(true));

        Field(vm, "timing.idlePopupSeconds").Text = "10";
        document = Document(null, new OverlayConfig { Scale = 1.9 }, 1);
        vm.RefreshFromOutside(changedElsewhere: true);
        Assert.Contains("Odśwież", vm.Message, StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(SharedConfigViewModel.ChangedElsewhereMessage, vm.Message);
        Assert.Equal(0, applied);

        vm.RefreshCommand.Execute(null);
        Assert.Equal("0", Field(vm, "timing.idlePopupSeconds").Text); // wpis „10” przepadł
        Field(vm, "timing.idlePopupSeconds").Text = "10";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, applied);
    }

    /// <summary>Złożenie jak w programie (SharedConfigSync na pliku): własny zapis nie daje fałszywego „zmieniło się” przy kolejnych zapisach.</summary>
    [Fact]
    public async Task OwnSaves_NeverLookLikeAChangeElsewhere()
    {
        var sync = new SharedConfigSync(new SharedConfigStore(Path.Combine(_directory, "shared-config.json")), "PC-A");
        var vm = Composed(sync, new RecordingConfirmation(true));

        foreach (string value in new[] { "5", "6", "7" })
        {
            Field(vm, "timing.idlePopupSeconds").Text = value;
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.StartsWith("Zapisano.", vm.Message, StringComparison.Ordinal);
        }
        await vm.ResetTimingCommand.ExecuteAsync(null);
        Assert.StartsWith("Przywrócono", vm.Message, StringComparison.Ordinal);
        Field(vm, "ui.overlay.scale").Text = "1.5";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("Zapisano.", vm.Message, StringComparison.Ordinal);
        Assert.Equal(1.5, sync.Current!.Overlay!.Scale);
    }

    [Fact]
    public async Task ChangeOfOnlyApproval_DoesNotBlockThePanelSave()
    {
        string path = Path.Combine(_directory, "shared-config.json");
        var sync = new SharedConfigSync(new SharedConfigStore(path), "PC-A");
        var confirmation = new RecordingConfirmation(true, whileOpen: () =>
        {
            new SharedConfigStore(path).SaveApproval(new UpdateApproval
            {
                Version = "0.4.120.0", FileName = "MaszynyAlert-Setup-0.4.120.0.exe", ApprovedBy = "PC-B", ApprovedAt = Noon,
            }, DateTimeOffset.UtcNow, "PC-B");
            sync.Reload();
        });
        var vm = Composed(sync, confirmation);

        Field(vm, "timing.idlePopupSeconds").Text = "5";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Zapisano.", vm.Message, StringComparison.Ordinal);
        Assert.Equal(5, sync.Current!.Timing!.IdlePopupSeconds);
    }


    [Fact]
    public void Source_TimingOverriddenOverlayNot_OnlyTheOverlayRow()
    {
        var vm = new SharedConfigViewModel(() => Input(Document(new TimingConfig(), null)), (_, _, _) => Task.CompletedTask);
        Field(vm, "timing.idlePopupSeconds").Text = "5";

        Assert.Equal([new SettingChange("Źródło wartości: nakładka", "config.json / profil grupy", "wspólna konfiguracja")],
            vm.PendingChanges().Where(IsSource));
    }

    [Fact]
    public void Source_BothOverridden_NoSourceRows()
    {
        var vm = new SharedConfigViewModel(() => Input(Document(new TimingConfig(), new OverlayConfig())), (_, _, _) => Task.CompletedTask);
        Field(vm, "ui.overlay.scale").Text = "1.3";

        Assert.DoesNotContain(vm.PendingChanges(), IsSource);
        Assert.Single(vm.PendingChanges());
    }

    [Fact]
    public async Task Source_RowsAloneNeverOpenTheDialog()
    {
        var confirmation = new RecordingConfirmation(true);
        var vm = new SharedConfigViewModel(() => Input(null), (_, _, _) => Task.CompletedTask, confirmation);

        Assert.Empty(vm.PendingChanges());
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(confirmation.Asked);
        Assert.Equal("Brak zmian do zapisania.", vm.Message);
    }


    [Theory]
    [InlineData("timing.reconnectBackoffMs", "2,5; 5", "2.5; 5 s")]
    [InlineData("timing.reconnectBackoffMs", "2, 5", "2; 5 s")]
    [InlineData("ui.overlay.scale", "1,50", "1.5 ×")]
    [InlineData("ui.overlay.opacity", " 0,7 ", "0.7")]
    [InlineData("timing.ocrMaxAttempts", " 4 ", "4 prób")]
    [InlineData("ui.overlay.layout", "two-columns", "two-columns")]
    public void After_IsTheParsedValueInFieldFormat(string key, string entered, string expected)
    {
        var vm = new SharedConfigViewModel(() => Input(null), (_, _, _) => Task.CompletedTask);
        Field(vm, key).Text = entered;

        Assert.Equal(expected, vm.PendingChanges().Single(c => !IsSource(c)).After);
    }

    [Fact]
    public void After_FlagStaysOnOff()
    {
        var vm = new SharedConfigViewModel(() => Input(null), (_, _, _) => Task.CompletedTask);
        Field(vm, "ui.overlay.clickThrough").Flag = true;

        Assert.Equal(new SettingChange("Przenikanie kliknięć", "wyłączone", "włączone"), vm.PendingChanges().Single(c => !IsSource(c)));
    }

    /// <summary>
    /// Wpis innym zapisem tej samej wartości („2.0” zamiast „2”, „2,0” zamiast „2”) to dla listy zmiana „2 min → 2 min”: okno pyta
    /// o nic, a zapis (bez warstwy) i tak przenosi obie sekcje do warstwy wspólnej. Wymaganie: brak zmian = bez okna.
    /// </summary>
    [Theory]
    [InlineData("2.0")]
    [InlineData("2,0")]
    [InlineData(" 2")]
    public async Task SameValueWrittenDifferently_IsNotAChange(string entered)
    {
        var confirmation = new RecordingConfirmation(true);
        int applied = 0;
        var vm = new SharedConfigViewModel(() => Input(null), (_, _, _) => { applied++; return Task.CompletedTask; }, confirmation);
        Assert.Equal("4", Field(vm, "timing.escalationAfterAckMs").Text);

        Field(vm, "timing.escalationAfterAckMs").Text = entered.Replace("2", "4", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(confirmation.Asked);
        Assert.Equal(0, applied);
    }

    // ==============================

    private static SharedConfigViewModel Composed(SharedConfigSync sync, ISettingsChangeConfirmation confirmation)
    {
        var config = BaseConfig();
        return new SharedConfigViewModel(
            () => new SharedConfigViewInput(config, sync.Current?.ApplyTo(config) ?? config, sync.Current, "", sync.LoadError, false, sync.Path),
            (timing, overlay, _) => { sync.Edit(timing, overlay, DateTimeOffset.UtcNow); return Task.CompletedTask; }, confirmation);
    }

    private sealed class ScriptedConfirmation(bool[] answers, Action whileOpen) : ISettingsChangeConfirmation
    {
        private int _index;

        public bool Confirm(IReadOnlyList<SettingChange> changes)
        {
            whileOpen();
            return answers[Math.Min(_index++, answers.Length - 1)];
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
