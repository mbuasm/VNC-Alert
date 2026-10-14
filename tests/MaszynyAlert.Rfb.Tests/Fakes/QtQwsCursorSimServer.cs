using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Rfb.Tests.Fakes;

internal readonly record struct SimRequest(
    int Number, bool Incremental, int X, int Y, int Width, int Height, bool WantUpdateAtReceipt, int ResponsesBefore);

internal sealed class QtQwsCursorSimServer : IAsyncDisposable
{
    public const int TileSize = 16;

    /// <summary>Kolor pikseli kursora — na ekranie atrapy nigdy nie występuje, więc narysowany kursor byłby widoczny.</summary>
    public static readonly Rgba CursorColour = new(0, 255, 0);

    /// <summary>Bajt maski kursora (zawartość bez znaczenia dla klienta; ≠ 0, żeby rozjechanie nie czytało zer).</summary>
    private const byte MaskByte = 0xC3;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<SimRequest> _requests = [];
    private readonly List<byte> _messageTypes = [];
    private readonly List<string> _problems = [];
    private ChunkedWriter? _writer;
    private NetworkStream? _stream;
    private Task? _loop;

    private RgbaImage _screen = new(1, 1);
    private RgbaImage _reference = new(1, 1);
    private bool[] _dirty = [];
    private int _mapWidth;
    private int _mapHeight;
    private int _numDirty;
    private bool _dirtyCursor;
    private bool _wantUpdate;
    private bool _hasClientCursor;
    private bool _discarded;
    private bool _hextile;
    private RfbPixelFormat? _clientFormat;
    private int[] _encodings = [];
    private (int W, int H, int HotX, int HotY) _cursor;
    private int _cursorResponses;
    private int _frameResponses;
    private long _cursorBytes;
    private RgbaImage? _lastFrameScreen;

    public QtQwsCursorSimServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public int Width { get; init; } = 48;

    public int Height { get; init; } = 32;

    public RfbPixelFormat ServerInitFormat { get; init; } = RfbPixelFormat.Rgb565LittleEndian;

    public string DesktopName { get; init; } = QtEmbeddedVncServer.DesktopName;

    /// <summary>Format zastany (pole <c>pixelFormat</c> po poprzednim kliencie); <see langword="null"/> = niezainicjowany (0 bpp).</summary>
    public RfbPixelFormat? LeftoverClientFormat { get; init; } = RfbPixelFormat.Rgb565LittleEndian;

    public bool ClientCursorSupported { get; init; } = true;

    public bool HextileSupported { get; init; } = true;

    public (int W, int H, int HotX, int HotY) InitialCursor { get; init; } = (16, 16, 1, 1);

    public Rgba InitialColour { get; init; } = new(0, 0, 255);

    public WriteMode Writes { get; init; } = WriteMode.Whole;

    public int WriteSeed { get; init; } = 1;

    public int MaxChunk { get; init; } = 97;

    /// <summary>Żądania o numerze &lt; N zastają „brudny” kursor (zmiana kształtu tuż przed żądaniem) — N odpowiedzi „sam kursor”.</summary>
    public int DirtyCursorBeforeFirstRequests { get; init; }

    /// <summary>Patologia: każde żądanie zastaje brudny kursor (kształt zmienia się częściej, niż przychodzą żądania).</summary>
    public bool DirtyCursorBeforeEveryRequest { get; init; }

    /// <summary>Żądania o numerach wskazanych przez predykat zastają brudny kursor (zmiana kształtu tuż przed nimi).</summary>
    public Func<int, bool>? DirtyCursorBeforeRequest { get; init; }

    public int CursorResponses => Volatile.Read(ref _cursorResponses);

    public int FrameResponses => Volatile.Read(ref _frameResponses);

    public long CursorBytes => Interlocked.Read(ref _cursorBytes);

