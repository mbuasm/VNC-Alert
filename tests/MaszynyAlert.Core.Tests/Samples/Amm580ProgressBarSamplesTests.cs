using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class Amm580ProgressBarSamplesTests
{
    private static readonly IReadOnlyDictionary<string, int> ProductionColumns = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["alarm-t4-tool-life.png"] = 75,
        ["auto-bez-alarmu-0-procent.png"] = 0,
        ["auto-praca-run-normal-22-procent.png"] = 75,
        ["auto-praca-run-continue-0-procent.png"] = 0,
        ["auto-praca-21-procent-zrzut-okna.png"] = 72,
        ["auto-praca-10-procent-2026-09-25.png"] = 33,
    };

    private static ProgressBarSource Bar => TestPaths.Profile("amm580").Idle!.ProgressBar!;

    private static string SeriesPath(string file) => Path.Combine(TestPaths.SamplesOutput, "ocr-580-postep", file);

    private static string ProductionPath(string file) => Path.Combine(TestPaths.SamplesOutput, "production-amm580", file);

    private static List<(string File, int Percent, int Columns)> LoadSeries()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SeriesPath("manifest.json")));
        var frames = new List<(string, int, int)>();
        foreach (JsonElement frame in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            frames.Add((frame.GetProperty("file").GetString()!,
                frame.GetProperty("progressPercentOnScreen").GetInt32(),
                frame.GetProperty("barColumns").GetInt32()));
        }

        return frames;
    }

    private static List<(string File, int? Percent)> LoadProduction()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ProductionPath("manifest.json")));
        var samples = new List<(string, int?)>();
        foreach (JsonElement sample in doc.RootElement.GetProperty("samples").EnumerateArray())
        {
            JsonElement percent = sample.GetProperty("progressPercentOnScreen");
            samples.Add((sample.GetProperty("samplePath").GetString()!,
                percent.ValueKind == JsonValueKind.Null ? null : percent.GetInt32()));
        }

        return samples;
    }

    public static TheoryData<string, int, int> SeriesFrames()
    {
        var data = new TheoryData<string, int, int>();
        foreach ((string file, int percent, int columns) in LoadSeries())
        {
            data.Add(file, percent, columns);
        }

        return data;
    }

    public static TheoryData<string> ProductionFrames()
    {
        var data = new TheoryData<string>();
        foreach ((string file, _) in LoadProduction())
        {
            data.Add(file);
        }

        return data;
    }

    private static ProgressBarReading ReadFrame(RgbaImage frame) =>
        ProgressBarReader.Read(frame.Crop(ProgressBarReader.RequiredArea(Bar)), Bar);

    private static RgbaImage Decode(string path)
    {
        Assert.True(File.Exists(path), $"Brak próbki '{path}'.");
        RgbaImage frame = PngCodec.DecodeFile(path);
        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);
        return frame;
    }

    [Fact]
    public void ProfilAmm580_MaPasekZWartosciamiZmierzonymi_IResztaProfiluBezZmian()
    {
        ScreenProfile profile = TestPaths.Profile("amm580");
        IdleWatchProfile idle = profile.Idle!;
        ProgressBarSource bar = Assert.IsType<ProgressBarSource>(idle.ProgressBar);

        Assert.Equal(ProgressMode.Number, idle.Mode);
        Assert.True(bar.Enabled);
        Assert.Equal(new RectInt(422, 277, 350, 10), bar.TrackRoi);
        Assert.Equal(new HsvRange { HueMinDeg = 140, HueMaxDeg = 215, MinSaturation = 0.55, MinValue = 0.35 }, bar.Fill);
        Assert.Equal(0.10, bar.TrackMaxSaturation);
        Assert.Equal(0.40, bar.TrackMinValue);
        Assert.Equal(0.35, bar.SurroundMaxValue);
        Assert.Equal(5, bar.SurroundRows);
        Assert.Equal(0.5, bar.ColumnFillMinFraction);
        Assert.Equal(3.54, bar.ColumnsPerPercent);
        Assert.Equal(2.5, bar.ColumnsOffset);

        Assert.Equal(new RectInt(420, 246, 370, 30), idle.ProgressRoi);
        Assert.Equal(new RectInt(150, 0, 460, 32), profile.StatusBarRoi);

        foreach ((string id, ScreenProfile other) in TestPaths.ExampleConfig.Profiles.Where(p => p.Key != "amm580"))
        {
            Assert.True(other.Idle?.ProgressBar is null, $"Profil '{id}' nie powinien mieć sekcji 'idle.progressBar'.");
        }
    }

    [Fact]
    public void SeriaKlatek_ManifestMaWszystkieKlatkiZLiczbaIKolumnami()
    {
        // Dlaczego: Theory niżej bierze klatki z manifestu — pusty albo okrojony manifest dałby zielony test
        // bez sprawdzenia czegokolwiek.
        List<(string File, int Percent, int Columns)> frames = LoadSeries();

        Assert.Equal(17, frames.Count);
        Assert.Equal(frames.Count, frames.Select(f => f.File).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(frames, f => f.Percent == 0);
        Assert.Contains(frames, f => f.Percent == 1);
        Assert.Contains(frames, f => f.Percent == 41);
        Assert.Equal(frames.Count, Directory.EnumerateFiles(Path.Combine(TestPaths.SamplesOutput, "ocr-580-postep"), "*.png").Count());
    }

    [Theory]
    [MemberData(nameof(SeriesFrames))]
    public void KlatkaSerii_PasekDajeLiczbeZHmiIZmierzoneKolumny(string file, int percentOnScreen, int barColumns)
    {
        ProgressBarReading reading = ReadFrame(Decode(SeriesPath(file)));

        Assert.True(reading.Visible, $"{file}: {reading.Description}");
        Assert.Equal(barColumns, reading.FilledColumns);
        Assert.Equal(350, reading.TrackWidth);
        Assert.Equal(percentOnScreen, reading.Percent);
        Assert.Equal($"pasek {barColumns}/350 kol. → {percentOnScreen} %", reading.Description);
    }

    [Theory]
    [MemberData(nameof(ProductionFrames))]
    public void KlatkaProdukcyjna_PasekDajeLiczbeZHmi_AZakladkaManualToNieWiem(string file)
    {
        int? percentOnScreen = LoadProduction().Single(s => s.File == file).Percent;

        ProgressBarReading reading = ReadFrame(Decode(ProductionPath(file)));

        if (percentOnScreen is null)
        {
            Assert.False(reading.Visible, reading.Description);
            Assert.Null(reading.Percent);
            Assert.Equal(0, reading.FilledColumns);
            Assert.StartsWith("pasek: tor niewidoczny (tło nad torem", reading.Description, StringComparison.Ordinal);
            return;
        }

        Assert.True(ProductionColumns.TryGetValue(file, out int columns),
            $"Klatka '{file}' nie ma zmierzonej liczby kolumn paska — zmierz ją i dopisz do ProductionColumns.");
        Assert.True(reading.Visible, $"{file}: {reading.Description}");
        Assert.Equal(columns, reading.FilledColumns);
        Assert.Equal(percentOnScreen, reading.Percent);
    }

    [Fact]
    public void ObszarPaska_NieObejmujeLiczbyProcentu_ChociazDotykaDolnychWierszyPolaOcr()
    {
        // Dlaczego: liczba „NN%” nad torem jest NIEBIESKA (~208°), w tej samej rodzinie barw co wypełnienie,
        // a stoi w wierszach y 251–268. Obszar paska (tor + 5 wierszy otoczenia) zaczyna się na y 272, więc
        // z polem OCR (progressRoi 420,246,370,30, do y 275) dzieli tylko 4 wiersze pustego panelu pod tekstem.
        // Na prawdziwych klatkach sprawdzamy, że w otoczeniu toru nie ma ANI JEDNEGO kolorowego piksela,
        // a w wierszu liczby jest (kontrola, że pomiar ma sens).
        RectInt required = ProgressBarReader.RequiredArea(Bar);
        RectInt progressRoi = TestPaths.Profile("amm580").Idle!.ProgressRoi;

        Assert.Equal(new RectInt(422, 272, 350, 20), required);
        Assert.True(required.Y > 268, $"Obszar paska zaczyna się na y {required.Y} — wchodzi w wiersz liczby (y 251–268).");
        Assert.Equal(new RectInt(422, 272, 350, 4), progressRoi.Intersect(required));

        var blue = new HsvRange { HueMinDeg = 140, HueMaxDeg = 250, MinSaturation = 0.3, MinValue = 0.3 };
        var surroundAbove = new RectInt(required.X, required.Y, required.Width, Bar.TrackRoi.Y - required.Y);
        var surroundBelow = new RectInt(required.X, Bar.TrackRoi.Bottom, required.Width, required.Bottom - Bar.TrackRoi.Bottom);
        var numberRow = new RectInt(700, 251, 90, 18);

        var frames = LoadSeries().Select(f => SeriesPath(f.File))
            .Concat(LoadProduction().Where(s => s.Percent is not null).Select(s => ProductionPath(s.File)))
            .ToList();
        Assert.True(frames.Count >= 20);
        foreach (string path in frames)
        {
            RgbaImage frame = Decode(path);
            Assert.Equal(0, CountInRange(frame.Crop(surroundAbove), blue));
            Assert.Equal(0, CountInRange(frame.Crop(surroundBelow), blue));
            Assert.True(CountInRange(frame.Crop(numberRow), blue) > 0,
                $"{Path.GetFileName(path)}: w wierszu liczby nie ma niebieskich pikseli — kontrola przestała cokolwiek mierzyć.");
        }
    }

    [Theory]
    [InlineData("580-2026-09-25-105517-run-0.png", 0, 1)]
    [InlineData("580-2026-09-25-105517-run-0.png", 0, -1)]
    [InlineData("580-2026-09-25-105517-run-0.png", 1, 0)]
    [InlineData("580-2026-09-25-105517-run-0.png", -1, 0)]
    [InlineData("580-2026-09-25-110103-run-1.png", 1, 0)]
    [InlineData("580-2026-09-25-110103-run-1.png", -1, 0)]
    [InlineData("580-2026-09-25-114133-run-25.png", 0, 1)]
    [InlineData("580-2026-09-25-114133-run-25.png", 0, -1)]
    [InlineData("580-2026-09-25-114133-run-25.png", 1, 0)]
    [InlineData("580-2026-09-25-114133-run-25.png", -1, 0)]
    public void PrawdziwaKlatkaPrzesunietaOPiksel_TorNiewidoczny(string file, int dx, int dy)
    {
        // Dlaczego: przesunięcie obrazu o piksel (inny układ, inna rozdzielczość) wpuszcza obwódkę do toru.
        // Najgroźniejsze przypadki to pusty tor (0 %) i 1 % przesunięte w poziomie: liczba kolumn nie
        // zmieniłaby się albo spadła do zera — bez sprawdzania zaokrąglonych końców toru wyszłoby „widoczne”
        // 0 %, czyli fałszywe „Stoi na 0 %”.
        RgbaImage frame = Decode(SeriesPath(file));
        RectInt required = ProgressBarReader.RequiredArea(Bar);
        RectInt shifted = required with { X = required.X + dx, Y = required.Y + dy };

        ProgressBarReading reading = ProgressBarReader.Read(frame.Crop(shifted), Bar);

        Assert.False(reading.Visible, reading.Description);
        Assert.Null(reading.Percent);
    }

    [Theory]
    [InlineData("kursor")]
    [InlineData("okno")]
    [InlineData("czerwony-tor")]
    [InlineData("czarna-klatka")]
    public void PrawdziwaKlatkaZZakloceniemNaTorze_TorNiewidoczny(string kind)
    {
        // Dlaczego: kursor operatora albo okno na torze zmieniają liczbę „wypełnionych” kolumn dowolnie —
        // odczyt ma wtedy być „nie wiem”, a procent ma przyjść z OCR (zapas).
        RgbaImage frame = Decode(SeriesPath("580-2026-09-25-114133-run-25.png"));
        switch (kind)
        {
            case "kursor":
                // Strzałka 12×19 z czarnym konturem i białym wnętrzem, czubek na y 272 nad torem przy x 600.
                for (int r = 0; r < 19; r++)
                {
                    int w = Math.Min(11, (int)(r * 0.6));
                    for (int c = 0; c <= w; c++)
                    {
                        bool outline = c == 0 || c == w || r == 18;
                        frame.SetPixel(600 + c, 272 + r, outline ? new Rgba(0, 0, 0) : new Rgba(255, 255, 255));
                    }
                }

                break;
            case "okno":
                Paint(frame, new RectInt(480, 180, 240, 140), new Rgba(240, 240, 240));
                Paint(frame, new RectInt(480, 180, 240, 20), new Rgba(0, 120, 215));
                break;
            case "czerwony-tor":
                Paint(frame, Bar.TrackRoi, new Rgba(255, 0, 0));
                break;
            case "czarna-klatka":
                frame.Fill(new Rgba(0, 0, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        ProgressBarReading reading = ReadFrame(frame);

        Assert.False(reading.Visible, reading.Description);
        Assert.Null(reading.Percent);
    }

    [Fact]
    public void StareOknoBarwy_GubiloWypelnienieOd34Procent_NoweGoLiczy()
    {
        // Dlaczego: przy 41 % prawy koniec wypełnienia ma barwę ~174° (gradient poziomy) — stare okno 180–210°
        // liczyło mniej kolumn niż HMI rysuje. Ten test pilnuje, żeby nikt nie wrócił do starego okna.
        RgbaImage frame = Decode(SeriesPath("580-2026-09-25-120953-run-41.png"));
        var oldWindow = new HsvRange { HueMinDeg = 180, HueMaxDeg = 210, MinSaturation = 0.5, MinValue = 0.4 };

        double oldColumns = ProgressBarDetector.FilledFraction(frame.Crop(Bar.TrackRoi), oldWindow) * Bar.TrackRoi.Width;
        ProgressBarReading reading = ReadFrame(frame);

        Assert.True(oldColumns < 143, $"Stare okno policzyło {oldColumns} kolumn — pomiar przestał opisywać tę klatkę.");
        Assert.Equal(143, reading.FilledColumns);
        Assert.Equal(41, reading.Percent);
    }

    private static void Paint(RgbaImage img, RectInt r, Rgba c)
    {
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                img.SetPixel(x, y, c);
            }
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
}
