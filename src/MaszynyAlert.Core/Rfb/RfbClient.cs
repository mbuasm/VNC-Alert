using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using MaszynyAlert.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Rfb;

public sealed class RfbClient : IAsyncDisposable
{
    private const byte MsgClientSetPixelFormat = 0;
    private const byte MsgClientSetEncodings = 2;
    private const byte MsgClientFramebufferUpdateRequest = 3;

    private const byte MsgServerFramebufferUpdate = 0;
    private const byte MsgServerSetColourMapEntries = 1;
    private const byte MsgServerBell = 2;
    private const byte MsgServerCutText = 3;

    private const int DesktopSizePseudoEncoding = -223;

    private const int CursorPseudoEncoding = -239;

    private const int MaxCursorDimension = 1024;

    private bool _cursorRequested;

    /// <summary>
    /// Maksymalny dopuszczalny rozmiar ekranu (w każdym wymiarze) zgłaszany przez serwer w <c>ServerInit</c>
    /// albo pseudo-kodowaniu DesktopSize. Prawdziwe maszyny mają ekrany rzędu 1024×600 /
    /// 1280×800; wartość znacznie ponad to (np. 40000×40000 od uszkodzonego/złośliwego serwera) prowadziłaby
    /// do alokacji rzędu gigabajtów albo przepełnienia arytmetyki `int` przy liczeniu rozmiaru bufora pikseli.
    /// </summary>
    private const int MaxScreenDimension = 8192;
    private const long MaxScreenPixels = 4096L * 4096; // 64 MiB bufora BGRA

    /// <summary>Odstęp między kolejnymi sprawdzeniami <c>NetworkStream.DataAvailable</c> w <see cref="TryReceiveUpdateAsync"/>.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(25);

    private readonly RfbClientOptions _options;
    private readonly ILogger<RfbClient> _logger;
    private readonly string _endpointDescription;

    private TcpClient? _tcp;
    private NetworkStream? _stream;

    private Stream? _transport;

    /// <summary>Niepuste tylko przy sesji szyfrowanej RSA-AES — patrz <see cref="HasPendingBytes"/>.</summary>
    private RsaAesStream? _rsaAes;

    /// <summary>Wariant poświadczeń zażądany przez serwer RSA-AES (1 = nazwa użytkownika i hasło, 2 = samo hasło); null przy VncAuth/None.</summary>
    private byte? _rsaAesCredentialSubtype;

    /// <summary>Prawda, gdy serwer RSA-AES zażądał nazwy użytkownika, a wysłano pustą (nie było jej w konfiguracji).</summary>
    private bool _rsaAesSentEmptyUsername;

    /// <summary>Surowy nagłówek wersji serwera (patrz <see cref="RfbServerInfo.RawVersionHeader"/>).</summary>
    private string? _rawVersionHeader;

    /// <summary>Odcisk klucza RSA serwera z ostatniego uwierzytelnienia RSA-AES (patrz <see cref="RfbServerInfo.ServerKeySha256"/>).</summary>
    private string? _rsaAesServerKeySha256;

    private RfbBinaryReader? _reader;
    private RfbColourMap? _colourMap;
    private bool _connectCalled;

    /// <summary>
    /// Prawda, gdy wysłano <c>FramebufferUpdateRequest</c>, na które nie przyszła jeszcze odpowiedź —
    /// <see cref="TryCaptureAsync"/> nie wysyła wtedy nowego żądania na ten sam ROI (tylko czeka dalej),
    /// żeby nie zalewać serwera WinCE żądaniami co sekundę, gdy ekran się nie zmienia.
    /// </summary>
    private bool _updatePending;
    private RectInt _pendingRoi;
    private List<RectInt>? _pendingCoverage;

    public RfbClient(RfbClientOptions options, ILogger<RfbClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("RfbClientOptions.Host nie może być puste.", nameof(options));
        }

