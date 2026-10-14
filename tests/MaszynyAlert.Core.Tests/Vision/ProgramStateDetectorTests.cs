using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class ProgramStateDetectorTests
{
    private static readonly Rgba Panel = new(43, 43, 43);
    private static readonly Rgba RunGreen = new(34, 172, 56);
    private static readonly Rgba White = new(255, 255, 255);

    /// <summary>Pudełko 40×12 na (10,5); wzorzec 6×4 na (20,8) — współrzędne EKRANU.</summary>
    private static readonly RectInt Box = new(10, 5, 40, 12);

    private static readonly string[] AbortRows =
    [
        "......",
        ".#..#.",
        ".####.",
        "......",
    ];

    private static readonly string[] OtherRows =
    [
        "......",
        ".####.",
        ".#..#.",
        "......",
    ];

    private static ProgramStateProfile Profile(AbortPattern? pattern = null, bool withPattern = true) => new()
    {
        BoxRoi = Box,
        AbortPattern = withPattern ? pattern ?? new AbortPattern { X = 20, Y = 8, Rows = AbortRows, MaxMismatch = 1 } : null,
    };

    /// <summary>Wycinek dokładnie <see cref="Box"/> wypełniony <paramref name="fill"/>, z napisem <paramref name="rows"/> w miejscu (x,y) ekranu.</summary>
    private static RgbaImage BoxImage(Rgba fill, string[]? rows = null, int x = 20, int y = 8, Rgba? ink = null)
    {
        var img = new RgbaImage(Box.Width, Box.Height);
        img.Fill(fill);
        if (rows is not null)
        {
            for (int r = 0; r < rows.Length; r++)
            {
                for (int c = 0; c < rows[r].Length; c++)
                {
                    int px = x - Box.X + c;
                    int py = y - Box.Y + r;
                    if (rows[r][c] == '#' && px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                    {
                        img.SetPixel(px, py, ink ?? White);
                    }
                }
            }
        }

        return img;
    }

    // ------------------------------------------------------------------------------------------------
    // AnalyzeBox — RUN (zieleń)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void AnalyzeBox_ZielonePudelko_ToRun_IWzorcaNiePorownuje()
    {
        // Dlaczego: przy RUN maszyna pracuje — nawet gdyby w zielonym pudełku stał napis o kształcie
        // wzorca, stan „przerwany” nie ma sensu, a „0 %” przy RUN nie może dać alertu postoju.
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(RunGreen, AbortRows), Profile());

        Assert.True(reading.Known);
        Assert.True(reading.Running);
        Assert.InRange(reading.RunFraction, 0.95, 1.0);
        Assert.False(reading.AbortPattern);
        Assert.Equal(-1, reading.AbortMismatch);
        Assert.StartsWith("RUN (zieleń ", reading.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeBox_SzarePudelko_ToIdle_ZZerowymUdzialemZieleni()
    {
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(Panel), Profile());

        Assert.True(reading.Known);
        Assert.False(reading.Running);
        Assert.Equal(0.0, reading.RunFraction);
        Assert.StartsWith("IDLE (zieleń 0 %)", reading.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeBox_ProgRunMinFraction_JestWlacznie()
    {
        // Połowa pudełka zielona, połowa szara: 0,5 przy progu 0,5 to RUN, przy progu 0,51 już nie.
        RgbaImage img = BoxImage(Panel);
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width / 2; x++)
            {
                img.SetPixel(x, y, RunGreen);
            }
        }

        Assert.True(ProgramStateDetector.AnalyzeBox(img, Profile() with { RunMinFraction = 0.5 }).Running);
        Assert.False(ProgramStateDetector.AnalyzeBox(img, Profile() with { RunMinFraction = 0.51 }).Running);
    }

    [Fact]
    public void AnalyzeBox_ZielenPozaOknemBarwy_NieJestRun()
    {
        Assert.False(ProgramStateDetector.AnalyzeBox(BoxImage(new Rgba(0, 165, 207)), Profile()).Running);
        Assert.False(ProgramStateDetector.AnalyzeBox(BoxImage(new Rgba(255, 0, 0)), Profile()).Running);
    }

    // ------------------------------------------------------------------------------------------------
    // AnalyzeBox — wzorzec „IDLE: Abort”
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void AnalyzeBox_NapisZgodnyZeWzorcem_ToAbort()
    {
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(Panel, AbortRows), Profile());

        Assert.False(reading.Running);
        Assert.True(reading.AbortPattern);
        Assert.Equal(0, reading.AbortMismatch);
        Assert.Equal("IDLE (zieleń 0 %), wzorzec Abort: zgodny (0 niezgodnych)", reading.Description);
    }

    [Fact]
    public void AnalyzeBox_InnyNapis_NieJestAbort()
    {
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(Panel, OtherRows), Profile());

        Assert.False(reading.AbortPattern);
        Assert.True(reading.AbortMismatch > 1);
        Assert.Contains("wzorzec Abort: niezgodny", reading.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeBox_PustePudelko_NieJestAbort()
    {
        // Samo tło (np. „IDLE” bez napisu albo napis nieczytelny) — wszystkie '#' wzorca są niezgodne.
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(Panel), Profile());

        Assert.False(reading.AbortPattern);
        Assert.Equal(6, reading.AbortMismatch);
    }

    [Fact]
    public void AnalyzeBox_CalkiemBialePudelko_NieJestAbort()
    {
        // Biała plama (np. podświetlenie) zgadza się z każdym '#', ale łamie wszystkie '.' wzorca.
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(White), Profile());

        Assert.False(reading.Running);
        Assert.False(reading.AbortPattern);
        Assert.Equal(18, reading.AbortMismatch);
    }

    [Theory]
    [InlineData(1, 0, 1, true)]
    [InlineData(-1, 1, 1, true)]
    [InlineData(2, 0, 1, false)]
    [InlineData(2, 0, 2, true)]
    [InlineData(0, -2, 2, true)]
    [InlineData(1, 1, 0, false)]
    public void AnalyzeBox_PrzesuniecieNapisu_MiesciSieWMaxShift(int dx, int dy, int maxShift, bool expected)
    {
        var pattern = new AbortPattern { X = 20, Y = 8, Rows = AbortRows, MaxMismatch = 0, MaxShift = maxShift };

        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(
            BoxImage(Panel, AbortRows, 20 + dx, 8 + dy), Profile(pattern));

        Assert.Equal(expected, reading.AbortPattern);
    }

    [Fact]
    public void AnalyzeBox_PojedynczeZakloceniaPonizejProgu_NadalAbort_PowyzejProgu_Nie()
    {
        // Jeden obcy biały piksel obok napisu (np. fragment kursora): 1 niezgodna komórka.
        RgbaImage noisy = BoxImage(Panel, AbortRows);
        noisy.SetPixel(20 - Box.X, 8 - Box.Y, White);

        var tolerant = new AbortPattern { X = 20, Y = 8, Rows = AbortRows, MaxMismatch = 1, MaxShift = 0 };
        var strict = tolerant with { MaxMismatch = 0 };

        Assert.True(ProgramStateDetector.AnalyzeBox(noisy, Profile(tolerant)).AbortPattern);
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(noisy, Profile(strict));
        Assert.False(reading.AbortPattern);
        Assert.Equal(1, reading.AbortMismatch);
    }

    [Fact]
    public void AnalyzeBox_ObwodkaClearType_NieJestBialymRdzeniem()
    {
        // Piksele obwódki ClearType mają jeden kanał wyraźnie niższy (np. pomarańczowe 255,200,120) —
        // maska „najciemniejszy kanał ≥ 150” ich nie liczy, więc napis z samych obwódek to nie Abort.
        RgbaImage fringe = BoxImage(Panel, AbortRows, ink: new Rgba(255, 200, 120));

        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(fringe, Profile());

        Assert.False(reading.AbortPattern);
        Assert.Equal(6, reading.AbortMismatch);

        // Ten sam napis przy progu 100 — piksele już się liczą.
        var lower = new AbortPattern { X = 20, Y = 8, Rows = AbortRows, WhiteMinChannel = 100 };
        Assert.True(ProgramStateDetector.AnalyzeBox(fringe, Profile(lower)).AbortPattern);
    }

    [Fact]
    public void AnalyzeBox_BezWzorcaWProfilu_NieRozpoznajeAbortZPikseli()
    {
        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(Panel, AbortRows), Profile(withPattern: false));

        Assert.True(reading.Known);
        Assert.False(reading.Running);
        Assert.False(reading.AbortPattern);
        Assert.Equal(-1, reading.AbortMismatch);
        Assert.Contains("bez wzorca Abort", reading.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeBox_WzorzecBezWierszy_NieRozpoznajeAbort()
    {
        // Walidacja konfiguracji takiego wzorca nie przepuści — detektor i tak nie może wtedy alarmować.
        var empty = new AbortPattern { X = 20, Y = 8, Rows = [] };

        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(BoxImage(Panel), Profile(empty));

        Assert.False(reading.AbortPattern);
        Assert.Equal(-1, reading.AbortMismatch);
    }

    // ------------------------------------------------------------------------------------------------
    // AnalyzeBox / AnalyzeFrame — stan nieznany
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(39, 12)]
    [InlineData(40, 11)]
    [InlineData(80, 24)]
    public void AnalyzeBox_WycinekInnegoRozmiaru_ToStanNieznany(int width, int height)
    {
        var img = new RgbaImage(width, height);
        img.Fill(RunGreen);

        ProgramStateReading reading = ProgramStateDetector.AnalyzeBox(img, Profile());

        Assert.False(reading.Known);
        Assert.False(reading.Running);
        Assert.False(reading.AbortPattern);
        Assert.Contains("nieznany", reading.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeFrame_WycinaPudelkoZKlatki()
    {
        var frame = new RgbaImage(100, 40);
        frame.Fill(RunGreen); // zieleń POZA pudełkiem nie może się liczyć
        frame.Blit(BoxImage(Panel, AbortRows), Box.X, Box.Y);

        ProgramStateReading reading = ProgramStateDetector.AnalyzeFrame(frame, Profile());

        Assert.True(reading.Known);
        Assert.False(reading.Running);
        Assert.True(reading.AbortPattern);
    }

    [Fact]
    public void AnalyzeFrame_PudelkoWystajacePozaKlatke_ToStanNieznany()
    {
        // Inna rozdzielczość ekranu niż zmierzona: przycięte pudełko dałoby fałszywy udział zieleni.
        var frame = new RgbaImage(45, 40);
        frame.Fill(Panel);

        ProgramStateReading reading = ProgramStateDetector.AnalyzeFrame(frame, Profile());

        Assert.False(reading.Known);
    }

    // ------------------------------------------------------------------------------------------------
    // HasAbortWord — słowo „Abort” z OCR
    // ------------------------------------------------------------------------------------------------

    /// <summary>Słowo w środku pudełka (środek prostokąta w <see cref="Box"/>).</summary>
    private static OcrWord InBox(string text) => new(text, new RectInt(25, 7, 20, 8), 0);

    [Theory]
    [InlineData("Abort")]
    [InlineData("ABORT")]
    [InlineData("abort")]
    [InlineData("Abort.")] // doklejona kropka
    [InlineData("Abort:")]
    [InlineData("|Abort")] // krawędź pudełka odczytana jako '|'
    [InlineData("IDLE:Abort")] // sklejone z nazwą stanu
    [InlineData("Ab0rt")] // cyfra wypada: „abrt”, odległość 1
    [InlineData("Abołt")] // polski OCR: ł → l, „abolt”, odległość 1
    [InlineData("Abórt")] // ó → o
    [InlineData("Aborl")]
    [InlineData("Abcrt")]
    [InlineData("Abor")]
    public void HasAbortWord_PrzekreceniaOcrWPudelku_Pasuja(string text)
    {
        Assert.True(ProgramStateDetector.HasAbortWord([InBox(text)], Profile()));
    }

    [Theory]
    [InlineData("IDLE")]
    [InlineData("IDLE:")]
    [InlineData("RUN")]
    [InlineData("RUN:")]
    [InlineData("N:")] // typowy odczyt „RUN: Normal” na zielonym tle
    [InlineData("Normal")]
    [InlineData("Stop")]
    [InlineData("Continue")]
    [InlineData("Ab")]
    [InlineData("Ab0r7")] // po normalizacji „abr” — za krótkie
    [InlineData("Aborted")] // 7 liter — za długie
    [InlineData("12345")]
    [InlineData("")]
    public void HasAbortWord_NapisyInnychStanowIPrzypadkoweSlowa_NiePasuja(string text)
    {
        Assert.False(ProgramStateDetector.HasAbortWord([InBox(text)], Profile()));
    }

    [Fact]
    public void HasAbortWord_PelnyOdczytInnychStanow_NiePasuje()
    {
        OcrWord[] normalStop = [InBox("IDLE:"), InBox("Normal"), InBox("Stop")];
        OcrWord[] runContinue = [InBox("RUN:"), InBox("Continue")];

        Assert.False(ProgramStateDetector.HasAbortWord(normalStop, Profile()));
        Assert.False(ProgramStateDetector.HasAbortWord(runContinue, Profile()));
        Assert.True(ProgramStateDetector.HasAbortWord([InBox("IDLE:"), InBox("Abort")], Profile()));
    }

    [Fact]
    public void HasAbortWord_SlowoPozaPudelkiem_NieLiczySie()
    {
        // „Abort” w środkowym pudełku komunikatu albo w G-kodzie to nie stan programu.
        var far = new OcrWord("Abort", new RectInt(300, 7, 30, 8), 0);
        // Prostokąt zahacza o pudełko, ale jego ŚRODEK leży poza nim (pudełko kończy się na x=50).
        var straddling = new OcrWord("Abort", new RectInt(45, 7, 20, 8), 0);
        // Środek w pudełku, choć prostokąt z niego wystaje — to się liczy.
        var centeredInside = new OcrWord("Abort", new RectInt(35, 0, 20, 12), 0);

        Assert.False(ProgramStateDetector.HasAbortWord([far], Profile()));
        Assert.False(ProgramStateDetector.HasAbortWord([straddling], Profile()));
        Assert.True(ProgramStateDetector.HasAbortWord([centeredInside], Profile()));
    }

    [Fact]
    public void HasAbortWord_OdlegloscZero_WymagaDokladnegoSlowaPoNormalizacji()
    {
        ProgramStateProfile exact = Profile() with { AbortMaxDistance = 0 };

        Assert.True(ProgramStateDetector.HasAbortWord([InBox("Abort.")], exact));
        Assert.True(ProgramStateDetector.HasAbortWord([InBox("ABÓRT")], exact));
        Assert.False(ProgramStateDetector.HasAbortWord([InBox("Ab0rt")], exact));
    }

    [Fact]
    public void HasAbortWord_SlowaZKonfiguracjiSaNormalizowane()
    {
        // Administrator wpisze „Abort” wielką literą — ma działać tak samo jak „abort”.
        ProgramStateProfile profile = Profile() with { AbortWords = ["Abort"] };

        Assert.True(ProgramStateDetector.HasAbortWord([InBox("abort")], profile));
    }

    [Fact]
    public void HasAbortWord_BezSlowAlboBezListy_ZwracaFalsz()
    {
        Assert.False(ProgramStateDetector.HasAbortWord([], Profile()));
        Assert.False(ProgramStateDetector.HasAbortWord([InBox("Abort")], Profile() with { AbortWords = [] }));
        Assert.False(ProgramStateDetector.HasAbortWord([InBox("Abort")], Profile() with { BoxRoi = RectInt.Empty }));
    }

    [Theory]
    [InlineData("Abort", "abort")]
    [InlineData("Ab0rt", "abrt")]
    [InlineData("Abołt.", "abolt")]
    [InlineData("ĄĆĘŁŃÓŚŹŻ", "acelnoszz")]
    [InlineData("IDLE: Abort", "idleabort")]
    [InlineData(null, "")]
    [InlineData("123:", "")]
    public void NormalizeWord_ZostawiaTylkoMaleLiteryAsciiIZamieniaPolskieZnaki(string? input, string expected)
    {
        Assert.Equal(expected, ProgramStateDetector.NormalizeWord(input));
    }
}
