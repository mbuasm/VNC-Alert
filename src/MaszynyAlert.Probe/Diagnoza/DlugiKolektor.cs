using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>
/// Tryb długi <c>probe.exe diagnoza --tryb dlugi</c>: uruchamia <see cref="MachineCollector"/> dla każdej wybranej maszyny
/// i co <see cref="DlugiOptions.PodsumowanieCoSek"/> (oraz przy zmianie dnia) zapisuje <c>out/dlugi/podsumowanie.json</c>
/// i <c>podsumowanie-yyyy-MM-dd.json</c> (status, klatki, zmiany, epizody per maszyna). Bez nasłuchu UDP (bus) — kolektor
/// nie może przeszkadzać zainstalowanemu VNC Alert. Łagodne zakończenie przez Ctrl+C, pasujące żądanie
/// <c>stop-request.json</c> albo <c>--seconds</c> domyka epizody i zapisuje końcowe podsumowanie.
/// </summary>
public static class DlugiKolektor
{
    public const string StopRequestFileName = "stop-request.json";
    public static async Task<int> RunAsync(
        MachineListResult listResult,
        DlugiOptions options,
        string outDir,
        GlobalOptions global,
        ProbeSession session,
        Report report,
        IReadOnlyList<IOcrEngine> engines,
        IReadOnlyList<VncAlertConflict.Skip> skips,
        IReadOnlyDictionary<string, string?> initialFailures,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listResult);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(skips);

        string dlugiDir = Path.Combine(outDir, "dlugi");
        Directory.CreateDirectory(dlugiDir);
        IReadOnlyDictionary<string, MachineModeStore.Entry> modes = MachineModeStore.Load(outDir);

        report.Line("=== TRYB DŁUGI: kolektor epizodów (działa aż do zatrzymania) ===");
        report.Line($"  Katalog: {Path.GetFullPath(dlugiDir)}; klatka okresowa co {options.OkresowaSek} s, zmiana nie częściej niż co {options.MinOdstepZmianySek} s, " +
            $"odczyt co >= {options.MinOdczytSek} s, limit {options.LimitBytes / (1024 * 1024)} MB/maszynę, podsumowanie co {options.PodsumowanieCoSek} s" +
            (options.TotalSeconds is { } t ? $", koniec po {t} s (--seconds)" : ", bez końca (Ctrl+C / Zatrzymaj-Kolektor.cmd)") + ".");
        report.Line($"  Tryby maszyn ({MachineModeStore.FileName}: {(modes.Count == 0 ? "brak pliku — domyślnie trwale, 520 pomijane" : $"{modes.Count} wpisów")}):");

        var collectors = new List<MachineCollector>();
        foreach (MachineConfig machine in listResult.Selected)
        {
            if (skips.FirstOrDefault(x => x.Id == machine.Id) is { } skip)
            {
                report.Line($"    {machine.Name} [{machine.Id}] — POMINIĘTA: {skip.Powod}");
                continue;
            }

            string mode = MachineModeStore.Resolve(machine, modes);
            string? password = session.HasKnownPassword(machine.Id) ? session.KnownPassword(machine.Id) : global.Password;
            report.Line($"    {machine.Name} [{machine.Id}] {machine.Host}:{machine.Port} — {mode}" +
                (modes.TryGetValue(machine.Id, out MachineModeStore.Entry? e) ? $" ({e.Powod})" : string.Empty));
            collectors.Add(new MachineCollector(machine, listResult.Config, Path.Combine(dlugiDir, SafeDirName(machine.Id)), options, mode,
                password, machine.Username ?? global.Username, report, engines, initialFailures.GetValueOrDefault(machine.Id)));
        }

