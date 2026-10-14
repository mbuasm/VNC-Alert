using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class GroupContractTests
{
    [Fact]
    public void Candidate_RequiresPasswordsOnlyForEnabledMachines_AndMayObserveWithoutOcr()
    {
        var config = TestPaths.ExampleConfig;
        var queried = new List<string>();
        var result = WatcherReadiness.Evaluate(config, id => { queried.Add(id); return true; },
            ocrAvailable: false, GroupCompatibility.Compatible);
        Assert.True(result.CanObserve);
        Assert.True(result.OcrLimited);
        Assert.Equal(config.Machines.Where(m => m.Enabled && m.RequiresPassword).Select(m => m.Id), queried);
        result = WatcherReadiness.Evaluate(config, _ => false, true, GroupCompatibility.Compatible);
        Assert.False(result.CanObserve);
        Assert.Equal(7, result.MissingPasswordMachineIds.Count);
        Assert.DoesNotContain("amm520-1", result.MissingPasswordMachineIds);

        result = WatcherReadiness.Evaluate(
            config,
            id => config.Machines.Single(m => m.Id == id).RequiresPassword,
            true,
            GroupCompatibility.Compatible);
        Assert.True(result.CanObserve);
        Assert.Empty(result.MissingPasswordMachineIds);
    }

    [Fact]
    public void GloballyDisabledCandidate_DoesNotReadSecrets()
    {
        var result = WatcherReadiness.Evaluate(TestPaths.ExampleConfig with { Enabled = false },
            _ => throw new Exception("No secret access expected"), true, GroupCompatibility.Compatible);
        Assert.False(result.CanObserve);
    }

    [Theory]
    [InlineData(GroupCompatibility.OtherGroup)]
    [InlineData(GroupCompatibility.OtherProtocol)]
    [InlineData(GroupCompatibility.OtherKey)]
    [InlineData(GroupCompatibility.OtherProfile)]
    public void IncompatibleCandidate_CannotTakeOver(GroupCompatibility compatibility)
    {
        Assert.False(WatcherReadiness.Evaluate(TestPaths.ExampleConfig, _ => true, true, compatibility).CanObserve);
    }

    [Fact]
    public void Identity_SeparatesGroupKeyProtocolAndProfile()
    {
        var identity = GroupFileTests.Invitation().Identity;
        Assert.Equal(GroupCompatibility.Compatible, identity.Compare(identity with { }));
        Assert.Equal(GroupCompatibility.OtherGroup, identity.Compare(identity with { GroupId = Guid.NewGuid() }));
        Assert.Equal(GroupCompatibility.OtherKey, identity.Compare(identity with { KeyId = Guid.NewGuid() }));
        Assert.Equal(GroupCompatibility.OtherProtocol, identity.Compare(identity with { ProtocolVersion = 1 }));
        Assert.Equal(GroupCompatibility.OtherProfile, identity.Compare(identity with { ProfileRevision = 2 }));
        Assert.Equal(GroupCompatibility.OtherProfile, identity.Compare(identity with { ProfileHash = new string('0', 64) }));
    }

    [Fact]
    public async Task V2Group_CannotAccidentallyUseLegacyRuntime_ButDisabledGroupRemainsLocal()
    {
        await using var network = new TestCluster();
        var config = TestPaths.ExampleConfig.Cluster with { Enabled = true, Group = GroupFileTests.Invitation().Identity };
        await using var bus = network.Hub.CreateBus("PC", config);
        var acks = new AckMemory(network.Clock, TimeSpan.FromHours(2));
        Assert.Throws<NotSupportedException>(() => new ClusterNode(config, bus, network.Clock, acks, "0.2.0"));
        await using var local = new ClusterNode(config with { Enabled = false }, bus, network.Clock, acks, "0.2.0");
        await local.StartAsync(CancellationToken.None);
        Assert.True(local.IsWatcher);
    }
}
