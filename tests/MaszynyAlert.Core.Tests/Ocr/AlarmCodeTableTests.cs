using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class AlarmCodeTableTests
{
    [Fact]
    public void TryResolve_PojedynczegoNumeru_ZwracaOpisKategorieIPriorytet()
    {
        var table = new AlarmCodeTable([new AlarmCode { Code = 1200, ShortText = "Bazowanie (Home)", Category = "restart", Priority = 50 }]);

        Assert.True(table.TryResolve(1200, out ResolvedAlarmCode r));
        Assert.Equal(1200, r.Code);
        Assert.Equal("Bazowanie (Home)", r.ShortText);
        Assert.Equal("restart", r.Category);
        Assert.Equal(50, r.Priority);
        Assert.False(table.TryResolve(1201, out _));
        Assert.Empty(table.Warnings);
        Assert.Equal(1, table.Count);
    }

    [Theory]
    [InlineData(700, "Servo 1")]
    [InlineData(705, "Servo 6")]
    [InlineData(719, "Servo 20")]
    public void TryResolve_NumeruZZakresu_PodstawiaPozycjeLiczonaOdJeden(int code, string expected)
    {
        var table = new AlarmCodeTable([new AlarmCode { From = 700, To = 719, ShortText = "Servo {n}" }]);

        Assert.True(table.TryResolve(code, out ResolvedAlarmCode r));
        Assert.Equal(expected, r.ShortText);
        Assert.Equal("inny", r.Category); // domyślna kategoria, gdy nie podano
        Assert.Equal(0, r.Priority);
    }

    [Fact]
    public void TryResolve_PozaZakresem_ZwracaFalsz()
    {
        var table = new AlarmCodeTable([new AlarmCode { From = 700, To = 719, ShortText = "Servo {n}" }]);

        Assert.False(table.TryResolve(699, out _));
        Assert.False(table.TryResolve(720, out _));
    }

    [Fact]
    public void TryResolve_PojedynczyNumerWygrywaZZakresem()
    {
        // Dlaczego: pozwala nadpisać jeden numer z zakresu własnym opisem (np. „Servo 6 (oś tacek)”)
        // bez rozbijania zakresu na kawałki.
        var table = new AlarmCodeTable(
        [
            new AlarmCode { From = 700, To = 719, ShortText = "Servo {n}" },
            new AlarmCode { Code = 705, ShortText = "Servo 6 (oś tacek)" },
        ]);

        Assert.True(table.TryResolve(705, out ResolvedAlarmCode r));
        Assert.Equal("Servo 6 (oś tacek)", r.ShortText);
        Assert.True(table.TryResolve(704, out r));
        Assert.Equal("Servo 5", r.ShortText);
    }

    [Fact]
    public void Konstruktor_WpisyBledne_PomijaZOstrzezeniem()
    {
        var table = new AlarmCodeTable(
        [
            new AlarmCode { ShortText = "bez numeru" },
            new AlarmCode { Code = 1, ShortText = "" },
            new AlarmCode { From = 10, To = 5, ShortText = "odwrócony" },
            new AlarmCode { From = 10, ShortText = "bez końca" },
            new AlarmCode { Code = 20, From = 20, To = 25, ShortText = "code i zakres naraz" },
            new AlarmCode { Code = 30, ShortText = "pierwszy" },
            new AlarmCode { Code = 30, ShortText = "dubel" },
        ]);

        Assert.Equal(6, table.Warnings.Count);
        Assert.Equal(1, table.Count);
        Assert.True(table.TryResolve(30, out ResolvedAlarmCode r));
        Assert.Equal("pierwszy", r.ShortText);
        Assert.False(table.TryResolve(20, out _));
    }

    [Fact]
    public void PrzykladowySlownik_PokrywaKomunikatyZHaliIJestSpojny()
    {
        var table = new AlarmCodeTable(TestPaths.ExampleAidite.Codes);

        Assert.Empty(table.Warnings);
        Assert.True(table.Count >= 100, $"Oczekiwano co najmniej 100 wpisów kodów, jest {table.Count}.");

        foreach ((int code, string expected) in new[] { (705, "Servo 6"), (725, "Servo 6 niewłączone"), (1200, "Bazowanie (Home)"), (520, "Błąd makra"), (2502, "Błąd wykonania"), (1615, "Narzędzie – pomiar poza tolerancją") })
        {
            Assert.True(table.TryResolve(code, out ResolvedAlarmCode r), $"Brak kodu {code} w rules.example.json.");
            Assert.Equal(expected, r.ShortText);
        }

        Assert.All(TestPaths.ExampleAidite.Codes, c =>
        {
            Assert.True(c.ShortText.Length <= 40, $"Opis kodu {c.Label} jest za długi ({c.ShortText.Length} znaków): \"{c.ShortText}\".");
            // „nie błąd” (Rule.IgnoredCategory) — znany komunikat informacyjny, np. No.4534 „plan produkcji
            // wykonany”: dopasowuje się jawnie, ale nigdy nie opisuje epizodu czerwieni.
            Assert.Contains(c.Category, new[] { "narzedzie", "tacka", "restart", "inny", Rule.IgnoredCategory });
            Assert.False(string.IsNullOrWhiteSpace(c.Description), $"Kod {c.Label} bez pola description (oryginalnej treści z listy producenta).");
        });
    }

    [Fact]
    public void PrzykladowySlownik_PriorytetyCykluAwariiSerwa_SaWKolejnosciZHali()
    {
        var table = new AlarmCodeTable(TestPaths.ExampleAidite.Codes);
        int Priority(int code)
        {
            Assert.True(table.TryResolve(code, out ResolvedAlarmCode r));
            return r.Priority;
        }

        Assert.True(Priority(705) > Priority(725));
        Assert.True(Priority(725) > Priority(1200));
        Assert.True(Priority(1200) > Priority(520));
        Assert.True(Priority(520) > Priority(2502));
    }
}
