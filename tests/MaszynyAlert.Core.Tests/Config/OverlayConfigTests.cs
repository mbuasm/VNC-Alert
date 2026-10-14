using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class OverlayConfigTests
{
    private static AppConfig ConfigWith(string overlayJson) => ConfigLoader.ParseAppConfig($$"""
        {
          "machines": [],
          "profiles": { "opencnc": { "statusBarRoi": { "x": 0, "y": 0, "width": 10, "height": 10 } } },
          "ui": { "overlay": {{overlayJson}} }
        }
        """);

    [Fact]
    public void DomyslneWartosciZgodneZDecyzjaUzytkownika()
    {
        OverlayConfig overlay = new UiConfig().Overlay;

        Assert.False(overlay.Enabled);
        Assert.Equal(0.55, overlay.Opacity);
        Assert.Equal(OverlayPosition.TopLeft, overlay.Position);
        Assert.Equal(1.0, overlay.Scale);
        Assert.False(overlay.ClickThrough);
    }

    [Theory]
    [InlineData("top-left", OverlayPosition.TopLeft)]
    [InlineData("top-right", OverlayPosition.TopRight)]
    [InlineData("bottom-left", OverlayPosition.BottomLeft)]
    [InlineData("bottom-right", OverlayPosition.BottomRight)]
    public void RogNakladkiZapisujeSieZMysllnikiem(string text, OverlayPosition expected)
    {
        AppConfig cfg = ConfigWith($$"""{ "position": "{{text}}" }""");

        Assert.Equal(expected, cfg.Ui.Overlay.Position);
        Assert.Contains($"\"position\": \"{text}\"", ConfigLoader.Serialize(cfg));
    }

    [Fact]
    public void PrzykladowaKonfiguracjaWlaczaNakladkeWLewymGornymRogu()
    {
        OverlayConfig overlay = TestPaths.ExampleConfig.Ui.Overlay;

        Assert.True(overlay.Enabled);
        Assert.Equal(OverlayPosition.TopLeft, overlay.Position);
        Assert.False(overlay.ClickThrough);
    }

    [Theory]
    [InlineData(0.19)]
    [InlineData(1.01)]
    public void ZlaPrzezroczystoscJestOdrzucana(double opacity)
    {
        AppConfig cfg = TestPaths.ExampleConfig with
        {
            Ui = new UiConfig { Overlay = new OverlayConfig { Opacity = opacity } },
        };

        ConfigException error = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains("ui.overlay.opacity", error.Message);
    }

    [Theory]
    [InlineData(0.59)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    public void ZlaSkalaJestOdrzucana(double scale)
    {
        AppConfig cfg = TestPaths.ExampleConfig with
        {
            Ui = new UiConfig { Overlay = new OverlayConfig { Scale = scale } },
        };

        ConfigException error = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains("ui.overlay.scale", error.Message);
    }

    [Fact]
    public void GranicaZakresuJestDopuszczona()
    {
        AppConfig cfg = TestPaths.ExampleConfig with
        {
            Ui = new UiConfig { Overlay = new OverlayConfig { Opacity = 0.2, Scale = 2.0 } },
        };

        ConfigLoader.Validate(cfg);
    }
}
