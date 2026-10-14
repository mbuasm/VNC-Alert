using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Ocr;

/// <summary>
/// Słowa OCR wraz z położeniem (<see cref="OcrWord"/>) i budowanie z nich wyniku
/// (<see cref="OcrResult.FromWords"/>) — podstawa kotwiczenia pól na etykiecie
/// (<c>Vision.LabelAnchorLocator</c>: „Milling Status” → wartość „79.08%” na prawo od niej).
/// </summary>
public sealed class OcrResultWordsTests
{
    private static OcrWord Word(string text, int x, int y, int lineIndex, int width = 40, int height = 12)
        => new(text, new RectInt(x, y, width, height), lineIndex);

    [Fact]
    public void Words_WynikuZbudowanegoKonstruktoremPozycyjnym_SaPustaLista()
    {
        // Dlaczego: silniki OCR, które nie zwracają pozycji słów (i wszystkie stare miejsca w kodzie
        // budujące OcrResult z samego tekstu), muszą dawać PUSTĄ listę, a nie null — wołający
        // (LabelAnchorLocator) iteruje po niej bez sprawdzania null i ma po prostu nic nie znaleźć.
        var result = new OcrResult("Milling Status 79.08%", ["Milling Status 79.08%"]);

        Assert.Empty(result.Words);
        Assert.Empty(OcrResult.Empty.Words);
    }

    [Fact]
    public void FromWords_SlowWJednejLinii_SklejaTekstIZachowujeSlowaZPozycjami()
    {
        OcrResult result = OcrResult.FromWords(
        [
            Word("Milling", 10, 100, 0),
            Word("Status", 60, 100, 0),
            Word("79.08%", 120, 100, 0),
        ]);

        Assert.Equal("Milling Status 79.08%", result.Text);
        Assert.Equal("Milling Status 79.08%", Assert.Single(result.Lines));
        Assert.Equal(3, result.Words.Count);
        Assert.False(result.IsEmpty);
        Assert.Equal(new RectInt(120, 100, 40, 12), result.Words[2].Bounds);
    }

    [Fact]
    public void FromWords_SlowWKilkuLiniach_GrupujeLinieWKolejnosciIndeksow()
    {
        // Dlaczego: Windows.Media.Ocr zwraca słowa pogrupowane w linie; kolejność linii w wyniku musi
        // odpowiadać kolejności czytania (od góry), bo na tej kolejności opiera się szukanie etykiety
        // (pierwsze wystąpienie = najwyższa linia).
        OcrResult result = OcrResult.FromWords(
        [
            Word("DS:", 10, 140, 1),
            Word("100%", 50, 140, 1),
            Word("Milling", 10, 100, 0),
            Word("Status", 60, 100, 0),
        ]);

        Assert.Equal(["Milling Status", "DS: 100%"], result.Lines);
        Assert.Equal("Milling Status DS: 100%", result.Text);
    }

    [Fact]
    public void FromWords_SlowPodanychWPrzypadkowejKolejnosci_UkladaJeWLiniiOdLewejDoPrawej()
    {
        OcrResult result = OcrResult.FromWords(
        [
            Word("79.08%", 120, 100, 0),
            Word("Status", 60, 100, 0),
            Word("Milling", 10, 100, 0),
        ]);

        Assert.Equal("Milling Status 79.08%", Assert.Single(result.Lines));
        Assert.Equal("Milling Status 79.08%", result.Text);
    }

    [Fact]
    public void FromWords_PustejListy_ZwracaWynikPusty()
    {
        OcrResult result = OcrResult.FromWords([]);

        Assert.Same(OcrResult.Empty, result);
        Assert.True(result.IsEmpty);
        Assert.Empty(result.Words);
    }

    [Fact]
    public void FromWords_Null_RzucaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => OcrResult.FromWords(null!));
    }
}
