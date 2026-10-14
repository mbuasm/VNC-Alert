using System.Runtime.Versioning;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Windows;

namespace MaszynyAlert.Probe.Commands;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class DiagnozaCommand : ICommand
{
    public const string RunnerPasswordVariable = "VNCALERT_DIAGNOZA_PASSWORD_580";

    private static readonly TimeSpan Phase0ConnectTimeout = TimeSpan.FromSeconds(5);

    public Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct) =>
        RunCoreAsync(args, global, report, session, ocrEngines: null, passwordStore: null, ct);

    /// <summary>
    /// Logika komendy; <paramref name="ocrEngines"/> = <see langword="null"/> oznacza „utwórz silniki Windows OCR
    /// (pl, en-US — te, które są)”, pusta lista = bez OCR, cokolwiek innego = silniki podstawione przez test.
    /// <paramref name="passwordStore"/> = <see langword="null"/> → magazyn DPAPI <see cref="KolektorPasswordStore"/> (gdy
    /// DPAPI działa); test podstawia własny. Hasła z magazynu trafiają do <see cref="ProbeSession"/> przed fazą 0 tylko
    /// dla maszyn, dla których nie podano <c>--password</c>.
    /// </summary>
    public static async Task<int> RunCoreAsync(
        IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, IReadOnlyList<IOcrEngine>? ocrEngines,
        ISecretStore? passwordStore, CancellationToken ct)
    {
        string tryb = (ArgsUtil.FindOption(args, "--tryb") ?? "szybki").ToLowerInvariant();
        if (tryb is not ("szybki" or "dlugi"))
        {
            throw new ProbeUsageException("Opcja --tryb przyjmuje \"szybki\" (domyślnie, jeden przebieg) albo \"dlugi\" (kolektor epizodów bez końca).");
        }

        session.IsAutoRun = true;
        GlobalOptions effective = global.HasMachineFilter ? global : global with { All = true };
        MachineListResult listResult = MachineList.Load(effective);
        string? runnerPassword = Environment.GetEnvironmentVariable(RunnerPasswordVariable);
        if (!string.IsNullOrEmpty(runnerPassword) && listResult.Selected.Any(m => m.Id == "amm580"))
        {
            session.RememberPassword("amm580", runnerPassword);
        }
        LoadStoredPasswords(listResult, global, session, passwordStore, report);

        if (tryb == "dlugi")
        {
            DlugiOptions dlugi = DlugiOptions.Parse(args);
            session.ZipRequested = false; // kolektor nigdy nie pakuje — robi to Zbierz-Wyniki.cmd
            report.Line("probe.exe diagnoza --tryb dlugi — kolektor epizodów (czerwień/żółć w całej klatce, zmiany obrazu, OCR) na dni/tygodnie.");
            report.Line($"  Lista maszyn: {listResult.SourcePath}; wybrane: {string.Join(", ", listResult.Selected.Select(m => m.Id))}");
            report.Line($"  Hasło z --password: {(global.Password is null ? "nie podano" : "podano (zamaskowane)")}; faza 0 pyta o hasła tylko bez --no-auth-prompt.");
            report.Line();
            IReadOnlyList<IOcrEngine> dlugiEngines = ocrEngines ?? (dlugi.NoOcr ? [] : CreateWindowsOcrEngines(report));
            string installedConfig = ArgsUtil.FindOption(args, "--vnc-alert-config") ?? VncAlertConflict.DefaultInstalledConfigPath;
            IReadOnlyList<VncAlertConflict.Skip> skips = VncAlertConflict.SkipsForLongMode(
                listResult.Selected, VncAlertConflict.WatchedByInstalledApp(installedConfig), ArgsUtil.HasFlag(args, "--z-520"));
            foreach (VncAlertConflict.Skip skip in skips)
            {
                report.Line($"{skip.Id}: pominięto w kolektorze — {skip.Powod}");
            }

            Dictionary<string, string?> failures = await Phase0Async(listResult, global, report, session, ct, skips.Select(x => x.Id).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
            return await DlugiKolektor.RunAsync(listResult, dlugi, global.OutDir, global, session, report, dlugiEngines, skips, failures, ct).ConfigureAwait(false);
        }

        DiagnozaOptions options = DiagnozaOptions.Parse(args);
        session.ZipRequested = !options.NoZip;
        DiagnozaSchedule schedule = DiagnozaSchedule.For(options.TotalSeconds, options.FrameIntervalSeconds);
        string diagDir = Path.Combine(global.OutDir, "diagnoza");
        Directory.CreateDirectory(diagDir);

        DateTimeOffset started = DateTimeOffset.Now;
        report.Line("probe.exe diagnoza — kompleksowa diagnostyka maszyn na jeden przebieg.");
        report.Line($"  Lista maszyn: {listResult.SourcePath}");
        report.Line($"  Wybrane maszyny ({listResult.Selected.Count}): {string.Join(", ", listResult.Selected.Select(m => $"{m.Name} [{m.Id}] {m.Host}:{m.Port}"))}");
        report.Line($"  Czas przebiegu: {options.TotalSeconds} s ({options.TotalSeconds / 60.0:0.#} min), klatka co {options.FrameIntervalSeconds} s, " +
            $"test czerni w {string.Join("/", schedule.BlackTestOffsetsSeconds)} s, rozłączenie po {schedule.DisconnectAtSeconds} s na {schedule.DisconnectForSeconds} s, " +
            $"shared-test po {schedule.SharedTestAtSeconds} s, pomiar aktualizacji po {schedule.RateMeasureAtSeconds} s przez {schedule.RateMeasureForSeconds} s.");
        report.Line($"  Wyniki: {Path.GetFullPath(diagDir)}\\<id maszyny>\\ (dziennik.txt, klatki\\, pomiary.jsonl, ocr\\), podsumowanie w {Path.GetFullPath(diagDir)}\\podsumowanie.txt");
        report.Line($"  Hasło z --password: {(global.Password is null ? "nie podano" : "podano (zamaskowane)")}; " +
            (global.NoAuthPrompt ? "bez pytania o hasło (--no-auth-prompt)." : "jeśli maszyna zapyta — pytanie pojawi się TERAZ, w fazie 0, potem już nie."));
        report.Line();

        IReadOnlyList<IOcrEngine> engines = ocrEngines ?? (options.NoOcr ? [] : CreateWindowsOcrEngines(report));
        report.Line(engines.Count == 0
            ? "OCR: wyłączony/niedostępny — klatki i pomiary koloru bez odczytu tekstu."
            : $"OCR: {string.Join(", ", engines.Select(e => e.LanguageTag ?? e.Name))}.");
        report.Line();

        Dictionary<string, string?> initialSkip = await Phase0Async(listResult, global, report, session, ct).ConfigureAwait(false);
        report.Line($"=== FAZA 1: diagnostyka równoległa, {options.TotalSeconds} s — nie zamykaj okna; Ctrl+C kończy wcześniej i zapisuje podsumowanie ===");
        report.Line();

        var diagnostics = new List<MachineDiagnostic>();
        foreach (MachineConfig machine in listResult.Selected)
        {
            string? password = session.HasKnownPassword(machine.Id) ? session.KnownPassword(machine.Id) : global.Password;
            diagnostics.Add(new MachineDiagnostic(
                machine,
                listResult.Config,
                Path.Combine(diagDir, SafeDirName(machine.Id)),
                schedule,
                password,
                machine.Username ?? global.Username,
                report,
                session.Summary.Machine(machine.Id),
                engines,
                initialSkip.GetValueOrDefault(machine.Id)));
        }

        List<Task> tasks = diagnostics.Select(d => d.RunAsync(ct)).ToList();
        // Pętla statusu kończy się razem z maszynami (własny token) — inaczej czekałaby na Ctrl+C w nieskończoność.
        using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task status = StatusLoopAsync(diagnostics, report, statusCts.Token);
        bool cancelled = false;
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        statusCts.Cancel();
        try
        {
            await status.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // j.w.
        }

        foreach (MachineDiagnostic d in diagnostics)
        {
            await d.DisposeAsync().ConfigureAwait(false);
        }

        DateTimeOffset finished = DateTimeOffset.Now;
        List<MachineDiagnosticResult> results = diagnostics.Select(d => d.Result).ToList();
        if (cancelled)
        {
            report.Line("Przerwano (Ctrl+C) — zapisuję podsumowanie z tego, co zebrano.");
        }

        report.Line();
        IReadOnlyList<string> text = DiagnozaSummary.RenderText(results, listResult.Config.Profiles, started, finished);
        foreach (string line in text)
        {
            report.Line(line);
        }

        try
        {
            string txtPath = Path.Combine(diagDir, "podsumowanie.txt");
            string jsonPath = Path.Combine(diagDir, "podsumowanie.json");
            File.WriteAllLines(txtPath, text, new UTF8Encoding(true));
            File.WriteAllText(jsonPath, DiagnozaSummary.RenderJson(results, listResult.Config.Profiles, started, finished, schedule), new UTF8Encoding(false));
            report.Line();
            report.Line($"Zapisano: {Path.GetFullPath(txtPath)} i {Path.GetFileName(jsonPath)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Line($"Nie udało się zapisać podsumowania diagnozy: {ex.Message}");
        }

        try
        {
            Dictionary<string, MachineModeStore.Entry> modes = MachineModeStore.FromResults(results);
            MachineModeStore.Save(global.OutDir, modes);
            report.Line($"Tryby maszyn dla kolektora ({MachineModeStore.FileName}): " +
                (modes.Count == 0 ? "brak decyzji (domyślnie trwale)" : string.Join(", ", modes.Select(m => $"{m.Key}={m.Value.Tryb}"))) + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Line($"Nie udało się zapisać {MachineModeStore.FileName}: {ex.Message}");
        }

        SummaryPrinter.PrintAndSave(session.Summary, report, global.OutDir);
        return DiagnozaSummary.ExitCode(results);
    }

    /// <summary>
    /// Hasła z magazynu kolektora (<see cref="KolektorPasswordStore"/>): dla każdej wybranej maszyny bez <c>--password</c>
    /// zapamiętane w sesji, więc faza 0 i kolektor używają ich bez pytania. Bez DPAPI w tej sesji — jedna linia w raporcie, dalej bez haseł.
    /// </summary>
    private static void LoadStoredPasswords(MachineListResult listResult, GlobalOptions global, ProbeSession session, ISecretStore? store, Report report)
    {
        if (global.Password is not null)
        {
            return;
        }

        try
        {
            if (store is null)
            {
                if (!KolektorPasswordStore.IsDpapiAvailable(out string? error))
                {
                    report.Line($"Magazyn haseł kolektora: DPAPI niedostępne w tej sesji ({error}) — maszyny wymagające hasła zostaną oznaczone \"wymaga hasła\".");
                    return;
                }

                store = KolektorPasswordStore.Open();
            }

            var found = new List<string>();
            foreach (MachineConfig machine in listResult.Selected)
            {
                if (!session.HasKnownPassword(machine.Id) && store.Get(machine.Id) is { Length: > 0 } password)
                {
                    session.RememberPassword(machine.Id, password);
                    found.Add(machine.Id);
                }
            }

            report.Line(found.Count == 0
                ? "Magazyn haseł kolektora: brak zapisanych haseł dla wybranych maszyn (probe.exe haslo zapisz <id>)."
                : $"Magazyn haseł kolektora: hasła dla {string.Join(", ", found)} (treści nie pokazuję).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            report.Line($"Magazyn haseł kolektora: nie udało się odczytać ({ex.Message}) — dalej bez zapisanych haseł.");
        }
    }

    /// <summary>Faza 0: sekwencyjny uścisk dłoni z każdą maszyną — tu padają pytania o hasło (ConnectionHelper), nigdzie indziej.</summary>
    private static async Task<Dictionary<string, string?>> Phase0Async(
        MachineListResult listResult, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct, HashSet<string>? skipIds = null)
    {
        report.Line("=== FAZA 0: szybki uścisk dłoni z każdą maszyną (po kolei, tu ewentualne pytania o hasło) ===");
        var initialSkip = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (MachineConfig machine in listResult.Selected)
        {
            ct.ThrowIfCancellationRequested();
            if (VncAlertConflict.Is520(machine) || (skipIds is not null && skipIds.Contains(machine.Id)))
            {
                report.Line($"{machine.Name}: pominięta (nie łączę się — patrz wyżej).");
                continue;
            }

            MachineSummary row = session.Summary.Machine(machine.Id);
            row.Name = machine.Name;
            if (!listResult.Config.Profiles.ContainsKey(machine.Profile))
            {
                report.Line($"{machine.Name}: profil \"{machine.Profile}\" nie istnieje w konfiguracji — tylko sieć.");
                initialSkip[machine.Id] = null;
                continue;
            }

            ConnectOutcome outcome = await session.Connections
                .ConnectAsync(machine.Id, machine.Name, machine.Host, machine.Port, Phase0ConnectTimeout, ct, machine.Username)
                .ConfigureAwait(false);
            if (outcome.Success)
            {
                row.ApplyServerInfo(outcome.Info!, DateTimeOffset.Now);
                await outcome.Client!.DisposeAsync().ConfigureAwait(false);
                report.Line($"{machine.Name}: OK — RFB {outcome.Info!.ProtocolVersion}, {outcome.Info.UsedSecurityType}, {outcome.Info.Width}x{outcome.Info.Height}, \"{outcome.Info.DesktopName}\".");
                initialSkip[machine.Id] = null;
            }
            else
            {
                row.ApplySkip(outcome.SkipReason!);
                report.Line($"{machine.Name}: {InfoCommand.SkipStatus(outcome.SkipReason!)} — {outcome.SkipReason}" +
                    (WatchCommand.IsRetryableSkip(outcome.SkipReason!) ? " (będę ponawiać przez cały przebieg)" : " (nie ponawiam logowania; sprawdzam tylko port)"));
                initialSkip[machine.Id] = outcome.SkipReason;
            }
        }

        report.Line();
        return initialSkip;
    }

    /// <summary>Silniki Windows OCR dla „pl” i „en-US” — tylko te, które naprawdę są (bez duplikatów, gdy brak jednego z nich silnik wraca z drugim).</summary>
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static IReadOnlyList<IOcrEngine> CreateWindowsOcrEngines(Report report)
    {
        var engines = new List<IOcrEngine>();
        try
        {
            IReadOnlyList<string> available = WindowsOcrEngine.AvailableLanguages();
            report.Line($"Dostępne języki OCR (Windows.Media.Ocr): {(available.Count == 0 ? "(brak)" : string.Join(", ", available))}");
            foreach (string lang in new[] { "en-US", "pl" })
            {
                var engine = new WindowsOcrEngine(lang);
                if (engine.IsAvailable && engines.All(e => !string.Equals(e.LanguageTag, engine.LanguageTag, StringComparison.OrdinalIgnoreCase)))
                {
                    engines.Add(engine);
                }
            }
        }
        catch (Exception ex)
        {
            report.Line($"OCR niedostępny ({ex.GetType().Name}: {ex.Message}) — kontynuuję bez OCR.");
        }

        return engines;
    }

    private static async Task StatusLoopAsync(IReadOnlyList<MachineDiagnostic> diagnostics, Report report, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                var parts = diagnostics.Select(d =>
                {
                    MachineDiagnosticResult r = d.Result;
                    return $"{r.Name}={r.Status}/{r.Frames.Count}kl" + (r.BlackFrames > 0 ? $"/{r.BlackFrames}cz" : string.Empty);
                });
                report.Line($"{DateTimeOffset.Now:HH:mm:ss}  {string.Join("  ", parts)}");
            }
        }
        catch (OperationCanceledException)
        {
            // normalne zakończenie
        }
    }

    private static string SafeDirName(string id) =>
        string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
