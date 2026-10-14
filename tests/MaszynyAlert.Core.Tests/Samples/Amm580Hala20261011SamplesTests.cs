using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class Amm580Hala20261011SamplesTests
{
    private const string Dir = "amm580-2026-10-11";
    private const string NormalStopFrame = "580-2026-10-11-191057-normal-stop-t8-tool-life-0.png";
    private const string ToastFrame = "580-2026-10-11-191102-normal-stop-dymek-realvnc.png";

    private static readonly RectInt RealVncToasts = new(480, 463, 320, 137);

    private static ScreenProfile Profile => TestPaths.Profile("amm580");

    public static TheoryData<string> Frames() => new() { NormalStopFrame, ToastFrame };

    [Fact]
    public void Manifest_HasheKlatekIOdczytuOcrSieZgadzaja()
    {
        Manifest manifest = LoadManifest();
        Assert.Equal(2, manifest.Samples.Count);
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(TestPaths.SamplesOutput, Dir))
            .Count(f => f.EndsWith(".png", StringComparison.Ordinal) || f.EndsWith("-ocr-en-US.json", StringComparison.Ordinal)));

        foreach (Entry entry in manifest.Samples)
        {
            string path = SamplePath(entry.SamplePath);
            Assert.True(File.Exists(path), $"Brak próbki '{path}'.");
            Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        // Plik tekstowy: git może go zapisać z CRLF (core.autocrlf) — hash liczony po sprowadzeniu końców linii do LF.
        string ocrPath = SamplePath(manifest.Ocr.SamplePath);
        Assert.True(File.Exists(ocrPath), $"Brak odczytu OCR '{ocrPath}'.");
        byte[] lf = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(ocrPath).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(manifest.Ocr.Sha256, Convert.ToHexString(SHA256.HashData(lf)));
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void PasekStanu_ToolLifeT8_JestCzerwieniaWZmierzonymObszarze(string file)
    {
        Assert.Equal(new RectInt(150, 0, 460, 32), Profile.StatusBarRoi);
        RgbaImage frame = Decode(file);

        RedDetectionResult red = new RedTextDetector(Profile).Analyze(frame.Crop(Profile.StatusBarRoi));

        Assert.True(red.IsRed);
        Assert.InRange(red.RedFraction, 0.74, 0.77);
    }

    [Fact]
    public void OdczytyPaskaZHali_TrafiajaWReguleToolLifeBezAlarmu()
    {
        var mapper = new MessageMapper(TestPaths.ExampleRules);
        List<string> texts = LoadManifest().StatusBarOcrFromVncAlert;
        Assert.Equal(5, texts.Count);

        foreach (string text in texts)
        {
            MappedMessage m = mapper.Map(text, "amm580");
            Assert.True(m.Matched, $"'{text}' nie trafił w żadną regułę bazy amm580.");
            Assert.Equal("amm580-tool-life", m.RuleId);
            Assert.True(m.Suppressed, $"'{text}' nie jest wyciszony.");
            Assert.Equal("bez alarmu", m.Category);
        }
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void PasekPostepu_PustyTor_Daje0Procent(string file)
    {
        ProgressBarSource bar = Profile.Idle!.ProgressBar!;
        Assert.Equal(new RectInt(422, 277, 350, 10), bar.TrackRoi);
        RgbaImage frame = Decode(file);

        ProgressBarReading reading = ProgressBarReader.Read(frame.Crop(ProgressBarReader.RequiredArea(bar)), bar);

        Assert.True(reading.Visible, reading.Description);
        Assert.Equal(0, reading.FilledColumns);
        Assert.Equal(350, reading.TrackWidth);
        Assert.Equal(0, reading.Percent);
    }

    /// <summary>
    /// „IDLE: Normal Stop” na zakładce Auto: nie RUN (więc 0 % liczy się jako postój) i nie „IDLE: Abort” —
    /// także z wymuszonym brakiem rozpoznania zieleni (wzorzec Abort musi mieć duży zapas na tym napisie).
    /// </summary>
    [Theory]
    [MemberData(nameof(Frames))]
    public void PudelkoStanu_NormalStopNaZakladceAuto_NieRunINieAbort(string file)
    {
        ProgramStateProfile state = Profile.ProgramState!;
        RgbaImage frame = Decode(file);

        ProgramStateReading reading = ProgramStateDetector.AnalyzeFrame(frame, state);
        Assert.True(reading.Known, reading.Description);
        Assert.False(reading.Running, reading.Description);
        Assert.Equal(0.0, reading.RunFraction);
        Assert.False(reading.AbortPattern, reading.Description);

        ProgramStateReading forced = ProgramStateDetector.AnalyzeFrame(frame, state with { RunMinFraction = 1.0 });
        Assert.False(forced.AbortPattern, forced.Description);
        Assert.True(forced.AbortMismatch > 5 * state.AbortPattern!.MaxMismatch,
            $"Napis „IDLE: Normal Stop” zbliżył się do wzorca Abort: {forced.AbortMismatch} niezgodnych komórek.");
    }

    /// <summary>
    /// Kotwice ekranu głównego na PRAWDZIWYCH słowach Windows OCR (z prostokątami) — także przy czerwonym pasku;
    /// napis okna „Alarm Information” (inny ekran) na tej klatce nie występuje.
    /// </summary>
    [Fact]
    public void KotwiceEkranuGlownego_PasujaDoPrawdziwychSlowOcrZProstokatami()
    {
        MainScreenWatchProfile main = Profile.MainScreen!;
        OcrResult ocr = LoadOcr();
        Assert.Contains(ocr.Words, w => w.Text == "Progress:");

        AnchoredField? anchor = LabelAnchorLocator.FindAny(ocr, main.EffectiveAnchorLabels, 800, 600, main.MaxDistance);
        Assert.NotNull(anchor);
        Assert.Null(LabelAnchorLocator.FindAny(ocr, main.OtherScreenLabels, 800, 600, main.MaxDistance));

        // Słowa stanu są w pudełku stanu programu, a słowo „Abort” na klatce nie pada.
        Assert.Contains(ocr.Words, w => w.Text == "Normal" && Profile.ProgramState!.BoxRoi.Intersect(w.Bounds) == w.Bounds);
        Assert.DoesNotContain(ocr.Words, w => w.Text.Contains("Abort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DymkiRealVnc_LezaPozaObszaramiProfilu()
    {
        ScreenProfile p = Profile;
        Assert.True(p.StatusBarRoi.Intersect(RealVncToasts).IsEmpty);
        Assert.True(ProgressBarReader.RequiredArea(p.Idle!.ProgressBar!).Intersect(RealVncToasts).IsEmpty);
        Assert.True(p.Idle!.ProgressRoi.Intersect(RealVncToasts).IsEmpty);
        Assert.True(p.ProgramState!.BoxRoi.Intersect(RealVncToasts).IsEmpty);

        // Dymek naprawdę jest na drugiej klatce i nie ma go na pierwszej (jasne tło dymku w jego obszarze).
        Assert.True(MeanValue(Decode(ToastFrame).Crop(RealVncToasts)) > MeanValue(Decode(NormalStopFrame).Crop(RealVncToasts)) + 0.3);
    }

    private static double MeanValue(RgbaImage image)
    {
        double sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba px = image.GetPixel(x, y);
                sum += Math.Max(px.R, Math.Max(px.G, px.B)) / 255.0;
            }
        }

        return sum / (image.Width * image.Height);
    }

    private static string SamplePath(string file) => Path.Combine(TestPaths.SamplesOutput, Dir, file);

    private static RgbaImage Decode(string file)
    {
        string path = SamplePath(file);
        Assert.True(File.Exists(path), $"Brak próbki '{path}'.");
        RgbaImage frame = PngCodec.DecodeFile(path);
        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);
        return frame;
    }

    private static OcrResult LoadOcr()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SamplePath(LoadManifest().Ocr.SamplePath)));
        var words = new List<OcrWord>();
        foreach (JsonElement w in doc.RootElement.GetProperty("slowa").EnumerateArray())
        {
            words.Add(new OcrWord(
                w.GetProperty("tekst").GetString()!,
                new RectInt(w.GetProperty("x").GetInt32(), w.GetProperty("y").GetInt32(),
                    w.GetProperty("w").GetInt32(), w.GetProperty("h").GetInt32()),
                w.GetProperty("linia").GetInt32()));
        }

        Assert.True(words.Count > 50, $"Odczyt OCR ma tylko {words.Count} słów.");
        return OcrResult.FromWords(words);
    }

    private static Manifest LoadManifest() =>
        JsonSerializer.Deserialize<Manifest>(File.ReadAllText(SamplePath("manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private sealed record Manifest
    {
        [JsonPropertyName("samples")] public List<Entry> Samples { get; init; } = new();
        [JsonPropertyName("ocr")] public Entry Ocr { get; init; } = new();
        [JsonPropertyName("statusBarOcrFromVncAlert")] public List<string> StatusBarOcrFromVncAlert { get; init; } = new();
    }

    private sealed record Entry
    {
        [JsonPropertyName("samplePath")] public string SamplePath { get; init; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    }
}
