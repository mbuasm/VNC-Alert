using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using static MaszynyAlert.Core.Tests.Machines.MachineMonitorAsyncOcrTests;

namespace MaszynyAlert.Core.Tests.Machines;

/// <summary>
/// Odtworzona obserwacja (przejęcie roli w grupie v2 albo nowy komplet monitorów po uśpieniu) z
/// rzeczywistym cyklem <see cref="MachineMonitor"/> i opóźnionym OCR: brak świeżej klatki/odczytu nie
/// może fałszywie zamknąć przejętego alertu ani odtworzyć dźwięku/pop-upu jako nowego epizodu.
/// </summary>
public sealed class MachineMonitorTakeoverTests
{
    private static readonly MachineConfig Machine = TestPaths.ExampleConfig.Machines.First(m => m.Enabled);
    private static readonly MessageMapper Mapper = new(TestPaths.ExampleRules);
    private static readonly MappedMessage Servo = Mapper.Map("No.705", "opencnc");

    private static Alert ErrorEpisode(AlertStatus status, string ocrText = "No.705", TimeSpan? ackAge = null)
    {
        var mapped = Mapper.Map(ocrText, "opencnc");
        return new Alert
        {
            Key = AlertKey.ForMessage(Machine.Id, mapped), MachineId = Machine.Id, MachineName = Machine.Name,
            Kind = AlertKind.Error, ShortText = mapped.ShortText, FullText = mapped.RawText.Length == 0 ? null : mapped.RawText,
            Category = mapped.Category, Priority = mapped.Priority, Sound = true,
            StartedAt = FakeClock.DefaultStart.AddMinutes(-5), LastSeenAt = FakeClock.DefaultStart.AddSeconds(-20),
            Status = status,
            AcknowledgedAt = status == AlertStatus.Acknowledged ? FakeClock.DefaultStart - (ackAge ?? TimeSpan.FromMinutes(3)) : null,
            AckedByHost = status == AlertStatus.Acknowledged ? "PC-B" : null,
            AckedByUser = status == AlertStatus.Acknowledged ? "operator" : null,
        };
    }

    private static Alert IdleEpisode(AlertStatus status) => new()
    {
        Key = AlertKey.Idle(Machine.Id), MachineId = Machine.Id, MachineName = Machine.Name,
        Kind = AlertKind.MachineIdle, ShortText = "Stoi bez pracy (100 %) – dołóż prace", Category = "postoj",
        StartedAt = FakeClock.DefaultStart.AddMinutes(-10), LastSeenAt = FakeClock.DefaultStart.AddSeconds(-20),
        Status = status, Sound = false,
        AcknowledgedAt = status == AlertStatus.Acknowledged ? FakeClock.DefaultStart.AddMinutes(-7) : null,
        AckedByHost = status == AlertStatus.Acknowledged ? "PC-B" : null,
        AckedByUser = status == AlertStatus.Acknowledged ? "operator" : null,
    };

    private static Alert WrongScreenEpisode() => new()
    {
        Key = AlertKey.WrongScreen(Machine.Id), MachineId = Machine.Id, MachineName = Machine.Name,
        Kind = AlertKind.WrongScreen, ShortText = AlertStateMachine.WrongScreenShortText, Category = "ekran",
        StartedAt = FakeClock.DefaultStart.AddMinutes(-5), LastSeenAt = FakeClock.DefaultStart.AddSeconds(-20),
        Status = AlertStatus.Active, Sound = false,
    };

    private static Harness Takeover(bool watchHmi, bool idle, params Alert[] episodes)
    {
        var h = new Harness(watchHmi, idle);
        h.Alerts.UseGroupProtocol(true);
        h.Alerts.RestoreObservation(episodes);
        return h;
    }

    private static void AssertNoNewEpisodeOrClosure(Harness h)
    {
        Assert.DoesNotContain(h.Events, e => e.Event.Kind is AlertEventKind.Raised or AlertEventKind.Closed);
    }

