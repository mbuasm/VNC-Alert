using System.Security.Cryptography;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class PremileCameo1SamplesTests
{
    private const string CameoDir = "cameo1-2026-10-11";
    private const string PremileDir = "premile-2026-10-11";

    [Theory]
    [InlineData(CameoDir, "cameo1")]
    [InlineData(PremileDir, "premile")]
    public void NatywneKlatki_ZachowujaBajtyIPasekStanuBezFalszywejBarwy(string dir, string profileId)
    {
        Manifest manifest = LoadManifest(dir);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(profileId, manifest.Profile);
        Assert.Equal("Qt for Embedded Linux VNC Server", manifest.ServerName);
        Assert.Equal("16bpp depth 16 RGB565 LE", manifest.PixelFormat);
        Assert.Equal(2, manifest.Samples.Count);

        ScreenProfile profile = TestPaths.Profile(profileId);
        Assert.Equal(new RectInt(35, 709, 954, 48), profile.StatusBarRoi);
        var detector = new RedTextDetector(profile);

        foreach (Sample sample in manifest.Samples)
        {
            string path = Resolve(dir, sample.SamplePath);
            Assert.Equal(sample.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            Assert.False(string.IsNullOrWhiteSpace(sample.Evidence));

            RgbaImage frame = PngCodec.DecodeFile(path);
            Assert.Equal(1024, frame.Width);
            Assert.Equal(768, frame.Height);

            RedDetectionResult red = detector.Analyze(frame.Crop(profile.StatusBarRoi));
            Assert.Equal(sample.ExpectedRed, red.IsRed);
            Assert.True(red.RedFraction <= sample.MaxRedFraction,
                $"Próbka '{sample.Id}': udział barwy w pasku stanu {red.RedFraction:P3} > {sample.MaxRedFraction:P3}.");

            Assert.True(TurquoiseFraction(frame.Crop(profile.StatusBarRoi)) > 0.5,
                $"Próbka '{sample.Id}': w obszarze paska stanu nie ma turkusowego tła paska — obszar przesunięty?");

            // Piksele z okna barwy istnieją w klatce (żółta zakładka/przyciski), ale ŻADEN nie leży w pasku stanu.
            RectInt? colour = WindowBoundingBox(frame, profile.Red);
            Assert.NotNull(colour);
            Assert.True(colour!.Value.Y + colour.Value.Height <= profile.StatusBarRoi.Y,
                $"Próbka '{sample.Id}': piksele okna barwy sięgają y={colour.Value.Y + colour.Value.Height}, a pasek zaczyna się na y={profile.StatusBarRoi.Y}.");
        }
    }

    [Fact]
    public void Cameo1_MagentaPodswietleniaLiniiGKodu_LezyPonizejOknaBarwy()
    {
        ScreenProfile profile = TestPaths.Profile("cameo1");
        RgbaImage frame = PngCodec.DecodeFile(Resolve(CameoDir, LoadManifest(CameoDir).Samples[0].SamplePath));
        RgbaImage preview = frame.Crop(new RectInt(40, 440, 390, 60));

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
                $"Piksel podglądu G-kodu RGB({pixel.R},{pixel.G},{pixel.B}) wpadł w okno barwy profilu cameo1.");
        }

        Assert.NotEmpty(magenta);
        Assert.True(magenta.Max() < profile.Red.HueMinDeg,
            $"Najwyższy odcień magenty {magenta.Max():F1}° dotknął dolnej granicy {profile.Red.HueMinDeg}°.");
    }

    [Fact]
    public async Task Cameo1_ObszarPostepuObejmujeWierszProgress_IStojacaMaszynaDaje0Procent()
    {
        ScreenProfile profile = TestPaths.Profile("cameo1");
        RectInt roi = profile.Idle!.ProgressRoi;

        foreach (Sample sample in LoadManifest(CameoDir).Samples)
        {
            RgbaImage frame = PngCodec.DecodeFile(Resolve(CameoDir, sample.SamplePath));

            RectInt text = DarkTextBoundingBox(frame, new RectInt(530, 395, 470, 45));
            Assert.Equal(new RectInt(551, 409, 321, 15), text);
            Assert.True(roi.X <= text.X && roi.Y <= text.Y
                        && roi.X + roi.Width >= text.X + text.Width && roi.Y + roi.Height >= text.Y + text.Height,
                $"Obszar postępu {roi} nie obejmuje wiersza Progress {text}.");

            var ocr = new FakeOcrEngine
            {
                Name = "controlled-fake-ocr",
                DefaultResult = new OcrResult(sample.ExpectedProgressText!, [sample.ExpectedProgressText!]),
            };
            var reader = new ProgressNumberReader(profile.Idle!, profile.DarkTheme, ocr);
            ProgressDetectionResult progress = await reader.ReadAsync(frame.Crop(roi), CancellationToken.None);
            Assert.Equal(sample.ExpectedProgressPercent, progress.Percent);
            Assert.False(progress.IsFull);
        }
    }

    [Theory]
    [InlineData(CameoDir, "cameo1", true)]
    [InlineData(PremileDir, "premile", false)]
    public async Task KotwiceEkranu_NaPrawdziwymOdczycieOcr(string dir, string profileId, bool expectedMainScreen)
    {
        ScreenProfile profile = TestPaths.Profile(profileId);
        Assert.True(profile.MainScreen!.Enabled);
        Assert.True(profile.MainScreen.StatusBarAlwaysVisible);

        foreach (Sample sample in LoadManifest(dir).Samples)
        {
            OcrResult real = LoadRealOcr(dir, sample.OcrPath);
            Assert.NotEmpty(real.Words);

            var ocr = new FakeOcrEngine { Name = "real-ocr-replay", DefaultResult = real };
            var detector = new MainScreenDetector(profile.MainScreen, ocr);
            MainScreenDetectionResult screen = await detector.DetectAsync(
                PngCodec.DecodeFile(Resolve(dir, sample.SamplePath)), CancellationToken.None);
            Assert.Equal(expectedMainScreen, screen.MainScreenVisible);

            Assert.Null(LabelAnchorLocator.FindAny(real, MainScreenWatchProfile.DefaultAnchorLabels, 1024, 768, 2));
        }

        if (expectedMainScreen)
        {
            OcrResult auto = LoadRealOcr(dir, LoadManifest(dir).Samples[0].OcrPath);
            Assert.NotNull(LabelAnchorLocator.FindAny(auto, [["Spi", "ndle"]], 1024, 768, 2));
            Assert.NotNull(LabelAnchorLocator.FindAny(auto, [["Tool", "Leg"]], 1024, 768, 2));
            Assert.NotNull(LabelAnchorLocator.FindAny(auto, [["Tool", "NO"]], 1024, 768, 2));
            Assert.Null(LabelAnchorLocator.FindAny(auto, [["Spindle"]], 1024, 768, 2));
        }
    }

    /// <summary>Profile i bazy z przykładowej konfiguracji: maszyny wskazują nowe profile, wyłączone, bez hasła.</summary>
    [Fact]
    public void PrzykladowaKonfiguracja_MaszynyWskazujaNoweProfileIOsobneBazyBledow()
    {
        AppConfig cfg = TestPaths.ExampleConfig;
        MachineConfig cameo = cfg.Machines.Single(m => m.Id == "cameo1");
        MachineConfig premile = cfg.Machines.Single(m => m.Id == "premile");
        Assert.Equal("cameo1", cameo.Profile);
        Assert.Equal("premile", premile.Profile);
        Assert.False(cameo.Enabled);
        Assert.False(premile.Enabled);
        Assert.False(cameo.RequiresPassword);
        Assert.False(premile.RequiresPassword);

        ScreenProfile reference = cfg.Profiles["aidite5x"];
        foreach (string id in new[] { "cameo1", "premile" })
        {
            ScreenProfile profile = cfg.Profiles[id];
            Assert.Equal(reference.StatusBarRoi, profile.StatusBarRoi);
            Assert.Equal(reference.Red, profile.Red);
            Assert.Equal(reference.MinRedFraction, profile.MinRedFraction);
            Assert.Equal(reference.Idle!.ProgressRoi, profile.Idle!.ProgressRoi);
            Assert.Empty(profile.MainScreen!.OtherScreenLabels);
        }

        RuleSet rules = TestPaths.ExampleRules;
        Assert.Empty(rules.Databases["cameo1"].Rules);
        Assert.Empty(rules.Databases["premile"].Rules);
        Assert.Empty(rules.Databases["cameo1"].Codes);
        Assert.Empty(rules.Databases["premile"].Codes);
    }

    private static double TurquoiseFraction(RgbaImage image)
    {
        int matched = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            (double h, double s, double v) = ColorMath.RgbToHsv(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
            if (h is >= 165 and <= 195 && s is >= 0.1 and <= 0.4 && v >= 0.75)
            {
                matched++;
            }
        }

        return (double)matched / (image.Pixels.Length / 4);
    }

    private static RectInt? WindowBoundingBox(RgbaImage image, RedThresholds thresholds)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int i = ((y * image.Width) + x) * 4;
                var pixel = new Rgba(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
                if (!ColorMath.IsRed(pixel, thresholds))
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < 0 ? null : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static RectInt DarkTextBoundingBox(RgbaImage frame, RectInt area)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                int i = ((y * frame.Width) + x) * 4;
                if (frame.Pixels[i] + frame.Pixels[i + 1] + frame.Pixels[i + 2] >= 300)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        Assert.True(maxX >= 0, "W pasie wiersza Progress nie ma ciemnego tekstu.");
        return new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static OcrResult LoadRealOcr(string dir, string fileName)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Resolve(dir, fileName)));
        var words = new List<OcrWord>();
        foreach (JsonElement w in doc.RootElement.GetProperty("slowa").EnumerateArray())
        {
            words.Add(new OcrWord(
                w.GetProperty("tekst").GetString()!,
                new RectInt(w.GetProperty("x").GetInt32(), w.GetProperty("y").GetInt32(),
                    w.GetProperty("w").GetInt32(), w.GetProperty("h").GetInt32()),
                w.GetProperty("linia").GetInt32()));
        }

        return OcrResult.FromWords(words);
    }

    private static Manifest LoadManifest(string dir)
    {
        string path = Resolve(dir, "manifest.json");
        return JsonSerializer.Deserialize<Manifest>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException($"Nie można odczytać manifestu '{path}'.");
    }

    private static string Resolve(string dir, string fileName) => Path.Combine(TestPaths.SamplesOutput, dir, fileName);

    private sealed record Manifest
    {
        public int SchemaVersion { get; init; }
        public string Machine { get; init; } = string.Empty;
        public string Profile { get; init; } = string.Empty;
        public string ServerName { get; init; } = string.Empty;
        public string PixelFormat { get; init; } = string.Empty;
        public string Hmi { get; init; } = string.Empty;
        public List<Sample> Samples { get; init; } = [];
    }

    private sealed record Sample
    {
        public string Id { get; init; } = string.Empty;
        public string SamplePath { get; init; } = string.Empty;
        public string OcrPath { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public string ExpectedScreen { get; init; } = string.Empty;
        public bool ExpectedRed { get; init; }
        public double MaxRedFraction { get; init; }
        public int? ExpectedProgressPercent { get; init; }
        public string? ExpectedProgressText { get; init; }
        public string Evidence { get; init; } = string.Empty;
    }
}
