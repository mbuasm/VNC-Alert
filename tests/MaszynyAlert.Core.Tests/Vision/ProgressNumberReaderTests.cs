using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Vision;

/// <summary>
/// Odczyt postępu pokazywanego jako LICZBA procent (<see cref="ProgressNumberReader"/>, tryb
/// <c>ProgressMode.Number</c>): zamiana tekstu OCR na <c>Percent</c>/<c>IsFull</c>, oszczędzanie silnika
/// OCR pamięcią podręczną po bajtach wycinka, odporność na awarię silnika oraz odwracanie jasności
/// wycinka dla ciemnego motywu HMI.
/// </summary>
public sealed class ProgressNumberReaderTests
{
    /// <summary>Profil obserwacji postoju w trybie liczbowym (domyślne fullAtPercent = 100, ocrScale = 3, ocrMarginPx = 8).</summary>
    private static readonly IdleWatchProfile Idle = new()
    {
        ProgressRoi = new RectInt(440, 380, 120, 24),
    };

    /// <summary>
    /// Wycinek z „liczbą”: jasne tło (<paramref name="tlo"/>) z ciemnym prostokątem udającym cyfry
    /// (<paramref name="atrament"/>) o szerokości <paramref name="szerokoscAtramentu"/> — treść nie ma
    /// znaczenia (tekst odczytu podaje atrapa OCR), liczą się BAJTY obrazu (pamięć podręczna) i jasność.
    /// </summary>
    private static RgbaImage Wycinek(byte tlo = 235, byte atrament = 40, int szerokoscAtramentu = 4)
    {
        var img = new RgbaImage(20, 10);
        img.Fill(new Rgba(tlo, tlo, tlo));
        for (int y = 2; y < 8; y++)
        {
            for (int x = 2; x < 2 + szerokoscAtramentu; x++)
            {
                img.SetPixel(x, y, new Rgba(atrament, atrament, atrament));
            }
        }

        return img;
    }

    private static ProgressNumberReader Reader(
        FakeOcrEngine ocr,
        IdleWatchProfile? profile = null,
        bool darkTheme = false,
        ILogger? logger = null) =>
        new(profile ?? Idle, darkTheme, ocr, logger);

    /// <summary>
    /// Ten sam profil, ale BEZ białego marginesu — w testach odwracania jasności margines (zawsze biały,
    /// niezależnie od motywu) byłby większy od samego wycinka i zamazywał wynik pomiaru.
    /// </summary>
    private static readonly IdleWatchProfile IdleBezMarginesu = Idle with { OcrMarginPx = 0 };

    /// <summary>Średnia jasność obrazu przekazanego do OCR — miara „czy wycinek został odwrócony”.</summary>
    private static double SredniaJasnosc(Gray8Image img) => img.Pixels.Average(b => (double)b);

    [Fact]
    public async Task ReadAsync_GdyOcrCzyta100Procent_ZwracaPelnyPostep()
    {
        // Dlaczego: to jest sytuacja, dla której cała funkcja istnieje — „100%” na ekranie ma dać
        // IsFull (a więc początek liczenia dwóch minut postoju) i procent do kolumny „Postęp”.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("100%");

        ProgressDetectionResult wynik = await Reader(ocr).ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Equal(100, wynik.Percent);
        Assert.True(wynik.IsFull);
        Assert.Equal(1.0, wynik.FilledFraction, 6);
        Assert.Equal("100%", wynik.RawText);
    }

    [Fact]
    public async Task ReadAsync_GdyOcrCzyta45Procent_ZwracaPostepWTrakciePracy()
    {
        // Dlaczego: maszyna w trakcie pracy nie może być uznana za stojącą; FilledFraction (percent/100)
        // jest tym, co widzi reszta programu wspólnie dla obu trybów.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("45%");

        ProgressDetectionResult wynik = await Reader(ocr).ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Equal(45, wynik.Percent);
        Assert.False(wynik.IsFull);
        Assert.Equal(0.45, wynik.FilledFraction, 6);
    }

    [Theory]
    [InlineData("0.20%", false)]
    [InlineData("Progress: 0.2%", false)]
    [InlineData("0.00%", true)]
    [InlineData("0%", true)]
    public async Task ReadAsync_UlamekProcentu_ZeroTylkoGdyNaEkranieJestNaprawdeZero(string tekst, bool zero)
    {
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(tekst);

        ProgressDetectionResult wynik = await Reader(ocr).ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Equal(0, wynik.Percent);
        Assert.Equal(zero, wynik.IsZero);
        Assert.Equal(!zero, wynik.HasNonZeroFraction);
        Assert.False(wynik.IsFull);
    }

    [Fact]
    public async Task ReadAsync_GdyOcrNicNieOdczytal_ZwracaOdczytNieczytelny()
    {
        var ocr = new FakeOcrEngine();
        ocr.EnqueueEmpty();

        ProgressDetectionResult wynik = await Reader(ocr).ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Null(wynik.Percent);
        Assert.False(wynik.IsFull);
        Assert.Equal(0.0, wynik.FilledFraction);
    }

