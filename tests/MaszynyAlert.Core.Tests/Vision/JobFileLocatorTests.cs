using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class JobFileLocatorTests
{
    private const int FrameWidth = 1024;
    private const int FrameHeight = 768;

    /// <summary>Wysokość „wiersza tekstu” we wszystkich próbkach (stąd margines pionowy = max(4, 14/2) = 7 px).</summary>
    private const int WordHeight = 14;

    private static OcrWord Word(string text, int x, int width, int lineIndex = 0, int y = 100)
        => new(text, new RectInt(x, y, width, WordHeight), lineIndex);

    private static OcrResult AiditeLine() => OcrResult.FromWords(
    [
        Word("Start-up", 10, 70),
        Word("File", 90, 40),
        Word("Current", 140, 70),
        Word("Puck", 220, 45),
        Word("Information", 275, 100),
        Word("Job", 385, 35),
        Word("16-27-30", 425, 80),
        Word("2026-09-11.nc", 510, 120),
        Word("Current", 640, 70),
        Word("B1_r", 720, 40),
    ]);

    private static AnchoredField Found(OcrResult ocr, int maxPreceding = JobFileLocator.DefaultMaxPrecedingWords)
        => JobFileLocator.Find(ocr, FrameWidth, FrameHeight, maxPreceding)
           ?? throw new Xunit.Sdk.XunitException("Nie znaleziono nazwy pliku pracy.");

    [Fact]
    public void Find_PrawdziwaLiniaZEkranuAidite_ZwracaCalaNazweBezSasiednichNapisow()
    {
        AnchoredField field = Found(AiditeLine());

        Assert.Equal("Job 16-27-30 2026-09-11.nc", field.ValueText);

        // Kotwica = samo słowo z rozszerzeniem; obszar = suma słów nazwy (385..630) + margines 6/7 px.
        Assert.Equal(new RectInt(510, 100, 120, WordHeight), field.Label);
        Assert.Equal("2026-09-11.nc", field.LabelText);
        Assert.Equal(new RectInt(379, 93, 257, 28), field.Value);

        // Sąsiedni napis „Information” NIE może wejść w obszar — limit słów poprzedzających to pilnuje.
        Assert.DoesNotContain("Information", field.ValueText, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_ObszarObejmujeCzlonZGodzina_ZebyDwiePraceTegoSamegoDniaSieRoznily()
    {
        // Dlaczego: ostatni człon nazwy to data, która w ciągu dnia się nie zmienia. Gdyby obszar objął
        // tylko słowo z „.nc”, start kolejnej pracy tego samego dnia nie zostałby wykryty w ogóle.
        AnchoredField pierwsza = Found(AiditeLine());
        AnchoredField druga = Found(OcrResult.FromWords(
        [
            Word("Information", 275, 100),
            Word("Job", 385, 35),
            Word("17-02-45", 425, 80),
            Word("2026-09-11.nc", 510, 120),
        ]));

        Assert.NotEqual(pierwsza.ValueText, druga.ValueText);
        Assert.Contains("16-27-30", pierwsza.ValueText, StringComparison.Ordinal);
        Assert.Contains("17-02-45", druga.ValueText, StringComparison.Ordinal);

        // Obszar jest ten sam (nazwa stoi w tym samym miejscu ekranu) — zmienia się treść, nie położenie.
        Assert.Equal(pierwsza.Value, druga.Value);
    }

    [Fact]
    public void Find_SlowoPrzedNazwaNiePasujaceDoWzorca_ZatrzymujeDobieranie()
    {
        // Dlaczego: napis z interpunkcją („Plik:”) to etykieta, nie człon nazwy — nie wciągamy jej w obszar.
        AnchoredField field = Found(OcrResult.FromWords(
        [
            Word("Plik:", 300, 50),
            Word("2026-09-11.nc", 510, 120),
        ]));

        Assert.Equal("2026-09-11.nc", field.ValueText);
        Assert.Equal(new RectInt(504, 93, 132, 28), field.Value);
    }

    [Fact]
    public void Find_NazwaJakoJednoSlowo_DobieraTezSasiedniStatycznyNapis()
    {
        // Świadomie akceptowane zachowanie: gdy OCR odczyta całą nazwę jako jedno słowo, limit słów
        // poprzedzających wciągnie w obszar sąsiedni napis. Jest nieszkodliwy, bo statyczny tekst nigdy
        // się nie zmienia, więc nie udaje startu nowej pracy (patrz uwagi JobFileLocator).
        AnchoredField field = Found(OcrResult.FromWords(
        [
            Word("Information", 275, 100),
            Word("Job_16-27-30_2026-09-11.nc", 385, 245),
        ]));

        Assert.Equal("Information Job_16-27-30_2026-09-11.nc", field.ValueText);
    }

    [Fact]
    public void Find_BezSlowPoprzedzajacych_ZwracaTylkoSlowoZRozszerzeniem()
    {
        AnchoredField field = Found(AiditeLine(), maxPreceding: 0);

        Assert.Equal("2026-09-11.nc", field.ValueText);
        Assert.Equal(field.Label, new RectInt(510, 100, 120, WordHeight));
    }

    [Fact]
    public void Find_KilkaNazwNaEkranie_WygrywaPierwszaWKolejnosciOdczytu()
    {
        // Kolejność odczytu = najwyższa linia, a w niej najbardziej lewe wystąpienie — tak samo jak w
        // LabelAnchorLocator, żeby oba kotwiczenia znaczyły to samo.
        AnchoredField field = Found(OcrResult.FromWords(
        [
            Word("stare.nc", 100, 80, lineIndex: 1, y: 200),
            Word("biezace.nc", 100, 90, lineIndex: 0, y: 100),
        ]));

        Assert.Equal("biezace.nc", field.ValueText);
    }

    [Fact]
    public void Find_KoncowaInterpunkcjaDoklejonaPrzezOcr_NieBlokujeDopasowania()
    {
        AnchoredField field = Found(OcrResult.FromWords([Word("2026-09-11.nc,", 510, 120)]));

        Assert.Equal("2026-09-11.nc,", field.ValueText);
    }

    [Theory]
    [InlineData(".nc")]          // samo rozszerzenie bez nazwy
    [InlineData("plik.ncx")]     // inne rozszerzenie
    [InlineData("Milling")]      // zwykły napis
    public void Find_BrakNazwyPlikuPracy_ZwracaNull(string word)
    {
        Assert.Null(JobFileLocator.Find(
            OcrResult.FromWords([Word(word, 100, 80)]), FrameWidth, FrameHeight));
    }

    [Fact]
    public void Find_PustyWynikOcr_ZwracaNull()
    {
        // Silnik OCR niedostępny albo nic nie odczytał — wołający ma dostać null, nie wyjątek.
        Assert.Null(JobFileLocator.Find(OcrResult.Empty, FrameWidth, FrameHeight));
    }
}
