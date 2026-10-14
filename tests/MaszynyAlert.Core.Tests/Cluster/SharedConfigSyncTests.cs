using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class SharedConfigSyncTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "shared-config-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly InMemoryPeerHub _hub;
    private readonly ClusterConfig _config;
    private readonly GroupClusterNode _a, _b;
    private readonly InMemoryPeerBus _aBus, _bBus;
    private readonly SharedConfigSync _aConfig, _bConfig;

    public SharedConfigSyncTests()
    {
        var file = GroupFileTests.Invitation();
        _config = new ClusterConfig { Enabled = true, Group = file.Identity };
        _hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), _clock);
        Directory.CreateDirectory(_directory);
        _aConfig = Layer("A");
        _bConfig = Layer("B");
        _aBus = _hub.CreateBus("A"); _bBus = _hub.CreateBus("B");
        _a = new GroupClusterNode(_config, _aBus, _clock, "test") { SharedConfig = _aConfig };
        _b = new GroupClusterNode(_config with { Priority = 200 }, _bBus, _clock, "test") { SharedConfig = _bConfig };
        _a.SetReadiness(new(true, false, [])); _b.SetReadiness(new(true, false, []));
    }

    private SharedConfigSync Layer(string host) =>
        new(new SharedConfigStore(Path.Combine(_directory, $"{host}.json")), host);

    private async Task Start() { await _a.StartAsync(default); await _b.StartAsync(default); }

    private async Task Tick(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            await _a.TickAsync(default);
            await _b.TickAsync(default);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Zapis w Panelu: plik + natychmiastowe rozgłoszenie, dokładnie jak robi to AppOrchestrator.</summary>
    private async Task EditOnA(TimingConfig? timing, OverlayConfig? overlay)
    {
        var document = _aConfig.Edit(timing, overlay, _clock.UtcNow);
        await _a.BroadcastSharedConfigAsync(document, default);
    }

    [Fact]
    public async Task ChangeOnOneComputerReachesTheOtherAtOnce()
    {
        await Start();
        await Tick(2);

        await EditOnA(new TimingConfig { AlertSoundRepeatMs = 30000 }, null);

        Assert.Equal(30000, _bConfig.Current!.Timing!.AlertSoundRepeatMs);
        Assert.Equal("A", _bConfig.Current.EditedBy);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);
    }

    [Fact]
    public async Task ComputerSwitchedOffDuringTheChangeCatchesUpFromHeartbeatsAfterItReturns()
    {
        await Start();
        await Tick(2);
        _hub.Partition("A", "B");

        await EditOnA(null, new OverlayConfig { Enabled = true, Opacity = 0.9, Scale = 1.5, Position = OverlayPosition.BottomRight });
        await Tick(5);
        Assert.Null(_bConfig.Current);

        _hub.Heal();
        await Tick(15);

        Assert.Equal(1.5, _bConfig.Current!.Overlay!.Scale);
        Assert.Equal(OverlayPosition.BottomRight, _bConfig.Current.Overlay.Position);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);
    }

    [Fact]
    public async Task OlderDocumentOnTheOtherComputerDoesNotUndoTheNewerOne()
    {
        // B ma starszą własną warstwę, A nowszą; obie strony mają skończyć na dokumencie z A.
        _bConfig.Edit(new TimingConfig { AlertSoundRepeatMs = 90000 }, null, _clock.UtcNow.AddMinutes(-5));
        _aConfig.Edit(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, _clock.UtcNow);

        await Start();
        await Tick(15);

        Assert.Equal(30000, _bConfig.Current!.Timing!.AlertSoundRepeatMs);
        Assert.Equal(30000, _aConfig.Current!.Timing!.AlertSoundRepeatMs);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);
    }

    [Fact]
    public async Task RestoringTheValuesFromConfigJsonTravelsToTheOtherComputerToo()
    {
        await Start();
        await Tick(2);
        await EditOnA(new TimingConfig { AlertSoundRepeatMs = 30000 }, null);
        Assert.False(_bConfig.Current!.IsEmpty);

        await Tick(30);
        await EditOnA(null, null);
        await Tick(2);

        // „Przywróć wartości z config.json” to pusty dokument — i on też musi się rozejść, inaczej
        // pozostałe komputery zostałyby z nadpisaniem, którego nikt już nie chce.
        Assert.True(_bConfig.Current!.IsEmpty);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);
    }

    [Fact]
    public async Task EqualFingerprintsProduceNoSharedConfigTrafficAtAll()
    {
        await Start();
        await Tick(2);
        await EditOnA(new TimingConfig { AlertSoundRepeatMs = 30000 }, null);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);

        int messages = 0;
        void Count(PeerMessage message) { if (message is SharedConfigMessage) messages++; }
        _aBus.Received += Count;
        _bBus.Received += Count;
        await Tick(120);

        Assert.Equal(0, messages);
    }

    [Fact]
    public async Task NodeWithoutSharedConfigurationIgnoresTheWholeExchange()
    {
        _b.SharedConfig = null;
        await Start();
        await Tick(2);
        await EditOnA(new TimingConfig { AlertSoundRepeatMs = 30000 }, null);
        await Tick(15);

        // Starszy komputer (bez wspólnej konfiguracji) ma po prostu nic nie robić — i niczego nie zepsuć.
        Assert.Null(_bConfig.Current);
        Assert.NotNull(_aConfig.Current);
    }

    [Fact]
    public async Task ReceivedChangeIsReportedSoTheRuntimeCanBeRecomputed()
    {
        int changed = 0;
        _b.SharedConfigChanged += () => changed++;
        await Start();
        await Tick(2);

        await EditOnA(new TimingConfig { AlertSoundRepeatMs = 30000 }, null);
        await Tick(30);

        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task ApprovalOnOneComputerReachesTheOtherAndSurvivesALaterConfigurationEdit()
    {
        await Start();
        await Tick(2);

        var approval = new UpdateApproval
        {
            Version = "0.4.26.0", FileName = "MaszynyAlert-Setup-0.4.26.0.exe", ApprovedBy = "A", ApprovedAt = _clock.UtcNow,
        };
        var document = _aConfig.Approve(approval, _clock.UtcNow);
        Assert.Equal(approval, _aConfig.Current!.UpdateApproval);
        await _a.BroadcastSharedConfigAsync(document, default);

        Assert.Equal(approval, _bConfig.Current!.UpdateApproval);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);

        _clock.Advance(TimeSpan.FromSeconds(5));
        var edited = _bConfig.Edit(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, _clock.UtcNow);
        await _b.BroadcastSharedConfigAsync(edited, default);

        Assert.Equal(approval, _aConfig.Current!.UpdateApproval);
        Assert.Equal(30000, _aConfig.Current.Timing!.AlertSoundRepeatMs);
    }

    [Fact]
    public async Task MachineSwitchedOnOneComputer_ReachesTheOther_AndSurvivesALaterTimingEdit()
    {
        await Start();
        await Tick(2);
        var localOnB = GroupFileTests.Invitation().Profile.ApplyTo(new AppConfig { Enabled = true });
        string someId = localOnB.Machines[0].Id;
        bool wasEnabled = localOnB.Machines[0].Enabled;

        var document = _aConfig.Edit(null, null, _clock.UtcNow, machines: new Dictionary<string, bool> { [someId] = !wasEnabled });
        await _a.BroadcastSharedConfigAsync(document, default);

        Assert.Equal(!wasEnabled, _bConfig.Current!.Machines![someId].Enabled);
        Assert.Equal(!wasEnabled, _bConfig.Current.ApplyTo(localOnB).Machines.Single(m => m.Id == someId).Enabled);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);

        _clock.Advance(TimeSpan.FromSeconds(5));
        var edited = _bConfig.Edit(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, _clock.UtcNow);
        await _b.BroadcastSharedConfigAsync(edited, default);

        Assert.Equal(!wasEnabled, _aConfig.Current!.Machines![someId].Enabled);
        Assert.Equal(30000, _aConfig.Current.Timing!.AlertSoundRepeatMs);
        Assert.Equal(_aConfig.Hash, _bConfig.Hash);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        // Sprzątanie katalogu tymczasowego nie może wywalić testu (jak TempDir).
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
