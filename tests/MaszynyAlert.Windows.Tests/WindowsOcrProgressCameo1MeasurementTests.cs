using System.Runtime.Versioning;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;
using Xunit.Abstractions;

namespace MaszynyAlert.Windows.Tests;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrProgressCameo1MeasurementTests(ITestOutputHelper output)
{
    private static readonly IdleWatchProfile IdleCameo1 = new()
    {
        ProgressRoi = new RectInt(540, 402, 365, 28),
        OcrScale = 3,
        OcrMarginPx = 8,
    };

    private static readonly string[] Frames =
    [
        "cameo1-2026-10-11/auto-stoi-0-procent-191242.png",
        "cameo1-2026-10-11/auto-stoi-0-procent-194717.png",
    ];

    [Fact]
    public async Task Ocr_NaKlatkachCameo1_NieDajeProcentuInnegoNiz0()
    {
        IReadOnlyList<string> languages = WindowsOcrEngine.AvailableLanguages();
        output.WriteLine($"Języki OCR: {string.Join(", ", languages)}");
        var failures = new List<string>();
        foreach (string language in languages)
        {
            var engine = new WindowsOcrEngine(language);
            foreach (string file in Frames)
            {
                RgbaImage frame = PngCodec.DecodeFile(Path.Combine(AppContext.BaseDirectory, "samples", file));
                var reader = new ProgressNumberReader(IdleCameo1, darkTheme: false, engine);
                ProgressDetectionResult result = await reader.ReadAsync(frame.Crop(IdleCameo1.ProgressRoi), CancellationToken.None);
                string line = $"{language} | {file} | ekran 0 | odczyt {result.Percent?.ToString() ?? "nie wiem"} | „{result.RawText}” | prób OCR {reader.OcrCalls}";
                output.WriteLine(line);
                if (result.Percent is { } percent && percent != 0)
                {
                    failures.Add(line);
                }

                Assert.False(result.IsFull, line);
            }
        }

        Assert.True(failures.Count == 0, "OCR dał na klatkach Cameo 1 procent inny niż na ekranie (0):\n" + string.Join("\n", failures));
    }
}
