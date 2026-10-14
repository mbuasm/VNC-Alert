using System.Text.RegularExpressions;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Export;

public sealed class ReportPresentationNamesTests
{
    [Theory]
    [InlineData("Tacka nie pobrana", "Ramka niepobrana")]
    [InlineData("Tacka nie odłożona", "Ramka nieodłożona")]
    [InlineData("Nie znaleziono tacki", "Nie znaleziono ramki")]
    [InlineData("Nr tacki = 0", "Nr ramki = 0")]
    [InlineData("Nr tacki poza magazynem", "Nr ramki poza magazynem")]
    [InlineData("Miejsce tacki zajęte", "Miejsce ramki zajęte")]
    [InlineData("tacka nie pobrana", "ramka niepobrana")]
    [InlineData("Brak tacek w magazynie", "Brak ramek w magazynie")]
    [InlineData("Problem z tacką", "Problem z ramką")]
    [InlineData("Odłóż tackę", "Odłóż ramkę")]
    [InlineData("Na tacce leży detal", "Na ramce leży detal")]
    [InlineData("  Tacka nie pobrana  ", "Ramka niepobrana")]
    public void Tacka_JakoRamka(string reason, string expected) =>
        Assert.Equal(expected, ReportPresentationNames.ErrorName(reason));

    [Theory]
    [InlineData("Servo 6")]
    [InlineData("Bazowanie (Home)")]
    [InlineData("Narzędzie nie wróciło do magazynu")]
    [InlineData("Program główny nie wczytany")] // „nie” bez tacki — bez sklejania
    [InlineData("Detal nie pobrany")]
    [InlineData("Tackarz")] // nie odmiana „tacki”
    [InlineData("Kontakt")]
    public void TekstBezTacki_BezZmian(string reason) => Assert.Equal(reason, ReportPresentationNames.ErrorName(reason));

    [Fact]
    public void WielkieLitery_TackaJakoRamka()
    {
        string name = ReportPresentationNames.ErrorName("TACKA NIE POBRANA");
        Assert.StartsWith("RAMKA", name, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("tack", RegexOptions.IgnoreCase), name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PustyTekst_BladBezOpisu(string? reason) =>
        Assert.Equal("Błąd (brak opisu)", ReportPresentationNames.ErrorName(reason));

    /// <summary>Każdy krótki opis ze słownika (wszystkie bazy) z „tack…” wychodzi bez „tack”, a „nie pobrana/odłożona” łącznie.</summary>
    [Fact]
    public void SlownikPrzykladowy_KazdyOpisTackiBezSlowaTacka()
    {
        string json = File.ReadAllText(TestPaths.RulesExample);
        var texts = Regex.Matches(json, "\"shortText\"\\s*:\\s*\"([^\"]*)\"").Select(m => m.Groups[1].Value).Distinct().ToArray();
        var trays = texts.Where(t => Regex.IsMatch(t, "tack", RegexOptions.IgnoreCase)).ToArray();

        Assert.True(trays.Length >= 6, $"opisów z tacką: {trays.Length}");
        foreach (string text in trays)
        {
            string name = ReportPresentationNames.ErrorName(text);
            Assert.DoesNotMatch(new Regex("tack", RegexOptions.IgnoreCase), name);
            Assert.DoesNotMatch(new Regex(@"\bnie (pobran|odłożon)"), name);
            Assert.Contains("amk", name, StringComparison.Ordinal);
        }
        foreach (string text in texts.Except(trays))
            Assert.Equal(text.Trim(), ReportPresentationNames.ErrorName(text));
    }

    [Theory]
    [InlineData("tacka", "Ramka")]
    [InlineData("TACKA", "Ramka")]
    [InlineData(" tacka ", "Ramka")]
    [InlineData("narzedzie", "Narzędzie")]
    [InlineData("restart", "Restart")]
    [InlineData("inny", "Inny")]
    [InlineData("nierozpoznany", "Nierozpoznany")]
    [InlineData(null, "Nierozpoznany")]
    [InlineData("", "Nierozpoznany")]
    [InlineData("  ", "Nierozpoznany")]
    [InlineData("nie błąd", "Nierozpoznany")]
    [InlineData("bez alarmu", "Nierozpoznany")]
    [InlineData("servo", "Servo")]
    [InlineData("ł", "Ł")]
    public void GrupaBledu_PoPolsku(string? category, string expected) =>
        Assert.Equal(expected, ReportPresentationNames.CategoryName(category));

    /// <summary>Każda kategoria błędu ze słownika ma polską nazwę grupy (bez „tacka” i bez „narzedzie” bez ogonków).</summary>
    [Fact]
    public void SlownikPrzykladowy_KazdaKategoriaMaNazweGrupy()
    {
        string json = File.ReadAllText(TestPaths.RulesExample);
        var categories = Regex.Matches(json, "\"category\"\\s*:\\s*\"([^\"]*)\"").Select(m => m.Groups[1].Value).Distinct();
        foreach (string category in categories)
        {
            string name = ReportPresentationNames.CategoryName(category);
            Assert.NotEqual("Tacka", name);
            Assert.NotEqual("Narzedzie", name);
            Assert.True(char.IsUpper(name[0]), name);
        }
    }

    private static DowntimeSlice Slice(bool interrupted, bool ongoing, bool isLong)
    {
        var start = DateTimeOffset.Parse("2026-09-15T08:00:00Z");
        return new DowntimeSlice(new DateOnly(2026, 9, 15), "06:00–14:00", "Aidite 1", "Servo 6", start, start.AddMinutes(10),
            interrupted, ongoing) { IsLong = isLong };
    }

    [Theory]
    [InlineData(false, false, false, "Zakończony")]
    [InlineData(false, true, false, "Otwarty epizod")]
    [InlineData(true, false, false, "Przerwana obserwacja")]
    [InlineData(true, true, false, "Przerwana obserwacja")]
    [InlineData(false, false, true, "Zakończony, długi postój (> 1 h)")]
    [InlineData(false, true, true, "Otwarty epizod, długi postój (> 1 h)")]
    [InlineData(true, false, true, "Przerwana obserwacja, długi postój (> 1 h)")]
    [InlineData(true, true, true, "Przerwana obserwacja, długi postój (> 1 h)")]
    public void Stan_JakWPanelu(bool interrupted, bool ongoing, bool isLong, string expected) =>
        Assert.Equal(expected, ReportPresentationNames.StateText(Slice(interrupted, ongoing, isLong)));

    [Fact]
    public void Stan_NullRzucaArgumentNull() =>
        Assert.Throws<ArgumentNullException>(() => ReportPresentationNames.StateText(null!));
}
