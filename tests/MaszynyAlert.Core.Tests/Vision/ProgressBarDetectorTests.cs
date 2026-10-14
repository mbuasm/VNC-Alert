using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Vision;

/// <summary>
/// Detekcja „maszyna stoi bez pracy – dołóż prace”: wypełnienie paska postępu liczone KOLUMNAMI
/// (<see cref="ProgressBarDetector.FilledFraction"/>), próg „100 %” (<c>IdleWatchProfile.FullAtFraction</c>)
/// oraz dwa pomocnicze mechanizmy, na których ta funkcja stoi: dopasowanie koloru
/// (<see cref="ColorMath.InRange"/>) i suma prostokątów ROI (<see cref="RectInt.Union"/>).
/// </summary>
public sealed class ProgressBarDetectorTests
{
    /// <summary>Profil obserwacji postoju z przykładowej konfiguracji (fullAtFraction = 0,97; zieleń 80°..160°).</summary>
    private static readonly IdleWatchProfile Idle = TestPaths.Profile("opencnc").Idle!;

    /// <summary>Kolor wypełnienia paska postępu — zieleń mieszcząca się w domyślnym <see cref="HsvRange"/> (H≈127°).</summary>
    private static readonly Rgba Green = new(40, 200, 60);

    /// <summary>Tło paska (jasny szary) — poza zakresem koloru wypełnienia (nasycenie bliskie zeru).</summary>
    private static readonly Rgba Background = new(215, 215, 215);

    /// <summary>
    /// Rysuje pasek postępu <paramref name="width"/>×<paramref name="height"/> wypełniony zielenią
    /// w <paramref name="fillFraction"/> (liczone od lewej, kolumnami) na jasnym tle.
    /// </summary>
    private static RgbaImage Bar(int width, int height, double fillFraction, Rgba? fill = null)
    {
        var img = new RgbaImage(width, height);
        img.Fill(Background);

        int filled = (int)Math.Round(fillFraction * width);
        for (int x = 0; x < filled; x++)
        {
            for (int y = 0; y < height; y++)
            {
                img.SetPixel(x, y, fill ?? Green);
            }
        }

        return img;
    }

    [Fact]
    public void FilledFraction_PaskaWypelnionegoWPolowie_ZwracaOkoloPolowy()
    {
        // Dlaczego: podstawowa umowa detektora — udział WYPEŁNIONYCH KOLUMN odpowiada postępowi pracy
        // pokazywanemu przez HMI (pasek w 50 % = praca w połowie, więc na pewno nie „postój na 100 %”).
        RgbaImage bar = Bar(200, 20, 0.5);

        double fraction = ProgressBarDetector.FilledFraction(bar, Idle.ProgressFill);

        Assert.Equal(0.5, fraction, 3);
    }

    [Fact]
    public void FilledFraction_GdyNaPaskuJestNadrukowanyTekst_NadalWidziPelneWypelnienie()
    {
        // Dlaczego: HMI potrafi nadrukować na pasku napis („100%”), który zjada część pikseli kolumny.
        // Liczymy kolumnami z progiem 30 % (ColumnFillMinFraction), więc kolumna z tekstem, ale nadal
        // z widocznym wypełnieniem, liczy się jako wypełniona — inaczej pełny pasek nigdy nie dałby 100 %.
        RgbaImage bar = Bar(200, 20, 1.0);
        for (int x = 80; x < 140; x++)
        {
            // 12 z 20 wierszy zamalowanych „tekstem” — zostaje 8/20 = 40 % zieleni, czyli powyżej progu 30 %.
            for (int y = 0; y < 12; y++)
            {
                bar.SetPixel(x, y, SyntheticScreens.WhiteText);
            }
        }

        double fraction = ProgressBarDetector.FilledFraction(bar, Idle.ProgressFill);

        Assert.Equal(1.0, fraction, 3);
    }

    [Fact]
    public void FilledFraction_GdyTekstZakrywaPrawieCalaKolumne_TakaKolumnaNieLiczySie()
    {
        // Dlaczego: próg 30 % ma też działać w drugą stronę — kolumna, w której zieleni praktycznie nie
        // widać (16 z 20 wierszy zamalowanych, zostaje 20 %), nie może udawać wypełnionej.
        RgbaImage bar = Bar(200, 20, 1.0);
        for (int x = 100; x < 200; x++)
        {
            for (int y = 0; y < 16; y++)
            {
                bar.SetPixel(x, y, SyntheticScreens.WhiteText);
            }
        }

        double fraction = ProgressBarDetector.FilledFraction(bar, Idle.ProgressFill);

        Assert.Equal(0.5, fraction, 3);
    }

    [Fact]
    public void FilledFraction_KoloruSpozaZakresuHsv_ZwracaZero()
    {
        // Dlaczego: pasek w innym kolorze niż skonfigurowany (albo źle ustawione ROI, np. na czerwonym
        // komunikacie błędu) nie może być mylnie odczytany jako „praca skończona w 100 %”.
        RgbaImage bar = Bar(200, 20, 1.0, SyntheticScreens.RedText);

        double fraction = ProgressBarDetector.FilledFraction(bar, Idle.ProgressFill);

        Assert.Equal(0.0, fraction);
    }

