using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

public sealed class ProgressBarReaderTests
{
    private static readonly ProgressBarSource Source580 = new()
    {
        TrackRoi = new RectInt(422, 277, 350, 10),
        Fill = new HsvRange { HueMinDeg = 140, HueMaxDeg = 215, MinSaturation = 0.55, MinValue = 0.35 },
        ColumnsPerPercent = 3.54,
        ColumnsOffset = 2.5,
    };

    private static readonly Rgba Panel = new(38, 38, 38);
    private static readonly Rgba BorderTop = new(81, 80, 80);
    private static readonly Rgba BorderBottom = new(65, 65, 65);
    private static readonly Rgba BorderSide = new(73, 73, 73);
    private static readonly Rgba Corner = new(60, 60, 60);
    private static readonly Rgba FillBlue = new(0, 165, 207);

    private static Rgba TrackBackground(int row, int height)
    {
        byte v = (byte)(128 + (99 * row / Math.Max(1, height - 1)));
        return new Rgba(v, v, v);
    }

    private static Rgba GradientFill(int x, int width)
    {
        double t = (double)x / Math.Max(1, width - 1);
        return new Rgba(0, (byte)Math.Round(165 + (90 * t)), (byte)Math.Round(207 - (52 * t)));
    }

    /// <summary>Zaokrąglony narożnik toru: skrajna kolumna traci 2 wiersze z każdej strony, druga — 1.</summary>
    private static bool IsCorner(int x, int y, int width, int height)
    {
        int fromEnd = Math.Min(x, width - 1 - x);
        int fromEdge = Math.Min(y, height - 1 - y);
        return fromEnd switch
        {
            0 => fromEdge < 2,
            1 => fromEdge < 1,
            _ => false,
        };
    }

    /// <summary>
    /// Ekran 800×600 z paskiem postępu w miejscu <c>trackRoi</c> źródła: <paramref name="filledColumns"/>
    /// wypełnionych kolumn od lewej (kolor z <paramref name="fillAt"/> albo stały niebieski).
    /// </summary>
    private static RgbaImage Screen(ProgressBarSource source, int filledColumns, Func<int, Rgba>? fillAt = null)
    {
        var screen = new RgbaImage(800, 600);
        screen.Fill(Panel);
        RectInt t = source.TrackRoi;
        for (int x = t.X - 1; x <= t.Right; x++)
        {
            screen.SetPixel(x, t.Y - 1, BorderTop);
            screen.SetPixel(x, t.Bottom, BorderBottom);
        }

        for (int y = 0; y < t.Height; y++)
        {
            screen.SetPixel(t.X - 1, t.Y + y, BorderSide);
            screen.SetPixel(t.Right, t.Y + y, BorderSide);
            for (int x = 0; x < t.Width; x++)
            {
                Rgba c = IsCorner(x, y, t.Width, t.Height) ? Corner
                    : x < filledColumns ? (fillAt?.Invoke(x) ?? FillBlue)
                    : TrackBackground(y, t.Height);
                screen.SetPixel(t.X + x, t.Y + y, c);
            }
        }

        return screen;
    }

    private static ProgressBarReading ReadScreen(RgbaImage screen, ProgressBarSource source) =>
        ProgressBarReader.Read(screen.Crop(ProgressBarReader.RequiredArea(source)), source);

