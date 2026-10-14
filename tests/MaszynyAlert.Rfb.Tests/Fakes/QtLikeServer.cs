using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace MaszynyAlert.Rfb.Tests.Fakes;

internal sealed class QtLikeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _serverInit;
    private readonly Func<QtLikeServer, NetworkStream, int, CancellationToken, Task>? _onRequest;
    private readonly List<byte> _after = [];
    private readonly List<byte> _types = [];
    private int[] _encodings = [];
    private Task? _loop;

    public QtLikeServer(byte[] serverInit, Func<QtLikeServer, NetworkStream, int, CancellationToken, Task>? onRequest = null)
    {
        _serverInit = serverInit;
        _onRequest = onRequest;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public bool CheckPipelining { get; init; }

    public int BytesBeforeAuth { get; private set; } = -1;

    public int BytesBeforeServerInit { get; private set; } = -1;

    public TaskCompletionSource EncodingsReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lista z ostatniego <c>SetEncodings</c> klienta.</summary>
    public IReadOnlyList<int> Encodings => Volatile.Read(ref _encodings);

    public byte[] ClientBytesAfterInit
    {
        get { lock (_after) return [.. _after]; }
    }

    public byte[] MessageTypes
    {
        get { lock (_after) return [.. _types]; }
    }

    public async Task<byte[]> WaitForTypesAsync(int count)
    {
        for (int i = 0; i < 250 && MessageTypes.Length < count; i++) await Task.Delay(20);
        return ClientBytesAfterInit;
    }

    public void Start() => _loop = Task.Run(() => ServeAsync(_cts.Token));

    private async Task ServeAsync(CancellationToken ct)
    {
        try
        {
            using TcpClient tcp = await _listener.AcceptTcpClientAsync(ct);
            tcp.NoDelay = true;
            NetworkStream s = tcp.GetStream();
            await s.WriteAsync("RFB 003.003\n"u8.ToArray(), ct);
            await s.ReadExactlyAsync(new byte[12], ct);
            if (CheckPipelining)
            {
                await Task.Delay(150, ct);
                BytesBeforeAuth = tcp.Available;
            }

            await s.WriteAsync(new byte[] { 0, 0, 0, 1 }, ct);
            await s.ReadExactlyAsync(new byte[1], ct);
            if (CheckPipelining)
            {
                await Task.Delay(150, ct);
                BytesBeforeServerInit = tcp.Available;
            }

            await s.WriteAsync(_serverInit, ct);

            int request = 0;
            while (!ct.IsCancellationRequested)
            {
                byte[] type = new byte[1];
                await s.ReadExactlyAsync(type, ct);
                byte[] body = type[0] switch
                {
                    0 => new byte[19],
                    2 => new byte[3],
                    3 => new byte[9],
                    _ => [],
                };
                await s.ReadExactlyAsync(body, ct);
                byte[] list = [];
                if (type[0] == 2)
                {
                    list = new byte[BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1)) * 4];
                    await s.ReadExactlyAsync(list, ct);
                    int[] encodings = new int[list.Length / 4];
                    for (int i = 0; i < encodings.Length; i++) encodings[i] = BinaryPrimitives.ReadInt32BigEndian(list.AsSpan(i * 4));
                    Volatile.Write(ref _encodings, encodings);
                }

                lock (_after)
                {
                    _types.Add(type[0]);
                    _after.AddRange([.. type, .. body, .. list]);
                }

                if (type[0] == 2) EncodingsReceived.TrySetResult();
                if (type[0] == 3 && _onRequest is not null) await _onRequest(this, s, request++, ct);
                if (type[0] is not (0 or 2 or 3)) return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
        }

        _cts.Dispose();
    }
}
