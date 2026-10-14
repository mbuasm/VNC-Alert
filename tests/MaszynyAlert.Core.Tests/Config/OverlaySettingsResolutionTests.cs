using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class OverlaySettingsResolutionTests
{
    private static readonly OverlayConfig Wlaczona = new() { Enabled = true };
    private static readonly OverlayConfig Wylaczona = new() { Enabled = false };

    [Fact]
    public void BezWyboruOperatoraObowiazujeUstawienieStanowiska()
    {
        var settings = new OverlaySettings();

        Assert.True(settings.ResolveEnabled(Wlaczona));
        Assert.False(settings.ResolveEnabled(Wylaczona));
    }

    [Fact]
    public void WyborOperatoraWygrywaDopokiKonfiguracjaSieNieZmienia()
    {
        OverlaySettings settings = new OverlaySettings().WithEnabled(false, Wlaczona);

        Assert.False(settings.ResolveEnabled(Wlaczona));
    }

    [Fact]
    public void ZmianaUstawieniaStanowiskaUniewaznaZapamietanyWybor()
    {
        // Operator schował nakładkę przy ui.overlay.enabled:false, administrator włączył ją w config.json.
        OverlaySettings settings = new OverlaySettings().WithEnabled(false, Wylaczona);

        Assert.True(settings.ResolveEnabled(Wlaczona));
    }

    [Fact]
    public void StaryPlikBezZapamietanegoUstawieniaOddajePierwszenstwoKonfiguracji()
    {
        var settings = new OverlaySettings { Enabled = false };

        Assert.True(settings.ResolveEnabled(Wlaczona));
        Assert.False(settings.ResolveEnabled(Wylaczona));
    }

    [Fact]
    public void PrzepuszczanieKlikniecDzialaNaTychSamychZasadach()
    {
        var clickThroughOn = new OverlayConfig { ClickThrough = true };
        var clickThroughOff = new OverlayConfig { ClickThrough = false };
        OverlaySettings settings = new OverlaySettings().WithClickThrough(true, clickThroughOff);

        // Wybór operatora obowiązuje przy niezmienionym ustawieniu stanowiska…
        Assert.True(settings.ResolveClickThrough(clickThroughOff));
        // …a po jego zmianie wraca wartość z config.json.
        Assert.False(new OverlaySettings().WithClickThrough(true, clickThroughOn).ResolveClickThrough(clickThroughOff));
        Assert.False(new OverlaySettings().ResolveClickThrough(clickThroughOff));
        Assert.True(new OverlaySettings { ClickThrough = false }.ResolveClickThrough(clickThroughOn));
    }

    [Fact]
    public void ZapisWyboruNieRuszaPozostalychPol()
    {
        var settings = new OverlaySettings { Left = 120, Top = 40, ClickThrough = true, ClickThroughDefault = false };

        OverlaySettings updated = settings.WithEnabled(true, Wylaczona);

        Assert.True(updated.Enabled);
        Assert.False(updated.EnabledDefault);
        Assert.True(updated.ClickThrough);
        Assert.False(updated.ClickThroughDefault);
        Assert.Equal(120, updated.Left);
        Assert.Equal(40, updated.Top);
    }

    [Fact]
    public void WyborOperatoraNieOzywaPoPowrocieAdministratoraDoStarejWartosci()
    {
        var a = new OverlayConfig { Layout = OverlayLayout.OneRow, Enabled = true, ClickThrough = false };
        var b = new OverlayConfig { Layout = OverlayLayout.TwoColumns, Enabled = false, ClickThrough = true };
        OverlaySettings chosen = new OverlaySettings()
            .WithLayout(OverlayLayout.ThreeColumns, a)
            .WithEnabled(false, a)
            .WithClickThrough(true, a);

        OverlaySettings afterB = chosen.Normalize(b);

        Assert.Equal(OverlayLayout.OneRow, afterB.ResolveLayout(a));
        Assert.True(afterB.ResolveEnabled(a));
        Assert.False(afterB.ResolveClickThrough(a));
    }

    [Fact]
    public void Normalize_BezZmianyUstawieniaZostawiaWyborOperatora()
    {
        var a = new OverlayConfig { Layout = OverlayLayout.OneRow };
        OverlaySettings chosen = new OverlaySettings { Left = 10, Top = 20 }.WithLayout(OverlayLayout.ThreeColumns, a);

        Assert.Same(chosen, chosen.Normalize(a));
    }
}