    [Fact]
    public void FilledFraction_ObrazuOZerowymRozmiarze_ZwracaZeroZamiastDzielicPrzezZero()
    {
        // Dlaczego: ROI całkowicie poza ekranem daje po Clamp obraz 0×0 — detektor musi to przeżyć
        // (zero, brak alertu), a nie rzucić wyjątkiem w pętli obserwatora.
        double fraction = ProgressBarDetector.FilledFraction(new RgbaImage(0, 0), Idle.ProgressFill);

        Assert.Equal(0.0, fraction);
    }

    [Fact]
    public void Analyze_ObrazuPustego_ZwracaZeroINieUznajeZaPelny()
    {
        // Dlaczego: zachowawczo — brak obrazu to brak wiedzy, a nie „maszyna stoi”.
        ProgressDetectionResult result = new ProgressBarDetector(Idle).Analyze(new RgbaImage(0, 0));

        Assert.Equal(0.0, result.FilledFraction);
        Assert.False(result.IsFull);
    }

    [Fact]
    public void Analyze_PaskaWypelnionegoWCalosci_UznajeZaPelny()
    {
        // Dlaczego: to jest sygnał, na którym opiera się cały alert postoju (100 % przez 2 minuty).
        ProgressDetectionResult result = new ProgressBarDetector(Idle).Analyze(Bar(400, 24, 1.0));

        Assert.Equal(1.0, result.FilledFraction, 3);
        Assert.True(result.IsFull);
    }

    [Fact]
    public void Analyze_PaskaWypelnionegoW95Procentach_NieUznajeZaPelny()
    {
        // Dlaczego: próg fullAtFraction = 0,97 — praca prawie skończona (95 %) to wciąż PRACA,
        // nie postój; fałszywy alert w tym miejscu podrywałby operatora przy każdej pracy.
        ProgressDetectionResult result = new ProgressBarDetector(Idle).Analyze(Bar(400, 24, 0.95));

        Assert.Equal(0.95, result.FilledFraction, 3);
        Assert.False(result.IsFull);
    }

    [Fact]
    public void Analyze_PikseliPrzezroczystych_NieLiczyDoWypelnienia()
    {
        // Dlaczego: obraz z kanałem alfa (np. nieodmalowany fragment ramki VNC) nie może udawać
        // wypełnionego paska — ColorMath odrzuca piksele z A < 128, a detektor musi to respektować.
        RgbaImage bar = Bar(200, 20, 1.0, new Rgba(40, 200, 60, 0));

        ProgressDetectionResult result = new ProgressBarDetector(Idle).Analyze(bar);

        Assert.Equal(0.0, result.FilledFraction);
        Assert.False(result.IsFull);
    }

    [Fact]
    public void InRange_ZakresuPrzezZero_ObejmujeCzerwienZObuStronKolaBarw()
    {
        // Dlaczego: HsvRange musi działać tak samo jak progi czerwieni (340°..20° „przez zero”) —
        // z tego samego rdzenia korzysta wykrywanie błędu i wykrywanie koloru paska postępu.
        var przezZero = new HsvRange { HueMinDeg = 340, HueMaxDeg = 20, MinSaturation = 0.55, MinValue = 0.35 };

        Assert.True(ColorMath.InRange(new Rgba(220, 30, 30), przezZero));   // H ≈ 0°
        Assert.True(ColorMath.InRange(new Rgba(220, 30, 62), przezZero));   // H ≈ 350° (druga strona zera)
        Assert.False(ColorMath.InRange(Green, przezZero));                  // zieleń H ≈ 127°
    }

    [Fact]
    public void InRange_ZwyklegoZakresu_ObejmujeTylkoKoloryWewnatrzIDostatecznieNasycone()
    {
        // Dlaczego: domyślny zakres wypełnienia paska (zieleń 80°..160°) ma przepuszczać zieleń,
        // a odrzucać czerwień błędu oraz wyblakłe/ciemne tło, które nie jest paskiem postępu.
        HsvRange zielen = Idle.ProgressFill;

        Assert.True(ColorMath.InRange(Green, zielen));
        Assert.False(ColorMath.InRange(SyntheticScreens.RedText, zielen));
        Assert.False(ColorMath.InRange(new Rgba(200, 210, 202), zielen));  // zbyt małe nasycenie (szarość)
        Assert.False(ColorMath.InRange(new Rgba(6, 30, 9), zielen));       // zbyt ciemna (V poniżej progu)
    }

    [Fact]
    public void Union_ZPustymProstokatem_ZwracaTenDrugiBezZmian()
    {
        // Dlaczego: obszar pobierany z VNC to StatusBarRoi ∪ ProgressRoi — pusty (np. wyłączona
        // obserwacja postoju) nie może rozciągnąć sumy aż do punktu (0,0).
        var status = new RectInt(0, 700, 1024, 40);

        Assert.Equal(status, status.Union(RectInt.Empty));
        Assert.Equal(status, RectInt.Empty.Union(status));
        Assert.Equal(RectInt.Empty, RectInt.Empty.Union(RectInt.Empty));
    }

    [Fact]
    public void Union_RozlacznychProstokatow_ZwracaNajmniejszyObejmujacyOba()
    {
        // Dlaczego: pasek statusu i pasek postępu leżą w różnych miejscach ekranu — jedno pobranie
        // musi obejmować oba (sprawdzamy dokładne krawędzie sumy).
        var status = new RectInt(0, 0, 1024, 40);
        var progress = new RectInt(60, 380, 400, 24);

        RectInt suma = status.Union(progress);

        Assert.Equal(new RectInt(0, 0, 1024, 404), suma);
        Assert.Equal(suma, progress.Union(status));
    }
}