        _options = options;
        _logger = logger ?? NullLogger<RfbClient>.Instance;
        _endpointDescription = $"{options.Host}:{options.Port}";
        Framebuffer = new Framebuffer(0, 0);
    }

    /// <summary>Informacje o serwerze zebrane podczas <see cref="ConnectAsync"/> (null przed połączeniem).</summary>
    public RfbServerInfo? ServerInfo { get; private set; }

    /// <summary>Prawda, gdy handshake zakończył się sukcesem i połączenie jest aktywne.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Bufor ramki. Przed <see cref="ConnectAsync"/> ma rozmiar 0x0.</summary>
    public Framebuffer Framebuffer { get; private set; }

    public async Task<RfbServerInfo> ConnectAsync(CancellationToken ct)
    {
        if (_connectCalled)
        {
            throw new InvalidOperationException("ConnectAsync zostało już wywołane na tej instancji RfbClient — utwórz nową instancję.");
        }

        _connectCalled = true;

        _tcp = new TcpClient();
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(_options.ConnectTimeout);
                try
                {
                    await _tcp.ConnectAsync(_options.Host, _options.Port, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new RfbException(
                        $"Nie udało się połączyć z {_endpointDescription} w ciągu {_options.ConnectTimeout.TotalSeconds:0}s (limit czasu połączenia).");
                }
                catch (SocketException ex)
                {
                    throw new RfbException($"Nie udało się połączyć z {_endpointDescription}: {ex.Message}", ex);
                }
            }

            _stream = _tcp.GetStream();
            _transport = _stream;
            _reader = new RfbBinaryReader(_stream, _options.ReadTimeout, _endpointDescription);

            int chosenMinor = await NegotiateProtocolVersionAsync(ct).ConfigureAwait(false);
            (IReadOnlyList<byte> offered, RfbSecurityType used) = await NegotiateSecurityAsync(chosenMinor, ct).ConfigureAwait(false);

            await SendClientInitAsync(ct).ConfigureAwait(false);
            (int width, int height, RfbPixelFormat format, string name) = await ReadServerInitAsync(ct).ConfigureAwait(false);

            Framebuffer = new Framebuffer(width, height);
            if (!format.TrueColour)
            {
                _colourMap = new RfbColourMap();
            }

            string protocolVersion = $"3.{chosenMinor}";
            ServerInfo = new RfbServerInfo(protocolVersion, offered, used, width, height, format, name)
            {
                RsaAesCredentialSubtype = _rsaAesCredentialSubtype,
                RawVersionHeader = _rawVersionHeader,
                ServerKeySha256 = _rsaAesServerKeySha256,
            };

            _logger.LogDebug(
                "RFB {Version}: typy zabezpieczeń oferowane [{Offered}], użyto {Used}, format {Format}",
                protocolVersion, string.Join(",", offered), used, format.Describe());

            bool qt = QtEmbeddedVncServer.IsQtEmbedded(name);
            if (qt && format.TrueColour)
            {
                await SendSetPixelFormatFromServerInitAsync(ct).ConfigureAwait(false);
            }

            await SendSetEncodingsAsync(qt, ct).ConfigureAwait(false);

            IsConnected = true;
            _logger.LogInformation(
                "Połączono z {Endpoint} (\"{Name}\", {Width}x{Height}, RFB {Version}, {Auth})",
                _endpointDescription, name, width, height, protocolVersion, used);

            return ServerInfo;
        }
        catch (RfbException)
        {
            IsConnected = false;
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            IsConnected = false;
            throw new RfbException($"Połączenie z {_endpointDescription} przerwane: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Wysyła <c>FramebufferUpdateRequest</c> (typ 3) dla podanego obszaru (przycinanego do rozmiaru
    /// ekranu serwera). Gdy obszar po przycięciu jest pusty (ROI leży całkowicie poza ekranem), rzuca
    /// <see cref="ArgumentException"/> PRZED wysłaniem czegokolwiek do serwera — patrz
    /// <see cref="ThrowIfClampedRoiEmpty"/>.
    /// </summary>
    public async Task RequestUpdateAsync(RectInt rect, bool incremental, CancellationToken ct)
    {
        EnsureConnected();

        RectInt clamped = ThrowIfClampedRoiEmpty(rect);
        if (_updatePending && _pendingRoi != clamped) incremental = false;
        byte[] msg = new byte[10];
        msg[0] = MsgClientFramebufferUpdateRequest;
        msg[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), (ushort)clamped.X);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), (ushort)clamped.Y);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6, 2), (ushort)clamped.Width);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8, 2), (ushort)clamped.Height);

        await SendAsync(msg, ct).ConfigureAwait(false);
        _updatePending = true;
        _pendingRoi = clamped;
        _pendingCoverage = incremental ? null : [clamped];
    }

    /// <summary>
    /// Czyta komunikaty serwera, aż do odebrania pełnego <c>FramebufferUpdate</c> (typ 0), który
    /// zwraca. Po drodze obsługuje (i odrzuca/zapisuje) <c>SetColourMapEntries</c> (1), <c>Bell</c> (2)
    /// i <c>ServerCutText</c> (3). Nieznany typ komunikatu → <see cref="RfbProtocolException"/>.
    /// </summary>
    public async Task<FramebufferUpdate> ReceiveUpdateAsync(CancellationToken ct) =>
        (await ReceiveUpdateCoreAsync(null, ct).ConfigureAwait(false))!;

    private async Task<FramebufferUpdate?> ReceiveUpdateCoreAsync(TimeSpan? idleTimeout, CancellationToken ct)
    {
        EnsureConnected();
        long started = Environment.TickCount64;

        try
        {
            while (true)
            {
                if (idleTimeout is { } idle && !await WaitForResponseDataAsync(
                    TimeSpan.FromMilliseconds(Math.Max(0, idle.TotalMilliseconds - (Environment.TickCount64 - started))), ct).ConfigureAwait(false))
                    return null;
                byte msgType = await _reader!.ReadByteAsync(ct).ConfigureAwait(false);
                using var messageDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                messageDeadline.CancelAfter(_options.ReadTimeout);
                CancellationToken mt = messageDeadline.Token;
                try
                {
                    switch (msgType)
                    {
                        case MsgServerFramebufferUpdate:
                            FramebufferUpdate update = await ReadFramebufferUpdateAsync(mt).ConfigureAwait(false);
                            UpdateCoverage(update);
                            _updatePending = _pendingCoverage is { Count: > 0 };
                            if (_updatePending && update.Rectangles.Count == 0 && update.EncodingsUsed.Contains(CursorPseudoEncoding))
                            {
                                await RequestUpdateAsync(_pendingRoi, incremental: false, mt).ConfigureAwait(false);
                            }

                            return update;
                        case MsgServerSetColourMapEntries:
                            await ReadSetColourMapEntriesAsync(mt).ConfigureAwait(false);
                            break;
                        case MsgServerBell:
                            // Brak dodatkowych danych — ignorujemy (nie mamy dźwięku "dzwonka" VNC).
                            break;
                        case MsgServerCutText:
                            await ReadServerCutTextAsync(mt).ConfigureAwait(false);
                            break;
                        default:
                            throw new RfbProtocolException($"Nieznany typ komunikatu serwera RFB: {msgType}.");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    IsConnected = false;
                    throw new RfbException(
                        $"Przekroczono limit {_options.ReadTimeout.TotalSeconds:0}s odczytu komunikatu {_endpointDescription} — serwer podaje dane zbyt wolno.");
                }
            }
        }
        catch (RfbException)
        {
            IsConnected = false;
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            IsConnected = false;
            throw new RfbException($"Połączenie z {_endpointDescription} przerwane: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Jak <see cref="ReceiveUpdateAsync"/>, ale czekanie między komunikatami ma wspólny budżet
    /// <paramref name="idleTimeout"/>. Bell i inne komunikaty pomocnicze nie przedłużają czekania
    /// na klatkę. Brak klatki zwraca null bez zamykania połączenia; rozpoczęty komunikat jest zawsze
    /// doczytywany z twardym limitem <see cref="RfbClientOptions.ReadTimeout"/>.
    /// Serwery WinCE/OpenCnc potrafią nie odpowiadać na żądanie przyrostowe w ogóle, dopóki ekran się nie
    /// zmieni; żądanie zostaje u serwera (RFC 6143 — serwery scalają kolejne żądania), więc
    /// kolejne wywołanie tej metody (albo zwykłe <see cref="ReceiveUpdateAsync"/>) po prostu czeka dalej —
    /// nic nie trzeba wysyłać ponownie.
    /// </summary>
    public async Task<FramebufferUpdate?> TryReceiveUpdateAsync(TimeSpan idleTimeout, CancellationToken ct)
    {
        return await ReceiveUpdateCoreAsync(idleTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Odpytuje <see cref="NetworkStream.DataAvailable"/> co <see cref="IdlePollInterval"/>, aż pojawią się
    /// jakiekolwiek bajty odpowiedzi albo upłynie <paramref name="idleTimeout"/>. Celowo NIE używa
    /// anulowania samego <c>ReadAsync</c> do implementacji limitu czasu (żeby nigdy nie zgubić/nie
    /// skonsumować bajtu należącego do właściwej odpowiedzi) — tylko sprawdza dostępność danych.
    /// </summary>
    private async Task<bool> WaitForResponseDataAsync(TimeSpan idleTimeout, CancellationToken ct)
    {
        NetworkStream stream = _stream
            ?? throw new InvalidOperationException("RfbClient nie jest połączony — wywołaj najpierw ConnectAsync.");

        long deadline = Environment.TickCount64 + Math.Max(0, (long)idleTimeout.TotalMilliseconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (HasPendingBytes(stream))
            {
                return true;
            }
            // FIN/RST może nie ustawić DataAvailable. Milczenie bez FIN nadal jest
            // dozwolone dla przyrostowego żądania na nieruchomym ekranie.
            if (_tcp!.Client.Poll(0, SelectMode.SelectRead) && _tcp.Client.Available == 0)
                throw new RfbException($"Serwer {_endpointDescription} zamknął połączenie.");

            if (Environment.TickCount64 >= deadline)
            {
                return HasPendingBytes(stream);
            }

            await Task.Delay(IdlePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Czy jest cokolwiek do odczytania: bajty w gnieździe ALBO — przy sesji szyfrowanej RSA-AES —
    /// tekst jawny już odszyfrowany i czekający w buforze. Bez tego drugiego warunku obserwator
    /// czekałby na dane, które fizycznie zdjął już z gniazda, i kończył cykl „bez zmian”.
    /// </summary>
    private bool HasPendingBytes(NetworkStream stream) =>
        stream.DataAvailable || (_rsaAes?.HasBufferedPlaintext ?? false);

    /// <summary>Skrót: <see cref="RequestUpdateAsync"/> + <see cref="ReceiveUpdateAsync"/>.</summary>
    public async Task<FramebufferUpdate> UpdateAsync(RectInt rect, bool incremental, CancellationToken ct)
    {
        await RequestUpdateAsync(rect, incremental, ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.ReadTimeout);
        try
        {
            FramebufferUpdate update;
            do { update = await ReceiveUpdateAsync(deadline.Token).ConfigureAwait(false); }
            while (_updatePending);
            return update;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            IsConnected = false;
            throw new RfbException($"Przekroczono limit {_options.ReadTimeout.TotalSeconds:0}s oczekiwania na pełną aktualizację {_endpointDescription}.");
        }
    }

    /// <summary>
    /// Skrót: <see cref="UpdateAsync"/> + <see cref="Rfb.Framebuffer.Crop"/> tego samego ROI. Gdy
    /// <paramref name="roi"/> leży całkowicie poza ekranem maszyny, rzuca <see cref="ArgumentException"/>
    /// (patrz <see cref="RequestUpdateAsync"/>) zanim cokolwiek zostanie wysłane do serwera — bez tej
    /// kontroli <see cref="RequestUpdateAsync"/> wysyłałby żądanie 0×0, a późniejszy
    /// <see cref="Rfb.Framebuffer.Crop"/> i tak rzuciłby wyjątek, tyle że już po (zbędnej) wymianie
    /// danych z serwerem.
    /// </summary>
    public async Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct)
    {
        await UpdateAsync(roi, incremental, ct).ConfigureAwait(false);
        return CropAfterUpdate(roi);
    }

    private RgbaImage CropAfterUpdate(RectInt roi)
    {
        if (roi.Clamp(Framebuffer.Width, Framebuffer.Height).IsEmpty)
            throw new RfbScreenResizedException(Framebuffer.Width, Framebuffer.Height);
        return Framebuffer.Crop(roi);
    }

    /// <summary>
    /// Odpowiednik <see cref="CaptureAsync"/> z limitem bezczynności: gdy poprzednie żądanie
    /// na ten sam ROI wciąż czeka na odpowiedź (<see cref="_updatePending"/>), NIE wysyła nowego — tylko
    /// czeka dalej (nie zalewa serwera WinCE żądaniami co cykl). Zwraca <see langword="null"/>, gdy w ciągu
    /// <paramref name="idleTimeout"/> nic się nie zmieniło.
    /// </summary>
    public async Task<RgbaImage?> TryCaptureAsync(RectInt roi, bool incremental, TimeSpan idleTimeout, CancellationToken ct)
    {
        EnsureConnected();
        RectInt clamped = ThrowIfClampedRoiEmpty(roi);
        if (!_updatePending || _pendingRoi != clamped || (!incremental && _pendingCoverage is null))
        {
            await RequestUpdateAsync(roi, incremental, ct).ConfigureAwait(false);
        }

        FramebufferUpdate? update = await TryReceiveUpdateAsync(idleTimeout, ct).ConfigureAwait(false);
        return update is null || _updatePending ? null : CropAfterUpdate(roi);
    }

    private void UpdateCoverage(FramebufferUpdate update)
    {
        if (_pendingCoverage is null) return;
        _pendingCoverage = _pendingCoverage.Select(r => r.Clamp(Framebuffer.Width, Framebuffer.Height))
            .Where(r => !r.IsEmpty).ToList();
        foreach (RectInt received in update.Rectangles)
        {
            var remaining = new List<RectInt>();
            foreach (RectInt missing in _pendingCoverage)
            {
                RectInt overlap = missing.Intersect(received);
                if (overlap.IsEmpty) { remaining.Add(missing); continue; }
                if (missing.Y < overlap.Y) remaining.Add(new(missing.X, missing.Y, missing.Width, overlap.Y - missing.Y));
                if (overlap.Bottom < missing.Bottom) remaining.Add(new(missing.X, overlap.Bottom, missing.Width, missing.Bottom - overlap.Bottom));
                if (missing.X < overlap.X) remaining.Add(new(missing.X, overlap.Y, overlap.X - missing.X, overlap.Height));
                if (overlap.Right < missing.Right) remaining.Add(new(overlap.Right, overlap.Y, missing.Right - overlap.Right, overlap.Height));
            }
            _pendingCoverage = remaining;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        try
        {
            // Warstwa RSA-AES trzyma klucze sesji AES — zwalniamy ją przed gniazdem, żeby zostały wymazane.
            _rsaAes?.Dispose();
        }
        catch (Exception)
        {
            // Zamknięcie warstwy szyfrującej nigdy nie powinno przerwać sprzątania zasobów.
        }

        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Zamknięcie strumienia nigdy nie powinno przerwać sprzątania zasobów.
        }

        try
        {
            _tcp?.Dispose();
        }
        catch (Exception)
        {
            // j.w.
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ---- Handshake: wersja protokołu ---------------------------------------------------------

    private async Task<int> NegotiateProtocolVersionAsync(CancellationToken ct)
    {
        byte[] header = await _reader!.ReadExactlyAsync(12, ct).ConfigureAwait(false);
        string text = Encoding.ASCII.GetString(header);
        // DebugFormat zapisuje bajt 0x0A jako "\x0A" — w raporcie czytelniejsze jest "\n".
        _rawVersionHeader = DebugFormat(header).Replace("\\x0A", "\\n", StringComparison.Ordinal);

        bool wellFormed = text.Length == 12
            && text.StartsWith("RFB ", StringComparison.Ordinal)
            && text[7] == '.'
            && text[11] == '\n';

        int major = -1;
        int minor = -1;
        if (wellFormed)
        {
            wellFormed = int.TryParse(text.AsSpan(4, 3), out major) && int.TryParse(text.AsSpan(8, 3), out minor);
        }

        if (!wellFormed)
        {
            throw new RfbProtocolException(
                $"Nieprawidłowy nagłówek protokołu RFB od serwera {_endpointDescription} (oczekiwano \"RFB 003.00x\\n\"): {DebugFormat(header)}.");
        }

        if (major < 3)
        {
            throw new RfbProtocolException(
                $"Serwer {_endpointDescription} zgłasza nieobsługiwaną wersję protokołu RFB {major}.{minor} (obsługiwana jest wersja 3.3-3.8 i nowsza, schodząca do 3.8).");
        }

        int chosenMinor = major > 3 || minor >= 8 ? 8 : minor == 7 ? 7 : 3;

        string response = $"RFB 003.{chosenMinor:000}\n";
        await SendAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);

        return chosenMinor;
    }

    private static string DebugFormat(byte[] bytes) =>
        string.Concat(bytes.Select(b => b is >= 0x20 and < 0x7F ? ((char)b).ToString() : $"\\x{b:X2}"));

    // ---- Handshake: zabezpieczenia ------------------------------------------------------------

    private async Task<(IReadOnlyList<byte> Offered, RfbSecurityType Used)> NegotiateSecurityAsync(int chosenMinor, CancellationToken ct)
    {
        if (chosenMinor == 3)
        {
            uint typeValue = await _reader!.ReadUInt32Async(ct).ConfigureAwait(false);
            if (typeValue == 0)
            {
                string reason = await ReadReasonStringAsync(ct).ConfigureAwait(false);
                throw new RfbAuthenticationException(
                    $"Serwer {_endpointDescription} odrzucił połączenie: {reason}", Array.Empty<byte>(), reason);
            }

            byte chosen = unchecked((byte)typeValue);
            var offered = new List<byte> { chosen };

            // W 3.3 to serwer NARZUCA typ — nie ma wyboru, więc RSA-AES obsługujemy tak samo jak niżej.
            if (chosen != (byte)RfbSecurityType.None
                && chosen != (byte)RfbSecurityType.VncAuth
                && !RsaAesHandshake.IsRsaAes(chosen))
            {
                throw BuildUnsupportedSecurityException(offered);
            }

            var usedType = (RfbSecurityType)chosen;
            if (usedType == RfbSecurityType.VncAuth)
            {
                await DoVncAuthAsync(ct).ConfigureAwait(false);
                // RFC 6143 §7.1.2 / §7.2.2: w wersji 3.3 SecurityResult jest czytany tylko po VncAuth.
                await ReadSecurityResultAsync(chosenMinor, offered, ct).ConfigureAwait(false);
            }
            else if (RsaAesHandshake.IsRsaAes(chosen))
            {
                await DoRsaAesAsync(chosen, ct).ConfigureAwait(false);
                await ReadSecurityResultAsync(chosenMinor, offered, ct).ConfigureAwait(false);
            }

            return (offered, usedType);
        }
        else
        {
            byte count = await _reader!.ReadByteAsync(ct).ConfigureAwait(false);
            if (count == 0)
            {
                string reason = await ReadReasonStringAsync(ct).ConfigureAwait(false);
                throw new RfbAuthenticationException(
                    $"Serwer {_endpointDescription} odrzucił połączenie: {reason}", Array.Empty<byte>(), reason);
            }

            byte[] offeredBytes = await _reader!.ReadExactlyAsync(count, ct).ConfigureAwait(false);
            var offered = offeredBytes.ToList();

            RfbSecurityType chosenType;
            if (offered.Contains((byte)RfbSecurityType.None))
            {
                chosenType = RfbSecurityType.None;
            }
            else if (offered.Contains((byte)RfbSecurityType.VncAuth))
            {
                chosenType = RfbSecurityType.VncAuth;
            }
            else if (PreferredRsaAes(offered) is { } rsaAes)
            {
                chosenType = (RfbSecurityType)rsaAes;
            }
            else
            {
                throw BuildUnsupportedSecurityException(offered);
            }

            await SendAsync([(byte)chosenType], ct).ConfigureAwait(false);

            if (chosenType == RfbSecurityType.VncAuth)
            {
                await DoVncAuthAsync(ct).ConfigureAwait(false);
            }
            else if (RsaAesHandshake.IsRsaAes((byte)chosenType))
            {
                await DoRsaAesAsync((byte)chosenType, ct).ConfigureAwait(false);
            }

            // RFC 6143 §7.1.2 / §7.2.1: SecurityResult jest zawsze czytany w 3.8 (także dla None);
            // w 3.7 tylko po VncAuth. RSA-AES kończy się własnym SecurityResult niezależnie od wersji.
            if (chosenMinor == 8 || chosenType == RfbSecurityType.VncAuth || RsaAesHandshake.IsRsaAes((byte)chosenType))
            {
                await ReadSecurityResultAsync(chosenMinor, offered, ct).ConfigureAwait(false);
            }

            return (offered, chosenType);
        }
    }

    /// <summary>
    /// Najmocniejszy wariant RSA-AES z listy oferowanej przez serwer, albo <see langword="null"/>, gdy
    /// serwer nie oferuje żadnego. Warianty szyfrujące całą sesję (5, 129) mają pierwszeństwo przed
    /// „ne” (6, 130), a 256-bitowe przed 128-bitowymi.
    /// </summary>
    private static byte? PreferredRsaAes(IReadOnlyList<byte> offered)
    {
        foreach (byte candidate in (ReadOnlySpan<byte>)[129, 5, 130, 6])
        {
            if (offered.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task DoRsaAesAsync(byte securityType, CancellationToken ct)
    {
        RsaAesResult result = await RsaAesHandshake.AuthenticateAsync(
            _stream!,
            _reader!,
            securityType,
            _options.Username,
            _options.Password,
            _endpointDescription,
            _options.ReadTimeout,
            _options.ExpectedServerKeySha256,
            ct).ConfigureAwait(false);

        _transport = result.Transport;
        _reader = result.Reader;
        _rsaAes = result.Encrypted ? (RsaAesStream)result.Transport : null;
        _rsaAesCredentialSubtype = result.CredentialSubtype;
        _rsaAesSentEmptyUsername = result.SentEmptyUsername;
        _rsaAesServerKeySha256 = result.ServerKeySha256;

        _logger.LogInformation(
            "Uwierzytelniono RSA-AES z {Endpoint} (typ {Type}); sesja {Mode}; odcisk klucza serwera {Fingerprint}{Pinned}.",
            _endpointDescription, securityType, result.Encrypted ? "szyfrowana" : "jawna po uwierzytelnieniu",
            result.ServerKeySha256, string.IsNullOrEmpty(_options.ExpectedServerKeySha256) ? " (nieprzypięty)" : " (zgodny z przypiętym)");
    }

    private async Task DoVncAuthAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.Password))
        {
            throw new RfbAuthenticationException(
                $"Serwer {_endpointDescription} wymaga hasła VNC (typ zabezpieczeń VncAuth), ale w konfiguracji nie podano hasła dla tej maszyny.");
        }

        if (_options.Password.FirstOrDefault(c => c > 'ÿ') is var bad && bad != default)
        {
            throw new RfbAuthenticationException(
                $"Hasło VNC dla {_endpointDescription} zawiera znak „{bad}”, którego uwierzytelnianie VncAuth nie obsługuje " +
                "(dozwolone są litery bez polskich znaków, cyfry i symbole) — zmień hasło na serwerze VNC i w Ustawieniach.");
        }

        byte[] challenge = await _reader!.ReadExactlyAsync(16, ct).ConfigureAwait(false);
        byte[] response = VncDes.EncryptChallenge(challenge, _options.Password);
        await SendAsync(response, ct).ConfigureAwait(false);
    }

    private async Task ReadSecurityResultAsync(int chosenMinor, IReadOnlyList<byte> offered, CancellationToken ct)
    {
        uint result = await _reader!.ReadUInt32Async(ct).ConfigureAwait(false);
        if (result == 0)
        {
            return;
        }

        string? reason = null;
        if (chosenMinor == 8)
        {
            reason = await ReadReasonStringAsync(ct).ConfigureAwait(false);
        }

        throw new RfbAuthenticationException(BuildSecurityFailureMessage(reason), offered, reason);
    }

    /// <summary>
    /// Komunikat odmowy uwierzytelnienia. Przy RSA-AES mówi wprost, o które poświadczenia chodziło —
    /// odmowa po wysłaniu PUSTEJ nazwy użytkownika jest jedyną twardą odpowiedzią na pytanie, czy ten
    /// serwer naprawdę wymaga logowania systemowego (patrz RsaAesHandshake, krok 6).
    /// </summary>
    private string BuildSecurityFailureMessage(string? reason)
    {
        string tail = reason is null ? "." : $": {reason}";

        if (_rsaAesCredentialSubtype == RsaAesHandshake.SubtypeUserAndPassword)
        {
            return _rsaAesSentEmptyUsername
                ? $"Serwer {_endpointDescription} odrzucił poświadczenia RSA-AES, wysłane z PUSTĄ nazwą " +
                  "użytkownika — ten serwer naprawdę wymaga logowania systemowego, więc oprócz hasła " +
                  "potrzebna jest nazwa użytkownika: uzupełnij pole 'username' w config.json" + tail
                : $"Serwer {_endpointDescription} odrzucił poświadczenia RSA-AES (nazwa użytkownika i hasło) — " +
                  "nieprawidłowa nazwa użytkownika albo hasło" + tail;
        }

        if (_rsaAesCredentialSubtype == RsaAesHandshake.SubtypePasswordOnly)
        {
            return $"Serwer {_endpointDescription} odrzucił hasło VNC (szyfrowane logowanie RSA-AES, samo hasło)" + tail;
        }

        return reason is null
            ? $"Serwer {_endpointDescription} odrzucił hasło VNC (nieprawidłowe hasło)."
            : $"Serwer {_endpointDescription} odrzucił hasło VNC: {reason}";
    }

    private async Task<string> ReadReasonStringAsync(CancellationToken ct)
    {
        uint len = await _reader!.ReadUInt32Async(ct).ConfigureAwait(false);
        if (len == 0)
        {
            return string.Empty;
        }

        if (len > 64 * 1024)
        {
            throw new RfbProtocolException($"Serwer {_endpointDescription} zgłosił nierealistycznie długi komunikat błędu ({len} B).");
        }

        byte[] bytes = await _reader!.ReadExactlyAsync((int)len, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private RfbAuthenticationException BuildUnsupportedSecurityException(IReadOnlyList<byte> offered)
    {
        string list = string.Join(", ", offered.Select(t => $"{t} ({DescribeSecurityType(t)})"));
        bool needsRsaAes = offered.Any(t => t is 5 or 6 or 129 or 130);
        string suffix = needsRsaAes ? " (wymaga RSA-AES)" : "";

        return new RfbAuthenticationException(
            $"Serwer {_endpointDescription} oferuje tylko typy zabezpieczeń: {list} – niewspierane{suffix}.",
            offered);
    }

    private static string DescribeSecurityType(byte t) => t switch
    {
        1 => "None",
        2 => "VncAuth",
        5 => "RA2",
        6 => "RA2ne",
        16 => "Tight",
        17 => "Ultra",
        18 => "TLS",
        19 => "VeNCrypt",
        129 => "RA2_256",
        130 => "RA2ne_256",
        _ => "nieznany",
    };

    // ---- ClientInit / ServerInit ---------------------------------------------------------------

    private async Task SendClientInitAsync(CancellationToken ct)
    {
        byte shared = (byte)(_options.Shared ? 1 : 0);
        await SendAsync([shared], ct).ConfigureAwait(false);
    }

    private async Task<(int Width, int Height, RfbPixelFormat Format, string Name)> ReadServerInitAsync(CancellationToken ct)
    {
        ushort width = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
        ushort height = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
        ValidateScreenDimensions(width, height);
        byte[] pixelFormatBytes = await _reader!.ReadExactlyAsync(16, ct).ConfigureAwait(false);
        RfbPixelFormat format = RfbPixelFormat.Parse(pixelFormatBytes);

        uint nameLen = await _reader!.ReadUInt32Async(ct).ConfigureAwait(false);
        string name = string.Empty;
        if (nameLen > 0)
        {
            if (nameLen > 64 * 1024)
            {
                throw new RfbProtocolException($"Serwer {_endpointDescription} zgłosił nierealistycznie długą nazwę pulpitu ({nameLen} B).");
            }

            byte[] nameBytes = await _reader!.ReadExactlyAsync((int)nameLen, ct).ConfigureAwait(false);
            name = DecodeServerName(nameBytes);
        }

        return (width, height, format, name);
    }

    /// <summary>
    /// Odrzuca nierealistyczne wymiary ekranu (0 albo powyżej <see cref="MaxScreenDimension"/>) zgłoszone
    /// przez serwer — w <c>ServerInit</c> albo pseudo-kodowaniu DesktopSize — zanim cokolwiek spróbuje
    /// zaalokować bufor tej wielkości. Bez tej kontroli np. 40000×40000 prowadzi do przepełnienia
    /// arytmetyki `int` (albo alokacji rzędu gigabajtów) w konstruktorze <see cref="Imaging.RgbaImage"/>.
    /// </summary>
    private void ValidateScreenDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxScreenDimension || height > MaxScreenDimension
            || (long)width * height > MaxScreenPixels)
        {
            throw new RfbProtocolException(
                $"Serwer {_endpointDescription} zgłosił nieprawidłowy rozmiar ekranu {width}×{height} " +
                $"(dozwolony zakres to 1–{MaxScreenDimension} pikseli w każdym wymiarze i najwyżej {MaxScreenPixels / 1_000_000.0:0.#} Mpx łącznie).");
        }
    }

    private static string DecodeServerName(byte[] bytes)
    {
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Starsze serwery (WinCE/OpenCnc) bywają w Latin1 — nigdy nie rzuca wyjątku.
            return Encoding.Latin1.GetString(bytes);
        }
    }


    private async Task SendSetPixelFormatFromServerInitAsync(CancellationToken ct)
    {
        RfbPixelFormat format = ServerInfo!.PixelFormat;
        byte[] msg = new byte[20];
        msg[0] = MsgClientSetPixelFormat; // bajty 1–3: wypełnienie (zera)
        format.Write(msg.AsSpan(4, 16));

        await SendAsync(msg, ct).ConfigureAwait(false);
        _logger.LogDebug(
            "Serwer Qt Embedded {Endpoint}: wysłano SetPixelFormat z formatem z ServerInit ({Format}) — bez tego serwer koduje w formacie niezainicjowanym albo poprzedniego klienta.",
            _endpointDescription, format.Describe());
    }

    // ---- SetEncodings ---------------------------------------------------------------------------

    private async Task SendSetEncodingsAsync(bool qtServer, CancellationToken ct)
    {
        int[] encodings = _options.Encodings;
        if (qtServer && !encodings.Contains(CursorPseudoEncoding))
        {
            encodings = [.. encodings, CursorPseudoEncoding];
        }

        _cursorRequested = encodings.Contains(CursorPseudoEncoding);
        byte[] msg = new byte[4 + (encodings.Length * 4)];
        msg[0] = MsgClientSetEncodings;
        msg[1] = 0; // padding
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(4 + (i * 4), 4), encodings[i]);
        }

        await SendAsync(msg, ct).ConfigureAwait(false);
    }

    // ---- FramebufferUpdate / SetColourMapEntries / ServerCutText ------------------------------

    private async Task<FramebufferUpdate> ReadFramebufferUpdateAsync(CancellationToken ct)
    {
        await _reader!.ReadByteAsync(ct).ConfigureAwait(false); // padding
        ushort rectCount = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);

        var rects = new List<RectInt>(rectCount);
        var encodingsUsed = new List<int>(rectCount);

        for (int i = 0; i < rectCount; i++)
        {
            ushort x = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort y = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort w = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort h = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
            int encoding = await _reader!.ReadInt32Async(ct).ConfigureAwait(false);

            if (encoding == DesktopSizePseudoEncoding)
            {
                ValidateScreenDimensions(w, h);
                Framebuffer.Resize(w, h);
                ServerInfo = ServerInfo! with { Width = w, Height = h };
                encodingsUsed.Add(encoding);
                _logger.LogDebug("DesktopSize: nowy rozmiar ekranu {Width}x{Height} ({Endpoint})", w, h, _endpointDescription);
                continue;
            }

            if (encoding == CursorPseudoEncoding && _cursorRequested)
            {
                await SkipCursorAsync(w, h, ct).ConfigureAwait(false);
                encodingsUsed.Add(encoding);
                continue;
            }

            var rect = new RectInt(x, y, w, h);
            switch (encoding)
            {
                case RfbEncodings.Raw:
                    await RawDecoder.DecodeAsync(_reader!, ServerInfo!.PixelFormat, rect, Framebuffer, _colourMap, ct).ConfigureAwait(false);
                    break;
                case RfbEncodings.CopyRect:
                    await CopyRectDecoder.DecodeAsync(_reader!, rect, Framebuffer, ct).ConfigureAwait(false);
                    break;
                case RfbEncodings.Hextile:
                    await HextileDecoder.DecodeAsync(_reader!, ServerInfo!.PixelFormat, rect, Framebuffer, _colourMap, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new RfbProtocolException($"Nieobsługiwane kodowanie prostokąta aktualizacji: {encoding} (serwer {_endpointDescription}).");
            }

            rects.Add(rect);
            encodingsUsed.Add(encoding);
        }

        return new FramebufferUpdate(rects, encodingsUsed);
    }

    private async Task SkipCursorAsync(int width, int height, CancellationToken ct)
    {
        if (width > MaxCursorDimension || height > MaxCursorDimension)
        {
            throw new RfbProtocolException(
                $"Serwer {_endpointDescription} zgłosił nierealistycznie duży kursor ({width}×{height}).");
        }

        int bytesPerPixel = ServerInfo!.PixelFormat.BytesPerPixel;
        int length = (width * height * bytesPerPixel) + (((width + 7) / 8) * height);
        if (length > 0)
        {
            await _reader!.SkipAsync(length, ct).ConfigureAwait(false);
        }
    }

    private async Task ReadSetColourMapEntriesAsync(CancellationToken ct)
    {
        await _reader!.ReadByteAsync(ct).ConfigureAwait(false); // padding
        ushort first = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
        ushort count = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);

        _colourMap ??= new RfbColourMap();
        for (int i = 0; i < count; i++)
        {
            ushort r = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort g = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort b = await _reader!.ReadUInt16Async(ct).ConfigureAwait(false);

            byte r8 = (byte)(r * 255 / 65535);
            byte g8 = (byte)(g * 255 / 65535);
            byte b8 = (byte)(b * 255 / 65535);

            _colourMap.Set((uint)(first + i), new Rgba(r8, g8, b8));
        }

        _logger.LogDebug("SetColourMapEntries: first={First} count={Count} ({Endpoint})", first, count, _endpointDescription);
    }

    private async Task ReadServerCutTextAsync(CancellationToken ct)
    {
        await _reader!.ReadExactlyAsync(3, ct).ConfigureAwait(false); // padding
        uint len = await _reader!.ReadUInt32Async(ct).ConfigureAwait(false);
        if (len == 0)
        {
            return;
        }

        if (len > 16 * 1024 * 1024)
        {
            throw new RfbProtocolException($"Serwer {_endpointDescription} zgłosił nierealistycznie długi ServerCutText ({len} B).");
        }

        // Klient tylko do odczytu — schowek serwera odczytujemy i odrzucamy (nie jest nam potrzebny).
        await _reader!.SkipAsync((int)len, ct).ConfigureAwait(false);
    }

    // ---- Wysyłka ---------------------------------------------------------------------------------

    private async Task SendAsync(byte[] msg, CancellationToken ct)
    {
        if (_transport is null)
        {
            throw new InvalidOperationException("Brak połączenia — SendAsync wywołane przed ConnectAsync.");
        }

        try
        {
            await _transport.WriteAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            IsConnected = false;
            throw new RfbException($"Połączenie z {_endpointDescription} przerwane podczas wysyłania danych: {ex.Message}", ex);
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected || ServerInfo is null || _transport is null)
        {
            throw new InvalidOperationException("RfbClient nie jest połączony — wywołaj najpierw ConnectAsync.");
        }
    }

    /// <summary>
    /// Przycina <paramref name="roi"/> do rozmiaru ekranu serwera; gdy wynik jest pusty (ROI leży
    /// całkowicie poza ekranem), rzuca <see cref="ArgumentException"/> zamiast zwracać prostokąt 0×0.
    /// Wywoływane zawsze PRZED wysłaniem jakiegokolwiek żądania do serwera — wymaga wywołanego wcześniej
    /// <see cref="EnsureConnected"/> (korzysta z <see cref="ServerInfo"/>).
    /// </summary>
    private RectInt ThrowIfClampedRoiEmpty(RectInt roi, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(roi))] string? paramName = null)
    {
        RectInt clamped = roi.Clamp(ServerInfo!.Width, ServerInfo.Height);
        if (clamped.IsEmpty)
        {
            throw new ArgumentException(
                $"Obszar {roi} leży poza ekranem maszyny ({ServerInfo.Width}×{ServerInfo.Height}) – sprawdź ustawienie statusBarRoi w profilu ekranu.",
                paramName);
        }

        return clamped;
    }
}
