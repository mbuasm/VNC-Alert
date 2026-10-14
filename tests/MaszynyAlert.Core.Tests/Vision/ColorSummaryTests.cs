using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

/// <summary>
/// Podsumowanie dominujących kolorów zrzutu (<see cref="ColorSummary"/>) — narzędzie <c>probe.exe</c>
/// ma nim NOTOWAĆ, „jakim kolorem maszyna sygnalizuje błąd” (np. czerwony tekst na niebieskim tle
/// okienka alertu HMI), żeby użytkownik nie musiał sam opisywać progów kolorów.
/// </summary>
public sealed class ColorSummaryTests
{
    private static RgbaImage Solid(Rgba color, int w = 4, int h = 4)
    {
        var img = new RgbaImage(w, h);
        img.Fill(color);
        return img;
    }

    [Theory]
    [InlineData(220, 30, 30, "czerwony")]     // czerwony tekst błędu na pasku statusu
    [InlineData(240, 200, 0, "żółty")]        // żółty komunikat „plan produkcji wykonany” (nie błąd)
    [InlineData(30, 60, 200, "niebieski")]    // tło okienka alertów HMI
    [InlineData(250, 140, 20, "pomarańczowy")]
    [InlineData(255, 255, 255, "biały")]
    [InlineData(10, 10, 10, "czarny")]
    [InlineData(128, 128, 128, "szary")]
    [InlineData(60, 10, 10, "ciemny/inny")]   // nasycone, ale za ciemne na pewny kubełek koloru
    public void Summarize_ObrazuJednolitego_KlasyfikujeGoDoOczekiwanegoKubelka(byte r, byte g, byte b, string nazwa)
    {
        // Dlaczego: to jest cała umowa tej klasy — nazwy kubełków trafiają wprost do raportu,
        // który czyta użytkownik (nie programista), więc czerwień błędu nie może wyjść jako „pomarańczowy”,
        // a szare tło jako „biały”.
        ColorShare share = Assert.Single(ColorSummary.Summarize(Solid(new Rgba(r, g, b))));

        Assert.Equal(nazwa, share.Name);
        Assert.Equal(1.0, share.Fraction, 6);
    }

    [Fact]
    public void Summarize_ObrazuZKilkomaKolorami_ZwracaUdzialyMalejacoIOgraniczaDoTop()
    {
        // Dlaczego: raport ma pokazać najpierw tło, potem kolor tekstu — kolejność jest informacją, nie ozdobą.
        var img = new RgbaImage(10, 10);
        img.Fill(new Rgba(30, 60, 200));                       // 100 px niebieskiego tła
        for (int x = 0; x < 10; x++)
        {
            img.SetPixel(x, 0, new Rgba(220, 30, 30));         // 10 px czerwonego „tekstu”
        }

        for (int x = 0; x < 5; x++)
        {
            img.SetPixel(x, 1, new Rgba(255, 255, 255));       // 5 px bieli
        }

        IReadOnlyList<ColorShare> all = ColorSummary.Summarize(img, top: 10);

        Assert.Equal(["niebieski", "czerwony", "biały"], all.Select(s => s.Name));
        Assert.Equal(0.85, all[0].Fraction, 6);
        Assert.Equal(0.10, all[1].Fraction, 6);
        Assert.Equal(0.05, all[2].Fraction, 6);

        IReadOnlyList<ColorShare> topTwo = ColorSummary.Summarize(img, top: 2);
        Assert.Equal(["niebieski", "czerwony"], topTwo.Select(s => s.Name));
    }

    [Fact]
    public void Summarize_PikseliPrzezroczystych_WcaleNieLiczyAniDoKubelkowAniDoMianownika()
    {
        // Dlaczego: zrzuty sklejane z kilku wycinków mają obszary nieodmalowane (A = 0) — gdyby liczyły
        // się jako „czarny”, raport twierdziłby, że okienko alertu jest głównie czarne.
        var img = new RgbaImage(4, 4); // domyślnie w pełni przezroczysty
        img.SetPixel(0, 0, new Rgba(30, 60, 200));
        img.SetPixel(1, 0, new Rgba(30, 60, 200));
        img.SetPixel(2, 0, new Rgba(220, 30, 30, 200)); // A >= 128 → liczy się
        img.SetPixel(3, 0, new Rgba(220, 30, 30, 100)); // A < 128 → pomijany

        IReadOnlyList<ColorShare> shares = ColorSummary.Summarize(img, top: 10);

        Assert.Equal(["niebieski", "czerwony"], shares.Select(s => s.Name));
        Assert.Equal(2.0 / 3.0, shares[0].Fraction, 6);
        Assert.Equal(1.0 / 3.0, shares[1].Fraction, 6);
    }

