using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

/// <summary>
/// Kotwiczenie pola wartości na etykiecie tekstowej (<see cref="LabelAnchorLocator"/>): ekran HMI
/// pokazuje postęp jako „Milling Status 79.08%”, a obok stoi pułapka „DS: 100%” (override posuwu,
/// ZAWSZE 100 %). Program musi brać to, co stoi na prawo od etykiety „Milling Status”, i nigdy nie
/// szukać „100%” gdziekolwiek na ekranie — inaczej alert „stoi bez pracy” nigdy by się nie odezwał
/// (pułapka DS) albo odzywałby się stale.
/// </summary>
public sealed class LabelAnchorLocatorTests
{
    private const int FrameWidth = 1024;
    private const int FrameHeight = 600;

    /// <summary>Wysokość „wiersza tekstu” używana we wszystkich próbkach (stąd margines pionowy = 7 px).</summary>
    private const int WordHeight = 14;

    private static OcrWord Word(string text, int x, int y, int width, int lineIndex)
        => new(text, new RectInt(x, y, width, WordHeight), lineIndex);

    /// <summary>Linia „Milling Status 79.08%”: etykieta 10..150, wartość 170..240, wiersz y=100.</summary>
    private static OcrResult MillingLine() => OcrResult.FromWords(
    [
        Word("Milling", 10, 100, 70, 0),
        Word("Status", 90, 100, 60, 0),
        Word("79.08%", 170, 100, 70, 0),
    ]);

    private static AnchoredField Find(OcrResult ocr, params string[] label)
        => LabelAnchorLocator.Find(ocr, label, FrameWidth, FrameHeight)
           ?? throw new Xunit.Sdk.XunitException($"Nie znaleziono etykiety [{string.Join(' ', label)}].");

    [Fact]
    public void Find_EtykietyZWartosciaNaPrawo_ZwracaProstokatEtykietyIWartosci()
    {
        AnchoredField field = Find(MillingLine(), "Milling", "Status");

        // Etykieta = suma prostokątów obu jej słów (10..150).
        Assert.Equal(new RectInt(10, 100, 140, WordHeight), field.Label);
        Assert.Equal("Milling Status", field.LabelText);

        // Wartość = prostokąt słowa „79.08%” powiększony o 6 px w poziomie i max(4, 14/2)=7 px w pionie.
        Assert.Equal(new RectInt(164, 93, 82, 28), field.Value);
        Assert.Equal("79.08%", field.ValueText);
    }

