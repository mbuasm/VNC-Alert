using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class MainScreenDetectorTests
{
    private const int WordHeight = 14;

    private static OcrWord Word(string text, int x, int y, int width, int lineIndex = 0)
        => new(text, new RectInt(x, y, width, WordHeight), lineIndex);

    /// <summary>Linia z ekranu głównego: „Milling Status 79.08%”.</summary>
    private static OcrResult MainScreenOcr() => OcrResult.FromWords(
    [
        Word("File", 10, 60, 40, 0),
        Word("Name", 60, 60, 50, 0),
        Word("Milling", 10, 100, 70, 1),
        Word("Status", 90, 100, 60, 1),
        Word("79.08%", 170, 100, 70, 1),
    ]);

    /// <summary>Linia z INNEGO ekranu HMI („Production info”) — jest tekst, ale nie ma etykiety ekranu głównego.</summary>
    private static OcrResult ProductionInfoOcr() => OcrResult.FromWords(
    [
        Word("Production", 10, 40, 100, 0),
        Word("info", 120, 40, 40, 0),
        Word("Job_15-20-29.nc", 10, 80, 160, 1),
    ]);

    private static RgbaImage Frame()
    {
        var img = new RgbaImage(320, 200);
        img.Fill(new Rgba(235, 235, 235));
        return img;
    }

    private static MainScreenDetector Detector(FakeOcrEngine ocr, MainScreenWatchProfile? profile = null)
        => new(profile ?? new MainScreenWatchProfile(), ocr);

    [Fact]
    public async Task DetectAsync_GdyEtykietaJestNaKlatce_ZwracaEkranGlownyPoJednymOcr()
    {
        var ocr = new FakeOcrEngine { DefaultResult = MainScreenOcr() };
        MainScreenDetector detector = Detector(ocr);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.True(result.MainScreenVisible);
        Assert.Equal("Milling Status", result.LabelText);
        Assert.False(result.ScaledRetry);
        Assert.Equal(1, detector.OcrCalls);
    }

    [Fact]
    public async Task DetectAsync_GdyEtykietaWidocznaDopieroPoPowiekszeniu_ZwracaEkranGlownyZOznaczeniemRetry()
    {
        // Dlaczego: mała czcionka HMI bywa nieczytelna dla OCR w skali 1:1 (tak samo robi probe.exe) —
        // bez powtórki na klatce ×2 program raportowałby „inny ekran” na maszynie, która stoi na głównym.
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(null);                 // skala 1:1 — nic
        ocr.EnqueueWords(Word("Milling", 20, 200, 140), Word("Status", 180, 200, 120));
        MainScreenDetector detector = Detector(ocr);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.True(result.MainScreenVisible);
        Assert.True(result.ScaledRetry);
        Assert.Equal(2, detector.OcrCalls);
    }

    [Fact]
    public async Task DetectAsync_GdyNaEkranieJestInnyTekstBezEtykiety_ZwracaInnyEkran()
    {
        // Dlaczego: zakładka „Production info” zamiast okna stanu.
        var ocr = new FakeOcrEngine { DefaultResult = ProductionInfoOcr() };
        MainScreenDetector detector = Detector(ocr);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.False(result.MainScreenVisible);
        Assert.Null(result.LabelText);
        Assert.Equal(2, detector.OcrCalls);   // 1:1 i ×2 — obie bez etykiety
    }

    [Fact]
    public async Task DetectAsync_BezPowtorkiPowiekszonej_RobiTylkoJedenOcr()
    {
        var ocr = new FakeOcrEngine { DefaultResult = ProductionInfoOcr() };
        MainScreenDetector detector = Detector(ocr, new MainScreenWatchProfile { RetryScaled = false });

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.False(result.MainScreenVisible);
        Assert.Equal(1, detector.OcrCalls);
    }

    [Fact]
    public async Task DetectAsync_GdyOcrNicNieOdczytal_ZwracaNieWiadomoANieInnyEkran()
    {
        // Dlaczego: pusty odczyt z CAŁEGO ekranu to objaw awarii/przeciążenia OCR albo czarnego ekranu, nie
        // dowód na inną zakładkę — fałszywe „włącz okno podglądu” irytowałoby operatora bez powodu.
        var ocr = new FakeOcrEngine { DefaultResult = OcrResult.Empty };
        MainScreenDetector detector = Detector(ocr);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.Null(result.MainScreenVisible);
        Assert.Equal(MainScreenDetectionResult.Unknown, result);
    }

    [Fact]
    public async Task DetectAsync_GdySilnikOcrRzucaWyjatkiem_ZwracaNieWiadomoBezWyjatku()
    {
        // Dlaczego: awaria WinRT OCR nie może ubić pętli obserwatora (wykrywanie czerwieni ma działać dalej).
        var ocr = new FakeOcrEngine { Failure = () => new InvalidOperationException("WinRT padł") };
        MainScreenDetector detector = Detector(ocr);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.Null(result.MainScreenVisible);
    }

    [Fact]
    public async Task DetectAsync_GdyOcrNiedostepny_ZwracaNieWiadomoBezWolaniaSilnika()
    {
        var ocr = new FakeOcrEngine { IsAvailable = false, DefaultResult = MainScreenOcr() };
        MainScreenDetector detector = Detector(ocr);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.Null(result.MainScreenVisible);
        Assert.Equal(0, detector.OcrCalls);
        Assert.Equal(0, ocr.CallCount);
    }

    [Fact]
    public async Task DetectAsync_EtykietaPrzekreconaPrzezOcr_DopasowujeSieRozmyto()
    {
        // Dlaczego: OCR gubi litery („Millng”) i dokleja interpunkcję („Status:”) — dosłowne dopasowanie
        // dawałoby losowe „inny ekran” co kilka sprawdzeń.
        var ocr = new FakeOcrEngine();
        ocr.EnqueueWords(Word("Millng", 10, 100, 70), Word("Status:", 90, 100, 60), Word("100.00%", 170, 100, 70));
        MainScreenDetector detector = Detector(ocr);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.True(result.MainScreenVisible);
        Assert.Equal("Millng Status:", result.LabelText);
    }

    [Fact]
    public async Task DetectAsync_ZWlasnymiWariantamiEtykiety_UzywaIchZamiastDomyslnych()
    {
        var profile = new MainScreenWatchProfile { AnchorLabels = [["Job", "Progress"]] };
        var ocr = new FakeOcrEngine();
        ocr.EnqueueWords(Word("Job", 10, 100, 40), Word("Progress", 60, 100, 80), Word("45%", 150, 100, 40));
        MainScreenDetector detector = Detector(ocr, profile);

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.True(result.MainScreenVisible);
        Assert.Equal("Job Progress", result.LabelText);
    }

    private static OcrResult Amm580AlarmInformationOcr() => OcrResult.FromWords(
    [
        Word("IDLE:", 10, 10, 40, 0),
        Word("Normal", 55, 10, 50, 0),
        Word("Stop", 110, 10, 40, 0),
        Word("Alarm", 90, 60, 50, 1),
        Word("Information", 145, 60, 90, 1),
        Word("Line", 10, 170, 30, 2),
        Word("Selection", 45, 170, 70, 2),
        Word("BreakPoint", 200, 170, 80, 2),
    ]);

    private static MainScreenWatchProfile Amm580MainScreen(bool withOtherScreenLabels) => new()
    {
        AnchorLabels = [["Progress:"], ["Progress"], ["BreakPoint"], ["Line", "Selection"]],
        OtherScreenLabels = withOtherScreenLabels ? [["Alarm", "Information"]] : [],
        StatusBarAlwaysVisible = true,
    };

    [Fact]
    public async Task DetectAsync_OknoZaslaniajaceEkranGlowny_WygrywaZKotwicami()
    {
        var ocr = new FakeOcrEngine { DefaultResult = Amm580AlarmInformationOcr() };
        MainScreenDetector detector = Detector(ocr, Amm580MainScreen(withOtherScreenLabels: true));

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.False(result.MainScreenVisible);
        Assert.False(result.AlarmPageVisible);
        Assert.Equal("Alarm Information", result.LabelText);
        Assert.Equal(1, detector.OcrCalls);
    }

    [Fact]
    public async Task DetectAsync_OknoZaslaniajacePrzekreconePrzezOcr_DopasowujeSieRozmyto()
    {
        var ocr = new FakeOcrEngine();
        ocr.EnqueueWords(Word("A1arm", 90, 60, 50), Word("lnformation", 145, 60, 90), Word("BreakPoint", 200, 170, 80, 1));
        MainScreenDetector detector = Detector(ocr, Amm580MainScreen(withOtherScreenLabels: true));

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.False(result.MainScreenVisible);
    }

    [Fact]
    public async Task DetectAsync_BezListyOkienZaslaniajacych_ZachowanieJakDotad()
    {
        var ocr = new FakeOcrEngine { DefaultResult = Amm580AlarmInformationOcr() };
        MainScreenDetector detector = Detector(ocr, Amm580MainScreen(withOtherScreenLabels: false));

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.True(result.MainScreenVisible);
    }

    [Fact]
    public async Task DetectAsync_EkranGlownyBezOkna_ZListaOkienNadalEkranGlowny()
    {
        var ocr = new FakeOcrEngine
        {
            DefaultResult = OcrResult.FromWords(
            [
                Word("Progress:", 10, 100, 70, 0),
                Word("22%", 250, 100, 30, 0),
                Word("Line", 10, 170, 30, 1),
                Word("Selection", 45, 170, 70, 1),
            ]),
        };
        MainScreenDetector detector = Detector(ocr, Amm580MainScreen(withOtherScreenLabels: true));

        MainScreenDetectionResult result = await detector.DetectAsync(Frame(), CancellationToken.None);

        Assert.True(result.MainScreenVisible);
    }

    [Fact]
    public void EffectiveAnchorLabels_GdyListaPusta_ZwracaDomyslneWariantyMillingStatus()
    {
        var profile = new MainScreenWatchProfile();

        Assert.Same(MainScreenWatchProfile.DefaultAnchorLabels, profile.EffectiveAnchorLabels);
        Assert.Contains(profile.EffectiveAnchorLabels, v => v.SequenceEqual(["Milling", "Status"]));
        Assert.Contains(profile.EffectiveAnchorLabels, v => v.SequenceEqual(["MillingStatus"]));
    }
}
