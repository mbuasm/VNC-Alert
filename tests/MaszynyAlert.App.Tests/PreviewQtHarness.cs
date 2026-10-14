using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.App.Tests;

internal sealed class PreviewManualClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;
    public DateTimeOffset Now => UtcNow;
    public long MonotonicMs { get; private set; } = 1_000_000;
    public void Advance(TimeSpan delta) { UtcNow += delta; MonotonicMs += (long)delta.TotalMilliseconds; }
}

internal sealed class FakeQtVncServer : IAsyncDisposable
{
    public const int Width = 1024;
    public const int Height = 768;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _handshake;
    private readonly bool _serveFrames;
    private readonly Task _loop;
    private readonly ConcurrentQueue<EndPoint?> _acceptedFrom = new();
    private int _accepted;
    private int _open;

    public FakeQtVncServer(bool handshake, bool serveFrames)
    {
        _handshake = handshake;
        _serveFrames = serveFrames;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    public int Accepted => Volatile.Read(ref _accepted);

    public int Open => Volatile.Read(ref _open);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (SocketException) when (!ct.IsCancellationRequested)
                {
                    continue; // połączenie zerwane, zanim zostało przyjęte
                }

                _acceptedFrom.Enqueue(tcp.Client.RemoteEndPoint);
                Interlocked.Increment(ref _accepted);
                Interlocked.Increment(ref _open); // przed Task.Run: test nie zobaczy przyjętego połączenia jako zamkniętego
                _ = Task.Run(() => ServeAsync(tcp, ct));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Koniec testu.
        }
    }

    /// <summary>Czeka, aż atrapa przyjmie połączenie-sondę, a więc i wszystkie połączenia zestawione przed nią.</summary>
    public async Task SettleAsync()
    {
        using var probe = new TcpClient(AddressFamily.InterNetwork);
        await probe.ConnectAsync(IPAddress.Loopback, Port);
        EndPoint local = probe.Client.LocalEndPoint!;
        await PreviewQtOrchestratorRig.WaitUntil(() => _acceptedFrom.Contains(local), "przyjęcie sondy przez atrapę HMI");
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            await ServeCoreAsync(tcp, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _open);
        }
    }

    private async Task ServeCoreAsync(TcpClient tcp, CancellationToken ct)
    {
        using (tcp)
        {
            if (!_handshake) return;
            try
            {
                tcp.NoDelay = true;
                NetworkStream s = tcp.GetStream();
                await s.WriteAsync("RFB 003.003\n"u8.ToArray(), ct);
                await s.ReadExactlyAsync(new byte[12], ct);
                await s.WriteAsync(new byte[] { 0, 0, 0, 1 }, ct); // 3.3: serwer narzuca None
                await s.ReadExactlyAsync(new byte[1], ct); // ClientInit
                await s.WriteAsync(ServerInit(), ct);
                while (!ct.IsCancellationRequested)
                {
                    byte[] type = new byte[1];
                    await s.ReadExactlyAsync(type, ct);
                    switch (type[0])
                    {
                        case 0:
                            await s.ReadExactlyAsync(new byte[19], ct); // SetPixelFormat
                            break;
                        case 2:
                            byte[] header = new byte[3];
                            await s.ReadExactlyAsync(header, ct);
                            await s.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)) * 4], ct);
                            break;
                        case 3:
                            byte[] request = new byte[9];
                            await s.ReadExactlyAsync(request, ct);
                            if (!_serveFrames) return; // zamknięcie przed pierwszą klatką
                            if (request[0] == 0) await s.WriteAsync(FullFrame(), ct);
                            break;
                        default:
                            return;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Klient zamknął połączenie albo koniec testu.
            }
        }
    }

    private static byte[] ServerInit()
    {
        byte[] name = Encoding.ASCII.GetBytes(QtEmbeddedVncServer.DesktopName);
        byte[] m = new byte[24 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(m, Width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), Height);
        m[4] = 16; // bpp
        m[5] = 16; // głębia
        m[6] = 0; // little-endian
        m[7] = 1; // true colour
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), 31);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(10), 63);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(12), 31);
        m[14] = 11;
        m[15] = 5;
        m[16] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(20), (uint)name.Length);
        name.CopyTo(m, 24);
        return m;
    }

    private static byte[] FullFrame()
    {
        byte[] m = new byte[4 + 12 + Width * Height * 2];
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), 1); // jeden prostokąt
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), Width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(10), Height); // kodowanie 0 = Raw
        m.AsSpan(16).Fill(0xFF);
        return m;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception) { }
        _cts.Dispose();
    }
}

