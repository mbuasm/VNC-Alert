using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class LevenshteinTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("", "", 0)]
    [InlineData("takie samo", "takie samo", 0)]
    [InlineData("abc", "abd", 1)]
    [InlineData("flaw", "lawn", 2)]
    public void Distance_DlaZnanychPar_ZwracaOczekiwanaOdleglosc(string a, string b, int expected)
    {
        Assert.Equal(expected, Levenshtein.Distance(a, b));
    }

    [Fact]
    public void Distance_JestSymetryczna()
    {
        Assert.Equal(Levenshtein.Distance("maszyna", "mazyna"), Levenshtein.Distance("mazyna", "maszyna"));
    }

    [Fact]
    public void FuzzyContains_GdyIgłaWystępujeDokładnie_ZwracaPrawde()
    {
        Assert.True(Levenshtein.FuzzyContains("ERROR: FAILED TO GRAB A TRAY", "FAILED TO GRAB A TRAY", 0));
    }

    [Fact]
    public void FuzzyContains_ZDwiemaLiterowkami_ZwracaPrawde()
    {
        Assert.True(Levenshtein.FuzzyContains("FALED TO GRAB A TRAV", "FAILED TO GRAB A TRAY", 2));
    }

    [Fact]
    public void FuzzyContains_ZWiecejNizDwiemaLiterowkami_ZwracaFalsz()
    {
        Assert.False(Levenshtein.FuzzyContains("FXLXD TX GRAB A TRAV", "FAILED TO GRAB A TRAY", 2));
    }

    [Fact]
    public void FuzzyContains_DlaPustejIgly_ZwracaFalsz()
    {
        Assert.False(Levenshtein.FuzzyContains("cokolwiek", "", 2));
        Assert.False(Levenshtein.FuzzyContains("", "", 2));
    }

    [Fact]
    public void FuzzyContains_GdyIglaDluzszaOdStogu_ZwracaFalsz()
    {
        Assert.False(Levenshtein.FuzzyContains("AB", "BARDZO DLUGA IGLA", 2));
    }

    [Fact]
    public void FuzzyContains_ZUjemnaTolerancja_WymagaDokladnegoDopasowania()
    {
        Assert.True(Levenshtein.FuzzyContains("ABCDEF", "CDE", -1));
        Assert.False(Levenshtein.FuzzyContains("ABCDEF", "CDX", -1));
    }
}
