using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

/// <summary>
/// Przygotowanie wycinka z LICZBĄ procent do OCR (<see cref="ImageOps.PrepareTextForOcr"/>, tryb
/// <c>ProgressMode.Number</c>): biały margines, powiększenie, rozciągnięcie kontrastu i odwrócenie
/// jasności dla ciemnego motywu HMI.
/// </summary>
public sealed class ImageOpsTextOcrTests
{
    /// <summary>Obraz <paramref name="w"/>×<paramref name="h"/> w jednolitym odcieniu szarości <paramref name="value"/>.</summary>
    private static RgbaImage Jednolity(int w, int h, byte value)
    {
        var img = new RgbaImage(w, h);
        img.Fill(new Rgba(value, value, value));
        return img;
    }

    /// <summary>
    /// Obraz 4×2 o dwóch odcieniach szarości: lewa połowa <paramref name="ciemny"/>, prawa
    /// <paramref name="jasny"/> (imitacja „cyfra na tle” o niskim kontraście).
    /// </summary>
    private static RgbaImage DwaOdcienie(byte ciemny, byte jasny)
    {
        var img = new RgbaImage(4, 2);
        img.Fill(new Rgba(ciemny, ciemny, ciemny));
        for (int y = 0; y < 2; y++)
        {
            for (int x = 2; x < 4; x++)
            {
                img.SetPixel(x, y, new Rgba(jasny, jasny, jasny));
            }
        }

        return img;
    }

    [Fact]
    public void PrepareTextForOcr_ZwracaObrazPowiekszonyOMarginesZObuStronIPrzeskalowany()
    {
        // Dlaczego: liczba procent bywa malutka i przyklejona do krawędzi ROI — silnik OCR potrzebuje
        // i zapasu dookoła, i powiększenia; rozmiar wyjścia to dokładnie (w+2m)*s × (h+2m)*s.
        Gray8Image gray = ImageOps.PrepareTextForOcr(Jednolity(4, 3, 100), marginPx: 2, scale: 3, invert: false);

        Assert.Equal((4 + (2 * 2)) * 3, gray.Width);
        Assert.Equal((3 + (2 * 2)) * 3, gray.Height);
    }

    [Fact]
    public void PrepareTextForOcr_MarginesJestBialy()
    {
        // Dlaczego: margines ma udawać czystą kartkę wokół tekstu — gdyby był czarny (domyślne zero
        // w nowym obrazie), OCR widziałby ramkę i gubił cyfry przy krawędzi.
        Gray8Image gray = ImageOps.PrepareTextForOcr(Jednolity(4, 3, 100), marginPx: 2, scale: 1, invert: false);

        Assert.Equal(255, gray.Get(0, 0));
        Assert.Equal(255, gray.Get(gray.Width - 1, gray.Height - 1));
        Assert.Equal(255, gray.Get(3, 1));
    }

    [Fact]
    public void PrepareTextForOcr_RozciagaKontrastDoPelnegoZakresu()
    {
        // Dlaczego: na jednolitym tle HMI różnica między cyfrą a tłem bywa niewielka (tu 100 vs 150) —
        // bez rozciągnięcia do 0..255 krawędzie cyfr są dla OCR zbyt „miękkie”.
        Gray8Image gray = ImageOps.PrepareTextForOcr(DwaOdcienie(100, 150), marginPx: 0, scale: 1, invert: false);

        Assert.Equal(0, gray.Get(0, 0));
        Assert.Equal(255, gray.Get(3, 1));
    }

    [Fact]
    public void PrepareTextForOcr_ZOdwroceniem_ZamieniaJasneNaCiemneIOdwrotnie()
    {
        // Dlaczego: na ciemnym motywie (profil amm580) liczba jest JASNA na CIEMNYM tle, a silnik OCR
        // oczekuje odwrotnie — po odwróceniu ten sam piksel musi wyjść dokładnie na drugim końcu skali.
        Gray8Image bez = ImageOps.PrepareTextForOcr(DwaOdcienie(100, 150), marginPx: 0, scale: 1, invert: false);
        Gray8Image zOdwroceniem = ImageOps.PrepareTextForOcr(DwaOdcienie(100, 150), marginPx: 0, scale: 1, invert: true);

        Assert.Equal(0, bez.Get(0, 0));
        Assert.Equal(255, zOdwroceniem.Get(0, 0));
        Assert.Equal(255, bez.Get(3, 1));
        Assert.Equal(0, zOdwroceniem.Get(3, 1));
    }

    [Fact]
    public void PrepareTextForOcr_ObrazuJednolitego_NieZmieniaJasnosci()
    {
        // Dlaczego: gdy w ROI nie ma żadnej cyfry (min == max), nie ma czego rozciągać — rozciąganie
        // „na siłę” zamieniłoby szum tła w czarno-białe plamy, na których OCR zacząłby zmyślać tekst.
        Gray8Image gray = ImageOps.PrepareTextForOcr(Jednolity(4, 3, 100), marginPx: 1, scale: 1, invert: false);

        Assert.Equal(100, gray.Get(1, 1));
        Assert.Equal(100, gray.Get(4, 3));
        Assert.Equal(255, gray.Get(0, 0));
    }

    [Fact]
    public void PrepareTextForOcr_ObrazuZerowego_ZwracaBialyObrazZMarginesuBezWyjatku()
    {
        Gray8Image gray = ImageOps.PrepareTextForOcr(new RgbaImage(0, 0), marginPx: 6, scale: 3, invert: false);

        Assert.Equal(12, gray.Width);
        Assert.Equal(12, gray.Height);
        Assert.All(gray.Pixels, b => Assert.Equal(255, b));
    }
}
