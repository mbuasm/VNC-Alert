using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class ProductionAmm580SamplesTests
{
    /// <summary>Obszary, w których czerwień na tym ekranie występuje NORMALNIE (wskaźniki OFF) — patrz test pułapki.</summary>
    private static readonly RectInt OffIndicators = new(300, 85, 110, 210);

    /// <summary>
    /// Rozdzielenie barwy w OBIE STRONY na prawdziwych pikselach: alarm ma zostać rozpoznany, a obie
    /// klatki bez alarmu mają dać dokładnie zero. To jest cała wartość tego profilu — reszta testów
    /// pilnuje, żeby ktoś go później nie rozszerzył w miejsce, gdzie czerwień jest normalna.
    /// </summary>
    [Fact]
    public void NatywneKlatki_ZachowujaBajtyGeometrieIRozdzielenieBarwy()
    {
        Amm580Manifest manifest = LoadManifest();
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(7, manifest.Samples.Count);
        Assert.Equal(7, manifest.Samples.Select(s => s.Sha256).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(manifest.Samples, s => s.ExpectedRed);
        Assert.Contains(manifest.Samples, s => !s.ExpectedRed);

        ScreenProfile profile = TestPaths.Profile("amm580");
        var detector = new RedTextDetector(profile);

        foreach (Amm580Sample sample in manifest.Samples)
        {
            Assert.False(string.IsNullOrWhiteSpace(sample.Evidence));
            Assert.False(string.IsNullOrWhiteSpace(sample.SourceKind));

            if (sample.Native)
            {
                Assert.Equal("32bpp depth 24 BGRA32 LE", sample.PixelFormat);
                Assert.Equal("RA2_256", sample.SecurityType);
            }
            else
            {
                // Klatka, która NIE przyszła prosto z VNC, nie może wyznaczać progu wykrywania alarmu:
                // po stratnej rekompresji jej kolory są przybliżeniem. Wolno jej dowodzić geometrii
                // i tego, że barwy NIE ma (0 pikseli to wynik, którego kompresja nie wyczarowała).
                Assert.Null(sample.MinRedFraction);
                Assert.False(sample.ExpectedRed);
            }

            string path = ResolveSample(sample.SamplePath);
            Assert.True(File.Exists(path), $"Brak próbki '{path}'.");
            Assert.Equal(sample.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            RgbaImage frame = PngCodec.DecodeFile(path);
            Assert.Equal(800, frame.Width);
            Assert.Equal(600, frame.Height);

            RedDetectionResult red = detector.Analyze(frame.Crop(profile.StatusBarRoi));
            Assert.Equal(sample.ExpectedRed, red.IsRed);

            if (sample.MinRedFraction is { } min)
            {
                Assert.True(red.RedFraction >= min,
                    $"Próbka '{sample.Id}': udział barwy {red.RedFraction:P2} spadł poniżej zmierzonego {min:P2}.");
            }

            if (sample.MaxRedFraction is { } max)
            {
                Assert.True(red.RedFraction <= max,
                    $"Próbka '{sample.Id}': udział barwy {red.RedFraction:P3} przekracza zmierzone {max:P3}.");
            }
        }
    }

    [Fact]
    public void KotwiceEkranuGlownego_PasujaDoPrawdziwegoOdczytuOcr_TakzePrzyAlarmie()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        MainScreenWatchProfile mainScreen = profile.MainScreen!;
        Assert.True(mainScreen.Enabled);
        Assert.True(mainScreen.StatusBarAlwaysVisible);

        var withOcr = LoadManifest().Samples.Where(s => !string.IsNullOrEmpty(s.OcrText)).ToList();
        Assert.Equal(5, withOcr.Count);

        foreach (Amm580Sample sample in withOcr)
        {
            OcrResult ocr = OcrFromRealText(sample.OcrText!);

            Assert.NotNull(LabelAnchorLocator.FindAny(
                ocr, mainScreen.EffectiveAnchorLabels, 800, 600, mainScreen.MaxDistance));

            Assert.Null(LabelAnchorLocator.FindAny(
                ocr, MainScreenWatchProfile.DefaultAnchorLabels, 800, 600, mainScreen.MaxDistance));
        }
    }

    [Fact]
    public void ObszarPaskaStatusu_MiesciSieWKlatce800x600()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        RectInt roi = profile.StatusBarRoi;

        Assert.False(roi.IsEmpty);
        Assert.Equal(roi, roi.Clamp(800, 600));
        Assert.True(roi.Bottom <= 600, $"Dolna krawędź ROI ({roi.Bottom}) wychodzi poza ekran 800×600.");
        Assert.True(roi.Right <= 800, $"Prawa krawędź ROI ({roi.Right}) wychodzi poza ekran 800×600.");
    }

    /// <summary>
    /// Pułapka zapisana w konfiguracji: na tym ekranie czerwień występuje NORMALNIE — wskaźniki
    /// „Spindle / Coolant / Tool Unclamp / HW Gear: OFF” to czerwone plakietki, a w całej klatce barwa
    /// zajmuje 2,85 %, czyli więcej niż próg profilu (2 %). Gdyby ktoś poszerzył obszar paska statusu na
    /// większą część ekranu, stojąca maszyna alarmowałaby bez przerwy. Ten test mierzy to na prawdziwych
    /// pikselach i pilnuje, że obszar z profilu tamtych miejsc nie dotyka.
    /// </summary>
    [Fact]
    public void CzerwoneWskaznikiOff_SaPozaObszaremPaskaStatusu()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        RgbaImage frame = PngCodec.DecodeFile(ResolveSample("alarm-t4-tool-life.png"));

        double offFraction = RedFraction(frame.Crop(OffIndicators), profile.Red);
        double wholeFrame = RedFraction(frame, profile.Red);

        Assert.True(offFraction > profile.MinRedFraction,
            $"Wskaźniki OFF dają {offFraction:P2} barwy — test pułapki ma sens tylko, gdy to przekracza próg {profile.MinRedFraction:P2}.");
        Assert.True(wholeFrame > profile.MinRedFraction,
            $"Cała klatka daje {wholeFrame:P2} barwy — powyżej progu {profile.MinRedFraction:P2}, więc ROI nie może obejmować całego ekranu.");
        Assert.True(profile.StatusBarRoi.Intersect(OffIndicators).IsEmpty,
            "Obszar paska statusu dotknął wskaźników OFF — stojąca maszyna zaczęłaby alarmować.");
    }

    /// <summary>
    /// Obszar liczby procent ma obejmować wiersz „Progress: 22%” (y 251-268), a NIE sam pasek postępu
    /// (x 422-771, y 277-286). Jasny tor paska w wycinku psułby OCR liczby.
    /// </summary>
    [Fact]
    public void ObszarLiczbyProcentu_ObejmujeWierszTekstuINieDotykaPaskaPostepu()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        IdleWatchProfile idle = profile.Idle!;
        Assert.True(idle.Enabled);
        Assert.Equal(ProgressMode.Number, idle.Mode);

        RectInt roi = idle.ProgressRoi;
        Assert.Equal(roi, roi.Clamp(800, 600));
        // Zmierzone położenie tekstu: etykieta x 428-494, liczba x 738-772, oba w wierszach y 251-268.
        Assert.True(roi.Contains(760, 260), "Obszar nie obejmuje zmierzonego położenia liczby procent (x 738-772, y 251-268).");
        Assert.True(roi.Contains(430, 252), "Obszar nie obejmuje początku wiersza (etykieta 'Progress:' od x=428).");

        // Pasek postępu (x 422-771, y 277-286) NIE może wejść do wycinka: jego jasny tor RGB(227,225,225)
        // zajmuje wtedy dolne wiersze i psuje OCR liczby.
        var progressBar = new RectInt(422, 277, 350, 10);
        Assert.True(roi.Intersect(progressBar).IsEmpty,
            $"Obszar liczby procent dotknął paska postępu (dolna krawędź {roi.Bottom}, pasek zaczyna się na y=277).");
    }

    /// <summary>
    /// Fakt zmierzony przy okazji, wart zapisania: na tym HMI zarówno WYPEŁNIENIE paska postępu
    /// (RGB(0,165,207) ≈ 192°), jak i sam TEKST wiersza „Progress: 22%” (RGB(119,190,255) ≈ 208°) są
    /// niebieskie — mieszczą się w jednym oknie barwy. Gdyby ktoś kiedyś przestawił ten profil na tryb
    /// „bar”, obszar wypełnienia musi objąć WYŁĄCZNIE pasek: tekst nad nim ma ten sam odcień i zostałby
    /// policzony jako wypełnienie, zawyżając postęp.
    /// </summary>
    [Fact]
    public void TekstProcentuIWypelnieniePaska_MajaTenSamOdcien_WiecObszaryMuszaBycRozdzielone()
    {
        RgbaImage frame = PngCodec.DecodeFile(ResolveSample("alarm-t4-tool-life.png"));
        var fill = new HsvRange { HueMinDeg = 180, HueMaxDeg = 210, MinSaturation = 0.5, MinValue = 0.4 };

        int inText = CountInRange(frame.Crop(new RectInt(420, 246, 370, 30)), fill);
        int inBar = CountInRange(frame.Crop(new RectInt(422, 277, 350, 10)), fill);

        Assert.True(inText > 0, "W wierszu tekstu nie ma pikseli w oknie barwy — pomiar przestał opisywać tę klatkę.");
        Assert.True(inBar > 0, "W pasku postępu nie ma pikseli wypełnienia — pomiar przestał opisywać tę klatkę.");
    }

    /// <summary>
    /// Czego w kotwicach BYĆ NIE MOŻE: etykiet z lewej kolumny („Actual F”, „Spindle”, „Coolant”,
    /// „Tool Unclamp”). Te same napisy stoją także na zakładce Manual — klatka z oknem kalibracji jest
    /// tego dowodem — więc kotwica na nich uznawałaby obcy ekran za główny i program przestałby
    /// przypominać operatorowi o powrocie.
    /// </summary>
    [Fact]
    public void KotwiceEkranuGlownego_NieOpierajaSieNaEtykietachWidocznychTakzePozaEkranemGlownym()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        MainScreenWatchProfile mainScreen = profile.MainScreen!;

        string[] wspolneZManual = ["Actual", "Spindle", "Coolant", "Unclamp", "MagDoor", "Machine"];
        foreach (IReadOnlyList<string> variant in mainScreen.EffectiveAnchorLabels)
        {
            foreach (string word in variant)
            {
                Assert.DoesNotContain(wspolneZManual, w => word.Contains(w, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>
    /// Postęp 0 %: pasek nie ma ani jednego piksela wypełnienia, a liczba „0%” stoi w obszarze
    /// <c>progressRoi</c>. To jest klatka, na której alert „Stoi na 0 % – wciśnij Start” musiałby zadziałać.
    /// </summary>
    [Fact]
    public void KlatkaBezAlarmu_MaPusteWypelnieniePaskaIliczbeWObszarzeProcentu()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        RgbaImage frame = PngCodec.DecodeFile(ResolveSample("auto-bez-alarmu-0-procent.png"));

        var fill = new HsvRange { HueMinDeg = 180, HueMaxDeg = 210, MinSaturation = 0.5, MinValue = 0.4 };
        Assert.Equal(0, CountInRange(frame.Crop(new RectInt(422, 277, 350, 10)), fill));

        // Liczba „0%” zmierzona na x 744-766, y 251-268 — wewnątrz obszaru z profilu.
        Assert.True(profile.Idle!.ProgressRoi.Contains(750, 260));
    }

    [Fact]
    public void KlatkaPracy_ZielonePudelkoStanuDajeZeroBarwy_APasekPostepuIliczba21ProcentSaNaSwoimMiejscu()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        RgbaImage frame = PngCodec.DecodeFile(ResolveSample("auto-praca-21-procent-zrzut-okna.png"));
        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);

        // Pudełko stanu „RUN: Normal” zmierzone na x 1-160, y 2-30 — wchodzi w ROI (od x=150).
        var stateBox = new RectInt(1, 2, 160, 29);
        Assert.False(profile.StatusBarRoi.Intersect(stateBox).IsEmpty,
            "Pudełko stanu nie dotyka ROI — pomiar przestał opisywać tę klatkę i test niczego nie pilnuje.");

        var greenRange = new HsvRange { HueMinDeg = 110, HueMaxDeg = 150, MinSaturation = 0.5, MinValue = 0.4 };
        Assert.True(CountInRange(frame.Crop(profile.StatusBarRoi), greenRange) > 0,
            "W obszarze paska statusu nie ma zielonych pikseli — bez nich ten test nie dowodzi niczego o jaskrawym pudełku stanu.");

        RedDetectionResult red = new RedTextDetector(profile).Analyze(frame.Crop(profile.StatusBarRoi));
        Assert.False(red.IsRed);
        Assert.Equal(0.0, red.RedFraction);

        // Pułapka wskaźników OFF obowiązuje też przy pracy, choć Spindle i Coolant świecą tu na zielono.
        Assert.True(RedFraction(frame.Crop(OffIndicators), profile.Red) > profile.MinRedFraction,
            "Wskaźniki OFF przestały przekraczać próg — test pułapki szerokiego ROI straciłby sens.");

        // Postęp 21 %: tor paska x 422-771, y 277-286; zmierzone wypełnienie 71 z 350 kolumn.
        var fill = new HsvRange { HueMinDeg = 180, HueMaxDeg = 210, MinSaturation = 0.5, MinValue = 0.4 };
        var progressBar = new RectInt(422, 277, 350, 10);
        double filled = CountInRange(frame.Crop(progressBar), fill) / (double)(progressBar.Width * progressBar.Height);
        Assert.InRange(filled, 0.18, 0.24);

        // Liczba „21%” zmierzona na x 738-772, y 251-265 — wewnątrz obszaru z profilu.
        Assert.True(profile.Idle!.ProgressRoi.Contains(755, 258),
            "Obszar liczby procent nie obejmuje zmierzonego położenia liczby dwucyfrowej (x 738-772, y 251-265).");
    }

    [Theory]
    [InlineData("auto-praca-run-normal-22-procent.png", 22, 0.18, 0.24)]
    [InlineData("auto-praca-run-continue-0-procent.png", 0, 0.0, 0.0)]
    [InlineData("auto-praca-10-procent-2026-09-25.png", 10, 0.07, 0.12)]
    public void KlatkiPracyNatywne_ZielonyStanIbialyKomunikatNieAlarmuja_APostepJestNaSwoimMiejscu(
        string fileName, int percentOnScreen, double minFill, double maxFill)
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        RgbaImage frame = PngCodec.DecodeFile(ResolveSample(fileName));
        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);

        // Jaskrawo zielone pudełko stanu („RUN: …”) wchodzi w ROI — i ma dać zero, nie alarm.
        var greenRange = new HsvRange { HueMinDeg = 110, HueMaxDeg = 150, MinSaturation = 0.5, MinValue = 0.4 };
        Assert.True(CountInRange(frame.Crop(profile.StatusBarRoi), greenRange) > 0,
            "W obszarze paska statusu nie ma zielonych pikseli — bez nich ta klatka nie pilnuje pudełka stanu.");

        RedDetectionResult red = new RedTextDetector(profile).Analyze(frame.Crop(profile.StatusBarRoi));
        Assert.False(red.IsRed);
        Assert.Equal(0.0, red.RedFraction);

        // Pułapka wskaźników OFF obowiązuje także przy pracy — ROI nie wolno na nie poszerzyć.
        Assert.True(RedFraction(frame.Crop(OffIndicators), profile.Red) > profile.MinRedFraction,
            "Wskaźniki OFF przestały przekraczać próg — test pułapki szerokiego ROI straciłby sens.");

        var fill = new HsvRange { HueMinDeg = 180, HueMaxDeg = 210, MinSaturation = 0.5, MinValue = 0.4 };
        var progressBar = new RectInt(422, 277, 350, 10);
        double filled = CountInRange(frame.Crop(progressBar), fill) / (double)(progressBar.Width * progressBar.Height);
        Assert.InRange(filled, minFill, maxFill);

        // Liczby zmierzone na pikselach: „22%” na x 739-772, „0%” na x 744-766 (obie w wierszu y 251-268)
        // i „10%” na x 738-772, y 251-265 — punkt x=755, y=260 leży w każdej z nich.
        const int numberX = 755;
        Assert.True(profile.Idle!.ProgressRoi.Contains(numberX, 260),
            $"Obszar liczby procent nie obejmuje zmierzonego położenia liczby „{percentOnScreen}%”.");

        // Obszar, który krok „ocr” zgadywał obok etykiety (501,245,134,32), tej liczby NIE obejmuje —
        // dlatego zgadnięty prostokąt nie może trafiać do sugerowane-roi.json ani wypierać profilu.
        Assert.False(new RectInt(501, 245, 134, 32).Contains(numberX, 260),
            "Zgadnięty obszar obejmuje liczbę procent — wtedy cała ta poprawka nie miałaby powodu.");
    }

    [Fact]
    public void OdczytOcrPolaPostepu_DajeLiczbeZEkranu_TakzeGdyPolskiOcrCzyta10ProcentJakoLez()
    {
        var withProgressOcr = LoadManifest().Samples
            .Where(s => !string.IsNullOrEmpty(s.ProgressRoiOcrText))
            .ToList();
        Assert.NotEmpty(withProgressOcr);
        Assert.Contains(withProgressOcr, s => s.ProgressRoiOcrText == "Progress: łez");

        foreach (Amm580Sample sample in withProgressOcr)
        {
            Assert.True(sample.ProgressPercentOnScreen.HasValue,
                $"Próbka '{sample.Id}' ma odczyt OCR pola postępu, ale nie ma liczby widocznej na ekranie.");
            Assert.True(PercentParser.TryParse(sample.ProgressRoiOcrText, out int percent, out bool hasNonZeroFraction),
                $"Próbka '{sample.Id}': parser nie odczytał procentu z „{sample.ProgressRoiOcrText}”.");
            Assert.Equal(sample.ProgressPercentOnScreen!.Value, percent);
            Assert.False(hasNonZeroFraction);
        }
    }

    private static int CountInRange(RgbaImage image, HsvRange range)
    {
        int n = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            var pixel = new Rgba(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
            if (ColorMath.InRange(pixel, range))
            {
                n++;
            }
        }

        return n;
    }

    private static double RedFraction(RgbaImage image, RedThresholds thresholds)
    {
        int red = 0;
        int total = image.Pixels.Length / 4;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            var pixel = new Rgba(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
            if (ColorMath.IsRed(pixel, thresholds))
            {
                red++;
            }
        }

        return total == 0 ? 0 : (double)red / total;
    }

    private static string ResolveSample(string fileName) =>
        Path.Combine(TestPaths.SamplesOutput, "production-amm580", fileName);

    /// <summary>Jedna linia słów z PRAWDZIWEGO odczytu OCR (sztuczne prostokąty — patrz granica testu kotwic).</summary>
    private static OcrResult OcrFromRealText(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var built = new List<OcrWord>(words.Length);
        int x = 0;
        foreach (string word in words)
        {
            int width = Math.Max(4, word.Length * 8);
            built.Add(new OcrWord(word, new RectInt(x, 400, width, 16), 0));
            x += width + 6;
        }

        return OcrResult.FromWords(built);
    }

    private static Amm580Manifest LoadManifest()
    {
        string path = ResolveSample("manifest.json");
        Assert.True(File.Exists(path), $"Brak manifestu próbek '{path}'.");
        return JsonSerializer.Deserialize<Amm580Manifest>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record Amm580Manifest
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

        [JsonPropertyName("samples")] public IReadOnlyList<Amm580Sample> Samples { get; init; } = [];
    }

    private sealed record Amm580Sample
    {
        [JsonPropertyName("samplePath")] public string SamplePath { get; init; } = string.Empty;

        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;

        [JsonPropertyName("securityType")] public string SecurityType { get; init; } = string.Empty;

        [JsonPropertyName("pixelFormat")] public string PixelFormat { get; init; } = string.Empty;

        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

        [JsonPropertyName("native")] public bool Native { get; init; }

        [JsonPropertyName("sourceKind")] public string SourceKind { get; init; } = string.Empty;

        [JsonPropertyName("expectedRed")] public bool ExpectedRed { get; init; }

        [JsonPropertyName("minRedFraction")] public double? MinRedFraction { get; init; }

        [JsonPropertyName("maxRedFraction")] public double? MaxRedFraction { get; init; }

        [JsonPropertyName("ocrText")] public string? OcrText { get; init; }

        [JsonPropertyName("progressPercentOnScreen")] public int? ProgressPercentOnScreen { get; init; }

        [JsonPropertyName("progressRoiOcrText")] public string? ProgressRoiOcrText { get; init; }

        [JsonPropertyName("evidence")] public string Evidence { get; init; } = string.Empty;
    }
}
