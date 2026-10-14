using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class RedTextDetectorTests
{
    private static readonly ScreenProfile OpenCnc = TestPaths.Profile("opencnc");
    private static readonly ScreenProfile Amm580 = TestPaths.Profile("amm580");

    private static RgbaImage Bar(Rgba background, Rgba text, string? seed, double coverage = 0.02)
        => SyntheticScreens.StatusBar(
            OpenCnc.StatusBarRoi.Width, OpenCnc.StatusBarRoi.Height, background, text, seed, coverage);

    [Fact]
    public void Analyze_PaskaZBialymTekstem_NieWykrywaCzerwieni()
    {
        RgbaImage bar = Bar(SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "normalny");

        RedDetectionResult result = new RedTextDetector(OpenCnc).Analyze(bar);

        Assert.False(result.IsRed);
        Assert.True(result.RedFraction < 0.001, $"Udział czerwieni {result.RedFraction:P4} powinien być poniżej 0,1 %.");
        Assert.Null(result.RedBounds);
    }

    [Fact]
    public void Analyze_PaskaZZoltymTekstem_NieWykrywaCzerwieni()
    {
        RgbaImage bar = Bar(SyntheticScreens.LightBackground, SyntheticScreens.YellowText, "ostrzezenie");

        RedDetectionResult result = new RedTextDetector(OpenCnc).Analyze(bar);

        Assert.False(result.IsRed);
        Assert.True(result.RedFraction < 0.001, $"Udział czerwieni {result.RedFraction:P4} powinien być poniżej 0,1 %.");
    }

    [Fact]
    public void Analyze_PustegoPaska_NieWykrywaCzerwieniIZwracaZeroPikseli()
    {
        RgbaImage bar = Bar(SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, seed: null);

        RedDetectionResult result = new RedTextDetector(OpenCnc).Analyze(bar);

        Assert.False(result.IsRed);
        Assert.Equal(0, result.RedPixels);
        Assert.Equal(bar.Width * bar.Height, result.TotalPixels);
        Assert.Null(result.RedBounds);
    }

    [Fact]
    public void Analyze_PaskaZCzerwonymTekstem_WykrywaCzerwienZOczekiwanymUdzialem()
    {
        RgbaImage bar = Bar(SyntheticScreens.LightBackground, SyntheticScreens.RedText, "blad");

        RedDetectionResult result = new RedTextDetector(OpenCnc).Analyze(bar);

        Assert.True(result.IsRed);
        Assert.InRange(result.RedFraction, 0.01, 0.04);
        Assert.Equal(result.RedPixels / (double)result.TotalPixels, result.RedFraction, 9);
    }

    [Fact]
    public void Analyze_PaskaZCzerwonymTekstem_ZwracaProstokatObejmujacyWszystkieCzerwonePiksele()
    {
        RgbaImage bar = Bar(SyntheticScreens.LightBackground, SyntheticScreens.RedText, "blad-bbox");
        RedDetectionResult result = new RedTextDetector(OpenCnc).Analyze(bar);

        RectInt bounds = Assert.IsType<RectInt>(result.RedBounds);
        int counted = 0;
        for (int y = 0; y < bar.Height; y++)
        {
            for (int x = 0; x < bar.Width; x++)
            {
                if (ColorMath.IsRed(bar.GetPixel(x, y), OpenCnc.Red))
                {
                    counted++;
                    Assert.True(bounds.Contains(x, y), $"Czerwony piksel ({x},{y}) leży poza zwróconym prostokątem {bounds}.");
                }
            }
        }

        Assert.Equal(counted, result.RedPixels);
    }

    [Fact]
    public void Analyze_CiemnegoMotywuZCzerwonymTekstem_WykrywaCzerwien()
    {
        RgbaImage bar = SyntheticScreens.StatusBar(
            Amm580.StatusBarRoi.Width, Amm580.StatusBarRoi.Height,
            SyntheticScreens.DarkBackground, SyntheticScreens.RedText, "ciemny-blad");

        RedDetectionResult result = new RedTextDetector(Amm580).Analyze(bar);

        Assert.True(result.IsRed);
        Assert.True(result.RedFraction >= Amm580.MinRedFraction);
    }

    [Fact]
    public void AnalyzeFullFrame_LiczyTylkoObszarPaskaStatusu()
    {
        RgbaImage bar = Bar(SyntheticScreens.LightBackground, SyntheticScreens.RedText, "roi");
        RgbaImage frame = SyntheticScreens.FullFrame(OpenCnc, bar);

        // Poza ROI malujemy duży czerwony prostokąt — nie może wpłynąć na wynik.
        var noise = new RgbaImage(frame.Width, 30);
        noise.Fill(new Rgba(255, 0, 0));
        frame.Blit(noise, 0, OpenCnc.StatusBarRoi.Bottom + 5);

        var detector = new RedTextDetector(OpenCnc);
        RedDetectionResult fromFrame = detector.AnalyzeFullFrame(frame);
        RedDetectionResult fromBar = detector.Analyze(bar);

        Assert.Equal(fromBar.RedPixels, fromFrame.RedPixels);
        Assert.Equal(fromBar.TotalPixels, fromFrame.TotalPixels);
        Assert.Equal(fromBar.RedFraction, fromFrame.RedFraction, 9);
        Assert.True(fromFrame.IsRed);
    }

    [Fact]
    public void AnalyzeFullFrame_GdyRoiPozaKlatka_ZwracaWynikPusty()
    {
        var tinyFrame = new RgbaImage(10, 10);
        tinyFrame.Fill(new Rgba(255, 0, 0));

        ScreenProfile profile = OpenCnc with { StatusBarRoi = new RectInt(500, 500, 100, 20) };
        RedDetectionResult result = new RedTextDetector(profile).AnalyzeFullFrame(tinyFrame);

        Assert.False(result.IsRed);
        Assert.Equal(0, result.TotalPixels);
        Assert.Null(result.RedBounds);
    }

    [Fact]
    public void AnalyzeStatyczne_ZWlasnymiProgami_UzywaPodanegoProguUdzialu()
    {
        var img = new RgbaImage(10, 10);
        img.Fill(new Rgba(255, 255, 255));
        img.SetPixel(0, 0, new Rgba(220, 30, 30));
        img.SetPixel(9, 9, new Rgba(220, 30, 30));

        var thresholds = new RedThresholds();

        RedDetectionResult low = RedTextDetector.Analyze(img, thresholds, minFraction: 0.01);
        RedDetectionResult high = RedTextDetector.Analyze(img, thresholds, minFraction: 0.5);

        Assert.Equal(2, low.RedPixels);
        Assert.Equal(0.02, low.RedFraction, 9);
        Assert.True(low.IsRed);
        Assert.False(high.IsRed);
        Assert.Equal(new RectInt(0, 0, 10, 10), low.RedBounds);
    }

    [Fact]
    public void Analyze_GdyUdzialDokladnieRownyProgowi_UznajeZaCzerwien()
    {
        var img = new RgbaImage(10, 10);
        img.Fill(new Rgba(255, 255, 255));
        img.SetPixel(5, 5, new Rgba(255, 0, 0));

        RedDetectionResult result = RedTextDetector.Analyze(img, new RedThresholds(), minFraction: 0.01);

        Assert.True(result.IsRed);
    }
}
