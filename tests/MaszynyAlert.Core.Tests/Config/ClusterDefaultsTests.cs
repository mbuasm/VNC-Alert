using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ClusterDefaultsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cluster\":{}}")]
    [InlineData("{\"cluster\":{\"priority\":10}}")]
    public void MissingClusterSwitch_DoesNotEnableNetworking(string json)
    {
        Assert.False(ConfigLoader.ParseAppConfig(json).Cluster.Enabled);
    }

    [Fact]
    public void ExplicitLegacyChoice_IsPreserved()
    {
        Assert.True(ConfigLoader.ParseAppConfig("{\"cluster\":{\"enabled\":true}}").Cluster.Enabled);
        Assert.False(new ClusterConfig().Enabled);
    }
}