    public IReadOnlyList<SimRequest> Requests
    {
        get
        {
            lock (_requests) return [.. _requests];
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_requests) return _requests.Count;
        }
    }

    public int Overlaps
    {
        get
        {
            lock (_requests) return _requests.Count(r => r.WantUpdateAtReceipt);
        }
    }

    public IReadOnlyList<byte> ClientMessageTypes
    {
        get
        {
            lock (_messageTypes) return [.. _messageTypes];
        }
    }

    /// <summary>Wiadomości klienta spoza 0/2/3 i błędy samej atrapy (puste = w porządku).</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_problems) return [.. _problems];
        }
    }

    public IReadOnlyList<int> ReceivedEncodings => Volatile.Read(ref _encodings);

    public RfbPixelFormat? ClientPixelFormat => Volatile.Read(ref _clientFormat);

    public bool Discarded => Volatile.Read(ref _discarded);

    public bool HasClientCursor => Volatile.Read(ref _hasClientCursor);

    public bool WantUpdate => Volatile.Read(ref _wantUpdate);

    /// <summary>Treść ekranu w chwili zapisu ostatniej klatki (po jej odczycie bufor klienta musi być dokładnie taki).</summary>
    public RgbaImage? LastFrameScreen => Volatile.Read(ref _lastFrameScreen);

    public long ChunkWrites => _writer?.Writes ?? 0;

    public long ChunkDelays => _writer?.Delays ?? 0;

    public void Start()
    {
        _mapWidth = (Width + TileSize - 1) / TileSize;
        _mapHeight = (Height + TileSize - 1) / TileSize;
        _screen = new RgbaImage(Width, Height);
        _screen.Fill(InitialColour);
        _reference = _screen.Clone();
        _dirty = new bool[_mapWidth * _mapHeight];
        _cursor = InitialCursor;
        _writer = new ChunkedWriter(Writes, WriteSeed, MaxChunk);
        _loop = Task.Run(() => ServeAsync(_cts.Token));
    }

    /// <summary>Kopia bieżącej treści ekranu.</summary>
    public async Task<RgbaImage> ScreenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return _screen.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ChangeScreenAsync(RectInt rect, Rgba colour)
    {
        await _gate.WaitAsync();
        try
        {
            RectInt r = rect.Clamp(Width, Height);
            for (int y = r.Y; y < r.Bottom; y++)
            {
                for (int x = r.X; x < r.Right; x++)
                {
                    _screen.SetPixel(x, y, colour);
                }
            }

            MarkChangedTiles(r);
            if (_wantUpdate)
            {
                await CheckUpdateLockedAsync(_cts.Token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ChangeCursorAsync(int width, int height, int hotX, int hotY)
    {
        await _gate.WaitAsync();
        try
        {
            _cursor = (width, height, hotX, hotY);
            if (_hasClientCursor && !_discarded)
            {
                _dirtyCursor = true;
                if (_wantUpdate)
                {
                    await CheckUpdateLockedAsync(_cts.Token);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Czeka, aż warunek będzie spełniony (albo minie <paramref name="timeout"/>); zwraca wynik warunku.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= end)
            {
                return condition();
            }

            await Task.Delay(10);
        }

        return true;
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        try
        {
            using TcpClient tcp = await _listener.AcceptTcpClientAsync(ct);
            tcp.NoDelay = true;
            NetworkStream s = tcp.GetStream();
            _stream = s;

            await _gate.WaitAsync(ct);
            try
            {
                Array.Fill(_dirty, true); // dirtyMap()->reset()
                _numDirty = _dirty.Length;
                _wantUpdate = false;
            }
            finally
            {
                _gate.Release();
            }

            await s.WriteAsync("RFB 003.003\n"u8.ToArray(), ct);
            await s.ReadExactlyAsync(new byte[12], ct);
            await s.WriteAsync(new byte[] { 0, 0, 0, 1 }, ct); // None, bez SecurityResult (3.3)
            await s.ReadExactlyAsync(new byte[1], ct); // ClientInit
            await s.WriteAsync(ScriptedRfbServer.ServerInit(Width, Height, ServerInitFormat, DesktopName), ct);

            while (!ct.IsCancellationRequested)
            {
                byte[] type = new byte[1];
                await s.ReadExactlyAsync(type, ct);
                lock (_messageTypes) _messageTypes.Add(type[0]);
                switch (type[0])
                {
                    case 0:
                        {
                            byte[] body = new byte[19];
                            await s.ReadExactlyAsync(body, ct);
                            await OnSetPixelFormatAsync(RfbPixelFormat.Parse(body.AsSpan(3, 16)), ct);
                            break;
                        }

                    case 2:
                        {
                            byte[] header = new byte[3];
                            await s.ReadExactlyAsync(header, ct);
                            byte[] list = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)) * 4];
                            await s.ReadExactlyAsync(list, ct);
                            int[] encodings = new int[list.Length / 4];
                            for (int i = 0; i < encodings.Length; i++)
                            {
                                encodings[i] = BinaryPrimitives.ReadInt32BigEndian(list.AsSpan(i * 4));
                            }

                            await OnSetEncodingsAsync(encodings, ct);
                            break;
                        }

                    case 3:
                        {
                            byte[] body = new byte[9];
                            await s.ReadExactlyAsync(body, ct);
                            await OnUpdateRequestAsync(body, ct);
                            break;
                        }

                    default:
                        lock (_problems) _problems.Add($"klient wysłał wiadomość typu {type[0]}");
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException
                                       or EndOfStreamException)
        {
            // Koniec połączenia albo zatrzymanie atrapy.
        }
        catch (Exception ex)
        {
            lock (_problems) _problems.Add("błąd atrapy: " + ex);
        }
    }

    private async Task OnSetPixelFormatAsync(RfbPixelFormat format, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_discarded)
            {
                return;
            }

            Volatile.Write(ref _clientFormat, format);
            if (!format.TrueColour)
            {
                // "Can only handle true color clients" → discardClient(): stan Unconnected, kursor usunięty, gniazdo otwarte.
                _discarded = true;
                _hasClientCursor = false;
                _wantUpdate = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnSetEncodingsAsync(int[] encodings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_discarded)
            {
                return;
            }

            Volatile.Write(ref _encodings, encodings);
            _hextile = HextileSupported && encodings.Contains(RfbEncodings.Hextile);
            if (encodings.Contains(-239) && ClientCursorSupported)
            {
                _hasClientCursor = true;
                _dirtyCursor = true; // konstruktor → set() → setDirtyCursor(); wantUpdate == false, więc nic nie wysyła
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnUpdateRequestAsync(byte[] body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_discarded)
            {
                return;
            }

            bool incremental = body[0] != 0;
            int x = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1));
            int y = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3));
            int w = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(5));
            int h = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(7));
            int number;
            lock (_requests)
            {
                number = _requests.Count;
                _requests.Add(new SimRequest(number, incremental, x, y, w, h, _wantUpdate, _cursorResponses + _frameResponses));
            }

            if (!incremental)
            {
                ForceDirty(x, y, w, h);
            }

            _wantUpdate = true;
            if ((DirtyCursorBeforeEveryRequest || number < DirtyCursorBeforeFirstRequests || (DirtyCursorBeforeRequest?.Invoke(number) ?? false))
                && _hasClientCursor)
            {
                _dirtyCursor = true;
            }

            await CheckUpdateLockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckUpdateLockedAsync(CancellationToken ct)
    {
        if (!_wantUpdate)
        {
            return;
        }

        // Liczniki PRZED zapisem: klient może skończyć czytać odpowiedź, zanim zapis (z opóźnieniami) wróci do atrapy.
        if (_dirtyCursor)
        {
            _dirtyCursor = false;
            _wantUpdate = false;
            Interlocked.Increment(ref _cursorResponses);
            await WriteCursorLockedAsync(ct);
            return;
        }

        if (_numDirty > 0)
        {
            _wantUpdate = false;
            Interlocked.Increment(ref _frameResponses);
            await WriteFrameLockedAsync(ct);
        }
    }

    private RfbPixelFormat? EncodingFormat => _clientFormat ?? LeftoverClientFormat;

    private PixelEncoder? ClientEncoder() =>
        EncodingFormat is { } f && f.BitsPerPixel / 8 is 1 or 2 or 4 ? new PixelEncoder(f, null) : null;

    private async Task WriteCursorLockedAsync(CancellationToken ct)
    {
        (int w, int h, int hotX, int hotY) = _cursor;
        PixelEncoder? encoder = ClientEncoder();
        byte[] pixel = [];
        if (encoder is not null)
        {
            pixel = new byte[encoder.BytesPerPixel];
            encoder.Write(encoder.ToRaw(CursorColour), pixel);
        }

        bool hasImage = w > 0 && h > 0; // cursor.isNull() → sam nagłówek
        int maskRow = (w + 7) / 8;
        long length = 16 + (hasImage ? ((long)w * h * pixel.Length) + ((long)maskRow * h) : 0);
        byte[] message = new byte[length];
        message[3] = 1; // htons(0) = typ 0 + wypełnienie, htons(1) = jeden prostokąt
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)hotX);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)hotY);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), (ushort)h);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), -239);
        if (hasImage)
        {
            int o = 16;
            for (long i = 0; i < (long)w * h; i++)
            {
                pixel.CopyTo(message, o);
                o += pixel.Length;
            }

            message.AsSpan(o).Fill(MaskByte);
        }

        Interlocked.Add(ref _cursorBytes, length);
        await _writer!.WriteAsync(_stream!, message, ct);
    }

    /// <summary><c>QRfbHextileEncoder::write</c> (1784–1896) / <c>QRfbRawEncoder::write</c> (1898–2000): wszystkie brudne kafle ekranu.</summary>
    private async Task WriteFrameLockedAsync(CancellationToken ct)
    {
        Volatile.Write(ref _lastFrameScreen, _screen.Clone());
        PixelEncoder? encoder = ClientEncoder();
        using var output = new MemoryStream();
        output.Write([0, 0]);
        byte[] count = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(count, (ushort)_numDirty);
        output.Write(count);
        int written = 0;
        for (int ty = 0; ty < _mapHeight; ty++)
        {
            for (int tx = 0; tx < _mapWidth; tx++)
            {
                int index = (ty * _mapWidth) + tx;
                if (!_dirty[index])
                {
                    continue;
                }

                _dirty[index] = false;
                written++;
                int x = tx * TileSize;
                int y = ty * TileSize;
                int w = Math.Min(TileSize, Width - x);
                int h = Math.Min(TileSize, Height - y);
                output.Write(ScriptedRfbServer.RectangleHeader(x, y, w, h, _hextile ? RfbEncodings.Hextile : RfbEncodings.Raw));
                if (_hextile && SingleColour(x, y, w, h, out Rgba colour))
                {
                    output.WriteByte(2); // BackgroundSpecified (QRfbSingleColorHextile::write zawsze z tłem)
                    encoder?.WritePixel(colour, output);
                    continue;
                }

                if (_hextile)
                {
                    output.WriteByte(1); // podkodowanie Raw
                }

                for (int yy = y; yy < y + h; yy++)
                {
                    for (int xx = x; xx < x + w; xx++)
                    {
                        encoder?.WritePixel(_screen.GetPixel(xx, yy), output);
                    }
                }
            }
        }

        if (written != _numDirty)
        {
            lock (_problems) _problems.Add($"atrapa: licznik kafli {_numDirty} ≠ zapisane {written}");
        }

        _numDirty = 0;
        await _writer!.WriteAsync(_stream!, output.ToArray(), ct);
    }

    private bool SingleColour(int x, int y, int w, int h, out Rgba colour)
    {
        colour = _screen.GetPixel(x, y);
        for (int yy = y; yy < y + h; yy++)
        {
            for (int xx = x; xx < x + w; xx++)
            {
                if (_screen.GetPixel(xx, yy) != colour)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ForceDirty(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0)
        {
            return;
        }

        int right = x + w - 1;
        int bottom = y + h - 1;
        for (int ty = y / TileSize; ty <= bottom / TileSize && ty < _mapHeight; ty++)
        {
            for (int tx = x / TileSize; tx <= right / TileSize && tx < _mapWidth; tx++)
            {
                SetDirty((ty * _mapWidth) + tx);
            }
        }
    }

    private void MarkChangedTiles(RectInt r)
    {
        if (r.IsEmpty)
        {
            return;
        }

        for (int ty = r.Y / TileSize; ty <= (r.Bottom - 1) / TileSize && ty < _mapHeight; ty++)
        {
            for (int tx = r.X / TileSize; tx <= (r.Right - 1) / TileSize && tx < _mapWidth; tx++)
            {
                int x0 = tx * TileSize;
                int y0 = ty * TileSize;
                int x1 = Math.Min(Width, x0 + TileSize);
                int y1 = Math.Min(Height, y0 + TileSize);
                bool changed = false;
                for (int yy = y0; yy < y1 && !changed; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                    {
                        if (_screen.GetPixel(xx, yy) != _reference.GetPixel(xx, yy))
                        {
                            changed = true;
                            break;
                        }
                    }
                }

                if (!changed)
                {
                    continue;
                }

                for (int yy = y0; yy < y1; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                    {
                        _reference.SetPixel(xx, yy, _screen.GetPixel(xx, yy));
                    }
                }

                SetDirty((ty * _mapWidth) + tx);
            }
        }
    }

    private void SetDirty(int index)
    {
        if (!_dirty[index])
        {
            _dirty[index] = true;
            _numDirty++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Sprzątanie atrapy nie może przerwać testu.
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
            }
        }

        _cts.Dispose();
    }
}
