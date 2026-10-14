using System.Runtime.Versioning;
using System.Text;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;
using Xunit.Abstractions;

namespace MaszynyAlert.Windows.Tests;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrRedField520MeasurementTests(ITestOutputHelper output)
{
    private const string ProfileId = "aidite5x";
    private const string ReportFileName = "pomiar-ocr-520-czerwone-pole.txt";

    /// <summary>
    /// Reguła, w którą POWINIEN trafić odczyt treści wycinka (wzrokowo, patrz manifest): <see langword="null"/> —
    /// komunikatu nie ma w bazie (żółte „TIP:ORIGIN NOT REINSTATED”). Tylko do podsumowania raportu, bez asercji.
    /// </summary>
    private static readonly Dictionary<string, string?> ExpectedRule = new(StringComparer.Ordinal)
    {
        ["520-akrony-093850-blad.png"] = null,
        ["520-akrony-093917-blad.png"] = "aidite5x-tool-life",
        ["520-akrony-093930-blad.png"] = "aidite5x-tool-life",
        ["520-akrony-093951-blad.png"] = "aidite5x-before-using",
        ["520-akrony-095746-blad.png"] = "aidite5x-before-using",
        ["520-akrony-100508-blad.png"] = "aidite5x-before-using",
        ["520-akrony-100552-blad.png"] = "aidite5x-before-using",
        ["520-akrony-101439-blad.png"] = "aidite5x-before-using",
        ["520-akrony-101955-blad.png"] = "aidite5x-before-using",
        ["520-akrony-102008-blad.png"] = "aidite5x-before-using",
    };

    [Fact]
    public async Task Pomiar_TekstuNaKolorowymPolu520_DlaKazdegoJezykaIWariantu()
    {
        string baseDir = AppContext.BaseDirectory;
        string folder = Path.Combine(baseDir, "samples", "production-aidite5x", "pasek-ocr-2026-09-25");
        var bars = Directory.EnumerateFiles(folder, "*.png")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Name: Path.GetFileName(f), Image: PngCodec.DecodeFile(f)))
            .ToList();
        Assert.Equal(ExpectedRule.Count, bars.Count);
        Assert.All(bars, b =>
        {
            Assert.True(ExpectedRule.ContainsKey(b.Name), $"Nieznany wycinek {b.Name}.");
            Assert.Equal(954, b.Image.Width);
            Assert.Equal(48, b.Image.Height);
        });

        ScreenProfile profile = ConfigLoader.LoadAppConfig(Path.Combine(baseDir, "config", "config.example.json")).Profiles[ProfileId];
        var mapper = new MessageMapper(ConfigLoader.LoadRules(Path.Combine(baseDir, "config", "rules.example.json")));
        int margin = profile.OcrMarginPx;
        var variants = new (string Name, Func<RgbaImage, Gray8Image?> Prepare)[]
        {
            ($"A maska-czerwieni m{margin} x{profile.OcrScale} (dziś 1. podejście)", roi => ImageOps.PrepareForOcr(roi, profile.Red, margin, profile.OcrScale)),
            ($"B szarość m{margin} x{profile.OcrScale} (dziś 2. podejście)", roi => ImageOps.PrepareTextForOcr(roi, margin, profile.OcrScale, invert: false)),
            ($"C pola m{margin} x1", roi => ImageOps.PrepareRedFieldTextForOcr(roi, profile.Red, margin, 1)),
            ($"C pola m{margin} x2", roi => ImageOps.PrepareRedFieldTextForOcr(roi, profile.Red, margin, 2)),
            ($"C pola m{margin} x3", roi => ImageOps.PrepareRedFieldTextForOcr(roi, profile.Red, margin, 3)),
        };

        IReadOnlyList<string> languages = WindowsOcrEngine.AvailableLanguages();
        output.WriteLine($"Języki OCR: {string.Join(", ", languages)}");
        var report = new StringBuilder();
        report.AppendLine($"Pomiar OCR paska 520 (profil {ProfileId}: red {profile.Red.HueMinDeg}-{profile.Red.HueMaxDeg}°, S≥{profile.Red.MinSaturation}, V≥{profile.Red.MinValue}; ocrMarginPx {margin}, ocrScale {profile.OcrScale}).");
        report.AppendLine($"Języki OCR: {(languages.Count == 0 ? "brak — pomiaru nie było" : string.Join(", ", languages))}.");
        report.AppendLine("Wiersz: język | wycinek | wariant | „odczyt” | reguła (oczekiwana) | krótki opis | wyciszenie");

        var summary = new Dictionary<(string Language, string Variant), int>();
        foreach (string language in languages)
        {
            var engine = new WindowsOcrEngine(language);
            report.AppendLine($"=== język {language} (silnik: {engine.LanguageTag}) ===");
            foreach ((string name, RgbaImage bar) in bars)
            {
                foreach ((string variant, Func<RgbaImage, Gray8Image?> prepare) in variants)
                {
                    Gray8Image? prepared = prepare(bar);
                    if (prepared is null)
                    {
                        report.AppendLine($"{language} | {name} | {variant} | (brak pól z napisem — wariant pominięty) | - | - | -");
                        continue;
                    }

                    OcrResult result = await engine.RecognizeAsync(prepared, CancellationToken.None);
                    string text = result.Lines.Count > 0 ? string.Join(" ⏎ ", result.Lines) : result.Text;
                    MappedMessage mapped = mapper.Map(result.Text, ProfileId);
                    string? expected = ExpectedRule[name];
                    bool agrees = string.Equals(mapped.RuleId, expected, StringComparison.Ordinal);
                    if (agrees)
                    {
                        summary[(language, variant)] = summary.GetValueOrDefault((language, variant)) + 1;
                    }

                    report.AppendLine(
                        $"{language} | {name} | {variant} ({prepared.Width}×{prepared.Height}) | „{text}” | {mapped.RuleId ?? "-"} ({expected ?? "brak reguły"}{(agrees ? ", zgodne" : ", NIEZGODNE")}) | {mapped.ShortText} | {(mapped.Suppressed ? "bez alarmu" : "alarm")}");
                }
            }
        }

        report.AppendLine("=== podsumowanie: odczyt trafił w oczekiwaną regułę (z 10 wycinków) ===");
        foreach (string language in languages)
        {
            foreach ((string variant, _) in variants)
            {
                report.AppendLine($"{language} | {variant} | {summary.GetValueOrDefault((language, variant))}/{bars.Count}");
            }
        }

        output.WriteLine(report.ToString());
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        string dir = !string.IsNullOrEmpty(workspace)
            ? Path.Combine(workspace, "TestResults", "Windows")
            : Path.Combine(baseDir, "TestResults");
        Directory.CreateDirectory(dir);
        string reportPath = Path.Combine(dir, ReportFileName);
        await File.WriteAllTextAsync(reportPath, report.ToString());

        Assert.True(File.Exists(reportPath));
    }
}
