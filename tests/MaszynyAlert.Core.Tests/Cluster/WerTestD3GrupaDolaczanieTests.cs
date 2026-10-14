using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class WerTestD3GrupaDolaczanieTests
{
    private static readonly DateTimeOffset GroupEdit = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, bool> Station520580Snapshot() => TestPaths.ExampleConfig.Machines
        .ToDictionary(m => m.Id, m => m.Id is "amm580" or "amm520-2", StringComparer.Ordinal);

    private static string Enabled(AppConfig config) =>
        string.Join(",", config.Machines.Where(m => m.Enabled).Select(m => m.Id).Order(StringComparer.Ordinal));

    private sealed record Outcome(SharedConfigSync Member, SharedConfigSync Joining, string MemberBefore);

    private static async Task<Outcome> JoinAsync(TempDir dir, DateTimeOffset localEditAt)
    {
        var clock = new FakeClock(GroupEdit.AddMinutes(1));
        GroupFile invitation = Config.GroupFileTests.Invitation();
        var group = new ClusterConfig { Enabled = true, Group = invitation.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(invitation.NetworkKey), clock);

        var member = new SharedConfigSync(new SharedConfigStore(dir.File(Path.Combine("czlonek", "shared-config.json"))), "PC-HALA-1");
        member.Reload();
        member.Approve(new UpdateApproval
        {
            Version = "0.4.250.0", FileName = "VncAlert-Setup-0.4.250.0.exe", ApprovedBy = "PC-HALA-1", ApprovedAt = GroupEdit,
        }, GroupEdit);
        Assert.Null(member.Current!.Machines);
        string memberBefore = Enabled(member.Current.ApplyTo(TestPaths.ExampleConfig));
        await using var node = new GroupClusterNode(group with { Priority = 1 }, hub.CreateBus("PC-HALA-1", group), clock, "test")
            { SharedConfig = member };
        node.SetReadiness(new(true, false, []));
        await node.StartAsync(default);

        var joining = new SharedConfigSync(new SharedConfigStore(dir.File(Path.Combine("dolaczany", "shared-config.json"))), "PC-DOLACZANY");
        joining.Reload();
        joining.Edit(null, null, localEditAt, machines: Station520580Snapshot()); // Panel → Maszyny, gdy komputer był poza grupą
        await using var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, new AckMemory(clock, TimeSpan.FromHours(2)),
            "PC-DOLACZANY", "test", (c, _) => hub.CreateBus("PC-DOLACZANY", c));
        runtime.SharedConfig = joining; // jak AppOrchestrator w konstruktorze
        runtime.SetReadiness(new(true, false, []));
        await runtime.StartAsync(default);
        Assert.Null(runtime.GroupNode);

        // Dołączenie do grupy (zaproszenie zatwierdzone): przełączenie transportu na grupę z kluczem zaproszenia.
        await runtime.SwitchAsync(group with { Priority = 5 }, invitation.NetworkKey, default);
        Assert.NotNull(runtime.GroupNode);
        for (int second = 0; second < 60; second++)
        {
            await runtime.TickAsync(default);
            await node.TickAsync(default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        return new Outcome(member, joining, memberBefore);
    }

    [Fact]
    public async Task Dolaczenie_LokalnePrzelacznikiStarszeOdDokumentuGrupy_NieZmieniajaMaszynGrupy_Aidite1do7ZostajaWlaczone()
    {
        using var dir = new TempDir("wer-d3-dolaczenie");
        Outcome o = await JoinAsync(dir, GroupEdit.AddDays(-1));

        string memberAfter = Enabled(o.Member.Current!.ApplyTo(TestPaths.ExampleConfig));
        Assert.True(o.MemberBefore == memberAfter,
            $"Dołączenie zmieniło maszyny obserwowane przez grupę: przed [{o.MemberBefore}], po [{memberAfter}]; "
            + $"dokument członka: EditedBy={o.Member.Current.EditedBy}, machines={(o.Member.Current.Machines is null ? "brak" : "migawka dołączanego")}.");
        Assert.Equal(o.Member.Hash, o.Joining.Hash);
    }

    /// <summary>
    /// lokalna zmiana dołączanego komputera jest NOWSZA od dokumentu grupy (nowy dokument
    /// wygrywa „nowszy wygrywa”). (przełączniki zapisane poza grupą nie
    /// wchodzą do grupy przy dołączeniu)
    /// </summary>
    [Fact]
    public async Task Dolaczenie_LokalnePrzelacznikiNowszeOdDokumentuGrupy_NieZmieniajaMaszynGrupy_Aidite1do7ZostajaWlaczone()
    {
        using var dir = new TempDir("wer-d3-dolaczenie");
        Outcome o = await JoinAsync(dir, GroupEdit.AddMinutes(-1).AddDays(1));

        string memberAfter = Enabled(o.Member.Current!.ApplyTo(TestPaths.ExampleConfig));
        Assert.True(o.MemberBefore == memberAfter,
            $"Dołączenie zmieniło maszyny obserwowane przez grupę: przed [{o.MemberBefore}], po [{memberAfter}]; "
            + $"dokument członka: EditedBy={o.Member.Current.EditedBy}, machines={(o.Member.Current.Machines is null ? "brak" : "migawka dołączanego")}.");
        Assert.Equal(o.Member.Hash, o.Joining.Hash);
    }
}
