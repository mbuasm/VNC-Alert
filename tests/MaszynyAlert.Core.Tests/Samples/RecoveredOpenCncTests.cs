using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class RecoveredOpenCncTests
{
    private static RgbaImage Load(string name) =>
        PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, "recovered-opencnc", name));

    [Theory]
    [InlineData("alarm-page-1615.png", true)]
    [InlineData("production-plan.png", false)]
    [InlineData("production-partial.png", false)]
    [InlineData("file-management.png", false)]
    public void RealStatusRoiDistinguishesErrorFromRedMenuHeaders(string name, bool error)
    {
        var frame = Load(name);
        Assert.Equal(1024, frame.Width);
        Assert.Equal(768, frame.Height);
        var profile = TestPaths.Profile("opencnc");
        var detection = new RedTextDetector(profile).Analyze(frame.Crop(profile.StatusBarRoi));
        Assert.Equal(error, detection.IsRed);
        if (error) Assert.InRange(detection.RedFraction, 0.07, 0.09);
        else Assert.Equal(0, detection.RedFraction);
    }

    [Theory]
    [InlineData("alarm-page-1615.png", true, "Current alarm page")]
    [InlineData("production-plan.png", false, "Production management")]
    [InlineData("production-partial.png", false, "Production management")]
    [InlineData("file-management.png", false, "Production management")]
    public async Task RealFrameWithRecognizedOverlayRaisesErrorButNeverIdle(string name, bool error, string label)
    {
        var clock = new FakeClock();
        var timing = new TimingConfig { MainScreenCheckIntervalMs = 1000, IdleAlertAfterMs = 1000 };
        var state = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        // Test ścieżki monitora na prawdziwych pikselach, ze STUBEM OCR etykiety.
        // Wynik prawdziwego Windows OCR jest osobnym pomiarem, nie jest udawany przez ten test.
        var words = label.Split(' ').Select((word, i) => new OcrWord(word, new RectInt(450 + i * 70, 125, 65, 16), 0)).ToArray();
        var ocr = new FakeOcrEngine { DefaultResult = OcrResult.FromWords(words) };
        var machine = new MachineConfig { Id = "recovered", Name = "Recovered", Host = "unused", Profile = "opencnc" };
        var source = new FakeScreenSource(machine.Id, Load(name));
        using var cts = new CancellationTokenSource();
        int cycles = 0;
        var monitor = new MachineMonitor(machine, TestPaths.Profile("opencnc"), () => source, null,
            ocr, new MessageMapper(TestPaths.ExampleRules), state, clock, timing, new NullCaptureStore(),
            delay: (delay, _) => { clock.Advance(delay); if (++cycles == 5) cts.Cancel(); return Task.CompletedTask; });
        await monitor.RunAsync(cts.Token);
        Assert.Equal(error, state.ActiveAlerts.Any(a => a.Kind == AlertKind.Error));
        Assert.DoesNotContain(state.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.False(monitor.Snapshot.MainScreenVisible);
        Assert.Null(monitor.Snapshot.LastProgressPercent);
    }
}
