using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Probe.Tests.TestUtil;

namespace MaszynyAlert.Probe.Tests;

public sealed class ScreenAnchorsTests
{
    private const int FrameWidth = 400;
    private const int FrameHeight = 200;

    private static readonly ScreenProfile Profile = new()
    {
        Id = "test-opencnc",
        StatusBarRoi = new RectInt(0, 0, 400, 40),
    };

    private static RgbaImage Frame(int w = FrameWidth, int h = FrameHeight)
    {
        var img = new RgbaImage(w, h);
        img.Fill(new Rgba(235, 235, 235));
        return img;
    }

    private static OcrWord Word(string text, int x, int y, int width, int lineIndex, int height = 14)
        => new(text, new RectInt(x, y, width, height), lineIndex);

    // ---------------------------------------------------------------------------------------------
    // ComputeAsync — pełne kotwiczenie jednej klatki.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ComputeAsync_GdyHmiNieMaEtykietyMillingStatus_KotwiczyNaProgress()
    {
        var ocr = new QueueOcrEngine();
        ocr.EnqueueWords(
            Word("Progress:", 10, 100, 80, 0),
            Word("22%", 200, 100, 40, 0),
            Word("Job_2026-09-18_14-15-46.NC", 10, 140, 200, 1));
        ocr.EnqueueText("22%");
        ocr.EnqueueText("Job_2026-09-18_14-15-46.NC");

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(), ocr, Profile, CancellationToken.None);

