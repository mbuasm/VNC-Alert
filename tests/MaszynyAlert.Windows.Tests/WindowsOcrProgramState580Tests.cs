using System.Runtime.Versioning;
using System.Text;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;
using Xunit.Abstractions;

namespace MaszynyAlert.Windows.Tests;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrProgramState580Tests(ITestOutputHelper output)
{
    /// <summary>Klatki „IDLE: Abort” (sprawdzone wzrokowo i wzorcem pikseli w Core.Tests: Amm580ProgramStateSamplesTests).</summary>
    private static readonly HashSet<string> AbortFrames = new(StringComparer.Ordinal)
    {
        "ocr-580-postep/580-2026-09-25-104520-idle-abort-25.png",
        "production-amm580/alarm-t4-tool-life.png",
        "production-amm580/auto-bez-alarmu-0-procent.png",
    };

    /// <summary>Klatka „IDLE: Normal Stop” (zakładka Manual). Wszystkie pozostałe klatki obu katalogów to RUN.</summary>
    private const string NormalStopFrame = "production-amm580/manual-kalibracja-bez-alarmu.png";

    [Fact]
    public async Task Pomiar_SlowoAbortWPudelkuStanu580_DlaKazdegoJezyka()
    {
        IReadOnlyList<string> languages = WindowsOcrEngine.AvailableLanguages();
        output.WriteLine($"Języki OCR: {string.Join(", ", languages)}");
        if (languages.Count == 0)
        {
            return;
        }

        ProgramStateProfile state = ConfigLoader.LoadAppConfig(
            Path.Combine(AppContext.BaseDirectory, "config", "config.example.json")).Profiles["amm580"].ProgramState!;
        Assert.NotNull(state);

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
        Assert.All(AbortFrames, name => Assert.Contains(frames, f => f.Name == name));

        var report = new StringBuilder();
        var failures = new List<string>();
        foreach (string language in languages)
        {
            var engine = new WindowsOcrEngine(language);
            report.AppendLine($"=== język {language} (silnik: {engine.LanguageTag}) ===");
            int abortRead = 0;
            foreach ((string name, string path) in frames)
            {
                RgbaImage frame = PngCodec.DecodeFile(path);
                if (frame.Width != 800 || frame.Height != 600)
                {
                    continue;
                }

                bool isAbort = AbortFrames.Contains(name);
                string expected = isAbort ? "IDLE: Abort" : name == NormalStopFrame ? "IDLE: Normal Stop" : "RUN";
                string pixels = ProgramStateDetector.AnalyzeFrame(frame, state).Description;

                // Skala 1:1 — tak jak MainScreenDetector; prostokąty słów są już we współrzędnych klatki.
                OcrResult x1 = await engine.RecognizeAsync(ImageOps.ToGray(frame), CancellationToken.None);
                bool has1 = ProgramStateDetector.HasAbortWord(x1.Words, state);
                if (isAbort && has1)
                {
                    abortRead++;
                }

                // Wariant ×2 (tylko do wypisania) — prostokąty sprowadzone do skali 1:1.
                OcrResult x2 = await engine.RecognizeAsync(ImageOps.ToGray(frame.Scale(2)), CancellationToken.None);
                OcrWord[] x2Words = x2.Words
                    .Select(w => new OcrWord(w.Text, new RectInt(w.Bounds.X / 2, w.Bounds.Y / 2, w.Bounds.Width / 2, w.Bounds.Height / 2), w.LineIndex))
                    .ToArray();
                bool has2 = ProgramStateDetector.HasAbortWord(x2Words, state);

                string line = $"{language} | {name} | ekran {expected} | piksele: {pixels} | " +
                    $"×1 słowa w pudełku: „{WordsInBox(x1.Words, state.BoxRoi)}” → Abort={has1} | " +
                    $"×2 słowa w pudełku: „{WordsInBox(x2Words, state.BoxRoi)}” → Abort={has2}";
                report.AppendLine(line);

                if (!isAbort && has1)
                {
                    failures.Add(line);
                }
            }

            report.AppendLine($"{language}: słowo Abort (×1) odczytane na {abortRead}/{AbortFrames.Count} klatkach „IDLE: Abort”.");
        }

        output.WriteLine(report.ToString());
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrEmpty(workspace))
        {
            string dir = Path.Combine(workspace, "TestResults", "Windows");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "pomiar-ocr-580-stan-programu.txt"), report.ToString());
        }

        Assert.True(failures.Count == 0,
            "OCR rozpoznał „Abort” w pudełku stanu na klatce BEZ przerwanego programu (fałszywy alarm):\n" + string.Join("\n", failures));
    }

    /// <summary>Słowa, których środek prostokąta leży w pudełku stanu — ta sama reguła co w <see cref="ProgramStateDetector.HasAbortWord"/>.</summary>
    private static string WordsInBox(IReadOnlyList<OcrWord> words, RectInt box) => string.Join(" ", words
        .Where(w => box.Contains(w.Bounds.X + (w.Bounds.Width / 2), w.Bounds.Y + (w.Bounds.Height / 2)))
        .Select(w => w.Text));
}