    [Theory]
    [InlineData(AlertStatus.Active, true)]
    [InlineData(AlertStatus.Acknowledged, true)]
    [InlineData(AlertStatus.Active, false)]
    [InlineData(AlertStatus.Acknowledged, false)]
    public async Task RestoredError_RedStillVisible_KeepsEpisodeAcrossDelayedOcr(AlertStatus status, bool sameKey)
    {
        var restored = ErrorEpisode(status, sameKey ? "No.705" : string.Empty, ackAge: TimeSpan.Zero);
        using var h = Takeover(watchHmi: true, idle: false, restored);
        h.Source.Frame = Frame(red: true);
        await h.Run(120);

        AssertNoNewEpisodeOrClosure(h);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind is AlertEventKind.Acknowledged or AlertEventKind.Escalated);
        var actual = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(restored.EpisodeId, actual.EpisodeId);
        Assert.Equal(restored.StartedAt, actual.StartedAt);
        Assert.Equal(status, actual.Status);
        Assert.Equal(restored.AcknowledgedAt, actual.AcknowledgedAt);
        Assert.Equal(AlertKey.ForMessage(Machine.Id, Servo), actual.Key);
        Assert.Equal(Servo.ShortText, actual.ShortText);
        Assert.True(actual.LastSeenAt > restored.LastSeenAt, "fresh red confirmation must refresh LastSeenAt");
        if (!sameKey)
            Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Updated); // lepszy opis tego samego epizodu
    }

    [Fact]
    public async Task RestoredAcknowledgedError_EscalatesFromOriginalAcknowledgementTime()
    {
        int escalateAfter = TestPaths.ExampleConfig.Timing.EscalationAfterAckMs / 1000;
        var restored = ErrorEpisode(AlertStatus.Acknowledged, ackAge: TimeSpan.FromSeconds(escalateAfter - 60));
        using var h = Takeover(watchHmi: false, idle: false, restored);
        h.Source.Frame = Frame(red: true);
        await h.Run(120);

        AssertNoNewEpisodeOrClosure(h);
        var escalated = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Escalated);
        Assert.Equal(60, escalated.Second);
        Assert.Equal(restored.EpisodeId, escalated.Event.Alert.EpisodeId);
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 31)]
    public async Task RestoredError_RedGone_ClosesOnlyAfterObservationResumesPlusThirtySeconds(bool watchHmi, int closedAt)
    {
        using var h = Takeover(watchHmi, idle: false, ErrorEpisode(AlertStatus.Active));
        await h.Run(60, second =>
        {
            if (second < closedAt - 1) Assert.Single(h.Alerts.ActiveAlerts);
        });

        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        var closed = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
        Assert.Equal(AlertKind.Error, closed.Event.Alert.Kind);
        Assert.Equal(closedAt, closed.Second);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task RestoredError_HmiOnAnotherTab_HoldsEpisodeUntilMainScreenReturns()
    {
        using var h = Takeover(watchHmi: true, idle: false, ErrorEpisode(AlertStatus.Active));
        h.Ocr.Screen = Menu;
        h.Source.Frame = Frame(context: 100);
        await h.Run(120, second =>
        {
            if (second == 60)
            {
                Assert.Single(h.Alerts.ActiveAlerts);
                h.Ocr.Screen = Main;
                h.Source.Frame = Frame();
            }
        });

        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        var closed = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
        Assert.Equal(AlertKind.Error, closed.Event.Alert.Kind);
        Assert.Equal(91, closed.Second);
    }

    [Fact]
    public async Task RestoredError_MachineOfflineAtTakeover_ClosesAsAfterRestartWithoutNewAlarm()
    {
        using var h = Takeover(watchHmi: true, idle: false, ErrorEpisode(AlertStatus.Active));
        h.Source.CaptureFailure = FakeScreenSource.RfbFailure();
        await h.Run(60);

        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        var closed = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
        Assert.Equal(AlertKind.Error, closed.Event.Alert.Kind);
        // Jak w trybie jednego PC: utrata łączności = restart maszyny; Tick w harnessie biegnie raz na
        // cykl (odstępy backoff), więc zamknięcie przypada na pierwszy cykl po 30 s.
        Assert.True(closed.Second >= 30, $"closed at {closed.Second}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredDismissedIdle_MachineStillAtHundredPercent_DoesNotReturnAsNewPopup(bool watchHmi)
    {
        var restored = IdleEpisode(AlertStatus.Acknowledged);
        using var h = Takeover(watchHmi, idle: true, restored);
        await h.Run(300);

        AssertNoNewEpisodeOrClosure(h);
        var actual = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(restored.EpisodeId, actual.EpisodeId);
        Assert.Equal(restored.StartedAt, actual.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, actual.Status);
        Assert.True(actual.LastSeenAt > restored.LastSeenAt);
        Assert.True(h.Ocr.ProgressCalls >= 4);
    }

    [Fact]
    public async Task RestoredIdle_MachineRunning_ClosesAtFirstRealReadBelowHundred()
    {
        using var h = Takeover(watchHmi: false, idle: true, IdleEpisode(AlertStatus.Active));
        h.Ocr.Progress = new("37.50%", ["37.50%"]);
        await h.Run(30);

        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        var closed = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
        Assert.Equal(AlertKind.MachineIdle, closed.Event.Alert.Kind);
        Assert.Equal(1, closed.Second); // pierwszy rzeczywisty odczyt (opóźniony OCR) w 1. sekundzie
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task RestoredIdle_UnreadableProgress_ClosesOnlyByStaleTimeout()
    {
        using var h = Takeover(watchHmi: false, idle: true, IdleEpisode(AlertStatus.Active));
        h.Ocr.Progress = OcrResult.Empty;
        await h.Run(60);

        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        var closed = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
        Assert.Equal(30, closed.Second);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoredWrongScreen_FollowsActualHmiCheck(bool stillOnMenu)
    {
        var restored = WrongScreenEpisode();
        using var h = Takeover(watchHmi: true, idle: false, restored);
        h.Ocr.Screen = stillOnMenu ? Menu : Main;
        if (stillOnMenu) h.Source.Frame = Frame(context: 100);
        await h.Run(120);

        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised);
        if (stillOnMenu)
        {
            Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
            var actual = Assert.Single(h.Alerts.ActiveAlerts);
            Assert.Equal(restored.EpisodeId, actual.EpisodeId);
            Assert.Equal(restored.StartedAt, actual.StartedAt);
        }
        else
        {
            var closed = Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed);
            Assert.Equal(AlertKind.WrongScreen, closed.Event.Alert.Kind);
            Assert.Equal(1, closed.Second); // pierwsze rozstrzygnięte sprawdzenie ekranu głównego
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdleEpisode_SurvivesMonitorRestartAfterSleepUntilRealRead(bool stillAtHundred)
    {
        using var first = new Harness(watchHmi: false, idle: true);
        await first.Run(200, second =>
        {
            if (second == 150) Assert.True(first.Alerts.AcknowledgeLocal(AlertKey.Idle(Machine.Id), "PC", "operator"));
        });
        var original = Assert.Single(first.Alerts.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, original.Status);

        // Nowy komplet monitorów po wybudzeniu (ten sam stan alertów, ten sam zegar).
        using var second = new Harness(watchHmi: false, idle: true, clock: first.Clock, alerts: first.Alerts);
        if (!stillAtHundred) second.Ocr.Progress = new("12%", ["12%"]);
        await second.Run(400);

        Assert.DoesNotContain(second.Events, e => e.Event.Kind == AlertEventKind.Raised);
        if (stillAtHundred)
        {
            Assert.DoesNotContain(second.Events, e => e.Event.Kind == AlertEventKind.Closed);
            var actual = Assert.Single(second.Alerts.ActiveAlerts);
            Assert.Equal(original.EpisodeId, actual.EpisodeId);
            Assert.Equal(original.StartedAt, actual.StartedAt);
        }
        else
        {
            var closed = Assert.Single(second.Events, e => e.Event.Kind == AlertEventKind.Closed);
            Assert.Equal(AlertKind.MachineIdle, closed.Event.Alert.Kind);
            Assert.Equal(201, closed.Second);
            Assert.Empty(second.Alerts.ActiveAlerts);
        }
    }
}
