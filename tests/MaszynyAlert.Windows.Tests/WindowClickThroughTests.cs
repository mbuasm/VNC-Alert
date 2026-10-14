using MaszynyAlert.Windows;

namespace MaszynyAlert.Windows.Tests;

public sealed class WindowClickThroughTests
{
    private const int OtherStyles = 0x0000_0008 | 0x0800_0000; // WS_EX_TOPMOST | WS_EX_NOACTIVATE

    [Fact]
    public void WlaczenieDokladaPrzezroczystoscIWarstwe()
    {
        int result = WindowClickThrough.NextExStyle(OtherStyles, clickThrough: true);

        Assert.Equal(WindowClickThrough.WsExTransparent, result & WindowClickThrough.WsExTransparent);
        Assert.Equal(WindowClickThrough.WsExLayered, result & WindowClickThrough.WsExLayered);
        Assert.Equal(OtherStyles, result & OtherStyles);
    }

    [Fact]
    public void WylaczenieZdejmujeTylkoPrzezroczystoscMyszy()
    {
        // Dlaczego: warstwę (WS_EX_LAYERED) trzyma też przezroczyste tło okna WPF — jej zdjęcie zepsułoby
        // wygląd nakładki, a nie o to prosi wyłączenie przenikania kliknięć.
        int enabled = WindowClickThrough.NextExStyle(OtherStyles, clickThrough: true);

        int result = WindowClickThrough.NextExStyle(enabled, clickThrough: false);

        Assert.Equal(0, result & WindowClickThrough.WsExTransparent);
        Assert.Equal(WindowClickThrough.WsExLayered, result & WindowClickThrough.WsExLayered);
        Assert.Equal(OtherStyles, result & OtherStyles);
    }

    [Fact]
    public void PowtorzonaZmianaNiczegoNieDoklada()
    {
        int once = WindowClickThrough.NextExStyle(OtherStyles, clickThrough: true);

        Assert.Equal(once, WindowClickThrough.NextExStyle(once, clickThrough: true));
        Assert.Equal(
            WindowClickThrough.NextExStyle(OtherStyles, clickThrough: false),
            WindowClickThrough.NextExStyle(WindowClickThrough.NextExStyle(OtherStyles, clickThrough: false), clickThrough: false));
    }

    [Fact]
    public void PustyUchwytOknaJestOdrzucany()
    {
        Assert.Throws<ArgumentException>(() => WindowClickThrough.Apply(0, clickThrough: true));
    }
}
