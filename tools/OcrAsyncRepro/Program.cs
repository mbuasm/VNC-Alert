using System.Text.Json;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

var cfg = ConfigLoader.LoadAppConfig("config/config.example.json");
var rules = ConfigLoader.LoadRules("config/rules.example.json");
foreach (string scenario in new[] { "idle", "idle-no-hmi", "wrong-screen", "error-clear" })
foreach (bool deferred in new[] { false, true })
{
    var clock = new FakeClock();
    using var cts = new CancellationTokenSource();
    var profile = cfg.Profiles["opencnc"] with
    {
        StatusBarRoi = new RectInt(10, 10, 280, 20),
        MainScreen = scenario == "idle-no-hmi" ? null : cfg.Profiles["opencnc"].MainScreen,
        Idle = scenario.StartsWith("idle") ? cfg.Profiles["opencnc"].Idle! with
        {
            ProgressRoi = new RectInt(10, 80, 100, 20)
        } : null
    };
    var machine = cfg.Machines.First(m => m.Enabled);
    var ocr = new ControlledOcr(deferred, scenario == "wrong-screen");
    var alerts = new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2)));
    var events = new List<AlertEvent>();
    alerts.Changed += events.Add;
    RgbaImage Frame(bool red)
    {
        var result = new RgbaImage(320, 180);
        result.Fill(new Rgba(240, 240, 240));
        if (red)
        {
            var bar = new RgbaImage(280, 20);
            bar.Fill(new Rgba(255, 0, 0));
            result.Blit(bar, 10, 10);
        }
        return result;
    }
    var source = new FakeScreenSource(machine.Id, Frame(scenario == "error-clear"));
    int cycles = 0, unknownCycles = 0, mainCycles = 0, otherCycles = 0;
    MachineMonitor? monitor = null;
    monitor = new MachineMonitor(machine, profile, () => source, null, ocr,
        new MessageMapper(rules), alerts, clock, cfg.Timing, new NullCaptureStore(),
        delay: (interval, _) =>
        {
            cycles++;
            if (monitor!.Snapshot.MainScreenVisible is null) unknownCycles++;
            else if (monitor.Snapshot.MainScreenVisible == true) mainCycles++;
            else otherCycles++;
            if (monitor.Snapshot.LastError is not null)
                throw new InvalidOperationException(monitor.Snapshot.LastError);
            alerts.Tick();
            // OCR was demonstrably incomplete during this cycle; complete it before the next.
            // Inline TCS continuations make this a deterministic scheduler, without wall-clock sleeps.
            ocr.CompletePending();
            if (scenario == "error-clear" && cycles == 5) source.Frame = Frame(false);
            clock.Advance(interval);
            if (cycles == 240) cts.Cancel();
            return Task.CompletedTask;
        });
    await monitor.RunAsync(cts.Token);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scenario, deferred, cycles, unknownCycles, mainCycles, otherCycles,
        ocr.Calls, ocr.DeferredCalls,
        raised = events.Where(e => e.Kind == AlertEventKind.Raised).Select(e => e.Alert.Kind.ToString()).ToArray(),
        closed = events.Where(e => e.Kind == AlertEventKind.Closed).Select(e => e.Alert.Kind.ToString()).ToArray(),
        active = alerts.ActiveAlerts.Select(a => a.Kind.ToString()).ToArray()
    }));
    if (!deferred)
    {
        var expected = scenario switch { "idle" or "idle-no-hmi" => AlertKind.MachineIdle, "wrong-screen" => AlertKind.WrongScreen, _ => AlertKind.Error };
        if (!events.Any(e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == expected))
            throw new InvalidOperationException($"Invalid positive control: {scenario} never raised {expected}.");
        if (scenario == "error-clear" && !events.Any(e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == expected))
            throw new InvalidOperationException("Invalid positive control: error did not close.");
    }
}

sealed class ControlledOcr(bool deferred, bool wrongScreen) : IOcrEngine
{
    private readonly Queue<(TaskCompletionSource<OcrResult> Completion, OcrResult Result)> _pending = new();
    public string Name => "deterministic-audit";
    public bool IsAvailable => true;
    public string? LanguageTag => "en-US";
    public int Calls { get; private set; }
    public int DeferredCalls { get; private set; }
    public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        bool full = (image.Width == 320 && image.Height == 180)
            || (image.Width == 640 && image.Height == 360);
        var result = full
            ? OcrResult.FromWords(new[]
            {
                new OcrWord(wrongScreen ? "Production" : "Milling", new RectInt(10, 40, 70, 14), 0),
                new OcrWord(wrongScreen ? "info" : "Status", new RectInt(90, 40, 60, 14), 0)
            })
            : new OcrResult("100.00%", new[] { "100.00%" });
        if (!deferred) return Task.FromResult(result);
        DeferredCalls++;
        var completion = new TaskCompletionSource<OcrResult>();
        _pending.Enqueue((completion, result));
        return completion.Task;
    }
    public void CompletePending()
    {
        // The scaled OCR retry may enqueue another request during completion.
        while (_pending.TryDequeue(out var item)) item.Completion.SetResult(item.Result);
    }
}
