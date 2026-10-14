using System.Net;
using System.Net.Sockets;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class ObservationTransportTests
{
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static string HeartbeatJson(IClock clock, string sender = "PC-OBCY")
    {
        var group = GroupFileTests.Invitation().Identity;
        var heartbeat = new HeartbeatMessage
        {
            Sender = sender, Version = "0.4.200", Group = group, Seq = 1, SentAt = clock.UtcNow,
            InstanceId = Guid.NewGuid().ToString("N"), Readiness = new(true, false, []), RecentAcks = [],
        };
        return Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<PeerMessage>(heartbeat, MessageCodec.JsonOptions));
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 50) await Task.Delay(50);
    }

    // ── Grupa mieszana na prawdziwym UDP ────────────────────────────────────────────────────────

    [Fact]
    public async Task UdpBusRejectsUnknownTypeWithOneThrottledWarningAndNoError()
    {
        int port = FreeUdpPort();
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        IClock clock = SystemClock.Instance;
        var logs = new RecordingLoggerFactory();
        UdpPeerBus bus;
        try { bus = new UdpPeerBus(new ClusterConfig { UdpPort = port }, signer, clock, "PC-ODBIORCA", logs.CreateLogger("udp")); }
        catch (SocketException ex) { Assert.Fail($"Nie udało się otworzyć gniazda UDP w tym środowisku: {ex.Message}"); return; }

        await using (bus)
        {
            int received = 0, rejected = 0;
            bus.Received += _ => Interlocked.Increment(ref received);
            bus.Rejected += _ => Interlocked.Increment(ref rejected);
            await bus.StartAsync(CancellationToken.None);

            using var sender = new UdpClient();
            var target = new IPEndPoint(IPAddress.Loopback, port);
            for (int i = 0; i < 20; i++)
            {
                string type = i % 2 == 0 ? "observationRequest" : "observationReply";
                string json = HeartbeatJson(clock).Replace("\"type\":\"heartbeat\"", $"\"type\":\"{type}x\"");
                byte[] datagram = signer.Seal(Encoding.UTF8.GetBytes(json));
                await sender.SendAsync(datagram, datagram.Length, target);
            }
            byte[] heartbeat = signer.Seal(Encoding.UTF8.GetBytes(HeartbeatJson(clock)));
            await sender.SendAsync(heartbeat, heartbeat.Length, target);
            await Until(() => Volatile.Read(ref received) + Volatile.Read(ref rejected) >= 21);

            if (received + rejected < 21) Assert.Fail($"Pętla zwrotna nie doręczyła pakietów ({received}+{rejected}/21) — środowisko bez UDP.");
            Assert.Equal(1, received);
            Assert.Equal(20, rejected);
            Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("niepoprawny JSON", StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        }
    }


    [Fact]
    public void TryDecodeThrowsNotSupportedWhenTypeIsNotTheFirstField_DocumentsCurrentBehaviour()
    {
        // OBECNE zachowanie: System.Text.Json wymaga dyskryminatora na początku obiektu,
        // a TryDecode łapie tylko JsonException — wyjątek wylatuje do wołającego zamiast „false + powód”.
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        IClock clock = SystemClock.Instance;
        string json = HeartbeatJson(clock);
        string reordered = "{" + json[(json.IndexOf(',') + 1)..^1] + ",\"type\":\"heartbeat\"}";
        Assert.DoesNotContain("{\"type\"", reordered);

        Assert.Throws<NotSupportedException>(() => MessageCodec.TryDecode(signer.Seal(Encoding.UTF8.GetBytes(reordered)),
            signer, clock, TimeSpan.FromSeconds(300), out _, out _));
    }

    [Fact]
    public async Task UdpBusLogsAnUnthrottledErrorForEveryPacketWithTypeNotFirst_DocumentsCurrentBehaviour()
    {
        // Skutek powyższego na prawdziwej szynie: ogólny catch pętli odbioru → LogError przy KAŻDYM pakiecie
        // (bez dławienia 1/min z HandleRejected). Pętla odbioru żyje dalej — heartbeat po nich dochodzi.
        int port = FreeUdpPort();
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        IClock clock = SystemClock.Instance;
        var logs = new RecordingLoggerFactory();
        UdpPeerBus bus;
        try { bus = new UdpPeerBus(new ClusterConfig { UdpPort = port }, signer, clock, "PC-ODBIORCA", logs.CreateLogger("udp")); }
        catch (SocketException ex) { Assert.Fail($"Nie udało się otworzyć gniazda UDP w tym środowisku: {ex.Message}"); return; }

        await using (bus)
        {
            int received = 0, rejected = 0;
            bus.Received += _ => Interlocked.Increment(ref received);
            bus.Rejected += _ => Interlocked.Increment(ref rejected);
            await bus.StartAsync(CancellationToken.None);
            using var sender = new UdpClient();
            var target = new IPEndPoint(IPAddress.Loopback, port);
            for (int i = 0; i < 5; i++)
            {
                string json = HeartbeatJson(clock);
                byte[] datagram = signer.Seal(Encoding.UTF8.GetBytes("{" + json[(json.IndexOf(',') + 1)..^1] + ",\"type\":\"heartbeat\"}"));
                await sender.SendAsync(datagram, datagram.Length, target);
            }
            byte[] heartbeat = signer.Seal(Encoding.UTF8.GetBytes(HeartbeatJson(clock)));
            await sender.SendAsync(heartbeat, heartbeat.Length, target);
            await Until(() => Volatile.Read(ref received) >= 1);
            await Until(() => logs.Entries.Count(e => e.Level == LogLevel.Error) >= 5, 1000);

            if (received == 0) Assert.Fail("Pętla zwrotna nie doręczyła heartbeatu — środowisko bez UDP.");
            Assert.Equal(0, rejected);
            Assert.Equal(5, logs.Entries.Count(e => e.Level == LogLevel.Error && e.Exception is NotSupportedException));
        }
    }

    // ── ClusterRuntime ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RuntimeHandsTheJournalToEveryNewGroupNodeAndForwardsChanges()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        using var dirA = new TempDir("observation-runtime"); using var dirB = new TempDir("observation-runtime");
        var mine = new ObservationJournal(dirA.File("observation.jsonl"));
        var theirs = new ObservationJournal(dirB.File("observation.jsonl"));
        theirs.Apply([new("aidite1", ObservationState.Observed, clock.UtcNow.AddHours(-2), clock.UtcNow.AddHours(-1))]);

        await using var runtime = new ClusterRuntime(config, file.NetworkKey, clock, new AckMemory(clock, TimeSpan.FromHours(2)),
            "A", "test", (c, _) => hub.CreateBus("A", c));
        runtime.Observation = mine; // przed startem — jak AppOrchestrator w konstruktorze
        int changed = 0;
        runtime.ObservationChanged += () => changed++;
        runtime.SetReadiness(new(true, false, []));
        await runtime.StartAsync(default);
        Assert.Same(mine, runtime.GroupNode!.Observation);

        await using var partner = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B", config), clock, "test");
        partner.SetReadiness(new(true, false, []));
        partner.Observation = theirs;
        await partner.StartAsync(default);
        for (int i = 0; i < 15; i++) { await runtime.TickAsync(default); await partner.TickAsync(default); clock.Advance(TimeSpan.FromSeconds(1)); }

        Assert.Equal(theirs.Snapshot(), mine.Snapshot());
        Assert.True(changed > 0);

        // Podmiana transportu (zmiana ustawień grupy): nowy węzeł dostaje ten sam dziennik.
        var before = runtime.GroupNode;
        await runtime.SwitchAsync(config with { Priority = 150 }, file.NetworkKey, default);
        Assert.NotSame(before, runtime.GroupNode);
        Assert.Same(mine, runtime.GroupNode!.Observation);

        // Ustawienie po starcie trafia do bieżącego węzła.
        runtime.Observation = null;
        Assert.Null(runtime.GroupNode!.Observation);
    }

    [Fact]
    public async Task RuntimeWithoutGroupKeepsTheJournalLocalOnly()
    {
        var clock = new FakeClock();
        using var dir = new TempDir("observation-runtime");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        await using var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, new AckMemory(clock, TimeSpan.FromHours(2)),
            "A", "test");
        runtime.Observation = journal;
        await runtime.StartAsync(default);

        Assert.Null(runtime.GroupNode);
        Assert.Same(journal, runtime.Observation);
    }
}
