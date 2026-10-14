using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ReconnectBackoffPositionTests
{
    [Theory]
    [InlineData(new[] { -1, 5000 }, 1, -1)]
    [InlineData(new[] { 2000, 5000, -15000 }, 3, -15000)]
    public void NegativeValuePositionCountsFromOne(int[] values, int position, int value)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.ValidateTiming(new TimingConfig { ReconnectBackoffMs = values }));
        Assert.Equal($"Pole 'timing.reconnectBackoffMs' zawiera ujemną wartość ({value}) na pozycji {position}.", ex.Message);
    }

    [Fact]
    public void ConfigJsonWithNegativeBackoff_ReportsPositionFromOne()
    {
        string example = File.ReadAllText(MaszynyAlert.Core.Tests.TestUtil.TestPaths.ConfigExample);
        AppConfig parsed = ConfigLoader.ParseAppConfig(example);
        AppConfig broken = parsed with { Timing = parsed.Timing with { ReconnectBackoffMs = [2000, -5000] } };
        AppConfig reparsed = ConfigLoader.ParseAppConfig(ConfigLoader.Serialize(broken));

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(reparsed));
        Assert.Contains("na pozycji 2.", ex.Message, StringComparison.Ordinal);
    }
}
