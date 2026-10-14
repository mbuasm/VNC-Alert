using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests;

public sealed class AuditPackage4Tests
{
    private static MachineConfig Machine => new() { Id = "aidite1", Name = "Aidite 1", Host = "localhost", Port = 9100, Profile = "opencnc" };
    private static MappedMessage Error(string code = "A", int priority = 0) => new(code, code, "inny", code, code, true) { Priority = priority };
    private static AlertStateMachine State(FakeClock clock, TimingConfig? timing = null) =>
        new(clock, timing ?? new(), new AckMemory(clock, TimeSpan.FromHours(2)));

    [Fact]
    public void LocalAcknowledgementAfterClockRollbackStillSilencesCurrentEpisode()
    {
        var clock = new FakeClock();
        var state = State(clock);
        var first = state.OnRedConfirmed(Machine, Error(), null);
        clock.UtcNow -= TimeSpan.FromHours(24);
        Assert.True(state.AcknowledgeLocal(first.Key, "PC", "operator"));
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        Assert.Equal(clock.UtcNow, Assert.Single(state.ActiveAlerts).AcknowledgedAt);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(-24)]
    public void AckExpiryUsesMonotonicTimeIncludingAfterCodeChange(int hours)
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        var a = AlertKey.ForMessage(Machine.Id, Error());
        var b = AlertKey.ForMessage(Machine.Id, Error("B"));
        memory.Record(new(a, "PC", "operator", clock.UtcNow));
        clock.AdvanceMs(1000);
        clock.UtcNow += TimeSpan.FromHours(hours);
        memory.LinkEpisode(b, [a]);
        memory.Prune();
        Assert.NotNull(memory.Get(b));
        clock.Advance(TimeSpan.FromHours(2));
        memory.Prune();
        Assert.Null(memory.Get(b));
    }

    [Fact]
    public void AdoptedAcknowledgementKeepsElapsedAgeAcrossClockJump()
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        var message = Error();
        memory.Record(new(AlertKey.ForMessage(Machine.Id, message), "PC", "operator", clock.UtcNow));
        clock.AdvanceMs(30000);
        var state = new AlertStateMachine(clock, new TimingConfig { EscalationAfterAckMs = 60000 }, memory);
        state.OnRedConfirmed(Machine, message, null);
        clock.UtcNow += TimeSpan.FromDays(1);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.AdvanceMs(30000);
        state.OnRedConfirmed(Machine, message, null);
        state.Tick();
        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(-24)]
    public void WallClockJumpDoesNotCloseErrorOrAccelerateIdleOrOffline(int hours)
    {
        var clock = new FakeClock();
        var error = State(clock);
        var idle = State(clock);
        var offline = State(clock);
        error.OnRedConfirmed(Machine, Error(), null);
        idle.OnProgressState(Machine, true);
        offline.OnMachineOffline(Machine, clock.UtcNow);
        clock.UtcNow += TimeSpan.FromHours(hours);
        error.Tick(); idle.OnProgressState(Machine, true); offline.Tick();
        Assert.Single(error.ActiveAlerts);
        Assert.Empty(idle.ActiveAlerts);
        Assert.Empty(offline.ActiveAlerts);
        clock.AdvanceMs(30000);
        error.Tick();
        Assert.Empty(error.ActiveAlerts);
        clock.AdvanceMs(90000);
        idle.OnProgressState(Machine, true);
        Assert.Equal(AlertKind.MachineIdle, Assert.Single(idle.ActiveAlerts).Kind);
        clock.AdvanceMs(60000);
        offline.Tick();
        Assert.Equal(AlertKind.Offline, Assert.Single(offline.ActiveAlerts).Kind);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(-24)]
    public void EscalationTimerSurvivesClockJumpAndChangeOfCode(int hours)
    {
        var clock = new FakeClock();
        var state = State(clock, new TimingConfig { EscalationAfterAckMs = 60000 });
        var first = state.OnRedConfirmed(Machine, Error(), null);
        state.Acknowledge(first.Key, "PC", "operator", clock.UtcNow);
        clock.AdvanceMs(10000);
        clock.UtcNow += TimeSpan.FromHours(hours);
        state.OnRedConfirmed(Machine, Error("B", 10), null);
        state.Tick();
        Assert.Equal(AlertStatus.Acknowledged, Assert.Single(state.ActiveAlerts).Status);
        clock.AdvanceMs(50000);
        state.OnRedConfirmed(Machine, Error("B", 10), null);
        state.Tick();
        Assert.Equal(AlertStatus.Escalated, Assert.Single(state.ActiveAlerts).Status);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(-24)]
    public void WrongScreenAndObservationRecoveryUseElapsedTime(int hours)
    {
        var clock = new FakeClock();
        var state = State(clock, new TimingConfig { WrongScreenAlertAfterMs = 120000 });
        var first = state.OnRedConfirmed(Machine, Error(), null);
        state.OnMainScreenState(Machine, false);
        clock.UtcNow += TimeSpan.FromHours(hours);
        state.OnMainScreenState(Machine, false);
        state.Tick();
        Assert.Single(state.ActiveAlerts);
        clock.AdvanceMs(120000);
        state.OnMainScreenState(Machine, false);
        Assert.Equal(2, state.ActiveAlerts.Count);
        state.OnMainScreenState(Machine, true);
        state.Tick();
        Assert.Equal(first.Key, Assert.Single(state.ActiveAlerts).Key);
        clock.AdvanceMs(29999); state.Tick();
        Assert.Single(state.ActiveAlerts);
        clock.AdvanceMs(1); state.Tick();
        Assert.Empty(state.ActiveAlerts);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(-24)]
    public void EpisodeBreakUsesElapsedTime(int hours)
    {
        var clock = new FakeClock();
        var state = State(clock);
        var first = state.OnRedConfirmed(Machine, Error(), null);
        state.Acknowledge(first.Key, "PC", "operator", clock.UtcNow);
        state.OnRedCleared(Machine.Id);
        clock.UtcNow += TimeSpan.FromHours(hours);
        Assert.Equal(AlertStatus.Acknowledged, state.OnRedConfirmed(Machine, Error("B", 10), null).Status);
        state.OnRedCleared(Machine.Id);
        clock.AdvanceMs(10000);
        Assert.Equal(AlertStatus.Active, state.OnRedConfirmed(Machine, Error("C", 20), null).Status);
    }

    private static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null, Idle = null };
    private static RgbaImage Frame(bool red) => SyntheticScreens.FullFrame(Profile,
        SyntheticScreens.StatusBar(Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, red ? SyntheticScreens.RedText : SyntheticScreens.WhiteText, "alarm"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconnectRequiresFullFrameAndNewFilterConfirmation(bool redAfterReconnect)
    {
        var clock = new FakeClock();
        var state = State(clock);
        var oldSource = new FakeScreenSource(Machine.Id, Frame(true));
        var newSource = new FakeScreenSource(Machine.Id, Frame(redAfterReconnect));
        using var cts = new CancellationTokenSource();
        int cycle = 0, sources = 0;
        MachineMonitor? monitor = null;
        monitor = new(Machine, Profile, () => ++sources == 1 ? oldSource : newSource, null,
            new NullOcrEngine(), new MessageMapper(new RuleSet()), state, clock, new(), new NullCaptureStore(),
            delay: (delay, _) =>
            {
                cycle++;
                if (cycle == 1) oldSource.CaptureFailure = FakeScreenSource.RfbFailure();
                if (cycle == 2)
                {
                    Assert.Equal(MachineConnectionState.Error, monitor!.Snapshot.State);
                    Assert.Null(monitor.LastFullFrame);
                    Assert.False(monitor.Snapshot.RedConfirmed);
                }
                if (cycle == 3)
                {
                    Assert.Equal(1, newSource.CaptureFullCount);
                    Assert.Empty(state.ActiveAlerts); // pierwszy czerwony odczyt NOWEJ sesji
                }
                if (cycle == 4)
                {
                    if (redAfterReconnect) Assert.Single(state.ActiveAlerts);
                    else Assert.Empty(state.ActiveAlerts);
                    cts.Cancel();
                }
                clock.Advance(delay);
                return Task.CompletedTask;
            });
        await monitor.RunAsync(cts.Token);
    }

    [Fact]
    public async Task SuccessfulHandshakeFollowedByFailedFrameDoesNotResetBackoff()
    {
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        var delays = new List<double>();
        var monitor = new MachineMonitor(Machine, Profile,
            () => new FakeScreenSource(Machine.Id, Frame(false)) { CaptureFailure = FakeScreenSource.RfbFailure() },
            null, new NullOcrEngine(), new MessageMapper(new RuleSet()), State(clock), clock,
            new TimingConfig { ReconnectBackoffMs = [2000, 5000, 15000] }, new NullCaptureStore(),
            delay: (delay, _) =>
            {
                delays.Add(delay.TotalMilliseconds);
                clock.Advance(delay);
                if (delays.Count == 4) cts.Cancel();
                return Task.CompletedTask;
            });
        await monitor.RunAsync(cts.Token);
        Assert.Equal(new double[] { 2000, 5000, 15000, 15000 }, delays);
    }

    [Fact]
    public async Task DisconnectAtFullProgressResetsIdleFilterAndWholeIdleTimer()
    {
        var clock = new FakeClock();
        var state = State(clock);
        var profile = Profile with { Idle = TestPaths.Profile("opencnc").Idle };
        var fullFrame = new RgbaImage(1024, 768);
        fullFrame.Fill(SyntheticScreens.LightBackground);
        var first = new FakeScreenSource(Machine.Id, fullFrame);
        var second = new FakeScreenSource(Machine.Id, fullFrame);
        var ocr = new FakeOcrEngine { DefaultResult = new("100.00%", ["100.00%"] ) };
        using var cts = new CancellationTokenSource();
        int cycle = 0, sources = 0;
        MachineMonitor? monitor = null;
        monitor = new(Machine, profile, () => ++sources == 1 ? first : second, null, ocr,
            new MessageMapper(new RuleSet()), state, clock, new TimingConfig { ReconnectBackoffMs = [1000] },
            new NullCaptureStore(), delay: (delay, _) =>
            {
                cycle++;
                if (cycle == 120) first.CaptureFailure = FakeScreenSource.RfbFailure();
                if (cycle == 121)
                {
                    Assert.Null(monitor!.Snapshot.ProgressFullSince);
                    Assert.Null(monitor.Snapshot.LastProgressPercent);
                }
                if (cycle == 122) Assert.False(monitor!.Snapshot.ProgressFullConfirmed);
                if (cycle < 243) Assert.DoesNotContain(state.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
                if (cycle == 243)
                {
                    Assert.Contains(state.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
                    cts.Cancel();
                }
                clock.Advance(delay);
                return Task.CompletedTask;
            });
        await monitor.RunAsync(cts.Token);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(-24)]
    public async Task CachedImageKeepsReceiveTimestampAndFullRefreshIgnoresClockJump(int hours)
    {
        var clock = new FakeClock();
        var source = new FakeScreenSource(Machine.Id, Frame(false)) { TryCaptureReturnsNull = true };
        using var cts = new CancellationTokenSource();
        int cycle = 0;
        var started = clock.UtcNow;
        MachineMonitor? monitor = null;
        monitor = new(Machine, Profile, () => source, null, new NullOcrEngine(), new MessageMapper(new RuleSet()),
            State(clock), clock, new TimingConfig { FullFrameIntervalMs = 3000 }, new NullCaptureStore(),
            delay: (delay, _) =>
            {
                cycle++;
                if (cycle == 1) clock.UtcNow += TimeSpan.FromHours(hours);
                if (cycle is 2 or 3)
                {
                    Assert.True(monitor!.Snapshot.UsingCachedFrame);
                    Assert.Equal(started, monitor.Snapshot.LastFrameAt);
                    Assert.Equal(1, source.CaptureFullCount);
                }
                if (cycle == 4)
                {
                    Assert.False(monitor!.Snapshot.UsingCachedFrame);
                    Assert.Equal(2, source.CaptureFullCount);
                    Assert.Equal(clock.UtcNow, monitor.Snapshot.LastFrameAt);
                    cts.Cancel();
                }
                clock.Advance(delay);
                return Task.CompletedTask;
            });
        await monitor.RunAsync(cts.Token);
    }
}
