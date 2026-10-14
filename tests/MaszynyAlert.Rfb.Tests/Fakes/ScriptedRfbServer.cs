using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Rfb.Tests.Fakes;

internal sealed class ScriptedRfbServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte> _clientBytes = [];
    private Task? _loop;
    private int _requestNumber;
    private RfbPixelFormat? _clientPixelFormat;

    public ScriptedRfbServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public int Width { get; init; } = 64;

    public int Height { get; init; } = 32;

    /// <summary>Format piksela zgłaszany w <c>ServerInit</c> (domyślnie BGRA32 jak dotąd).</summary>
    public RfbPixelFormat PixelFormat { get; init; } = RfbPixelFormat.Bgra32LittleEndian;

    public string DesktopName { get; init; } = "skrypt";

    public bool Rfb33 { get; init; }

    /// <summary>Typy wiadomości odebranych od klienta po <c>ServerInit</c>, w kolejności (do asercji „czego klient nie wysłał”).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<byte> ClientMessageTypes { get; } = new();

    /// <summary>Lista kodowań z ostatniego <c>SetEncodings</c> klienta (pusta, dopóki go nie wysłał).</summary>
    public IReadOnlyList<int> ReceivedEncodings => Volatile.Read(ref _encodings);

    private int[] _encodings = [];

    /// <summary>Formaty z kolejnych <c>SetPixelFormat</c> klienta (w kolejności).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<RfbPixelFormat> ReceivedPixelFormats { get; } = new();

    public RfbPixelFormat? ClientPixelFormat => Volatile.Read(ref _clientPixelFormat);

    /// <summary>Wszystkie bajty wysłane przez klienta po <c>ServerInit</c> (pełne wiadomości), do porównań bajt w bajt.</summary>
    public byte[] ClientBytesAfterInit
    {
        get
        {
            lock (_clientBytes) return [.. _clientBytes];
        }
    }

    public Func<NetworkStream, int, CancellationToken, Task>? OnRequest { get; init; }

    public void Start() => _loop = Task.Run(() => ServeAsync(_cts.Token));

    private async Task ServeAsync(CancellationToken ct)
    {
        try
        {
            using TcpClient tcp = await _listener.AcceptTcpClientAsync(ct);
            tcp.NoDelay = true;
            NetworkStream s = tcp.GetStream();
            if (Rfb33)
            {
                await s.WriteAsync("RFB 003.003\n"u8.ToArray(), ct);
                await s.ReadExactlyAsync(new byte[12], ct);
                await s.WriteAsync(new byte[] { 0, 0, 0, 1 }, ct); // 3.3: serwer narzuca None, bez SecurityResult
            }
            else
            {
                await s.WriteAsync("RFB 003.008\n"u8.ToArray(), ct);
                await s.ReadExactlyAsync(new byte[12], ct);
                await s.WriteAsync(new byte[] { 1, 1 }, ct); // jeden typ zabezpieczenia: None
                await s.ReadExactlyAsync(new byte[1], ct);
                await s.WriteAsync(new byte[4], ct); // SecurityResult OK
            }

            await s.ReadExactlyAsync(new byte[1], ct); // ClientInit
            await s.WriteAsync(ServerInit(Width, Height, PixelFormat, DesktopName), ct);

            while (!ct.IsCancellationRequested)
            {
                byte[] type = new byte[1];
                await s.ReadExactlyAsync(type, ct);
                ClientMessageTypes.Enqueue(type[0]);
                if (type[0] == 0)
                {
                    byte[] body = new byte[19];
                    await s.ReadExactlyAsync(body, ct);
                    Record(type, body);
                    RfbPixelFormat format = RfbPixelFormat.Parse(body.AsSpan(3, 16));
                    ReceivedPixelFormats.Enqueue(format);
                    Volatile.Write(ref _clientPixelFormat, format);
                }
                else if (type[0] == 2)
                {
                    byte[] header = new byte[3];
                    await s.ReadExactlyAsync(header, ct);
                    byte[] list = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)) * 4];
                    await s.ReadExactlyAsync(list, ct);
                    Record(type, header, list);
                    int[] encodings = new int[list.Length / 4];
                    for (int i = 0; i < encodings.Length; i++) encodings[i] = BinaryPrimitives.ReadInt32BigEndian(list.AsSpan(i * 4));
                    Volatile.Write(ref _encodings, encodings);
                }
                else if (type[0] == 3)
                {
                    byte[] body = new byte[9];
                    await s.ReadExactlyAsync(body, ct);
                    Record(type, body);
                    if (OnRequest is not null) await OnRequest(s, _requestNumber++, ct);
                }
                else
                {
                    Record(type);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // Koniec połączenia albo zatrzymanie serwera.
        }
    }

    private void Record(params byte[][] parts)
    {
        lock (_clientBytes)
        {
            foreach (byte[] part in parts) _clientBytes.AddRange(part);
        }
    }

    public static byte[] ServerInit(int width, int height, RfbPixelFormat format, string name)
    {
        byte[] n = Encoding.UTF8.GetBytes(name);
        byte[] m = new byte[24 + n.Length];
        BinaryPrimitives.WriteUInt16BigEndian(m, (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)height);
        format.Write(m.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(20), (uint)n.Length);
        n.CopyTo(m, 24);
        return m;
    }

    public static byte[] UpdateHeader(int rectangles)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)rectangles);
        return b;
    }

    public static byte[] RectangleHeader(int x, int y, int width, int height, int encoding)
    {
        byte[] b = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)height);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), encoding);
        return b;
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
