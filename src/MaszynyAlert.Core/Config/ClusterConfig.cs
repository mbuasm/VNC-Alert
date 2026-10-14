namespace MaszynyAlert.Core.Config;

/// <summary>Ustawienia komunikacji między komputerami operatorów (klaster, UDP broadcast + HMAC).</summary>
public sealed record ClusterConfig
{
    /// <summary>Local IPv4 interface for v2. Never exported to another computer.</summary>
    public string? LocalAddress { get; init; }
    /// <summary>Port UDP używany do rozgłaszania i odbierania komunikatów klastra.</summary>
    public int UdpPort { get; init; } = 47777;

    /// <summary>Priorytet tego komputera przy wyborze obserwatora — im niższa liczba, tym wyższy priorytet.</summary>
    public int Priority { get; init; } = 100;

    /// <summary>Odstęp między kolejnymi komunikatami HEARTBEAT (ms).</summary>
    public int HeartbeatIntervalMs { get; init; } = 1000;

    /// <summary>Odstęp między kolejnymi pełnymi komunikatami STATE wysyłanymi przez obserwatora (ms).</summary>
    public int StateIntervalMs { get; init; } = 1000;

    /// <summary>Po ilu milisekundach braku heartbeatu od danego komputera uznajemy go za martwego.</summary>
    public int PeerTimeoutMs { get; init; } = 15000;

    /// <summary>Ile milisekund komputer musi być "kandydatem" na obserwatora, zanim faktycznie przejmie rolę.</summary>
    public int TakeoverDelayMs { get; init; } = 20000;

    /// <summary>Tolerancja rozjazdu zegarów między komputerami przy weryfikacji znacznika czasu pakietów (s).</summary>
    public int ClockSkewToleranceSec { get; init; } = 300;

    /// <summary>Próg skoku zegara monotonicznego uznawany za wybudzenie ze snu (ms).</summary>
    public int SleepJumpThresholdMs { get; init; } = 5000;

    public int DowntimeSyncIntervalMs { get; init; } = 600000;

    /// <summary>
    /// Jak daleko wstecz wolno prosić o postoje (dni). Komputer wyłączony dłużej NIE dostanie starszych
    /// epizodów.
    /// </summary>
    public int DowntimeSyncWindowDays { get; init; } = 90;

    /// <summary>Opcjonalna lista adresów (unicast) komputerów spoza zasięgu broadcastu, np. innej podsieci.</summary>
    public List<string> Peers { get; init; } = new();

    /// <summary>Wyłącznik komunikacji klastrowej — false = tryb pojedynczego komputera (zawsze obserwator, bez UDP).</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Metadata for the opt-in v2 group; null preserves explicit legacy cluster settings.</summary>
    public Cluster.GroupIdentity? Group { get; init; }
}
