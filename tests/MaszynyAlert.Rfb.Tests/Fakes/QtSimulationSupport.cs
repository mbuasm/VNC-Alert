using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Rfb.Tests.Fakes;

internal sealed class VirtualClock : IClock
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);

    private readonly object _lock = new();
    private readonly List<Sleeper> _sleepers = [];
    private long _now;
    private long _seq;
    private int _actors;
    private TaskCompletionSource _quiescent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DateTimeOffset UtcNow => Epoch.AddMilliseconds(Interlocked.Read(ref _now));

    public DateTimeOffset Now => UtcNow;

    public long MonotonicMs => Interlocked.Read(ref _now);

    /// <summary>Rejestruje aktora (przed jego startem); <see cref="Run"/> robi to sam.</summary>
    public void AddActor()
    {
        lock (_lock) _actors++;
    }

    /// <summary>Uruchamia aktora i wyrejestrowuje go, gdy skończy (także błędem) — inaczej zegar czekałby na niego w nieskończoność.</summary>
    public Task Run(Func<Task> actor)
    {
        AddActor();
        return Task.Run(async () =>
        {
            try { await actor(); }
            finally
            {
                lock (_lock)
                {
                    _actors--;
                    CheckQuiescent();
                }
            }
        });
    }

    /// <summary>Czekanie aktora w czasie wirtualnym (hak <c>delay</c> monitora).</summary>
    public Task SleepAsync(TimeSpan delay, CancellationToken ct)
    {
        var sleeper = new Sleeper();
        lock (_lock)
        {
            sleeper.WakeAt = _now + Math.Max(0, (long)delay.TotalMilliseconds);
            sleeper.Seq = ++_seq;
            _sleepers.Add(sleeper);
            CheckQuiescent();
        }

        if (ct.CanBeCanceled)
        {
            sleeper.Registration = ct.Register(() =>
            {
                lock (_lock)
                {
                    _sleepers.Remove(sleeper);
                }

                sleeper.Tcs.TrySetCanceled(ct);
            });
        }

        return sleeper.Tcs.Task;
    }

    /// <summary>
    /// Czeka (najwyżej <paramref name="realTimeout"/> czasu rzeczywistego), aż wszyscy aktorzy śpią, i budzi tych z najbliższą
    /// pobudką. Zwraca false, gdy następna pobudka wypadłaby po <paramref name="endMs"/> (koniec symulacji).
    /// </summary>
    public async Task<bool> StepAsync(long endMs, TimeSpan realTimeout)
    {
        Task quiescent;
        lock (_lock) quiescent = _quiescent.Task;
        await quiescent.WaitAsync(realTimeout);
        List<Sleeper> due;
        lock (_lock)
        {
            _quiescent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_sleepers.Count == 0) return false;
            long next = _sleepers.Min(s => s.WakeAt);
            if (next > endMs) return false;
            Interlocked.Exchange(ref _now, Math.Max(_now, next));
            due = _sleepers.Where(s => s.WakeAt <= _now).OrderBy(s => s.Seq).ToList();
            foreach (Sleeper s in due) _sleepers.Remove(s);
            CheckQuiescent();
        }

        foreach (Sleeper s in due)
        {
            s.Registration.Dispose();
            s.Tcs.TrySetResult();
        }

        return true;
    }

    private void CheckQuiescent()
    {
        if (_actors > 0 && _sleepers.Count >= _actors) _quiescent.TrySetResult();
    }

    private sealed class Sleeper
    {
        public long WakeAt;
        public long Seq;
        public CancellationTokenRegistration Registration;
        public readonly TaskCompletionSource Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Rejestrator logów w pamięci (poziom, czas wirtualny, treść) — do asercji na komunikatach bezpieczników.</summary>
internal sealed class ListLogger(IClock clock, string name) : ILogger
{
    private readonly List<(long AtMs, LogLevel Level, string Message)> _entries = [];

    public string Name { get; } = name;

    public IReadOnlyList<(long AtMs, LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        lock (_entries) _entries.Add((clock.MonotonicMs, logLevel, formatter(state, exception)));
    }
}

internal sealed class DesktopNameRewritingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _targetPort;
    private readonly string _name;
    private readonly Task _accept;

    public DesktopNameRewritingProxy(int targetPort, string replacementName)
    {
        _targetPort = targetPort;
        _name = replacementName;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public int Connections => Volatile.Read(ref _connections);

    private int _connections;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => PipeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task PipeAsync(TcpClient client)
    {
        using var server = new TcpClient { NoDelay = true };
        client.NoDelay = true;
        using (client)
        {
            try
            {
                await server.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return; // serwer odmawia — klient dostanie zamknięcie
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            NetworkStream c = client.GetStream(), s = server.GetStream();
            Task up = CopyAsync(c, s, linked.Token);
            Task down = RewriteThenCopyAsync(s, c, linked.Token);
            await Task.WhenAny(up, down);
            linked.Cancel();
            try { client.Client.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            try { server.Client.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0) await to.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task RewriteThenCopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        try
        {
            // Krok po kroku — klient odpowiada dopiero po każdej części: wersja (12 B), typ zabezpieczeń 3.3 (4 B), ServerInit.
            byte[] version = new byte[12];
            await from.ReadExactlyAsync(version, ct);
            await to.WriteAsync(version, ct);
            byte[] security = new byte[4];
            await from.ReadExactlyAsync(security, ct);
            await to.WriteAsync(security, ct);
            byte[] init = new byte[24];
            await from.ReadExactlyAsync(init, ct);
            int nameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
            await from.ReadExactlyAsync(new byte[nameLength], ct);
            byte[] name = Encoding.UTF8.GetBytes(_name);
            BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)name.Length);
            await to.WriteAsync(init, ct);
            await to.WriteAsync(name, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException)
        {
            return;
        }

        await CopyAsync(from, to, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _accept.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
        _cts.Dispose();
    }
}
