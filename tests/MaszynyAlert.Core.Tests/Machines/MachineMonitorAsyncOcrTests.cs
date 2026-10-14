using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorAsyncOcrTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressDisplay_SourceBoundaryClearsPreviouslyVisiblePercent(bool sourceSwitch)
    {
        using var directory = new TempDir();
        string machineId = TestPaths.ExampleConfig.Machines.First(m => m.Enabled).Id;
        var fileSource = new FolderScreenSource(machineId, directory.Path);
        using var h = new Harness(watchHmi: false, idle: true, testOverride: fileSource);
        h.Ocr.Progress = new("56%", ["56%"]);
        await h.Run(7, second =>
        {
            if (second == 3)
            {
                Assert.Equal(56, h.Monitor.Snapshot.LastProgressPercent);
                h.Ocr.HoldProgress = true;
                if (sourceSwitch) PngCodec.EncodeToFile(Frame(), fileSource.FilePath);
                else h.Source.CaptureFailure = FakeScreenSource.RfbFailure();
            }
            if (second >= 4) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressDisplay_UnreliableHmiClearsPreviouslyVisiblePercent(bool menu)
    {
        using var h = new Harness(watchHmi: true, idle: true);
        await h.Run(25, second =>
        {
            if (second == 19)
            {
                Assert.Equal(100, h.Monitor.Snapshot.LastProgressPercent);
                h.Ocr.Screen = menu ? Menu : OcrResult.Empty;
                if (menu) h.Source.Frame = Frame(context: 100);
            }
            if (second >= 21) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressDisplay_KeepsPercentWhileChangedDigitsAreBeingRead(bool yellowStatus)
    {
        using var h = new Harness(watchHmi: true, idle: true);
        h.Ocr.Progress = new("56.10%", ["56.10%"]);
        RgbaImage Screen(byte progress)
        {
            var frame = Frame(progress: progress);
            var status = new RgbaImage(StatusRoi.Width, StatusRoi.Height);
            status.Fill(yellowStatus ? new Rgba(255, 255, 0) : new Rgba(255, 255, 255));
            frame.Blit(status, StatusRoi.X, StatusRoi.Y);
            return frame;
        }
        h.Source.Frame = Screen(240);
        await h.Run(9, second =>
        {
            if (second == 3)
            {
                Assert.Equal(56, h.Monitor.Snapshot.LastProgressPercent);
                h.Source.Frame = Screen(230);
                h.Ocr.Progress = new("57.20%", ["57.20%"]);
            }
            if (second == 4)
            {
                Assert.Equal(56, h.Monitor.Snapshot.LastProgressPercent);
                var card = MachineCardState.From(h.Monitor.Snapshot.MachineId,
                    h.Monitor.Snapshot, [], Harness.LegacyMainScreenTiming, h.Clock.UtcNow);
                Assert.Equal("56 %", card.Progress);
                Assert.Equal("Postęp pracy: 56%", card.Detail);
            }
            if (second >= 5) Assert.Equal(57, h.Monitor.Snapshot.LastProgressPercent);
        });
        Assert.Empty(h.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task ProgressDisplay_HeldPercentDoesNotAdvanceIdleAndExpires(int percent)
    {
        using var h = new Harness(watchHmi: false, idle: true);
        h.Ocr.Progress = new($"{percent}%", [$"{percent}%"]);
        await h.Run(200, second =>
        {
            if (second == 3)
            {
                Assert.Equal(percent, h.Monitor.Snapshot.LastProgressPercent);
                h.Source.Frame = Frame(progress: 230);
                h.Ocr.HoldProgress = true;
            }
            if (second is >= 4 and <= 17)
            {
                Assert.Equal(percent, h.Monitor.Snapshot.LastProgressPercent);
                Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
                Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
                Assert.False(h.Monitor.Snapshot.ProgressFullConfirmed);
            }
            if (second >= 18) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
        });
        Assert.Empty(h.Events);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("failure")]
    [InlineData("unavailable")]
    public async Task ProgressDisplay_CompletedBadReadClearsHeldPercent(string outcome)
    {
        using var h = new Harness(watchHmi: false, idle: true);
        await h.Run(9, second =>
        {
            if (second == 3)
            {
                Assert.Equal(100, h.Monitor.Snapshot.LastProgressPercent);
                h.Source.Frame = Frame(progress: 230);
                h.Ocr.Progress = outcome == "unavailable" ? OcrResult.NotAvailable : OcrResult.Empty;
                h.Ocr.FailProgress = outcome == "failure";
            }
            if (second == 4) Assert.Equal(100, h.Monitor.Snapshot.LastProgressPercent);
            if (second >= 5) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
        });
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ProgressDisplay_ChangingAgainDuringReadUsesFreshRunningSampleOnlyForDisplay()
    {
        using var h = new Harness(watchHmi: false, idle: true);
        h.Ocr.Progress = new("56%", ["56%"]);
        await h.Run(11, second =>
        {
            if (second == 3)
            {
                h.Source.Frame = Frame(progress: 230);
                h.Ocr.Progress = new("57%", ["57%"]);
                h.Ocr.HoldProgress = true;
            }
            if (second == 4)
            {
                h.Source.Frame = Frame(progress: 220);
                h.Ocr.Progress = new("58%", ["58%"]);
            }
            if (second == 5) h.Ocr.HoldProgress = false;
            if (second is >= 4 and <= 5) Assert.Equal(56, h.Monitor.Snapshot.LastProgressPercent);
            if (second is 6 or 7)
            {
                Assert.Equal(57, h.Monitor.Snapshot.LastProgressPercent);
                Assert.Equal(0, h.Monitor.Snapshot.LastProgressFraction);
                Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
            }
            if (second >= 8) Assert.Equal(58, h.Monitor.Snapshot.LastProgressPercent);
        });
        Assert.Empty(h.Events);
    }

    [Theory]
    [InlineData(false, false, 121)]
    [InlineData(false, true, 122)]
    [InlineData(true, false, 121)]
    [InlineData(true, true, 123)]
    public async Task Idle_RaisesAfterTwoMinutesAcrossHmiChecksAndNumberRefreshes(
        bool watchHmi, bool deferred, int raisedAt)
    {
        using var h = new Harness(watchHmi, idle: true, deferred);
        await h.Run(240);
        var raised = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        Assert.Equal(AlertKind.MachineIdle, raised.Event.Alert.Kind);
        Assert.Equal(raisedAt, raised.Second);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(raisedAt - 120), raised.Event.Alert.StartedAt);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
        Assert.True(h.Ocr.ProgressCalls >= 4); // Includes actual 60-second cache refreshes.
    }

    [Theory]
    [InlineData(false, 120)]
    [InlineData(true, 121)]
    public async Task WrongScreen_RaisesAfterTwoMinutesAcrossDeferredChecks(bool deferred, int raisedAt)
    {
        // Dodatni próg pozostaje dopuszczalną konfiguracją (harness przypina 2 min — patrz
        // Harness.LegacyMainScreenTiming): licznik biegnie przez kolejne sprawdzenia mimo odroczonego OCR.
        using var h = new Harness(watchHmi: true, idle: false, deferred);
        h.Ocr.Screen = Menu;
        await h.Run(240);
        var raised = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        Assert.Equal(AlertKind.WrongScreen, raised.Event.Alert.Kind);
        Assert.Equal(raisedAt, raised.Second);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(raisedAt - 120), raised.Event.Alert.StartedAt);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task WrongScreen_ZKonfiguracjaStanowiska_PowstajePrzyPierwszymRozstrzygnietymOdczycie(bool deferred, int raisedAt)
    {
        using var h = new Harness(watchHmi: true, idle: false, deferred, timing: TestPaths.ExampleConfig.Timing);
        h.Ocr.Screen = Menu;
        await h.Run(60);
        var raised = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        Assert.Equal(AlertKind.WrongScreen, raised.Event.Alert.Kind);
        Assert.Equal(raisedAt, raised.Second);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(raisedAt), raised.Event.Alert.StartedAt);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error_ClosesOnTimeWhenRedDisappearsAcrossHmiRefresh(bool deferred)
    {
        using var h = new Harness(watchHmi: true, idle: false, deferred);
        h.Source.Frame = Frame(red: true);
        await h.Run(80, second =>
        {
            if (second == 4) h.Source.Frame = Frame();
        });
        Assert.Equal(1, Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Raised).Second);
        var closed = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
        Assert.Equal(AlertKind.Error, closed.Event.Alert.Kind);
        Assert.Equal(35, closed.Second); // Last 2-of-3 red confirmation at t=5, plus 30 seconds.
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("failure")]
    [InlineData("unavailable")]
    [InlineData("menu")]
    public async Task CompletedUnreliableOrHiddenHmi_RequiresThirtyNewSecondsWithoutRed(string outcome)
    {
        using var h = new Harness(watchHmi: true, idle: false);
        h.Source.Frame = Frame(red: true);
        await h.Run(100, second =>
        {
            if (second == 4) h.Source.Frame = Frame();
            if (second == 19)
            {
                h.Ocr.Screen = outcome switch { "menu" => Menu, "unavailable" => OcrResult.NotAvailable, _ => OcrResult.Empty };
                h.Ocr.FailScreen = outcome == "failure";
                if (outcome == "menu") h.Source.Frame = Frame(context: 100);
            }
            if (second == 20)
                Assert.Equal(outcome == "menu" ? null : (bool?)true, h.Monitor.Snapshot.MainScreenVisible);
            if (second == 21)
                Assert.Equal(outcome == "menu" ? false : (bool?)null, h.Monitor.Snapshot.MainScreenVisible);
            if (second == 59)
            {
                Assert.Equal(FakeClock.DefaultStart.AddSeconds(5), Assert.Single(h.Alerts.ActiveAlerts).LastSeenAt);
                h.Ocr.Screen = Main;
                h.Ocr.FailScreen = false;
                h.Source.Frame = Frame();
            }
            if (second == 90) Assert.Contains(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
        });
        Assert.Equal(91, Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed).Second);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("failure")]
    [InlineData("unavailable")]
    public async Task CompletedUnreliableProgress_RestartsTheIdleCountdown(string outcome)
    {
        using var h = new Harness(watchHmi: false, idle: true);
        await h.Run(220, second =>
        {
            if (second == 59)
            {
                h.Ocr.Progress = outcome == "unavailable" ? OcrResult.NotAvailable : OcrResult.Empty;
                h.Ocr.FailProgress = outcome == "failure";
            }
            if (second == 60) Assert.True(h.Monitor.Snapshot.ProgressFullConfirmed);
            if (second == 61) Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
            if (second == 79)
            {
                h.Ocr.Progress = new("100%", ["100%"]);
                h.Ocr.FailProgress = false;
            }
        });
        var raised = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        Assert.Equal(202, raised.Second); // Refresh at 80, receive at 81, filter confirms at 82.
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(82), raised.Event.Alert.StartedAt);
    }

    [Fact]
    public async Task HungHmi_ExpiresPreviousObservationAndRejectsLateResult()
    {
        using var h = new Harness(watchHmi: true, idle: false);
        h.Source.Frame = Frame(red: true);
        await h.Run(120, second =>
        {
            if (second == 4) h.Source.Frame = Frame();
            if (second == 19) h.Ocr.HoldScreen = true;
            if (second == 34) Assert.True(h.Monitor.Snapshot.MainScreenVisible);
            if (second is >= 35 and <= 82) Assert.Null(h.Monitor.Snapshot.MainScreenVisible);
            if (second == 80)
            {
                Assert.Equal(2, h.Ocr.ScreenCalls);
                h.Ocr.HoldScreen = false;
            }
        });
        Assert.Equal(113, Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed).Second);
    }

    [Fact]
    public async Task HungProgress_ExpiresAtSeventyFiveSecondsAndRejectsLateResult()
    {
        using var h = new Harness(watchHmi: false, idle: true);
        await h.Run(220, second =>
        {
            if (second == 59) h.Ocr.HoldProgress = true;
            if (second == 74) Assert.True(h.Monitor.Snapshot.ProgressFullConfirmed);
            if (second is >= 75 and <= 92) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
            if (second == 90)
            {
                Assert.Equal(2, h.Ocr.ProgressCalls);
                h.Ocr.HoldProgress = false;
            }
        });
        Assert.Equal(214, Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Raised).Second);
    }

    [Fact]
    public async Task ImageChangesDuringRead_RejectEvenAnImageThatChangesBack()
    {
        using var h = new Harness(watchHmi: false, idle: true);
        h.Ocr.HoldProgress = true;
        await h.Run(8, second =>
        {
            if (second == 0) h.Source.Frame = Frame(progress: 100);
            if (second == 1)
            {
                h.Source.Frame = Frame();
                h.Ocr.HoldProgress = false;
            }
            if (second == 2) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
            if (second == 6) Assert.Equal(100, h.Monitor.Snapshot.LastProgressPercent);
        });
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task AnchorChangesDuringRead_RejectEvenAnImageThatChangesBack()
    {
        using var h = new Harness(watchHmi: true, idle: false);
        await h.Run(26, second =>
        {
            if (second == 1) Assert.True(h.Monitor.Snapshot.MainScreenVisible);   // pas kotwic już znany
            if (second == 19) h.Ocr.HoldScreen = true;
            if (second == 20) h.Source.Frame = Frame(context: 100);               // inna zakładka HMI
            if (second == 21)
            {
                h.Source.Frame = Frame();                                          // i z powrotem
                h.Ocr.HoldScreen = false;
            }
            if (second == 22)
            {
                Assert.Null(h.Monitor.Snapshot.MainScreenVisible);
                Assert.Equal("invalidated", h.Monitor.Snapshot.MainScreenUnknownReason);
                Assert.Equal(1, h.Monitor.Snapshot.MainScreenUnknownStreak);
            }
            if (second == 24) Assert.True(h.Monitor.Snapshot.MainScreenVisible);   // wymuszony nowy odczyt
        });
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task LiveHmiFields_ChangingEveryCycle_DoNotBlockTheMainScreenCheck()
    {
        using var h = new Harness(watchHmi: true, idle: false);
        byte live = 240;
        await h.Run(80, second =>
        {
            h.Source.Frame = Frame(live: live = (byte)(200 + (second % 40)));   // współrzędne osi lecą
            if (second >= 2)
            {
                Assert.True(h.Monitor.Snapshot.MainScreenVisible, $"sekunda {second}");
                Assert.False(h.Monitor.Snapshot.ScreenObservationUnavailable);
                Assert.Equal(0, h.Monitor.Snapshot.MainScreenUnknownStreak);
            }
        });
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ProgressDropDuringRefresh_CannotBeOverwrittenByOldHundredPercent()
    {
        using var h = new Harness(watchHmi: false, idle: true);
        await h.Run(250, second =>
        {
            if (second == 59) h.Ocr.HoldProgress = true;
            if (second == 60)
            {
                h.Source.Frame = Frame(progress: 239);
                h.Ocr.Progress = new("0%", ["0%"]);
                h.Ocr.HoldProgress = false;
            }
            if (second == 61) Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
            if (second == 63) Assert.Equal(0, h.Monitor.Snapshot.LastProgressPercent);
        });

        // O to chodzi w tym teście: stare, zbuforowane 100 % nie może wskrzesić postoju „dołóż prace”
        // po tym, jak odczyt spadł na 0 %.
        Assert.DoesNotContain(h.Events, e => e.Event.Alert.Kind == AlertKind.MachineIdle
            && e.Event.Alert.Category == "postoj");

        Assert.Contains(h.Events, e => e.Event.Kind == AlertEventKind.Raised
            && e.Event.Alert.Category == "postoj-0");
    }

    [Fact]
    public async Task IdleAck_StaysAcknowledgedAcrossRefreshesUntilANewEpisode()
    {
        using var h = new Harness(watchHmi: true, idle: true);
        await h.Run(1120, second =>
        {
            if (second == 125)
            {
                Alert idle = Assert.Single(h.Alerts.ActiveAlerts);
                Assert.True(h.Alerts.AcknowledgeLocal(idle.Key, "PC", "operator"));
            }
            if (second == 980)
            {
                Assert.Equal(AlertStatus.Acknowledged, Assert.Single(h.Alerts.ActiveAlerts).Status);
                h.Source.Frame = Frame(progress: 100);
                h.Ocr.Progress = new("0%", ["0%"]);
            }
            if (second is 981 or 982) Assert.Single(h.Alerts.ActiveAlerts);
            if (second == 983) Assert.Empty(h.Alerts.ActiveAlerts);
            if (second == 990)
            {
                h.Source.Frame = Frame();
                h.Ocr.Progress = new("100%", ["100%"]);
            }
        });
        Assert.Equal(new[] { 123, 1113 }, h.Events.Where(e => e.Event.Kind == AlertEventKind.Raised).Select(e => e.Second));
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Escalated);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReconnectOrSourceSwitch_RejectsResultsEvenForIdenticalPixels(bool hmi, bool sourceSwitch)
    {
        using var directory = new TempDir();
        string machineId = TestPaths.ExampleConfig.Machines.First(m => m.Enabled).Id;
        var fileSource = new FolderScreenSource(machineId, directory.Path);
        using var h = new Harness(watchHmi: hmi, idle: true, testOverride: fileSource);
        h.Ocr.HoldScreen = hmi;
        h.Ocr.HoldProgress = !hmi;
        await h.Run(10, second =>
        {
            if (second == 0)
            {
                h.Ocr.Screen = Menu;
                h.Ocr.Progress = new("0%", ["0%"]);
                if (sourceSwitch) PngCodec.EncodeToFile(Frame(), fileSource.FilePath);
                else h.Source.CaptureFailure = FakeScreenSource.RfbFailure();
            }
            if (second == 1)
            {
                h.Source.CaptureFailure = null;
                h.Ocr.HoldScreen = h.Ocr.HoldProgress = false;
            }
            if (second == 3)
            {
                Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
                if (hmi) Assert.Null(h.Monitor.Snapshot.MainScreenVisible);
            }
            if (second == 9)
            {
                if (hmi) Assert.False(h.Monitor.Snapshot.MainScreenVisible);
                else Assert.Equal(0, h.Monitor.Snapshot.LastProgressPercent);
            }
        });
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ReaderResetDuringOcr_PreventsOldContinuationFromRepopulatingCache()
    {
        var ocr = new ControlledOcr(deferred: true);
        var reader = new ProgressNumberReader(TestPaths.Profile("opencnc").Idle!, false, ocr, clock: new FakeClock());
        RgbaImage crop = Frame().Crop(ProgressRoi);
        Task<ProgressDetectionResult> old = reader.ReadAsync(crop, CancellationToken.None);
        reader.Reset();
        ocr.CompletePending();
        await old;
        ocr.Progress = new("0%", ["0%"]);
        Task<ProgressDetectionResult> current = reader.ReadAsync(crop, CancellationToken.None);
        Assert.False(current.IsCompleted);
        Assert.Equal(2, ocr.ProgressCalls);
        ocr.CompletePending();
        Assert.Equal(0, (await current).Percent);
    }

    [Fact]
    public async Task ReaderOwnsPixelsWhileOcrRuns_MutatedInputCannotAcquireOldResult()
    {
        var ocr = new ControlledOcr(deferred: true);
        var reader = new ProgressNumberReader(TestPaths.Profile("opencnc").Idle!, false, ocr, clock: new FakeClock());
        RgbaImage crop = Frame().Crop(ProgressRoi);
        Task<ProgressDetectionResult> old = reader.ReadAsync(crop, CancellationToken.None);
        crop.Fill(new Rgba(239, 239, 239));
        ocr.CompletePending();
        await old;
        ocr.Progress = new("0%", ["0%"]);
        Task<ProgressDetectionResult> current = reader.ReadAsync(crop, CancellationToken.None);
        Assert.False(current.IsCompleted);
        ocr.CompletePending();
        Assert.Equal(0, (await current).Percent);
    }

    internal static readonly RectInt StatusRoi = new(10, 10, 280, 20);
    internal static readonly RectInt ProgressRoi = new(10, 80, 100, 20);

    internal static readonly RectInt LiveRoi = new(200, 120, 60, 20);
    internal static OcrResult Main => OcrResult.FromWords([
        new("Milling", new RectInt(10, 40, 70, 14), 0),
        new("Status", new RectInt(90, 40, 60, 14), 0)]);
    internal static OcrResult Menu => OcrResult.FromWords([
        new("Production", new RectInt(10, 40, 70, 14), 0),
        new("info", new RectInt(90, 40, 60, 14), 0)]);

    internal static RgbaImage Frame(bool red = false, byte context = 240, byte progress = 240, byte live = 240)
    {
        var frame = new RgbaImage(320, 180);
        frame.Fill(new Rgba(context, context, context));
        if (live != context)
        {
            var coords = new RgbaImage(LiveRoi.Width, LiveRoi.Height);
            coords.Fill(new Rgba(live, live, live));
            frame.Blit(coords, LiveRoi.X, LiveRoi.Y);
        }

        var number = new RgbaImage(ProgressRoi.Width, ProgressRoi.Height);
        number.Fill(new Rgba(progress, progress, progress));
        frame.Blit(number, ProgressRoi.X, ProgressRoi.Y);
        var status = new RgbaImage(StatusRoi.Width, StatusRoi.Height);
        status.Fill(red ? new Rgba(255, 0, 0) : new Rgba(240, 240, 240));
        frame.Blit(status, StatusRoi.X, StatusRoi.Y);
        return frame;
    }

    internal sealed class Harness : IDisposable
    {
        internal static readonly TimingConfig LegacyMainScreenTiming = TestPaths.ExampleConfig.Timing with
        {
            MainScreenCheckIntervalMs = 20000,
            WrongScreenAlertAfterMs = 120000,
        };

        private readonly CancellationTokenSource _cts = new();
        private Action<int>? _afterCycle;
        private int _seconds;
        public FakeClock Clock { get; }
        public ControlledOcr Ocr { get; }
        public FakeScreenSource Source { get; }
        public MachineMonitor Monitor { get; }
        public AlertStateMachine Alerts { get; }
        public List<(int Second, AlertEvent Event)> Events { get; } = [];

        public Harness(bool watchHmi, bool idle, bool deferred = true, FolderScreenSource? testOverride = null,
            FakeClock? clock = null, AlertStateMachine? alerts = null, TimingConfig? timing = null)
        {
            var config = TestPaths.ExampleConfig;
            TimingConfig effectiveTiming = timing ?? LegacyMainScreenTiming;
            Clock = clock ?? new FakeClock();
            var machine = config.Machines.First(m => m.Enabled);
            var profile = config.Profiles["opencnc"] with
            {
                StatusBarRoi = StatusRoi,
                MainScreen = watchHmi ? config.Profiles["opencnc"].MainScreen : null,
                Idle = idle ? config.Profiles["opencnc"].Idle! with { ProgressRoi = ProgressRoi } : null
            };
            Ocr = new ControlledOcr(deferred);
            Source = new FakeScreenSource(machine.Id, Frame());
            Alerts = alerts ?? new AlertStateMachine(Clock, effectiveTiming, new AckMemory(Clock, TimeSpan.FromHours(2)));
            Alerts.Changed += e => Events.Add(((int)(Clock.UtcNow - FakeClock.DefaultStart).TotalSeconds, e));
            Monitor = new MachineMonitor(machine, profile, () => Source, testOverride, Ocr,
                new MessageMapper(TestPaths.ExampleRules), Alerts, Clock, effectiveTiming, new NullCaptureStore(),
                delay: (interval, _) =>
                {
                    int second = (int)(Clock.UtcNow - FakeClock.DefaultStart).TotalSeconds;
                    if (Source.CaptureFailure is null) Assert.Null(Monitor!.Snapshot.LastError);
                    Alerts.Tick();
                    _afterCycle?.Invoke(second);
                    Ocr.CompletePending();
                    Clock.Advance(interval);
                    if (Clock.UtcNow >= FakeClock.DefaultStart.AddSeconds(_seconds)) _cts.Cancel();
                    return Task.CompletedTask;
                });
        }

        public Task Run(int seconds, Action<int>? afterCycle = null)
        {
            _seconds = seconds;
            _afterCycle = afterCycle;
            // xUnit's SynchronizationContext can schedule even ConfigureAwait(false) continuations
            // away from SetResult. Run the deterministic loop with the default scheduler/context.
            return Task.Run(() => Monitor.RunAsync(_cts.Token));
        }

        public void Dispose() => _cts.Dispose();
    }

    // Intentionally inline continuations: each request is incomplete during the monitor cycle,
    // then deterministically completes between cycles, including a possible scaled HMI retry.
    internal sealed class ControlledOcr(bool deferred) : IOcrEngine
    {
        private readonly List<(TaskCompletionSource<OcrResult> Completion, OcrResult Result, bool Screen, bool Fail)> _pending = [];
        public string Name => "controlled-async";
        public bool IsAvailable { get; set; } = true;
        public string? LanguageTag => "en-US";
        public OcrResult Screen { get; set; } = Main;
        public OcrResult Progress { get; set; } = new("100.00%", ["100.00%"]);
        public int ProgressCalls { get; private set; }
        public int ScreenCalls { get; private set; }
        public bool HoldScreen { get; set; }
        public bool HoldProgress { get; set; }
        public bool FailScreen { get; set; }
        public bool FailProgress { get; set; }

        public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            bool screen = image.Width == 320 && image.Height == 180 || image.Width == 640 && image.Height == 360;
            var idle = TestPaths.Profile("opencnc").Idle!;
            bool progress = image.Width == (ProgressRoi.Width + 2 * idle.OcrMarginPx) * idle.OcrScale;
            var result = screen ? Screen : progress ? Progress : new OcrResult("No.705", ["No.705"]);
            if (screen) ScreenCalls++;
            if (progress) ProgressCalls++;
            if (!deferred) return Task.FromResult(result);
            var completion = new TaskCompletionSource<OcrResult>();
            _pending.Add((completion, result, screen, screen ? FailScreen : progress && FailProgress));
            return completion.Task;
        }

        public void CompletePending()
        {
            while (true)
            {
                int index = _pending.FindIndex(item => !(item.Screen ? HoldScreen : HoldProgress));
                if (index < 0) break;
                var item = _pending[index];
                _pending.RemoveAt(index);
                if (item.Fail) item.Completion.SetException(new InvalidOperationException("OCR failed (test)"));
                else item.Completion.SetResult(item.Result);
            }
        }
    }
}
