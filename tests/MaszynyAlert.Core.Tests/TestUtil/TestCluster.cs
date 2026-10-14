using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>
/// Symulowany klaster DOWOLNEJ liczby komputerów: jeden <see cref="InMemoryPeerHub"/>, jeden <see cref="FakeClock"/>
/// i po jednym <see cref="ClusterNode"/> na komputer. Wszystko jest deterministyczne i bez realnego czekania —
/// <see cref="TickSecondsAsync"/> wykonuje kolejne "sekundy" pracy programu (tyknięcie każdego węzła + przesunięcie
/// zegara). Rozszerza scenariusze z <c>ClusterNodeTests</c> (tam zaszyte na sztywno dwa komputery) o:
/// dołączanie/restart komputerów w trakcie testu, podsłuch wysyłanych pakietów (<see cref="AddSniffer"/>) oraz
/// wstrzykiwanie ręcznie zbudowanych pakietów (<see cref="Inject"/>) — potrzebne, gdy test musi sterować polami
/// <c>Seq</c>/<c>InstanceId</c>, których zwykła magistrala nadaje sama.
/// </summary>
public sealed class TestCluster : IAsyncDisposable
{
    private readonly List<TestNode> _nodes = [];
    private readonly List<InMemoryPeerBus> _sniffers = [];

    public TestCluster(FakeClock? clock = null, HmacSigner? signer = null)
    {
        Clock = clock ?? new FakeClock();
        Hub = new InMemoryPeerHub(signer ?? new HmacSigner(HmacSigner.GenerateKey()), Clock);
    }

    /// <summary>Wspólny zegar wszystkich komputerów tego klastra.</summary>
    public FakeClock Clock { get; }

    /// <summary>Symulowana sieć lokalna (można ją dzielić <see cref="InMemoryPeerHub.Partition"/> i wyłączać <see cref="InMemoryPeerHub.Deliver"/>).</summary>
    public InMemoryPeerHub Hub { get; }

    /// <summary>Komputery dodane do klastra, w kolejności dodania.</summary>
    public IReadOnlyList<TestNode> Nodes => _nodes;

