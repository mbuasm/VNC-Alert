using System.Runtime.Versioning;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;
using Xunit.Abstractions;

namespace MaszynyAlert.Windows.Tests;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrProgress580RegressionTests(ITestOutputHelper output)
{
    /// <summary>Sekcja <c>idle</c> profilu <c>amm580</c> z config/config.example.json (motyw ciemny → odwrócenie).</summary>
    private static readonly IdleWatchProfile Idle580 = new()
    {
        ProgressRoi = new RectInt(420, 246, 370, 30),
        OcrScale = 3,
        OcrMarginPx = 8,
    };

    /// <summary>Klatka → procent na ekranie (null = na tym ekranie nie ma pola postępu, np. zakładka Manual).</summary>
    private static readonly (string File, int? Percent)[] Frames =
    [
        ("ocr-580-postep/580-10-procent-2026-09-25.png", 10),
        ("ocr-580-postep/580-17-procent-2026-09-25.png", 17),
        ("production-amm580/alarm-t4-tool-life.png", 22),
        ("production-amm580/auto-bez-alarmu-0-procent.png", 0),
        ("production-amm580/auto-praca-10-procent-2026-09-25.png", 10),
        ("production-amm580/auto-praca-21-procent-zrzut-okna.png", 21),
        ("production-amm580/auto-praca-run-continue-0-procent.png", 0),
        ("production-amm580/auto-praca-run-normal-22-procent.png", 22),
        ("production-amm580/manual-kalibracja-bez-alarmu.png", null),
    ];

    [Fact]
    public async Task PolskiOcr_CzytaProcentZKazdejKlatki580()
    {
        IReadOnlyList<string> languages = WindowsOcrEngine.AvailableLanguages();
        output.WriteLine($"Języki OCR: {string.Join(", ", languages)}");
        var failures = new List<string>();
        foreach (string language in languages)
        {
            bool polish = language.StartsWith("pl", StringComparison.OrdinalIgnoreCase);
            var engine = new WindowsOcrEngine(language);
            foreach ((string file, int? expected) in Frames)
            {
                RgbaImage frame = PngCodec.DecodeFile(Path.Combine(AppContext.BaseDirectory, "samples", file));
                var reader = new ProgressNumberReader(Idle580, darkTheme: true, engine);
                ProgressDetectionResult result = await reader.ReadAsync(frame.Crop(Idle580.ProgressRoi), CancellationToken.None);
                string line = $"{language} | {file} | ekran {expected?.ToString() ?? "-"} | odczyt {result.Percent?.ToString() ?? "nie wiem"} | „{result.RawText}” | prób OCR {reader.OcrCalls}";
                output.WriteLine(line);
                if (polish && result.Percent != expected)
                {
                    failures.Add(line);
                }
            }
        }

        Assert.True(failures.Count == 0, "Polski OCR nie odczytał procentu jak na ekranie:\n" + string.Join("\n", failures));
    }
}