    [Fact]
    public void Summarize_ObrazuZerowegoAlboCalkiemPrzezroczystego_ZwracaPustaListe()
    {
        Assert.Empty(ColorSummary.Summarize(new RgbaImage(0, 0)));
        Assert.Empty(ColorSummary.Summarize(new RgbaImage(4, 4)));
    }

    [Fact]
    public void Describe_UdzialowKolorow_FormatujeJePoPolskuZProcentami()
    {
        // Dlaczego: ten napis trafia do raportu i do logu czytanego przez użytkownika — musi być po polsku,
        // ze spacją przed znakiem procentu i bez „naukowej” notacji.
        string text = ColorSummary.Describe(
        [
            new ColorShare("niebieski", 0.62),
            new ColorShare("czerwony", 0.09),
        ]);

        Assert.Equal("niebieski 62 %, czerwony 9 %", text);
    }

    [Fact]
    public void Describe_UdzialuPonizejProcenta_PiszeMniejNizJedenZamiastZera()
    {
        // Dlaczego: cienki czerwony napis to ułamek procenta obrazu — „czerwony 0 %” wyglądałoby jak
        // „czerwieni nie ma”, a to właśnie ta czerwień jest dowodem, że maszyna sygnalizuje błąd.
        Assert.Equal("czerwony <1 %", ColorSummary.Describe([new ColorShare("czerwony", 0.004)]));
    }

    [Fact]
    public void Describe_PustejListy_ZwracaBrak()
    {
        Assert.Equal("(brak)", ColorSummary.Describe([]));
        Assert.Equal("(brak)", ColorSummary.Describe(ColorSummary.Summarize(new RgbaImage(0, 0))));
    }

    [Fact]
    public void DominantTextColor_CzerwonegoTekstuNaNiebieskimTle_ZwracaCzerwony()
    {
        // Dlaczego: to odpowiedź na pytanie „jakim kolorem maszyna pisze błąd w okienku alertu”.
        var img = new RgbaImage(100, 100);
        img.Fill(new Rgba(30, 60, 200));
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                img.SetPixel(x, y, new Rgba(220, 30, 30)); // 900 z 10000 px = 9 %
            }
        }

        Assert.Equal("czerwony", ColorSummary.DominantTextColor(img));
    }

    [Fact]
    public void DominantTextColor_ObrazuJednolitego_ZwracaNull()
    {
        // Dlaczego: samo tło bez tekstu nie odpowiada na nic — lepiej „nie wiem” niż nazwa tła udająca
        // kolor tekstu (inaczej raport mówiłby, że maszyna pisze błędy na niebiesko).
        Assert.Null(ColorSummary.DominantTextColor(Solid(new Rgba(30, 60, 200), 20, 20)));
        Assert.Null(ColorSummary.DominantTextColor(new RgbaImage(0, 0)));
    }

    [Theory]
    [InlineData(40, null)]        // 0,40 % — poniżej progu 0,5 %
    [InlineData(50, "czerwony")]  // dokładnie 0,50 % — próg włącznie
    public void DominantTextColor_DrugiegoKubelkaNaGranicyPolProcenta_RozstrzygaZgodnieZProgiem(int redPixels, string? oczekiwany)
    {
        // Dlaczego: kilkadziesiąt pikseli innego koloru to zwykle antyaliasing/kompresja, a nie tekst —
        // bez progu raport zgłaszałby „kolor tekstu” dla każdego jednolitego zrzutu.
        var img = new RgbaImage(100, 100);
        img.Fill(new Rgba(30, 60, 200));
        for (int i = 0; i < redPixels; i++)
        {
            img.SetPixel(i % 100, i / 100, new Rgba(220, 30, 30));
        }

        Assert.Equal(oczekiwany, ColorSummary.DominantTextColor(img));
    }
}