    /// <summary>Dodaje komputer o podanej nazwie i priorytecie, uruchamia jego węzeł i włącza go do tyknięć.</summary>
    public async Task<TestNode> AddAsync(string host, int priority = 100, ClusterConfig? cfg = null, string version = "0.1.0")
    {
        ClusterConfig config = cfg ?? new ClusterConfig { Enabled = true, Priority = priority };
        TestNode node = Create(host, config, version);
        await node.Node.StartAsync(CancellationToken.None);
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Symuluje restart programu na danym komputerze: stary węzeł jest zamykany, a w jego miejsce powstaje nowy
    /// (ta sama nazwa hosta i konfiguracja, ale NOWA magistrala — czyli nowy <c>InstanceId</c> i numeracja
    /// <c>Seq</c> od zera, dokładnie jak po ponownym uruchomieniu procesu). Zwraca nowy węzeł.
    /// </summary>
    public async Task<TestNode> RestartAsync(TestNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        int index = _nodes.IndexOf(node);
        if (index < 0)
        {
            throw new InvalidOperationException($"Komputer \"{node.Host}\" nie należy do tego klastra.");
        }

        await node.Node.DisposeAsync();
        TestNode restarted = Create(node.Host, node.Config, node.Version);
        await restarted.Node.StartAsync(CancellationToken.None);
        _nodes[index] = restarted;
        return restarted;
    }

    /// <summary>
    /// Dodaje "podsłuch" — magistralę, która niczego nie wysyła, tylko zapamiętuje wszystkie odebrane pakiety.
    /// Pozwala sprawdzić, co dokładnie węzły rozgłaszają (np. ile wpisów niesie <c>HeartbeatMessage.RecentAcks</c>).
    /// </summary>
    public TestSniffer AddSniffer(string host = "PC-PODSLUCH")
    {
        InMemoryPeerBus bus = Hub.CreateBus(host, new ClusterConfig());
        _sniffers.Add(bus);
        return new TestSniffer(bus);
    }

    /// <summary>Wykonuje <paramref name="seconds"/> sekundowych tyknięć wszystkich komputerów (po każdym przesuwa zegar o 1 s).</summary>
    public async Task TickSecondsAsync(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            foreach (TestNode node in _nodes.ToList())
            {
                await node.Node.TickAsync(CancellationToken.None);
            }

            Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Tyka klastrem, dopóki <paramref name="condition"/> nie będzie spełnione (najwyżej <paramref name="maxSeconds"/> razy).
    /// Zwraca liczbę wykonanych sekund — test może sprawdzić, że warunek wystąpił przed limitem.
    /// </summary>
    public async Task<int> TickUntilAsync(Func<bool> condition, int maxSeconds = 60)
    {
        ArgumentNullException.ThrowIfNull(condition);
        for (int i = 0; i < maxSeconds; i++)
        {
            if (condition())
            {
                return i;
            }

            await TickSecondsAsync(1);
        }

        return condition() ? maxSeconds : -1;
    }

    /// <summary>
    /// Doręcza <paramref name="msg"/> WYŁĄCZNIE do wskazanego komputera, tą samą ścieżką co prawdziwy pakiet
    /// (podpis HMAC + <see cref="MessageCodec"/>), ale bez nadawania pól przez magistralę — dzięki temu test
    /// steruje wprost <c>Sender</c>, <c>Seq</c> i <c>InstanceId</c> (scenariusze anty-powtórkowe i restartu nadawcy).
    /// Puste <c>SentAt</c> jest uzupełniane bieżącym czasem zegara testowego.
    /// </summary>
    public void Inject(TestNode target, PeerMessage msg)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(msg);
        PeerMessage stamped = msg.SentAt == default ? msg with { SentAt = Clock.UtcNow } : msg;
        target.Bus.DeliverIncoming(MessageCodec.Encode(stamped, Hub.Signer));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (TestNode node in _nodes)
        {
            await node.Node.DisposeAsync();
        }

        foreach (InMemoryPeerBus sniffer in _sniffers)
        {
            await sniffer.DisposeAsync();
        }

        _nodes.Clear();
        _sniffers.Clear();
    }

    private TestNode Create(string host, ClusterConfig config, string version)
    {
        InMemoryPeerBus bus = Hub.CreateBus(host, config);
        var acks = new AckMemory(Clock, TimeSpan.FromHours(2));
        var node = new ClusterNode(config, bus, Clock, acks, version);
        return new TestNode(host, config, version, bus, acks, node);
    }
}

/// <summary>Jeden komputer symulowanego klastra — magistrala, pamięć potwierdzeń i węzeł <see cref="ClusterNode"/>.</summary>
public sealed record TestNode(
    string Host, ClusterConfig Config, string Version, InMemoryPeerBus Bus, AckMemory Acks, ClusterNode Node);

/// <summary>Podsłuch pakietów krążących w symulowanej sieci (patrz <see cref="TestCluster.AddSniffer"/>).</summary>
public sealed class TestSniffer
{
    internal TestSniffer(InMemoryPeerBus bus)
    {
        Bus = bus;
        bus.Received += Messages.Add;
    }

    /// <summary>Magistrala podsłuchu (nic nie wysyła).</summary>
    public InMemoryPeerBus Bus { get; }

    /// <summary>Wszystkie odebrane pakiety, w kolejności doręczenia.</summary>
    public List<PeerMessage> Messages { get; } = [];

    /// <summary>Ostatni heartbeat odebrany od wskazanego komputera.</summary>
    public HeartbeatMessage LastHeartbeatFrom(string host) =>
        Messages.OfType<HeartbeatMessage>().Last(m => string.Equals(m.Sender, host, StringComparison.Ordinal));
}
