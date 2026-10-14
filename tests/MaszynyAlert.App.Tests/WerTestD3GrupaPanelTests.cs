using System.IO;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using static MaszynyAlert.App.Tests.SettingsChangeConfirmationTests;

namespace MaszynyAlert.App.Tests;

public sealed class WerTestD3GrupaPanelTests
{
    private static AppConfig HallLikeConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        AppConfig example = ConfigLoader.LoadAppConfig(Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium."), "config", "config.example.json"));
        return example with
        {
            Machines = example.Machines
                .Select(m => m.Id is "premile" or "cameo1" ? m with { Profile = "aidite-light2", RequiresPassword = true } : m).ToList(),
        };
    }

    private static bool AiditeOnly(string machineId) => machineId.StartsWith("aidite", StringComparison.Ordinal);

    private sealed class Recorder
    {
        public List<(TimingConfig? Timing, OverlayConfig? Overlay, IReadOnlyDictionary<string, bool>? Machines)> Saves { get; } = [];

        public Task Apply(TimingConfig? timing, OverlayConfig? overlay, IReadOnlyDictionary<string, bool>? machines, CancellationToken ct)
        {
            Saves.Add((timing, overlay, machines));
            return Task.CompletedTask;
        }
    }

    private static SharedConfigViewModel Create(AppConfig config, bool inGroup, Func<string, bool> hasPassword, Recorder recorder,
        SharedConfigDocument? document = null) =>
        new(() => new SharedConfigViewInput(config, document?.ApplyTo(config) ?? config, document,
                inGroup ? "Konfiguracja jest wspólna dla grupy; zgodna z 3 z 3 komputerami." : "", null, inGroup, null, hasPassword),
            recorder.Apply, new RecordingConfirmation(true));

    private static SharedConfigField Row(SharedConfigViewModel vm, string machineId) =>
        vm.MachineFields.Single(f => f.Key == SharedConfigViewModel.MachineKey(machineId));

    [Fact]
    public async Task WGrupie_KomputerZWylaczonymMonitorowaniem_WlaczenieMaszynyBezHasla_TakzeOdmowa()
    {
        AppConfig thisPc = HallLikeConfig() with { Enabled = false };
        var recorder = new Recorder();
        int asked = 0;
        var vm = Create(thisPc, inGroup: true, id => { asked++; return AiditeOnly(id); }, recorder);

        Row(vm, "premile").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        AppConfig otherPcAfter = HallLikeConfig() with
        {
            Machines = HallLikeConfig().Machines.Select(m => m.Id == "premile" ? m with { Enabled = true } : m).ToList(),
        };
        WatcherReadiness other = WatcherReadiness.Evaluate(otherPcAfter, AiditeOnly, true, GroupCompatibility.Compatible);
        Assert.False(other.CanObserve);
        Assert.Equal(["premile"], other.MissingPasswordMachineIds);

        string saved = recorder.Saves.Count == 0 ? "(brak zapisu)"
            : string.Join(",", recorder.Saves[0].Machines?.Where(p => p.Value).Select(p => p.Key) ?? []);
        Assert.True(recorder.Saves.Count == 0,
            $"Panel zapisał włączenie Premile w grupie bez hasła na tym komputerze (globalny enabled:false; pytań o hasło: {asked}); "
            + $"włączone po zapisie: {saved}.");
        Assert.Equal(SharedConfigViewModel.MissingPasswordInGroupMessage, Row(vm, "premile").Error);
    }

    [Fact]
    public async Task WGrupie_ZapisCzasuIPrzywrocCzasu_PrzyMaszynieWlaczonejWczesniejBezHasla_PrzechodzaBezPrzelacznikow()
    {
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig { AlertSoundRepeatMs = 30000 },
            Machines = SharedConfigDocument.CanonicalMachines(new Dictionary<string, bool> { ["premile"] = true }),
            EditedAt = new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero), EditedBy = "PC-HALA-2",
        };
        var recorder = new Recorder();
        var vm = Create(HallLikeConfig(), inGroup: true, AiditeOnly, recorder, document);
        Assert.True(Row(vm, "premile").Flag);

        vm.Fields.Single(f => f.Key == "timing.alertSoundRepeatMs").Text = "0.75";
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.ResetTimingCommand.ExecuteAsync(null);

        Assert.Equal(2, recorder.Saves.Count);
        Assert.Equal(45000, recorder.Saves[0].Timing!.AlertSoundRepeatMs);
        Assert.Null(recorder.Saves[0].Machines);
        Assert.Null(recorder.Saves[1].Timing);
        Assert.Null(recorder.Saves[1].Machines);
        Assert.All(vm.MachineFields, f => Assert.Equal("", f.Error));
    }
}
