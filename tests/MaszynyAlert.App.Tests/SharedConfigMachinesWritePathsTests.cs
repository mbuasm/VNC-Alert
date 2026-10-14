using System.IO;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;
using static MaszynyAlert.App.Tests.SettingsChangeConfirmationTests;

namespace MaszynyAlert.App.Tests;

public sealed class SharedConfigMachinesWritePathsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "d3-write-paths-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private string FilePath => Path.Combine(_directory, "shared-config", "shared-config.json");

    private static string Describe(SharedConfigDocument? document) => document?.Machines is not { } machines ? "null"
        : string.Join(",", machines.Select(p => p.Key + "=" + (p.Value.Enabled ? "on" : "off")));

    private static AppConfig ExampleConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return ConfigLoader.LoadAppConfig(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Brak repozytorium."),
            "config", "config.example.json"));
    }

    [Fact]
    public async Task PrzywrocNakladke_ZostawiaPrzelacznikiZPliku()
    {
        AppConfig config = ExampleConfig();
        new SharedConfigStore(FilePath).Save(null, new OverlayConfig { Scale = 1.5 }, Noon, "PC-B",
            machines: new Dictionary<string, bool> { ["amm580"] = true });
        var sync = new SharedConfigSync(new SharedConfigStore(FilePath), "PC-A");
        sync.Reload();
        var vm = new SharedConfigViewModel(
            () => new SharedConfigViewInput(config, sync.Current?.ApplyTo(config) ?? config, sync.Current, "", sync.LoadError, false, sync.Path),
            (timing, overlay, machines, _) => { sync.Edit(timing, overlay, DateTimeOffset.UtcNow, machines); return Task.CompletedTask; },
            new RecordingConfirmation(true));

        await vm.ResetOverlayCommand.ExecuteAsync(null);

        SharedConfigDocument file = new SharedConfigStore(FilePath).Load()!;
        Assert.Null(file.Overlay);
        Assert.Equal("PC-A", file.EditedBy);
        Assert.Equal("amm580=on", Describe(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
