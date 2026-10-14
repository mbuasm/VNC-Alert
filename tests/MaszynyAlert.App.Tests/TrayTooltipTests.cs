using MaszynyAlert.App.Services;

namespace MaszynyAlert.App.Tests;

public sealed class TrayTooltipTests
{
    [Fact]
    public void NajdluzszyWariant_NieTnieWPolowieSlowa()
    {
        string tooltip = TrayIconService.BuildTooltip("monitorowanie wyłączone", 12, 12, 12);

        Assert.True(tooltip.Length <= 63, $"{tooltip.Length} znaków: {tooltip}");
        Assert.True(tooltip.EndsWith(": 12", StringComparison.Ordinal), tooltip);
    }

    [Fact]
    public void KrotkiWariant_ZawieraWszystkieLiczniki()
    {
        Assert.Equal("VNC Alert – jeden komputer – alerty: 1, stoją: 2, ekran: 3",
            TrayIconService.BuildTooltip("jeden komputer", 1, 2, 3));
    }
}