    [Fact]
    public async Task ReadAsync_TegoSamegoObrazuKilkaRazy_WolaOcrTylkoRaz()
    {
        // Dlaczego: stojąca maszyna pokazuje PIKSEL W PIKSEL ten sam „100%” co sekundę — bez pamięci
        // podręcznej byłoby to 12 wywołań OCR na sekundę (po jednym na maszynę) zamiast 12 na epizod.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("100%");
        ProgressNumberReader reader = Reader(ocr);
        RgbaImage wycinek = Wycinek();

        for (int i = 0; i < 5; i++)
        {
            ProgressDetectionResult wynik = await reader.ReadAsync(wycinek, CancellationToken.None);
            Assert.Equal(100, wynik.Percent);
        }

        Assert.Equal(1, reader.OcrCalls);
        Assert.Equal(1, ocr.CallCount);
    }

    [Fact]
    public async Task ReadAsync_GdyWycinekSieZmienil_WolaOcrPonownie()
    {
        // Dlaczego: pamięć podręczna nie może „zamrozić” odczytu — gdy liczba na ekranie się zmieni
        // (praca ruszyła: 100 % → 1 %), kolejny cykl musi to zobaczyć.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("100%").Enqueue("1%");
        ProgressNumberReader reader = Reader(ocr);

        ProgressDetectionResult pierwszy = await reader.ReadAsync(Wycinek(szerokoscAtramentu: 4), CancellationToken.None);
        ProgressDetectionResult drugi = await reader.ReadAsync(Wycinek(szerokoscAtramentu: 9), CancellationToken.None);

        Assert.Equal(100, pierwszy.Percent);
        Assert.True(pierwszy.IsFull);
        Assert.Equal(1, drugi.Percent);
        Assert.False(drugi.IsFull);
        Assert.Equal(2, reader.OcrCalls);
    }

    [Fact]
    public async Task ReadAsync_ZObnizonymProgiemFullAtPercent_UznajeMniejszaWartoscZaPelnyPostep()
    {
        // Dlaczego: gdyby HMI potrafiło stanąć na „99%” zamiast „100%”, próg musi dać się obniżyć
        // w konfiguracji — bez tego alert postoju nigdy by dla takiej maszyny nie powstał.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("99%");

        ProgressDetectionResult wynik = await Reader(ocr, Idle with { FullAtPercent = 99 })
            .ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Equal(99, wynik.Percent);
        Assert.True(wynik.IsFull);
    }

    [Fact]
    public async Task ReadAsync_GdySilnikOcrRzucaWyjatek_ZwracaOdczytNieczytelnyIOstrzegaWLogu()
    {
        // Dlaczego: awaria silnika OCR (np. WinRT) nie może ubić pętli obserwatora ani po cichu udawać
        // postępu — ma zostać zapisana w logu, żeby było wiadomo, dlaczego kolumna „Postęp” pokazuje „?”.
        var ocr = new FakeOcrEngine { Failure = () => new InvalidOperationException("silnik OCR padł (test)") };
        var logs = new RecordingLoggerFactory();

        ProgressDetectionResult wynik = await Reader(ocr, logger: logs.CreateLogger("ProgressNumberReader"))
            .ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Null(wynik.Percent);
        Assert.False(wynik.IsFull);
        Assert.Equal(0.0, wynik.FilledFraction);
        LogEntry ostrzezenie = Assert.Single(logs.Containing("Błąd silnika OCR"));
        Assert.Equal(LogLevel.Warning, ostrzezenie.Level);
        Assert.IsType<InvalidOperationException>(ostrzezenie.Exception);
    }

    [Fact]
    public async Task ReadAsync_GdySilnikOcrJestNiedostepny_NieWolaGoWcaleIZwracaOdczytNieczytelny()
    {
        // Dlaczego: czytnik ma być bezpieczny również użyty samodzielnie (w pętli obserwatora tryb number
        // bez OCR jest w ogóle nieuruchamiany) — bez silnika po prostu nie ma odczytu.
        var ocr = new FakeOcrEngine { IsAvailable = false };
        ocr.Enqueue("100%");

        ProgressDetectionResult wynik = await Reader(ocr).ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Null(wynik.Percent);
        Assert.False(wynik.IsFull);
        Assert.Equal(0, ocr.CallCount);
    }

    [Fact]
    public async Task ReadAsync_NaJasnymMotywie_PrzekazujeDoOcrCiemnyTekstNaJasnymTle()
    {
        // Dlaczego: jasny motyw HMI (profil opencnc) daje już to, czego chce OCR — wycinek NIE może
        // zostać odwrócony, bo biały tekst na czarnym tle silnik czyta znacznie gorzej.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("100%");

        await Reader(ocr, IdleBezMarginesu, darkTheme: false)
            .ReadAsync(Wycinek(tlo: 235, atrament: 40), CancellationToken.None);

        Gray8Image przekazany = Assert.IsType<Gray8Image>(ocr.LastImage);
        Assert.True(SredniaJasnosc(przekazany) > 200, "Jasne tło ma zostać jasne (bez odwrócenia).");
    }

