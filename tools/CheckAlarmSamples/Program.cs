using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;
using MaszynyAlert.Windows;

// Run from the repository root. Uses the real MachineMonitor and Windows OCR, with a local
// image source only. Crops cannot verify main-screen or progress recognition; disable those.
string root = "tests/samples/alarm-crops-opencnc";
string output = args.Length > 0 ? args[0] : "artifacts/alarm-crops-ocr.json";
var profile = ConfigLoader.LoadAppConfig("config/config.example.json").Profiles["opencnc"]
    with { MainScreen = null, Idle = null };
var mapper = new MessageMapper(ConfigLoader.LoadRules("config/rules.example.json"));
var ocr = new RecordingOcr(new WindowsOcrEngine());
if (!ocr.IsAvailable) throw new InvalidOperationException("Windows OCR jest niedostępny; nie wykonano weryfikacji.");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
var rows = new List<object>();
bool allPassed = true;
foreach (var sample in manifest.RootElement.GetProperty("samples").EnumerateArray())
{
    string file = sample.GetProperty("file").GetString()!;
    string machineId = sample.GetProperty("machine").GetString()!;
    bool expectedRed = sample.GetProperty("expectedRed").GetBoolean();
    string? expectedText = sample.GetProperty("expectedShortText").GetString();
    string? expectedCode = sample.GetProperty("expectedCode").ValueKind == JsonValueKind.Null
        ? null : sample.GetProperty("expectedCode").GetInt32().ToString();
    var crop = PngCodec.DecodeFile(Path.Combine(root, file));
    var red = new RedTextDetector(profile).Analyze(crop);
    var frame = new RgbaImage(1024, 768);
    frame.Blit(crop, profile.StatusBarRoi.X, profile.StatusBarRoi.Y);
    var machine = new MachineConfig { Id = machineId, Name = machineId, Host = "local-file", Profile = profile.Id };
    var clock = new SystemClock();
    var timing = new TimingConfig { PollIntervalMs = 20, OcrMaxAttempts = 1 };
    var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
    ocr.Texts.Clear();
    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var monitor = new MachineMonitor(machine, profile, () => new CropSource(machineId, frame),
        null, ocr, mapper, alerts, clock, timing, new NullCaptureStore());
    var running = monitor.RunAsync(ct.Token);
    try
    {
        do
        {
            await Task.Delay(100, ct.Token);
        } while (expectedRed && monitor.Snapshot.LastShortText != expectedText);
    }
    catch (OperationCanceledException) { }
    finally { await ct.CancelAsync(); await running; }
    var alert = alerts.ActiveAlerts.SingleOrDefault();
    bool passed = red.IsRed == expectedRed && (expectedRed
        ? alert?.Key == AlertKey.ForRule(machineId, "code-" + expectedCode) && alert.ShortText == expectedText
        : alert is null && ocr.Texts.Count == 0);
    allPassed &= passed;
    rows.Add(new { file, red.RedPixels, red.RedFraction, red.IsRed,
        ocrAttempts = ocr.Texts.ToArray(), key = alert?.Key.ToString(), alert?.ShortText, passed });
}
string json = JsonSerializer.Serialize(new { ocr.IsAvailable, ocr.LanguageTag, allPassed, results = rows },
    new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, json);
Console.WriteLine(json);
return allPassed ? 0 : 1;

sealed class RecordingOcr(IOcrEngine inner) : IOcrEngine
{
    public string Name => inner.Name;
    public bool IsAvailable => inner.IsAvailable;
    public string? LanguageTag => inner.LanguageTag;
    public List<string> Texts { get; } = [];
    public async Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
    {
        var result = await inner.RecognizeAsync(image, ct);
        Texts.Add(result.Text);
        return result;
    }
}

sealed class CropSource(string machineId, RgbaImage frame) : IScreenSource
{
    public string MachineId => machineId;
    public bool IsConnected { get; private set; }
    public ScreenSourceInfo? Info { get; private set; }
    public Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        Info = new ScreenSourceInfo("local-file", "", "", "decoded PNG on synthetic canvas", frame.Width, frame.Height, "crop-check");
        return Task.FromResult(Info);
    }
    public Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct) => Task.FromResult(frame.Crop(roi));
    public Task<RgbaImage?> TryCaptureAsync(RectInt roi, bool incremental, TimeSpan idleTimeout, CancellationToken ct) => Task.FromResult<RgbaImage?>(frame.Crop(roi));
    public Task<RgbaImage> CaptureFullAsync(CancellationToken ct) => Task.FromResult(frame);
    public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
}
