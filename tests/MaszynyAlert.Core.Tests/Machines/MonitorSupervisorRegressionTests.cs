using System.Diagnostics;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

/// <summary>
/// Testy regresyjne zatrzymywania i ponownego uruchamiania nadzorcy: monitory
/// „wiszące” w środku cyklu nie mogą ani blokować wywołania <see cref="MonitorSupervisor.StopAsync"/>,
/// ani doprowadzić do dwóch żywych zestawów monitorów, ani do <see cref="ObjectDisposedException"/>
/// z przedwcześnie zwolnionego CTS.
/// </summary>
public sealed class MonitorSupervisorRegressionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Silnik OCR, który „wisi” w <see cref="RecognizeAsync"/> do czasu zwolnienia bramki — CELOWO
    /// ignoruje token anulowania (tak zachowuje się zawieszone wywołanie natywne, np. WinRT OCR).
    /// Liczy równoczesne wywołania, dzięki czemu test widzi, czy nie żyją naraz DWA zestawy monitorów.
    /// </summary>
    private sealed class HangingOcrEngine : IOcrEngine
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _concurrent;
        private int _maxConcurrent;

        public string Name => "test-hang";

        public bool IsAvailable => true;

        public string? LanguageTag => "en-US";

        /// <summary>Ile razy w ogóle wejściowo wywołano rozpoznawanie.</summary>
        public int EnteredCount => Volatile.Read(ref _entered);

        /// <summary>Największa liczba rozpoznawań wykonywanych JEDNOCZEŚNIE.</summary>
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        /// <summary>Odblokowuje wszystkie wiszące (i wszystkie przyszłe) wywołania.</summary>
        public void Release() => _gate.TrySetResult();

        public async Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
        {
            Interlocked.Increment(ref _entered);
            int now = Interlocked.Increment(ref _concurrent);
            int max = Volatile.Read(ref _maxConcurrent);
            while (now > max && Interlocked.CompareExchange(ref _maxConcurrent, now, max) != max)
            {
                max = Volatile.Read(ref _maxConcurrent);
            }

            try
            {
                // Bez .WaitAsync(ct) — zawieszone wywołanie natywne nie reaguje na anulowanie.
                await _gate.Task.ConfigureAwait(false);
                return OcrResult.Empty;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    private sealed record Fixture(
        MonitorSupervisor Supervisor,
        HangingOcrEngine Ocr,
        RecordingLoggerFactory Loggers,
        AppConfig Config);

    /// <summary>
    /// Nadzorca dwóch maszyn czytających obraz z katalogu <c>test-ekran</c> (czerwony pasek statusu),
    /// z bardzo krótkimi odstępami — dzięki temu monitory dochodzą do OCR (i tam wiszą) w kilkadziesiąt ms.
    /// </summary>
    private static Fixture BuildFixture(string testScreensDir)
    {
        AppConfig example = TestPaths.ExampleConfig;
        ScreenProfile profile = example.Profiles["opencnc"];

        List<MachineConfig> machines = example.Machines
            .Where(m => m.Profile == "opencnc")
            .Take(2)
            .Select(m => m with { Enabled = true, Mode = ConnectionMode.Persistent })
            .ToList();
        Assert.Equal(2, machines.Count);

        AppConfig cfg = example with
        {
            Machines = machines,
            Timing = example.Timing with
            {
                PollIntervalMs = 50,
                FullFrameIntervalMs = 60000,
                ConnectTimeoutMs = 500,
                ReconnectBackoffMs = [50],
            },
        };

        RgbaImage red = SyntheticScreens.FullFrame(
            profile,
            SyntheticScreens.StatusBar(
                profile.StatusBarRoi.Width, profile.StatusBarRoi.Height,
                SyntheticScreens.LightBackground, SyntheticScreens.RedText, "blad"));

        foreach (MachineConfig machine in machines)
        {
            PngCodec.EncodeToFile(red, Path.Combine(testScreensDir, machine.Id + ".png"));
        }

        var clock = new FakeClock();
        var ocr = new HangingOcrEngine();
        var loggers = new RecordingLoggerFactory();
        var supervisor = new MonitorSupervisor(
            cfg,
            _ => null,
            ocr,
            new MessageMapper(TestPaths.ExampleRules),
            new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2))),
            clock,
            new NullCaptureStore(),
            testScreensDir,
            loggers);

        return new Fixture(supervisor, ocr, loggers, cfg);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Patience)
            {
                Assert.Fail($"Nie doczekano się: {what} (po {sw.Elapsed.TotalSeconds:0.0} s).");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task StopAsync_GdyMonitoryWiszaWCyklu_WracaNatychmiastAStoppedTaskDopieroPoIchZakonczeniu()
    {
        using var dir = new TempDir("test-ekran");
        Fixture f = BuildFixture(dir.Path);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Ocr.EnteredCount >= 1, "wspólny silnik rozpocznie OCR");

        var sw = Stopwatch.StartNew();
        Task stopTask = supervisor.StopAsync();
        TimeSpan returnedAfter = sw.Elapsed;
        Task stoppedTask = supervisor.StoppedTask;

        // Samo wywołanie wróciło od razu, mimo że obie pętle wiszą w OCR...
        Assert.True(returnedAfter < TimeSpan.FromSeconds(2), $"StopAsync wracało {returnedAfter.TotalSeconds:0.00} s.");
        Assert.False(supervisor.IsRunning);

        // Pętle kończą się również przy nadal zablokowanym OCR.
        await stopTask.WaitAsync(Patience);
        await stoppedTask.WaitAsync(Patience);

        f.Ocr.Release();

        await stopTask.WaitAsync(Patience);
        await stoppedTask.WaitAsync(Patience);
        Assert.True(stoppedTask.IsCompletedSuccessfully);

        // Zatrzymanie nie czekało na twardy limit StopTimeout — skończyło się zaraz po pętlach.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Zatrzymanie trwało {sw.Elapsed.TotalSeconds:0.00} s.");
        Assert.DoesNotContain(f.Loggers.Entries, e => e.Exception is ObjectDisposedException);
    }

    [Fact]
    public async Task StartAsync_WywolaneWTrakcieZatrzymywania_CzekaNaZakonczenieINieTworzyDrugiegoZestawuMonitorow()
    {
        using var dir = new TempDir("test-ekran");
        Fixture f = BuildFixture(dir.Path);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Ocr.EnteredCount >= 1, "wspólny silnik rozpocznie OCR");

        MachineMonitor? firstGeneration = supervisor.Get(f.Config.Machines[0].Id);
        Assert.NotNull(firstGeneration);

        Task stopTask = supervisor.StopAsync();
        Task startTask = supervisor.StartAsync(CancellationToken.None);

        // OCR nie blokuje zakończenia pętli ani nowej generacji. Wspólny silnik
        // nadal trzyma bramkę aż porzucony odczyt faktycznie się zakończy.
        await startTask.WaitAsync(Patience);
        Assert.True(supervisor.IsRunning);
        Assert.Equal(1, f.Ocr.MaxConcurrent);

        f.Ocr.Release();

        await stopTask.WaitAsync(Patience);
        await startTask.WaitAsync(Patience);

        Assert.True(supervisor.IsRunning);
        Assert.Equal(2, supervisor.Snapshots.Count);
        Assert.Equal(
            f.Config.Machines.Select(m => m.Id),
            supervisor.Snapshots.Select(s => s.MachineId));

        MachineMonitor? secondGeneration = supervisor.Get(f.Config.Machines[0].Id);
        Assert.NotNull(secondGeneration);
        Assert.NotSame(firstGeneration, secondGeneration);

        // Kluczowa asercja „jeden zestaw naraz”: nigdy nie było więcej równoczesnych rozpoznawań OCR
        // niż maszyn (dwa zestawy dałyby cztery).
        await WaitUntilAsync(() => f.Ocr.EnteredCount > 2, "nowy zestaw monitorów ruszy z pracą");
        Assert.True(f.Ocr.MaxConcurrent <= 1,
            $"Naraz pracowało {f.Ocr.MaxConcurrent} monitorów przy {f.Config.Machines.Count} maszynach.");
        Assert.DoesNotContain(f.Loggers.Entries, e => e.Exception is ObjectDisposedException);

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task StopAsync_WywolanaDwaRazyGdyMonitoryWisza_NieRzucaIZwalniaCtsDopieroPoPetlach()
    {
        using var dir = new TempDir("test-ekran");
        Fixture f = BuildFixture(dir.Path);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Ocr.EnteredCount >= 1, "wspólny silnik rozpocznie OCR");

        Task firstStop = supervisor.StopAsync();
        Task secondStop = supervisor.StopAsync();
        Assert.False(supervisor.IsRunning);

        f.Ocr.Release();

        await firstStop.WaitAsync(Patience);
        await secondStop.WaitAsync(Patience);
        await supervisor.StoppedTask.WaitAsync(Patience);

        Assert.DoesNotContain(f.Loggers.Entries, e => e.Exception is ObjectDisposedException);
        Assert.DoesNotContain(
            f.Loggers.Entries,
            e => e.Message.Contains("Nieoczekiwany błąd", StringComparison.Ordinal));
    }
}