        Assert.NotNull(anchors.ProgressRoi);
        Assert.Equal(22, anchors.ProgressPercent);
    }

    [Fact]
    public async Task ComputeAsync_GdySaObieEtykiety_WygrywaMillingStatus()
    {
        // Nazwa pliku jest w kolejce po to, żeby kotwiczenie zamknęło się na pierwszym przebiegu OCR
        // (bez niej ComputeAsync ponawia odczyt na klatce ×2 i zjada kolejne pozycje kolejki).
        var ocr = new QueueOcrEngine();
        ocr.EnqueueWords(
            Word("Progress", 10, 60, 80, 0),
            Word("1%", 200, 60, 30, 0),
            Word("Milling", 10, 100, 70, 1),
            Word("Status", 90, 100, 60, 1),
            Word("79.08%", 170, 100, 70, 1),
            Word("Job_15-20-29.nc", 10, 140, 120, 2));
        ocr.EnqueueText("79.08%");
        ocr.EnqueueText("Job_15-20-29.nc");

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(), ocr, Profile, CancellationToken.None);

        Assert.Equal(79, anchors.ProgressPercent);
    }

    [Fact]
    public async Task ComputeAsync_GdyOcrWidziObieEtykiety_ZwracaObszaryIOdczytanyProcent()
    {
        // Dlaczego: to jest docelowy przebieg kroku „ocr” — z jednego zrzutu mają wyjść gotowe ROI do
        // wklejenia w config.json ORAZ dowód, że kotwica trafiła (odczyt 79 % z wyciętego pola).
        var ocr = new QueueOcrEngine();
        ocr.EnqueueWords(
            Word("Milling", 10, 100, 70, 0),
            Word("Status", 90, 100, 60, 0),
            Word("79.08%", 170, 100, 70, 0),
            Word("File", 10, 140, 40, 1),
            Word("Name", 60, 140, 50, 1),
            Word("Job_15-20-29.nc", 120, 140, 120, 1));
        ocr.EnqueueText("79.08%");            // weryfikacja wyciętego pola postępu
        ocr.EnqueueText("Job_15-20-29.nc");   // weryfikacja wyciętego pola nazwy pliku

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(), ocr, Profile, CancellationToken.None);

        // Pole wartości = słowo na prawo od etykiety, powiększone o margines (6 px w poziomie, 7 px w pionie).
        Assert.Equal(new RectInt(164, 93, 82, 28), anchors.ProgressRoi);
        Assert.Equal("79.08%", anchors.ProgressRawText);
        Assert.Equal(79, anchors.ProgressPercent);

        // Obszar wzięty z PRAWDZIWIE odczytanego słowa — nie zgadnięty, więc wolno go podać do wklejenia.
        Assert.False(anchors.ProgressRoiIsGuess);

        Assert.Equal(new RectInt(114, 133, 132, 28), anchors.FileNameRoi);
        Assert.Equal("Job_15-20-29.nc", anchors.FileNameRawText);

        // Bez słów „Tool”/„Manual” heurystyka okienka alertów nie ma na czym się oprzeć — zostaje profil.
        Assert.False(anchors.AlertWindowIsHeuristic);
        Assert.Equal(Profile.StatusBarRoi, anchors.AlertWindowRoi);

        // Jedno OCR całej klatki + po jednym na weryfikację każdego z dwóch pól; klatka ×2 niepotrzebna.
        Assert.Equal(3, ocr.CallCount);
    }

    [Fact]
    public async Task ComputeAsync_GdyEtykietySaCzytelneDopieroWPowiekszeniu_DzieliWspolrzedneNaDwa()
    {
        // Dlaczego: czcionka HMI jest mała i OCR często gubi etykiety w skali 1:1. Powtórka na klatce ×2
        // ratuje odczyt, ale wynik MUSI wrócić do współrzędnych oryginalnego ekranu — inaczej program
        // prosiłby maszynę o obszar dwa razy za daleko i czytałby zupełnie inne miejsce ekranu.
        var ocr = new QueueOcrEngine();
        ocr.EnqueueEmpty();                    // skala 1:1 — OCR nic nie odczytał
        ocr.EnqueueWords(                      // ta sama klatka powiększona ×2
            Word("Milling", 20, 200, 140, 0, height: 28),
            Word("Status", 180, 200, 120, 0, height: 28),
            Word("79.08%", 340, 200, 140, 0, height: 28),
            Word("File", 20, 280, 80, 1, height: 28),
            Word("Name", 120, 280, 100, 1, height: 28),
            Word("Job.nc", 240, 280, 240, 1, height: 28));
        ocr.EnqueueText("79.08%");
        ocr.EnqueueText("Job.nc");

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(), ocr, Profile, CancellationToken.None);

        Assert.Equal(new RectInt(167, 93, 76, 28), anchors.ProgressRoi);
        Assert.Equal(new RectInt(117, 133, 126, 28), anchors.FileNameRoi);
        Assert.Equal(79, anchors.ProgressPercent);

        // Druga próba dostała obraz dokładnie dwa razy większy — to ona jest „powiększeniem”.
        Assert.Equal(4, ocr.CallCount);
        Assert.Equal((FrameWidth, FrameHeight), ocr.ImageSizes[0]);
        Assert.Equal((FrameWidth * 2, FrameHeight * 2), ocr.ImageSizes[1]);
    }

    [Fact]
    public async Task ComputeAsync_GdyOcrNieZnajdujeEtykietWcale_ZwracaPustePolaBezWeryfikacji()
    {
        // Dlaczego: „nie znaleziono” musi być normalnym, opisanym wynikiem (użytkownik dostanie wtedy
        // instrukcję ręcznego wskazania pola), a nie wyjątkiem przerywającym cały przebieg auto.
        var ocr = new QueueOcrEngine(); // kolejka pusta → każdy odczyt pusty

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(), ocr, Profile, CancellationToken.None);

        Assert.Null(anchors.ProgressRoi);
        Assert.Null(anchors.ProgressRawText);
        Assert.Null(anchors.ProgressPercent);
        Assert.Null(anchors.FileNameRoi);
        Assert.Null(anchors.FileNameRawText);
        Assert.Equal(Profile.StatusBarRoi, anchors.AlertWindowRoi);
        Assert.False(anchors.AlertWindowIsHeuristic);

        // Tylko dwie próby (1:1 i ×2) — nie ma czego weryfikować, więc nie ma dalszych wywołań OCR.
        Assert.Equal(2, ocr.CallCount);
        Assert.Equal("nie znaleziono", ScreenAnchors.DescribeProgressField(anchors));
        Assert.Equal("nie znaleziono", ScreenAnchors.DescribeFileNameField(anchors));
    }

    [Fact]
    public async Task ComputeAsync_GdySilnikOcrJestNiedostepny_WcaleGoNieWywolujeIZwracaObszarZProfilu()
    {
        // Dlaczego: na komputerze bez pakietu językowego OCR krok „ocr” ma się zakończyć czytelnym
        // „brak OCR”, a nie wywołaniem niedostępnego silnika (i wyjątkiem WinRT w środku przebiegu auto).
        var ocr = new QueueOcrEngine { IsAvailable = false };

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(), ocr, Profile, CancellationToken.None);

        Assert.Equal(0, ocr.CallCount);
        Assert.Null(anchors.ProgressRoi);
        Assert.Equal(Profile.StatusBarRoi, anchors.AlertWindowRoi);
        Assert.False(anchors.AlertWindowIsHeuristic);
    }

    [Fact]
    public async Task ComputeAsync_GdyWeryfikacjaPolaNieDaLiczby_ZostawiaObszarAlePustyProcent()
    {
        // Dlaczego: sam obszar bywa dobry, a odczyt nieczytelny (rozmyta klatka) — tabela ma wtedy
        // powiedzieć „OCR nie odczytał liczby”, a nie ukrywać znaleziony obszar.
        var ocr = new QueueOcrEngine();
        ocr.EnqueueWords(
            Word("Milling", 10, 100, 70, 0),
            Word("Status", 90, 100, 60, 0),
            Word("79.08%", 170, 100, 70, 0));
        ocr.EnqueueEmpty();  // klatka ×2 (bo nie znaleziono „File Name”)
        ocr.EnqueueText("???"); // weryfikacja pola postępu — nieczytelna

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(), ocr, Profile, CancellationToken.None);

        Assert.Equal(new RectInt(164, 93, 82, 28), anchors.ProgressRoi);
        Assert.Null(anchors.ProgressPercent);
        Assert.Equal("164,93,82,28 (OCR nie odczytał liczby)", ScreenAnchors.DescribeProgressField(anchors));
    }

    [Fact]
    public async Task ComputeAsync_GdyOcrRozdzielaEtykieteIWartoscNaOsobneLinie_OznaczaObszarJakoZgadniety()
    {
        var ocr = new QueueOcrEngine();
        ocr.EnqueueWords(
            Word("Progress:", 429, 251, 67, lineIndex: 0, height: 18),
            Word("22%", 739, 251, 34, lineIndex: 1, height: 18));
        ocr.EnqueueEmpty();  // klatka ×2 (nazwy pliku nie ma)
        ocr.EnqueueEmpty();  // weryfikacja pola postępu — w zgadniętym miejscu nie ma nic do odczytania

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(Frame(800, 600), ocr, Profile, CancellationToken.None);

        Assert.True(anchors.ProgressRoi.HasValue);
        RectInt roi = anchors.ProgressRoi.Value;
        Assert.True(anchors.ProgressRoiIsGuess);
        Assert.Null(anchors.ProgressPercent);
        Assert.True(
            roi.X + roi.Width <= 739,
            $"Zgadnięty obszar {ScreenAnchors.FormatRoi(roi)} miałby obejmować liczbę procent — wtedy nie byłoby o czym mówić.");
        Assert.Equal($"{ScreenAnchors.FormatRoi(roi)} (zgadnięte obok etykiety)", ScreenAnchors.DescribeProgressField(anchors));
    }

    [Fact]
    public async Task ComputeAsync_NullowychArgumentow_RzucaArgumentNullException()
    {
        var ocr = new QueueOcrEngine();

        await Assert.ThrowsAsync<ArgumentNullException>(() => ScreenAnchors.ComputeAsync(null!, ocr, Profile, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ScreenAnchors.ComputeAsync(Frame(), null!, Profile, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ScreenAnchors.ComputeAsync(Frame(), ocr, null!, CancellationToken.None));
    }

    // ---------------------------------------------------------------------------------------------
    // FindAlertWindowHeuristic — okienko alertów NAD przyciskami „Tool” i „Manual”.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void FindAlertWindowHeuristic_SlowTOOLIMANUALWTejSamejLinii_ZwracaProstokatNadNimi()
    {
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Tool", 100, 300, 40, 0, height: 20),
            Word("Manual", 200, 300, 60, 0, height: 20),
        ]);

        RectInt? roi = ScreenAnchors.FindAlertWindowHeuristic(ocr, 1024, 768);

        Assert.NotNull(roi);
        // 20 px w lewo od „Tool”, w prawo symetrycznie za „Manual”, w pionie od 7× do 2,5× wysokości słowa.
        Assert.Equal(new RectInt(80, 160, 340, 90), roi!.Value);
        Assert.True(roi.Value.Bottom < 300, "Okienko alertów ma leżeć NAD przyciskami, nie na nich.");
    }

    [Fact]
    public void FindAlertWindowHeuristic_GdyProstokatWychodziPozaKlatke_PrzycinaGoDoNiej()
    {
        // Dlaczego: wynik trafia wprost do żądania VNC o wycinek ekranu — obszar wystający poza ekran
        // maszyny zostałby odrzucony przez serwer.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Tool", 100, 300, 40, 0, height: 20),
            Word("Manual", 200, 300, 60, 0, height: 20),
        ]);

        RectInt? roi = ScreenAnchors.FindAlertWindowHeuristic(ocr, 300, 400);

        Assert.NotNull(roi);
        Assert.Equal(new RectInt(80, 160, 220, 90), roi!.Value);
        Assert.True(roi.Value.Right <= 300 && roi.Value.Bottom <= 400);
    }

    [Fact]
    public void FindAlertWindowHeuristic_ZLekkoPrzekreconymiSlowami_NadalJeRozpoznaje()
    {
        // Dlaczego: OCR małej czcionki dokleja dwukropki i gubi litery — wymóg dosłownego „Tool”/„Manual”
        // oznaczałby, że heurystyka działa raz na kilka uruchomień.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Tool:", 100, 300, 40, 0, height: 20),
            Word("Manua", 200, 300, 60, 0, height: 20),
        ]);

        Assert.NotNull(ScreenAnchors.FindAlertWindowHeuristic(ocr, 1024, 768));
    }

    [Fact]
    public void FindAlertWindowHeuristic_GdyBrakujeJednegoZeSlowAlboSaWZlejKolejnosci_ZwracaNull()
    {
        // Dlaczego: gdy nie ma pewnego punktu zaczepienia, wołający ma sięgnąć po statusBarRoi z profilu —
        // zgadywany obszar byłby gorszy niż znany, sprawdzony pasek statusu.
        OcrResult bezManual = OcrResult.FromWords([Word("Tool", 100, 300, 40, 0, height: 20)]);
        Assert.Null(ScreenAnchors.FindAlertWindowHeuristic(bezManual, 1024, 768));

        OcrResult odwrotnaKolejnosc = OcrResult.FromWords(
        [
            Word("Manual", 100, 300, 60, 0, height: 20),
            Word("Tool", 200, 300, 40, 0, height: 20),
        ]);
        Assert.Null(ScreenAnchors.FindAlertWindowHeuristic(odwrotnaKolejnosc, 1024, 768));

        OcrResult inneLinie = OcrResult.FromWords(
        [
            Word("Tool", 100, 300, 40, 0, height: 20),
            Word("Manual", 200, 340, 60, 1, height: 20),
        ]);
        Assert.Null(ScreenAnchors.FindAlertWindowHeuristic(inneLinie, 1024, 768));

        Assert.Null(ScreenAnchors.FindAlertWindowHeuristic(OcrResult.Empty, 1024, 768));
    }

    [Fact]
    public void FindAlertWindowHeuristic_GdyPrzyciskiSaPrzyGornejKrawedzi_ZwracaNullZamiastPustegoProstokata()
    {
        // Dlaczego: nad przyciskami nie ma wtedy miejsca na okienko — pusty prostokąt (0 px wysokości)
        // byłby bezużyteczny, a w watch dawałby wycinek, którego serwer i tak by nie przysłał.
        OcrResult ocr = OcrResult.FromWords(
        [
            Word("Tool", 100, 5, 40, 0, height: 20),
            Word("Manual", 200, 5, 60, 0, height: 20),
        ]);

        Assert.Null(ScreenAnchors.FindAlertWindowHeuristic(ocr, 1024, 768));
    }

    [Fact]
    public void FindAlertWindowHeuristic_Null_RzucaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ScreenAnchors.FindAlertWindowHeuristic(null!, 100, 100));
    }

    // ---------------------------------------------------------------------------------------------
    // Formatowanie wyników dla użytkownika.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void FormatRoiJsonSnippet_ZwracaFragmentDoWklejeniaWConfigJson()
    {
        // Dlaczego: użytkownik ma ten fragment SKOPIOWAĆ do config.json — musi być poprawnym JSON-em
        // i nazywać się dokładnie tak, jak pole w konfiguracji, bo nikt tego za niego nie poprawi.
        string snippet = ScreenAnchors.FormatRoiJsonSnippet("progressRoi", new RectInt(440, 380, 120, 24));

        Assert.Equal("\"progressRoi\": { \"x\": 440, \"y\": 380, \"width\": 120, \"height\": 24 }", snippet);

        // Fragment musi się parsować po opakowaniu w obiekt — inaczej wklejenie zepsułoby config.json.
        using var doc = System.Text.Json.JsonDocument.Parse("{" + snippet + "}");
        System.Text.Json.JsonElement roi = doc.RootElement.GetProperty("progressRoi");
        Assert.Equal(440, roi.GetProperty("x").GetInt32());
        Assert.Equal(24, roi.GetProperty("height").GetInt32());
    }

    [Fact]
    public void FormatRoi_ZwracaZwiezlyFormatXYWH()
    {
        Assert.Equal("440,380,120,24", ScreenAnchors.FormatRoi(new RectInt(440, 380, 120, 24)));
    }

    [Fact]
    public void DescribeProgressField_DlaZnalezionegoPolaZOdczytem_PodajeObszarIProcent()
    {
        var anchors = new MachineAnchors
        {
            ProgressRoi = new RectInt(440, 380, 120, 24),
            ProgressRawText = "79.08%",
            ProgressPercent = 79,
            FileNameRoi = new RectInt(300, 60, 320, 20),
        };

        Assert.Equal("440,380,120,24 (odczyt 79 %)", ScreenAnchors.DescribeProgressField(anchors));
        Assert.Equal("300,60,320,20", ScreenAnchors.DescribeFileNameField(anchors));
    }

    [Fact]
    public void Fallback_UstawiaObszarOknaAlertowNaPasekStatusuProfilu()
    {
        MachineAnchors fallback = MachineAnchors.Fallback(new RectInt(0, 0, 1024, 40));

        Assert.Equal(new RectInt(0, 0, 1024, 40), fallback.AlertWindowRoi);
        Assert.False(fallback.AlertWindowIsHeuristic);
        Assert.Null(fallback.ProgressRoi);
        Assert.Null(fallback.FileNameRoi);
    }
}