    private static void FillRect(RgbaImage img, RectInt r, Rgba c)
    {
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                img.SetPixel(x, y, c);
            }
        }
    }

    private static void AssertNotVisible(ProgressBarReading reading)
    {
        Assert.False(reading.Visible, reading.Description);
        Assert.Null(reading.Percent);
        Assert.Equal(0, reading.FilledColumns);
        Assert.StartsWith("pasek: ", reading.Description, StringComparison.Ordinal);
    }

    // --- Obszar do wycięcia --------------------------------------------------------------------------------

    [Fact]
    public void RequiredArea_ToTorRozszerzonyWPionieOWierszeOtoczenia()
    {
        Assert.Equal(new RectInt(422, 272, 350, 20), ProgressBarReader.RequiredArea(Source580));
        Assert.Equal(Source580.TrackRoi, ProgressBarReader.RequiredArea(Source580 with { SurroundRows = 0 }));
        Assert.Equal(RectInt.Empty, ProgressBarReader.RequiredArea(Source580 with { TrackRoi = RectInt.Empty }));
    }

    // --- Tor widoczny: procent z modelu HMI -----------------------------------------------------------------

    [Fact]
    public void PustyTor_DajeZeroProcentJakoPewnyOdczyt()
    {
        // Dlaczego: 0 % z paska jest dozwolone TYLKO, gdy tor jest widoczny i nie ma ani jednej kolumny
        // wypełnienia — to jest odczyt, na którym opiera się „Stoi na 0 % – wciśnij Start”.
        ProgressBarReading reading = ReadScreen(Screen(Source580, 0), Source580);

        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(0, reading.FilledColumns);
        Assert.Equal(350, reading.TrackWidth);
        Assert.Equal(0, reading.Percent);
        Assert.Equal("pasek 0/350 kol. → 0 %", reading.Description);
    }

    [Fact]
    public void CzesciowyTor_DajeProcentZModeluHmi_IOpisPoPolsku()
    {
        ProgressBarReading reading = ReadScreen(Screen(Source580, 86), Source580);

        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(86, reading.FilledColumns);
        Assert.Equal(25, reading.Percent);
        Assert.Equal("pasek 86/350 kol. → 25 %", reading.Description);
    }

    [Fact]
    public void PelnyTor_DajeStoProcent()
    {
        ProgressBarReading reading = ReadScreen(Screen(Source580, 350), Source580);

        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(350, reading.FilledColumns);
        Assert.Equal(100, reading.Percent);
    }

    [Theory]
    [InlineData(349, 3.54, 2.5, 99)]
    [InlineData(349, 0.0, 0.0, 99)]
    [InlineData(1, 3.54, 2.5, 1)]
    [InlineData(1, 0.0, 0.0, 1)]
    [InlineData(175, 0.0, 0.0, 50)]
    public void CzesciowyTor_NigdyNieDajeZeraAniStu(int columns, double perPercent, double offset, int expected)
    {
        // Dlaczego: jedna kolumna wypełnienia to praca w toku (nie „Stoi na 0 %”), a niepełny tor to nie
        // koniec pracy (nie „stoi na 100 %”) — model może wyliczyć 0 albo 100, więc wynik jest przycinany do 1..99.
        ProgressBarSource source = Source580 with { ColumnsPerPercent = perPercent, ColumnsOffset = offset };

        ProgressBarReading reading = ReadScreen(Screen(source, columns), source);

        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(columns, reading.FilledColumns);
        Assert.Equal(expected, reading.Percent);
    }

    [Fact]
    public void ModelHmi580_OdwrotnoscOddajeKazdyProcentOd1Do100()
    {
        for (int p = 1; p <= 100; p++)
        {
            int columns = Math.Min(350, (int)Math.Floor((3.54 * p) - 2));
            Assert.Equal(p, ProgressBarReader.PercentFromColumns(columns, 350, 3.54, 2.5));
        }

        Assert.Equal(0, ProgressBarReader.PercentFromColumns(0, 350, 3.54, 2.5));
    }

    [Fact]
    public void ModelDomyslny_ColumnsPerPercentZero_ToSzerokoscToruPrzezSto()
    {
        // Dlaczego: pasek „liniowy” innego HMI ma działać bez dopasowywania modelu — 0 = szerokość / 100.
        var source = new ProgressBarSource
        {
            TrackRoi = new RectInt(100, 100, 200, 12),
            Fill = Source580.Fill,
        };

        Assert.Equal(0, ReadScreen(Screen(source, 0), source).Percent);
        Assert.Equal(1, ReadScreen(Screen(source, 1), source).Percent);
        Assert.Equal(50, ReadScreen(Screen(source, 100), source).Percent);
        Assert.Equal(99, ReadScreen(Screen(source, 199), source).Percent);
        Assert.Equal(100, ReadScreen(Screen(source, 200), source).Percent);
    }

    [Fact]
    public void WypelnienieZPoziomymGradientemBarwy_LiczySieNaCalejDlugosci()
    {
        ProgressBarReading reading = ReadScreen(Screen(Source580, 340, x => GradientFill(x, 350)), Source580);

        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(340, reading.FilledColumns);
        Assert.Equal(97, reading.Percent);

        // Kontrola, że test coś pokazuje: stare okno 180–210° na tym samym torze widzi wyraźnie mniej kolumn.
        var oldWindow = new HsvRange { HueMinDeg = 180, HueMaxDeg = 210, MinSaturation = 0.5, MinValue = 0.4 };
        RgbaImage track = Screen(Source580, 340, x => GradientFill(x, 350)).Crop(Source580.TrackRoi);
        Assert.True(ProgressBarDetector.FilledFraction(track, oldWindow) * 350 < 300);
    }

    [Fact]
    public void ZaokraglonyKoniecToru_JednaKolumnaZSzesciomaPikselami_LiczySieJakoWypelniona()
    {
        ProgressBarReading reading = ReadScreen(Screen(Source580, 1), Source580);
        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(1, reading.FilledColumns);
        Assert.Equal(1, reading.Percent);

        ProgressBarSource tooStrict = Source580 with { ColumnFillMinFraction = 0.7 };
        ProgressBarReading strict = ReadScreen(Screen(tooStrict, 1), tooStrict);
        Assert.NotEqual(1, strict.Percent);
        Assert.NotEqual(0, strict.Percent);
    }

    [Fact]
    public void BezWierszyOtoczenia_CzytaSamTor()
    {
        // Dlaczego: surroundRows = 0 to dozwolona konfiguracja (inny układ HMI) — wtedy wycinek to sam tor.
        ProgressBarSource source = Source580 with { SurroundRows = 0 };

        ProgressBarReading reading = ReadScreen(Screen(source, 86), source);

        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(25, reading.Percent);
    }

    // --- Tor niewidoczny: „nie wiem”, nigdy 0 % --------------------------------------------------------------

    [Fact]
    public void DziuraWWypelnieniu_TorNiewidoczny()
    {
        // Dlaczego: pasek postępu nie ma dziur — wypełnienie z przerwą to coś innego (okno, grafika, inny ekran).
        RgbaImage screen = Screen(Source580, 150);
        for (int y = 277; y < 287; y++)
        {
            for (int x = 522; x < 532; x++)
            {
                screen.SetPixel(x, y, TrackBackground(y - 277, 10));
            }
        }

        ProgressBarReading reading = ReadScreen(screen, Source580);

        AssertNotVisible(reading);
        Assert.Contains("ciągłe od lewej", reading.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("okno-jasne")]
    [InlineData("okno-ciemne-na-torze")]
    [InlineData("kursor")]
    [InlineData("kursor-w-torze-na-tle")]
    [InlineData("kursor-w-torze-na-wypelnieniu")]
    [InlineData("jasne-otoczenie")]
    [InlineData("czerwony-tor")]
    [InlineData("czarna-klatka")]
    [InlineData("przezroczysty-tor")]
    public void ZaklocenieNaTorzeAlboObok_TorNiewidoczny(string kind)
    {
        // Dlaczego: każda z tych sytuacji zdarza się na prawdziwym HMI (okno dialogowe, kursor operatora,
        // inna zakładka, zgaszony ekran) — liczenie kolumn dałoby wtedy dowolną liczbę, więc odczyt ma być „nie wiem”.
        RgbaImage screen = Screen(Source580, 86);
        switch (kind)
        {
            case "okno-jasne":
                FillRect(screen, new RectInt(560, 200, 200, 120), new Rgba(236, 236, 236));
                break;
            case "okno-ciemne-na-torze":
                FillRect(screen, new RectInt(560, 277, 120, 10), new Rgba(30, 30, 60));
                break;
            case "kursor":
                DrawCursor(screen, 600, 270, 16);
                break;
            case "kursor-w-torze-na-tle":
                DrawCursor(screen, 600, 277, 10);
                break;
            case "kursor-w-torze-na-wypelnieniu":
                DrawCursor(screen, 450, 277, 10);
                break;
            case "jasne-otoczenie":
                FillRect(screen, new RectInt(422, 272, 350, 5), new Rgba(200, 200, 200));
                break;
            case "czerwony-tor":
                FillRect(screen, Source580.TrackRoi, new Rgba(220, 30, 30));
                break;
            case "czarna-klatka":
                screen.Fill(new Rgba(0, 0, 0));
                break;
            case "przezroczysty-tor":
                FillRect(screen, Source580.TrackRoi, new Rgba(200, 200, 200, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        AssertNotVisible(ReadScreen(screen, Source580));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    public void PrzesuniecieObrazuOJedenPiksel_TorNiewidoczny_TakzePrzyPustymTorze(int dx, int dy)
    {
        // Dlaczego: gdy obraz HMI przesunie się choćby o piksel (inny układ, inna rozdzielczość), w wycinek
        // wchodzi obwódka. Przy PUSTYM torze przesunięcie w poziomie nie zmienia liczby kolumn (0) — bez
        // sprawdzania końców toru dałoby „widoczne” 0 %, czyli fałszywe „Stoi na 0 %”.
        foreach (int filled in new[] { 0, 86 })
        {
            RgbaImage screen = Screen(Source580, filled);
            RectInt shifted = ProgressBarReader.RequiredArea(Source580) with
            {
                X = ProgressBarReader.RequiredArea(Source580).X + dx,
                Y = ProgressBarReader.RequiredArea(Source580).Y + dy,
            };

            AssertNotVisible(ProgressBarReader.Read(screen.Crop(shifted), Source580));
        }
    }

    [Fact]
    public void WycinekOZlychWymiarach_TorNiewidocznyZOpisem()
    {
        // Dlaczego: gdy tor leży przy krawędzi ekranu, przycięty wycinek jest mniejszy niż obszar paska —
        // wtedy nie wiemy, co jest poza nim, więc nie zgadujemy.
        RgbaImage screen = Screen(Source580, 86);
        RgbaImage cropped = screen.Crop(new RectInt(422, 272, 350, 19));

        ProgressBarReading reading = ProgressBarReader.Read(cropped, Source580);

        AssertNotVisible(reading);
        Assert.Contains("350×19", reading.Description, StringComparison.Ordinal);
        Assert.Contains("350×20", reading.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ObrazZerowy_TorNiewidoczny()
    {
        AssertNotVisible(ProgressBarReader.Read(new RgbaImage(0, 0), Source580));
    }

    [Fact]
    public void WylaczoneZrodlo_NieCzytaPaska()
    {
        // Dlaczego: "enabled": false ma działać jak brak sekcji — procent wyłącznie z OCR.
        ProgressBarSource off = Source580 with { Enabled = false };

        ProgressBarReading reading = ReadScreen(Screen(off, 86), off);

        AssertNotVisible(reading);
        Assert.Contains("wyłączony", reading.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void JasneOtoczenie_OpisMowiKtoryWierszIIle()
    {
        // Dlaczego: opis trafia do diagnostyki — ma mówić, KTÓRY warunek zawiódł i z jaką liczbą.
        RgbaImage screen = Screen(Source580, 86);
        FillRect(screen, new RectInt(425, 272, 35, 1), new Rgba(200, 200, 200));

        ProgressBarReading reading = ReadScreen(screen, Source580);

        AssertNotVisible(reading);
        // 35 z 344 kolumn rdzenia w jednym z 5 wierszy: 1 − 35/1720 = 97,96 % → obcięte do 97,9 %.
        Assert.Equal("pasek: tor niewidoczny (tło nad torem 97,9 % zamiast ≥ 98 %)", reading.Description);
    }

    /// <summary>
    /// Strzałka kursora: czarny kontur i białe wnętrze, <paramref name="height"/> wierszy od (<paramref name="x0"/>,
    /// <paramref name="y0"/>). Biel ma nasycenie 0 i jasność 1, więc sama w sobie przechodzi za „tło toru” —
    /// kursor łapie warunek czystych kolumn, a nie warunek barwy.
    /// </summary>
    private static void DrawCursor(RgbaImage img, int x0, int y0, int height)
    {
        var black = new Rgba(0, 0, 0);
        var white = new Rgba(255, 255, 255);
        for (int r = 0; r < height; r++)
        {
            int w = (int)(r * 0.6);
            for (int c = 0; c <= w; c++)
            {
                bool outline = c == 0 || c == w || r == height - 1;
                img.SetPixel(x0 + c, y0 + r, outline ? black : white);
            }
        }
    }
}
