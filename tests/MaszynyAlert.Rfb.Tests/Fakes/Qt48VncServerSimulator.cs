using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace MaszynyAlert.Rfb.Tests.Fakes;

internal enum QtSimEventKind
{
    NowePolaczenie,

    UsunieciePoprzedniegoKlienta,

    RozlaczenieKlienta,

    DiscardClient,

    WersjaProtokoluKlienta,

    ServerInit,

    SetPixelFormat,

    SetEncodings,

    KursorKlientaUtworzony,

    KursorKlientaUsuniety,

    ZmianaKsztaltuKursoraHmi,

    ZmianaEkranuHmi,

    FramebufferUpdateRequest,

    WyslanoKursor,

    WyslanoObraz,

    KursorWtopionyWObraz,

    WejscieKlienta,

    NieobslugiwanaWiadomosc,

    FormatNiezainicjowany,

    AwariaKursorNull,

    /// <summary>Proces HMI padł: gniazda zamknięte, port przestaje słuchać (połączenia odrzucane).</summary>
    AwariaProcesu,

    RestartHmi,

    /// <summary>Błąd samego symulatora albo ograniczenie modelu (testy wymagają, żeby go nie było).</summary>
    BladSymulatora,
}

internal sealed record QtSimEvent(long Seq, double AtMs, int? Connection, QtSimEventKind Kind, string Detail)
{
    public bool IsFailure => Kind is QtSimEventKind.FormatNiezainicjowany or QtSimEventKind.AwariaKursorNull or QtSimEventKind.AwariaProcesu;

    public override string ToString() => $"#{Seq,-5} {AtMs,10:0.0} ms  k{Connection?.ToString() ?? "-",-3} {Kind}: {Detail}";
}

/// <summary>Co robi symulowany serwer, gdy koduje piksele formatem, którego żaden klient nie ustawił (stan niezdefiniowany w C++).</summary>
internal enum UninitializedFormatBehavior
{
    Crash,

    /// <summary>Pamięć wyzerowana (<c>bitsPerPixel = 0</c>, <c>sameEndian = needConversion = false</c>) — serwer żyje, ale wysyła śmieci.</summary>
    ZeroedMemory,
}

internal sealed record QtSimState(
    string State,
    int? ClientId,
    bool WantUpdate,
    bool DirtyCursor,
    bool HasClientCursor,
    bool PixelFormatInitialized,
    string PixelFormat,
    string Encoder,
    int NumDirty,
    bool TimerActive,
    bool Crashed,
    int Connections,
    int Takeovers,
    int PoisonedStates,
    int CursorWrites,
    int ImageWrites)
{
    /// <summary>Stan „zatruty”: następny klient bez kursora (−239) położy serwer pierwszym żądaniem aktualizacji.</summary>
    public bool Poisoned => DirtyCursor && !HasClientCursor;
}

internal sealed record QtPixelFormat(
    int BitsPerPixel, int Depth, bool BigEndian, bool TrueColor,
    int RedBits, int GreenBits, int BlueBits, int RedShift, int GreenShift, int BlueShift)
{
    public static QtPixelFormat ServerRgb565 { get; } = new(16, 16, false, true, 5, 6, 5, 11, 5, 0);

    /// <summary>„Pamięć wyzerowana” — wariant <see cref="UninitializedFormatBehavior.ZeroedMemory"/>.</summary>
    public static QtPixelFormat Zeroed { get; } = new(0, 0, false, false, 0, 0, 0, 0, 0, 0);

    public static QtPixelFormat Read(ReadOnlySpan<byte> b)
    {
        static int Bits(ushort a)
        {
            int n = 0;
            while (a != 0) { a >>= 1; n++; }
            return n;
        }

        return new(b[0], b[1], b[2] != 0, b[3] != 0,
            Bits(BinaryPrimitives.ReadUInt16BigEndian(b[4..])), Bits(BinaryPrimitives.ReadUInt16BigEndian(b[6..])),
            Bits(BinaryPrimitives.ReadUInt16BigEndian(b[8..])), b[10], b[11], b[12]);
    }

    public byte[] Write()
    {
        var buf = new byte[16];
        buf[0] = (byte)BitsPerPixel;
        buf[1] = (byte)Depth;
        buf[2] = (byte)(BigEndian ? 1 : 0);
        buf[3] = (byte)(TrueColor ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)((1 << RedBits) - 1));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), (ushort)((1 << GreenBits) - 1));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), (ushort)((1 << BlueBits) - 1));
        buf[10] = (byte)RedShift;
        buf[11] = (byte)GreenShift;
        buf[12] = (byte)BlueShift;
        return buf;
    }

    /// <summary>PIXEL_FORMAT w postaci z przewodu (RFC 6143 §7.4) — do budowy <c>SetPixelFormat</c> po stronie klientów skryptowych.</summary>
    public static byte[] Wire(int bpp, int depth, bool bigEndian, bool trueColor, int redMax, int greenMax, int blueMax,
        int redShift, int greenShift, int blueShift)
    {
        var buf = new byte[16];
        buf[0] = (byte)bpp;
        buf[1] = (byte)depth;
        buf[2] = (byte)(bigEndian ? 1 : 0);
        buf[3] = (byte)(trueColor ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)redMax);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), (ushort)greenMax);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), (ushort)blueMax);
        buf[10] = (byte)redShift;
        buf[11] = (byte)greenShift;
        buf[12] = (byte)blueShift;
        return buf;
    }

    public int ClientBytesPerPixel => BitsPerPixel / 8;

    public string Describe() =>
        $"{BitsPerPixel}bpp depth {Depth} {(TrueColor ? "true-colour" : "paleta")} {(BigEndian ? "BE" : "LE")} " +
        $"bity {RedBits}/{GreenBits}/{BlueBits} przesunięcia {RedShift}/{GreenShift}/{BlueShift}";
}

internal sealed class Qt48VncServerSimulator : IAsyncDisposable
{
    public const string QtDesktopName = "Qt for Embedded Linux VNC Server";

    private const int TileSize = 16;

    private readonly Channel<Action> _loop = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _eventsLock = new();
    private readonly List<QtSimEvent> _events = [];
    private readonly List<SimSocket> _sockets = [];
    private TcpListener _listener;
    private Task? _loopTask;
    private Task? _acceptTask;
    private long _seq;
    private int _nextConnectionId;
    private int _disposed;
    private bool _started;

    // ---- statystyka symulacji (wątek pętli) ----
    private int _takeovers;          // newConnection skasował połączonego klienta
    private int _poisonedStates;
    private int _cursorWrites;
    private int _imageWrites;        // encoder->write

    private SimSocket? _client;
    private ClientState _state;                 // ClientState state
    private byte _msgType;                      // quint8 msgType
    private bool _handleMsg;                    // bool handleMsg
    private QtPixelFormat? _pixelFormat;        // QRfbPixelFormat pixelFormat — null = nigdy nieustawiony (niezainicjowany)
    private bool? _sameEndian;                  // bool sameEndian — null = niezainicjowany
    private bool? _needConversion;              // bool needConversion — null = niezainicjowany
    private int _encodingsPending;              // int encodingsPending
    private int _cutTextPending;                // int cutTextPending
    private bool _supportCopyRect, _supportRre, _supportCoRre, _supportHextile, _supportZrle, _supportCursor, _supportDesktopSize;
    private bool _wantUpdate;                   // bool wantUpdate
    private bool _dirtyCursor;                  // bool dirtyCursor
    private ClientCursor? _qvncCursor;
    private IEncoder? _encoder;                 // QRfbEncoder *encoder
    private bool _timerActive;
    private long _timerGeneration;
    private bool _crashed;

