using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;
using Xunit.Abstractions;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class Audit20261010A1EpisodeTests(ITestOutputHelper output)
{
    private static readonly MachineConfig Machine = new()
    {
        Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Port = 9100, Profile = "opencnc",
    };

    private const string HomeText = "No.1200 [p]Request home alarm";
    private static readonly MappedMessage Home = new("code-1200", "Bazowanie (Home)", "restart",
        HomeText, "NO I2OO P REQUEST HOME ALARM", true) { Priority = 50 };
    private static readonly MappedMessage Servo = new("code-705", "Servo 6", "restart",
        "No.705 [N]Axis 6 servo alarm", "NO 7O5 N AXIS 6 SERVO ALARM", true) { Priority = 90 };

    private static AlertStateMachine State(FakeClock clock, bool group, TimingConfig? timing = null)
    {
        var result = new AlertStateMachine(clock, timing ?? new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        result.UseGroupProtocol(group);
        return result;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A1_01_ClosedAcknowledgedHomeReturns_RealMonitorWaitsForDelayedOcrWithoutNewRaised(bool group)
    {
        var clock = new FakeClock();
        var timing = new TimingConfig();
        var state = State(clock, group, timing);
        var profile = TestPaths.Profile("opencnc") with { MainScreen = null, Idle = null };
        RgbaImage redCrop = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, "alarm-crops-opencnc", "aidite1_20260912-114258_blad.png"));
        RgbaImage blankCrop = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, "alarm-crops-opencnc", "aidite4_20260912-101033_przerwa.png"));
        Assert.True(new RedTextDetector(profile).Analyze(redCrop).IsRed);
        Assert.False(new RedTextDetector(profile).Analyze(blankCrop).IsRed);
        RgbaImage red = SyntheticScreens.FullFrame(profile, redCrop);
        RgbaImage blank = SyntheticScreens.FullFrame(profile, blankCrop);
        var source = new FakeScreenSource(Machine.Id, red);
        var ocr = new GatedOcr();
        using var cts = new CancellationTokenSource();
        var events = new List<AlertEvent>();
        state.Changed += events.Add;
        Alert? first = null;
        Alert? whileOcrPending = null;
        int cycle = 0;
        var monitor = new MachineMonitor(Machine, profile, () => source, null, ocr,
            new MessageMapper(TestPaths.ExampleRules), state, clock, timing, new NoCaptures(),
            new RecordingLoggerFactory().CreateLogger("A1"), (interval, _) =>
            {
                clock.Advance(interval);
                state.Tick();
                cycle++;
                if (cycle == 2)
                {
                    Assert.Equal(1, ocr.Calls);
                    Assert.False(ocr.Requests[0].Task.IsCompleted);
                    ocr.Complete(0, HomeText);
                }
                if (cycle == 5)
                {
                    first = Assert.Single(state.ActiveAlerts);
                    Assert.Equal("rule:code-1200", first.Key.Discriminator);
                    Assert.True(state.AcknowledgeLocal(first.Key, "PC-test", "operator"));
                }
                if (cycle == 10) source.Frame = blank;
                if (cycle == 45)
                {
                    Assert.Empty(state.ActiveAlerts); // The 30 s close timer really fired.
                    Assert.Contains(events, e => e.Kind == AlertEventKind.Closed && e.Alert.EpisodeId == first!.EpisodeId);
                    source.Frame = red;
                }
                if (cycle == 48)
                {
                    Assert.Equal(2, ocr.Calls);
                    Assert.False(ocr.Requests[1].Task.IsCompleted); // Real pending OCR across several monitor cycles.
                    whileOcrPending = state.ActiveAlerts.SingleOrDefault();
                }
                if (cycle == 50) ocr.Complete(1, HomeText);
                if (cycle >= 55) cts.Cancel();
                return Task.CompletedTask;
            });

        // As in MachineMonitorAsyncOcrTests.Harness: use the default context so releasing a gated OCR request
        // completes its ConfigureAwait(false) continuation before the next deterministic cycle.
        await Task.Run(() => monitor.RunAsync(cts.Token));

        Alert current = Assert.Single(state.ActiveAlerts);
        Assert.NotNull(first);
        Assert.Equal("rule:code-1200", current.Key.Discriminator);
        output.WriteLine($"group={group}; Raised={events.Count(e => e.Kind == AlertEventKind.Raised)}; pending={whileOcrPending?.Status}/{whileOcrPending?.Key.Discriminator}; final={current.Status}; first={first.StartedAt:O}; current={current.StartedAt:O}");
        Assert.Single(events, e => e.Kind == AlertEventKind.Raised);
        Assert.True(whileOcrPending is null || whileOcrPending.Status == AlertStatus.Acknowledged,
            "A contentless first read must not restart ringing while the same-message return is being resolved.");
        Assert.Equal(AlertStatus.Acknowledged, current.Status);
        Assert.Equal(first.StartedAt, current.StartedAt);
        if (group) Assert.Contains(first.EpisodeId, current.SupersededEpisodeIds);
        else Assert.Equal(first.EpisodeId, current.EpisodeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A1_02_DisabledMachine_ReturningKnownErrorStartsANewUnacknowledgedEpisode(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        clock.Advance(TimeSpan.FromSeconds(15));
        state.OnMachineDisabled(Machine.Id);
        Assert.Empty(state.ActiveAlerts);
        clock.Advance(TimeSpan.FromSeconds(20));
        var events = new List<AlertEvent>();
        state.Changed += events.Add;

        // This is the first known message after the operator enables the machine again.
        // Program-abort observations and suppression-enabled profiles can reach this path directly.
        Alert current = state.OnRedConfirmed(Machine, Home, null);
        output.WriteLine($"group={group}; status={current.Status}; kind={Assert.Single(events).Kind}; first={first.StartedAt:O}; current={current.StartedAt:O}; now={clock.UtcNow:O}");

        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Equal(clock.UtcNow, current.StartedAt);
        Assert.NotEqual(first.EpisodeId, current.EpisodeId);
        Assert.Empty(current.SupersededEpisodeIds);
        Assert.Null(current.AcknowledgedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(events).Kind);
    }

    [Fact]
    public void A1_03_BetterTextInReconciledEpisode_DoesNotMakeOldRemoteAckValidAgain()
    {
        var clock = new FakeClock();
        var state = State(clock, group: true);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        clock.Advance(TimeSpan.FromSeconds(20));
        DateTimeOffset reconciliation = clock.UtcNow;
        Guid superseded = Guid.NewGuid();
        state.ReconcileGroupState([first with { ReconciledAt = reconciliation, SupersededEpisodeIds = [superseded] }], []);
        clock.Advance(TimeSpan.FromSeconds(10));
        Alert current = state.OnRedConfirmed(Machine, Servo, null); // Better OCR description, same episode.
        Assert.Equal(first.EpisodeId, current.EpisodeId);
        DateTimeOffset staleAck = reconciliation.AddSeconds(-5);

        state.ApplyRemoteAck(new AckRecord(current.Key, "late-PC", "operator", staleAck) { EpisodeId = current.EpisodeId });

        Alert afterAck = Assert.Single(state.ActiveAlerts);
        output.WriteLine($"reconciliation before={reconciliation:O}; after={current.ReconciledAt:O}; predecessors={current.SupersededEpisodeIds.Count}; status after stale ACK={afterAck.Status}");
        Assert.Equal(AlertStatus.Active, afterAck.Status);
        Assert.Equal(reconciliation, current.ReconciledAt);
        Assert.Contains(superseded, current.SupersededEpisodeIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A1_04_DisabledGap_DoesNotBecomeDowntimeWhenTheErrorReturns(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        using var dir = new TempDir("a1-disabled-gap");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        state.Changed += change => journal.Record(change.Alert, "PC-test");
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        clock.Advance(TimeSpan.FromSeconds(15));
        journal.Record(state.OnRedConfirmed(Machine, Home, null));
        state.OnMachineDisabled(Machine.Id);
        Assert.Equal(15d, DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60, 6);

        // The machine is deliberately unmonitored from 08:00:15 until 08:00:35.
        clock.Advance(TimeSpan.FromSeconds(20));
        state.OnRedConfirmed(Machine, Home, null);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Record(state.OnRedConfirmed(Machine, Home, null));
        double whileOpen = DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60;
        state.OnMachineDisabled(Machine.Id);
        double afterClose = DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60;

        Assert.Null(journal.Error);
        output.WriteLine($"group={group}; observed downtime=15+5=20 s; disabled gap=20 s; report while open={whileOpen} s; report after close={afterClose} s");
        // This tests the measured time independently of the policy of inheriting ACK after disable.
        Assert.Equal(20d, afterClose, 6);
        Assert.Equal(20d, whileOpen, 6);
    }
    private sealed class GatedOcr : IOcrEngine
    {
        public string Name => "A1-gated";
        public bool IsAvailable => true;
        public string? LanguageTag => "en-US";
        public List<TaskCompletionSource<OcrResult>> Requests { get; } = [];
        public int Calls => Requests.Count;

        public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Completion is controlled by monitor cycles, never Task.FromResult.
            var pending = new TaskCompletionSource<OcrResult>();
            Requests.Add(pending);
            return pending.Task;
        }

        public void Complete(int request, string text) => Requests[request].SetResult(new OcrResult(text, [text]));
    }

    private sealed class NoCaptures : ICaptureStore
    {
        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;
        public void Prune(TimeSpan retention) { }
    }
}
