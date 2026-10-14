using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class RedEpisodeFilterTests
{
    [Fact]
    public void Push_SekwencjaFalszPrawdaPrawda_PotwierdzaEpizod()
    {
        var filter = new RedEpisodeFilter();

        Assert.False(filter.Push(false));
        Assert.False(filter.Push(true));
        Assert.True(filter.Push(true));
        Assert.True(filter.Confirmed);
    }

    [Fact]
    public void Push_SekwencjaPrawdaFalszFalsz_NiePotwierdzaEpizodu()
    {
        var filter = new RedEpisodeFilter();

        Assert.True(!filter.Push(true));
        Assert.False(filter.Push(false));
        Assert.False(filter.Push(false));
        Assert.False(filter.Confirmed);
    }

    [Fact]
    public void Push_SekwencjaPrawdaPrawdaFalsz_PotwierdzaEpizod()
    {
        var filter = new RedEpisodeFilter();

        filter.Push(true);
        Assert.True(filter.Push(true));
        Assert.True(filter.Push(false));
    }

    [Fact]
    public void Push_PoWyjsciuCzerwieniZOkna_PrzestajePotwierdzac()
    {
        var filter = new RedEpisodeFilter();

        filter.Push(true);
        filter.Push(true);
        Assert.True(filter.Confirmed);

        filter.Push(false);
        filter.Push(false);
        Assert.False(filter.Confirmed);
    }

    [Fact]
    public void Reset_CzysciOknoIStanPotwierdzenia()
    {
        var filter = new RedEpisodeFilter();
        filter.Push(true);
        filter.Push(true);
        Assert.True(filter.Confirmed);

        filter.Reset();

        Assert.False(filter.Confirmed);
        Assert.False(filter.Push(true));
    }

    [Fact]
    public void Konstruktor_ZNiepoprawnymiParametrami_RzucaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new RedEpisodeFilter(0));
        Assert.Throws<ArgumentException>(() => new RedEpisodeFilter(3, 0));
        Assert.Throws<ArgumentException>(() => new RedEpisodeFilter(3, 4));
    }

    [Fact]
    public void Push_ZWlasnymOknem_WymagaZadanejLiczbyTrafien()
    {
        var filter = new RedEpisodeFilter(window: 5, required: 3);

        Assert.False(filter.Push(true));
        Assert.False(filter.Push(true));
        Assert.True(filter.Push(true));
    }
}
