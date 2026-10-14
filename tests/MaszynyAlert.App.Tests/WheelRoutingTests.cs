using MaszynyAlert.App.Views;

namespace MaszynyAlert.App.Tests;

public sealed class WheelRoutingTests
{
    [Theory]
    [InlineData(0, 0, -120, false)]     // tabela bez paska — strona
    [InlineData(0, 0, 120, false)]
    [InlineData(0, 300, -120, true)]    // na górze listy, kółko w dół — lista
    [InlineData(0, 300, 120, false)]    // na górze listy, kółko w górę — strona
    [InlineData(300, 300, -120, false)] // na dole listy, kółko w dół — strona
    [InlineData(300, 300, 120, true)]
    [InlineData(150, 300, -120, true)]
    public void ListaPrzewijaSieTylkoGdyMaDokad(double offset, double scrollable, int delta, bool inner)
    {
        Assert.Equal(inner, WheelRouting.InnerCanScroll(offset, scrollable, delta));
    }
}
