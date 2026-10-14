using System.Diagnostics;
using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using Microsoft.Extensions.Logging;

// Lokalna próba obciążenia. Bez sieci, haseł i WinRT. Parametry: czas w sekundach, plik JSON.
int seconds = args.Length > 0 ? int.Parse(args[0]) : 180;
if (seconds < 30 || seconds > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
string output = args.Length > 1 ? args[1] : "soak-result.json";
var clock = SystemClock.Instance;
var timing = new TimingConfig { PollIntervalMs = 100, FullFrameIntervalMs = 5000, MainScreenCheckIntervalMs = 2000 };
var profile = new ScreenProfile
{
    Id = "soak", StatusBarRoi = new(423, 40, 543, 66),
    MainScreen = new MainScreenWatchProfile(),
    Idle = new IdleWatchProfile { Mode = ProgressMode.Number, ProgressRoi = new(689, 422, 190, 32) }
};
var logger = new CounterLogger();
var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)), logger);
var inner = new SlowOcr();
var ocr = new SerializingOcrEngine(inner);
using var cancellation = new CancellationTokenSource();
var frame = new RgbaImage(1024, 768);
frame.Fill(new Rgba(255, 255, 255));
for (int y = 55; y < 65; y++)
    for (int x = 450; x < 750; x++) frame.SetPixel(x, y, new Rgba(255, 0, 0));
var sources = new List<StaticSource>();
var monitors = new List<MachineMonitor>();
for (int i = 1; i <= 7; i++)
{
    var machine = new MachineConfig { Id = $"soak{i}", Name = $"Soak {i}", Host = "unused", Profile = "soak" };
    var source = new StaticSource(machine.Id, frame);
    sources.Add(source);
    monitors.Add(new(machine, profile, () => source, null, ocr, new MessageMapper(new RuleSet()),
        alerts, clock, timing, new NullCaptureStore(), logger));
}
var watch = Stopwatch.StartNew();
var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
var tasks = monitors.Select(m => Task.Run(() => m.RunAsync(cancellation.Token))).ToArray();
var samples = new List<object>();
var liveBytes = new List<long>();
int checkedTicks = 0, checkedMainScreenTicks = 0;
try
{
    while (watch.Elapsed.TotalSeconds < seconds)
    {
        await Task.Delay(1000);
        alerts.Tick();
        if (watch.Elapsed.TotalSeconds >= 5)
        {
            if (alerts.ActiveAlerts.Count(a => a.Kind == AlertKind.Error) != 7)
                throw new InvalidOperationException("Utrata alarmu jednej z siedmiu maszyn.");
            if (monitors.Any(m => m.Snapshot.State != MachineConnectionState.Connected))
                throw new InvalidOperationException("Monitor utracił połączenie.");
            checkedTicks++;
            if (monitors.All(m => m.Snapshot.MainScreenVisible == true)) checkedMainScreenTicks++;
        }
        if (checkedTicks > 0 && (checkedTicks == 1 || checkedTicks % 30 == 0))
        {
            long retained = GC.GetTotalMemory(forceFullCollection: true);
            liveBytes.Add(retained);
            samples.Add(new { elapsedSeconds = watch.Elapsed.TotalSeconds, retainedBytes = retained,
                privateBytes = Process.GetCurrentProcess().PrivateMemorySize64,
                frames = sources.Sum(s => s.Captures), ocrCalls = inner.Calls, warnings = logger.Warnings });
        }
    }
}
finally
{
    cancellation.Cancel();
    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
}
if (liveBytes.Count >= 2 && liveBytes[^1] - liveBytes[0] > 32 * 1024 * 1024)
    throw new InvalidOperationException("Wzrost pamięci zarządzanej przekroczył 32 MiB.");
if (inner.MaxConcurrency != 1 || checkedMainScreenTicks == 0 || logger.Warnings != 0)
    throw new InvalidOperationException("Niepoprawna kontrola HMI, wspólnego OCR lub ostrzeżenia podczas próby.");
var result = new { passed = true, machines = 7, elapsedSeconds = watch.Elapsed.TotalSeconds,
    pollIntervalMs = timing.PollIntervalMs, checkedTicks, checkedMainScreenTicks, frames = sources.Sum(s => s.Captures),
    cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds,
    ocrCalls = inner.Calls, maxOcrConcurrency = inner.MaxConcurrency, warnings = logger.Warnings, samples };
string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(output, json);
Console.WriteLine(json);

sealed class SlowOcr : IOcrEngine
{
    private int _calls, _active, _maximum;
    public int Calls => _calls;
    public int MaxConcurrency => _maximum;
    public bool IsAvailable => true;
    public string Name => "soak fake";
    public string? LanguageTag => "en-US";
    public async Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        int active = Interlocked.Increment(ref _active);
        _maximum = Math.Max(_maximum, active);
        try { await Task.Delay(20, ct); return OcrResult.FromWords([
            new("Milling", new(580, 422, 60, 16), 0), new("Status", new(645, 422, 50, 16), 0),
            new("100.00%", new(700, 422, 80, 16), 0), new("No.705", new(450, 55, 80, 16), 1)
        ]); }
        finally { Interlocked.Decrement(ref _active); }
    }
}

sealed class StaticSource(string id, RgbaImage frame) : IScreenSource
{
    private int _captures;
    public int Captures => _captures;
    public string MachineId => id;
    public bool IsConnected { get; private set; }
    public ScreenSourceInfo? Info => new("soak", "fake", "None", "RGBA", frame.Width, frame.Height, "synthetic");
    public Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.FromResult(Info!); }
    public Task<RgbaImage> CaptureFullAsync(CancellationToken ct) { Interlocked.Increment(ref _captures); return Task.FromResult(frame); }
    public Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct)
    { Interlocked.Increment(ref _captures); return Task.FromResult(frame.Crop(roi)); }
    public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
}

sealed class CounterLogger : ILogger
{
    private int _warnings;
    public int Warnings => _warnings;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { if (level >= LogLevel.Warning) Interlocked.Increment(ref _warnings); }
}
