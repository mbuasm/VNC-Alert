using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class UpdatesConfigTests
{
    private static AppConfig Config(UpdatesConfig updates) => new() { Enabled = false, Updates = updates };

    [Fact]
    public void MissingSectionMeansDefaultsWithUpdatesSwitchedOn()
    {
        var cfg = ConfigLoader.ParseAppConfig("{ \"enabled\": false }");

        Assert.True(cfg.Updates.Enabled);
        Assert.Equal("https://wydania.mbuas.pl/maszyny-alert/", cfg.Updates.Url);
        Assert.Equal(60, cfg.Updates.CheckIntervalMinutes);
        Assert.Null(cfg.Updates.AccessClientId);
        Assert.True(cfg.Updates.AutoInstallWhenApproved);
        ConfigLoader.Validate(cfg);
    }

    [Fact]
    public void ExampleConfigurationDescribesTheSectionWithTheDefaultValues()
    {
        var example = TestPaths.ExampleConfig.Updates;

        Assert.Equal(new UpdatesConfig(), example);
        Assert.Contains("\"updates\"", File.ReadAllText(TestPaths.ConfigExample), StringComparison.Ordinal);
    }

    [Fact]
    public void SectionSurvivesSavingAndLoading()
    {
        var cfg = Config(new UpdatesConfig { Enabled = false, CheckIntervalMinutes = 120, AccessClientId = "abc.access", AutoInstallWhenApproved = false });

        var loaded = ConfigLoader.ParseAppConfig(ConfigLoader.Serialize(cfg));

        Assert.Equal(cfg.Updates, loaded.Updates);
    }

    [Theory]
    [InlineData("https://wydania.mbuas.pl/maszyny-alert/")]
    [InlineData("https://wydania.mbuas.pl/maszyny-alert")]
    [InlineData("http://localhost:8080/wydania/")]
    [InlineData("http://127.0.0.1/wydania")]
    public void AllowedAddressesPass(string url)
    {
        ConfigLoader.Validate(Config(new UpdatesConfig { Url = url }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wydania.mbuas.pl/maszyny-alert")]
    [InlineData("http://wydania.mbuas.pl/maszyny-alert/")]
    [InlineData("ftp://wydania.mbuas.pl/")]
    [InlineData("file:///C:/wydania/")]
    public void AddressThatIsNotHttpsIsRefused(string url)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(new UpdatesConfig { Url = url })));

        Assert.Contains("updates.url", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1441)]
    [InlineData(0)]
    public void CheckIntervalOutsideTheRangeIsRefused(int minutes)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(new UpdatesConfig { CheckIntervalMinutes = minutes })));

        Assert.Contains("updates.checkIntervalMinutes", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1440)]
    public void CheckIntervalAtTheEdgesPasses(int minutes)
    {
        ConfigLoader.Validate(Config(new UpdatesConfig { CheckIntervalMinutes = minutes }));
    }

    [Fact]
    public void AccessClientIdWithControlCharactersOrTooLongIsRefused()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(new UpdatesConfig { AccessClientId = "abc\r\nX-Inny: 1" })));
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(new UpdatesConfig { AccessClientId = new string('a', 201) })));
        ConfigLoader.Validate(Config(new UpdatesConfig { AccessClientId = new string('a', 200) }));
    }

    [Fact]
    public void NullSectionIsRefusedLikeEveryOtherSection()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new AppConfig { Enabled = false, Updates = null! }));

        Assert.Contains("updates", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretKeyOfTheAccessTokenIsFixed()
    {
        // Klucz trafia do magazynu sekretów stanowiska (DPAPI) — zmiana nazwy zgubiłaby zapisany sekret.
        Assert.Equal("updates:access-client-secret", SecretKeys.UpdatesAccessClientSecret);
    }
}