    [Fact]
    public void Find_EtykietyPrzekreconejPrzezOcr_DopasowujeJaRozmyto()
    {
        // Dlaczego: OCR małej czcionki HMI gubi litery („Millng”) i dokleja interpunkcję („Status:”) —
        // wymaganie dosłownego dopasowania oznaczałoby, że kotwica raz działa, a raz nie, zależnie od cyklu.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Millng", 10, 100, 70, 0),
            Word("Status:", 90, 100, 60, 0),
            Word("79.08%", 170, 100, 70, 0),
        ]);

        AnchoredField field = Find(ocr, "Milling", "Status");

        Assert.Equal("Millng Status:", field.LabelText);
        Assert.Equal("79.08%", field.ValueText);
    }

    [Fact]
    public void Find_GdyOcrNieOdczytalWartosciNaPrawo_ZwracaProstokatZapasowyIPustyTekstWartosci()
    {
        // Dlaczego: pole wartości potrafi akurat migać albo być nieostre — wtedy zamiast „nie znaleziono”
        // zwracamy PRZYBLIŻONE miejsce, w którym wartość się pojawi (wołający pozna to po pustym ValueText
        // i może spróbować w kolejnym cyklu), zamiast zmuszać użytkownika do ręcznego podawania ROI.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Milling", 10, 100, 70, 0),
            Word("Status", 90, 100, 60, 0),
        ]);

        AnchoredField field = Find(ocr, "Milling", "Status");

        // Szerokość zapasowa = max(2 × szerokość etykiety, 120) = max(280, 120) = 280, start w Label.Right + 6.
        Assert.Equal(new RectInt(156, 93, 280, 28), field.Value);
        Assert.Equal(string.Empty, field.ValueText);
    }

    [Fact]
    public void Find_WartosciStojacejWInnejLinii_NieUznajeJejZaWartoscEtykiety()
    {
        // Dlaczego: „na prawo od etykiety” znaczy W TEJ SAMEJ LINII — liczba z wiersza niżej (inne pole
        // HMI) nie może udawać postępu tylko dlatego, że leży bardziej z prawej.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Milling", 10, 100, 70, 0),
            Word("Status", 90, 100, 60, 0),
            Word("79.08%", 170, 130, 70, 1),
        ]);

        AnchoredField field = Find(ocr, "Milling", "Status");

        Assert.Equal(string.Empty, field.ValueText);
        Assert.Equal(new RectInt(156, 93, 280, 28), field.Value);
    }

    [Fact]
    public void Find_GdyNaEkranieJestPulapkaDS_BierzeWartoscZLiniiZEtykietaMillingStatus()
    {
        // Dlaczego: TO JEST SEDNO tej klasy — „DS: 100%” pokazuje stale 100 %, więc wyszukiwanie „100%”
        // gdziekolwiek na ekranie dałoby maszynę na zawsze „stojącą bez pracy”. Kotwica na unikatowej
        // etykiecie „Milling Status” bierze 79.08% z właściwej linii, a linia „DS: 100%” etykiety nie ma.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("DS:", 10, 60, 40, 0),
            Word("100%", 60, 60, 60, 0),
            Word("Milling", 10, 100, 70, 1),
            Word("Status", 90, 100, 60, 1),
            Word("79.08%", 170, 100, 70, 1),
        ]);

        AnchoredField field = Find(ocr, "Milling", "Status");

        Assert.Equal("79.08%", field.ValueText);
        Assert.Equal(100, field.Label.Y);

        // W linii „DS: 100%” etykiety nie ma — sama ta linia nie daje żadnego dopasowania.
        OcrResult onlyDs = OcrResult.FromWords([Word("DS:", 10, 60, 40, 0), Word("100%", 60, 60, 60, 0)]);
        Assert.Null(LabelAnchorLocator.Find(onlyDs, ["Milling", "Status"], FrameWidth, FrameHeight));
    }

    [Fact]
    public void Find_GdyEtykietaWystepujeKilkaRazy_BierzePierwszeWystapienieWKolejnosciCzytania()
    {
        // Dlaczego: gdyby etykieta jednak nie była unikatowa, wynik musi być POWTARZALNY (zawsze ta sama
        // linia), a nie zależny od kolejności, w jakiej silnik OCR zwrócił słowa.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Milling", 10, 200, 70, 1),
            Word("Status", 90, 200, 60, 1),
            Word("11.11%", 170, 200, 70, 1),
            Word("Milling", 10, 100, 70, 0),
            Word("Status", 90, 100, 60, 0),
            Word("79.08%", 170, 100, 70, 0),
        ]);

        AnchoredField field = Find(ocr, "Milling", "Status");

        Assert.Equal("79.08%", field.ValueText);
        Assert.Equal(100, field.Label.Y);
    }

    [Fact]
    public void Find_GdyProstokatWartosciWychodzilbyPozaKlatke_PrzycinaGoDoNiej()
    {
        // Dlaczego: wynik trafia wprost do żądania VNC o wycinek ekranu — prostokąt wystający poza ekran
        // maszyny byłby odrzucony przez serwer (albo zerwałby połączenie), więc musi być przycięty.
        OcrResult ocr = OcrResult.FromWords([Word("Milling", 10, 100, 70, 0), Word("Status", 90, 100, 60, 0)]);

        AnchoredField? field = LabelAnchorLocator.Find(ocr, ["Milling", "Status"], frameWidth: 200, frameHeight: 110);

        Assert.NotNull(field);
        Assert.Equal(new RectInt(156, 93, 44, 17), field!.Value);
        Assert.True(field.Value.Right <= 200);
        Assert.True(field.Value.Bottom <= 110);
    }

    [Fact]
    public void Find_SlowaWartosciNachodzacegoNaEtykieteOKilkaPikseli_NadalUznajeJeZaWartosc()
    {
        // Dlaczego: ramki słów z Windows.Media.Ocr potrafią się o piksel–dwa nakładać (antyaliasing,
        // ciasny kerning). Gdyby warunek brzmiał „lewa krawędź >= prawa krawędź etykiety”, prawdziwa
        // wartość stojąca tuż za etykietą wypadłaby z wyniku i program uznałby, że nie odczytał postępu.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Milling", 10, 100, 70, 0),
            Word("Status", 90, 100, 60, 0),
            // Lewa krawędź 148 < prawa krawędź etykiety 150, ale środek słowa (183) leży na prawo od niej.
            Word("79.08%", 148, 100, 70, 0),
        ]);

        AnchoredField field = Find(ocr, "Milling", "Status");

        Assert.Equal("79.08%", field.ValueText);
        Assert.Equal(142, field.Value.X); // 148 - 6 px marginesu
    }

    [Fact]
    public void FindAny_GdyOcrSklejilSlowaEtykiety_ZnajdujeJaPoWariancieJednoslownym()
    {
        // Dlaczego: ten sam HMI raz zwraca „Milling Status” jako dwa słowa, raz jako jedno „MillingStatus”
        // (zależnie od ostrości i skali) — kotwica ma działać w obu przypadkach, bez zmiany konfiguracji.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("MillingStatus", 10, 100, 140, 0),
            Word("79.08%", 170, 100, 70, 0),
        ]);

        Assert.Null(LabelAnchorLocator.Find(ocr, ["Milling", "Status"], FrameWidth, FrameHeight));

        AnchoredField? field = LabelAnchorLocator.FindAny(
            ocr,
            [["MillingStatus"], ["Milling", "Status"]],
            FrameWidth,
            FrameHeight);

        Assert.NotNull(field);
        Assert.Equal("MillingStatus", field!.LabelText);
        Assert.Equal("79.08%", field.ValueText);
    }

    [Fact]
    public void FindAny_GdyPierwszyWariantNiePasujeADrugiTak_ZwracaWynikDrugiego()
    {
        AnchoredField? field = LabelAnchorLocator.FindAny(
            MillingLine(),
            [["MillingStatus"], ["Milling", "Status"]],
            FrameWidth,
            FrameHeight);

        Assert.NotNull(field);
        Assert.Equal("Milling Status", field!.LabelText);
    }

    [Fact]
    public void FindAny_GdyZadenWariantNiePasuje_ZwracaNull()
    {
        Assert.Null(LabelAnchorLocator.FindAny(MillingLine(), [["File", "Name"]], FrameWidth, FrameHeight));
        Assert.Null(LabelAnchorLocator.FindAny(MillingLine(), [], FrameWidth, FrameHeight));
    }

    [Fact]
    public void Find_PustegoWynikuOcrAlboPustejListySlowEtykiety_ZwracaNull()
    {
        // Dlaczego: brak silnika OCR / nieudany odczyt / pusta konfiguracja etykiety to normalne sytuacje
        // (probe.exe uruchamiany na komputerze bez pakietu językowego) — mają dać „nie znalazłem”, a nie wyjątek.
        Assert.Null(LabelAnchorLocator.Find(OcrResult.Empty, ["Milling", "Status"], FrameWidth, FrameHeight));
        Assert.Null(LabelAnchorLocator.Find(MillingLine(), [], FrameWidth, FrameHeight));
    }

    [Fact]
    public void Find_WynikuOcrBezPozycjiSlow_ZwracaNull()
    {
        // Dlaczego: silnik OCR bez pozycji słów (albo wynik zbudowany konstruktorem pozycyjnym) nie daje
        // się zakotwiczyć — program ma to zgłosić jako „nie znaleziono pola”, a nie zgadywać współrzędne.
        var ocr = new OcrResult("Milling Status 79.08%", ["Milling Status 79.08%"]);

        Assert.Null(LabelAnchorLocator.Find(ocr, ["Milling", "Status"], FrameWidth, FrameHeight));
    }
}
