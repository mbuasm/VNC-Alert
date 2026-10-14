using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;

namespace MaszynyAlert.Probe.Tests;

/// <summary>Pomiary klatki (<see cref="FrameMetrics"/>) na syntetycznych obrazach — klasyfikacja czerni, macierz czerwieni, motyw, skrót, bbox.</summary>
public sealed class FrameMetricsTests
{
    private static readonly Dictionary<string, ScreenProfile> Profiles = DiagnozaTestConfig.Profiles();

    [Fact]
    public void CzarnaKlatka_JestKlasyfikowanaJakoCzarna()
    {
        FrameMeasurement m = FrameMetrics.Measure(DiagnozaTestConfig.Solid(32, 32, new Rgba(0, 0, 0)), Profiles);

        Assert.Equal(FrameMetrics.KlasaCzarna, m.KlasaCzerni);
        Assert.True(m.IsBlack);
        Assert.Equal(1.0, m.UdzialCiemnych);
        Assert.Equal(FrameMetrics.MotywNieokreslony, m.Motyw);
        Assert.False(m.JakakolwiekCzerwien);
    }

    [Fact]
    public void PrawieCzarnaKlatka_ZKilkomaJasnymiPikselami_JestPrawieCzarna()
    {
        // Wygaszony ekran z jedną diodą/kursorem: 95 % ciemnych pikseli — to nadal „ekran zgasł”, nie „normalny ciemny motyw”.
        RgbaImage img = DiagnozaTestConfig.Solid(20, 20, new Rgba(0, 0, 0));
        for (int i = 0; i < 20; i++)
        {
            img.SetPixel(i, 0, new Rgba(200, 200, 200));
        }

        FrameMeasurement m = FrameMetrics.Measure(img, Profiles);

        Assert.Equal(FrameMetrics.KlasaPrawieCzarna, m.KlasaCzerni);
        Assert.True(m.IsBlack);
    }

    [Fact]
    public void JasnaKlatka_MaMotywJasny_ACiemnaZTrescia_Ciemny()
    {
        FrameMeasurement light = FrameMetrics.Measure(DiagnozaTestConfig.WithRed(32, 32, new RectInt(0, 0, 4, 4)), Profiles);
        Assert.Equal(FrameMetrics.MotywJasny, light.Motyw);
        Assert.Equal(FrameMetrics.KlasaNormalna, light.KlasaCzerni);

        RgbaImage dark = DiagnozaTestConfig.WithRed(32, 32, new RectInt(0, 0, 4, 4), new Rgba(20, 20, 20));
        FrameMeasurement m = FrameMetrics.Measure(dark, Profiles);
        Assert.Equal(FrameMetrics.MotywCiemny, m.Motyw);
        Assert.Equal(FrameMetrics.KlasaNormalna, m.KlasaCzerni);
    }

    [Fact]
    public void MacierzCzerwieni_LiczyKazdyProfilWKazdymRoi()
    {
        FrameMeasurement m = FrameMetrics.Measure(DiagnozaTestConfig.WithRed(32, 32, new RectInt(0, 0, 32, 8)), Profiles);

        Assert.Equal(Profiles.Count * Profiles.Count, m.Czerwien.Count);
        RedCell top = m.Czerwien.Single(c => c.Profil == "opencnc" && c.Roi == "opencnc");
        Assert.Equal(1.0, top.Udzial);
        Assert.True(top.PowyzejProgu);
        RedCell bottom = m.Czerwien.Single(c => c.Profil == "opencnc" && c.Roi == "aidite5x");
        Assert.Equal(0.0, bottom.Udzial);
        Assert.False(bottom.PowyzejProgu);
        Assert.Equal(0.25, m.CzerwienCalaKlatka["opencnc"], 3);
        Assert.True(m.JakakolwiekCzerwien);
        Assert.Equal(new RectInt(0, 0, 32, 8), m.CzerwienBbox["opencnc"]);
    }

    [Fact]
    public void RoiPozaKlatka_DajeZeroIPonizejProgu()
    {
        FrameMeasurement m = FrameMetrics.Measure(DiagnozaTestConfig.WithRed(16, 16, new RectInt(0, 0, 16, 16)), Profiles);

        RedCell cell = m.Czerwien.Single(c => c.Profil == "opencnc" && c.Roi == "aidite5x");
        Assert.Equal(0.0, cell.Udzial);
        Assert.False(cell.PowyzejProgu);
    }

    [Fact]
    public void Hash_TenSamObrazTenSamSkrot_InnyObrazInny()
    {
        RgbaImage a = DiagnozaTestConfig.Solid(8, 8, new Rgba(10, 20, 30));
        RgbaImage b = DiagnozaTestConfig.Solid(8, 8, new Rgba(10, 20, 30));
        b.SetPixel(3, 3, new Rgba(11, 20, 30));

        Assert.Equal(FrameMetrics.Hash(a), FrameMetrics.Hash(DiagnozaTestConfig.Solid(8, 8, new Rgba(10, 20, 30))));
        Assert.NotEqual(FrameMetrics.Hash(a), FrameMetrics.Hash(b));
        Assert.Equal(16, FrameMetrics.Hash(a).Length);
    }

    [Fact]
    public void Odcienie_12KubelkowSumujaSieDoJednegoDlaNasyconychPikseli()
    {
        RgbaImage img = DiagnozaTestConfig.WithRed(10, 10, new RectInt(0, 0, 10, 5), new Rgba(0, 0, 255));
        FrameMeasurement m = FrameMetrics.Measure(img, Profiles);

        Assert.Equal(12, m.Odcienie.Count);
        Assert.Equal(1.0, m.Odcienie.Sum(), 2);
        Assert.Equal(0.5, m.Odcienie[0], 2); // czerwień 0° → kubełek 0
        Assert.Equal(0.5, m.Odcienie[8], 2); // niebieski 240° → kubełek 8
    }

    [Fact]
    public void EpisodeTracker_MeasureRange_ZoltyProstokat_DajeUdzialIBbox()
    {
        RgbaImage img = DiagnozaTestConfig.Solid(20, 10, new Rgba(255, 255, 255));
        for (int y = 2; y < 4; y++)
        {
            for (int x = 5; x < 15; x++)
            {
                img.SetPixel(x, y, new Rgba(255, 220, 0)); // ~52°
            }
        }

        ColorRegion yellow = EpisodeTracker.MeasureRange(img, EpisodeTracker.Yellow);
        ColorRegion red = EpisodeTracker.MeasureRed(img, new RedThresholds());

        Assert.Equal(0.1, yellow.Udzial, 3);
        Assert.Equal(new RectInt(5, 2, 10, 2), yellow.Bbox);
        Assert.Equal(0.0, red.Udzial);
        Assert.Null(red.Bbox);
    }
}
