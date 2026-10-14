using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class TextTruncationTests
{
    [Fact]
    public void MarkIfCut_GdyCzerwienDochodziDoPrawejKrawedzi_DoklejaWielokropekNaKoncu()
    {
        // Dlaczego: operator w oknie alertu ma widzieć, że pełna treść na ekranie maszyny jest dłuższa.
        var bounds = new RectInt(10, 3, 190, 12); // Right = 200 = szerokość wycinka

        string marked = TextTruncation.MarkIfCut("All procedures have been processed according to the proc", bounds, 200);

        Assert.Equal("All procedures have been processed according to the proc …", marked);
    }

    [Fact]
    public void MarkIfCut_GdyCzerwienDochodziDoLewejKrawedzi_DoklejaWielokropekNaPoczatku()
    {
        // Dlaczego: przewijany/przesunięty komunikat może być ucięty także z lewej strony.
        var bounds = new RectInt(0, 3, 100, 12);

        Assert.Equal("… servo alarm", TextTruncation.MarkIfCut("servo alarm", bounds, 200));
    }

    [Fact]
    public void MarkIfCut_GdyCzerwienJestZDalaOdKrawedzi_NieZmieniaTekstu()
    {
        // Dlaczego: zwykły, mieszczący się komunikat nie może dostać fałszywego „…”.
        var bounds = new RectInt(10, 3, 100, 12); // Right = 110 < 198

        Assert.Equal("No.705 [N]Axis 6 servo alarm", TextTruncation.MarkIfCut("No.705 [N]Axis 6 servo alarm", bounds, 200));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MarkIfCut_PustegoTekstu_ZwracaPusty(string? tekst)
    {
        // Dlaczego: brak treści to osobny przypadek („Błąd (nie odczytano treści)”) — samo „…” byłoby mylące.
        var bounds = new RectInt(0, 0, 200, 12);

        Assert.Equal(string.Empty, TextTruncation.MarkIfCut(tekst, bounds, 200));
    }

    [Fact]
    public void MarkIfCut_BezCzerwieni_NieZmieniaTekstu()
    {
        Assert.Equal("abc", TextTruncation.MarkIfCut("abc", null, 200));
    }

    [Fact]
    public void MarkIfCut_TekstuJuzZakonczonegoWielokropkiem_NieDoklejaDrugiego()
    {
        // Dlaczego: kolejne OCR-y w epizodzie nie mogą „hodować” wielokropków.
        var bounds = new RectInt(10, 3, 190, 12);

        Assert.Equal("abc …", TextTruncation.MarkIfCut("abc …", bounds, 200));
    }

    [Fact]
    public void MarkIfCut_NieWplywaNaNormalizacjeIKluczAlertu()
    {
        // Dlaczego: „…” nie może zmienić dopasowania reguły ani klucza deduplikacji (znormalizowany tekst
        // pomija znaki spoza [A-Z0-9]) — inaczej ten sam błąd ucięty i nieucięty byłby dwoma alertami.
        var bounds = new RectInt(10, 3, 190, 12);
        string marked = TextTruncation.MarkIfCut("No.705 [N]Axis 6 servo alarm", bounds, 200);

        Assert.Equal(
            MaszynyAlert.Core.Ocr.TextNormalizer.Normalize("No.705 [N]Axis 6 servo alarm"),
            MaszynyAlert.Core.Ocr.TextNormalizer.Normalize(marked));
    }

    [Theory]
    [InlineData(10, 190, false, true)]   // Right = 200
    [InlineData(10, 188, false, true)]   // Right = 198 = width - EdgePx
    [InlineData(10, 187, false, false)]  // Right = 197
    [InlineData(0, 50, true, false)]
    [InlineData(1, 50, true, false)]
    [InlineData(2, 50, false, false)]
    public void TouchesEdge_ProgKrawedzi(int x, int width, bool lewa, bool prawa)
    {
        (bool left, bool right) = TextTruncation.TouchesEdge(new RectInt(x, 0, width, 10), 200);

        Assert.Equal(lewa, left);
        Assert.Equal(prawa, right);
    }
}
