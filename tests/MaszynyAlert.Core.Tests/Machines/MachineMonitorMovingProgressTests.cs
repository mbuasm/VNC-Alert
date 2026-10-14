using MaszynyAlert.Core.Ocr;
using static MaszynyAlert.Core.Tests.Machines.MachineMonitorAsyncOcrTests;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorMovingProgressTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MovingDigits_CompletedSamplesKeepDisplayAliveWithoutIdleEvidence(bool watchHmi, bool yellow)
    {
        using var h = new Harness(watchHmi, idle: true);
        h.Ocr.Progress = new("56.10%", ["56.10%"]);
        await h.Run(70, second =>
        {
            if (second >= 4)
            {
                Assert.Equal(56, h.Monitor.Snapshot.LastProgressPercent);
                Assert.Equal("56.10%", h.Monitor.Snapshot.LastProgressText);
                Assert.Equal(0, h.Monitor.Snapshot.LastProgressFraction);
                Assert.False(h.Monitor.Snapshot.ProgressFullConfirmed);
                Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
                Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
            }
            var frame = Frame(progress: (byte)(100 + second));
            if (yellow)
            {
                var status = new MaszynyAlert.Core.Imaging.RgbaImage(StatusRoi.Width, StatusRoi.Height);
                status.Fill(new MaszynyAlert.Core.Imaging.Rgba(255, 255, 0));
                frame.Blit(status, StatusRoi.X, StatusRoi.Y);
            }
            // Every OCR task completes between cycles, but its crop changes before consumption.
            h.Source.Frame = frame;
        });
        Assert.True(h.Ocr.ProgressCalls > 10);
        Assert.Empty(h.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task MovingDigits_DoNotPublishAnEndpointOrRaiseIdleFromAnOldCrop(int percent)
    {
        using var h = new Harness(watchHmi: true, idle: true);
        h.Ocr.Progress = new($"{percent}%", [$"{percent}%"]);
        await h.Run(240, second =>
        {
            h.Source.Frame = Frame(progress: (byte)(100 + second % 100));
            Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
            Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
            Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
        });
        Assert.Empty(h.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task MovingDigits_DisplaySampleDoesNotDirectlyCloseAnExistingIdleAlert(int initial)
    {
        using var h = new Harness(watchHmi: false, idle: true);
        h.Ocr.Progress = new($"{initial}%", [$"{initial}%"]);
        // Stay inside the existing 30-second alert TTL; this test isolates direct closure by a sample.
        await h.Run(220, second =>
        {
            if (second >= 200)
            {
                Assert.Single(h.Alerts.ActiveAlerts);
                h.Ocr.Progress = new("56%", ["56%"]);
                h.Source.Frame = Frame(progress: (byte)(100 + second % 100));
            }
            if (second >= 204)
            {
                Assert.Equal(56, h.Monitor.Snapshot.LastProgressPercent);
                Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
                Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
            }
        });
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == MaszynyAlert.Core.Alerts.AlertEventKind.Closed);
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("hmi")]
    [InlineData("ocr")]
    public async Task MovingDigits_LateRunningSampleCannotCrossObservationBoundary(string boundary)
    {
        using var h = new Harness(watchHmi: true, idle: true,
            timing: Harness.LegacyMainScreenTiming with { MainScreenCheckIntervalMs = 1000 });
        h.Ocr.Progress = new("56%", ["56%"]);
        h.Ocr.HoldProgress = true;
        await h.Run(10, second =>
        {
            h.Source.Frame = Frame(progress: (byte)(100 + second));
            if (second == 3)
            {
                if (boundary == "reconnect") h.Source.CaptureFailure = MaszynyAlert.Core.Tests.TestUtil.FakeScreenSource.RfbFailure();
                if (boundary == "hmi") h.Ocr.Screen = Menu;
                if (boundary == "ocr") h.Ocr.IsAvailable = false;
            }
            if (boundary == "hmi" && second >= 3) h.Source.Frame = Frame(context: 100, progress: (byte)(100 + second));
            if (second == 5) h.Ocr.HoldProgress = false;
            if (second >= 6) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
        });
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task MovingDigits_DisplayExpiresFromCaptureTimeNotCompletionTime()
    {
        using var h = new Harness(watchHmi: false, idle: true);
        h.Ocr.Progress = new("56%", ["56%"]);
        h.Ocr.HoldProgress = true;
        await h.Run(30, second =>
        {
            h.Source.Frame = Frame(progress: (byte)(100 + second));
            if (second == 12) h.Ocr.HoldProgress = false;
            if (second == 13)
            {
                Assert.Equal(56, h.Monitor.Snapshot.LastProgressPercent);
                h.Ocr.HoldProgress = true;
            }
            if (second >= 15) Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
        });
        Assert.Empty(h.Events);
    }
}
