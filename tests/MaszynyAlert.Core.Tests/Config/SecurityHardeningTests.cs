using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class SecurityHardeningTests
{
    private const string Base = """
    {
      "enabled": true,
      "machines": [ { "id": "m1", "name": "M1", "host": "127.0.0.1", "port": 5900, "profile": "p1" } ],
      "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 }, "minRedFraction": 0.005,
        "red": { "hueMinDeg": 340, "hueMaxDeg": 70, "minSaturation": 0.4, "minValue": 0.3 } } }
    }
    """;

    [Fact]
    public void OdciskKluczaRsaAes_Poprawny_Przyjety()
    {
        AppConfig cfg = ConfigLoader.ParseAppConfig(Base.Replace("\"profile\": \"p1\" }",
            $"\"profile\": \"p1\", \"rsaAesKeySha256\": \"{new string('a', 64)}\" }}", StringComparison.Ordinal));

        ConfigLoader.Validate(cfg);

        Assert.Equal(new string('a', 64), cfg.Machines[0].RsaAesKeySha256);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void OdciskKluczaRsaAes_Nieprawidlowy_ConfigException(string pin)
    {
        AppConfig cfg = ConfigLoader.ParseAppConfig(Base.Replace("\"profile\": \"p1\" }",
            $"\"profile\": \"p1\", \"rsaAesKeySha256\": \"{pin}\" }}", StringComparison.Ordinal));

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains("rsaAesKeySha256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdresKataloguWydanZDanymiLogowania_Odrzucony()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() =>
            ConfigLoader.ValidateUpdates(new UpdatesConfig { Url = "https://user:haslo@wydania.example/maszyny-alert/" }));
        Assert.Contains("updates.url", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1-1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void HistoriaCsv_NeutralizujeFormulyArkusza(string text)
    {
        using var dir = new TempDir("historia-formuly");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());
        var alert = new Alert
        {
            Key = AlertKey.ForRule("m1", "r1"), MachineId = "m1", MachineName = text, Kind = AlertKind.Error,
            ShortText = text, FullText = text, Category = "inny", StartedAt = FakeClock.DefaultStart,
            LastSeenAt = FakeClock.DefaultStart, Status = AlertStatus.Active, Sound = true,
        };

        writer.Append(new AlertEvent(AlertEventKind.Raised, alert));

        string[] fields = File.ReadAllLines(path, Encoding.UTF8)[1].Split(';');
        Assert.All(new[] { fields[1], fields[3], fields[5] }, f => Assert.False(f.TrimStart('"').Length > 0 && "=+-@".Contains(f.TrimStart('"')[0]), f));
    }
}
