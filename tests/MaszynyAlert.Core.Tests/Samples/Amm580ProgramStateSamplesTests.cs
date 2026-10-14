using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

public sealed class Amm580ProgramStateSamplesTests
{
    private const string SeriesDir = "ocr-580-postep";
    private const string ProductionDir = "production-amm580";

    private const string Abort = "IDLE: Abort";
    private const string NormalStop = "IDLE: Normal Stop";

    /// <summary>
    /// Stany klatek z <c>production-amm580</c> — ich manifest nie ma pola „state”; odczytane wzrokowo
    /// z pudełka stanu (zgodne z opisami w manifeście i z tekstem OCR tam, gdzie go zapisano).
    /// </summary>
    private static readonly Dictionary<string, string> ProductionStates = new(StringComparer.Ordinal)
    {
        ["alarm-t4-tool-life.png"] = Abort,
        ["auto-bez-alarmu-0-procent.png"] = Abort,
        ["auto-praca-10-procent-2026-09-25.png"] = "RUN: Normal",
        ["auto-praca-21-procent-zrzut-okna.png"] = "RUN: Normal",
        ["auto-praca-run-continue-0-procent.png"] = "RUN: Continue",
        ["auto-praca-run-normal-22-procent.png"] = "RUN: Normal",
        ["manual-kalibracja-bez-alarmu.png"] = NormalStop,
    };

    private static ProgramStateProfile ProgramState => TestPaths.Profile("amm580").ProgramState!;

    /// <summary>Wszystkie klatki PNG z obu katalogów (nowa klatka dołącza sama — i musi mieć znany stan).</summary>
    public static TheoryData<string, string> Frames()
    {
        var data = new TheoryData<string, string>();
        foreach (string dir in new[] { SeriesDir, ProductionDir })
        {
            string full = Path.Combine(TestPaths.SamplesOutput, dir);
            foreach (string file in Directory.EnumerateFiles(full, "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                data.Add(dir, Path.GetFileName(file));
            }
        }

        return data;
    }

    [Fact]
    public void ProfilAmm580_MaWlaczonaSekcjeStanuProgramuZWzorcemAbort()
    {
        ProgramStateProfile p = ProgramState;

        Assert.True(p.Enabled);
        Assert.Equal(new RectInt(1, 2, 160, 29), p.BoxRoi);
        Assert.Equal(["abort"], p.AbortWords);
        Assert.Equal(1, p.AbortMaxDistance);
        Assert.NotNull(p.AbortPattern);
        Assert.Equal(40, p.AbortPattern!.X);
        Assert.Equal(9, p.AbortPattern.Y);
        Assert.Equal(14, p.AbortPattern.Rows.Count);
        Assert.All(p.AbortPattern.Rows, r => Assert.Equal(83, r.Length));
        // 183 piksele białego rdzenia „IDLE: Abort” — tyle zmierzono na każdej natywnej klatce Abort.
        Assert.Equal(183, p.AbortPattern.Rows.Sum(r => r.Count(c => c == '#')));

        // Pudełko stanu NIE jest częścią obszaru koloru alarmu: statusBarRoi zostaje (150,0,460,32).
        Assert.Equal(new RectInt(150, 0, 460, 32), TestPaths.Profile("amm580").StatusBarRoi);
    }

    [Fact]
    public void WszystkieKlatki_MajaZnanyStan_IObejmujaTrzyAbortINormalStop()
    {
        var frames = Frames().Select(row => ((string)row[0], (string)row[1])).ToList();
        Assert.Equal(24, frames.Count);

        var states = frames.Select(f => ExpectedState(f.Item1, f.Item2)).ToList();
        Assert.Equal(3, states.Count(s => s == Abort));
        Assert.Equal(1, states.Count(s => s == NormalStop));
        Assert.Equal(20, states.Count(s => s.StartsWith("RUN:", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Sedno: RUN ⇔ zielone pudełko, „IDLE: Abort” ⇔ wzorzec zgodny — na każdej klatce, w obie strony.
    /// Przy okazji pilnuje zmierzonych liczb: zieleń RUN 0,89–0,90 (próg 0,5), zieleń IDLE 0, wzorzec
    /// Abort 0 niezgodnych komórek (maska identyczna co do piksela).
    /// </summary>
    [Theory]
    [MemberData(nameof(Frames))]
    public void Klatka_StanRunIAbort_ZgadzajaSieZEkranem(string dir, string file)
    {
        string state = ExpectedState(dir, file);
        RgbaImage frame = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, dir, file));
        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);

        ProgramStateReading reading = ProgramStateDetector.AnalyzeFrame(frame, ProgramState);

        Assert.True(reading.Known, reading.Description);
        bool run = state.StartsWith("RUN:", StringComparison.Ordinal);
        Assert.Equal(run, reading.Running);
        Assert.Equal(state == Abort, reading.AbortPattern);

        if (run)
        {
            Assert.InRange(reading.RunFraction, 0.85, 0.95);
            Assert.Equal(-1, reading.AbortMismatch);
        }
        else
        {
            Assert.Equal(0.0, reading.RunFraction);
        }

        if (state == Abort)
        {
            Assert.Equal(0, reading.AbortMismatch);
        }

        if (state == NormalStop)
        {
            // „IDLE: Normal Stop” (zakładka Manual, tło panelu 37 zamiast 43): nie pracuje i nie jest przerwany.
            Assert.False(reading.Running);
            Assert.False(reading.AbortPattern);
        }
    }

    /// <summary>
    /// Niezależność dwóch bramek: nawet gdyby rozpoznanie zieleni zawiodło (tu wymuszone progiem
    /// <c>runMinFraction</c> = 1,0), napisy „RUN: Normal” / „RUN: Continue” / „IDLE: Normal Stop” nie mogą
    /// zbliżyć się do wzorca Abort. Zmierzone minimum to 244 niezgodnych komórek przy progu 20.
    /// </summary>
    [Theory]
    [MemberData(nameof(Frames))]
    public void Klatka_WzorzecAbort_MaDuzyZapasNaNapisachInnychStanow(string dir, string file)
    {
        string state = ExpectedState(dir, file);
        RgbaImage frame = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, dir, file));
        ProgramStateProfile forced = ProgramState with { RunMinFraction = 1.0 };

        ProgramStateReading reading = ProgramStateDetector.AnalyzeFrame(frame, forced);

        Assert.False(reading.Running);
        if (state == Abort)
        {
            Assert.True(reading.AbortPattern);
            Assert.Equal(0, reading.AbortMismatch);
        }
        else
        {
            Assert.False(reading.AbortPattern);
            Assert.True(reading.AbortMismatch >= 200,
                $"{dir}/{file} ({state}): tylko {reading.AbortMismatch} niezgodnych komórek wzorca Abort — zapas wobec progu {forced.AbortPattern!.MaxMismatch} się skurczył.");
        }
    }

