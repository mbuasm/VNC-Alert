using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class NullSectionRegressionTests
{
    private const string Base = """
    {
      "enabled": true,
      "machines": [ { "id": "m1", "name": "M1", "host": "127.0.0.1", "port": 5900, "profile": "p1" } ],
      "profiles": {
        "p1": {
          "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
          "minRedFraction": 0.005,
          "red": { "hueMinDeg": 340, "hueMaxDeg": 70, "minSaturation": 0.4, "minValue": 0.3 }
        }
      }
    }
    """;

    private static string With(string field, string value)
        => Base.TrimEnd().TrimEnd('}').TrimEnd() + ",\n  " + $"\"{field}\": {value}\n}}";

    private static string WithProfileSection(string section, string value)
        => Base.Replace("\"minRedFraction\": 0.005,", $"\"minRedFraction\": 0.005,\n      \"{section}\": {value},", StringComparison.Ordinal);

    private static ConfigException AssertConfigException(string json)
    {
        Exception? ex = Record.Exception(() => ConfigLoader.Validate(ConfigLoader.ParseAppConfig(json)));
        return Assert.IsType<ConfigException>(ex);
    }

    [Fact]
    public void UiOverlayNull_DajeConfigExceptionZNazwaPola()
    {
        ConfigException ex = AssertConfigException(With("ui", """{ "overlay": null }"""));

        Assert.Contains("ui.overlay", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anchorLabels")]
    [InlineData("otherScreenLabels")]
    public void MainScreenListaNull_DajeConfigExceptionZNazwaPola(string field)
    {
        ConfigException ex = AssertConfigException(WithProfileSection("mainScreen",
            $$"""{ "enabled": true, "{{field}}": null }"""));

        Assert.Contains("mainScreen." + field, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdleProgressFillNull_WTrybieBar_DajeConfigExceptionZamiastBleduWKazdymCyklu()
    {
        ConfigException ex = AssertConfigException(WithProfileSection("idle", """
            { "enabled": true, "mode": "bar", "progressRoi": { "x": 0, "y": 30, "width": 100, "height": 10 }, "progressFill": null }
            """));

        Assert.Contains("idle.progressFill", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NazwaMaszynyNull_DajeConfigException()
    {
        ConfigException ex = AssertConfigException(Base.Replace("\"name\": \"M1\"", "\"name\": null", StringComparison.Ordinal));

        Assert.Contains("m1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JezykOcrNull_DajeConfigException()
    {
        ConfigException ex = AssertConfigException(With("ocr", """{ "language": null }"""));

        Assert.Contains("ocr.language", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PeersNullPrzyWylaczonejGrupie_PrzyjmowanePustaLista()
    {
        AppConfig cfg = ConfigLoader.ParseAppConfig(With("cluster", """{ "enabled": false, "peers": null }"""));

        ConfigLoader.Validate(cfg);

        Assert.NotNull(cfg.Cluster.Peers);
        Assert.Empty(cfg.Cluster.Peers);
    }

    [Theory]
    [InlineData("""{ "enabled": false, "udpPort": 0 }""", "cluster.udpPort")]
    [InlineData("""{ "enabled": false, "udpPort": 70000 }""", "cluster.udpPort")]
    [InlineData("""{ "enabled": false, "heartbeatIntervalMs": 1 }""", "cluster")]
    public void WartosciGrupySpozaZakresuPrzyWylaczonejGrupie_StartBezBlokadyAleZOstrzezeniem(string cluster, string expected)
    {
        AppConfig cfg = ConfigLoader.ParseAppConfig(With("cluster", cluster));

        ConfigLoader.Validate(cfg);
        IReadOnlyList<string> warnings = ConfigLoader.Warnings(cfg);

        string warning = Assert.Single(warnings);
        Assert.Contains(expected, warning, StringComparison.Ordinal);
        Assert.Contains("enabled", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void WartosciGrupySpozaZakresuPrzyWlaczonejGrupie_NadalBlokujaStart()
    {
        ConfigException ex = AssertConfigException(With("cluster", """{ "enabled": true, "udpPort": 0 }"""));

        Assert.Contains("cluster.udpPort", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PoprawnaKonfiguracja_BezOstrzezen()
    {
        Assert.Empty(ConfigLoader.Warnings(ConfigLoader.ParseAppConfig(Base)));
    }

    private static ConfigException AssertRulesConfigException(string rulesJson)
    {
        AppConfig cfg = ConfigLoader.ParseAppConfig(Base);
        Exception? ex = Record.Exception(() => ConfigLoader.Validate(ConfigLoader.ParseRules(rulesJson), cfg));
        return Assert.IsType<ConfigException>(ex);
    }

    [Fact]
    public void RegulaZPhrasesNull_DajeConfigExceptionPrzyWczytaniu()
    {
        ConfigException ex = AssertRulesConfigException("""
            { "rules": [ { "id": "r1", "phrases": null, "shortText": "A", "category": "inny" } ] }
            """);

        Assert.Contains("r1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("phrases", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegulaZPhrasesNullWBazieBledow_DajeConfigException()
    {
        ConfigException ex = AssertRulesConfigException("""
            { "databases": { "p1": { "rules": [ { "id": "r1", "phrases": null, "shortText": "A", "category": "inny" } ] } } }
            """);

        Assert.Contains("phrases", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ElementListyRegulNull_DajeConfigException()
    {
        ConfigException ex = AssertRulesConfigException("""{ "rules": [ null ] }""");

        Assert.Contains("null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ElementListyKodowNull_DajeConfigException()
    {
        ConfigException ex = AssertRulesConfigException("""{ "codes": [ null ] }""");

        Assert.Contains("null", ex.Message, StringComparison.Ordinal);
    }
}
