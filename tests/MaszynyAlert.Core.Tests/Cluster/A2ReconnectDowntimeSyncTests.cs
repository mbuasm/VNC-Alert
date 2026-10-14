using System.Globalization;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class A2ReconnectDowntimeSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
    private static DowntimeEpisode Episode(string machine, DateTimeOffset from, DateTimeOffset to) =>
        new(machine, machine, "Servo", from, to, to, EpisodeId: Guid.NewGuid(), Category: "inny");

    [Fact]
    public async Task A2_03_ReconnectBackfillsTheOfflineGapAfterANewLocalObservation()
    {
        using var dir = new TempDir("audit-a2");
        var clock = new FakeClock(T0);
        var group = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = group.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "0.4.148") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B"), clock, "main") { Downtime = bJournal };
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default);
        await Tick(a, b, clock, 25);
        hub.Partition("A", "B");
        var missed = Episode("aidite1", T0.AddHours(1), T0.AddHours(2));
        aJournal.Merge(missed);
        clock.Advance(TimeSpan.FromDays(3));
        bJournal.Merge(Episode("aidite2", clock.UtcNow.AddMinutes(-2), clock.UtcNow));
        hub.Heal();
        await Tick(a, b, clock, 700);

        Assert.Contains(bJournal.Snapshot(), e => e.EpisodeId == missed.EpisodeId);
    }

    private static async Task Tick(GroupClusterNode a, GroupClusterNode b, FakeClock clock, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            await a.TickAsync(default); await b.TickAsync(default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }
}