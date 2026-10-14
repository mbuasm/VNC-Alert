using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Vision;
using MaszynyAlert.Windows;

namespace MaszynyAlert.Probe.Commands;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class OcrCommand : ICommand
{
    public Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct) =>
        RunCoreAsync(args, global, report, session, ct);

    /// <summary>Logika komendy, wyodrębniona żeby <c>probe.exe auto</c> (Commands/AutoCommand.cs) mogła wykonać KROK 2 bez duplikowania kodu.</summary>
    [SupportedOSPlatform("windows10.0.19041.0")]
    public static async Task<int> RunCoreAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct)
    {
        report.Line("Dostępne języki OCR (Windows.Media.Ocr):");
        IReadOnlyList<string> languages = WindowsOcrEngine.AvailableLanguages();
        if (languages.Count == 0)
        {
            report.Line("  (brak)");
        }
        else
        {
            foreach (string lang in languages)
            {
                report.Line($"  {lang}");
            }
        }

        report.Line();

        MachineListResult listResult = MachineList.Load(global);
        string preferredLanguage = listResult.Config.Ocr.Language;
        var engine = new WindowsOcrEngine(preferredLanguage);

        report.Line($"Silnik: {engine.Name}, dostępny: {(engine.IsAvailable ? "TAK" : "NIE")}, język: {engine.LanguageTag ?? "-"}");
        report.Line();

        if (!engine.IsAvailable)
        {
            report.Line(
                "brak OCR – nic nie rób. " +
                "Alarmy błędów działają normalnie (wykrywanie po kolorze), ale pole postępu " +
                "(\"maszyna stoi bez pracy\") i pole nazwy pliku nie zostaną znalezione — w podsumowaniu obu " +
                "kolumnom przypisano \"brak OCR\".");

            foreach (MachineConfig machine in listResult.Selected)
            {
                MachineSummary row = session.Summary.Machine(machine.Id);
                row.Name = machine.Name;
                row.ProgressField = "brak OCR";
                row.FileNameField = "brak OCR";
            }

            return 0;
        }

        IReadOnlyList<OcrInput> inputs = CollectInputs(args, global.OutDir, listResult.Config.Machines);

        if (inputs.Count == 0)
        {
            report.Line(
                $"Nie znaleziono żadnych zrzutów do odczytania w \"{Path.GetFullPath(global.OutDir)}\" — " +
                "uruchom najpierw probe.exe info (zapisuje out\\<id>_full.png) albo probe.exe capture " +
                "(zapisuje out\\capture_<adres>_<port>_<data>.png). Możesz też wskazać pliki wprost: " +
                "probe.exe ocr <plik.png|folder> ...");
            return 0;
        }

        report.Line($"Znaleziono {inputs.Count} zrzutów do odczytania.");
        report.Line();

        var suggestedRoi = new Dictionary<string, object>(StringComparer.Ordinal);
        var roiSource = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (OcrInput input in inputs)
        {
            ct.ThrowIfCancellationRequested();

            string file = input.Path;
            string machineId = input.MachineId;

            report.Line($"--- {Path.GetFileName(file)} (id maszyny: {machineId}; {input.MachineSource}) ---");

            RgbaImage full;
            try
            {
                full = PngCodec.DecodeFile(file);
            }
            catch (PngFormatException ex)
            {
                report.Line($"nie udało się odczytać pliku: {ex.Message}");
                report.Line();
                continue;
            }

            // Cała klatka w skali szarości — zawsze, niezależnie od tego, czy znamy profil maszyny.
            OcrResult wholeFrameResult = await engine.RecognizeAsync(ImageOps.ToGray(full), ct).ConfigureAwait(false);
            report.Line($"OCR całej klatki: \"{wholeFrameResult.Text}\"");

            MachineConfig? machine = listResult.Config.Machines
                .FirstOrDefault(m => string.Equals(m.Id, machineId, StringComparison.OrdinalIgnoreCase));

            if (machine is null || !listResult.Config.Profiles.TryGetValue(machine.Profile, out ScreenProfile? profile))
            {
                report.Line($"(nie znam profilu dla \"{machineId}\" — pomijam OCR samego paska błędu i kotwiczenie).");
                report.Line();
                continue;
            }

            session.Summary.Machine(machineId).Name = machine.Name;
            session.Summary.Machine(machineId).Screen = $"{full.Width}x{full.Height}";

            RectInt roi = profile.StatusBarRoi.Clamp(full.Width, full.Height);
            if (roi.IsEmpty)
            {
                report.Line("(ROI paska statusu profilu leży poza tym zrzutem — pomijam OCR paska błędu, kotwiczenie i tak liczę).");
            }
            else
            {
                RgbaImage roiImage = full.Crop(roi);
                Gray8Image prepared = ImageOps.PrepareForOcr(roiImage, profile.Red, profile.OcrMarginPx, profile.OcrScale);

                string ocrPath = Path.Combine(global.OutDir, input.PreparedImageName);
                PngCodec.EncodeToFile(prepared, ocrPath);

                OcrResult roiOcrResult = await engine.RecognizeAsync(prepared, ct).ConfigureAwait(false);
                report.Line($"OCR paska błędu (profil \"{profile.Id}\"): \"{roiOcrResult.Text}\"");
                report.Line($"zapisano obraz przygotowany do OCR: {Path.GetFullPath(ocrPath)}");
            }

            MachineAnchors anchors = await ScreenAnchors.ComputeAsync(full, engine, profile, ct).ConfigureAwait(false);
            session.SetAnchors(machineId, anchors);

            MachineSummary summaryRow = session.Summary.Machine(machineId);
            summaryRow.ProgressField = ScreenAnchors.DescribeProgressField(anchors);
            summaryRow.FileNameField = ScreenAnchors.DescribeFileNameField(anchors);

            report.Line(DescribeProgressReportLine(anchors));
            report.Line(await DescribeConfiguredProgressAsync(full, profile, engine, ct).ConfigureAwait(false));
            foreach (string line in DescribeBarAndProgramState(full, profile))
            {
                report.Line(line);
            }
            report.Line(DescribeFileNameReportLine(anchors));
            report.Line(
                anchors.AlertWindowIsHeuristic
                    ? $"okienko alertów: znaleziono heurystyką (\"Tool\"/\"Manual\") — {ScreenAnchors.FormatRoi(anchors.AlertWindowRoi)}."
                    : $"okienko alertów: heurystyka nie zadziałała na tym zrzucie — używam statusBarRoi profilu ({ScreenAnchors.FormatRoi(anchors.AlertWindowRoi)}).");

            if (anchors.ProgressRoi is { } progressRoi && !anchors.ProgressRoiIsGuess)
            {
                report.Line($"Fragment do wklejenia w config.json (profil \"{profile.Id}\", sekcja \"idle\"):");
                report.Line("  " + ScreenAnchors.FormatRoiJsonSnippet("progressRoi", progressRoi));
            }
            else if (anchors.ProgressRoiIsGuess)
            {
                report.Line(
                    "NIE wklejaj tego obszaru do config.json — to tylko zgadnięte miejsce obok etykiety. " +
                    (ProfileProgressRoi(profile) is { } configured
                        ? $"Zostaw zmierzone \"progressRoi\": {ScreenAnchors.FormatRoi(configured)} z profilu \"{profile.Id}\" (wiersz wyżej widać, co z niego odczytał OCR)."
                        : $"Profil \"{profile.Id}\" nie ma jeszcze \"idle.progressRoi\" — obszar trzeba zmierzyć na tym PNG, a nie brać z tej sugestii."));
            }

            if (anchors.AlertWindowIsHeuristic)
            {
                report.Line($"Fragment do wklejenia w config.json (profil \"{profile.Id}\"):");
                report.Line("  " + ScreenAnchors.FormatRoiJsonSnippet("statusBarRoi", anchors.AlertWindowRoi));
            }

            if (roiSource.TryGetValue(machineId, out string? earlier))
            {
                report.Line(
                    $"(dla maszyny \"{machineId}\" mam już wynik z pliku {earlier} — w sugerowane-roi.json zostaje " +
                    $"ten nowszy, z {Path.GetFileName(file)}).");
            }

            roiSource[machineId] = Path.GetFileName(file);
            suggestedRoi[machineId] = new
            {
                profile = profile.Id,
                zrodlo = Path.GetFileName(file),
                screen = new { width = full.Width, height = full.Height },
                // Zgadnięty obszar zostaje NULLEM: ten plik operator wkleja do config.json wprost, a wpisanie
                // do niego rectangle'a, z którego OCR nic nie odczytał, zepsułoby wykrywanie postoju.
                progressRoi = RoiOrNull(anchors.ProgressRoiIsGuess ? null : anchors.ProgressRoi),
                progressRoiUwaga = anchors.ProgressRoiIsGuess
                    ? $"kotwica trafiła w etykietę, ale OCR nie odczytał obok niej żadnej wartości, więc obszar {ScreenAnchors.FormatRoi(anchors.ProgressRoi!.Value)} jest ZGADNIĘTY i nie jest tu podany. "
                        + (ProfileProgressRoi(profile) is { } configuredRoi
                            ? $"Zostaw zmierzone \"idle.progressRoi\" {ScreenAnchors.FormatRoi(configuredRoi)} z profilu \"{profile.Id}\"."
                            : $"Profil \"{profile.Id}\" nie ma jeszcze \"idle.progressRoi\" — zmierz obszar na tym PNG.")
                    : null,
                fileNameRoi = RoiOrNull(anchors.FileNameRoi),
                statusBarRoi = RoiOrNull(anchors.AlertWindowRoi.IsEmpty ? null : anchors.AlertWindowRoi),
            };

            report.Line();
        }

        SaveSuggestedRoi(suggestedRoi, report, global.OutDir);

        return 0;
    }

    /// <summary>
    /// Jeden zrzut wzięty do odczytu przez krok <c>ocr</c>: ścieżka pliku, maszyna, do której go
    /// przypisano, skąd wzięło się to przypisanie (do raportu — operator ma widzieć, czemu wierzy
    /// program) i nazwa pliku, pod którą zapisze się obraz przygotowany do OCR.
    /// </summary>
    internal sealed record OcrInput(string Path, string MachineId, string MachineSource, string PreparedImageName);

    internal static IReadOnlyList<OcrInput> CollectInputs(IReadOnlyList<string> args, string outDir, IReadOnlyList<MachineConfig> machines)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(machines);

        var paths = new List<string>();
        List<string> requested = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (requested.Count > 0)
        {
            foreach (string arg in requested)
            {
                if (Directory.Exists(arg))
                {
                    paths.AddRange(ScanDirectory(arg));
                }
                else if (File.Exists(arg))
                {
                    // Wskazany wprost plik bierzemy zawsze — także wycinek _roi/_ocr, skoro operator go podał.
                    paths.Add(arg);
                }
                else
                {
                    throw new ProbeUsageException(
                        $"Nie znalazłem pliku ani folderu \"{arg}\". Użycie: probe.exe ocr [plik.png|folder] … " +
                        "(bez argumentów: wszystkie zrzuty z folderu --out).");
                }
            }
        }
        else if (Directory.Exists(outDir))
        {
            paths.AddRange(ScanDirectory(outDir));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OcrInput>();
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                result.Add(Describe(full, machines));
            }
        }

        return result;
    }

    /// <summary>Zrzuty z folderu: pliki z kroku <c>info</c> i z kroku <c>capture</c>, bez obrazów pochodnych (patrz <see cref="IsDerivedImage"/>).</summary>
    private static IEnumerable<string> ScanDirectory(string dir) =>
        Directory.GetFiles(dir, "*.png")
            .Where(f => !IsDerivedImage(Path.GetFileName(f)))
            .Where(f => IsFullFrame(Path.GetFileName(f)) || IsCapture(Path.GetFileName(f)))
            .OrderBy(f => IsFullFrame(Path.GetFileName(f)) ? 0 : 1)
            .ThenBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

    internal static bool IsDerivedImage(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        foreach (string suffix in new[] { "_ocr.png", "-ocr.png", "_roi.png", "-roi.png" })
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFullFrame(string fileName) =>
        fileName.EndsWith("_full.png", StringComparison.OrdinalIgnoreCase);

    private static bool IsCapture(string fileName) =>
        fileName.StartsWith("capture_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Przypisuje zrzut do maszyny: po nazwie pliku z kroku <c>info</c> albo po adresie z nazwy pliku z kroku <c>capture</c>.</summary>
    private static OcrInput Describe(string path, IReadOnlyList<MachineConfig> machines)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);

        const string suffix = "_full";
        // Bez rozróżniania wielkości liter, tak samo jak IsFullFrame.
        if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            string id = fileName[..^suffix.Length];
            return new OcrInput(path, id, "z nazwy pliku kroku info", $"{id}_ocr.png");
        }

        if (TryParseCaptureFileName(fileName, out string? host, out int port))
        {
            MachineConfig? machine = machines.FirstOrDefault(m =>
                string.Equals(m.Host, host, StringComparison.OrdinalIgnoreCase) && m.Port == port);

            return machine is null
                ? new OcrInput(path, $"{host}:{port}", $"adres {host}:{port} z nazwy pliku — w konfiguracji nie ma takiej maszyny", $"{fileName}_pasek-ocr.png")
                : new OcrInput(path, machine.Id, $"po adresie {host}:{port} z nazwy pliku kroku capture", $"{fileName}_pasek-ocr.png");
        }

        return new OcrInput(path, fileName, "z nazwy pliku", $"{fileName}_pasek-ocr.png");
    }

    /// <summary>Rozbiera nazwę zapisywaną przez <c>probe.exe capture</c>: <c>capture_&lt;adres&gt;_&lt;port&gt;_&lt;data-godzina&gt;</c>.</summary>
    internal static bool TryParseCaptureFileName(string fileNameWithoutExtension, out string? host, out int port)
    {
        host = null;
        port = 0;

        if (!fileNameWithoutExtension.StartsWith("capture_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = fileNameWithoutExtension.Split('_');
        if (parts.Length < 4)
        {
            return false;
        }

        if (!int.TryParse(parts[^2], CultureInfo.InvariantCulture, out port) || port <= 0 || port > 65535)
        {
            port = 0;
            return false;
        }

        // Adres może zawierać podkreślenie (nazwa komputera), więc sklejamy wszystko między "capture" a portem.
        host = string.Join('_', parts[1..^2]);
        return host.Length > 0;
    }

    private static void SaveSuggestedRoi(Dictionary<string, object> suggestedRoi, Report report, string outDir)
    {
        if (suggestedRoi.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(outDir);
            string jsonPath = Path.Combine(outDir, "sugerowane-roi.json");
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(suggestedRoi, options));
            report.Line($"Zapisano sugerowane obszary (ROI) do wklejenia w config.json: {Path.GetFullPath(jsonPath)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Line($"Nie udało się zapisać sugerowane-roi.json: {ex.Message}");
        }
    }

    private static object? RoiOrNull(RectInt? r) =>
        r is { } v ? new { x = v.X, y = v.Y, width = v.Width, height = v.Height } : null;

    /// <summary>Zmierzony obszar liczby procent z profilu (<c>idle.progressRoi</c>), albo <see langword="null"/>, gdy profil go nie ma albo sekcja <c>idle</c> jest wyłączona.</summary>
    private static RectInt? ProfileProgressRoi(ScreenProfile profile)
    {
        IdleWatchProfile? idle = profile.Idle;
        return idle is { Enabled: true } && !idle.ProgressRoi.IsEmpty ? idle.ProgressRoi : null;
    }

    private static async Task<string> DescribeConfiguredProgressAsync(
        RgbaImage full, ScreenProfile profile, WindowsOcrEngine engine, CancellationToken ct)
    {
        IdleWatchProfile? idle = profile.Idle;
        if (idle is null || !idle.Enabled || idle.ProgressRoi.IsEmpty)
        {
            return $"pole postępu z profilu: profil \"{profile.Id}\" nie ma \"idle.progressRoi\" — nie ma czego sprawdzić.";
        }

        if (idle.Mode != ProgressMode.Number)
        {
            return $"pole postępu z profilu (\"idle.progressRoi\" {ScreenAnchors.FormatRoi(idle.ProgressRoi)}): tryb \"{idle.Mode}\" — postęp liczony z koloru paska, nie z OCR; tego wiersza to nie dotyczy.";
        }

        RectInt roi = idle.ProgressRoi.Clamp(full.Width, full.Height);
        if (roi.IsEmpty)
        {
            return $"pole postępu z profilu (\"idle.progressRoi\" {ScreenAnchors.FormatRoi(idle.ProgressRoi)}): leży POZA tym zrzutem ({full.Width}x{full.Height}) — po przycięciu zostaje pusty prostokąt, więc postoju NIE DA SIĘ wykryć. Popraw obszar w config.json.";
        }

        ProgressDetectionResult result = await new ProgressNumberReader(idle, profile.DarkTheme, engine)
            .ReadAsync(full.Crop(roi), ct)
            .ConfigureAwait(false);

        return result.Percent is { } pct
            ? $"pole postępu z profilu (\"idle.progressRoi\" {ScreenAnchors.FormatRoi(idle.ProgressRoi)}): odczyt {pct} % (tekst OCR: „{result.RawText}”)"
            : $"pole postępu z profilu (\"idle.progressRoi\" {ScreenAnchors.FormatRoi(idle.ProgressRoi)}): OCR nie odczytał liczby (tekst OCR: „{result.RawText}”) — postój z tego obszaru się nie policzy.";
    }

    private static IEnumerable<string> DescribeBarAndProgramState(RgbaImage full, ScreenProfile profile)
    {
        if (profile.Idle is { Enabled: true, ProgressBar: { Enabled: true } bar })
        {
            RectInt area = ProgressBarReader.RequiredArea(bar);
            yield return area.Clamp(full.Width, full.Height) != area
                ? $"pasek postępu z profilu (\"idle.progressBar\" {ScreenAnchors.FormatRoi(bar.TrackRoi)}): leży poza tym zrzutem ({full.Width}x{full.Height})."
                : $"pasek postępu z profilu (\"idle.progressBar\" {ScreenAnchors.FormatRoi(bar.TrackRoi)}): {ProgressBarReader.Read(full.Crop(area), bar).Description} (w programie to źródło główne procentu; OCR tylko, gdy toru nie widać).";
        }

        if (profile.ProgramState is { Enabled: true } state)
        {
            yield return $"stan programu z profilu (\"programState\" {ScreenAnchors.FormatRoi(state.BoxRoi)}): {ProgramStateDetector.AnalyzeFrame(full, state).Description}.";
        }
    }

    private static string DescribeProgressReportLine(MachineAnchors a)
    {
        if (a.ProgressRoi is not { } roi)
        {
            return "pole postępu (kotwica): nie znaleziono (żadna z etykiet profilu nie została odczytana przez OCR na tym zrzucie).";
        }

        if (a.ProgressRoiIsGuess)
        {
            return $"pole postępu (kotwica): etykieta znaleziona, ale OCR nie odczytał obok niej wartości — "
                + $"obszar {ScreenAnchors.FormatRoi(roi)} jest ZGADNIĘTY („wartość pewnie pojawi się tutaj”), "
                + $"odczyt z niego: „{a.ProgressRawText}”.";
        }

        return a.ProgressPercent is { } pct
            ? $"pole postępu (kotwica): znaleziono {ScreenAnchors.FormatRoi(roi)}, odczyt: {pct} %"
            : $"pole postępu (kotwica): znaleziono {ScreenAnchors.FormatRoi(roi)}, ale OCR nie odczytał liczby: „{a.ProgressRawText}”";
    }

    private static string DescribeFileNameReportLine(MachineAnchors a) =>
        a.FileNameRoi is { } roi
            ? $"pole nazwy pliku: znaleziono {ScreenAnchors.FormatRoi(roi)} (treść: „{a.FileNameRawText}”)"
            : "pole nazwy pliku: nie znaleziono (ani etykieta \"File Name\", ani żadna nazwa \"*.nc\" nie "
                + "została odczytana przez OCR na tym zrzucie).";
}
