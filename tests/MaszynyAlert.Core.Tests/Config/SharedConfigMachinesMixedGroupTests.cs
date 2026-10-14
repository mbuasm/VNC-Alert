using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class SharedConfigMachinesMixedGroupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("klucz-testowy-grupy"));

    private static readonly GroupIdentity Group = new()
    {
        GroupId = Guid.Parse("11111111-2222-3333-4444-555555555555"), GroupName = "Hala",
        KeyId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), ProtocolVersion = GroupIdentity.CurrentProtocolVersion,
        ProfileRevision = 3, ProfileHash = new string('c', 64),
    };

    private static byte[] Golden(string name) => Cluster.GroupMixedVersionGoldenTests.Golden(name);

    private static SharedConfigDocument DocumentFrom0148()
    {
        Assert.True(MessageCodec.TryDecode(Golden("13-sharedConfig"), new HmacSigner(Key), new FakeClock(T0), TimeSpan.FromSeconds(300),
            out var message, out string? reason), reason);
        return Assert.IsType<SharedConfigMessage>(message).Document;
    }

    [Fact]
    public void NowszyZapisNaStarszejWersji_NieKasujePrzelacznikowMaszyn()
    {
        using var dir = new TempDir("d3-grupa-mieszana");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(null, null, T0.AddMinutes(-1), "PC-NOWY", machines: new Dictionary<string, bool> { ["amm580"] = true });
        SharedConfigDocument fromOld = DocumentFrom0148();
        Assert.Null(fromOld.Machines);

        store.Merge(fromOld);

        SharedConfigDocument loaded = store.Load()!;
        Assert.Equal("0.4.249.0", loaded.UpdateApproval?.Version); // zgoda ze starszego komputera dotarła
        Assert.True(loaded.Machines?.GetValueOrDefault("amm580")?.Enabled == true,
            "Zapis wspólnej konfiguracji na 0.4.148 skasował przełączniki maszyn z Panelu 0.4.249 (machines = "
            + (loaded.Machines is null ? "null" : string.Join(",", loaded.Machines.Select(p => p.Key + "=" + p.Value.Enabled))) + ").");
    }

    [Fact]
    public async Task ZgodaKliknietaNa0_4_148_ObserwatorNowejWersjiDalejObserwuje580WlaczonaWPanelu()
    {
        using var dir = new TempDir("d3-grupa-mieszana");
        var clock = new FakeClock(T0);
        var hub = new InMemoryPeerHub(new HmacSigner(Key), clock);
        var oldPc = hub.CreateBus("PC-A");
        var sync = new SharedConfigSync(new SharedConfigStore(dir.File("shared-config.json")), "PC-B");
        sync.Reload();
        await using var node = new GroupClusterNode(new ClusterConfig { Enabled = true, Group = Group }, hub.CreateBus("PC-B"), clock, "test")
            { SharedConfig = sync };
        node.SetReadiness(new(true, false, []));
        await node.StartAsync(default);
        sync.Edit(null, null, T0.AddMinutes(-1), machines: new Dictionary<string, bool> { ["amm580"] = true });
        AppConfig baseConfig = TestPaths.ExampleConfig;
        Assert.False(baseConfig.Machines.Single(m => m.Id == "amm580").Enabled);
        Assert.True(sync.Current!.ApplyTo(baseConfig).Machines.Single(m => m.Id == "amm580").Enabled);

        hub.Dispatch(oldPc, Golden("01-heartbeat"));
        hub.Dispatch(oldPc, Golden("13-sharedConfig"));

        Assert.Equal("PC-A", sync.Current!.EditedBy); // dokument 0.4.148 przyjęty (nowszy)
        Assert.True(sync.Current.ApplyTo(baseConfig).Machines.Single(m => m.Id == "amm580").Enabled,
            "Po zgodzie na aktualizację klikniętej na komputerze 0.4.148 maszyna 580 włączona w Panelu 0.4.249 jest znów wyłączona.");
    }
}