    private ushort[] _screen = [];              // pamięć ekranu RGB565 (16 bpp)
    private DirtyMap _dirty = null!;
    private int _cursorShape;                   // numer kształtu kursora HMI (zmiany = zdarzenie symulacji)
    private int _clockTick;                     // „zegar” HMI dla RepaintBeforeEachRequest

    /// <param name="port">Port na 127.0.0.1; 0 = dowolny wolny (testy). Stały port — narzędzie dla przeglądarki zewnętrznej.</param>
    public Qt48VncServerSimulator(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    // ===================================================================================================
    // Parametry symulacji
    // ===================================================================================================

    public int Port { get; }

    public int Width { get; init; } = 1024;

    public int Height { get; init; } = 768;

    public string DesktopName { get; init; } = QtDesktopName;

    public int ScreenDepth { get; init; } = 16;

    public bool HasSubscreen { get; init; } = true;

    public bool CursorAccelerated { get; init; } = true;

    public bool ClientCursorCondition => !HasSubscreen || CursorAccelerated;

    public bool HextileCompiledFor16 { get; init; } = true;


    public TimeSpan TimerInterval { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Timer uruchamiany ręcznie (<see cref="FireTimerAsync"/>) zamiast po <see cref="TimerInterval"/>.</summary>
    public bool ManualTimer { get; init; }

    /// <summary>Co robi serwer, gdy koder użyje niezainicjowanego <c>pixelFormat</c>.</summary>
    public UninitializedFormatBehavior OnUninitializedFormat { get; init; } = UninitializedFormatBehavior.Crash;

    public int CursorWidth { get; init; } = 16;

    public int CursorHeight { get; init; } = 16;

    /// <summary>Położenie kursora HMI (lewy górny róg obrazka kursora; hotspot 0,0).</summary>
    public int CursorX { get; init; } = 300;

    public int CursorY { get; init; } = 300;

    /// <summary>Kolor kursora HMI (RGB565) — czysta zieleń; nasz detektor go nie zna, test „kursor nigdy na klatce” go szuka.</summary>
    public ushort CursorColor565 { get; init; } = 0x07E0;

    /// <summary>
    /// „Żywe” HMI: przed obsłużeniem każdego <c>FramebufferUpdateRequest</c> HMI przemalowuje mały obszar zegara (prawy
    /// dolny róg) — każde żądanie przyrostowe dostaje odpowiedź od razu, a <c>wantUpdate</c> przez większość czasu jest
    /// fałszywe (najczęstszy stan, w którym zmiana kursora zostawia <c>dirtyCursor</c>).
    /// </summary>
    public bool RepaintBeforeEachRequest { get; init; }

    /// <summary>Ekran początkowy (RGB565, <see cref="Width"/>×<see cref="Height"/>); null — wzór <see cref="DefaultScreen"/>.</summary>
    public ushort[]? InitialScreen { get; init; }

    /// <summary>Obszar zegara HMI (<see cref="RepaintBeforeEachRequest"/>, <see cref="AnimateClockAsync"/>).</summary>
    public (int X, int Y, int W, int H) ClockArea => (Width - 40, Height - 20, 32, 12);

    // ===================================================================================================
    // API testów
    // ===================================================================================================

    public void Start()
    {
        if (_started) throw new InvalidOperationException("Symulator już wystartował.");
        _started = true;
        _screen = InitialScreen is { } s ? (ushort[])s.Clone() : DefaultScreen(Width, Height);
        if (_screen.Length != Width * Height) throw new ArgumentException("InitialScreen ma zły rozmiar.");
        InitServer();
        _loopTask = Task.Run(RunLoopAsync);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_listener));
    }

    /// <summary>Kopia dziennika zdarzeń.</summary>
    public IReadOnlyList<QtSimEvent> Events
    {
        get { lock (_eventsLock) return [.. _events]; }
    }

    /// <summary>Zdarzenia ścieżki awarii.</summary>
    public IReadOnlyList<QtSimEvent> Failures => Events.Where(e => e.IsFailure).ToList();

    /// <summary>Liczba połączeń przyjętych przez serwer (<c>newConnection</c>).</summary>
    public int ConnectionCount => Volatile.Read(ref _nextConnectionId);

    /// <summary>Czytelny zapis dziennika (do komunikatów asercji i raportu).</summary>
    public string Dump(int max = 400)
    {
        var all = Events;
        var lines = all.Skip(Math.Max(0, all.Count - max)).Select(e => e.ToString());
        return string.Join(Environment.NewLine, lines);
    }

    public Task<QtSimState> InspectAsync() => InvokeAsync(() => new QtSimState(
        _state.ToString(), _client?.Id, _wantUpdate, _dirtyCursor, _qvncCursor is not null, _pixelFormat is not null,
        _pixelFormat?.Describe() ?? "niezainicjowany", _encoder?.Name ?? "brak", _dirty.NumDirty, _timerActive, _crashed,
        ConnectionCount, _takeovers, _poisonedStates, _cursorWrites, _imageWrites));

    /// <summary>Czeka, aż pętla zdarzeń przetworzy wszystko, co już do niej trafiło.</summary>
    public Task SyncAsync() => InvokeAsync(() => true);

    public Task ChangeCursorShapeAsync() => InvokeAsync(() =>
    {
        if (_crashed) return false;
        _cursorShape++;
        if (_qvncCursor is not null)
        {
            _dirtyCursor = true;
            SetDirty();
            Record(QtSimEventKind.ZmianaKsztaltuKursoraHmi, _client?.Id,
                $"kształt {_cursorShape}: QVNCClientCursor::set → dirtyCursor = true (wantUpdate = {_wantUpdate}, stan {_state})");
        }
        else
        {
            bool dirties = CursorAccelerated;
            if (dirties)
            {
                ScreenSetDirty(CursorRect(), force: true);
            }

            Record(QtSimEventKind.ZmianaKsztaltuKursoraHmi, _client?.Id,
                $"kształt {_cursorShape}: QVNCCursor::set (brak kursora klienta) — {(dirties ? "brudzi kafle pod kursorem" : "kursor programowy, bez zmian w VNC")}");
        }

        return true;
    });

    public Task PaintAsync(int x, int y, int w, int h, ushort color565) => InvokeAsync(() =>
    {
        if (_crashed) return false;
        Paint(x, y, w, h, color565, record: true);
        return true;
    });

    /// <summary>Zdarzenie HMI: „zegar” zmienia się (jeden krok animacji obszaru <see cref="ClockArea"/>).</summary>
    public Task AnimateClockAsync() => InvokeAsync(() =>
    {
        if (_crashed) return false;
        AnimateClock();
        return true;
    });

    /// <summary>Ręczne odpalenie timera (tylko <see cref="ManualTimer"/>): <c>checkUpdate</c>, jeśli timer jest aktywny.</summary>
    public Task<bool> FireTimerAsync() => InvokeAsync(() =>
    {
        if (_crashed || !_timerActive) return false;
        _timerActive = false;
        _timerGeneration++;
        CheckUpdate();
        return true;
    });

    /// <summary>Zdarzenie symulacji: ponowny start HMI po awarii (nowy proces: <c>init</c>, format znów niezainicjowany).</summary>
    public Task RestartHmiAsync() => InvokeAsync(() =>
    {
        foreach (SimSocket s in SnapshotSockets()) CloseSocket(s);
        try { _listener.Stop(); } catch (SocketException) { }
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        _listener = listener;
        _crashed = false;
        InitServer();
        Record(QtSimEventKind.RestartHmi, null, "nowy proces HMI: QVNCServer::init (S1:584–611), pixelFormat niezainicjowany, dirtyCursor = false");
        _acceptTask = Task.Run(() => AcceptLoopAsync(listener));
        return true;
    });