internal sealed class PreviewQtOrchestratorRig : IAsyncDisposable
{
    public const string MachineId = "amm520-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "podglad-qt-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _disposablesSync = [];

    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }

    public PreviewManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

    public string TestScreensDir => Path.Combine(_dir, "test-ekran");

    public AppOrchestrator Build(int port, string profile, Func<AppConfig, (ClusterRuntime Runtime, AppConfig Config)>? group = null)
    {
        string root = RepositoryRoot();
        var example = ConfigLoader.LoadAppConfig(Path.Combine(root, "config", "config.example.json"));
        var rules = ConfigLoader.LoadRules(Path.Combine(root, "config", "rules.example.json"));
        var sample = example.Machines.First(m => m.Enabled);
        var cfg = example with
        {
            Machines =
            [
                sample with
                {
                    Id = MachineId, Name = "520 (1)", Host = "127.0.0.1", Port = port, Profile = profile, Enabled = true,
                    RequiresPassword = false, Mode = ConnectionMode.Persistent, Username = null,
                },
            ],
            Paths = example.Paths with
            {
                HistoryFile = Path.Combine(_dir, "data", "history.csv"), LogsDir = Path.Combine(_dir, "logs"),
                CapturesDir = Path.Combine(_dir, "captures"), TestScreensDir = TestScreensDir,
            },
        };
        Directory.CreateDirectory(TestScreensDir);

        var acks = new AckMemory(Clock, TimeSpan.FromHours(2));
        ClusterRuntime runtime;
        if (group is not null) (runtime, cfg) = group(cfg);
        else runtime = new ClusterRuntime(new ClusterConfig(), null, Clock, acks, "TEST-PC", "test");

        var secrets = new InMemorySecretStore();
        var alerts = new AlertStateMachine(Clock, cfg.Timing, acks);
        var supervisor = new MonitorSupervisor(cfg, m => secrets.Get(SecretKeys.VncPassword(m.Id)), new NullOcrEngine(),
            new MessageMapper(rules), alerts, Clock, new FileCaptureStore(Path.Combine(_dir, "captures")), TestScreensDir);
        var power = new PowerModeWatcher();
        var orchestrator = new AppOrchestrator(runtime, supervisor, alerts, new HistoryWriter(cfg.Paths.HistoryFile, Clock), power, Clock,
            new StartupConfig(cfg, cfg), new SharedConfigSync(new SharedConfigStore(Path.Combine(_dir, "shared-config", "shared-config.json")), "TEST-PC"),
            new NullOcrEngine(), NullLogger<AppOrchestrator>.Instance,
            new AlarmHelpService(rules, Path.Combine(_dir, "alarm-instructions.json"), false, clock: Clock), secrets);
        _disposables.Add(supervisor); _disposables.Add(runtime); _disposablesSync.Add(power);
        return orchestrator;
    }

    public static Task Tick(AppOrchestrator orchestrator) =>
        (Task)typeof(AppOrchestrator).GetMethod("TickOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(orchestrator, [CancellationToken.None])!;

    /// <summary>Tiki do startu monitorów; po starcie zegar stoi (okno „przed pierwszą klatką” liczy się na tym zegarze).</summary>
    public async Task StartObserving(AppOrchestrator orchestrator)
    {
        for (int i = 0; i < 30; i++)
        {
            await Tick(orchestrator);
            if (orchestrator.Supervisor.IsRunning) return;
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Fail("Monitory nie wystartowały — test nie sprawdzi obserwatora.");
    }

    public static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int waited = 0; waited < 15_000; waited += 50)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail("Nie doczekano się: " + what);
    }

    public static MachineStatusSnapshot Snapshot(AppOrchestrator orchestrator) =>
        orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == MachineId);

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _disposables) { try { await item.DisposeAsync(); } catch (Exception) { } }
        foreach (var item in _disposablesSync) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
