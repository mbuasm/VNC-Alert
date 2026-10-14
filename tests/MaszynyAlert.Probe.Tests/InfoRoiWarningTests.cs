using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

public sealed class InfoRoiWarningTests
{
    [Fact]
    public void GdyObszarMiesciSieWEkranie_NieMaOstrzezenia()
    {
        var roi = new RectInt(150, 0, 460, 32);

        Assert.Null(InfoCommand.RoiWarning(roi, roi.Clamp(800, 600), 800, 600));
    }

    [Fact]
    public void GdyObszarLezyCalkowiciePozaEkranem_MowiToWprost()
    {
        var roi = new RectInt(0, 728, 1024, 40);

        string? warning = InfoCommand.RoiWarning(roi, roi.Clamp(800, 600), 800, 600);

        Assert.NotNull(warning);
        Assert.Contains("CAŁKOWICIE poza ekranem", warning, StringComparison.Ordinal);
        Assert.Contains("800x600", warning, StringComparison.Ordinal);
        Assert.Contains("statusBarRoi", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void GdyObszarTylkoWystaje_PodajeIleZostaloPoPrzycieciu()
    {
        var roi = new RectInt(700, 0, 460, 32);

        string? warning = InfoCommand.RoiWarning(roi, roi.Clamp(800, 600), 800, 600);

        Assert.NotNull(warning);
        Assert.Contains("wystaje poza ekran", warning, StringComparison.Ordinal);
        Assert.Contains("100x32", warning, StringComparison.Ordinal);
    }
}