    [Fact]
    public async Task ReadAsync_NaCiemnymMotywie_OdwracaWycinekPrzedOcr()
    {
        // Dlaczego: na ciemnym motywie (profil amm580) liczba jest jasna na ciemnym tle — bez odwrócenia
        // OCR czytałby „negatyw” i kolumna „Postęp” pokazywałaby „?” mimo poprawnego progressRoi.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("100%");

        await Reader(ocr, IdleBezMarginesu, darkTheme: true)
            .ReadAsync(Wycinek(tlo: 30, atrament: 220), CancellationToken.None);

        Gray8Image przekazany = Assert.IsType<Gray8Image>(ocr.LastImage);
        Assert.True(SredniaJasnosc(przekazany) > 200, "Ciemne tło ma po odwróceniu wyjść jasne.");
    }

    [Fact]
    public async Task ReadAsync_ZJawnymInvertForOcr_NadpisujeMotywProfilu()
    {
        // Dlaczego: jawne „invertForOcr” w konfiguracji istnieje po to, żeby dało się poprawić sytuację,
        // w której akurat obszar liczby procent ma inny motyw niż reszta ekranu.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("100%");

        await Reader(ocr, IdleBezMarginesu with { InvertForOcr = true }, darkTheme: false)
            .ReadAsync(Wycinek(tlo: 235, atrament: 40), CancellationToken.None);

        Gray8Image przekazany = Assert.IsType<Gray8Image>(ocr.LastImage);
        Assert.True(SredniaJasnosc(przekazany) < 150, "Jawne invertForOcr=true ma odwrócić jasny wycinek.");
    }


    [Fact]
    public async Task ReadAsync_GdyPierwszaProbaGubiLiczbe_CzytaPrzySkali1()
    {
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("Progress:").Enqueue("Progress: 17%");
        ProgressNumberReader reader = Reader(ocr);

        ProgressDetectionResult wynik = await reader.ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Equal(17, wynik.Percent);
        Assert.Equal("Progress: 17%", wynik.RawText);
        Assert.Equal(2, reader.OcrCalls);
        // Druga próba: skala 1 — wycinek 20×10 + margines 8 z każdej strony, bez powiększenia.
        Assert.Equal(36, ocr.LastImage!.Width);
    }

    [Fact]
    public async Task ReadAsync_ZeroOdczytanePrzySkali2JakoEoz_DajeZero()
    {
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("Progress:").Enqueue("Progress:").Enqueue("Progress: eoz");
        ProgressNumberReader reader = Reader(ocr);

        ProgressDetectionResult wynik = await reader.ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Equal(0, wynik.Percent);
        Assert.False(wynik.HasNonZeroFraction);
        Assert.Equal(3, reader.OcrCalls);
        Assert.Equal(72, ocr.LastImage!.Width);
    }

    [Fact]
    public async Task ReadAsync_PierwszaProbaUdana_NieRobiKolejnych()
    {
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult("Progress: 99%", ["Progress: 99%"]) };
        ocr.Enqueue("Progress: łez");
        ProgressNumberReader reader = Reader(ocr);

        ProgressDetectionResult wynik = await reader.ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Equal(10, wynik.Percent);
        Assert.Equal(1, reader.OcrCalls);
    }

    [Theory]
    // Próba zapasowa to dodatkowa szansa na śmieciowy odczyt — goła cyfra bez '%' (ani 'z' zamiast '%') z próby
    // zapasowej zostaje „nie wiem”. W pierwszej próbie zasady parsera bez zmian (np. „99” → 99).
    [InlineData("Progress: 6")]
    [InlineData("M023. 62")]
    public async Task ReadAsync_ProbaZapasowaBezZnakuProcentu_NieDajeLiczby(string zapasowy)
    {
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult(zapasowy, [zapasowy]) };
        ocr.Enqueue("Progress:");
        ProgressNumberReader reader = Reader(ocr);

        ProgressDetectionResult wynik = await reader.ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Null(wynik.Percent);
        Assert.False(wynik.IsFull);
        Assert.Equal("Progress:", wynik.RawText);
        Assert.Equal(3, reader.OcrCalls);
    }

    [Fact]
    public async Task ReadAsync_AwariaSilnika_DajeNieczytelnyBezProbZapasowych()
    {
        var ocr = new FakeOcrEngine { Failure = () => new InvalidOperationException("WinRT"), FailingCalls = int.MaxValue };
        ProgressNumberReader reader = Reader(ocr);

        ProgressDetectionResult wynik = await reader.ReadAsync(Wycinek(), CancellationToken.None);

        Assert.Null(wynik.Percent);
        Assert.Equal(1, reader.OcrCalls);
    }
}
