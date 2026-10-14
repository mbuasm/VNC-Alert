using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Cluster;

public sealed class UdpPeerBus : IPeerBus
{
    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RejectLogThrottle = TimeSpan.FromMinutes(1);

    private readonly ClusterConfig _cfg;
    private readonly HmacSigner _signer;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly UdpClient _client;
    private readonly UdpClient? _sender;
    private readonly LanInterface? _lan;

    private readonly object _dnsCacheLock = new();
    private readonly Dictionary<string, (IReadOnlyList<IPAddress> Addresses, DateTimeOffset ExpiresAt)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _rejectThrottleLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRejectLogByReason = new(StringComparer.Ordinal);

    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private readonly object _startLock = new();
    private long _seq;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private bool _started;
    private bool _disposed;

    public UdpPeerBus(ClusterConfig cfg, HmacSigner signer, IClock clock, string localHost, ILogger? logger = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        LocalHost = string.IsNullOrWhiteSpace(localHost)
            ? throw new ArgumentException("Nazwa lokalnego komputera nie może być pusta.", nameof(localHost))
            : localHost;
        _logger = logger ?? NullLogger.Instance;

        if (_cfg.Group is not null) _lan = LanInterface.Select(_cfg.LocalAddress);

        _client = new UdpClient(AddressFamily.InterNetwork);
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, _lan is null);
        _client.ExclusiveAddressUse = _lan is not null;
        _client.EnableBroadcast = true;
        try
        {
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, _cfg.UdpPort));
            if (_lan is not null)
            {
                _sender = new UdpClient(new IPEndPoint(_lan.Address, 0));
                _sender.EnableBroadcast = true;
            }
        }
        catch { _sender?.Dispose(); _client.Dispose(); throw; }
    }

    public string LocalHost { get; }

    public event Action<PeerMessage>? Received;
    public event Action<string>? Rejected;

    /// <summary>
    /// Adresy broadcast wszystkich aktywnych (Up, nie-loopback) lokalnych interfejsów IPv4 — obliczone jako
    /// <c>adres | ~maska</c>. Używane przy wysyłce (oprócz globalnego 255.255.255.255) i do diagnostyki (`probe.exe`).
    /// </summary>
    public static IReadOnlyList<IPAddress> LocalBroadcastAddresses()
    {
        var result = new List<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                IPAddress? mask = ua.IPv4Mask;
                if (mask is null || mask.Equals(IPAddress.Any))
                {
                    continue;
                }

                IPAddress broadcast = ComputeBroadcast(ua.Address, mask);
                if (!result.Contains(broadcast))
                {
                    result.Add(broadcast);
                }
            }
        }

        return result;
    }

    public Task StartAsync(CancellationToken ct)
    {
        lock (_startLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        return Task.CompletedTask;
    }

    public async Task SendAsync(PeerMessage msg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(msg);
        ObjectDisposedException.ThrowIf(_disposed, this);

        PeerMessage stamped = msg with
        {
            Sender = LocalHost,
            Seq = Interlocked.Increment(ref _seq),
            SentAt = _clock.UtcNow,
            InstanceId = _instanceId,
        };
        byte[] datagram = MessageCodec.Encode(stamped, _signer, _logger);

        var targets = new HashSet<IPEndPoint> { new(_lan?.Broadcast ?? IPAddress.Broadcast, _cfg.UdpPort) };

        foreach (IPAddress broadcast in _lan is null ? LocalBroadcastAddresses() : [])
        {
            targets.Add(new IPEndPoint(broadcast, _cfg.UdpPort));
        }

        foreach (string peerEntry in _cfg.Peers)
        {
            if (string.IsNullOrWhiteSpace(peerEntry))
            {
                continue;
            }

            (string host, int port) = ParsePeerEntry(peerEntry, _cfg.UdpPort);
            IReadOnlyList<IPAddress> addresses = await ResolveHostAsync(host, ct).ConfigureAwait(false);
            foreach (IPAddress addr in addresses)
            {
                targets.Add(new IPEndPoint(addr, port));
            }
        }

        bool sent = false;
        foreach (IPEndPoint target in targets)
        {
            try
            {
                await (_sender ?? _client).SendAsync(datagram, target, ct).ConfigureAwait(false);
                sent = true;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "Nie udało się wysłać pakietu klastra do {Target}.", target);
            }
        }
        if (!sent && _cfg.Group is not null) throw new IOException("Nie wysłano pakietu UDP grupy.");
    }

    public async ValueTask DisposeAsync()
    {
        lock (_startLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts?.Cancel();
        _client.Close();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // spodziewane przy zamykaniu
            }
        }

        _client.Dispose();
        _sender?.Dispose();
        _cts?.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result;
                try
                {
                    result = await _client.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "Błąd odbioru pakietu UDP klastra — kontynuuję nasłuch.");
                    continue;
                }

                if (!MessageCodec.TryDecode(
                        result.Buffer, _signer, _clock, TimeSpan.FromSeconds(_cfg.ClockSkewToleranceSec),
                        out PeerMessage? msg, out string? rejectReason))
                {
                    HandleRejected(rejectReason ?? "nieznany powód odrzucenia", result.RemoteEndPoint);
                    continue;
                }

                if (msg is null || string.Equals(msg.Sender, LocalHost, StringComparison.Ordinal))
                {
                    continue; // własny broadcast — ignorujemy
                }

                // Wyjątek z handlera zdarzenia Received (kod aplikacji) NIE MOŻE ubić pętli odbioru.
                try
                {
                    Received?.Invoke(msg);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Błąd obsługi odebranego pakietu klastra — kontynuuję nasłuch.");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Ogólne zabezpieczenie pętli — cokolwiek nieoczekiwanego wyżej (poza zamykaniem), nasłuch
                // ma trwać dalej w kolejnej iteracji.
                _logger.LogError(ex, "Nieoczekiwany błąd w pętli odbioru pakietów klastra — kontynuuję nasłuch.");
            }
        }
    }

    private void HandleRejected(string reason, IPEndPoint remote)
    {
        string message = $"{reason} (od {remote})";
        try
        {
            Rejected?.Invoke(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd obsługi odebranego pakietu klastra — kontynuuję nasłuch.");
        }

        string throttleKey = ThrottleKey(reason);
        bool shouldLog;
        lock (_rejectThrottleLock)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (_lastRejectLogByReason.TryGetValue(throttleKey, out DateTimeOffset last) && now - last < RejectLogThrottle)
            {
                shouldLog = false;
            }
            else
            {
                _lastRejectLogByReason[throttleKey] = now;
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            _logger.LogWarning("Odrzucono obcy/zepsuty pakiet klastra: {Reason}", message);
        }
    }

    /// <summary>Wyodrębnia stabilny "powód bez zmiennych szczegółów" (np. bez różnicy czasu w sekundach) do throttlingu logów.</summary>
    private static string ThrottleKey(string reason)
    {
        int parenIndex = reason.IndexOf('(');
        int colonIndex = reason.IndexOf(':');
        int cut = parenIndex >= 0 && (colonIndex < 0 || parenIndex < colonIndex) ? parenIndex : colonIndex;
        return cut >= 0 ? reason[..cut].Trim() : reason;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveHostAsync(string host, CancellationToken ct)
    {
        lock (_dnsCacheLock)
        {
            if (_dnsCache.TryGetValue(host, out var cached) && cached.ExpiresAt > _clock.UtcNow)
            {
                return cached.Addresses;
            }
        }

        IPAddress[] resolved;
        try
        {
            resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            _logger.LogDebug(ex, "Nie udało się rozwiązać adresu peera \"{Host}\".", host);
            resolved = [];
        }

        IReadOnlyList<IPAddress> ipv4 = resolved.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        lock (_dnsCacheLock)
        {
            _dnsCache[host] = (ipv4, _clock.UtcNow + DnsCacheTtl);
        }

        return ipv4;
    }

    private static (string Host, int Port) ParsePeerEntry(string entry, int defaultPort)
    {
        string trimmed = entry.Trim();
        int colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(trimmed.AsSpan(colonIndex + 1), out int parsedPort))
        {
            return (trimmed[..colonIndex], parsedPort);
        }

        return (trimmed, defaultPort);
    }

    private static IPAddress ComputeBroadcast(IPAddress address, IPAddress mask)
    {
        byte[] addrBytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] broadcastBytes = new byte[addrBytes.Length];
        for (int i = 0; i < addrBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(addrBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcastBytes);
    }
}