    [Fact]
    public void SeriaZ25092026_RunBoxGreenZManifestu_ZgadzaSieZOdczytem()
    {
        foreach (SeriesFrame f in LoadSeriesManifest().Frames)
        {
            RgbaImage frame = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, SeriesDir, f.File));
            ProgramStateReading reading = ProgramStateDetector.AnalyzeFrame(frame, ProgramState);
            Assert.Equal(f.RunBoxGreen, reading.Running);
        }
    }

    [Fact]
    public void ZrzutOknaViewera_DajeTenSamWynikCoKlatkaNatywnaRunNormal()
    {
        RgbaImage lossy = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, ProductionDir, "auto-praca-21-procent-zrzut-okna.png"));
        RgbaImage native = PngCodec.DecodeFile(Path.Combine(TestPaths.SamplesOutput, ProductionDir, "auto-praca-run-normal-22-procent.png"));
        RectInt box = ProgramState.BoxRoi;

        byte[] a = lossy.Crop(box).Pixels;
        byte[] b = native.Crop(box).Pixels;
        int maxDiff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(a[i] - b[i]));
        }

        Assert.InRange(maxDiff, 1, 2); // obraz NIE jest identyczny (rekompresja), ale różni się o włos

        ProgramStateReading fromLossy = ProgramStateDetector.AnalyzeFrame(lossy, ProgramState);
        ProgramStateReading fromNative = ProgramStateDetector.AnalyzeFrame(native, ProgramState);
        Assert.True(fromLossy.Running);
        Assert.Equal(fromNative.RunFraction, fromLossy.RunFraction, 3);

        ProgramStateProfile forced = ProgramState with { RunMinFraction = 1.0 };
        Assert.Equal(
            ProgramStateDetector.AnalyzeFrame(native, forced).AbortMismatch,
            ProgramStateDetector.AnalyzeFrame(lossy, forced).AbortMismatch);
    }

    [Fact]
    public void PrawdziweOdczytyOcr_SlowoAbortTylkoNaKlatkachAbort()
    {
        var withOcr = LoadProductionManifest().Samples.Where(s => !string.IsNullOrEmpty(s.OcrText)).ToList();
        Assert.Equal(5, withOcr.Count);

        foreach (ProductionSample sample in withOcr)
        {
            OcrWord[] words = sample.OcrText!
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new OcrWord(w, new RectInt(60, 10, 40, 12), 0))
                .ToArray();

            bool expected = ProductionStates[sample.SamplePath] == Abort;
            Assert.Equal(expected, ProgramStateDetector.HasAbortWord(words, ProgramState));
        }
    }

    private static string ExpectedState(string dir, string file)
    {
        if (dir == SeriesDir)
        {
            SeriesFrame? f = LoadSeriesManifest().Frames.SingleOrDefault(x => x.File == file);
            Assert.True(f is not null, $"Klatka {dir}/{file} nie ma wpisu w manifeście — dopisz jej stan (pole „state”).");
            return f!.State;
        }

        Assert.True(ProductionStates.TryGetValue(file, out string? state),
            $"Klatka {dir}/{file} nie ma znanego stanu — dopisz ją do ProductionStates.");
        return state!;
    }

    private static SeriesManifest LoadSeriesManifest() =>
        JsonSerializer.Deserialize<SeriesManifest>(
            File.ReadAllText(Path.Combine(TestPaths.SamplesOutput, SeriesDir, "manifest.json")))!;

    private static ProductionManifest LoadProductionManifest() =>
        JsonSerializer.Deserialize<ProductionManifest>(
            File.ReadAllText(Path.Combine(TestPaths.SamplesOutput, ProductionDir, "manifest.json")))!;

    private sealed record SeriesManifest
    {
        [JsonPropertyName("frames")] public IReadOnlyList<SeriesFrame> Frames { get; init; } = [];
    }

    private sealed record SeriesFrame
    {
        [JsonPropertyName("file")] public string File { get; init; } = string.Empty;

        [JsonPropertyName("state")] public string State { get; init; } = string.Empty;

        [JsonPropertyName("runBoxGreen")] public bool RunBoxGreen { get; init; }
    }

    private sealed record ProductionManifest
    {
        [JsonPropertyName("samples")] public IReadOnlyList<ProductionSample> Samples { get; init; } = [];
    }

    private sealed record ProductionSample
    {
        [JsonPropertyName("samplePath")] public string SamplePath { get; init; } = string.Empty;

        [JsonPropertyName("ocrText")] public string? OcrText { get; init; }
    }
}
