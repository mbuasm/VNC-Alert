using System.Security.Cryptography;
using System.Text.Json;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class AlarmCropSamplesTests
{
    private static string Root => Path.Combine(TestPaths.SamplesOutput, "alarm-crops-opencnc");

    public static IEnumerable<object[]> Samples()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifest.json")));
        return json.RootElement.GetProperty("samples").EnumerateArray().Select(s => new object[]
        {
            s.GetProperty("file").GetString()!, s.GetProperty("sha256").GetString()!,
            s.GetProperty("expectedRedPixels").GetInt32(),
        }).ToArray();
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void OriginalCropsKeepBytesAndDetectActualColourRegardlessOfFilename(string file, string hash, int pixels)
    {
        string path = Path.Combine(Root, file);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var crop = PngCodec.DecodeFile(path);
        var profile = TestPaths.Profile("opencnc");
        Assert.Equal(profile.StatusBarRoi.Width, crop.Width);
        Assert.Equal(profile.StatusBarRoi.Height, crop.Height);
        var detection = new RedTextDetector(profile).Analyze(crop);
        Assert.Equal(pixels, detection.RedPixels);
        Assert.Equal(pixels > 0, detection.IsRed);
    }

    [Theory]
    [InlineData("aidite4_20260912-101027_blad.png", "No.15", "No. 705 [N]Axis 6 servo alarm", "code-705", "Servo 6")]
    [InlineData("aidite6_20260913-054054_blad.png", "No.\"' gmbbd", "No.1248 [p]Z2 Not grabbed to tray", "code-1248", "Tacka nie pobrana")]
    [InlineData("aidite1_20260912-114258_blad.png", "No.łmo h•—", "No.1200 [p]Request home alarm", "code-1200", "Bazowanie (Home)")]
    [InlineData("aidite4_20260912-101135_przerwa.png", "", "No.520", "code-520", "Błąd makra")]
    public async Task MonitorUsesPlainOcrWhenMaskFailsToMatchDictionary(
        string file, string mask, string plain, string rule, string shortText)
    {
        var ocr = new FakeOcrEngine().Enqueue(mask).Enqueue(plain);
        var alerts = await Observe(file, ocr);
        var alert = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite6", rule), alert.Key);
        Assert.Equal(shortText, alert.ShortText);
        Assert.Equal(2, ocr.CallCount);
    }

    [Fact]
    public async Task YellowProductionMessageNeverRaisesErrorOrRunsErrorOcr()
    {
        var ocr = new FakeOcrEngine().Enqueue("No.705");
        var alerts = await Observe("aidite4_20260912-101033_przerwa.png", ocr);
        Assert.Empty(alerts.ActiveAlerts);
        Assert.Equal(0, ocr.CallCount);
    }

    [Theory]
    [InlineData("No.9999", "unrecognized text", "Błąd nr 9999 (brak opisu w słowniku)")]
    [InlineData("No.9999", "", "Błąd nr 9999 (brak opisu w słowniku)")]
    public async Task FailedPlainReadDoesNotDiscardUnknownAlarmNumber(string mask, string plain, string shortText)
    {
        var ocr = new FakeOcrEngine().Enqueue(mask).Enqueue(plain);
        var alerts = await Observe("aidite6_20260913-054054_blad.png", ocr);
        Assert.Equal(shortText, Assert.Single(alerts.ActiveAlerts).ShortText);
        Assert.Equal(2, ocr.CallCount);
    }

    private static async Task<AlertStateMachine> Observe(string file, FakeOcrEngine ocr)
    {
        // Crops cannot prove HMI screen/progress recognition. Put the original pixels on a synthetic
        // canvas solely for the monitor's ROI extraction, with both unrelated features disabled.
        var profile = TestPaths.Profile("opencnc") with { MainScreen = null, Idle = null };
        var frame = new RgbaImage(1024, 768);
        frame.Blit(PngCodec.DecodeFile(Path.Combine(Root, file)), profile.StatusBarRoi.X, profile.StatusBarRoi.Y);
        var machine = new MachineConfig { Id = "aidite6", Name = "Aidite 6", Host = "localhost", Profile = profile.Id };
        var clock = new FakeClock();
        var timing = new TimingConfig { OcrMaxAttempts = 1 };
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        using var ct = new CancellationTokenSource();
        int cycles = 0;
        var monitor = new MachineMonitor(machine, profile, () => new FakeScreenSource(machine.Id, frame),
            null, ocr, new MessageMapper(TestPaths.ExampleRules), alerts, clock, timing, new NullCaptureStore(),
            delay: (interval, _) =>
            {
                clock.Advance(interval);
                if (++cycles == 5) ct.Cancel();
                return Task.CompletedTask;
            });
        await monitor.RunAsync(ct.Token);
        return alerts;
    }
}