        report.Line();
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.TotalSeconds is { } seconds)
        {
            runCts.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        DateTimeOffset started = DateTimeOffset.Now;
        if (!WriteSummary(collectors, dlugiDir, options, started, report, skips, final: false))
            return ProbeExitCodes.Error;
        List<Task> tasks = collectors.Select(c => c.RunAsync(runCts.Token)).ToList();
        Task summary = SummaryLoopAsync(collectors, dlugiDir, options, started, report, skips, runCts);
        Task stopRequest = StopRequestLoopAsync(dlugiDir, started, runCts);
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // koniec
        }
        finally
        {
            // Także gdy wszystkie maszyny pominięto lub zakończyły pracę po odmowie logowania.
            runCts.Cancel();
        }

        try
        {
            await Task.WhenAll(summary, stopRequest).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // j.w.
        }

        bool summarySaved = WriteSummary(collectors, dlugiDir, options, started, report, skips, final: true);
        foreach (MachineCollector c in collectors)
        {
            c.Dispose();
        }

        int connected = collectors.Count(c => c.Stats.ConnectedAt is not null);
        if (!summarySaved || collectors.Any(c => c.Stats.OverLimit || c.Stats.WriteFailed)) return ProbeExitCodes.Error;
        // Maszyny pominięte świadomie (konflikt z VNC Alert) nie obniżają kodu wyjścia — nie były wybrane do zbierania.
        return collectors.Count == 0 || connected == 0 ? ProbeExitCodes.NoneConnected : connected == collectors.Count ? ProbeExitCodes.AllConnected : ProbeExitCodes.SomeConnected;
    }

    private static async Task SummaryLoopAsync(List<MachineCollector> collectors, string dir, DlugiOptions options, DateTimeOffset started, Report report, IReadOnlyList<VncAlertConflict.Skip> skips, CancellationTokenSource runCts)
    {
        CancellationToken ct = runCts.Token;
        string day = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.PodsumowanieCoSek), ct).ConfigureAwait(false);
            if (!WriteSummary(collectors, dir, options, started, report, skips, final: false))
            {
                foreach (MachineCollector collector in collectors)
                {
                    collector.Stats.WriteFailed = true;
                    collector.Stats.Reason = "Błąd zapisu podsumowania — zakończono zbieranie. Wynik jest niepełny.";
                }
                runCts.Cancel();
                return;
            }
            string today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            if (today != day)
            {
                day = today;
                report.Line($"{DateTimeOffset.Now:HH:mm:ss} nowy dzień {day} — nowe podkatalogi i dziennik.");
            }
        }
    }

    private static async Task StopRequestLoopAsync(string dir, DateTimeOffset started, CancellationTokenSource runCts)
    {
        string requestPath = Path.Combine(dir, StopRequestFileName);
        string startedUtc = started.UtcDateTime.ToString("O");
        while (!runCts.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(requestPath))
                {
                    using JsonDocument request = JsonDocument.Parse(File.ReadAllText(requestPath, Encoding.UTF8));
                    if (request.RootElement.TryGetProperty("pid", out JsonElement pid) && pid.TryGetInt32(out int value) && value == Environment.ProcessId &&
                        request.RootElement.TryGetProperty("startedUtc", out JsonElement stamp) && stamp.ValueKind == JsonValueKind.String && stamp.GetString() == startedUtc)
                    {
                        runCts.Cancel();
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // Niepełny zapis żądania lub obcy format nie może zatrzymać kolektora.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), runCts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Zapis <c>podsumowanie.json</c> i kopii dziennej; nigdy nie rzuca.</summary>
    public static bool WriteSummary(IReadOnlyList<MachineCollector> collectors, string dir, DlugiOptions options, DateTimeOffset started, Report report, IReadOnlyList<VncAlertConflict.Skip> skips, bool final)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            var doc = new
            {
                probe = VersionInfo.InformationalVersion,
                tryb = "dlugi",
                pid = Environment.ProcessId,
                startedUtc = started.UtcDateTime.ToString("O"),
                heartbeatUtc = now.UtcDateTime.ToString("O"),
                start = started.ToString("yyyy-MM-dd HH:mm:ss"),
                zapis = now.ToString("yyyy-MM-dd HH:mm:ss"),
                dzialaSek = Math.Round((now - started).TotalSeconds),
                zakonczony = final,
                pominiete = skips.ToDictionary(x => x.Id, x => x.Powod, StringComparer.Ordinal),
                maszyny = collectors.ToDictionary(c => c.Machine.Id, c => new
                {
                    nazwa = c.Machine.Name,
                    host = c.Machine.Host,
                    port = c.Machine.Port,
                    tryb = c.Stats.Tryb,
                    status = c.Stats.Status,
                    powod = c.Stats.Reason,
                    serwer = c.Stats.Server,
                    polaczonoO = c.Stats.ConnectedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    polaczen = c.Stats.Connections,
                    zerwan = c.Stats.Disconnects,
                    odczytow = c.Stats.FramesRead,
                    zmian = c.Stats.Changes,
                    zapisanychOkresowych = c.Stats.PeriodicSaved,
                    zapisanychZmian = c.Stats.ChangesSaved,
                    epizodow = new { razem = c.Stats.EpisodesClosed, czerwien = c.Stats.EpisodesRed, zolc = c.Stats.EpisodesYellow, ostatni = c.Stats.LastEpisode },
                    ocr = new { odczytow = c.Stats.OcrRuns, ostatni = c.Stats.LastOcr },
                    dyskMb = Math.Round(c.Stats.DiskBytes / 1024.0 / 1024.0, 1),
                    limitPrzekroczony = c.Stats.OverLimit,
                    bladZapisu = c.Stats.WriteFailed,
                    ostatniBlad = c.Stats.LastError,
                }, StringComparer.Ordinal),
            };
            string json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            WriteSummaryFile(Path.Combine(dir, "podsumowanie.json"), json);
            WriteSummaryFile(Path.Combine(dir, $"podsumowanie-{now:yyyy-MM-dd}.json"), json);
            if (final)
            {
                report.Line($"Podsumowanie kolektora: {Path.GetFullPath(Path.Combine(dir, "podsumowanie.json"))}");
                foreach (MachineCollector c in collectors)
                {
                    report.Line($"  {c.Machine.Name}: {c.Stats.Status}, tryb {c.Stats.Tryb}, odczytów {c.Stats.FramesRead}, zmian {c.Stats.Changes}, epizodów {c.Stats.EpisodesClosed} " +
                        $"(czerwień {c.Stats.EpisodesRed}, żółć {c.Stats.EpisodesYellow}), zerwań {c.Stats.Disconnects}, dysk {c.Stats.DiskBytes / 1024 / 1024} MB." +
                        (c.Stats.Reason is { } reason ? $" {reason}" : string.Empty));
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Line($"Nie udało się zapisać podsumowania kolektora: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ile razy (co <see cref="SummaryRetryDelay"/>) ponawiać podmianę podsumowania zajętego przez czytelnika.</summary>
    private const int SummaryWriteAttempts = 20;

    private static readonly TimeSpan SummaryRetryDelay = TimeSpan.FromMilliseconds(100);

    private static void WriteSummaryFile(string path, string json)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < SummaryWriteAttempts)
            {
                Thread.Sleep(SummaryRetryDelay);
            }
        }
    }

    private static string SafeDirName(string id) =>
        string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
