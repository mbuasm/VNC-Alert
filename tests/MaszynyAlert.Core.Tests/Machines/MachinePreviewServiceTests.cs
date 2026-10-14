using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachinePreviewServiceTests
{
    private static readonly MachineConfig Machine = new()
    {
        Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Port = 5900,
        Profile = "opencnc", Mode = ConnectionMode.Polling,
    };

    [Fact]
    public async Task CaptureAsync_FreshLocalFullFrame_DoesNotOpenAnotherSource()
    {
        var clock = new FakeClock();
        var config = Config();
        var image = Image(8, 6, new(12, 34, 56));
        var local = new MachinePreviewFrame(image, clock.UtcNow.AddSeconds(-4), clock.MonotonicMs - 4_000, "vnc");
        int opened = 0;
        var service = new MachinePreviewService(() => config, _ => local, (_, _) =>
        {
            opened++;
            return new ControlledScreenSource(Machine.Id, image);
        }, clock);

        MachinePreviewFrame result = await service.CaptureAsync(Machine.Id, CancellationToken.None);

        Assert.Same(local, result);
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData(-5_001)]
    [InlineData(1)]
    public async Task CaptureAsync_StaleOrFutureLocalFrame_PerformsFreshRead(long localOffsetMs)
    {
        var clock = new FakeClock();
        var config = Config();
        var old = new MachinePreviewFrame(Image(2, 2, new(1, 1, 1)), clock.UtcNow,
            clock.MonotonicMs + localOffsetMs, "vnc");
        var source = new ControlledScreenSource(Machine.Id, Image(9, 7, new(7, 8, 9)));
        var service = new MachinePreviewService(() => config, _ => old, (_, requested) =>
        {
            Assert.Equal(ConnectionMode.Persistent, requested.Mode);
            return source;
        }, clock);

        MachinePreviewFrame result = await service.CaptureAsync(Machine.Id, CancellationToken.None);

        Assert.Equal("vnc-na-zadanie", result.Source);
        Assert.Same(source.Frame, result.Image);
        Assert.Equal(1, source.ConnectCount);
        Assert.Equal(1, source.CaptureCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task CaptureAsync_DisabledOrMissingMachine_DoesNotOpenSource(
        bool appEnabled, bool machinePresent, bool machineEnabled)
    {
        var clock = new FakeClock();
        var config = Config(appEnabled, machinePresent, machineEnabled);
        int opened = 0;
        var service = new MachinePreviewService(() => config, _ => null, (_, _) =>
        {
            opened++;
            return new ControlledScreenSource(Machine.Id, Image(1, 1, new(0, 0, 0)));
        }, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync(Machine.Id, CancellationToken.None));
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task CaptureAsync_ConfigurationReplacedDuringRead_DiscardsFrameAndDisposesSource()
    {
        var clock = new FakeClock();
        AppConfig current = Config();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledScreenSource(Machine.Id, Image(4, 3, new(1, 2, 3)))
        {
            CaptureGate = release.Task,
        };
        var service = new MachinePreviewService(() => current, _ => null, (_, _) => source, clock);

        Task<MachinePreviewFrame> capture = service.CaptureAsync(Machine.Id, CancellationToken.None);
        await source.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        current = Config();
        release.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => capture);
        Assert.Contains("Ustawienia zmieniły się", error.Message);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureAsync_TimeoutDuringHandshakeOrRead_DisposesSource(bool duringHandshake)
    {
        var clock = new FakeClock();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledScreenSource(Machine.Id, Image(4, 3, new(1, 2, 3)))
        {
            ConnectGate = duringHandshake ? never.Task : Task.CompletedTask,
            CaptureGate = duringHandshake ? Task.CompletedTask : never.Task,
        };
        var config = Config();
        var service = new MachinePreviewService(() => config, _ => null, (_, _) => source, clock)
        {
            Timeout = TimeSpan.FromMilliseconds(50),
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.CaptureAsync(Machine.Id, CancellationToken.None));
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task CaptureAsync_ExternalCancellationDuringRead_StaysCancellationAndDisposesSource()
    {
        var clock = new FakeClock();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledScreenSource(Machine.Id, Image(4, 3, new(1, 2, 3)))
        {
            CaptureGate = never.Task,
        };
        var config = Config();
        var service = new MachinePreviewService(() => config, _ => null, (_, _) => source, clock);
        using var cancellation = new CancellationTokenSource();

        Task<MachinePreviewFrame> capture = service.CaptureAsync(Machine.Id, cancellation.Token);
        await source.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task CaptureAsync_TestPngHasPriorityAndBrokenPngNeverFallsBackToVnc()
    {
        using var dir = new TempDir("preview-file");
        var clock = new FakeClock();
        var config = Config() with { Paths = new PathsConfig { TestScreensDir = dir.Path } };
        var expected = Image(7, 5, new(90, 40, 10));
        string path = dir.File(Machine.Id + ".png");
        File.WriteAllBytes(path, PngCodec.Encode(expected));
        int vncOpened = 0;
        var service = new MachinePreviewService(() => config, _ => null, (cfg, requested) =>
        {
            var file = new FolderScreenSource(requested.Id, cfg.Paths.TestScreensDir);
            if (file.FileExists) return file;
            vncOpened++;
            return new ControlledScreenSource(requested.Id, Image(1, 1, new(0, 0, 0)));
        }, clock);

        MachinePreviewFrame result = await service.CaptureAsync(Machine.Id, CancellationToken.None);
        Assert.Equal("plik", result.Source);
        Assert.Equal(PngCodec.Encode(expected), PngCodec.Encode(result.Image));
        Assert.Equal(0, vncOpened);

        File.WriteAllBytes(path, [1, 2, 3, 4]);
        await Assert.ThrowsAsync<ScreenSourceException>(
            () => service.CaptureAsync(Machine.Id, CancellationToken.None));
        Assert.Equal(0, vncOpened);
    }


    private static AppConfig QtConfig(string profile = "opencnc") => new()
    {
        Enabled = true,
        Machines = [Machine with { Id = "amm520-1", Name = "520 (1)", Profile = profile, Mode = ConnectionMode.Persistent }],
    };

    [Theory]
    [InlineData("Qt for Embedded Linux VNC Server", "opencnc")] // nazwa pulpitu z handshake'u monitora
    [InlineData(null, "aidite5x")] // monitor nie działa na tym komputerze — rozpoznanie po profilu
    [InlineData(null, "premile")]
    [InlineData(null, "cameo1")]
    public async Task CaptureAsync_SerwerQt_StaraKlatkaZMonitoringu_ZwracanaBezDrugiegoPolaczenia(string? desktopName, string profile)
    {
        var clock = new FakeClock();
        var config = QtConfig(profile);
        var local = new MachinePreviewFrame(Image(4, 3, new(9, 9, 9)), clock.UtcNow.AddMinutes(-20),
            clock.MonotonicMs - 20 * 60_000, "vnc");
        int opened = 0;
        var service = new MachinePreviewService(() => config, _ => local, (_, _) =>
        {
            opened++;
            return new ControlledScreenSource("amm520-1", Image(1, 1, new(0, 0, 0)));
        }, clock, _ => desktopName);

        MachinePreviewFrame result = await service.CaptureAsync("amm520-1", CancellationToken.None);

        Assert.Same(local, result);
        Assert.Equal("vnc", result.Source); // okno: „lokalna obserwacja” z czasem klatki
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task CaptureAsync_SerwerQt_BezKlatkiZMonitoringu_CzytelnyBladBezPolaczenia()
    {
        var clock = new FakeClock();
        var config = QtConfig("aidite5x");
        int opened = 0;
        var service = new MachinePreviewService(() => config, _ => null, (_, _) =>
        {
            opened++;
            return new ControlledScreenSource("amm520-1", Image(1, 1, new(0, 0, 0)));
        }, clock);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync("amm520-1", CancellationToken.None));

        Assert.StartsWith("Podgląd niedostępny: maszyna obsługuje jedno połączenie VNC, a monitoring", ex.Message);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task CaptureAsync_SerwerInnyNizQt_StaraKlatka_NadalOtwieraOdczytNaZadanie()
    {
        var clock = new FakeClock();
        var config = QtConfig("opencnc");
        var old = new MachinePreviewFrame(Image(2, 2, new(1, 1, 1)), clock.UtcNow, clock.MonotonicMs - 6_000, "vnc");
        var source = new ControlledScreenSource("amm520-1", Image(3, 3, new(4, 5, 6)));
        var service = new MachinePreviewService(() => config, _ => old, (_, _) => source, clock, _ => "WinCE");

        MachinePreviewFrame result = await service.CaptureAsync("amm520-1", CancellationToken.None);

        Assert.Equal("vnc-na-zadanie", result.Source);
        Assert.Equal(1, source.ConnectCount);
    }

    private static AppConfig Config(bool enabled = true, bool machinePresent = true, bool machineEnabled = true) => new()
    {
        Enabled = enabled,
        Machines = machinePresent ? [Machine with { Enabled = machineEnabled }] : [],
    };

    private static RgbaImage Image(int width, int height, Rgba color)
    {
        var image = new RgbaImage(width, height);
        image.Fill(color);
        return image;
    }

    private sealed class ControlledScreenSource(string machineId, RgbaImage frame) : IScreenSource
    {
        private int _connectCount;
        private int _captureCount;
        private int _disposeCount;

        public string MachineId { get; } = machineId;
        public RgbaImage Frame { get; } = frame;
        public bool IsConnected { get; private set; }
        public ScreenSourceInfo? Info { get; private set; }
        public Task ConnectGate { get; init; } = Task.CompletedTask;
        public Task CaptureGate { get; init; } = Task.CompletedTask;
        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int CaptureCount => Volatile.Read(ref _captureCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _connectCount);
            await ConnectGate.WaitAsync(ct);
            IsConnected = true;
            return Info = new("vnc", "3.8", "None", "32 bpp", Frame.Width, Frame.Height, "test");
        }

        public Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct) =>
            CaptureFullAsync(ct);

        public async Task<RgbaImage> CaptureFullAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _captureCount);
            CaptureStarted.TrySetResult();
            await CaptureGate.WaitAsync(ct);
            return Frame;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
