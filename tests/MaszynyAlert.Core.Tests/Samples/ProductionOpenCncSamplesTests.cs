using System.Security.Cryptography;
using System.Text.Json;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

/// <summary>
/// Regresje na małym, opisanym zestawie pełnych klatek VNC. Hash pilnuje, że test
/// używa oryginalnych bajtów, a nie obrazu ponownie zapisanego przez edytor.
/// </summary>
public sealed class ProductionOpenCncSamplesTests
{
    private const string Rgb555 = "16bpp depth 15 RGB max(31,31,31) shift(10,5,0) LE";

    [Fact]
    public void ManifestedFramesKeepOriginalBytesGeometryAndExpectedRedState()
    {
        ProductionManifest manifest = LoadManifest();
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("out z produkcji/out", manifest.SourceRoot);
        Assert.Equal(8, manifest.Samples.Count);
        Assert.Equal(8, manifest.Samples.Select(s => s.Sha256).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, manifest.Samples.Count(s => s.PixelFormat == Rgb555));
        Assert.Equal(4, manifest.Samples.Count(s => s.PixelFormat.Contains("32bpp", StringComparison.Ordinal)));

        var profile = TestPaths.Profile("opencnc");
        var detector = new RedTextDetector(profile);

        foreach (ProductionSample sample in manifest.Samples)
        {
            Assert.False(string.IsNullOrWhiteSpace(sample.SourceRelativePath));
            Assert.False(string.IsNullOrWhiteSpace(sample.Machine));
            Assert.False(string.IsNullOrWhiteSpace(sample.ServerName));
            Assert.False(string.IsNullOrWhiteSpace(sample.ExpectedScreen));
            Assert.False(string.IsNullOrWhiteSpace(sample.Evidence));
            string path = ResolveSample(sample.SamplePath);
            Assert.True(File.Exists(path), $"Brak próbki produkcyjnej '{path}'.");
            Assert.Equal(sample.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            RgbaImage frame = PngCodec.DecodeFile(path);
            Assert.Equal(1024, frame.Width);
            Assert.Equal(768, frame.Height);

            RgbaImage status = frame.Crop(profile.StatusBarRoi);
            Assert.Equal(543, status.Width);
            Assert.Equal(66, status.Height);
            RedDetectionResult red = detector.Analyze(status);
            Assert.Equal(sample.ExpectedRed, red.IsRed);
            if (!sample.ExpectedRed)
            {
                Assert.Equal(0, red.RedFraction);
            }

            if (sample.ExpectedScreen == "main")
            {
                RgbaImage progress = frame.Crop(profile.Idle!.ProgressRoi);
                Assert.Equal(190, progress.Width);
                Assert.Equal(32, progress.Height);
                Assert.True(CountRgbColours(progress) > 1,
                    $"Próbka '{sample.Id}' ma pusty/jednolity progressRoi; nie sprawdza rzeczywistej geometrii pola.");
            }
        }
    }

    [Fact]
    public async Task Rgb555MainFramesExerciseRealRoisWithExplicitlyControlledFakeOcr()
    {
        ProductionSample[] samples = LoadManifest().Samples
            .Where(s => s.PixelFormat == Rgb555)
            .ToArray();
        Assert.Equal(
            new[] { "aidite3", "aidite5", "aidite6", "aidite7" },
            samples.Select(s => s.Machine).Order().ToArray());

        var profile = TestPaths.Profile("opencnc");
        foreach (ProductionSample sample in samples)
        {
            Assert.Equal("main", sample.ExpectedScreen);
            Assert.NotNull(sample.ExpectedProgressPercent);
            Assert.False(string.IsNullOrWhiteSpace(sample.ExpectedProgressText));

            RgbaImage frame = PngCodec.DecodeFile(ResolveSample(sample.SamplePath));

            // To celowo kontrolowana atrapa OCR. Klatka, oba ROI i piksele są prawdziwe, lecz ten test
            // nie twierdzi, że Windows OCR odczyta etykietę albo liczbę na bieżącym komputerze.
            var screenOcr = new FakeOcrEngine
            {
                Name = "controlled-fake-ocr",
                DefaultResult = OcrResult.FromWords(
                [
                    new OcrWord("Milling", new RectInt(586, 424, 62, 18), 0),
                    new OcrWord("Status", new RectInt(652, 424, 52, 18), 0),
                    new OcrWord(sample.ExpectedProgressText!, new RectInt(765, 424, 76, 18), 0),
                ]),
            };
            var screenDetector = new MainScreenDetector(profile.MainScreen!, screenOcr);
            MainScreenDetectionResult screen = await screenDetector.DetectAsync(frame, CancellationToken.None);
            Assert.True(screen.MainScreenVisible);
            Assert.Equal(1, screenOcr.CallCount);

            var progressOcr = new FakeOcrEngine
            {
                Name = "controlled-fake-ocr",
                DefaultResult = new OcrResult(sample.ExpectedProgressText!, [sample.ExpectedProgressText!]),
            };
            var reader = new ProgressNumberReader(profile.Idle!, profile.DarkTheme, progressOcr);
            ProgressDetectionResult progress = await reader.ReadAsync(frame.Crop(profile.Idle!.ProgressRoi), CancellationToken.None);
            Assert.Equal(sample.ExpectedProgressPercent, progress.Percent);
            Assert.Equal(sample.ExpectedProgressPercent == 100, progress.IsFull);
            Assert.Equal(1, progressOcr.CallCount);
        }
    }

    private static ProductionManifest LoadManifest()
    {
        string path = Path.Combine(TestPaths.SamplesOutput, "production-opencnc", "manifest.json");
        return JsonSerializer.Deserialize<ProductionManifest>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException($"Nie można odczytać manifestu '{path}'.");
    }

    private static string ResolveSample(string relativePath) => Path.GetFullPath(
        Path.Combine(
            TestPaths.SamplesOutput,
            "production-opencnc",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static int CountRgbColours(RgbaImage image)
    {
        var colours = new HashSet<(byte R, byte G, byte B)>();
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            colours.Add((image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]));
            if (colours.Count > 1)
            {
                return colours.Count;
            }
        }

        return colours.Count;
    }

    private sealed record ProductionManifest
    {
        public int SchemaVersion { get; init; }
        public string SourceRoot { get; init; } = string.Empty;
        public List<ProductionSample> Samples { get; init; } = [];
    }

    private sealed record ProductionSample
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
        public int? ExpectedProgressPercent { get; init; }
        public string? ExpectedProgressText { get; init; }
        public string Evidence { get; init; } = string.Empty;
    }
}
