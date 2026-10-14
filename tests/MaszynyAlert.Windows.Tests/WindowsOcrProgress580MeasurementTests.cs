using System.Runtime.Versioning;
using System.Text;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;
using Xunit.Abstractions;

namespace MaszynyAlert.Windows.Tests;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrProgress580MeasurementTests(ITestOutputHelper output)
{
    /// <summary>Zmierzony <c>idle.progressRoi</c> profilu <c>amm580</c> (config/config.example.json).</summary>
    private static readonly RectInt ProgressRoi = new(420, 246, 370, 30);

    /// <summary>Prawa część tego obszaru — liczba jest wyrównana do prawej i kończy się na x≈772.</summary>
    private static readonly RectInt RightPart = new(640, 246, 150, 30);

    private static readonly (string Name, RectInt Roi, int Margin, int Scale)[] Variants =
    [
        ("roi-m8-x3 (dziś)", ProgressRoi, 8, 3),
        ("roi-m8-x2", ProgressRoi, 8, 2),
        ("roi-m8-x1", ProgressRoi, 8, 1),
        ("roi-m20-x3", ProgressRoi, 20, 3),
        ("prawa-m8-x3", RightPart, 8, 3),
        ("prawa-m8-x2", RightPart, 8, 2),
        ("prawa-m20-x3", RightPart, 20, 3),
        ("prawa-m20-x4", RightPart, 20, 4),
    ];

    [Fact]
    public async Task Pomiar_PolaPostepu580_DlaKazdegoJezykaIWariantu()
    {
        IReadOnlyList<string> languages = WindowsOcrEngine.AvailableLanguages();
        output.WriteLine($"Języki OCR: {string.Join(", ", languages)}");
        if (languages.Count == 0)
        {
            return;
        }

        string samples = Path.Combine(AppContext.BaseDirectory, "samples");
        var frames = new List<(string Name, string Path)>();
        foreach (string dir in new[] { "ocr-580-postep", "production-amm580" })
        {
            string full = Path.Combine(samples, dir);
            if (Directory.Exists(full))
            {
                frames.AddRange(Directory.EnumerateFiles(full, "*.png").OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => ($"{dir}/{Path.GetFileName(f)}", f)));
            }
        }

        Assert.NotEmpty(frames);
        var report = new StringBuilder();
        int reads = 0;
        foreach (string language in languages)
        {
            var engine = new WindowsOcrEngine(language);
            report.AppendLine($"=== język {language} (silnik: {engine.LanguageTag}) ===");
            foreach ((string frameName, string path) in frames)
            {
                RgbaImage frame = PngCodec.DecodeFile(path);
                if (frame.Width != 800 || frame.Height != 600)
                {
                    continue;
                }

                foreach ((string variant, RectInt roi, int margin, int scale) in Variants)
                {
                    Gray8Image prepared = ImageOps.PrepareTextForOcr(frame.Crop(roi), margin, scale, invert: true);
                    OcrResult result = await engine.RecognizeAsync(prepared, CancellationToken.None);
                    bool ok = PercentParser.TryParse(result.Text, out int percent, out bool fraction);
                    report.AppendLine($"{language} | {frameName} | {variant} | „{result.Text}” | {(ok ? $"{percent}%{(fraction ? " (ułamek)" : string.Empty)}" : "nie wiem")}");
                    reads++;
                }
            }
        }

        output.WriteLine(report.ToString());
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrEmpty(workspace))
        {
            string dir = Path.Combine(workspace, "TestResults", "Windows");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "pomiar-ocr-580-postep.txt"), report.ToString());
        }

        Assert.True(reads > 0);
    }
}
