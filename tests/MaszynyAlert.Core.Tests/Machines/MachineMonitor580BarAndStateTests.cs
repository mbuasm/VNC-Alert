using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitor580BarAndStateTests
{
    private static readonly ScreenProfile Profile = TestPaths.Profile("amm580") with { MainScreen = null };

    /// <summary>Ile cykli pętli (po 1 s) trzeba, żeby przekroczyć próg postoju na 0 % (3 min) z zapasem.</summary>
    private const int CykliPonadProgZera = 190;

    private const string ToolLife = "{T4} Tool life was reached. Please change the tool.";

    private static RgbaImage Sample(string folder, string file) =>
        PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, folder, file));

    private static RgbaImage Seria(string file) => Sample("ocr-580-postep", file);

    private static MachineConfig Machine() => new()
    {
        Id = "amm580",
        Name = "580",
        Host = "192.168.1.110",
        Port = 5900,
        Profile = "amm580",
        Enabled = true,
        Mode = ConnectionMode.Persistent,
    };

    private sealed class RecordingCaptureStore : ICaptureStore
    {
        public List<string> Tags { get; } = [];

        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag)
        {
            Tags.Add(tag);
            return $"captures/{machineId}_{tag}.png";
        }

        public void Prune(TimeSpan retention)
        {
        }
    }

    private sealed class LoopDriver(int maxCycles, FakeClock clock)
    {
        private int _cycles;

        public FakeClock Clock { get; } = clock;

        public CancellationTokenSource Cts { get; } = new();

        public Action<int>? AfterCycle { get; set; }

        public Task Delay(TimeSpan interval, CancellationToken ct)
        {
            Clock.Advance(interval);
            _cycles++;
            AfterCycle?.Invoke(_cycles);
            if (_cycles >= maxCycles)
            {
                Cts.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private sealed record Harness(MachineMonitor Monitor, LoopDriver Driver, AlertStateMachine Alerts, RecordingCaptureStore Captures);

    private static Harness Build(int maxCycles, FakeScreenSource source, FakeOcrEngine ocr, ScreenProfile? profile = null)
    {
        var clock = new FakeClock();
        var driver = new LoopDriver(maxCycles, clock);
        var timing = new TimingConfig();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var captures = new RecordingCaptureStore();
        var monitor = new MachineMonitor(
            Machine(), profile ?? Profile, () => source, testOverride: null, ocr,
            new MessageMapper(TestPaths.ExampleRules), alerts, clock, timing, captures, logger: null, driver.Delay);
        return new Harness(monitor, driver, alerts, captures);
    }

    /// <summary>OCR, który pola postępu nie czyta — jak Windows OCR en-US przy 25 % (samo „Progress:”).</summary>
    private static FakeOcrEngine OcrBezLiczby() => new() { DefaultResult = new OcrResult("Progress:", ["Progress:"]) };

    [Theory]
    [InlineData("580-2026-09-25-110103-run-1.png", 1)]
    [InlineData("580-2026-09-25-112320-run-14.png", 14)]
    [InlineData("580-2026-09-25-114133-run-25.png", 25)]
    [InlineData("580-2026-09-25-120953-run-41.png", 41)]
    public async Task ProcentZPaska_GdyOcrNieCzytaLiczby(string file, int percent)
    {
        // Pasek jest źródłem
        // głównym, więc karta ma procent z ekranu, a OCR liczby w ogóle nie jest potrzebny.
        var source = new FakeScreenSource("amm580", Seria(file));
        FakeOcrEngine ocr = OcrBezLiczby();
        Harness h = Build(maxCycles: 5, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.True(snapshot.IdleWatchEnabled);
        Assert.Equal(percent, snapshot.LastProgressPercent);
        Assert.Equal("pasek", snapshot.LastProgressSource);
        Assert.NotNull(snapshot.LastProgressBarText);
        Assert.Equal(0, ocr.CallCount);
        Assert.False(snapshot.ProgramAborted);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task BezSilnikaOcr_PasekNadalDajeProcent()
    {
        var source = new FakeScreenSource("amm580", Seria("580-2026-09-25-114133-run-25.png"));
        var ocr = new FakeOcrEngine { IsAvailable = false, Name = "brak" };
        Harness h = Build(maxCycles: 5, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(h.Monitor.Snapshot.IdleWatchEnabled);
        Assert.Equal(25, h.Monitor.Snapshot.LastProgressPercent);
        Assert.Equal("pasek", h.Monitor.Snapshot.LastProgressSource);
    }

    [Fact]
    public async Task ZeroWStanieRun_NieJestPostojem()
    {
        var source = new FakeScreenSource("amm580", Seria("580-2026-09-25-105836-run-0.png"));
        Harness h = Build(maxCycles: CykliPonadProgZera, source, OcrBezLiczby());

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, h.Monitor.Snapshot.LastProgressPercent);
        Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task ZeroZOcrWStanieRun_TezNieJestPostojem()
    {
        // Ta sama bramka dotyczy odczytu „0%” z OCR (gdy toru paska nie widać, np. zasłonięty kursorem):
        // profil bez paska, OCR czyta „0%”, pudełko zielone — brak postoju.
        ScreenProfile bezPaska = Profile with { Idle = Profile.Idle! with { ProgressBar = null } };
        var source = new FakeScreenSource("amm580", Seria("580-2026-09-25-105836-run-0.png"));
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult("Progress: 0%", ["Progress: 0%"]) };
        Harness h = Build(maxCycles: CykliPonadProgZera, source, ocr, bezPaska);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, h.Monitor.Snapshot.LastProgressPercent);
        Assert.Equal("ocr", h.Monitor.Snapshot.LastProgressSource);
        Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task ZeroPrzyIdle_DajeStoiNaZero()
    {
        // Druga strona bramki: „IDLE: Abort” przy 0 % (praca skasowana, maszyna czeka na Start) to postój —
        // po 3 min alert „Stoi na 0 %”, a NIE alarm przerwanego programu (ten jest tylko przy 1–99 %).
        var source = new FakeScreenSource("amm580", Sample("production-amm580", "auto-bez-alarmu-0-procent.png"));
        Harness h = Build(maxCycles: CykliPonadProgZera, source, OcrBezLiczby());

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.NotNull(h.Monitor.Snapshot.ProgressZeroSince);
        Assert.False(h.Monitor.Snapshot.ProgramAborted);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
    }

    [Fact]
    public async Task IdleAbortWTrakciePracy_DajeAlarmJakBlad()
    {
        // Pęknięte wiertło przy 25 %: ekran jak przy pracy, tylko „IDLE: Abort” w pudełku stanu, bez czerwieni.
        var source = new FakeScreenSource("amm580", Seria("580-2026-09-25-104520-idle-abort-25.png"));
        Harness h = Build(maxCycles: 5, source, OcrBezLiczby());

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(h.Monitor.Snapshot.ProgramAborted);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.Equal("rule:amm580" + AlarmHelp.ProgramAbortRuleSuffix, alert.Key.Discriminator);
        Assert.Equal(MachineMonitor.ProgramAbortShortText, alert.ShortText);
        Assert.Equal("narzedzie", alert.Category);
        Assert.True(alert.Sound);
        Assert.Contains("przerwany", h.Captures.Tags);
        Assert.Contains("BreakPoint Resume", AlarmHelp.For(alert), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleAbortPrzyWyciszonymToolLife_NadalDzwoni()
    {
        var source = new FakeScreenSource("amm580", Sample("production-amm580", "alarm-t4-tool-life.png"));
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(ToolLife, [ToolLife]) };
        Harness h = Build(maxCycles: 8, source, ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(h.Monitor.Snapshot.ProgramAborted);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("rule:amm580" + AlarmHelp.ProgramAbortRuleSuffix, alert.Key.Discriminator);
    }

    [Fact]
    public async Task PoPowrocieDoRun_AlarmPrzerwanegoProgramuZnika()
    {
        // Operator wymienił narzędzie i wznowił pracę — zielone pudełko kończy stan „program przerwany”,
        // a alert zamyka Tick jak każdy błąd, którego już nie widać.
        var source = new FakeScreenSource("amm580", Seria("580-2026-09-25-104520-idle-abort-25.png"));
        Harness h = Build(maxCycles: 60, source, OcrBezLiczby());
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 10)
            {
                source.Frame = Seria("580-2026-09-25-114133-run-25.png");
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);
        h.Alerts.Tick();

        Assert.False(h.Monitor.Snapshot.ProgramAborted);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Theory]
    [InlineData("production-amm580", "manual-kalibracja-bez-alarmu.png")]
    [InlineData("ocr-580-postep", "580-2026-09-25-110305-run-1-kursor.png")]
    [InlineData("ocr-580-postep", "580-2026-09-25-120953-run-41.png")]
    public async Task InneStany_NieDajaAlarmuPrzerwanegoProgramu(string folder, string file)
    {
        // „IDLE: Normal Stop” (zakładka Manual), praca z kursorem w polu komunikatu, praca przy 41 %.
        var source = new FakeScreenSource("amm580", Sample(folder, file));
        Harness h = Build(maxCycles: 8, source, OcrBezLiczby());

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.False(h.Monitor.Snapshot.ProgramAborted);
        Assert.DoesNotContain(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Error);
    }
}
