using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class InstructionSyncTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "instruction-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly InMemoryPeerHub _hub;
    private readonly ClusterConfig _config;
    private readonly GroupClusterNode _a, _b;
    private readonly InMemoryPeerBus _aBus, _bBus;
    private readonly SharedInstructions _aInstructions, _bInstructions;

    public InstructionSyncTests()
    {
        var file = GroupFileTests.Invitation();
        _config = new ClusterConfig { Enabled = true, Group = file.Identity };
        _hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), _clock);
        Directory.CreateDirectory(_directory);
        _aInstructions = Instructions("A");
        _bInstructions = Instructions("B");
        _aBus = _hub.CreateBus("A"); _bBus = _hub.CreateBus("B");
        _a = new GroupClusterNode(_config, _aBus, _clock, "test") { Instructions = _aInstructions };
        _b = new GroupClusterNode(_config with { Priority = 200 }, _bBus, _clock, "test") { Instructions = _bInstructions };
        _a.SetReadiness(new(true, false, [])); _b.SetReadiness(new(true, false, []));
    }

    private SharedInstructions Instructions(string host) =>
        new(new AlarmInstructionStore(Path.Combine(_directory, $"{host}.json")), host);

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

    /// <summary>Zapis w panelu: plik + natychmiastowe rozgłoszenie, dokładnie jak robi to AppOrchestrator.</summary>
    private async Task EditOnA(string key, string? text)
    {
        var entry = _aInstructions.Edit(key, text, _clock.UtcNow);
        await _a.BroadcastInstructionAsync(entry, default);
    }

    [Fact]
    public async Task ChangeOnOneComputerReachesTheOtherAtOnce()
    {
        await Start();
        await Tick(2);

        await EditOnA("code:705", "Wyjmij tackę i wciśnij Start.");

        Assert.Equal("Wyjmij tackę i wciśnij Start.", _bInstructions.Texts["code:705"]);
        Assert.Equal(_aInstructions.Hash, _bInstructions.Hash);
        Assert.Equal("A", _bInstructions.Entries["code:705"].EditedBy);
    }

    [Fact]
    public async Task RestoringTheDefaultRemovesTheTextOnTheOtherComputerToo()
    {
        await Start();
        await Tick(2);
        await EditOnA("code:705", "Własna treść");
        Assert.True(_bInstructions.Texts.ContainsKey("code:705"));

        await Tick(60);
        await EditOnA("code:705", null);

        Assert.False(_bInstructions.Texts.ContainsKey("code:705"));
        Assert.True(_bInstructions.Entries["code:705"].Deleted);
        Assert.Equal(_aInstructions.Hash, _bInstructions.Hash);
    }

    [Fact]
    public async Task ComputerSwitchedOnLaterGetsEverythingItMissed()
    {
        // B jest wyłączony: pakiety A nie mają go jak dosięgnąć.
        await _a.StartAsync(default);
        await Tick(2);
        await EditOnA("code:705", "Zmiana zrobiona przy wyłączonym B");
        await EditOnA("offline", "Sprawdź zasilanie.");
        Assert.Empty(_bInstructions.Texts);

        await _b.StartAsync(default);
        await Tick(15);

        Assert.Equal("Zmiana zrobiona przy wyłączonym B", _bInstructions.Texts["code:705"]);
        Assert.Equal("Sprawdź zasilanie.", _bInstructions.Texts["offline"]);
        Assert.Equal(_aInstructions.Hash, _bInstructions.Hash);
    }

    [Fact]
    public async Task ComputerWithTheOlderSetAsksAndTheOneWithTheNewerAnswers()
    {
        // Obie strony mają własny, inny wpis; żadna nie zna drugiego.
        _aInstructions.Edit("code:705", "Tylko u A", _clock.UtcNow);
        _bInstructions.Edit("offline", "Tylko u B", _clock.UtcNow.AddMinutes(-5));

        await Start();
        await Tick(25);

        Assert.Equal("Tylko u A", _bInstructions.Texts["code:705"]);
        Assert.Equal("Tylko u B", _aInstructions.Texts["offline"]);
        Assert.Equal(_aInstructions.Hash, _bInstructions.Hash);
    }

    [Fact]
    public async Task SimultaneousEditOfTheSameInstructionEndsWithTheSameTextOnBoth()
    {
        await Start();
        await Tick(2);

        var at = _clock.UtcNow;
        var fromA = _aInstructions.Edit("code:705", "Treść z A", at);
        var fromB = _bInstructions.Edit("code:705", "Treść z B", at);
        await _a.BroadcastInstructionAsync(fromA, default);
        await _b.BroadcastInstructionAsync(fromB, default);
        await Tick(2);

        // Remis czasu rozstrzyga nazwa komputera, więc obie strony wybierają ten sam tekst.
        Assert.Equal("Treść z B", _aInstructions.Texts["code:705"]);
        Assert.Equal("Treść z B", _bInstructions.Texts["code:705"]);
        Assert.Equal(_aInstructions.Hash, _bInstructions.Hash);
    }

    [Fact]
    public async Task LargeSetIsSentInSeveralPacketsAndArrivesComplete()
    {
        string text = new('x', AlarmInstructionStore.MaxTextLength);
        for (int code = 1000; code < 1100; code++)
            _aInstructions.Edit($"code:{code}", text, _clock.UtcNow);

        await Start();
        await Tick(15);

        Assert.Equal(100, _bInstructions.Texts.Count);
        Assert.All(_bInstructions.Texts.Values, value => Assert.Equal(text, value));
        Assert.Equal(_aInstructions.Hash, _bInstructions.Hash);
    }

    [Fact]
    public async Task EqualSetsProduceNoInstructionTrafficAtAll()
    {
        await Start();
        await Tick(2);
        await EditOnA("code:705", "Wspólna treść");
        Assert.Equal(_aInstructions.Hash, _bInstructions.Hash);

        int instructionMessages = 0;
        void Count(PeerMessage message)
        { if (message is InstructionsSyncRequest or InstructionsSyncReply or InstructionChangeMessage) instructionMessages++; }
        _aBus.Received += Count;
        _bBus.Received += Count;
        await Tick(120);

        Assert.Equal(0, instructionMessages);
    }

    [Fact]
    public async Task NodeWithoutSharedInstructionsIgnoresTheWholeExchange()
    {
        _b.Instructions = null;
        await Start();
        await Tick(2);
        await EditOnA("code:705", "Treść z A");
        await Tick(15);

        // Starszy komputer (bez wspólnych instrukcji) ma po prostu nic nie robić — i niczego nie zepsuć.
        Assert.Empty(_bInstructions.Texts);
        Assert.Single(_aInstructions.Texts);
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
