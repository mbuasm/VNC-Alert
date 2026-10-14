using System.Runtime.Versioning;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Vision;
using MaszynyAlert.Windows;

namespace MaszynyAlert.Probe.Commands;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WatchCommand : ICommand
{
    private const double RedRiseThresholdFraction = 0.003; // 0,3 punktu procentowego
    private const int ErrorSaveCooldownSeconds = 60;
    private const int MedianWindowSize = 30;

    private const double AlertWindowChangeThreshold = 0.03;
    private const int AlertWindowSaveCooldownSeconds = 5;

    private const double FileNameChangeThreshold = 0.03;
    private const int FileNameSaveCooldownSeconds = 10;

    /// <summary>Domyślny odstęp zrzutów okresowych w minutach — <c>auto</c> odwołuje się do tej stałej w komunikatach.</summary>
    internal const int DefaultIntervalMinutes = 15;

    private static readonly int[] ReconnectBackoffMs = [5000, 15000, 30000];

    public Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct) =>
        RunCoreAsync(args, global, report, session, ct);

    /// <summary>Logika komendy, wyodrębniona żeby <c>probe.exe auto</c> (Commands/AutoCommand.cs) mogła wykonać KROK 3 bez duplikowania kodu.</summary>
    public static async Task<int> RunCoreAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct)
    {
        int? seconds = ParseOptionalSeconds(args);
        int intervalMinutes = ParseIntervalMinutes(args);

        MachineListResult listResult = MachineList.Load(global);
        report.Line($"Lista maszyn: {listResult.SourcePath}");

        List<MachineConfig> enabled = listResult.Selected.ToList();
        report.Line(listResult.FilterActive
            ? $"Maszyny wybrane do obserwacji (--machines/--all): {enabled.Count} z {listResult.Config.Machines.Count}."
            : $"Maszyny włączone w konfiguracji: {enabled.Count} z {listResult.Config.Machines.Count}.");
        report.Line($"Zrzuty okresowe co {intervalMinutes} min (folder okresowe\\); okienko alertów obserwowane niezależnie od koloru (folder okno-alertow\\).");
        report.Line();

        var engine = new WindowsOcrEngine(listResult.Config.Ocr.Language);
        if (!engine.IsAvailable)
        {
            report.Line(
                "brak OCR na tym komputerze — obserwacja pola postępu i zmiany nazwy pliku pracy jest " +
                "wyłączona dla wszystkich maszyn (alarmy błędów po kolorze działają bez zmian).");
            report.Line();
        }

        var states = new List<MachineWatchState>();

        report.Line("Sprawdzam połączenia (jednorazowo, zanim zacznę obserwację)...");
        foreach (MachineConfig machine in enabled)
        {
            ct.ThrowIfCancellationRequested();

            MachineSummary summaryRow = session.Summary.Machine(machine.Id);
            summaryRow.Name = machine.Name;

            if (!listResult.Config.Profiles.TryGetValue(machine.Profile, out ScreenProfile? profile))
            {
                report.Line($"{machine.Name}: pominięto — profil \"{machine.Profile}\" nie istnieje w konfiguracji.");
                summaryRow.Status = MachineStatus.ConfigError;
                summaryRow.Reason = $"profil \"{machine.Profile}\" nie istnieje w konfiguracji";
                continue;
            }

            ConnectOutcome outcome = await session.Connections
                .ConnectAsync(machine.Id, machine.Name, machine.Host, machine.Port, TimeSpan.FromSeconds(5), ct, machine.Username)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                summaryRow.ApplySkip(outcome.SkipReason!);
                if (!IsRetryableSkip(outcome.SkipReason!))
                {
                    // Hasło/poświadczenia/nieobsługiwane zabezpieczenia — ponowne próby co kilka sekund niczego nie zmienią.
                    report.Line($"{machine.Name}: pominięto w watch — {outcome.SkipReason}");
                    continue;
                }

                report.Line($"{machine.Name}: nie odpowiada na starcie ({outcome.SkipReason}) — dołączam do obserwacji i będę ponawiać.");
                states.Add(new MachineWatchState
                {
                    Machine = machine,
                    Profile = profile,
                    SummaryRow = summaryRow,
                    Anchors = session.GetAnchors(machine.Id),
                    Connected = false,
                });
                continue;
            }

            summaryRow.ApplyServerInfo(outcome.Info!, DateTimeOffset.Now);
            await outcome.Client!.DisposeAsync().ConfigureAwait(false);
            states.Add(new MachineWatchState
            {
                Machine = machine,
                Profile = profile,
                SummaryRow = summaryRow,
                Anchors = session.GetAnchors(machine.Id),
            });
            report.Line($"{machine.Name}: OK, dołączam do obserwacji.");
        }

        report.Line();
        if (states.Count == 0)
        {
            report.Line("Żadna maszyna nie jest dostępna do obserwacji — kończę.");
            SummaryPrinter.PrintAndSave(session.Summary, report, global.OutDir);
            return ProbeExitCodes.FromSummary(session.Summary);
        }

        report.Line(
            $"Obserwuję {states.Count} maszyn(-y). Zakończ przez Ctrl+C" +
            (seconds is { } sVal ? $" (albo automatycznie po {sVal} s — podano --seconds)." : "."));
        report.Line();

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (seconds is { } secondsValue)
        {
            loopCts.CancelAfter(TimeSpan.FromSeconds(secondsValue));
        }

        List<Task> machineTasks = states
            .Select(state => WatchMachineLoopAsync(state, session, engine, report, global.OutDir, intervalMinutes, loopCts.Token))
            .ToList();
        Task statusTask = StatusLoopAsync(states, report, loopCts.Token);

        try
        {
            await Task.WhenAll(machineTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Spodziewane przy Ctrl+C / upływie --seconds.
        }

        try
        {
            await statusTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // j.w.
        }

        PrintSummary(states, report);
        SummaryPrinter.PrintAndSave(session.Summary, report, global.OutDir);
        return ProbeExitCodes.FromSummary(session.Summary);
    }

    /// <summary>
    /// Czy nieudane połączenie na starcie warto ponawiać w pętli obserwacji: tak dla milczącej maszyny
    /// (wyłączona, odłączona od sieci — może wstać), nie dla hasła/poświadczeń/nieobsługiwanych zabezpieczeń
    /// (każda kolejna próba skończy się tak samo, a przy RSA-AES mogłaby dodatkowo zablokować konto po stronie serwera).
    /// </summary>
    internal static bool IsRetryableSkip(string skipReason) =>
        InfoCommand.SkipStatus(skipReason) is MachineStatus.Unreachable or MachineStatus.Skipped;

    internal static int? ParseOptionalSeconds(IReadOnlyList<string> args)
    {
        string? text = ArgsUtil.FindOption(args, "--seconds");
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, out int seconds) || seconds <= 0)
        {
            throw new ProbeUsageException("Opcja --seconds musi być dodatnią liczbą całkowitą (sekundy).");
        }

        return seconds;
    }

    internal static int ParseIntervalMinutes(IReadOnlyList<string> args)
    {
        string? text = ArgsUtil.FindOption(args, "--interval-min");
        if (text is null)
        {
            return DefaultIntervalMinutes;
        }

        if (!int.TryParse(text, out int minutes) || minutes <= 0)
        {
            throw new ProbeUsageException("Opcja --interval-min musi być dodatnią liczbą całkowitą (minuty).");
        }

        return minutes;
    }

    private static void PrintSummary(List<MachineWatchState> states, Report report)
    {
        report.Line();
        report.Line("Podsumowanie obserwacji (watch):");
        int savedTotal = 0;
        foreach (MachineWatchState s in states)
        {
            bool connectedNow;
            int savedErr;
            int savedPeriodic;
            double lastFraction;
            int windowChanges;
            lock (s)
            {
                connectedNow = s.Connected;
                savedErr = s.SavedErrorCount;
                savedPeriodic = s.SavedPeriodicCount;
                lastFraction = s.LastFullFraction;
                windowChanges = s.AlertWindowChangeCount;

                string baseText = windowChanges > 0
                    ? $"zmian: {windowChanges}" + (s.AlertWindowTextColors.Count > 0 ? $", kolory tekstu: {string.Join(", ", s.AlertWindowTextColors)}" : string.Empty)
                    : "bez zmian";
                s.SummaryRow.AlertWindow = s.TruncatedRedTextCount > 0
                    ? $"{baseText}, ucięte: {s.TruncatedRedTextCount}"
                    : baseText;
            }

            savedTotal += s.SummaryRow.Saved;
            report.Line(
                $"  {s.Machine.Name}: połączona na koniec: {(connectedNow ? "tak" : "nie")}, " +
                $"zrzutów błędu: {savedErr}, okresowych: {savedPeriodic}, zmian okienka alertów: {windowChanges}, " +
                $"ostatni udział czerwieni (cała klatka): {Fmt.Pct2(lastFraction)}.");
        }

        report.Line();
        report.Line($"Razem zapisano {savedTotal} plików (wszystkie foldery: bledy, okno-alertow, okresowe, nowa-praca).");
    }

    private static async Task StatusLoopAsync(List<MachineWatchState> states, Report report, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                PrintStatusLine(states, report);
            }
        }
        catch (OperationCanceledException)
        {
            // normalne zakończenie
        }
    }

    private static void PrintStatusLine(List<MachineWatchState> states, Report report)
    {
        int connectedCount = 0;
        int savedTotal = 0;
        MachineConfig? redMachine = null;
        double redFraction = double.NegativeInfinity;
        var progressParts = new List<string>();

        foreach (MachineWatchState s in states)
        {
            lock (s)
            {
                if (s.Connected)
                {
                    connectedCount++;
                }

                savedTotal += s.SummaryRow.Saved;
                if (s.LastFullFraction > redFraction)
                {
                    redFraction = s.LastFullFraction;
                    redMachine = s.Machine;
                }

                if (s.LastProgressPercent is { } pct)
                {
                    progressParts.Add($"{s.Machine.Name}={pct}%");
                }
            }
        }

        string redPart = redMachine is not null ? $"{redMachine.Name} ({Fmt.Pct1(redFraction)})" : "-";
        string progressPart = progressParts.Count > 0 ? $"  postęp: {string.Join(", ", progressParts)}" : string.Empty;
        report.Line($"{DateTimeOffset.Now:HH:mm:ss}  połączone: {connectedCount}/{states.Count}  czerwień: {redPart}  zapisów: {savedTotal}{progressPart}");
    }

    private static async Task WatchMachineLoopAsync(
        MachineWatchState state, ProbeSession session, WindowsOcrEngine engine, Report report, string outDir, int intervalMinutes, CancellationToken ct)
    {
        RfbClient? client = null;
        RfbServerInfo? info = null;
        int backoffIndex = 0;
        bool wasOffline = false;
        bool firstFrame = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (client is null)
                {
                    var options = new RfbClientOptions
                    {
                        Host = state.Machine.Host,
                        Port = state.Machine.Port,
                        Password = session.KnownPassword(state.Machine.Id),
                        Username = state.Machine.Username,
                        Shared = true,
                        ConnectTimeout = TimeSpan.FromSeconds(5),
                    };

                    var candidate = new RfbClient(options);
                    try
                    {
                        info = await candidate.ConnectAsync(ct).ConfigureAwait(false);
                        client = candidate;
                        backoffIndex = 0;
                        lock (state)
                        {
                            state.Connected = true;
                            state.SummaryRow.ApplyServerInfo(info, DateTimeOffset.Now);
                        }

                        if (wasOffline)
                        {
                            report.Line($"{state.Machine.Name}: połączono ponownie.");
                            wasOffline = false;
                        }
                    }
                    catch (RfbException ex)
                    {
                        await candidate.DisposeAsync().ConfigureAwait(false);
                        lock (state)
                        {
                            state.Connected = false;
                            state.SummaryRow.ApplySkip(ConnectionHelper.BuildSkipReason(ex));
                        }

                        int delayMs = ReconnectBackoffMs[Math.Min(backoffIndex, ReconnectBackoffMs.Length - 1)];
                        if (!wasOffline)
                        {
                            report.Line($"{state.Machine.Name}: brak połączenia ({ex.Message}) — ponawiam za {delayMs / 1000} s.");
                            wasOffline = true;
                        }

                        backoffIndex++;
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                try
                {
                    var fullRect = new RectInt(0, 0, info!.Width, info.Height);
                    FramebufferUpdate update = await client.UpdateAsync(fullRect, incremental: false, ct).ConfigureAwait(false);
                    RectInt clamped = fullRect.Clamp(client.Framebuffer.Width, client.Framebuffer.Height);
                    if (clamped.IsEmpty)
                    {
                        throw new RfbScreenResizedException(client.Framebuffer.Width, client.Framebuffer.Height);
                    }

                    RgbaImage frame = client.Framebuffer.Crop(clamped);
                    lock (state)
                    {
                        state.SummaryRow.Frames++;
                        state.SummaryRow.NoteEncodings(update.EncodingsUsed);
                    }

                    if (frame.Width != info.Width || frame.Height != info.Height)
                    {
                        // DesktopSize w trakcie: od tej klatki obserwujemy nowy rozmiar (profil może już nie pasować — raport to pokaże).
                        report.Line($"{state.Machine.Name}: serwer zmienił rozmiar ekranu {info.Width}x{info.Height} → {frame.Width}x{frame.Height} — obserwuję dalej w nowym rozmiarze.");
                        info = client.ServerInfo is { } si ? si with { Width = frame.Width, Height = frame.Height } : info;
                        lock (state)
                        {
                            state.SummaryRow.Screen = $"{frame.Width}x{frame.Height}";
                        }
                    }

                    if (firstFrame)
                    {
                        firstFrame = false;
                        await EnsureAnchorsAsync(state, session, engine, frame, ct).ConfigureAwait(false);
                    }

                    await ProcessFrameAsync(state, session, engine, frame, report, outDir, intervalMinutes, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is RfbException or RfbScreenResizedException or ArgumentException)
                {
                    int delayMs = ex is RfbException
                        ? ReconnectBackoffMs[Math.Min(backoffIndex, ReconnectBackoffMs.Length - 1)]
                        : 1000;
                    report.Line(ex is RfbException
                        ? $"{state.Machine.Name}: utracono połączenie ({ex.Message}) — ponawiam za {delayMs / 1000} s."
                        : $"{state.Machine.Name}: zmiana rozmiaru ekranu ({ex.Message}) — łączę ponownie, żeby odczytać nowy rozmiar.");
                    lock (state)
                    {
                        state.Connected = false;
                        state.SummaryRow.AddDisconnect(DateTimeOffset.Now, ex.Message);
                    }

                    await client.DisposeAsync().ConfigureAwait(false);
                    client = null;
                    wasOffline = true;
                    firstFrame = true;
                    if (ex is RfbException)
                    {
                        backoffIndex++;
                    }

                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    // Nieoczekiwany wyjątek w obróbce klatki (np. OCR) — jedna maszyna nie może zatrzymać obserwacji pozostałych.
                    report.Line($"{state.Machine.Name}: nieoczekiwany błąd w obserwacji ({ex.GetType().Name}: {ex.Message}) — kontynuuję po 5 s.");
                    report.FileOnly($"szczegóły techniczne: {ex}");
                    lock (state)
                    {
                        state.SummaryRow.LastError = ex.Message;
                    }

                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normalne zakończenie (Ctrl+C / upływ --seconds)
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Kotwiczenie na pierwszej klatce (tylko gdy sesja jeszcze nie ma wyniku dla tej maszyny — czyli
    /// samodzielne <c>watch</c> bez wcześniejszego <c>ocr</c> w tym przebiegu) — patrz XML-doc klasy.
    /// </summary>
    private static async Task EnsureAnchorsAsync(MachineWatchState state, ProbeSession session, WindowsOcrEngine engine, RgbaImage frame, CancellationToken ct)
    {
        if (state.Anchors is not null || !engine.IsAvailable)
        {
            return;
        }

        MachineAnchors anchors = await ScreenAnchors.ComputeAsync(frame, engine, state.Profile, ct).ConfigureAwait(false);
        session.SetAnchors(state.Machine.Id, anchors);
        state.Anchors = anchors;

        lock (state)
        {
            state.SummaryRow.ProgressField = ScreenAnchors.DescribeProgressField(anchors);
            state.SummaryRow.FileNameField = ScreenAnchors.DescribeFileNameField(anchors);
        }
    }

    private static async Task ProcessFrameAsync(
        MachineWatchState state, ProbeSession session, WindowsOcrEngine engine, RgbaImage frame, Report report, string outDir, int intervalMinutes, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        ProcessRedRise(state, frame, report, outDir, now);
        ProcessAlertWindow(state, frame, report, outDir, now);
        ProcessPeriodicSnapshot(state, frame, report, outDir, intervalMinutes, now);
        await ProcessNewJobAsync(state, engine, frame, report, outDir, now, ct).ConfigureAwait(false);
        await ProcessProgressAsync(state, session, engine, frame, report, now, ct).ConfigureAwait(false);
    }

    // --- Błąd (wzrost czerwieni) ---------------------------

    private static void ProcessRedRise(MachineWatchState state, RgbaImage frame, Report report, string outDir, DateTimeOffset now)
    {
        double fullFraction = ImageOps.RedFraction(frame, state.Profile.Red);
        RedDetectionResult roiResult = new RedTextDetector(state.Profile).AnalyzeFullFrame(frame);

        double median = ComputeMedian(state);
        bool alert = fullFraction > median + RedRiseThresholdFraction || roiResult.RedFraction >= state.Profile.MinRedFraction;

        lock (state)
        {
            state.LastFullFraction = fullFraction;
            state.LastRoiFraction = roiResult.RedFraction;
            state.RecentFractions.Enqueue(fullFraction);
            while (state.RecentFractions.Count > MedianWindowSize)
            {
                state.RecentFractions.Dequeue();
            }
        }

        if (!alert)
        {
            return;
        }

        bool canSave;
        lock (state)
        {
            canSave = state.LastErrorSaveAt is null || (now - state.LastErrorSaveAt.Value).TotalSeconds >= ErrorSaveCooldownSeconds;
        }

        if (!canSave)
        {
            // Ta sama usterka trwa — nie zapisujemy kolejnego pliku częściej niż raz na 60 s, ale
            // operator widzi na żywo, że czerwień się utrzymuje (bez zaśmiecania raport.txt).
            report.ConsoleOnly($"{now:HH:mm:ss} {state.Machine.Name}: czerwień trwa ({Fmt.Pct2(fullFraction)}).");
            return;
        }

        string dir = Path.Combine(outDir, "bledy");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{state.Machine.Id}_{now:yyyyMMdd-HHmmss}.png");
        PngCodec.EncodeToFile(frame, path);

        // Czasem czerwony tekst błędu nie mieści się w całości w oknie komunikatów na maszynie — sprawdzamy
        // to TYLKO diagnostycznie (żeby operator/serwis wiedział), NIE poszerzamy ROI automatycznie.
        RectInt clampedRoi = state.Profile.StatusBarRoi.Clamp(frame.Width, frame.Height);
        bool truncated = IsRedTextTruncated(roiResult.RedBounds, clampedRoi.Width);

        lock (state)
        {
            state.LastErrorSaveAt = now;
            state.SavedErrorCount++;
            state.SummaryRow.Saved++;
            if (truncated)
            {
                state.TruncatedRedTextCount++;
            }
        }

        string line =
            $"{now:HH:mm:ss} {state.Machine.Name}: WYKRYTO CZERWIEŃ — cała klatka {Fmt.Pct2(fullFraction)} " +
            $"(mediana ostatnich odczytów {Fmt.Pct2(median)}), ROI profilu {Fmt.Pct2(roiResult.RedFraction)} " +
            $"(próg {Fmt.Pct2(state.Profile.MinRedFraction)}) — zapisano {path}";
        if (truncated)
        {
            line += " UWAGA: czerwony tekst dochodzi do krawędzi okienka — prawdopodobnie ucięty na ekranie maszyny.";
        }

        report.Line(line);
    }

    private static bool IsRedTextTruncated(RectInt? redBounds, int cropWidth) =>
        redBounds is { } b && (b.Right >= cropWidth - 2 || b.X <= 1);

    private static double ComputeMedian(MachineWatchState state)
    {
        lock (state)
        {
            if (state.RecentFractions.Count == 0)
            {
                return 0.0;
            }

            double[] sorted = state.RecentFractions.ToArray();
            Array.Sort(sorted);
            int n = sorted.Length;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
        }
    }

    // --- Okienko alertów, niezależnie od koloru --------------------------------------------

    private static void ProcessAlertWindow(MachineWatchState state, RgbaImage frame, Report report, string outDir, DateTimeOffset now)
    {
        RectInt roi = ResolveAlertWindowRoi(state).Clamp(frame.Width, frame.Height);
        if (roi.IsEmpty)
        {
            return;
        }

        RgbaImage crop = frame.Crop(roi);

        bool changed;
        bool canSave;
        lock (state)
        {
            changed = state.LastSavedAlertWindowCrop is null || FrameDiff.ChangedFraction(crop, state.LastSavedAlertWindowCrop) > AlertWindowChangeThreshold;
            canSave = state.LastAlertWindowSaveAt is null || (now - state.LastAlertWindowSaveAt.Value).TotalSeconds >= AlertWindowSaveCooldownSeconds;
        }

        if (!changed || !canSave)
        {
            return;
        }

        IReadOnlyList<ColorShare> shares = ColorSummary.Summarize(crop);
        string colors = ColorSummary.Describe(shares);
        string? textColor = ColorSummary.DominantTextColor(crop);
        double redShare = shares.FirstOrDefault(s => s.Name == "czerwony")?.Fraction ?? 0.0;

        // Czasem czerwony tekst błędu nie mieści się w całości w oknie komunikatów na maszynie — sprawdzamy
        // to TYLKO diagnostycznie (żeby operator/serwis wiedział), NIE poszerzamy ROI automatycznie.
        RedDetectionResult redResult = RedTextDetector.Analyze(crop, state.Profile.Red, state.Profile.MinRedFraction);
        bool truncated = IsRedTextTruncated(redResult.RedBounds, crop.Width);

        string dir = Path.Combine(outDir, "okno-alertow");
        Directory.CreateDirectory(dir);
        string stamp = now.ToString("yyyyMMdd-HHmmss");
        string fullPath = Path.Combine(dir, $"{state.Machine.Id}_{stamp}.png");
        string cropPath = Path.Combine(dir, $"{state.Machine.Id}_{stamp}_okno.png");
        PngCodec.EncodeToFile(frame, fullPath);
        PngCodec.EncodeToFile(crop, cropPath);

        lock (state)
        {
            state.LastSavedAlertWindowCrop = crop;
            state.LastAlertWindowSaveAt = now;
            state.AlertWindowChangeCount++;
            state.SummaryRow.Saved += 2;
            if (textColor is not null)
            {
                state.AlertWindowTextColors.Add(textColor);
            }

            if (truncated)
            {
                state.TruncatedRedTextCount++;
            }
        }

        string line =
            $"{now:HH:mm:ss} {state.Machine.Name}: okienko alertów zmieniło treść — kolory: {colors}; " +
            $"kolor tekstu: {textColor ?? "-"}; czerwień w okienku: {Fmt.Pct2(redShare)} — zapisano {fullPath}";
        if (truncated)
        {
            line += " UWAGA: czerwony tekst dochodzi do krawędzi okienka — prawdopodobnie ucięty na ekranie maszyny.";
        }

        report.Line(line);
    }

    /// <summary>Okienko alertów: kotwica (jeśli policzona), inaczej <c>StatusBarRoi</c> profilu (patrz XML-doc klasy).</summary>
    private static RectInt ResolveAlertWindowRoi(MachineWatchState state) =>
        state.Anchors?.AlertWindowRoi ?? state.Profile.StatusBarRoi;

    // --- Zrzuty okresowe ------------------------------

    private static void ProcessPeriodicSnapshot(MachineWatchState state, RgbaImage frame, Report report, string outDir, int intervalMinutes, DateTimeOffset now)
    {
        bool due;
        lock (state)
        {
            due = state.LastPeriodicSaveAt is null || (now - state.LastPeriodicSaveAt.Value).TotalMinutes >= intervalMinutes;
        }

        if (!due)
        {
            return;
        }

        string dir = Path.Combine(outDir, "okresowe");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{state.Machine.Id}_{now:yyyyMMdd-HHmmss}.png");
        PngCodec.EncodeToFile(frame, path);

        lock (state)
        {
            state.LastPeriodicSaveAt = now;
            state.SavedPeriodicCount++;
            state.SummaryRow.Saved++;
        }

        report.FileOnly($"{now:HH:mm:ss} {state.Machine.Name}: zrzut okresowy (co {intervalMinutes} min, stan odniesienia) zapisano — {path}");
    }

    // --- Zmiana nazwy pliku pracy = start kolejnej pracy ---------------------------------

    private static async Task ProcessNewJobAsync(MachineWatchState state, WindowsOcrEngine engine, RgbaImage frame, Report report, string outDir, DateTimeOffset now, CancellationToken ct)
    {
        RectInt? roiOpt = state.Anchors?.FileNameRoi;
        if (roiOpt is not { IsEmpty: false } roiRaw)
        {
            return;
        }

        RectInt roi = roiRaw.Clamp(frame.Width, frame.Height);
        if (roi.IsEmpty)
        {
            return;
        }

        RgbaImage crop = frame.Crop(roi);

        bool changed;
        bool canSave;
        lock (state)
        {
            changed = state.LastSavedFileNameCrop is null || FrameDiff.ChangedFraction(crop, state.LastSavedFileNameCrop) > FileNameChangeThreshold;
            canSave = state.LastFileNameSaveAt is null || (now - state.LastFileNameSaveAt.Value).TotalSeconds >= FileNameSaveCooldownSeconds;
        }

        if (!changed || !canSave)
        {
            return;
        }

        string? newName = null;
        if (engine.IsAvailable)
        {
            Gray8Image forOcr = ImageOps.PrepareTextForOcr(crop, marginPx: 8, scale: 2, invert: state.Profile.DarkTheme);
            OcrResult result = await engine.RecognizeAsync(forOcr, ct).ConfigureAwait(false);
            newName = result.Text;
        }

        string dir = Path.Combine(outDir, "nowa-praca");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{state.Machine.Id}_{now:yyyyMMdd-HHmmss}.png");
        PngCodec.EncodeToFile(frame, path);

        lock (state)
        {
            state.LastSavedFileNameCrop = crop;
            state.LastFileNameSaveAt = now;
            state.SummaryRow.Saved++;
        }

        report.Line(newName is not null
            ? $"{now:HH:mm:ss} {state.Machine.Name}: zmiana nazwy pliku pracy (prawdopodobny start kolejnej pracy) — odczyt: \"{newName}\" — zapisano {path}"
            : $"{now:HH:mm:ss} {state.Machine.Name}: zmiana nazwy pliku pracy (prawdopodobny start kolejnej pracy) — OCR niedostępny, nazwy nie odczytano — zapisano {path}");
    }

    // --- Odczyt liczby procent postępu (dodatkowo, przy okazji kotwicy) --------------------------

    /// <summary>
    /// Zmierzony obszar liczby procent z profilu, albo <see langword="null"/>, gdy profil go nie ma, sekcja
    /// <c>idle</c> jest wyłączona albo postęp jest liczony z koloru paska (<see cref="ProgressMode.Bar"/>) —
    /// wtedy <c>progressRoi</c> opisuje pasek, nie liczbę, i OCR wymyślałby z niego procenty (krok <c>ocr</c>
    /// odmawia w tym samym przypadku).
    /// </summary>
    internal static RectInt? ConfiguredProgressRoi(IdleWatchProfile? idle) =>
        idle is { Enabled: true, Mode: ProgressMode.Number } && !idle.ProgressRoi.IsEmpty ? idle.ProgressRoi : null;

    internal static RectInt? ChooseProgressRoi(IdleWatchProfile? idle, MachineAnchors? anchors)
    {
        RectInt? configured = ConfiguredProgressRoi(idle);
        RectInt? anchorRoi = anchors?.ProgressRoi;

        return anchors?.ProgressRoiIsGuess == true
            ? configured ?? anchorRoi
            : anchorRoi ?? configured;
    }

    private static async Task ProcessProgressAsync(MachineWatchState state, ProbeSession session, WindowsOcrEngine engine, RgbaImage frame, Report report, DateTimeOffset now, CancellationToken ct)
    {
        IdleWatchProfile? idle = state.Profile.Idle;
        RectInt? configured = ConfiguredProgressRoi(idle);
        RectInt? roiOpt = ChooseProgressRoi(idle, state.Anchors);

        if (roiOpt is not { IsEmpty: false } roiRaw || !engine.IsAvailable)
        {
            return;
        }

        if (state.ProgressReader is null)
        {
            // Gdy obszar pochodzi z profilu, bierzemy też JEGO ustawienia OCR (skala, margines, odwrócenie
            // jasności) — inaczej ten sam obszar czytałby się tu inaczej niż w programie.
            IdleWatchProfile idleProfile = idle is not null && configured is { } measured && roiRaw.Equals(measured)
                ? idle
                : new IdleWatchProfile { ProgressRoi = roiRaw };
            state.ProgressReader = new ProgressNumberReader(idleProfile, state.Profile.DarkTheme, engine);
        }

        ProgressNumberReader reader = state.ProgressReader;

        RectInt roi = roiRaw.Clamp(frame.Width, frame.Height);
        if (roi.IsEmpty)
        {
            return;
        }

        RgbaImage crop = frame.Crop(roi);
        ProgressDetectionResult result = await reader.ReadAsync(crop, ct).ConfigureAwait(false);

        bool changed;
        bool wasFull;
        lock (state)
        {
            changed = result.Percent != state.LastProgressPercent;
            wasFull = state.LastProgressWasFull;
        }

        if (!changed)
        {
            return;
        }

        string oldText = state.LastProgressPercent is { } op ? $"{op} %" : "brak odczytu";
        string newText = result.Percent is { } np ? $"{np} %" : "brak odczytu";
        bool crossedToFull = result.IsFull && !wasFull;
        bool droppedFromFull = wasFull && !result.IsFull;
        string line = $"{now:HH:mm:ss} {state.Machine.Name}: postęp: {oldText} → {newText}";

        lock (state)
        {
            state.LastProgressPercent = result.Percent;
            state.LastProgressWasFull = result.IsFull;
        }

        if (crossedToFull)
        {
            report.Line(line + " (osiągnięto 100%)");
        }
        else if (droppedFromFull)
        {
            report.Line(line + " (spadek ze 100% — prawdopodobny start nowej pracy)");
        }
        else
        {
            report.FileOnly(line);
        }
    }

    private sealed class MachineWatchState
    {
        public required MachineConfig Machine { get; init; }
        public required ScreenProfile Profile { get; init; }
        public required MachineSummary SummaryRow { get; init; }

        public bool Connected { get; set; }
        public double LastFullFraction { get; set; }
        public double LastRoiFraction { get; set; }
        public int SavedErrorCount { get; set; }
        public int SavedPeriodicCount { get; set; }
        public DateTimeOffset? LastErrorSaveAt { get; set; }
        public DateTimeOffset? LastPeriodicSaveAt { get; set; }
        public Queue<double> RecentFractions { get; } = new();

        public MachineAnchors? Anchors { get; set; }
        public ProgressNumberReader? ProgressReader { get; set; }
        public int? LastProgressPercent { get; set; }
        public bool LastProgressWasFull { get; set; }

        public RgbaImage? LastSavedAlertWindowCrop { get; set; }
        public DateTimeOffset? LastAlertWindowSaveAt { get; set; }
        public int AlertWindowChangeCount { get; set; }
        public HashSet<string> AlertWindowTextColors { get; } = new(StringComparer.Ordinal);

        /// <summary>Ile razy zapisany zrzut (folder "bledy" albo "okno-alertow") miał czerwony tekst dochodzący do krawędzi wycinka — prawdopodobnie ucięty na ekranie maszyny (patrz <see cref="WatchCommand.IsRedTextTruncated"/>).</summary>
        public int TruncatedRedTextCount { get; set; }

        public RgbaImage? LastSavedFileNameCrop { get; set; }
        public DateTimeOffset? LastFileNameSaveAt { get; set; }
    }
}
