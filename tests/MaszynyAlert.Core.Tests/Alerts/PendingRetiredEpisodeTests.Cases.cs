using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed partial class PendingRetiredEpisodeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealMonitor_DifferentFirstRead_PublicReportIs16SecondsIncludingSynchronousOcrWork(bool group, bool synchronousWork)
    {
        var ocr = new GatedOcr();
        using var h = new ReturningMonitor(group, ocr);
        ocr.BeforeRecognize = _ => { if (synchronousWork) h.Clock.AdvanceMs(1000); };
        h.AfterCycle = cycle =>
        {
            if (cycle == 2)
            {
                Assert.Empty(h.State.ActiveAlerts);
                Assert.Empty(h.Events);
                Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot()));
                ocr.Complete(0, Servo.RawText);
            }
            if (cycle == 3) Assert.Equal("Servo 6", h.Snapshot.LastShortText);
        };
        await h.Run(8);
        Alert current = Assert.Single(h.State.ActiveAlerts);
        h.Journal.Record(current);
        h.State.OnMachineDisabled(Machine.Id);

        Assert.Equal(FakeClock.DefaultStart.AddSeconds(78), current.StartedAt);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(84), current.LastSeenAt);
        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Equal("rule:code-705", current.Key.Discriminator);
        Assert.Empty(current.SupersededEpisodeIds);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.DoesNotContain(h.Events, e => e.Alert.EpisodeId == h.Old.EpisodeId);
        Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId == h.Old.EpisodeId));
        Assert.Equal(16d, DowntimeReport.Build(h.Journal.Snapshot(), TimeZoneInfo.Utc).Sum(row => row.Minutes) * 60, 6);
    }

    [Fact]
    public async Task RealMonitor_FirstAlarmIsPublishedBeforeSynchronousOcrWork()
    {
        var ocr = new GatedOcr();
        using var h = new ReturningMonitor(false, ocr, seedClosed: false);
        bool raisedBeforeOcr = false;
        ocr.BeforeRecognize = _ =>
        {
            raisedBeforeOcr = h.Events.Any(e => e.Kind == AlertEventKind.Raised);
            h.Clock.AdvanceMs(1000);
        };
        h.AfterCycle = cycle => { if (cycle == 2) ocr.Complete(0, HomeText); };
        await h.Run(4);
        Assert.True(raisedBeforeOcr, "Color must publish an ordinary first alarm before OCR executes any synchronous work.");
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(1), Assert.Single(h.State.ActiveAlerts).StartedAt);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IgnoredFirstReadUsesContentlessFallback_ThroughTickOrCurrentFrame(bool group, bool nextFrame)
    {
        using var h = new ClosedEpisode(group);
        var read = new TaskCompletionSource<MappedMessage>();
        h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        var ignored = new MessageMapper(TestPaths.ExampleRules).Map(
            "All procedures have been processed according to the production plan", "opencnc");
        Assert.True(ignored.Ignored);
        read.SetResult(ignored);
        if (nextFrame) h.State.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task);
        else h.State.Tick();

        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal("text:empty", current.Key.Discriminator);
        Assert.Equal(Contentless.ShortText, current.ShortText);
        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
    }

    [Theory]
    [InlineData(false, "no-engine")]
    [InlineData(true, "no-engine")]
    [InlineData(false, "max-zero")]
    [InlineData(true, "max-zero")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "failure")]
    [InlineData(true, "failure")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    public async Task RealMonitor_ReturnWithoutUsableOcr_RaisesWithoutWaitingForRejoinWindow(bool group, string outcome)
    {
        var ocr = new GatedOcr { IsAvailable = outcome != "no-engine" };
        var timing = new TimingConfig { OcrMaxAttempts = outcome == "max-zero" ? 0 : 3 };
        using var h = new ReturningMonitor(group, ocr, timing);
        bool noRead = outcome is "no-engine" or "max-zero";
        h.AfterCycle = cycle =>
        {
            if (cycle == 2)
            {
                if (noRead)
                {
                    Assert.Equal(0, ocr.Calls);
                    Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
                }
                else
                {
                    Assert.Empty(h.State.ActiveAlerts);
                    Assert.Empty(h.Events);
                    if (outcome == "failure") ocr.Requests[0].SetException(new InvalidOperationException("OCR test failure"));
                    else if (outcome == "unavailable") ocr.Requests[0].SetResult(OcrResult.NotAvailable);
                    else
                    {
                        // One logical first read includes all raw fallback recognitions. Release each explicitly.
                        for (int i = 0; i < ocr.Requests.Count; i++) ocr.Requests[i].SetResult(OcrResult.Empty);
                    }
                }
            }
            if (cycle == 3)
            {
                Alert current = Assert.Single(h.State.ActiveAlerts);
                Assert.Equal("text:empty", current.Key.Discriminator);
                Assert.Equal(AlertStatus.Active, current.Status);
                Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
            }
        };
        await h.Run(7);
        Alert last = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(78), last.StartedAt);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId == h.Old.EpisodeId));
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMonitor_EngineBecomesUnavailableDuringFirstRead_RaisesOnNextRedCycle(bool group)
    {
        var ocr = new GatedOcr();
        using var h = new ReturningMonitor(group, ocr);
        h.AfterCycle = cycle =>
        {
            if (cycle == 2)
            {
                Assert.Empty(h.State.ActiveAlerts);
                Assert.False(ocr.Requests[0].Task.IsCompleted);
                ocr.IsAvailable = false;
            }
            if (cycle == 3)
            {
                Assert.Equal("text:empty", Assert.Single(h.State.ActiveAlerts).Key.Discriminator);
                Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
                Assert.False(ocr.Requests[0].Task.IsCompleted);
            }
        };
        await h.Run(4);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(78), Assert.Single(h.State.ActiveAlerts).StartedAt);
        Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId == h.Old.EpisodeId));
    }
    [Theory]
    [InlineData(false, "roi")]
    [InlineData(true, "roi")]
    [InlineData(false, "screen")]
    [InlineData(true, "screen")]
    public async Task RealMonitor_LostImageDiscardsOldFirstReadBeforeObservationReturns(bool group, string lostImage)
    {
        var ocr = new GatedOcr();
        bool mainScreen = true;
        if (lostImage == "screen")
        {
            ocr.OtherRead = image => image.Width > 1024 ? null : OcrResult.FromWords(mainScreen
                ? [new("Milling", new(10, 30, 70, 14), 0), new("Status", new(90, 30, 60, 14), 0)]
                : [new("Production", new(10, 30, 100, 14), 0), new("info", new(120, 30, 40, 14), 0)]);
        }
        var profile = TestPaths.Profile("opencnc") with
        {
            Idle = null, MainScreen = lostImage == "screen" ? new MainScreenWatchProfile { RetryScaled = false } : null,
        };
        using var h = new ReturningMonitor(group, ocr,
            new TimingConfig { FullFrameIntervalMs = 1, MainScreenCheckIntervalMs = 1, WrongScreenAlertAfterMs = 10000 }, screenProfile: profile);
        h.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                Assert.Single(ocr.Requests);
                Assert.Empty(h.State.ActiveAlerts);
                if (lostImage == "roi") h.Source.Frame = new RgbaImage(1, 1, new byte[4]);
                else mainScreen = false;
            }
            if (cycle == 4)
            {
                Alert published = Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised).Alert;
                Assert.Equal("text:empty", published.Key.Discriminator);
                Assert.Equal(FakeClock.DefaultStart.AddSeconds(78), published.StartedAt);
                Assert.Equal(FakeClock.DefaultStart.AddSeconds(79), published.LastSeenAt);
                ocr.Complete(0, HomeText); // This result belongs to the image that was abandoned.
                h.Source.Frame = h.Red;
                mainScreen = true;
            }
        };
        await h.Run(7);
        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal("text:empty", current.Key.Discriminator);
        Assert.Equal(AlertStatus.Active, current.Status);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId == h.Old.EpisodeId));
    }

    [Theory]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [InlineData(false, "blank")]
    [InlineData(true, "blank")]
    [InlineData(false, "offline")]
    [InlineData(true, "offline")]
    public async Task RealMonitor_EndBeforeFirstReadPublishesTheObservedInterval(bool group, string ending)
    {
        var ocr = new GatedOcr();
        using var h = new ReturningMonitor(group, ocr);
        h.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                Assert.Empty(h.State.ActiveAlerts);
                if (ending == "blank") h.Source.Frame = h.Blank;
                if (ending == "offline") h.Source.CaptureFailure = FakeScreenSource.RfbFailure();
            }
        };
        await h.Run(ending == "stop" ? 3 : 6);
        Alert raised = Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised).Alert;
        Assert.Equal("text:empty", raised.Key.Discriminator);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(78), raised.StartedAt);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(ending == "blank" ? 80 : 79), raised.LastSeenAt);
        var fresh = Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId == raised.EpisodeId);
        Assert.Equal(raised.LastSeenAt, fresh.End);
        Assert.True(fresh.End > fresh.StartedAt);
        int published = h.Events.Count;
        ocr.Complete(0, HomeText);
        h.State.Tick();
        Assert.Equal(published, h.Events.Count);
        Assert.Equal(h.OldHistory, Assert.Single(h.Journal.Snapshot(), e => e.EpisodeId == h.Old.EpisodeId));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMonitor_CompletedValidReadStillResolvesWhenEngineBecomesUnavailable(bool group)
    {
        var ocr = new GatedOcr();
        using var h = new ReturningMonitor(group, ocr);
        h.AfterCycle = cycle =>
        {
            if (cycle == 2)
            {
                ocr.Complete(0, HomeText);
                ocr.IsAvailable = false;
            }
        };
        await h.Run(4);
        Alert current = Assert.Single(h.State.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, current.Status);
        Assert.Equal(h.Old.StartedAt, current.StartedAt);
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Raised);
    }
    private sealed class ClosedEpisode : IDisposable
    {
        private readonly TempDir _dir = new("a1-pending-lifecycle");
        public FakeClock Clock { get; } = new();
        public AlertStateMachine State { get; }
        public DowntimeJournal Journal { get; }
        public List<AlertEvent> Events { get; } = [];
        public Alert Old { get; }
        public DowntimeEpisode OldHistory { get; }
        public ClosedEpisode(bool group, TimingConfig? timing = null)
        {
            State = PendingRetiredEpisodeTests.State(Clock, group, timing);
            Journal = new DowntimeJournal(_dir.File("downtime.jsonl"));
            State.Changed += e => { Events.Add(e); Journal.Record(e.Alert, "PC-test"); };
            Old = State.OnRedConfirmed(Machine, Home, null);
            State.AcknowledgeLocal(Old.Key, "PC-test", "operator");
            Clock.AdvanceMs(10000);
            Journal.Record(State.OnRedConfirmed(Machine, Home, null));
            State.OnRedCleared(Machine.Id);
            Clock.AdvanceMs(31000);
            State.Tick();
            Assert.Empty(State.ActiveAlerts);
            OldHistory = Assert.Single(Journal.Snapshot());
            Clock.AdvanceMs(37000); // First new confirmed red at t=78.
            Events.Clear();
        }
        public void Dispose() => _dir.Dispose();
    }

    private sealed class ReturningMonitor : IDisposable
    {
        private readonly TempDir _dir = new("a1-pending-monitor");
        private readonly CancellationTokenSource _cts = new();
        private readonly MachineMonitor _monitor;
        private int _maxCycles;
        public FakeClock Clock { get; } = new();
        public AlertStateMachine State { get; }
        public DowntimeJournal Journal { get; }
        public List<AlertEvent> Events { get; } = [];
        public FakeScreenSource Source { get; }
        public RgbaImage Red { get; }
        public RgbaImage Blank { get; }
        public Alert Old { get; } = null!;
        public DowntimeEpisode OldHistory { get; } = null!;
        public Action<int>? AfterCycle { get; set; }
        public int Cycle { get; private set; }
        public MachineStatusSnapshot Snapshot => _monitor.Snapshot;
        public ReturningMonitor(bool group, GatedOcr ocr, TimingConfig? timing = null, bool seedClosed = true, ScreenProfile? screenProfile = null)
        {
            timing ??= new TimingConfig();
            State = PendingRetiredEpisodeTests.State(Clock, group, timing);
            Journal = new DowntimeJournal(_dir.File("downtime.jsonl"));
            State.Changed += e => { Events.Add(e); Journal.Record(e.Alert, "PC-test"); };
            if (seedClosed)
            {
                Old = State.OnRedConfirmed(Machine, Home, null);
                State.AcknowledgeLocal(Old.Key, "PC-test", "operator");
                Clock.AdvanceMs(10000);
                Journal.Record(State.OnRedConfirmed(Machine, Home, null));
                State.OnRedCleared(Machine.Id);
                Clock.AdvanceMs(31000);
                State.Tick();
                Assert.Empty(State.ActiveAlerts);
                OldHistory = Assert.Single(Journal.Snapshot());
                Clock.AdvanceMs(36000); // First raw frame 77, second confirms red at 78.
                Events.Clear();
            }
            var profile = screenProfile ?? TestPaths.Profile("opencnc") with { MainScreen = null, Idle = null };
            Red = SyntheticScreens.FullFrame(profile, PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput,
                "alarm-crops-opencnc", "aidite1_20260912-114258_blad.png")));
            Blank = SyntheticScreens.FullFrame(profile, PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput,
                "alarm-crops-opencnc", "aidite4_20260912-101033_przerwa.png")));
            Source = new FakeScreenSource(Machine.Id, Red);
            _monitor = new MachineMonitor(Machine, profile, () => Source, null, ocr,
                new MessageMapper(TestPaths.ExampleRules), State, Clock, timing, new NoCaptures(),
                new RecordingLoggerFactory().CreateLogger("A1-cases"), (interval, _) =>
                {
                    Clock.Advance(interval);
                    State.Tick();
                    Cycle++;
                    AfterCycle?.Invoke(Cycle);
                    if (Cycle >= _maxCycles) _cts.Cancel();
                    return Task.CompletedTask;
                });
        }
        public Task Run(int cycles)
        {
            _maxCycles = cycles;
            return Task.Run(() => _monitor.RunAsync(_cts.Token));
        }
        public void Dispose() { _cts.Dispose(); _dir.Dispose(); }
    }
}