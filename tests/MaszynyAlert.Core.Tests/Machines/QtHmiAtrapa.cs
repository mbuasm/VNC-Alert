using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

internal enum QtHmiBehavior
{
    /// <summary>Odpowiada na każde żądanie klatką (Raw, szary ekran).</summary>
    Serve,

    /// <summary>Po handshake'u i pierwszym FramebufferUpdateRequest zamyka połączenie bez klatki (proces HMI pada).</summary>
    CrashOnFirstRequest,
}

internal sealed class QtHmiAtrapa : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<int, QtHmiBehavior> _behavior;
    private readonly ConcurrentQueue<long> _connections = new();
    private readonly ConcurrentQueue<TcpClient> _tcps = new();
    private readonly ConcurrentQueue<Task> _clients = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Task _accept;
    private int _count;

    public QtHmiAtrapa(Func<int, QtHmiBehavior>? behavior = null)
    {
        _behavior = behavior ?? (_ => QtHmiBehavior.Serve);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public int Width { get; } = 1024;

    public int Height { get; } = 768;

    public int ConnectionCount => Volatile.Read(ref _count);

    /// <summary>Chwile przyjęcia kolejnych połączeń (ms od utworzenia atrapy).</summary>
    public long[] ConnectionTimesMs => _connections.ToArray();

    public long NowMs => _sw.ElapsedMilliseconds;

    /// <summary>Port przestaje nasłuchiwać — kolejne próby dostają odmowę.</summary>
    public void StopListening()
    {
        try { _listener.Stop(); }
        catch (Exception) { }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                int index = Interlocked.Increment(ref _count) - 1;
                _connections.Enqueue(_sw.ElapsedMilliseconds);
                _tcps.Enqueue(tcp);
                QtHmiBehavior behavior = _behavior(index);
                _clients.Enqueue(Task.Run(() => ServeAsync(tcp, behavior, _cts.Token)));
            }
        }
        catch (Exception)
        {
            // Listener zatrzymany (StopListening/Dispose).
        }
    }

    private async Task ServeAsync(TcpClient tcp, QtHmiBehavior behavior, CancellationToken ct)
    {
        try
        {
            using (tcp)
            {
                tcp.NoDelay = true;
                NetworkStream s = tcp.GetStream();
                await s.WriteAsync("RFB 003.003\n"u8.ToArray(), ct).ConfigureAwait(false);
                await s.ReadExactlyAsync(new byte[12], ct).ConfigureAwait(false);
                await s.WriteAsync(new byte[] { 0, 0, 0, 1 }, ct).ConfigureAwait(false); // 3.3: serwer narzuca None
                await s.ReadExactlyAsync(new byte[1], ct).ConfigureAwait(false); // ClientInit
                await s.WriteAsync(ServerInit(), ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    byte[] type = new byte[1];
                    await s.ReadExactlyAsync(type, ct).ConfigureAwait(false);
                    switch (type[0])
                    {
                        case 0:
                            await s.ReadExactlyAsync(new byte[19], ct).ConfigureAwait(false);
                            break;
                        case 2: // SetEncodings
                            byte[] header = new byte[3];
                            await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                            await s.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)) * 4], ct).ConfigureAwait(false);
                            break;
                        case 3: // FramebufferUpdateRequest
                            byte[] request = new byte[9];
                            await s.ReadExactlyAsync(request, ct).ConfigureAwait(false);
                            if (behavior == QtHmiBehavior.CrashOnFirstRequest) return; // zamknięcie bez klatki
                            await s.WriteAsync(Update(request), ct).ConfigureAwait(false);
                            break;
                        default:
                            return;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Klient się rozłączył albo atrapa jest zatrzymywana.
        }
    }

    private byte[] ServerInit()
    {
        byte[] name = Encoding.ASCII.GetBytes(QtEmbeddedVncServer.DesktopName);
        byte[] m = new byte[24 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(m, (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)Height);
        RfbPixelFormat.Bgra32LittleEndian.Write(m.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(20), (uint)name.Length);
        name.CopyTo(m, 24);
        return m;
    }

    private byte[] Update(byte[] request)
    {
        int x = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1));
        int y = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3));
        int w = Math.Max(0, Math.Min(BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(5)), Width - x));
        int h = Math.Max(0, Math.Min(BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(7)), Height - y));
        byte[] m = new byte[4 + 12 + (w * h * 4)];
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), 1); // FramebufferUpdate, 1 prostokąt
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(4), (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(10), (ushort)h);
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(12), 0); // Raw
        m.AsSpan(16).Fill(0x80);
        return m;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        StopListening();
        foreach (TcpClient tcp in _tcps)
        {
            try { tcp.Dispose(); }
            catch (Exception) { }
        }

        try { await _accept.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { }
        try { await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { }
        _cts.Dispose();
    }
}

internal static class QtHmiSupervisor
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public const string EarlyClose = "zamknął połączenie przed pierwszą klatką";

    internal sealed record Fixture(MonitorSupervisor Supervisor, AlertStateMachine Alerts, FakeClock Clock, RecordingLoggerFactory Loggers,
        ConcurrentQueue<AlertEvent> Events);

    public static AppConfig QtConfig(int port1, int port2, bool enabled1, bool enabled2)
    {
        AppConfig example = TestPaths.ExampleConfig;
        MachineConfig m1 = example.Machines.Single(m => m.Id == "amm520-1") with { Host = "127.0.0.1", Port = port1, Enabled = enabled1 };
        MachineConfig m2 = example.Machines.Single(m => m.Id == "amm520-2") with { Host = "127.0.0.1", Port = port2, Enabled = enabled2 };
        return example with
        {
            Machines = [m1, m2],
            // reconnectBackoffMs [100] → cykl podtrzymania wstrzymania = max(1000, 100) ms czasu rzeczywistego.
            Timing = example.Timing with { ReconnectBackoffMs = [100], ConnectTimeoutMs = 5000, ReadTimeoutMs = 5000 },
        };
    }

    /// <summary>Zegar monitorów ręczny: bezpiecznik (5 min) nie mija sam w czasie testu; pętle śpią naprawdę (Task.Delay).</summary>
    public static Fixture Build(AppConfig cfg)
    {
        var clock = new FakeClock();
        var alerts = new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new ConcurrentQueue<AlertEvent>();
        alerts.Changed += events.Enqueue;
        var loggers = new RecordingLoggerFactory();
        var supervisor = new MonitorSupervisor(cfg, _ => null, new NullOcrEngine(), new MessageMapper(TestPaths.ExampleRules), alerts, clock,
            new NullCaptureStore(), null, loggers);
        return new Fixture(supervisor, alerts, clock, loggers, events);
    }

    public static async Task ApplyLikePanelAsync(Fixture f, AppConfig next, params string[] disabledNow)
    {
        await f.Supervisor.ReconfigureAsync(next, new MessageMapper(TestPaths.ExampleRules), new NullOcrEngine(), CancellationToken.None);
        foreach (string id in disabledNow) f.Alerts.OnMachineDisabled(id);
        await f.Supervisor.StartAsync(CancellationToken.None);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Patience) Assert.Fail($"Nie doczekano się: {what} (po {sw.Elapsed.TotalSeconds:0.0} s).");
            await Task.Delay(20);
        }
    }
}
