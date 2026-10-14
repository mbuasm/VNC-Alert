using System.Security.Cryptography;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class ProductionAidite5xSamplesTests
{
    [Fact]
    public void NatywneKlatki_ZachowujaBajtyGeometrieIRozdzielenieBarwy()
    {
        Aidite5xManifest manifest = LoadManifest();
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(2, manifest.Samples.Count);
        Assert.Equal(2, manifest.Samples.Select(s => s.Sha256).Distinct(StringComparer.Ordinal).Count());
        Assert.All(manifest.Samples, s => Assert.Equal("16bpp depth 16 RGB565 LE", s.PixelFormat));
        Assert.All(manifest.Samples, s => Assert.Equal("Qt for Embedded Linux VNC Server", s.ServerName));

        ScreenProfile profile = TestPaths.Profile("aidite5x");
        var detector = new RedTextDetector(profile);

        foreach (Aidite5xSample sample in manifest.Samples)
        {
            string path = ResolveSample(sample.SamplePath);
            Assert.True(File.Exists(path), $"Brak próbki '{path}'.");
            Assert.Equal(sample.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            Assert.False(string.IsNullOrWhiteSpace(sample.Evidence));

            RgbaImage frame = PngCodec.DecodeFile(path);
            Assert.Equal(1024, frame.Width);
            Assert.Equal(768, frame.Height);

            RgbaImage statusBar = frame.Crop(profile.StatusBarRoi);
            Assert.Equal(954, statusBar.Width);
            Assert.Equal(48, statusBar.Height);

            RedDetectionResult red = detector.Analyze(statusBar);
            Assert.Equal(sample.ExpectedRed, red.IsRed);

            if (sample.MaxRedFraction is { } max)
            {
                Assert.True(red.RedFraction <= max,
                    $"Próbka '{sample.Id}': udział barwy {red.RedFraction:P3} przekracza {max:P3}.");
            }

            if (sample.MinRedFraction is { } min)
            {
                Assert.True(red.RedFraction >= min,
                    $"Próbka '{sample.Id}': udział barwy {red.RedFraction:P3} jest poniżej {min:P3}.");
            }
        }
    }

    /// <summary>
    /// Pułapka zapisana w konfiguracji: bieżąca linia G-kodu jest podświetlana MAGENTĄ tuż poniżej
    /// dolnej granicy okna barwy. Gdyby ktoś kiedyś obniżył <c>hueMinDeg</c>, KAŻDA pracująca maszyna
    /// zaczęłaby alarmować. Ten test trzyma margines na prawdziwych pikselach klatki z awarią (to na niej
    /// widać podświetloną linię) i jednocześnie dowodzi, że przy obecnych progach magenta nie wpada.
    /// </summary>
    [Fact]
    public void Magenta_PodswietleniaLiniiGKodu_NieWpadaWOknoBarwyProfilu()
    {
        ScreenProfile profile = TestPaths.Profile("aidite5x");
        RgbaImage frame = PngCodec.DecodeFile(ResolveSample("auto-awaria-1099.png"));

        // Podgląd G-kodu leży po lewej stronie, pod tabelą pozycji — poza paskiem stanu i poza ROI postępu.
        var gcodePreview = new RectInt(55, 460, 375, 30);
        RgbaImage preview = frame.Crop(gcodePreview);

        var magenta = new List<double>();
        for (int i = 0; i < preview.Pixels.Length; i += 4)
        {
            var pixel = new Rgba(preview.Pixels[i], preview.Pixels[i + 1], preview.Pixels[i + 2], preview.Pixels[i + 3]);
            (double h, double s, double v) = ColorMath.RgbToHsv(pixel.R, pixel.G, pixel.B);
            if (s >= 0.9 && v >= 0.5 && h is > 300 and < 340)
            {
                magenta.Add(h);
            }

            Assert.False(ColorMath.IsRed(pixel, profile.Red),
                $"Piksel podglądu G-kodu RGB({pixel.R},{pixel.G},{pixel.B}) wpadł w okno barwy profilu.");
        }

        Assert.NotEmpty(magenta);
        // Zmierzone natywnie: RGB(213,0,148) ≈ 318,3° i RGB(205,0,139) ≈ 319,3°.
        Assert.True(magenta.Max() < profile.Red.HueMinDeg,
            $"Najwyższy odcień magenty {magenta.Max():F1}° dotknął dolnej granicy {profile.Red.HueMinDeg}°.");
    }

    [Fact]
    public void KotwiceEkranu_PasujaDoPrawdziwegoOdczytuOcrObuKlatek()
    {
        ScreenProfile profile = TestPaths.Profile("aidite5x");
        MainScreenWatchProfile mainScreen = profile.MainScreen!;
        Assert.True(mainScreen.Enabled);

        foreach (Aidite5xSample sample in LoadManifest().Samples)
        {
            OcrResult ocr = OcrFromRealText(sample.OcrText);

            AnchoredField? found = LabelAnchorLocator.FindAny(
                ocr, mainScreen.EffectiveAnchorLabels, 1024, 768, mainScreen.MaxDistance);
            Assert.True(found is not null,
                $"Żadna kotwica profilu nie pasuje do prawdziwego odczytu OCR klatki '{sample.Id}' — " +
                "program uznałby AUTO za obcy ekran i (bez statusBarAlwaysVisible) przestał patrzeć na kolor.");

            Assert.Null(LabelAnchorLocator.FindAny(
                ocr, MainScreenWatchProfile.DefaultAnchorLabels, 1024, 768, mainScreen.MaxDistance));

            // Ten OCR rozbija "Spindle" na dwa słowa, więc wariant jednowyrazowy sam z siebie NIE łapie —
            // dlatego w profilu są warianty ["Spi","ndle"] i ["Tool","NO"].
            Assert.Null(LabelAnchorLocator.FindAny(
                ocr, [["Spindle"]], 1024, 768, mainScreen.MaxDistance));
            Assert.NotNull(LabelAnchorLocator.FindAny(
                ocr, [["Spi", "ndle"]], 1024, 768, mainScreen.MaxDistance));
            Assert.NotNull(LabelAnchorLocator.FindAny(
                ocr, [["Tool", "Leg"]], 1024, 768, mainScreen.MaxDistance));
        }
    }

    /// <summary>
    /// Najważniejszy pojedynczy fakt o tym HMI: przy awarii wyskakujące okno <c>WARN INFO</c> zasłania
    /// etykietę <c>Progress</c>. Gdyby kotwicą było samo to słowo, rozpoznanie ekranu kasowałoby się
    /// dokładnie w chwili awarii. Sprawdzamy to na prawdziwych odczytach obu klatek.
    /// </summary>
    [Fact]
    public void PrzyAwarii_SlowaProgressNieMaWOdczycie_AleKotwicaZDolnejCzesciPanelu_Jest()
    {
        ScreenProfile profile = TestPaths.Profile("aidite5x");
        int maxDistance = profile.MainScreen!.MaxDistance;
        Aidite5xManifest manifest = LoadManifest();

        OcrResult praca = OcrFromRealText(manifest.Samples.Single(s => s.ExpectedScreen == "auto").OcrText);
        OcrResult awaria = OcrFromRealText(manifest.Samples.Single(s => s.ExpectedScreen == "auto-warn-info").OcrText);

        Assert.NotNull(LabelAnchorLocator.FindAny(praca, [["Progress"]], 1024, 768, maxDistance));
        Assert.Null(LabelAnchorLocator.FindAny(awaria, [["Progress"]], 1024, 768, maxDistance));

        // ...a kotwice z dolnej części panelu przeżywają okno WARN INFO na obu klatkach.
        foreach (OcrResult ocr in new[] { praca, awaria })
        {
            Assert.NotNull(LabelAnchorLocator.FindAny(ocr, [["Tool", "Leg"]], 1024, 768, maxDistance));
            Assert.NotNull(LabelAnchorLocator.FindAny(ocr, [["Tool", "NO"]], 1024, 768, maxDistance));
        }
    }

    /// <summary>
    /// Zmierzona granica obszaru postępu: przy awarii okno <c>WARN INFO</c> przykrywa górną część
    /// <c>progressRoi</c>. To jest przyjęte świadomie (procent staje się nieczytelny, więc obserwacja
    /// postoju milczy — zachowawczo), ale ma zostać zapisane w teście, żeby nikt nie „poprawił” tego
    /// obszaru w przekonaniu, że to pomyłka.
    /// </summary>
    [Fact]
    public void ObszarPostepu_ObejmujeWierszProgress_ALeczPrzyAwariiJestZaslonietyPrzezWarnInfo()
    {
        ScreenProfile profile = TestPaths.Profile("aidite5x");
        RectInt progressRoi = profile.Idle!.ProgressRoi;

        RgbaImage praca = PngCodec.DecodeFile(ResolveSample("auto-praca-100.png")).Crop(progressRoi);
        RgbaImage awaria = PngCodec.DecodeFile(ResolveSample("auto-awaria-1099.png")).Crop(progressRoi);
        Assert.Equal(365, praca.Width);
        Assert.Equal(28, praca.Height);

        // Na klatce pracy normalnej obszar zawiera zwykły, szary panel informacyjny — żadnej barwy alarmu.
        Assert.Equal(0.0, YellowOrRedFraction(praca, profile.Red));
        // Na klatce awarii ten sam obszar jest w dużej części żółty — to okno WARN INFO, nie pasek stanu.
        Assert.True(YellowOrRedFraction(awaria, profile.Red) > 0.3);
    }

    private static double YellowOrRedFraction(RgbaImage image, RedThresholds thresholds)
    {
        int matched = 0;
        int total = image.Pixels.Length / 4;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            var pixel = new Rgba(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
            if (ColorMath.IsRed(pixel, thresholds))
            {
                matched++;
            }
        }

        return total == 0 ? 0 : (double)matched / total;
    }

    /// <summary>
    /// Buduje <see cref="OcrResult"/> z PRAWDZIWEJ sekwencji słów odczytanej przez Windows OCR.
    /// Prostokąty są sztuczne (raport <c>probe.exe</c> ich nie zapisuje) — patrz granica opisana przy
    /// <see cref="KotwiceEkranu_PasujaDoPrawdziwegoOdczytuOcrObuKlatek"/>.
    /// </summary>
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

    private static Aidite5xManifest LoadManifest()
    {
        string path = Path.Combine(TestPaths.SamplesOutput, "production-aidite5x", "manifest.json");
        return JsonSerializer.Deserialize<Aidite5xManifest>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException($"Nie można odczytać manifestu '{path}'.");
    }

    private static string ResolveSample(string fileName) =>
        Path.Combine(TestPaths.SamplesOutput, "production-aidite5x", fileName);

    private sealed record Aidite5xManifest
    {
        public int SchemaVersion { get; init; }
        public string SourceRoot { get; init; } = string.Empty;
        public List<Aidite5xSample> Samples { get; init; } = [];
    }

    private sealed record Aidite5xSample
    {
        public string Id { get; init; } = string.Empty;
        public string SamplePath { get; init; } = string.Empty;
        public string SourceRelativePath { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public string Machine { get; init; } = string.Empty;
        public string ServerName { get; init; } = string.Empty;
        public string PixelFormat { get; init; } = string.Empty;
        public string ExpectedScreen { get; init; } = string.Empty;
        public bool ExpectedRed { get; init; }
        public double? MaxRedFraction { get; init; }
        public double? MinRedFraction { get; init; }
        public int? ExpectedProgressPercent { get; init; }
        public string? ExpectedProgressText { get; init; }
        public string OcrText { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
    }
}
