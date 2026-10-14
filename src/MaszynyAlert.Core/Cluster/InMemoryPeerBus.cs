using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Cluster;

/// <summary>
/// Centralny "hub" łączący kilka <see cref="InMemoryPeerBus"/> w symulowaną sieć lokalną — do testów i
/// scenariuszy bez prawdziwego UDP. Dostarczanie jest synchroniczne i deterministyczne (bez wątków w tle):
/// <see cref="InMemoryPeerBus.SendAsync"/> od razu doręcza (o ile <see cref="Deliver"/> jest włączone, a para
/// nadawca/odbiorca nie jest rozdzielona przez <see cref="Partition"/>) do wszystkich pozostałych busów w
/// hubie, w kolejności ich utworzenia. Wiadomość przechodzi przez prawdziwą ścieżkę Encode→TryDecode tym samym
/// <see cref="Signer"/>, więc testy odrzucania złego HMAC/JSON działają identycznie jak dla <see cref="UdpPeerBus"/>.
/// </summary>
public sealed class InMemoryPeerHub
{
    private readonly IClock _clock;
    private readonly List<InMemoryPeerBus> _buses = [];
    private readonly HashSet<(string A, string B)> _partitions = [];
    private readonly Random? _random;
    private readonly object _lock = new();

    /// <param name="signer">Wspólny podpisujący HMAC używany przez wszystkie busy tego hubu.</param>
    /// <param name="clock">Zegar współdzielony przez busy (zwykle zegar testowy sterowany ręcznie).</param>
    /// <param name="lossSeed">Ziarno generatora losowości do symulacji strat pakietów (<see cref="LossRate"/>); bez niego straty są wyłączone.</param>
    public InMemoryPeerHub(HmacSigner signer, IClock clock, int? lossSeed = null)
    {
        Signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = lossSeed.HasValue ? new Random(lossSeed.Value) : null;
    }

    /// <summary>Wspólny podpisujący (HMAC) używany przez wszystkie busy w tym hubie — tą samą ścieżką co prawdziwy UDP.</summary>
    public HmacSigner Signer { get; }

    /// <summary>Globalny wyłącznik dostarczania — <see langword="false"/> symuluje całkowitą awarię sieci między komputerami.</summary>
    public bool Deliver { get; set; } = true;

    /// <summary>Prawdopodobieństwo zgubienia pojedynczej wiadomości (0..1) — działa tylko, gdy podano ziarno losowości w konstruktorze.</summary>
    public double LossRate { get; set; }

    /// <summary>Tworzy nowy bus dla komputera o podanej nazwie hosta i dołącza go do hubu.</summary>
    /// <param name="host">Nazwa hosta (<see cref="IPeerBus.LocalHost"/>).</param>
    /// <param name="cfg">Konfiguracja klastra tego komputera (używana m.in. do tolerancji rozjazdu zegarów przy odbiorze); domyślna, gdy pominięto.</param>
    /// <param name="logger">Opcjonalny logger — wyjątki z handlerów <see cref="InMemoryPeerBus.Received"/>/<see cref="InMemoryPeerBus.Rejected"/> są tu logowane.</param>
    public InMemoryPeerBus CreateBus(string host, ClusterConfig? cfg = null, ILogger? logger = null)
    {
        var bus = new InMemoryPeerBus(this, host, _clock, cfg ?? new ClusterConfig(), logger);
        lock (_lock)
        {
            _buses.Add(bus);
        }

        return bus;
    }

    /// <summary>Blokuje dostarczanie wiadomości (w obie strony) między dwoma hostami — symuluje partycję sieci.</summary>
    public void Partition(string hostA, string hostB)
    {
        ArgumentNullException.ThrowIfNull(hostA);
        ArgumentNullException.ThrowIfNull(hostB);
        lock (_lock)
        {
            _partitions.Add(NormalizedPair(hostA, hostB));
        }
    }

    /// <summary>Usuwa wszystkie partycje — sieć wraca do normalnego działania.</summary>
    public void Heal()
    {
        lock (_lock)
        {
            _partitions.Clear();
        }
    }

    internal void Dispatch(InMemoryPeerBus sender, byte[] datagram)
    {
        List<InMemoryPeerBus> targets;
        lock (_lock)
        {
            if (!Deliver)
            {
                return;
            }

            targets = _buses
                .Where(b => !ReferenceEquals(b, sender) && !IsPartitioned(sender.LocalHost, b.LocalHost))
                .ToList();
        }

        foreach (InMemoryPeerBus target in targets)
        {
            if (_random is not null && LossRate > 0 && _random.NextDouble() < LossRate)
            {
                continue;
            }

            target.DeliverIncoming(datagram);
        }
    }

    private bool IsPartitioned(string hostA, string hostB) => _partitions.Contains(NormalizedPair(hostA, hostB));

    private static (string A, string B) NormalizedPair(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}

/// <summary>Bus w pamięci (bez sieci) — połączony z innymi busami przez <see cref="InMemoryPeerHub"/>. Do testów i symulacji.</summary>
public sealed class InMemoryPeerBus : IPeerBus
{
    private readonly InMemoryPeerHub _hub;
    private readonly IClock _clock;
    private readonly ClusterConfig _cfg;
    private readonly ILogger _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private long _seq;
    private bool _disposed;

    internal InMemoryPeerBus(InMemoryPeerHub hub, string localHost, IClock clock, ClusterConfig cfg, ILogger? logger = null)
    {
        _hub = hub;
        _clock = clock;
        _cfg = cfg;
        _logger = logger ?? NullLogger.Instance;
        LocalHost = string.IsNullOrWhiteSpace(localHost)
            ? throw new ArgumentException("Nazwa lokalnego komputera nie może być pusta.", nameof(localHost))
            : localHost;
    }

    public string LocalHost { get; }

    public event Action<PeerMessage>? Received;
    public event Action<string>? Rejected;

    /// <summary>W wersji w pamięci nie ma niczego do uruchamiania — zwraca ukończone zadanie.</summary>
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SendAsync(PeerMessage msg, CancellationToken ct)
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

        byte[] datagram = MessageCodec.Encode(stamped, _hub.Signer);
        _hub.Dispatch(this, datagram);
        return Task.CompletedTask;
    }

    internal void DeliverIncoming(byte[] datagram)
    {
        var skewTolerance = TimeSpan.FromSeconds(_cfg.ClockSkewToleranceSec);
        if (!MessageCodec.TryDecode(datagram, _hub.Signer, _clock, skewTolerance, out PeerMessage? msg, out string? reason))
        {
            // Wyjątek z handlera zdarzenia (kod aplikacji) nie może przerwać doręczania kolejnym busom w hubie
            // ani "wykrzaczyć" nadawcy.
            try
            {
                Rejected?.Invoke(reason ?? "nieznany powód odrzucenia");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd obsługi odebranego pakietu klastra — kontynuuję nasłuch.");
            }

            return;
        }

        if (msg is null || string.Equals(msg.Sender, LocalHost, StringComparison.Ordinal))
        {
            return; // własny broadcast — ignorujemy
        }

        try
        {
            Received?.Invoke(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd obsługi odebranego pakietu klastra — kontynuuję nasłuch.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