    /// <summary>Oczekuje, aż w dzienniku pojawi się zdarzenie spełniające warunek (limit czasu rzeczywistego).</summary>
    public async Task<QtSimEvent> WaitForEventAsync(Func<QtSimEvent, bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            QtSimEvent? found = Events.FirstOrDefault(predicate);
            if (found is not null) return found;
            if (sw.Elapsed > timeout)
                throw new TimeoutException($"Brak oczekiwanego zdarzenia w {timeout.TotalSeconds:0.#} s. Dziennik:{Environment.NewLine}{Dump(60)}");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _listener.Stop(); } catch (SocketException) { }
        if (_loopTask is not null)
        {
            try
            {
                await InvokeAsync(() =>
                {
                    foreach (SimSocket s in SnapshotSockets()) CloseSocket(s);
                    return true;
                }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException) { }
        }

        _cts.Cancel();
        _loop.Writer.TryComplete();
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }

        foreach (SimSocket s in SnapshotSockets()) s.CloseTcp();
        _cts.Dispose();
    }

    /// <summary>Domyślny ekran HMI: tło, pasek stanu z czerwonym tekstem, kafle dwu- i wielokolorowe, gradient (kafle Raw).</summary>
    public static ushort[] DefaultScreen(int width, int height)
    {
        var s = new ushort[width * height];
        ushort background = Rgb565(200, 204, 208);
        Array.Fill(s, background);
        void Fill(int x0, int y0, int w, int h, ushort c)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(height, y0 + h); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(width, x0 + w); x++)
                    s[(y * width) + x] = c;
        }

        Fill(35, height - 59, Math.Min(954, width - 35), 48, Rgb565(232, 232, 232));
        for (int i = 0; i < 40 && 60 + (i * 12) + 8 < width; i++) Fill(60 + (i * 12), height - 45, 8, 16, Rgb565(255, 0, 0));
        // Gęste pionowe paski trzech kolorów (podprostokątów za dużo na 256 B → kafle Raw).
        for (int x = 0; x < Math.Min(96, width); x++) Fill(x, 40, 1, 16, (x % 3) switch { 0 => Rgb565(255, 0, 0), 1 => Rgb565(0, 0, 255), _ => Rgb565(255, 255, 0) });
        // Kafle wielokolorowe mieszczące się w 256 B (QRfbMultiColorHextile): kilka małych prostokątów trzech kolorów na tle.
        for (int t = 0; t < 6; t++)
        {
            int x0 = 128 + (t * 16), y0 = 160;
            Fill(x0 + 1, y0 + 2, 4, 3, Rgb565(255, 0, 0));
            Fill(x0 + 7, y0 + 6, 3, 5, Rgb565(0, 0, 255));
            Fill(x0 + 11, y0 + 12 - t, 2, 2, Rgb565(255, 255, 0));
        }
        // Gradient (kafle Raw — za dużo kolorów na podprostokąty).
        for (int y = 80; y < Math.Min(112, height); y++)
            for (int x = 0; x < Math.Min(64, width); x++)
                s[(y * width) + x] = (ushort)((x * 997) + (y * 31));

        return s;
    }

    public static ushort Rgb565(int r, int g, int b) => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));


    private async Task RunLoopAsync()
    {
        await foreach (Action action in _loop.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Record(QtSimEventKind.BladSymulatora, _client?.Id, ex.ToString());
            }

            FlushAll();
        }
    }

    private void Post(Action action) => _loop.Writer.TryWrite(action);

    private Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_loop.Writer.TryWrite(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(Qt48VncServerSimulator)));
        }

        return tcs.Task;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp = await listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                var socket = new SimSocket(Interlocked.Increment(ref _nextConnectionId), tcp);
                lock (_sockets) _sockets.Add(socket);
                Post(() => OnNewConnection(socket));
                _ = Task.Run(() => WriteLoopAsync(socket));
                _ = Task.Run(() => ReadLoopAsync(socket));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Listener zatrzymany (awaria HMI albo koniec testu).
        }
    }

    private async Task ReadLoopAsync(SimSocket socket)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await socket.Stream.ReadAsync(buffer, socket.Token).ConfigureAwait(false);
                if (n == 0) break;
                byte[] chunk = buffer.AsSpan(0, n).ToArray();
                Post(() => OnReadyRead(socket, chunk));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Zamknięte z naszej albo z drugiej strony.
        }

        Post(() => OnPeerClosed(socket));
    }

    private static async Task WriteLoopAsync(SimSocket socket)
    {
        try
        {
            await foreach (byte[] chunk in socket.Outgoing.Reader.ReadAllAsync(socket.Token).ConfigureAwait(false))
            {
                await socket.Stream.WriteAsync(chunk, socket.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Gniazdo zamknięte.
        }
        finally
        {
            // Zamknięcie po wysłaniu wszystkiego, co już oddano „do jądra” (abort/awaria — patrz CloseSocket).
            socket.CloseTcp();
        }
    }

    private void FlushAll()
    {
        foreach (SimSocket s in SnapshotSockets())
        {
            s.Flush();
        }
    }

    private SimSocket[] SnapshotSockets()
    {
        lock (_sockets) return [.. _sockets];
    }

    private void CloseSocket(SimSocket s)
    {
        s.DiscardPending();
        s.Outgoing.Writer.TryComplete(); // pisarz wyśle, co już oddano, i zamknie gniazdo
        lock (_sockets) _sockets.Remove(s);
    }

    private void Record(QtSimEventKind kind, int? connection, string detail)
    {
        lock (_eventsLock)
        {
            _events.Add(new QtSimEvent(++_seq, _clock.Elapsed.TotalMilliseconds, connection, kind, detail));
        }
    }


    private enum ClientState
    {
        Unconnected,
        Protocol,
        Init,
        Connected,
    }

    private void InitServer()
    {
        _handleMsg = false;
        _client = null;
        _encodingsPending = 0;
        _cutTextPending = 0;
        _state = ClientState.Unconnected;
        _dirtyCursor = false;
        _qvncCursor = null;
        _encoder = null;
        _pixelFormat = null;
        _sameEndian = null;
        _needConversion = null;
        _wantUpdate = false;
        _supportCopyRect = _supportRre = _supportCoRre = _supportHextile = _supportZrle = _supportCursor = _supportDesktopSize = false;
        StopTimer();
        _dirty = new DirtyMap(this);
    }

    private void OnNewConnection(SimSocket s)
    {
        if (_crashed)
        {
            CloseSocket(s);
            return;
        }

        Record(QtSimEventKind.NowePolaczenie, s.Id,
            $"poprzedni klient: {(_client is null ? "brak" : $"k{_client.Id} ({(_client.QtConnected ? "połączony" : "rozłączony")})")}; " +
            $"dirtyCursor = {_dirtyCursor}, qvnc_cursor {(_qvncCursor is null ? "= 0" : "istnieje")}, format {(_pixelFormat is null ? "niezainicjowany" : _pixelFormat.Describe())}");
        if (_client is { } old)
        {
            bool wasConnected = old.QtConnected;
            old.QtConnected = false;
            old.Deleted = true;
            CloseSocket(old);
            Record(QtSimEventKind.UsunieciePoprzedniegoKlienta, old.Id,
                wasConnected ? "delete client → abort → disconnected() → discardClient (synchronicznie)" : "gniazdo już rozłączone — bez discardClient");
            if (wasConnected)
            {
                _takeovers++;
                DiscardClient("delete client w newConnection (S1:635–636)");
            }
        }

        _client = s;
        _handleMsg = false;
        _encodingsPending = 0;
        _cutTextPending = 0;
        _supportHextile = false;
        _wantUpdate = false;
        StartTimer();
        _dirty.Reset();
        s.Write("RFB 003.003\n"u8);
        _state = ClientState.Protocol;
    }

    private void OnReadyRead(SimSocket s, byte[] data)
    {
        // Gniazdo usunięte w newConnection nie dostarcza już nic (obiekt skasowany); rozłączone — też nie.
        if (_crashed || s.Deleted || !s.QtConnected || !ReferenceEquals(s, _client)) return;
        s.Append(data);
        ReadClient();
    }

    private void OnPeerClosed(SimSocket s)
    {
        if (s.Deleted || !s.QtConnected)
        {
            CloseSocket(s);
            return;
        }

        s.QtConnected = false;
        CloseSocket(s);
        if (_crashed) return;
        Record(QtSimEventKind.RozlaczenieKlienta, s.Id, "klient zamknął połączenie → disconnected()");
        if (ReferenceEquals(s, _client)) DiscardClient("klient się rozłączył (S1:640)");
    }

    private void ReadClient()
    {
        SimSocket c = _client!;
        switch (_state)
        {
            case ClientState.Protocol:
                if (c.BytesAvailable >= 12)
                {
                    string proto = Encoding.ASCII.GetString(c.Read(12));
                    Record(QtSimEventKind.WersjaProtokoluKlienta, c.Id, proto.TrimEnd('\n'));
                    c.Write([0, 0, 0, 1]);
                    _state = ClientState.Init;
                }

                break;

            case ClientState.Init:
                if (c.BytesAvailable >= 1)
                {
                    c.Read(1);
                    c.Write(BuildServerInit());
                    Record(QtSimEventKind.ServerInit, c.Id, $"{Width}x{Height}, {ServerFormat().Describe()}, \"{DesktopName}\"");
                    _state = ClientState.Connected;
                }

                break;

            case ClientState.Connected:
                do
                {
                    if (!_handleMsg)
                    {
                        if (c.BytesAvailable > 0) _msgType = c.Read(1)[0]; // QIODevice::read przy 0 B zostawia stary msgType
                        _handleMsg = true;
                    }

                    if (_handleMsg)
                    {
                        switch (_msgType)
                        {
                            case 0: SetPixelFormat(); break;
                            case 1:
                                Record(QtSimEventKind.NieobslugiwanaWiadomosc, c.Id, "FixColourMapEntries — nieobsługiwane, treść NIE jest pomijana (S1:800–803)");
                                _handleMsg = false;
                                break;
                            case 2: SetEncodings(); break;
                            case 3: FrameBufferUpdateRequest(); break;
                            case 4: KeyEvent(); break;
                            case 5: PointerEvent(); break;
                            case 6: ClientCutText(); break;
                            default:
                                Record(QtSimEventKind.NieobslugiwanaWiadomosc, c.Id, $"nieznany typ {_msgType} (S1:819–821)");
                                _handleMsg = false;
                                break;
                        }
                    }

                    if (_crashed) return;
                }
                while (!_handleMsg && c.BytesAvailable > 0);

                break;
        }
    }

    private void DiscardClient(string reason)
    {
        StopTimer();
        _state = ClientState.Unconnected;
        _encoder = null;
        bool hadCursor = _qvncCursor is not null;
        DeleteClientCursor("discardClient (S1:2038–2039)");
        if (hadCursor && _dirtyCursor) _poisonedStates++;
        Record(QtSimEventKind.DiscardClient, _client?.Id,
            $"{reason}; usunięto kursor klienta: {(hadCursor ? "tak" : "nie było")}; dirtyCursor = {_dirtyCursor} (NIE zerowany), wantUpdate = {_wantUpdate}" +
            (!HasSubscreen ? "; enablePainting(false)" : ""));
    }

    // ===================================================================================================
    // Wiadomości klienta
    // ===================================================================================================

    private void SetPixelFormat()
    {
        SimSocket c = _client!;
        if (c.BytesAvailable < 19) return; // czeka na resztę (handleMsg zostaje true)
        c.Read(3); // wypełnienie
        QtPixelFormat pf = QtPixelFormat.Read(c.Read(16));
        _pixelFormat = pf;
        Record(QtSimEventKind.SetPixelFormat, c.Id, pf.Describe());
        if (!pf.TrueColor)
        {
            DiscardClient("setPixelFormat: „Can only handle true color clients” — gniazdo zostaje otwarte (S1:862–865)");
        }

        _handleMsg = false;
        _sameEndian = !pf.BigEndian;
        _needConversion = PixelConversionNeeded();
    }

    private void SetEncodings()
    {
        SimSocket c = _client!;
        if (_encodingsPending == 0 && c.BytesAvailable >= 3)
        {
            c.Read(1);
            _encodingsPending = BinaryPrimitives.ReadUInt16BigEndian(c.Read(2));
            if (_encodingsPending == 0) _handleMsg = false;
        }

        _encoder = null;

        if (_encodingsPending > 0 && c.BytesAvailable >= _encodingsPending * 4)
        {
            var list = new List<int>(_encodingsPending);
            var notes = new List<string>();
            for (int i = 0; i < _encodingsPending; ++i)
            {
                int enc = BinaryPrimitives.ReadInt32BigEndian(c.Read(4));
                list.Add(enc);
                switch (enc)
                {
                    case 0:
                        _encoder ??= new RawEncoder(this);
                        break;
                    case 1: _supportCopyRect = true; break;
                    case 2: _supportRre = true; break;
                    case 4: _supportCoRre = true; break;
                    case 5:
                        _supportHextile = true;
                        if (_encoder is not null) break;
                        if (ScreenDepth == 16 && HextileCompiledFor16) _encoder = new HextileEncoder(this);
                        break;
                    case 16: _supportZrle = true; break;
                    case -239:
                        _supportCursor = true;
                        if (ClientCursorCondition)
                        {
                            DeleteClientCursor("setEncodings: delete qvnc_cursor (S1:982)");
                            CreateClientCursor();
                            notes.Add("Cursor → nowy QVNCClientCursor");
                        }
                        else
                        {
                            notes.Add("Cursor → warunek S1:981 fałszywy, kursor klienta NIE powstaje");
                        }

                        break;
                    case -223: _supportDesktopSize = true; break;
                    default: break;
                }
            }

            _handleMsg = false;
            _encodingsPending = 0;
            _encoder ??= new RawEncoder(this);
            string supports = string.Join(",", new[]
            {
                _supportCopyRect ? "CopyRect" : null, _supportRre ? "RRE" : null, _supportCoRre ? "CoRRE" : null,
                _supportHextile ? "Hextile" : null, _supportZrle ? "ZRLE" : null, _supportCursor ? "Cursor" : null,
                _supportDesktopSize ? "DesktopSize" : null,
            }.Where(s => s is not null));
            Record(QtSimEventKind.SetEncodings, c.Id,
                $"[{string.Join(", ", list)}] → koder {_encoder.Name}{(notes.Count > 0 ? "; " + string.Join("; ", notes) : "")}; flagi: {supports}");
            return;
        }

        _encoder ??= new RawEncoder(this);
    }

    private void FrameBufferUpdateRequest()
    {
        SimSocket c = _client!;
        if (c.BytesAvailable < 9) return;
        byte[] body = c.Read(9);
        bool incremental = body[0] != 0;
        int x = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1));
        int y = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3));
        int w = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(5));
        int h = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(7));
        Record(QtSimEventKind.FramebufferUpdateRequest, c.Id,
            $"{(incremental ? "przyrostowe" : "pełne")} ({x},{y},{w},{h}); przed: wantUpdate = {_wantUpdate}, dirtyCursor = {_dirtyCursor}, kursor klienta: {(_qvncCursor is null ? "brak" : "jest")}");
        if (RepaintBeforeEachRequest) AnimateClock();
        if (!incremental) ScreenSetDirty((x, y, w, h), force: true);
        _wantUpdate = true;
        CheckUpdate();
        _handleMsg = false;
    }

    private void KeyEvent()
    {
        SimSocket c = _client!;
        if (c.BytesAvailable < 7) return;
        c.Read(7);
        Record(QtSimEventKind.WejscieKlienta, c.Id, "KeyEvent → QWSServer::sendKeyEvent (S1:1046–1047)");
        _handleMsg = false;
    }

    private void PointerEvent()
    {
        SimSocket c = _client!;
        if (c.BytesAvailable < 5) return;
        c.Read(5);
        Record(QtSimEventKind.WejscieKlienta, c.Id, "PointerEvent → QWSServer::sendMouseEvent (S1:1027)");
        _handleMsg = false;
    }

    private void ClientCutText()
    {
        SimSocket c = _client!;
        if (_cutTextPending == 0 && c.BytesAvailable >= 7)
        {
            c.Read(3);
            _cutTextPending = (int)BinaryPrimitives.ReadUInt32BigEndian(c.Read(4));
            if (_cutTextPending == 0) _handleMsg = false;
            Record(QtSimEventKind.WejscieKlienta, c.Id, $"ClientCutText ({_cutTextPending} B)");
        }

        if (_cutTextPending > 0 && c.BytesAvailable >= _cutTextPending)
        {
            c.Read(_cutTextPending);
            _cutTextPending = 0;
            _handleMsg = false;
        }
    }

    // ===================================================================================================
    // checkUpdate, timer, setDirty
    // ===================================================================================================

    private void CheckUpdate()
    {
        if (_crashed) return;
        if (!_wantUpdate) return;
        if (_dirtyCursor)
        {
            if (_qvncCursor is null)
            {
                Crash(QtSimEventKind.AwariaKursorNull,
                    "checkUpdate: wantUpdate && dirtyCursor, a qvnc_cursor == 0 → Q_ASSERT(qvnc_cursor) / qvnc_cursor->write() na wskaźniku zerowym (S1:2014–2017)");
                return;
            }

            _qvncCursor.Write();
            if (_crashed) return;
            _dirtyCursor = false;
            _wantUpdate = false;
            return;
        }

        if (_dirty.NumDirty > 0)
        {
            _encoder?.Write();
            if (_crashed) return;
            _wantUpdate = false;
        }
    }

    private void SetDirty()
    {
        if (_state == ClientState.Connected && !_timerActive && (_dirty.NumDirty > 0 || _dirtyCursor)) StartTimer();
    }

    private void StartTimer()
    {
        _timerActive = true;
        long generation = ++_timerGeneration;
        if (ManualTimer) return;
        _ = FireLaterAsync(generation);
    }

    private async Task FireLaterAsync(long generation)
    {
        try
        {
            await Task.Delay(TimerInterval, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Post(() =>
        {
            if (_crashed || !_timerActive || generation != _timerGeneration) return;
            _timerActive = false;
            CheckUpdate();
        });
    }

    private void StopTimer()
    {
        _timerActive = false;
        _timerGeneration++;
    }

    private void ScreenSetDirty((int X, int Y, int W, int H) r, bool force)
    {
        if (r.W <= 0 || r.H <= 0) return;                 // rect.isEmpty()
        if (_state != ClientState.Connected) return;
        int right = r.X + r.W - 1, bottom = r.Y + r.H - 1; // QRect::right()/bottom()
        int x1 = r.X / TileSize;
        for (int y = r.Y / TileSize; y <= bottom / TileSize && y < _dirty.MapHeight; y++)
            for (int x = x1; x <= right / TileSize && x < _dirty.MapWidth; x++)
                _dirty.SetDirty(x, y, force);
        SetDirty();
    }

    private void Paint(int x0, int y0, int w, int h, ushort color, bool record)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(Height, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(Width, x0 + w); x++)
                _screen[(y * Width) + x] = color;
        if (record) Record(QtSimEventKind.ZmianaEkranuHmi, _client?.Id, $"({x0},{y0},{w},{h}) = 0x{color:X4}");
        ScreenSetDirty((x0, y0, w, h), force: false);
    }

    private void AnimateClock()
    {
        (int x, int y, int w, int h) = ClockArea;
        _clockTick++;
        Paint(x, y, w, h, (ushort)(0x2104 + ((_clockTick % 7) * 0x0841)), record: false);
    }

    private (int X, int Y, int W, int H) CursorRect() => (CursorX, CursorY, CursorWidth, CursorHeight);

    // ===================================================================================================
    // Kursor klienta
    // ===================================================================================================

    private void CreateClientCursor()
    {
        _qvncCursor = new ClientCursor(this);
        _dirtyCursor = true;
        SetDirty();
        Record(QtSimEventKind.KursorKlientaUtworzony, _client?.Id, "QVNCClientCursor (qt_screencursor = kursor klienta, S1:138); dirtyCursor = true");
    }

    private void DeleteClientCursor(string where)
    {
        if (_qvncCursor is null) return;
        _qvncCursor = null;
        Record(QtSimEventKind.KursorKlientaUsuniety, _client?.Id, where);
    }

    private sealed class ClientCursor(Qt48VncServerSimulator sim)
    {
        public void Write()
        {
            SimSocket c = sim._client!;
            int w = sim.CursorWidth, h = sim.CursorHeight;
            byte[] header = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), 0);    // htons(0): typ 0 + wypełnienie
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 1);    // htons(1): jeden prostokąt
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 0);    // hotspot.x
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 0);    // hotspot.y
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)w);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)h);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), -239);
            c.Write(header);
            if (w == 0 || h == 0)
            {
                sim.Record(QtSimEventKind.WyslanoKursor, c.Id, "kursor pusty (cursor.isNull, S1:171–172)");
                return;
            }

            if (!sim.EnsurePixelFormat("QVNCClientCursor::write — clientBytesPerPixel/convertPixels (S1:177–182)")) return;
            if (sim.ScreenDepth != 16)
            {
                sim.Record(QtSimEventKind.BladSymulatora, c.Id, "ograniczenie modelu: kursor dla ekranu innego niż 16-bitowy nie jest symulowany");
                return;
            }

            int bpp = sim.ClientBytesPerPixel;
            var row = new ushort[w];
            Array.Fill(row, sim.CursorColor565);
            int n = bpp * w;
            for (int i = 0; i < h; ++i)
            {
                byte[] buffer = new byte[Math.Max(n, sim.ConvertedSize(w))];
                sim.ConvertPixels(buffer, row, w);
                c.Write(buffer.AsSpan(0, n));
            }

            int maskWidth = (w + 7) / 8;
            for (int i = 0; i < h; ++i)
            {
                byte[] mask = new byte[maskWidth];
                for (int x = 0; x < w; x++) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                c.Write(mask);
            }

            sim._cursorWrites++;
            sim.Record(QtSimEventKind.WyslanoKursor, c.Id, $"{w}x{h}, {n * h + maskWidth * h} B po nagłówku, format klienta {sim._pixelFormat?.Describe() ?? "niezainicjowany"}");
        }
    }

    // ===================================================================================================
    // Format piksela i konwersja
    // ===================================================================================================

    private QtPixelFormat ServerFormat() => ScreenDepth == 16
        ? QtPixelFormat.ServerRgb565
        : new QtPixelFormat(8, 8, false, false, 0, 0, 0, 0, 0, 0);

    private bool EnsurePixelFormat(string where)
    {
        if (_pixelFormat is not null) return true;
        Record(QtSimEventKind.FormatNiezainicjowany, _client?.Id,
            $"{where}: QVNCServer::pixelFormat nigdy nie ustawiony od startu HMI (S2:494; init S1:584–611 i newConnection S1:633–657 go nie ustawiają; " +
            "sameEndian/needConversion też niezainicjowane, S1:867–868) — zachowanie niezdefiniowane");
        if (OnUninitializedFormat == UninitializedFormatBehavior.Crash)
        {
            Crash(QtSimEventKind.AwariaProcesu, "kodowanie pikseli formatem-śmieciem (np. memcpy/bufor o długości z bitsPerPixel spoza zakresu)");
            return false;
        }

        return true;
    }

    private QtPixelFormat CurrentFormat => _pixelFormat ?? QtPixelFormat.Zeroed;

    private int ClientBytesPerPixel => CurrentFormat.ClientBytesPerPixel;

    private bool SameEndian => _sameEndian ?? false;

    private bool DoPixelConversion => _needConversion ?? false;

    private int ConvertedSize(int count) => Math.Max(CurrentFormat.ClientBytesPerPixel, (CurrentFormat.BitsPerPixel + 7) / 8) * count;

    private bool PixelConversionNeeded()
    {
        QtPixelFormat pf = CurrentFormat;
        if (!SameEndian) return true;
        if (ScreenDepth != pf.BitsPerPixel) return true;
        return ScreenDepth switch
        {
            32 or 24 => false,
            18 => pf.RedBits == 6 && pf.GreenBits == 6 && pf.BlueBits == 6,
            16 => pf.RedBits == 5 && pf.GreenBits == 6 && pf.BlueBits == 5,
            15 => pf.RedBits == 5 && pf.GreenBits == 5 && pf.BlueBits == 5,
            12 => pf.RedBits == 4 && pf.GreenBits == 4 && pf.BlueBits == 4,
            _ => true,
        };
    }

    private void ConvertPixels(Span<byte> dst, ReadOnlySpan<ushort> src, int count)
    {
        QtPixelFormat pf = CurrentFormat;
        if (SameEndian && ScreenDepth == pf.BitsPerPixel && pf.RedBits == 5 && pf.GreenBits == 6 && pf.BlueBits == 5)
        {
            for (int i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(dst[(i * 2)..], src[i]);
            return;
        }

        int bytesPerPixel = (pf.BitsPerPixel + 7) / 8;
        int d = 0;
        for (int i = 0; i < count; ++i)
        {
            ushort p = src[i];
            int r = ((p >> 11) & 0x1f) << 3;
            int g = ((p >> 5) & 0x3f) << 2;
            int b = (p & 0x1f) << 3;
            r >>= 8 - pf.RedBits;
            g >>= 8 - pf.GreenBits;
            b >>= 8 - pf.BlueBits;
            uint pixel = (uint)((r << pf.RedShift) | (g << pf.GreenShift) | (b << pf.BlueShift));
            if (!(SameEndian || pf.BitsPerPixel == 8))
            {
                pixel = pf.BitsPerPixel switch
                {
                    16 => ((pixel & 0xff000000) >> 8) | ((pixel & 0x00ff0000) << 8),
                    32 => ((pixel & 0xff000000) >> 24) | ((pixel & 0x00ff0000) >> 8) | ((pixel & 0x0000ff00) << 8) | ((pixel & 0x000000ff) << 24),
                    _ => pixel,
                };
            }

            for (int k = 0; k < bytesPerPixel && k < 4; k++) dst[d + k] = (byte)(pixel >> (8 * k)); // memcpy(dst, &pixel, bytesPerPixel)
            d += bytesPerPixel;
        }
    }

    private byte[] BuildServerInit()
    {
        byte[] name = Encoding.UTF8.GetBytes(DesktopName);
        byte[] m = new byte[24 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(0), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)Height);
        ServerFormat().Write().CopyTo(m, 4);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(20), (uint)name.Length);
        name.CopyTo(m, 24);
        return m;
    }

    private void Crash(QtSimEventKind kind, string detail)
    {
        if (kind != QtSimEventKind.AwariaProcesu) Record(kind, _client?.Id, detail);
        Record(QtSimEventKind.AwariaProcesu, _client?.Id,
            (kind == QtSimEventKind.AwariaProcesu ? detail + "; " : "") +
            "proces HMI padł — gniazda zamknięte bez dalszych danych, port przestaje słuchać (kolejne połączenia odrzucone)");
        _crashed = true;
        StopTimer();
        foreach (SimSocket s in SnapshotSockets()) CloseSocket(s);
        try { _listener.Stop(); } catch (SocketException) { }
    }

    // ===================================================================================================
    // Mapa brudnych kafli
    // ===================================================================================================

    private sealed class DirtyMap
    {
        private readonly Qt48VncServerSimulator _sim;
        private readonly byte[] _map;
        private readonly ushort[] _shadow; // bufor porównawczy

        public DirtyMap(Qt48VncServerSimulator sim)
        {
            _sim = sim;
            MapWidth = (sim.Width + TileSize - 1) / TileSize;
            MapHeight = (sim.Height + TileSize - 1) / TileSize;
            _map = new byte[MapWidth * MapHeight];
            _shadow = new ushort[sim.Width * sim.Height];
        }

        public int MapWidth { get; }

        public int MapHeight { get; }

        public int NumDirty { get; private set; }

        public void Reset()
        {
            Array.Fill(_map, (byte)1);
            Array.Clear(_shadow);
            NumDirty = _map.Length;
        }

        public bool Dirty(int x, int y) => _map[(y * MapWidth) + x] != 0;

        public void SetClean(int x, int y)
        {
            _map[(y * MapWidth) + x] = 0;
            --NumDirty;
        }

        public void SetDirty(int tileX, int tileY, bool force)
        {
            bool changed = false;
            if (!force)
            {
                int startX = tileX * TileSize, startY = tileY * TileSize;
                int tileHeight = startY + TileSize > _sim.Height ? _sim.Height - startY : TileSize;
                int tileWidth = startX + TileSize > _sim.Width ? _sim.Width - startX : TileSize;
                int y = 0;
                for (; y < tileHeight; y++)
                {
                    int row = ((startY + y) * _sim.Width) + startX;
                    if (!_sim._screen.AsSpan(row, tileWidth).SequenceEqual(_shadow.AsSpan(row, tileWidth)))
                    {
                        changed = true;
                        break;
                    }
                }

                for (; y < tileHeight; y++) // memcpy reszty wierszy do bufora porównawczego
                {
                    int row = ((startY + y) * _sim.Width) + startX;
                    _sim._screen.AsSpan(row, tileWidth).CopyTo(_shadow.AsSpan(row, tileWidth));
                }
            }

            int index = (tileY * MapWidth) + tileX;
            if ((force || changed) && _map[index] == 0)
            {
                _map[index] = 1;
                ++NumDirty;
            }
        }
    }

    // ===================================================================================================
    // Kodery
    // ===================================================================================================

    private interface IEncoder
    {
        string Name { get; }

        void Write();
    }

    private bool DoBlendCursor => _qvncCursor is null && CursorAccelerated;

    /// <summary>Piksele kafla (z wtopionym kursorem, gdy trzeba) jako tablica w×h.</summary>
    private ushort[] TilePixels(int x0, int y0, int w, int h, ref bool blended)
    {
        var tile = new ushort[w * h];
        for (int y = 0; y < h; y++) _screen.AsSpan(((y0 + y) * Width) + x0, w).CopyTo(tile.AsSpan(y * w, w));
        if (DoBlendCursor && CursorWidth > 0 && CursorHeight > 0)
        {
            int cx0 = Math.Max(x0, CursorX), cy0 = Math.Max(y0, CursorY);
            int cx1 = Math.Min(x0 + w, CursorX + CursorWidth), cy1 = Math.Min(y0 + h, CursorY + CursorHeight);
            if (cx0 < cx1 && cy0 < cy1)
            {
                blended = true;
                for (int y = cy0; y < cy1; y++)
                    for (int x = cx0; x < cx1; x++)
                        tile[((y - y0) * w) + (x - x0)] = CursorColor565;
            }
        }

        return tile;
    }

    private sealed class HextileEncoder(Qt48VncServerSimulator sim) : IEncoder
    {
        private ushort _bg, _fg;
        private bool _newBg, _newFg;

        private readonly byte[] _dualXy = new byte[8 * 16];
        private readonly byte[] _dualWh = new byte[8 * 16];
        private byte _dualNumRects;

        private readonly List<byte> _multiRects = [];
        private byte _multiBpp;
        private byte _multiNumRects;

        public string Name => "Hextile<quint16>";

        public void Write()
        {
            SimSocket socket = sim._client!;
            DirtyMap map = sim._dirty;
            if (!sim.EnsurePixelFormat("QRfbHextileEncoder::write — clientBytesPerPixel (S1:1792)")) return;
            int bytesPerPixel = sim.ClientBytesPerPixel;
            int numDirty = map.NumDirty;
            socket.Write([0, 0]);
            socket.Write([(byte)(numDirty >> 8), (byte)numDirty]);
            if (numDirty <= 0) return;
            _newBg = true;
            _newFg = true;
            long start = socket.PendingLength;
            int rx = 0, ry = 0, rw = TileSize, rh = TileSize;
            int tiles = 0, single = 0, dual = 0, multi = 0, raw = 0;
            bool blended = false;
            for (int y = 0; y < map.MapHeight; ++y)
            {
                if (ry + TileSize > sim.Height) rh = sim.Height - ry;
                rw = TileSize;
                for (int x = 0; x < map.MapWidth; ++x)
                {
                    if (!map.Dirty(x, y)) continue;
                    map.SetClean(x, y);
                    rx = x * TileSize;
                    if (rx + TileSize > sim.Width) rw = sim.Width - rx;
                    byte[] header = new byte[12];
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), (ushort)rx);
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)ry);
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)rw);
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)rh);
                    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 5);
                    socket.Write(header);
                    ushort[] data = sim.TilePixels(rx, ry, rw, rh, ref blended);
                    tiles++;
                    if (SingleRead(data, rw, rh)) { SingleWrite(socket); single++; }
                    else if (DualRead(data, rw, rh)) { DualWrite(socket); dual++; }
                    else if (MultiRead(data, rw, rh)) { MultiWrite(socket); multi++; }
                    else if (sim.DoPixelConversion)
                    {
                        int bufferSize = (rw * rh * bytesPerPixel) + 1;
                        byte[] buffer = new byte[bufferSize + sim.ConvertedSize(rw)];
                        buffer[0] = 1; // Raw
                        for (int i = 0; i < rh; ++i)
                            sim.ConvertPixels(buffer.AsSpan(1 + (i * rw * bytesPerPixel)), data.AsSpan(i * rw, rw), rw);
                        socket.Write(buffer.AsSpan(0, bufferSize));
                        raw++;
                    }
                    else
                    {
                        socket.Write([1]);
                        for (int i = 0; i < rh; ++i)
                        {
                            byte[] row = new byte[rw * bytesPerPixel];
                            for (int k = 0; k < row.Length && (k / 2) < rw; k++) row[k] = (byte)(data[(i * rw) + (k / 2)] >> (8 * (k % 2)));
                            socket.Write(row);
                        }

                        raw++;
                    }
                }

                ry += TileSize;
            }

            if (blended) sim.Record(QtSimEventKind.KursorWtopionyWObraz, socket.Id, "kursor sprzętowy wtopiony w kafle (brak kursora klienta, S1:1834–1852)");
            sim._imageWrites++;
            sim.Record(QtSimEventKind.WyslanoObraz, socket.Id,
                $"Hextile: {tiles} kafli (1-kolor {single}, 2-kolor {dual}, wielokolor {multi}, raw {raw}), {socket.PendingLength - start + 4} B, format {sim._pixelFormat?.Describe() ?? "ZEROWANY (niezainicjowany)"}");
        }

        private bool SingleRead(ushort[] data, int width, int height)
        {
            if (width % 2 != 0) return false;
            if (data[0] != data[1]) return false;
            foreach (ushort p in data) if (p != data[0]) return false;
            ushort color = data[0];
            _newBg |= color != _bg;
            _bg = color;
            return true;
        }

        private void SingleWrite(SimSocket socket)
        {
            int bpp = sim.ClientBytesPerPixel;
            byte[] buffer = new byte[1 + Math.Max(bpp, sim.ConvertedSize(1))];
            buffer[0] = 2; // BackgroundSpecified
            sim.ConvertPixels(buffer.AsSpan(1), [_bg], 1);
            socket.Write(buffer.AsSpan(0, bpp + 1));
        }

        private bool DualRead(ushort[] data, int width, int height)
        {
            int ptr = 0, x = 0, y = 0, n1 = 0, n2 = 0;
            ushort c1 = data[0], c2 = 0;
            bool found = false;
            while (y < height && !found)
            {
                while (x < width)
                {
                    if (data[ptr] == c1) ++n1;
                    else { c2 = data[ptr]; found = true; break; }
                    ++ptr; ++x;
                }

                if (found) break;
                x = 0; ++y;
            }

            while (y < height)
            {
                while (x < width)
                {
                    if (data[ptr] == c1) ++n1;
                    else if (data[ptr] == c2) ++n2;
                    else return false;
                    ++ptr; ++x;
                }

                x = 0; ++y;
            }

            if (n2 > n1) (c1, c2) = (c2, c1);
            _newBg |= c1 != _bg;
            _newFg |= c2 != _fg;
            _bg = c1;
            _fg = c2;

            bool inRect = false;
            _dualNumRects = 0;
            ptr = 0;
            for (y = 0; y < height; ++y)
            {
                for (x = 0; x < width; ++x)
                {
                    if (inRect && data[ptr] == _bg)
                    {
                        SetWidth(_dualNumRects, x - RectX(_dualNumRects));
                        Next();
                        inRect = false;
                    }
                    else if (!inRect && data[ptr] == _fg)
                    {
                        SetX(_dualNumRects, x);
                        SetY(_dualNumRects, y);
                        SetHeight(_dualNumRects, 1);
                        inRect = true;
                    }

                    ++ptr;
                }

                if (inRect)
                {
                    SetWidth(_dualNumRects, width - RectX(_dualNumRects));
                    Next();
                    inRect = false;
                }
            }

            return true;
        }

        private int RectX(int r) => _dualXy[r] >> 4;
        private int RectY(int r) => _dualXy[r] & 0x0f;
        private int RectW(int r) => (_dualWh[r] >> 4) + 1;
        private void SetX(int r, int x) => _dualXy[r] = (byte)((x << 4) | (_dualXy[r] & 0x0f));
        private void SetY(int r, int y) => _dualXy[r] = (byte)((_dualXy[r] & 0xf0) | y);
        private void SetWidth(int r, int w) => _dualWh[r] = (byte)(((w - 1) << 4) | (_dualWh[r] & 0x0f));
        private void SetHeight(int r, int h) => _dualWh[r] = (byte)((_dualWh[r] & 0xf0) | (h - 1));

        private void Next()
        {
            int n = _dualNumRects;
            for (int r = n - 1; r >= 0; --r)
            {
                if (RectY(r) == RectY(n)) continue;
                if (RectY(r) < RectY(n) - 1) break;
                if (RectX(r) == RectX(n) && RectW(r) == RectW(n))
                {
                    ++_dualWh[r];
                    return;
                }
            }

            ++_dualNumRects;
        }

        private void DualWrite(SimSocket socket)
        {
            int bpp = sim.ClientBytesPerPixel;
            var buffer = new List<byte>();
            byte subenc = 0x8;
            var colours = new List<byte>();
            if (_newBg)
            {
                subenc |= 0x2;
                byte[] px = new byte[Math.Max(bpp, sim.ConvertedSize(1))];
                sim.ConvertPixels(px, [_bg], 1);
                colours.AddRange(px.AsSpan(0, bpp).ToArray());
            }

            if (_newFg)
            {
                subenc |= 0x4;
                byte[] px = new byte[Math.Max(bpp, sim.ConvertedSize(1))];
                sim.ConvertPixels(px, [_fg], 1);
                colours.AddRange(px.AsSpan(0, bpp).ToArray());
            }

            buffer.Add(subenc);
            buffer.AddRange(colours);
            buffer.Add(_dualNumRects);
            for (int r = 0; r < _dualNumRects; r++)
            {
                buffer.Add(_dualXy[r]);
                buffer.Add(_dualWh[r]);
            }

            socket.Write(buffer.ToArray());
        }

        private bool MultiRead(ushort[] data, int width, int height)
        {
            int ptr = 0;
            _multiBpp = (byte)sim.ClientBytesPerPixel;
            if (_newBg) _bg = data[0];
            ushort bg = _bg, color = bg;
            bool inRect = false;
            _multiNumRects = 0;
            _multiRects.Clear();
            int entry = _multiBpp + 2;
            int start = 0;
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                {
                    if (inRect && data[ptr] != color)
                    {
                        _multiRects[start + _multiBpp + 1] = (byte)(((x - (_multiRects[start + _multiBpp] >> 4) - 1) << 4) | 0); // setWidth + endRect(setHeight 1)
                        _multiNumRects++;
                        inRect = false;
                    }

                    if (!inRect && data[ptr] != bg)
                    {
                        if (_multiRects.Count + entry > 256) return false;
                        start = _multiRects.Count;
                        for (int k = 0; k < entry; k++) _multiRects.Add(0);
                        inRect = true;
                        color = data[ptr];
                        byte[] px = new byte[Math.Max(_multiBpp, sim.ConvertedSize(1))];
                        sim.ConvertPixels(px, [color], 1);
                        for (int k = 0; k < _multiBpp; k++) _multiRects[start + k] = px[k];
                        _multiRects[start + _multiBpp] = (byte)((x << 4) | y); // setX/setY
                    }

                    ++ptr;
                }

                if (inRect)
                {
                    _multiRects[start + _multiBpp + 1] = (byte)(((width - (_multiRects[start + _multiBpp] >> 4) - 1) << 4) | 0);
                    _multiNumRects++;
                    inRect = false;
                }
            }

            return true;
        }

        private void MultiWrite(SimSocket socket)
        {
            int bpp = _multiBpp;
            var buffer = new List<byte> { 8 | 16 };
            if (_newBg)
            {
                buffer[0] |= 0x2;
                byte[] px = new byte[Math.Max(bpp, sim.ConvertedSize(1))];
                sim.ConvertPixels(px, [_bg], 1);
                buffer.AddRange(px.AsSpan(0, bpp).ToArray());
            }

            buffer.Add(_multiNumRects);
            buffer.AddRange(_multiRects);
            socket.Write(buffer.ToArray());
        }
    }

    private sealed class RawEncoder(Qt48VncServerSimulator sim) : IEncoder
    {
        public string Name => "Raw";

        public void Write()
        {
            SimSocket socket = sim._client!;
            DirtyMap map = sim._dirty;
            if (!sim.EnsurePixelFormat("QRfbRawEncoder::write — clientBytesPerPixel (S1:1905)")) return;
            if (sim.ScreenDepth != 16)
            {
                sim.Record(QtSimEventKind.BladSymulatora, socket.Id, "ograniczenie modelu: kodowanie ekranu innego niż 16-bitowy nie jest symulowane");
                return;
            }

            int bytesPerPixel = sim.ClientBytesPerPixel;
            var bands = new List<(int X, int Y, int W, int H)>();
            for (int y = 0; y < map.MapHeight; ++y)
            {
                for (int x = 0; x < map.MapWidth; ++x)
                {
                    if (!map.Dirty(x, y)) continue;
                    int x0 = x;
                    while (x < map.MapWidth && map.Dirty(x, y)) { map.SetClean(x, y); x++; }
                    int px = x0 * TileSize, py = y * TileSize;
                    int pw = Math.Min((x - x0) * TileSize, sim.Width - px), ph = Math.Min(TileSize, sim.Height - py);
                    bands.Add((px, py, pw, ph));
                }
            }

            socket.Write([0, 0]);
            socket.Write([(byte)(bands.Count >> 8), (byte)bands.Count]);
            if (bands.Count <= 0) return;
            bool blended = false;
            long start = socket.PendingLength;
            foreach ((int x, int y, int w, int h) in bands)
            {
                byte[] header = new byte[12];
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), (ushort)x);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)y);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)w);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)h);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 0);
                socket.Write(header);
                ushort[] data = sim.TilePixels(x, y, w, h, ref blended);
                if (sim.DoPixelConversion)
                {
                    byte[] buffer = new byte[(w * h * bytesPerPixel) + sim.ConvertedSize(w)];
                    for (int i = 0; i < h; ++i) sim.ConvertPixels(buffer.AsSpan(i * w * bytesPerPixel), data.AsSpan(i * w, w), w);
                    socket.Write(buffer.AsSpan(0, w * h * bytesPerPixel));
                }
                else
                {
                    for (int i = 0; i < h; ++i)
                    {
                        byte[] row = new byte[w * bytesPerPixel];
                        for (int k = 0; k < row.Length && (k / 2) < w; k++) row[k] = (byte)(data[(i * w) + (k / 2)] >> (8 * (k % 2)));
                        socket.Write(row);
                    }
                }
            }

            if (blended) sim.Record(QtSimEventKind.KursorWtopionyWObraz, socket.Id, "kursor sprzętowy wtopiony (koder Raw, S1:1955–1972)");
            sim._imageWrites++;
            sim.Record(QtSimEventKind.WyslanoObraz, socket.Id, $"Raw: {bands.Count} prostokątów, {socket.PendingLength - start + 4} B");
        }
    }


    private sealed class SimSocket(int id, TcpClient tcp)
    {
        private readonly List<byte> _readBuffer = [];
        private readonly MemoryStream _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private int _closed;

        public int Id { get; } = id;

        public NetworkStream Stream { get; } = tcp.GetStream();

        public Channel<byte[]> Outgoing { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        public CancellationToken Token => _cts.Token;

        /// <summary><c>QAbstractSocket::state() == ConnectedState</c>.</summary>
        public bool QtConnected { get; set; } = true;

        public bool Deleted { get; set; }

        public int BytesAvailable => _readBuffer.Count;

        public long PendingLength => _pending.Length;

        public void Append(byte[] data) => _readBuffer.AddRange(data);

        /// <summary><c>QIODevice::read</c>: najwyżej tyle, ile jest.</summary>
        public byte[] Read(int count)
        {
            int n = Math.Min(count, _readBuffer.Count);
            byte[] result = _readBuffer.GetRange(0, n).ToArray();
            _readBuffer.RemoveRange(0, n);
            return result;
        }

        public void Write(ReadOnlySpan<byte> data) => _pending.Write(data);

        /// <summary>Koniec iteracji pętli: dane z <c>writeBuffer</c> idą do jądra.</summary>
        public void Flush()
        {
            if (_pending.Length == 0) return;
            if (Volatile.Read(ref _closed) == 0) Outgoing.Writer.TryWrite(_pending.ToArray());
            _pending.SetLength(0);
        }

        public void DiscardPending() => _pending.SetLength(0);

        public void CloseTcp()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { tcp.Close(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
    }
}
