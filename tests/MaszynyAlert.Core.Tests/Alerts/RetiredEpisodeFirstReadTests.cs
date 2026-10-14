using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;
using Xunit.Abstractions;

namespace MaszynyAlert.Core.Tests.Alerts;

/// <summary>
/// Powrót zamkniętego błędu przed pierwszym odczytem OCR: granice istniejącego okna łączenia.
/// Atrapy obrazu/OCR, ręczny zegar; żadnych połączeń z maszynami ani uruchamiania GUI.
/// </summary>
public sealed class RetiredEpisodeFirstReadTests(ITestOutputHelper output)
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
    public async Task ClosedAcknowledgedHomeReturns_RealMonitorWaitsForDelayedOcrWithoutNewRaised(bool group)
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

    private static readonly MappedMessage Contentless =
        new(null, "Błąd (nie odczytano treści)", "inny", string.Empty, string.Empty, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstReturnAtOrAfterTheWindow_StartsNewAndKeepsThatIdentityWhenOcrArrives(bool group)
    {
        var (state, clock, first, events) = RetireHome(group);
        clock.Advance(TimeSpan.FromSeconds(89)); // t=120, precisely the existing clear boundary.
        Alert pending = state.OnRedConfirmed(Machine, Contentless, null);
        clock.Advance(TimeSpan.FromSeconds(1));

        Alert described = state.OnRedConfirmed(Machine, Home, null);

        Assert.Equal(AlertStatus.Active, described.Status);
        Assert.Null(described.AcknowledgedAt);
        Assert.NotEqual(first.EpisodeId, described.EpisodeId);
        Assert.Equal(pending.EpisodeId, described.EpisodeId);
        Assert.Equal(pending.StartedAt, described.StartedAt);
        Assert.Single(events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredErrorOfAnotherMachine_DoesNotHoldTheFirstAlarm(bool group)
    {
        var (state, clock, first, events) = RetireHome(group);
        MachineConfig other = Machine with { Id = "aidite2", Name = "Aidite 2" };

        Alert current = state.OnRedConfirmed(other, Contentless, null);

        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Null(current.AcknowledgedAt);
        Assert.NotEqual(first.EpisodeId, current.EpisodeId);
        Assert.Equal(clock.UtcNow, current.StartedAt);
        Assert.Single(events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongOfflineGap_DoesNotHoldOrRejoinTheRetiredError(bool group)
    {
        var (state, clock, first, events) = RetireHome(group);
        state.OnMachineOffline(Machine, clock.UtcNow);
        clock.Advance(TimeSpan.FromSeconds(11));
        state.OnMachineOnline(Machine.Id);
        Alert pending = state.OnRedConfirmed(Machine, Contentless, null);
        clock.Advance(TimeSpan.FromSeconds(1));

        Alert described = state.OnRedConfirmed(Machine, Home, null);

        Assert.Equal(AlertStatus.Active, described.Status);
        Assert.NotEqual(first.EpisodeId, described.EpisodeId);
        Assert.Equal(pending.EpisodeId, described.EpisodeId);
        Assert.Null(described.AcknowledgedAt);
        Assert.Single(events, e => e.Kind == AlertEventKind.Raised);
    }

    private static (AlertStateMachine State, FakeClock Clock, Alert First, List<AlertEvent> Events) RetireHome(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        state.OnRedCleared(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.Tick();
        Assert.Empty(state.ActiveAlerts);
        var events = new List<AlertEvent>();
        state.Changed += events.Add;
        return (state, clock, first, events);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentReadAfterEmpty_DoesNotExtendTheClosedEpisodeInThePublicReport(bool group)
    {
        var clock = new FakeClock();
        var state = State(clock, group);
        using var dir = new TempDir("a1-first-read-history");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        state.Changed += change => journal.Record(change.Alert, "PC-test");
        Alert first = state.OnRedConfirmed(Machine, Home, null);
        state.AcknowledgeLocal(first.Key, "PC-test", "operator");
        clock.Advance(TimeSpan.FromSeconds(10));
        journal.Record(state.OnRedConfirmed(Machine, Home, null));
        state.OnRedCleared(Machine.Id);
        clock.Advance(TimeSpan.FromSeconds(31));
        state.Tick();
        Assert.Empty(state.ActiveAlerts);
        DowntimeEpisode oldHistory = Assert.Single(journal.Snapshot());
        Assert.Equal(first.StartedAt.AddSeconds(10), oldHistory.End);
        clock.Advance(TimeSpan.FromSeconds(37)); // t=78: first red frame of a DIFFERENT error.
        state.OnRedConfirmed(Machine, Contentless, null);
        clock.Advance(TimeSpan.FromSeconds(1));
        state.OnRedConfirmed(Machine, Servo, null);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Record(state.OnRedConfirmed(Machine, Servo, null));
        state.OnMachineDisabled(Machine.Id);

        double seconds = DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60;

        output.WriteLine($"group={group}; previous=0..10; new=78..84; expected=16s; actual={seconds}s");
        Assert.Null(journal.Error);
        Assert.Equal(16d, seconds, 6);
        Assert.Equal(oldHistory, Assert.Single(journal.Snapshot(), row => row.EpisodeId == first.EpisodeId));
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
