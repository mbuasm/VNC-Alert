using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>
/// Konfigurowalny fałszywy serwer RFB (RFC 6143) na 127.0.0.1 i losowym porcie. Obsługuje wielu
/// klientów równolegle (jedno zadanie na połączenie), pełny handshake 3.3/3.7/3.8, VncAuth
/// (weryfikowany referencyjnym DES-em) i odpowiedzi <c>FramebufferUpdate</c> w kodowaniach
/// Raw / Hextile / CopyRect. Rejestruje wszystko, co przysłał klient — w szczególności typy
/// wiadomości, co jest podstawową asercją bezpieczeństwa („klient tylko do odczytu”).
/// </summary>
public sealed class FakeRfbServer : IAsyncDisposable
{
    private const int DesktopSizePseudoEncoding = -223;

    private static readonly byte[] DefaultChallenge =
        [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xF0, 0x01];

    private readonly FakeRfbServerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _gate = new();
    private readonly List<FakeClientSession> _sessions = [];
    private readonly List<(int Index, TaskCompletionSource<FakeClientSession> Tcs)> _waiters = [];
    private readonly ConcurrentQueue<byte> _allMessageTypes = new();
    private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();
    private readonly int _minor;

    private RgbaImage _screen;
    private int _screenVersion;

    public FakeRfbServer(FakeRfbServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _screen = options.Screen;
        _minor = options.ProtocolVersion switch
        {
            "3.3" => 3,
            "3.7" => 7,
            "3.8" => 8,
            _ => throw new ArgumentException($"Nieobsługiwana wersja protokołu: {options.ProtocolVersion}.", nameof(options)),
        };

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>Port TCP, na którym nasłuchuje serwer (przydzielony przez system).</summary>
    public int Port { get; }

    /// <summary>Liczba połączeń zaakceptowanych od startu serwera.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Wszystkie sesje klientów w kolejności podłączenia.</summary>
    public IReadOnlyList<FakeClientSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToArray();
            }
        }
    }

    /// <summary>Typy wszystkich wiadomości klientów odebranych przez serwer (ze wszystkich połączeń).</summary>
    public IReadOnlyList<byte> AllReceivedMessageTypes => _allMessageTypes.ToArray();

    /// <summary>Obraz „ekranu maszyny”. Podmiana w trakcie testu jest widoczna w kolejnych aktualizacjach.</summary>
    public RgbaImage Screen
    {
        get => Volatile.Read(ref _screen);
        set
        {
            Volatile.Write(ref _screen, value ?? throw new ArgumentNullException(nameof(value)));
            Interlocked.Increment(ref _screenVersion);
        }
    }

    /// <summary>Szerokość ekranu ogłaszana w <c>ServerInit</c>.</summary>
    public int Width => _options.Width ?? _options.Screen.Width;

    /// <summary>Wysokość ekranu ogłaszana w <c>ServerInit</c>.</summary>
    public int Height => _options.Height ?? _options.Screen.Height;

    /// <summary>Czeka na sesję o podanym indeksie (0 = pierwsze połączenie).</summary>
    public Task<FakeClientSession> WaitForSessionAsync(int index, TimeSpan timeout)
    {
        TaskCompletionSource<FakeClientSession> tcs;
        lock (_gate)
        {
            if (index < _sessions.Count)
            {
                return Task.FromResult(_sessions[index]);
            }

            tcs = new TaskCompletionSource<FakeClientSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((index, tcs));
        }

        return tcs.Task.WaitAsync(timeout);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // Ignorujemy — sprzątanie testowe.
        }

        foreach (FakeClientSession session in Sessions)
        {
            session.Close();
        }

        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignorujemy — sprzątanie testowe.
        }

        try
        {
            await Task.WhenAll(_clientTasks.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignorujemy — sprzątanie testowe.
        }

        _cts.Dispose();
    }

    // ---- Pętla akceptacji -----------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                tcp.NoDelay = true;

                FakeClientSession? session = null;
                FakeClientSession? toKick = null;

                lock (_gate)
                {
                    if (_sessions.Count(s => !s.IsClosed) >= _options.MaxClients)
                    {
                        // Za dużo klientów — zamykamy natychmiast (poza lockiem).
                    }
                    else
                    {
                        if (_options.KickPreviousClientOnNewConnection)
                        {
                            toKick = _sessions.LastOrDefault(s => !s.IsClosed);
                        }

                        session = new FakeClientSession(_sessions.Count, tcp);
                        _sessions.Add(session);
                        CompleteWaitersLocked();
                    }
                }

                if (session is null)
                {
                    tcp.Dispose();
                    continue;
                }

                toKick?.Close();

                FakeClientSession accepted = session;
                Task clientTask = Task.Run(() => HandleClientAsync(accepted, ct), CancellationToken.None);
                _clientTasks[clientTask] = 0;
            }
        }
        catch (OperationCanceledException)
        {
            // Normalne zakończenie serwera.
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Listener zamknięty w DisposeAsync.
        }
    }

    private void CompleteWaitersLocked()
    {
        for (int i = _waiters.Count - 1; i >= 0; i--)
        {
            (int index, TaskCompletionSource<FakeClientSession> tcs) = _waiters[i];
            if (index < _sessions.Count)
            {
                tcs.TrySetResult(_sessions[index]);
                _waiters.RemoveAt(i);
            }
        }
    }

    // ---- Obsługa jednego klienta ----------------------------------------------------------------

    private async Task HandleClientAsync(FakeClientSession session, CancellationToken ct)
    {
        try
        {
            using TcpClient tcp = session.Tcp;
            await using NetworkStream stream = tcp.GetStream();

            // Po RSA-AES dalsza część sesji może iść przez INNY strumień (szyfrowany), więc handshake
            // zwraca strumień do dalszej rozmowy, a nie samo „udało się / nie udało”.
            Stream? sessionStream = await HandshakeAsync(session, stream, ct).ConfigureAwait(false);
            if (sessionStream is null)
            {
                return;
            }

            await ReadClientInitAsync(session, sessionStream, ct).ConfigureAwait(false);
            await SendServerInitAsync(sessionStream, ct).ConfigureAwait(false);

            if (_options.SendColourMap)
            {
                await SendColourMapAsync(sessionStream, ct).ConfigureAwait(false);
            }

            await MessageLoopAsync(session, sessionStream, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Rozłączenie klienta / zamknięcie serwera — normalna sytuacja w testach.
        }
        finally
        {
            session.MarkClosed();
        }
    }

    /// <summary>
    /// Zwraca strumień do dalszej części sesji, gdy handshake zabezpieczeń się powiódł, albo
    /// <see langword="null"/>, gdy nie. Przy RA2/RA2_256 jest to strumień SZYFROWANY.
    /// </summary>
    private async Task<Stream?> HandshakeAsync(FakeClientSession session, Stream stream, CancellationToken ct)
    {
        if (_options.SilentUntilClientCloses)
        {
            // Odczyt kończy się dopiero rozłączeniem klienta (0 bajtów albo wyjątek) — sesja jest wtedy zamknięta.
            byte[] buffer = new byte[64];
            while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
            {
            }

            return null;
        }

        if (_options.DelayBeforeVersion > TimeSpan.Zero)
        {
            await Task.Delay(_options.DelayBeforeVersion, ct).ConfigureAwait(false);
        }

        string versionHeader = _options.RawVersionHeader ?? $"RFB 003.{_minor:000}\n";
        await WriteAsync(stream, Encoding.ASCII.GetBytes(versionHeader), ct).ConfigureAwait(false);

        byte[] clientVersion = await ReadExactlyAsync(stream, 12, ct).ConfigureAwait(false);
        session.ClientProtocolVersion = Encoding.ASCII.GetString(clientVersion);

        byte chosen;
        if (_minor == 3)
        {
            chosen = _options.SecurityTypes[0];
            byte[] typeBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(typeBytes, chosen);
            await WriteAsync(stream, typeBytes, ct).ConfigureAwait(false);
        }
        else
        {
            var list = new byte[1 + _options.SecurityTypes.Count];
            list[0] = (byte)_options.SecurityTypes.Count;
            for (int i = 0; i < _options.SecurityTypes.Count; i++)
            {
                list[i + 1] = _options.SecurityTypes[i];
            }

            await WriteAsync(stream, list, ct).ConfigureAwait(false);
            chosen = (await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false))[0];
        }

        session.ChosenSecurityType = chosen;

        bool ok = true;
        Stream sessionStream = stream;
        bool rsaAes = chosen is 5 or 6 or 129 or 130;
        if (chosen == (byte)RfbSecurityType.VncAuth)
        {
            ok = await DoVncAuthAsync(stream, ct).ConfigureAwait(false);
        }
        else if (rsaAes)
        {
            FakeRsaAesOutcome outcome = await FakeRsaAesServer
                .AuthenticateAsync(stream, chosen, _options.RsaAes, ct).ConfigureAwait(false);
            if (outcome.Stream is null)
            {
                return null; // sam handshake się nie udał — tu prawdziwy serwer też nie ma czym odpowiedzieć
            }

            // Złe poświadczenia: sesja idzie dalej tylko po to, żeby odesłać SecurityResult
            // z niepowodzeniem — tak jak robi to prawdziwy serwer.
            sessionStream = outcome.Stream;
            ok = outcome.CredentialsOk;
        }
        else if (chosen != (byte)RfbSecurityType.None)
        {
            // Typ nieobsługiwany przez klienta — klient i tak zerwie połączenie.
            return null;
        }

        // SecurityResult po RSA-AES idzie tym samym strumieniem, którym pójdzie reszta sesji.
        bool sendResult = _minor == 8 || rsaAes || (_minor == 3 && chosen == (byte)RfbSecurityType.VncAuth) ||
                          (_minor == 7 && chosen == (byte)RfbSecurityType.VncAuth);
        if (sendResult)
        {
            await SendSecurityResultAsync(sessionStream, ok, ct).ConfigureAwait(false);
        }

        return ok ? sessionStream : null;
    }

    private async Task<bool> DoVncAuthAsync(Stream stream, CancellationToken ct)
    {
        byte[] challenge = _options.FixedChallenge ?? DefaultChallenge;
        await WriteAsync(stream, challenge, ct).ConfigureAwait(false);

        byte[] response = await ReadExactlyAsync(stream, 16, ct).ConfigureAwait(false);
        if (_options.RejectVncAuth)
        {
            return false;
        }

        byte[] expected = ReferenceVncDes.EncryptChallenge(challenge, _options.Password);
        return response.AsSpan().SequenceEqual(expected);
    }

    private async Task SendSecurityResultAsync(Stream stream, bool ok, CancellationToken ct)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, ok ? 0u : 1u);
        await WriteAsync(stream, result, ct).ConfigureAwait(false);

        if (!ok && _minor == 8)
        {
            byte[] reason = Encoding.UTF8.GetBytes("Authentication failed");
            var lengthPrefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)reason.Length);
            await WriteAsync(stream, lengthPrefix, ct).ConfigureAwait(false);
            await WriteAsync(stream, reason, ct).ConfigureAwait(false);
        }
    }

    private static async Task ReadClientInitAsync(FakeClientSession session, Stream stream, CancellationToken ct)
    {
        byte[] clientInit = await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false);
        session.Shared = clientInit[0] != 0;
    }

    private async Task SendServerInitAsync(Stream stream, CancellationToken ct)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(_options.DesktopName);
        var msg = new byte[4 + 16 + 4 + nameBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(0, 2), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), (ushort)Height);
        _options.PixelFormat.Write(msg.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(20, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(msg.AsSpan(24));

        await WriteAsync(stream, msg, ct).ConfigureAwait(false);
    }

    private async Task SendColourMapAsync(Stream stream, CancellationToken ct)
    {
        IReadOnlyList<Rgba> palette = _options.Palette
            ?? throw new InvalidOperationException("SendColourMap wymaga ustawionej palety.");

        var msg = new byte[6 + (palette.Count * 6)];
        msg[0] = 1; // SetColourMapEntries
        msg[1] = 0; // padding
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), (ushort)palette.Count);
        for (int i = 0; i < palette.Count; i++)
        {
            int o = 6 + (i * 6);
            // 8-bitowa wartość * 257 daje dokładne odwzorowanie w 16 bitach (0 → 0, 255 → 65535).
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(o, 2), (ushort)(palette[i].R * 257));
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(o + 2, 2), (ushort)(palette[i].G * 257));
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(o + 4, 2), (ushort)(palette[i].B * 257));
        }

        await WriteAsync(stream, msg, ct).ConfigureAwait(false);
    }

    // ---- Pętla wiadomości klienta ---------------------------------------------------------------

    private async Task MessageLoopAsync(FakeClientSession session, Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte type = (await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false))[0];
            session.RecordMessageType(type);
            _allMessageTypes.Enqueue(type);

            switch (type)
            {
                case 0: // SetPixelFormat — klient tylko do odczytu NIGDY tego nie wysyła.
                    await ReadExactlyAsync(stream, 19, ct).ConfigureAwait(false);
                    break;

                case 2: // SetEncodings
                {
                    byte[] header = await ReadExactlyAsync(stream, 3, ct).ConfigureAwait(false);
                    int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1, 2));
                    byte[] body = await ReadExactlyAsync(stream, count * 4, ct).ConfigureAwait(false);
                    var encodings = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        encodings[i] = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(i * 4, 4));
                    }

                    session.SetEncodings(encodings);
                    break;
                }

                case 3: // FramebufferUpdateRequest
                {
                    byte[] body = await ReadExactlyAsync(stream, 9, ct).ConfigureAwait(false);
                    bool incremental = body[0] != 0;
                    int x = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
                    int y = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3, 2));
                    int w = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(5, 2));
                    int h = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(7, 2));
                    session.RecordUpdateRequest(new FakeUpdateRequest(x, y, w, h, incremental));
                    if (_options.DelayBeforeUpdate > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.DelayBeforeUpdate, ct).ConfigureAwait(false);
                    }
                    await session.WriteGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await SendUpdateAsync(session, stream, new RectInt(x, y, w, h), incremental, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        session.WriteGate.Release();
                    }

                    break;
                }

                case 4: // KeyEvent
                    await ReadExactlyAsync(stream, 7, ct).ConfigureAwait(false);
                    break;

                case 5: // PointerEvent
                    await ReadExactlyAsync(stream, 5, ct).ConfigureAwait(false);
                    break;

                case 6: // ClientCutText
                {
                    byte[] header = await ReadExactlyAsync(stream, 7, ct).ConfigureAwait(false);
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3, 4));
                    await ReadExactlyAsync(stream, (int)length, ct).ConfigureAwait(false);
                    break;
                }

                default:
                    throw new InvalidOperationException($"Nieznany typ wiadomości klienta: {type}.");
            }
        }
    }

    // ---- FramebufferUpdate ------------------------------------------------------------------------

    private async Task SendUpdateAsync(
        FakeClientSession session,
        Stream stream,
        RectInt requested,
        bool incremental,
        CancellationToken ct)
    {
        RgbaImage screen = Screen;
        int version = Volatile.Read(ref _screenVersion);

        if (incremental && _options.IncrementalNoResponseWhenUnchanged && session.LastSentScreenVersion == version)
        {
            // Prawdziwe zachowanie WinCE/OpenCnc: dosłownie NIC nie wysyłamy, dopóki ekran się nie zmieni
            // — w odróżnieniu od IncrementalSendsNothingWhenUnchanged poniżej, który odsyła
            // poprawną, ale pustą odpowiedź. Pętla komunikatów po prostu czeka na kolejne żądanie klienta.
            if (_options.DeliverPendingIncrementalOnScreenChange)
            {
                StartPendingDelivery(session, stream, requested, version, ct);
            }

            return;
        }

        if (_options.SendUnknownServerMessageType is byte unknownType)
        {
            await WriteAsync(stream, [unknownType], ct).ConfigureAwait(false);
        }

        if (_options.SendBellBeforeUpdate)
        {
            await WriteAsync(stream, [2], ct).ConfigureAwait(false);
        }

        if (_options.SendServerCutTextBeforeUpdate)
        {
            byte[] text = Encoding.Latin1.GetBytes(_options.ServerCutText);
            var cut = new byte[8 + text.Length];
            cut[0] = 3;
            BinaryPrimitives.WriteUInt32BigEndian(cut.AsSpan(4, 4), (uint)text.Length);
            text.CopyTo(cut.AsSpan(8));
            await WriteAsync(stream, cut, ct).ConfigureAwait(false);
        }

        if (incremental && _options.IncrementalSendsNothingWhenUnchanged && session.LastSentScreenVersion == version)
        {
            await WriteAsync(stream, BuildUpdateHeader(0), ct).ConfigureAwait(false);
            return;
        }

        RectInt rect = requested.Clamp(screen.Width, screen.Height);
        using var payload = new MemoryStream();

        if (_options.OverrideRectEncoding is int forcedEncoding)
        {
            payload.Write(BuildUpdateHeader(1));
            payload.Write(BuildRectHeader(rect, forcedEncoding));
            await WriteAsync(stream, payload.ToArray(), ct).ConfigureAwait(false);
            return;
        }

        bool announceDesktopSize = _options.AnnounceDesktopSize && !session.DesktopSizeAnnounced;

        if (_options.Encoding == FakeEncodingMode.CopyRectHalves)
        {
            int halfWidth = screen.Width / 2;
            var left = new RectInt(0, 0, halfWidth, screen.Height);
            var right = new RectInt(halfWidth, 0, screen.Width - halfWidth, screen.Height);

            payload.Write(BuildUpdateHeader(2));
            payload.Write(BuildRectHeader(left, RfbEncodings.Raw));
            await MaybeCloseAfterHeaderAsync(stream, payload, ct).ConfigureAwait(false);

            NewPixelEncoder().WriteRawRect(screen, left, payload);

            payload.Write(BuildRectHeader(right, RfbEncodings.CopyRect));
            var copySource = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(copySource.AsSpan(0, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(copySource.AsSpan(2, 2), 0);
            payload.Write(copySource);
        }
        else
        {
            int encoding = _options.Encoding == FakeEncodingMode.Hextile ? RfbEncodings.Hextile : RfbEncodings.Raw;
            payload.Write(BuildUpdateHeader(announceDesktopSize ? 2 : 1));
            if (announceDesktopSize)
            {
                int announcedWidth = _options.AnnouncedDesktopWidth ?? screen.Width;
                int announcedHeight = _options.AnnouncedDesktopHeight ?? screen.Height;
                payload.Write(BuildRectHeader(new RectInt(0, 0, announcedWidth, announcedHeight), DesktopSizePseudoEncoding));
                session.DesktopSizeAnnounced = true;
            }

            payload.Write(BuildRectHeader(rect, encoding));
            await MaybeCloseAfterHeaderAsync(stream, payload, ct).ConfigureAwait(false);

            PixelEncoder pixels = NewPixelEncoder();
            if (encoding == RfbEncodings.Hextile)
            {
                var hextile = new HextileEncoder(pixels, _options.HextileUseStickyBackground, _options.HextileUseColouredSubrects);
                hextile.Encode(screen, rect, payload, session.RecordHextileSubencoding);
            }
            else
            {
                pixels.WriteRawRect(screen, rect, payload);
            }
        }

        await WriteAsync(stream, payload.ToArray(), ct).ConfigureAwait(false);
        session.LastSentScreenVersion = version;
    }

    /// <summary>
    /// Żądanie, na które serwer nie odpowiedział, zostaje „u serwera” (RFC 6143 §7.5.3): to zadanie czeka
    /// na zmianę <see cref="Screen"/> i dopiero wtedy odsyła zaległą aktualizację — bez kolejnego żądania
    /// klienta. Dokładnie na tym opiera się <c>RfbClient.TryCaptureAsync</c>.
    /// </summary>
    private void StartPendingDelivery(
        FakeClientSession session,
        Stream stream,
        RectInt requested,
        int versionAtRequest,
        CancellationToken ct)
    {
        if (Interlocked.Exchange(ref session.PendingDeliveryStarted, 1) != 0)
        {
            return;
        }

        Task delivery = Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && !session.IsClosed)
                    {
                        if (Volatile.Read(ref _screenVersion) != versionAtRequest)
                        {
                            await session.WriteGate.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                if (!session.IsClosed)
                                {
                                    await SendUpdateAsync(session, stream, requested, incremental: false, ct).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                session.WriteGate.Release();
                            }

                            return;
                        }

                        await Task.Delay(10, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // Rozłączenie klienta / zamknięcie serwera — normalna sytuacja w testach.
                }
                finally
                {
                    Volatile.Write(ref session.PendingDeliveryStarted, 0);
                }
            },
            CancellationToken.None);

        _clientTasks[delivery] = 0;
    }

    /// <summary>
    /// Tryb awarii <c>CloseAfterUpdateHeader</c>: wysyła to, co do tej pory zbudowano (same nagłówki)
    /// i zrywa połączenie w środku <c>FramebufferUpdate</c>, przed danymi pikseli.
    /// </summary>
    private async Task MaybeCloseAfterHeaderAsync(Stream stream, MemoryStream payload, CancellationToken ct)
    {
        if (!_options.CloseAfterUpdateHeader)
        {
            return;
        }

        await WriteAsync(stream, payload.ToArray(), ct).ConfigureAwait(false);
        throw new IOException("FakeRfbServer: celowe zerwanie połączenia w środku FramebufferUpdate.");
    }

    private PixelEncoder NewPixelEncoder() => new(_options.PixelFormat, _options.Palette);

    private static byte[] BuildUpdateHeader(int rectangleCount)
    {
        var header = new byte[4];
        header[0] = 0; // FramebufferUpdate
        header[1] = 0; // padding
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)rectangleCount);
        return header;
    }

    private static byte[] BuildRectHeader(RectInt rect, int encoding)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), (ushort)rect.X);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)rect.Y);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), (ushort)rect.Width);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)rect.Height);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), encoding);
        return header;
    }

    // ---- Niskopoziomowe IO -------------------------------------------------------------------------

    private static async Task WriteAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Klient zamknął połączenie.");
            }

            total += read;
        }

        return buffer;
    }
}
