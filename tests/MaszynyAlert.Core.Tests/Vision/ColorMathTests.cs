using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class ColorMathTests
{
    private static readonly RedThresholds Default = new();

    [Theory]
    [InlineData(255, 0, 0, 0.0, 1.0, 1.0)]
    [InlineData(0, 255, 0, 120.0, 1.0, 1.0)]
    [InlineData(0, 0, 255, 240.0, 1.0, 1.0)]
    [InlineData(255, 255, 0, 60.0, 1.0, 1.0)]
    [InlineData(0, 255, 255, 180.0, 1.0, 1.0)]
    [InlineData(255, 0, 255, 300.0, 1.0, 1.0)]
    [InlineData(255, 255, 255, 0.0, 0.0, 1.0)]
    [InlineData(0, 0, 0, 0.0, 0.0, 0.0)]
    [InlineData(128, 128, 128, 0.0, 0.0, 128 / 255.0)]
    public void RgbToHsv_DlaCzystychKolorow_ZwracaZnaneWartosci(byte r, byte g, byte b, double h, double s, double v)
    {
        (double actualH, double actualS, double actualV) = ColorMath.RgbToHsv(r, g, b);

        Assert.Equal(h, actualH, 3);
        Assert.Equal(s, actualS, 3);
        Assert.Equal(v, actualV, 3);
    }

    [Fact]
    public void IsRed_DlaOdcieniPrzezZero_AkceptujeObieStronyZakresu()
    {
        // Odcień ~350 stopni (powyżej HueMinDeg = 340) oraz ~10 stopni (poniżej HueMaxDeg = 20).
        Assert.True(ColorMath.IsRed(new Rgba(255, 0, 43), Default));
        Assert.True(ColorMath.IsRed(new Rgba(255, 42, 0), Default));
        Assert.True(ColorMath.IsRed(new Rgba(255, 0, 0), Default));
    }

    [Fact]
    public void IsRed_DlaOdcieniaPomaranczowego_ZwracaFalsz()
    {
        // Odcień ~30 stopni — poza zakresem (20, 340).
        Assert.False(ColorMath.IsRed(new Rgba(255, 128, 0), Default));
    }

    [Fact]
    public void IsRed_DlaJasnegoRozu_ZwracaFalszPrzezNiskieNasycenie()
    {
        Assert.False(ColorMath.IsRed(new Rgba(250, 180, 180), Default));
    }

    [Fact]
    public void IsRed_DlaBardzoCiemnejCzerwieni_ZwracaFalszPrzezNiskaJasnosc()
    {
        Assert.False(ColorMath.IsRed(new Rgba(60, 0, 0), Default));
    }

    [Fact]
    public void IsRed_DlaPikselaPrawiePrzezroczystego_ZwracaFalsz()
    {
        Assert.False(ColorMath.IsRed(new Rgba(255, 0, 0, 127), Default));
        Assert.True(ColorMath.IsRed(new Rgba(255, 0, 0, 128), Default));
    }

    [Fact]
    public void IsRed_DlaZwyklegoZakresuOdcieni_DzialaBezPrzejsciaPrzezZero()
    {
        var green = new RedThresholds { HueMinDeg = 100, HueMaxDeg = 140, MinSaturation = 0.5, MinValue = 0.3 };

        Assert.True(ColorMath.IsRed(new Rgba(0, 255, 0), green));
        Assert.False(ColorMath.IsRed(new Rgba(255, 0, 0), green));
        Assert.False(ColorMath.IsRed(new Rgba(0, 0, 255), green));
    }

    [Fact]
    public void IsRed_DlaBieliICzerni_ZwracaFalsz()
    {
        Assert.False(ColorMath.IsRed(new Rgba(255, 255, 255), Default));
        Assert.False(ColorMath.IsRed(new Rgba(0, 0, 0), Default));
    }
}
