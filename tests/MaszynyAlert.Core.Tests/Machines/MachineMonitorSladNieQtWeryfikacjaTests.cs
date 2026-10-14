using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorSladNieQtWeryfikacjaTests
{
    private static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null, Idle = null };

    private static readonly string[] DesktopNames =
        ["68EDA4721759", "WinCE", "OPENCNC2", "test", "", "Qt VNC Server", "Qt for Embedded Linux VNC Server"];

    private const string ClosedByServer = "Połączenie z 192.168.1.110:5900 zostało zamknięte przez serwer w trakcie odczytu.";
    private const string Refused = "Nie udało się połączyć z 192.168.1.110:5900: Nie można nawiązać połączenia, ponieważ komputer docelowy aktywnie go odmawia.";

    private sealed class Ctx(FakeClock clock, long startMs)
    {
        public FakeClock Clock { get; } = clock;

        public long StartMs { get; } = startMs;

        public long Elapsed => Clock.MonotonicMs - StartMs;

        public Exception After(int ms, Exception ex)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(ms));
            return ex;
        }
    }

    private sealed record Scenario(string Name, int Cycles, Func<Ctx, int, Exception?> Connect, Func<Ctx, int, int, Exception?> Capture);

    private static IEnumerable<Scenario> Scenarios() =>
    [
        new("S1 zamkniecie-216ms-potem-odmowy", 30,
            (_, n) => n == 1 ? null : new RfbException(Refused),
            (c, n, k) => n == 1 && k == 1 ? c.After(216, new RfbException(ClosedByServer)) : null),
        new("S2 protokol-1282-po-400ms", 20,
            (_, _) => null,
            (c, _, k) => k == 1 ? c.After(400, new RfbProtocolException("Nieobsługiwane kodowanie prostokąta aktualizacji: 1282 (test).")) : null),
        new("S3 zerwanie-po-3-klatkach", 40,
            (_, _) => null,
            (_, _, k) => k == 4 ? new RfbException(ClosedByServer) : null),
        new("S4 haslo-odrzucone", 12,
            (_, _) => new RfbAuthenticationException("Serwer VNC odrzucił hasło (test)."),
            (_, _, _) => null),
        new("S5 limit-czasu-polaczenia", 15,
            (c, _) => c.After(10000, new RfbException("Nie udało się połączyć z 192.168.1.110:5900 w ciągu 10s (limit czasu połączenia).")),
            (_, _, _) => null),
        new("S6 odmowy-12min-potem-sesja", 70,
            (c, _) => c.Elapsed < 12 * 60_000 ? new RfbException(Refused) : null,
            (_, _, _) => null),
        new("S7 zamkniecie-po-2s-bez-klatki", 15,
            (_, _) => null,
            (c, _, k) => k == 1 ? c.After(2000, new RfbException(ClosedByServer)) : null),
        new("S8 mieszany", 90,
            (_, n) => n is 2 or (>= 4 and <= 6) ? new RfbException(Refused) : null,
            (c, n, k) => (n, k) switch
            {
                (1, 4) => new RfbException(ClosedByServer),
                (3, 1) => c.After(75, new RfbException(ClosedByServer)),
                _ => null,
            }),
        new("S9 zerwanie-po-12min-sesji", 800,
            (_, _) => null,
            (_, _, k) => k == 720 ? new RfbException(ClosedByServer) : null),
        new("S10 limit-odczytu-w-sesji", 40,
            (_, _) => null,
            (c, _, k) => k == 5 ? c.After(30000, new RfbException("Przekroczono limit 30s odczytu komunikatu 192.168.1.110:5900 — serwer podaje dane zbyt wolno.")) : null),
    ];

    /// <summary>Źródło obrazu sterowane skryptem scenariusza (numer połączenia od 1, numer odczytu w sesji od 1).</summary>
    private sealed class ScriptedSource(Ctx ctx, Scenario scenario, RgbaImage frame, string desktopName) : IScreenSource
    {
        private int _captures;

        public string MachineId => "amm580";

        public bool IsConnected { get; private set; }

        public ScreenSourceInfo? Info { get; private set; }

        public int Connects { get; private set; }

        public List<long> ConnectTimes { get; } = [];

        public Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Connects++;
            ConnectTimes.Add(ctx.Elapsed);
            _captures = 0;
            if (scenario.Connect(ctx, Connects) is { } failure)
            {
                IsConnected = false;
                throw failure;
            }

            IsConnected = true;
            Info = new ScreenSourceInfo("vnc", "3.8", "None", "32bpp BGRA LE", frame.Width, frame.Height, desktopName);
            return Task.FromResult(Info);
        }

        public Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct)
        {
            Check(ct);
            return Task.FromResult(frame.Crop(roi.Clamp(frame.Width, frame.Height)));
        }

        public async Task<RgbaImage?> TryCaptureAsync(RectInt roi, bool incremental, TimeSpan idleTimeout, CancellationToken ct) =>
            await CaptureAsync(roi, incremental, ct).ConfigureAwait(false);

        public Task<RgbaImage> CaptureFullAsync(CancellationToken ct)
        {
            Check(ct);
            return Task.FromResult(frame);
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        private void Check(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _captures++;
            if (scenario.Capture(ctx, Connects, _captures) is { } failure)
            {
                IsConnected = false;
                throw failure;
            }
        }
    }

    private static async Task RunAsync(Scenario scenario, string desktopName, StringBuilder trace)
    {
        var clock = new FakeClock();
        var ctx = new Ctx(clock, clock.MonotonicMs);
        DateTimeOffset startUtc = clock.UtcNow;
        var timing = new TimingConfig { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(timing.AckMemoryHours)));
        var loggers = new RecordingLoggerFactory();
        RgbaImage frame = SyntheticScreens.FullFrame(Profile, SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height, SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));
        var source = new ScriptedSource(ctx, scenario, frame, desktopName);
        var machine = new MachineConfig
        {
            Id = "amm580", Name = "580", Host = "192.168.1.110", Port = 5900, Profile = "opencnc", Enabled = true,
            Mode = ConnectionMode.Persistent,
        };
        string tag = $"[{scenario.Name}|{(desktopName.Length == 0 ? "<pusta>" : desktopName)}]";
        alerts.Changed += e => trace.AppendLine(
            $"{tag} E t={ctx.Elapsed} {e.Kind} {e.Alert.Kind} start={(e.Alert.StartedAt - startUtc).TotalMilliseconds} status={e.Alert.Status}");

        using var cts = new CancellationTokenSource();
        int cycles = 0;
        MachineMonitor? monitor = null;
        monitor = new MachineMonitor(machine, Profile, () => source, null, new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules),
            alerts, clock, timing, new NullCaptureStore(), loggers.CreateLogger<MachineMonitor>(), (interval, _) =>
            {
                cycles++;
                MachineStatusSnapshot s = monitor!.Snapshot;
                trace.AppendLine($"{tag} c{cycles} t={ctx.Elapsed} d={interval.TotalMilliseconds} st={s.State} off={(s.OfflineSince is { } o ? (o - startUtc).TotalMilliseconds : -1)} err={s.LastError}");
                clock.Advance(interval);
                alerts.Tick();
                if (cycles >= scenario.Cycles) cts.Cancel();
                return Task.CompletedTask;
            });

        await monitor.RunAsync(cts.Token);

        trace.AppendLine($"{tag} P {string.Join(",", source.ConnectTimes)}");
        foreach (LogEntry e in loggers.Entries.Where(e => e.Category.EndsWith(nameof(MachineMonitor), StringComparison.Ordinal)
            && e.Level >= LogLevel.Information))
        {
            trace.AppendLine($"{tag} L {e.Level} {e.Message.ReplaceLineEndings(" ")}");
        }
    }

    [Fact]
    public async Task Slad_ScenariuszeAwariiPolaczenia_DlaNazwPulpitu()
    {
        var trace = new StringBuilder();
        foreach (Scenario scenario in Scenarios())
        {
            foreach (string name in DesktopNames)
            {
                await RunAsync(scenario, name, trace);
            }
        }

        string? file = Environment.GetEnvironmentVariable("WER_TRACE_FILE");
        if (!string.IsNullOrWhiteSpace(file))
        {
            File.WriteAllText(file, trace.ToString(), new UTF8Encoding(false));
        }

        Assert.True(trace.Length > 0);
    }
}
