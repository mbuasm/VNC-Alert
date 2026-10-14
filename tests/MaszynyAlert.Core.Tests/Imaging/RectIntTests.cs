using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Tests.Imaging;

public sealed class RectIntTests
{
    [Fact]
    public void RightBottom_DlaProstokata_SumaPozycjiIRozmiaru()
    {
        var r = new RectInt(10, 20, 30, 40);
        Assert.Equal(40, r.Right);
        Assert.Equal(60, r.Bottom);
    }

    [Fact]
    public void Intersect_GdyProstokatySieNakladaja_ZwracaCzescWspolna()
    {
        var a = new RectInt(0, 0, 100, 50);
        var b = new RectInt(80, 10, 100, 100);
        Assert.Equal(new RectInt(80, 10, 20, 40), a.Intersect(b));
    }

    [Fact]
    public void Intersect_GdyProstokatySieNieNakladaja_ZwracaEmpty()
    {
        var a = new RectInt(0, 0, 10, 10);
        var b = new RectInt(20, 20, 10, 10);
        Assert.Equal(RectInt.Empty, a.Intersect(b));
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Intersect_GdyStykajaSieKrawedziami_ZwracaEmpty()
    {
        var a = new RectInt(0, 0, 10, 10);
        var b = new RectInt(10, 0, 10, 10);
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Clamp_GdyProstokatWystajePozaObraz_PrzycinaDoGranic()
    {
        var r = new RectInt(-10, -5, 100, 100);
        Assert.Equal(new RectInt(0, 0, 50, 40), r.Clamp(50, 40));
    }

    [Fact]
    public void Clamp_GdyProstokatCalkowiciePozaObrazem_ZwracaPusty()
    {
        var r = new RectInt(200, 200, 10, 10);
        Assert.True(r.Clamp(100, 100).IsEmpty);
    }

    [Fact]
    public void Clamp_GdyProstokatMiesciSieWCalosci_NieZmieniaGo()
    {
        var r = new RectInt(5, 5, 10, 10);
        Assert.Equal(r, r.Clamp(100, 100));
    }

    [Fact]
    public void Contains_DlaPunktowNaKrawedziach_LewaGornaWliczonaPrawaDolnaNie()
    {
        var r = new RectInt(10, 10, 5, 5);
        Assert.True(r.Contains(10, 10));
        Assert.True(r.Contains(14, 14));
        Assert.False(r.Contains(15, 14));
        Assert.False(r.Contains(14, 15));
        Assert.False(r.Contains(9, 10));
    }

    [Fact]
    public void Inflate_DlaDodatnichWartosci_PowiekszaZKazdejStrony()
    {
        var r = new RectInt(10, 10, 20, 20);
        Assert.Equal(new RectInt(8, 7, 24, 26), r.Inflate(2, 3));
    }

    [Fact]
    public void Inflate_DlaUjemnychWartosci_ZmniejszaProstokat()
    {
        var r = new RectInt(10, 10, 20, 20);
        Assert.Equal(new RectInt(12, 12, 16, 16), r.Inflate(-2, -2));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, true)]
    [InlineData(0, 0, 10, 0, true)]
    [InlineData(0, 0, 0, 10, true)]
    [InlineData(0, 0, -1, 5, true)]
    [InlineData(0, 0, 1, 1, false)]
    public void IsEmpty_DlaRoznychRozmiarow_ZwracaOczekiwanaWartosc(int x, int y, int w, int h, bool expected)
    {
        Assert.Equal(expected, new RectInt(x, y, w, h).IsEmpty);
    }

    [Fact]
    public void Empty_ZawszeZwracaProstokatZerowy()
    {
        Assert.Equal(new RectInt(0, 0, 0, 0), RectInt.Empty);
    }
}
