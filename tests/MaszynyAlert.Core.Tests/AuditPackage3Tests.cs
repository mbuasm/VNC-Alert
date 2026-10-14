using System.Diagnostics;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests;

public sealed class AuditPackage3Tests
{
    private static MachineConfig Machine(string id = "a") => new()
    { Id = id, Name = id, Host = "127.0.0.1", Port = 1, Profile = "opencnc" };
    private static MappedMessage Message(string code, int priority) =>
        new(code, code, "inny", code, code, true) { Priority = priority };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F3_OldAckAfterCodeSwapAndCloseDoesNotSilenceNextEpisode(bool replayBeforeRaise)
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var sm = new AlertStateMachine(clock, new TimingConfig(), acks);
        var a = sm.OnRedConfirmed(Machine(), Message("A", 1), null);
        var ack = new AckRecord(a.Key, "pc", "user", clock.UtcNow);
        sm.ApplyRemoteAck(ack);
        clock.AdvanceMs(1000);
        var b = sm.OnRedConfirmed(Machine(), Message("B", 2), null);
        Assert.Equal(AlertStatus.Acknowledged, b.Status);
        Assert.False(acks.IsAcked(a.Key));
        Assert.True(acks.IsAcked(b.Key));
        clock.AdvanceMs(31000);
        sm.Tick();
        if (replayBeforeRaise) sm.ApplyRemoteAck(ack);
        clock.AdvanceMs(1000);
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        sm.OnRedConfirmed(Machine(), Message("A", 1), null);
        if (!replayBeforeRaise) sm.ApplyRemoteAck(ack);
        Assert.Equal(AlertStatus.Active, Assert.Single(sm.ActiveAlerts).Status);
        Assert.Contains(events, e => e.Kind == AlertEventKind.Raised && e.Alert.Sound);
    }

    [Fact]
    public void F3_DelayedAckForOldCodeConfirmsCurrentCodeAndSurvivesTakeover()
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var sm = new AlertStateMachine(clock, new TimingConfig(), acks);
        var a = sm.OnRedConfirmed(Machine(), Message("A", 1), null);
        clock.AdvanceMs(1000);
        var ack = new AckRecord(a.Key, "pc", "user", clock.UtcNow);
        clock.AdvanceMs(1000);
        sm.OnRedConfirmed(Machine(), Message("B", 2), null);
        sm.ApplyRemoteAck(ack);
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(sm.ActiveAlerts).Status);
        clock.AdvanceMs(1000);
        var takeover = new AlertStateMachine(clock, new TimingConfig(), acks);
        Assert.Equal(AlertStatus.Acknowledged, takeover.OnRedConfirmed(Machine(), Message("B", 2), null).Status);
    }

    [Fact]
    public async Task F3_StateCarriesAliasesAcrossTakeoverAndRetiresThemOnPeer()
    {
        await using var cluster = new TestCluster();
        var peer = await cluster.AddAsync("peer");
        var localAcks = new AckMemory(cluster.Clock, TimeSpan.FromHours(2));
        var local = new AlertStateMachine(cluster.Clock, new TimingConfig(), localAcks);
        var a = local.OnRedConfirmed(Machine(), Message("A", 1), null);
        var ack = new AckRecord(a.Key, "source", "user", cluster.Clock.UtcNow);
        local.ApplyRemoteAck(ack);
        cluster.Clock.AdvanceMs(1000);
        var b = local.OnRedConfirmed(Machine(), Message("B", 2), null);
        cluster.Inject(peer, new StateMessage { Version = "test", Sender = "source", Seq = 1, Alerts = [b] });
        var takeover = new AlertStateMachine(cluster.Clock, new TimingConfig(), peer.Acks);
        Assert.Equal(AlertStatus.Acknowledged, takeover.OnRedConfirmed(Machine(), Message("A", 1), null).Status);
        cluster.Clock.AdvanceMs(31000);
        local.Tick();
        cluster.Inject(peer, new StateMessage { Version = "test", Sender = "source", Seq = 2, RecentlyClosed = [b.Key] });
        peer.Acks.Record(ack); // retransmisja po zamknięciu
        Assert.Null(peer.Acks.Get(a.Key));
        Assert.Null(peer.Acks.Get(b.Key));
    }

    [Fact]
    public void F3_RepeatedSameCodeAfterRestartIsNewAudibleEpisode()
    {
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        var first = sm.OnRedConfirmed(Machine(), Message("A", 1), null);
        sm.Acknowledge(first.Key, "pc", "user", clock.UtcNow);
        clock.AdvanceMs(1000);
        sm.OnMachineOffline(Machine(), clock.UtcNow);
        clock.AdvanceMs(new TimingConfig().EpisodeBreakAfterMs);
        sm.OnMachineOnline("a");
        var next = sm.OnRedConfirmed(Machine(), Message("A", 1), null);
        Assert.Equal(AlertStatus.Active, next.Status);
        Assert.True(next.StartedAt > first.StartedAt);
    }

    [Fact]
    public async Task F5_FailedStatic100RetriesAndCanRaiseIdleAlert()
    {
        var clock = new FakeClock();
        var ocr = new FakeOcrEngine { DefaultResult = new("100%", ["100% "]) };
        // Nieudany odczyt = pusta pierwsza próba i puste próby zapasowe (ProgressNumberReader.FallbackScales).
        int passes = 1 + ProgressNumberReader.FallbackScales.Count(s => s != new IdleWatchProfile().OcrScale);
        for (int i = 0; i < passes; i++) ocr.EnqueueEmpty();
        var reader = new ProgressNumberReader(new IdleWatchProfile(), false, ocr, clock: clock);
        var image = new RgbaImage(20, 10);
        Assert.Null((await reader.ReadAsync(image, default)).Percent);
        clock.AdvanceMs(4999);
        Assert.Null((await reader.ReadAsync(image, default)).Percent);
        Assert.Equal(passes, ocr.CallCount);
        clock.AdvanceMs(1);
        Assert.Equal(100, (await reader.ReadAsync(image, default)).Percent);
        var sm = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        for (int i = 0; i < 122; i++)
        {
            sm.OnProgressState(Machine(), (await reader.ReadAsync(image, default)).IsFull);
            clock.AdvanceMs(1000);
        }
        Assert.Equal(AlertKind.MachineIdle, Assert.Single(sm.ActiveAlerts).Kind);
        Assert.InRange(ocr.CallCount, passes + 2, passes + 3);
    }

    [Fact]
    public async Task F5_SuccessRefreshAndResetInvalidateStaticImage()
    {
        var clock = new FakeClock();
        var ocr = new FakeOcrEngine().Enqueue("100%").Enqueue("50%").Enqueue("25%");
        var reader = new ProgressNumberReader(new IdleWatchProfile(), false, ocr, clock: clock);
        var image = new RgbaImage(20, 10);
        Assert.Equal(100, (await reader.ReadAsync(image, default)).Percent);
        clock.AdvanceMs(60000);
        Assert.Equal(50, (await reader.ReadAsync(image, default)).Percent);
        reader.Reset();
        Assert.Equal(25, (await reader.ReadAsync(image, default)).Percent);
    }

    [Fact]
    public async Task F5_UnavailableIsDistinctAndPersistentFailuresAreThrottled()
    {
        var clock = new FakeClock();
        var ocr = new FakeOcrEngine { DefaultResult = OcrResult.NotAvailable };
        var reader = new ProgressNumberReader(new IdleWatchProfile(), false, ocr, clock: clock);
        for (int i = 0; i < 60; i++)
        {
            var value = await reader.ReadAsync(new RgbaImage(20, 10), default);
            Assert.True(value.OcrUnavailable);
            Assert.Null(value.Percent);
            clock.AdvanceMs(1000);
        }
        Assert.Equal(12, ocr.CallCount);
    }

    [Fact]
    public async Task F5_LosingOcrInvalidatesPreviouslySuccessfulCache()
    {
        var ocr = new FakeOcrEngine().Enqueue("100%").Enqueue("50%");
        var reader = new ProgressNumberReader(new IdleWatchProfile(), false, ocr);
        var image = new RgbaImage(20, 10);
        Assert.Equal(100, (await reader.ReadAsync(image, default)).Percent);
        ocr.IsAvailable = false;
        Assert.Null((await reader.ReadAsync(image, default)).Percent);
        ocr.IsAvailable = true;
        Assert.Equal(50, (await reader.ReadAsync(image, default)).Percent);
    }

    private sealed class HungOcr : IOcrEngine
    {
        public string Name => "hung";
        public bool IsAvailable => true;
        public string? LanguageTag => "en";
        public TaskCompletionSource<OcrResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
        { Calls++; return Completion.Task; } // deliberately ignores cancellation
    }

    private sealed class OrderedOcr : IOcrEngine
    {
        public string Name => "ordered";
        public bool IsAvailable => true;
        public string? LanguageTag => "en";
        public TaskCompletionSource<OcrResult> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int> Order { get; } = [];
        public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
        {
            Order.Add(image.Width);
            return Order.Count == 1 ? First.Task : Task.FromResult(OcrResult.Empty);
        }
    }

    [Fact]
    public async Task F4_QueuedErrorHasPriorityOverQueuedProgress()
    {
        var inner = new OrderedOcr();
        var ocr = new SerializingOcrEngine(inner);
        var first = ocr.RecognizeAsync(new Gray8Image(1, 1), default);
        var progress = ocr.RecognizeAsync(new Gray8Image(2, 1), default);
        var error = ocr.RecognizeErrorAsync(new Gray8Image(3, 1), default);
        inner.First.SetResult(OcrResult.Empty);
        await Task.WhenAll(first, progress, error).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([1, 3, 2], inner.Order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F4_TimeoutOrCancellationDoesNotReleaseNativeEngineEarly(bool cancel)
    {
        // To limit samego testu, nie silnika (poniżej nadal 50 ms). Obciążony runner
        // Windows może wznowić kontynuację później niż po 2 s.
        var testTimeout = TimeSpan.FromSeconds(10);
        var inner = new HungOcr();
        var ocr = new SerializingOcrEngine(inner, TimeSpan.Zero, executionTimeout: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();
        var first = ocr.RecognizeAsync(new Gray8Image(4, 4), cts.Token);
        if (cancel)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        else
        {
            Assert.True((await first.WaitAsync(testTimeout)).Unavailable);
            Assert.False(ocr.IsAvailable);
        }
        Assert.True((await ocr.RecognizeAsync(new Gray8Image(4, 4), default)).Unavailable);
        Assert.Equal(1, inner.Calls);
        inner.Completion.SetResult(new("No.705", ["No.705"]));
        using var timeout = new CancellationTokenSource(testTimeout);
        while (!ocr.IsAvailable) await Task.Delay(1, timeout.Token);
    }

    [Fact]
    public async Task F4_MonitorStartedDuringOcrTimeoutKeepsHmiCapabilityForRecovery()
    {
        var inner = new HungOcr();
        var ocr = new SerializingOcrEngine(inner, executionTimeout: TimeSpan.FromMilliseconds(30));
        await ocr.RecognizeAsync(new Gray8Image(2, 2), default);
        Assert.False(ocr.IsAvailable);
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        var monitor = new MachineMonitor(Machine(), TestPaths.Profile("opencnc"), () => throw new Exception(), null,
            ocr, new MessageMapper(TestPaths.ExampleRules), sm, clock, new TimingConfig(), new NullCaptureStore());
        Assert.True(monitor.Snapshot.MainScreenWatchEnabled);
        inner.Completion.TrySetResult(OcrResult.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F4_SevenMachinesStillRaiseAndRefreshWhileOcrNeverCompletes(bool watchHmi)
    {
        var clock = new FakeClock();
        var timing = new TimingConfig();
        var sm = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var inner = new HungOcr();
        var ocr = new SerializingOcrEngine(inner, TimeSpan.Zero, executionTimeout: TimeSpan.FromSeconds(30));
        var profile = TestPaths.Profile("opencnc");
        profile = profile with { MainScreen = watchHmi ? profile.MainScreen : null };
        var frame = new RgbaImage(1024, 768);
        frame.Fill(SyntheticScreens.LightBackground);
        var roi = profile.StatusBarRoi;
        frame.Blit(SyntheticScreens.StatusBar(roi.Width, roi.Height, SyntheticScreens.LightBackground,
            SyntheticScreens.RedText, "blad"), roi.X, roi.Y);
        using var cts = new CancellationTokenSource();
        int arrivals = 0;
        var barrierLock = new object();
        var nextCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitors = new List<MachineMonitor>();
        var sources = new List<FakeScreenSource>();
        for (int i = 0; i < 7; i++)
        {
            var machine = Machine("a" + i);
            var source = new FakeScreenSource(machine.Id, frame);
            sources.Add(source);
            monitors.Add(new MachineMonitor(machine, profile, () => source, null, ocr,
                new MessageMapper(TestPaths.ExampleRules), sm, clock, timing, new NullCaptureStore(),
                delay: (_, _) =>
                {
                    lock (barrierLock)
                    {
                        Task barrier = nextCycle.Task;
                        arrivals++;
                        if (arrivals % 7 == 0)
                        {
                            int cycle = arrivals / 7;
                            if (cycle >= 2)
                            {
                                Assert.Equal(7, sm.ActiveAlerts.Count);
                                Assert.All(sm.ActiveAlerts, a => Assert.Equal(clock.UtcNow, a.LastSeenAt));
                            }
                            sm.Tick(); // ponad 30 s: zawieszony OCR nie wygasza istniejącego alarmu
                            clock.AdvanceMs(1000);
                            if (cycle == 40) cts.Cancel();
                            nextCycle.SetResult();
                            nextCycle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        return barrier;
                    }
                }));
        }
        try
        {
            // Limit służy wykryciu zakleszczenia, a czas alarmów sprawdza FakeClock.
            // Instrumentacja pokrycia na wolniejszym runnerze zajmuje ponad 30 s.
            await Task.WhenAll(monitors.Select(m => m.RunAsync(cts.Token))).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(7, sm.ActiveAlerts.Count);
            Assert.All(sources, source =>
            {
                Assert.True(source.CaptureCount + source.CaptureFullCount >= 40);
                Assert.Equal(1, source.DisposeCount);
            });
            Assert.Equal(1, inner.Calls);
        }
        finally { cts.Cancel(); inner.Completion.TrySetResult(OcrResult.Empty); }
        inner.Completion.TrySetResult(OcrResult.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F4_SlowDescriptionEnrichesOnlyItsOwnEpisode(bool restart)
    {
        var clock = new FakeClock();
        var sm = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        var profile = TestPaths.Profile("opencnc") with { MainScreen = null, Idle = null };
        var frame = SyntheticScreens.FullFrame(profile, SyntheticScreens.StatusBar(profile.StatusBarRoi.Width,
            profile.StatusBarRoi.Height, SyntheticScreens.LightBackground, SyntheticScreens.RedText, "blad"));
        var source = new FakeScreenSource("a", frame);
        var ocr = new HungOcr();
        using var cts = new CancellationTokenSource();
        var timer = Stopwatch.StartNew();
        var monitor = new MachineMonitor(Machine(), profile, () => source, null, ocr,
            new MessageMapper(TestPaths.ExampleRules), sm, clock, new TimingConfig(), new NullCaptureStore(),
            delay: async (_, _) =>
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(2)) cts.Cancel();
                if (ocr.Calls == 1 && !ocr.Completion.Task.IsCompleted)
                {
                    Assert.Equal(AlertStatus.Active, Assert.Single(sm.ActiveAlerts).Status);
                    if (restart) source.CaptureFailure = FakeScreenSource.RfbFailure();
                    ocr.Completion.SetResult(new("No.705", ["No.705"]));
                    await Task.Delay(10);
                }
                else if (restart && source.CaptureFailure is not null)
                {
                    source.CaptureFailure = null;
                }
                else if (sm.ActiveAlerts.Any(a => a.Key == AlertKey.ForRule("a", "code-705")) || restart)
                {
                    cts.Cancel();
                }
                clock.AdvanceMs(1000);
            });
        await monitor.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(3));
        if (restart) Assert.DoesNotContain(sm.ActiveAlerts, a => a.Key == AlertKey.ForRule("a", "code-705"));
        else Assert.Contains(sm.ActiveAlerts, a => a.Key == AlertKey.ForRule("a", "code-705"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F6_GlobalDisableCreatesNoMonitorsOrPasswordLookup(bool clusterEnabled)
    {
        var clock = new FakeClock();
        var cfg = TestPaths.ExampleConfig with { Enabled = false,
            Cluster = TestPaths.ExampleConfig.Cluster with { Enabled = clusterEnabled } };
        int lookups = 0;
        await using var supervisor = new MonitorSupervisor(cfg, _ => { lookups++; return "fake"; },
            new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules),
            new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2))),
            clock, new NullCaptureStore(), "unused-test-screens");
        await supervisor.StartAsync(default);
        Assert.False(supervisor.IsRunning);
        Assert.Equal(0, lookups);
        Assert.Equal(cfg.Machines.Count, supervisor.Snapshots.Count);
        Assert.All(supervisor.Snapshots, s => Assert.Equal(MachineConnectionState.Disabled, s.State));
        Assert.All(cfg.Machines, m => Assert.Null(supervisor.Get(m.Id)));
    }
}
