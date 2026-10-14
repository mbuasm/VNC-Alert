using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Vision;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>Stan dnia jednej maszyny w kolektorze — do dziennego <c>podsumowanie.json</c>.</summary>
public sealed class CollectorStats
{
    public string Status { get; set; } = MachineStatus.Unknown;
    public string? Reason { get; set; }
    public string Tryb { get; set; } = MachineMode.Trwale;
    public DateTimeOffset? ConnectedAt { get; set; }
    public int Connections { get; set; }
    public int Disconnects { get; set; }
    public int FramesRead { get; set; }
    public int Changes { get; set; }
    public int PeriodicSaved { get; set; }
    public int ChangesSaved { get; set; }
    public int EpisodesClosed { get; set; }
    public int EpisodesRed { get; set; }
    public int EpisodesYellow { get; set; }
    public int OcrRuns { get; set; }
    public string? LastEpisode { get; set; }
    public string? LastOcr { get; set; }
    public long DiskBytes { get; set; }
    public bool OverLimit { get; set; }
    public bool WriteFailed { get; set; }
    public string? Server { get; set; }
    public string? LastError { get; set; }
}

public sealed class MachineCollector : IDisposable
{
    private bool _terminalConnectionFailure;
    private readonly string? _initialSkipReason;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BudgetCheckEvery = TimeSpan.FromMinutes(10);
    private static readonly int[] BackoffSeconds = [2, 5, 15, 30, 60];

    private readonly MachineConfig _machine;
    private readonly AppConfig _config;
    private readonly RedThresholds _red;
    private readonly string _dir;
    private readonly DlugiOptions _options;
    private readonly string? _password;
    private readonly string? _username;
    private readonly Report _report;
    private readonly IReadOnlyList<IOcrEngine> _ocr;
    private readonly EpisodeTracker _episodes = new();
    private readonly object _logLock = new();
    private readonly JsonSerializerOptions _json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private bool _finalizing;
    private bool _budgetInitialized;
    private const long FinalizationReserveBytes = 16 * 1024;
    private string? _lastHash;
    private DateTimeOffset _lastChangeSave = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPeriodic = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBudgetCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;
    private DateTimeOffset _lastObserved = DateTimeOffset.MinValue;
    private readonly Dictionary<EpisodeColor, StringBuilder> _episodeText = [];

    public MachineCollector(
        MachineConfig machine,
        AppConfig config,
        string machineOutDir,
        DlugiOptions options,
        string mode,
        string? password,
        string? username,
        Report report,
        IReadOnlyList<IOcrEngine> ocrEngines,
        string? initialSkipReason = null)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dir = machineOutDir ?? throw new ArgumentNullException(nameof(machineOutDir));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _password = password;
        _username = username;
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _ocr = ocrEngines ?? throw new ArgumentNullException(nameof(ocrEngines));
        _initialSkipReason = initialSkipReason;
        _red = config.Profiles.TryGetValue(DiagnozaConclusions.BboxProfile, out ScreenProfile? p) ? p.Red : new RedThresholds();
        Stats.Tryb = mode;
        if (initialSkipReason is not null && !WatchCommand.IsRetryableSkip(initialSkipReason))
        {
            // Już pierwsze podsumowanie (to, które runner bierze za dowód startu) ma pokazać odmowę z fazy 0, nie „-”.
            Stats.Status = MachineStatus.FromSkipReason(initialSkipReason);
            Stats.Reason = initialSkipReason;
        }
    }

    /// <summary>Liczniki do podsumowania dziennego (czytane z innego wątku — wartości proste).</summary>
    public CollectorStats Stats { get; } = new();

    public MachineConfig Machine => _machine;

    private bool StorageStopped => Stats.OverLimit || Stats.WriteFailed;

    /// <summary>Kolektor jednej maszyny; kończy się po anulowaniu, błędzie uwierzytelnienia, limicie lub błędzie zapisu.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int backoff = 0;
        try
        {
            Directory.CreateDirectory(_dir);
            CheckBudget();
            if (StorageStopped) return;
            Log($"=== kolektor {_machine.Name} ({_machine.Id}) {_machine.Host}:{_machine.Port}, tryb {Stats.Tryb}, profil \"{_machine.Profile}\", " +
                $"okresowa co {_options.OkresowaSek} s, zmiana nie częściej niż co {_options.MinOdstepZmianySek} s, odczyt co >= {_options.MinOdczytSek} s, " +
                $"odpytywanie co {_options.OdpytywanieCoSek} s przez {_options.OdpytywanieTrwaSek} s, limit {_options.LimitBytes / (1024 * 1024)} MB, OCR: " +
                $"{(_ocr.Count == 0 ? "brak" : string.Join(", ", _ocr.Select(e => e.LanguageTag ?? e.Name)))} ===");
            if (_initialSkipReason is not null && !WatchCommand.IsRetryableSkip(_initialSkipReason))
            {
                Stats.Status = MachineStatus.FromSkipReason(_initialSkipReason);
                Stats.Reason = _initialSkipReason;
                Log($"faza 0: {_initialSkipReason}; nie ponawiam logowania.");
                return;
            }
            while (!ct.IsCancellationRequested && !StorageStopped)
            {
                CheckBudget();
                if (StorageStopped) break;
                RfbClient? client = await ConnectAsync(ct).ConfigureAwait(false);
                if (client is null)
                {
                    if (_terminalConnectionFailure)
                    {
                        break;
                    }
                    int delay = BackoffSeconds[Math.Min(backoff++, BackoffSeconds.Length - 1)];
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    continue;
                }

                backoff = 0;
                try
                {
                    if (Stats.Tryb == MachineMode.Odpytywanie)
                    {
                        await SessionAsync(client, TimeSpan.FromSeconds(_options.OdpytywanieTrwaSek), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await SessionAsync(client, null, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is RfbException or RfbScreenResizedException or ArgumentException)
                {
                    Stats.Disconnects++;
                    Stats.LastError = ex.Message;
                    Log($"ZERWANIE: {MachineDiagnostic.DescribeDisconnect(ex)}");
                }
                finally
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    if (!ct.IsCancellationRequested && !StorageStopped)
                    {
                        // Nie rozciągamy epizodu przez nieobserwowany czas reconnectu lub odpytywania.
                        foreach (EpisodeEvent e in _episodes.CloseAll(_lastObserved == DateTimeOffset.MinValue ? DateTimeOffset.Now : _lastObserved))
                            HandleEpisodeEvent(e, null, DateTimeOffset.Now, "przerwa w obserwacji");
                    }
                }

                if (StorageStopped) break;
                if (Stats.Tryb == MachineMode.Odpytywanie)
                {
                    Log($"odpytywanie: rozłączono, następne za {_options.OdpytywanieCoSek} s.");
                    await Task.Delay(TimeSpan.FromSeconds(_options.OdpytywanieCoSek), ct).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds[0]), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // koniec kolektora
        }
        catch (Exception ex)
        {
            Stats.LastError = ex.Message;
            if (ex is IOException or UnauthorizedAccessException) StopForWriteFailure(ex);
            Log($"NIEOCZEKIWANY BŁĄD: {ex}");
            _report.Line($"{_machine.Name}: nieoczekiwany błąd kolektora ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            _finalizing = true;
            foreach (EpisodeEvent e in _episodes.CloseAll(DateTimeOffset.Now))
            {
                HandleEpisodeEvent(e, null, DateTimeOffset.Now);
            }

            Log($"=== koniec kolektora ==={(Stats.Reason is { } reason ? " " + reason : string.Empty)}");
        }
    }

    private async Task<RfbClient?> ConnectAsync(CancellationToken ct)
    {
        var client = new RfbClient(new RfbClientOptions
        {
            Host = _machine.Host,
            Port = _machine.Port,
            Password = _password,
            Username = _username,
            Shared = true,
            ConnectTimeout = ConnectTimeout,
            ReadTimeout = ReadTimeout,
        });
        try
        {
            RfbServerInfo info = await client.ConnectAsync(ct).ConfigureAwait(false);
            bool first = Stats.ConnectedAt is null;
            Stats.ConnectedAt ??= DateTimeOffset.Now;
            Stats.Connections++;
            Stats.Status = MachineStatus.Connected;
            Stats.Reason = null;
            Stats.Server = info.Describe();
            Log($"połączono{(first ? string.Empty : " ponownie")}: {info.Describe()}, nagłówek \"{info.RawVersionHeader}\", oferowane {RfbFormat.FormatOfferedTypes(info.OfferedSecurityTypes)}" +
                (info.ServerKeySha256 is { } k ? $", klucz RSA {k}" : string.Empty));
            if (first)
            {
                _report.Line($"{DateTimeOffset.Now:HH:mm:ss} {_machine.Name}: połączono ({info.Describe()}), tryb {Stats.Tryb}.");
            }

            return client;
        }
        catch (RfbException ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            string reason = ConnectionHelper.BuildSkipReason(ex);
            string status = MachineStatus.FromSkipReason(reason);
            _terminalConnectionFailure = !WatchCommand.IsRetryableSkip(reason);
            if (Stats.Status != status || Stats.Reason != reason)
            {
                Log($"połączenie nieudane: {reason}");
                if (!WatchCommand.IsRetryableSkip(reason))
                {
                    _report.Line($"{DateTimeOffset.Now:HH:mm:ss} {_machine.Name}: {status} — {reason} (nie ponawiam logowania; sprawdź poświadczenia przed ponownym uruchomieniem).");
                }
            }

            if (Stats.ConnectedAt is null)
            {
                Stats.Status = status;
                Stats.Reason = reason;
            }
            else
            {
                Stats.LastError = reason;
            }

            return null;
        }
    }

    /// <summary>Sesja: pełna klatka na start, potem tanie odczyty przyrostowe; <paramref name="holdFor"/> ogranicza czas (odpytywanie).</summary>
    private async Task SessionAsync(RfbClient client, TimeSpan? holdFor, CancellationToken ct)
    {
        DateTimeOffset sessionStart = DateTimeOffset.Now;
        int width = client.ServerInfo!.Width;
        int height = client.ServerInfo.Height;
        bool first = true;
        while (!ct.IsCancellationRequested && !StorageStopped)
        {
            if (holdFor is { } hold && DateTimeOffset.Now - sessionStart >= hold)
            {
                return;
            }

            CheckBudget();
            if (StorageStopped) return;
            DateTimeOffset now = DateTimeOffset.Now;
            TimeSpan sinceRead = now - _lastRead;
            if (sinceRead < TimeSpan.FromSeconds(_options.MinOdczytSek))
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.MinOdczytSek) - sinceRead, ct).ConfigureAwait(false);
            }

            var rect = new RectInt(0, 0, width, height);
            bool periodicDue = first || (now - _lastPeriodic).TotalSeconds >= _options.OkresowaSek;
            RgbaImage? frame;
            if (periodicDue)
            {
                await client.UpdateAsync(rect, incremental: false, ct).ConfigureAwait(false);
                frame = client.Framebuffer.Crop(rect.Clamp(client.Framebuffer.Width, client.Framebuffer.Height));
            }
            else
            {
                frame = await client.TryCaptureAsync(rect, incremental: true, TimeSpan.FromSeconds(_options.MinOdczytSek), ct).ConfigureAwait(false);
            }

            _lastRead = DateTimeOffset.Now;
            if (client.Framebuffer.Width != width || client.Framebuffer.Height != height)
            {
                Log($"zmiana rozmiaru ekranu {width}x{height} -> {client.Framebuffer.Width}x{client.Framebuffer.Height}.");
                width = client.Framebuffer.Width;
                height = client.Framebuffer.Height;
                if (width <= 0 || height <= 0)
                {
                    throw new RfbScreenResizedException(width, height);
                }
            }

            if (frame is null)
            {
                // Cisza na incremental oznacza niezmieniony framebuffer, nie zniknięcie koloru.
                // Użycie istniejących pikseli utrzymuje epizod i zapisuje serię +2/+10/+60 s bez nowego żądania VNC.
                RgbaImage unchanged = client.Framebuffer.Crop(rect.Clamp(client.Framebuffer.Width, client.Framebuffer.Height));
                await ProcessFrameAsync(unchanged, periodic: false, ct, received: false).ConfigureAwait(false);
                continue;
            }

            first = false;
            await ProcessFrameAsync(frame, periodicDue, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessFrameAsync(RgbaImage frame, bool periodic, CancellationToken ct, bool received = true)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        _lastObserved = now;
        if (received) Stats.FramesRead++;
        string hash = FrameMetrics.Hash(frame);
        bool changed = _lastHash is not null && hash != _lastHash;
        bool firstFrame = _lastHash is null;
        _lastHash = hash;
        if (changed)
        {
            Stats.Changes++;
        }

        ColorRegion red = EpisodeTracker.MeasureRed(frame, _red);
        ColorRegion yellow = EpisodeTracker.MeasureRange(frame, EpisodeTracker.Yellow);
        string? ocrText = null;

        if (periodic && !Stats.OverLimit)
        {
            _lastPeriodic = now;
            string? path = SaveFrame(frame, now, "okresowe", null);
            if (path is null) return;
            Stats.PeriodicSaved++;
            AppendMeasure(now, frame, hash, red, yellow, "okresowa", path);
            if (StorageStopped) return;
        }

        bool saveChange = (changed || firstFrame) && (now - _lastChangeSave).TotalSeconds >= _options.MinOdstepZmianySek;
        if (saveChange)
        {
            _lastChangeSave = now;
            string? path = SaveFrame(frame, now, "zmiany", null);
            if (path is null) return;
            if (path is not null)
            {
                Stats.ChangesSaved++;
            }

            AppendMeasure(now, frame, hash, red, yellow, firstFrame ? "pierwsza" : "zmiana", path);
            if (StorageStopped) return;
            ocrText = await OcrAsync(frame, now, ct).ConfigureAwait(false);
            if (StorageStopped) return;
            Log($"zmiana obrazu: hash {hash}, czerwień {red.Udzial:P2}{(red.Bbox is { } rb ? $" bbox {rb.X},{rb.Y},{rb.Width},{rb.Height}" : string.Empty)}, " +
                $"żółć {yellow.Udzial:P2}{(ocrText is null ? string.Empty : $", OCR: \"{Head(ocrText)}\"")}");
        }

        foreach (EpisodeEvent e in _episodes.Update(now, red, yellow))
        {
            if (StorageStopped) break;
            if (e.Rodzaj == EpisodeEvent.SaveFrame)
            {
                string? path = SaveFrame(frame, now, "epizody", $"{e.Kolor.ToString().ToLowerInvariant()}-{e.OffsetSek}s");
                if (path is null) break;
                ocrText ??= await OcrAsync(frame, now, ct).ConfigureAwait(false);
                if (StorageStopped) break;
                if (ocrText is not null)
                {
                    GetText(e.Kolor).Append(ocrText).Append(" | ");
                }

                Log($"epizod {e.Kolor}: klatka +{e.OffsetSek} s -> {Path.GetFileName(path)}");
            }
            else
            {
                HandleEpisodeEvent(e, frame, now);
            }
        }
    }

    private void HandleEpisodeEvent(EpisodeEvent e, RgbaImage? frame, DateTimeOffset now, string? endReason = null)
    {
        switch (e.Rodzaj)
        {
            case EpisodeEvent.Start:
                Log($"EPIZOD {e.Kolor}: początek");
                _report.Line($"{now:HH:mm:ss} {_machine.Name}: {(e.Kolor == EpisodeColor.Czerwien ? "czerwień" : "żółć")} na ekranie — zapisuję serię klatek.");
                GetText(e.Kolor).Clear();
                break;
            case EpisodeEvent.End when e.Epizod is { } ep:
                Stats.EpisodesClosed++;
                if (ep.Kolor == EpisodeColor.Czerwien) Stats.EpisodesRed++; else Stats.EpisodesYellow++;
                string text = GetText(e.Kolor).ToString().Trim(' ', '|');
                var record = new
                {
                    maszyna = _machine.Id,
                    nazwa = _machine.Name,
                    kolor = ep.Kolor.ToString().ToLowerInvariant(),
                    poczatek = ep.Poczatek.ToString("yyyy-MM-dd HH:mm:ss"),
                    koniec = ep.Koniec.ToString("yyyy-MM-dd HH:mm:ss"),
                    trwanieSek = Math.Round((ep.Koniec - ep.Poczatek).TotalSeconds, 1),
                    maksUdzial = Math.Round(ep.MaksUdzial, 5),
                    bbox = ep.Bbox is { } b ? new { x = b.X, y = b.Y, width = b.Width, height = b.Height } : null,
                    klatek = ep.Klatek,
                    tekstOcr = text.Length > 600 ? text[..600] + "…" : text,
                    powodZakonczenia = endReason ?? (_finalizing ? (Stats.OverLimit ? "limit dysku — koniec zbierania" : "koniec zbierania") : "kolor zniknął"),
                };
                try
                {
                    WriteText(Path.Combine(_dir, "epizody.jsonl"), JsonSerializer.Serialize(record, _json) + Environment.NewLine, append: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"nie udało się dopisać epizodu: {ex.Message}");
                }

                Stats.LastEpisode = $"{ep.Poczatek:yyyy-MM-dd HH:mm:ss} {ep.Kolor} {Math.Round((ep.Koniec - ep.Poczatek).TotalSeconds)} s: {Head(text)}";
                Log($"EPIZOD {e.Kolor}: koniec, {(ep.Koniec - ep.Poczatek).TotalSeconds:0} s, maks {ep.MaksUdzial:P2}, bbox {(ep.Bbox is { } bb ? $"{bb.X},{bb.Y},{bb.Width},{bb.Height}" : "-")}");
                _report.Line($"{now:HH:mm:ss} {_machine.Name}: koniec epizodu {ep.Kolor} ({(ep.Koniec - ep.Poczatek).TotalSeconds:0} s).");
                break;
        }
    }

    private StringBuilder GetText(EpisodeColor color)
    {
        if (!_episodeText.TryGetValue(color, out StringBuilder? sb))
        {
            sb = new StringBuilder();
            _episodeText[color] = sb;
        }

        return sb;
    }

    private async Task<string?> OcrAsync(RgbaImage frame, DateTimeOffset now, CancellationToken ct)
    {
        if (_ocr.Count == 0)
        {
            return null;
        }

        Gray8Image gray = ImageOps.ToGray(frame);
        string? firstText = null;
        string dir = Path.Combine(DayDir(now), "ocr");
        Directory.CreateDirectory(dir);
        foreach (IOcrEngine engine in _ocr.Where(e => e.IsAvailable))
        {
            string lang = engine.LanguageTag ?? engine.Name;
            try
            {
                OcrResult result = await engine.RecognizeAsync(gray, ct).ConfigureAwait(false);
                Stats.OcrRuns++;
                firstText ??= result.Text;
                bool saved = WriteText(Path.Combine(dir, $"{now:yyyyMMdd-HHmmss}-{lang.Replace('/', '_')}.json"), JsonSerializer.Serialize(new
                {
                    czas = now.ToString("yyyy-MM-dd HH:mm:ss"),
                    jezyk = lang,
                    tekst = result.Text,
                    linie = result.Lines,
                    slowa = result.Words.Select(w => new { tekst = w.Text, x = w.Bounds.X, y = w.Bounds.Y, w = w.Bounds.Width, h = w.Bounds.Height, linia = w.LineIndex }),
                }, _json));
                if (!saved) return firstText;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"OCR {lang} nie powiódł się: {ex.Message}");
            }
        }

        if (firstText is not null)
        {
            Stats.LastOcr = Head(firstText);
        }

        return firstText;
    }

    private string? SaveFrame(RgbaImage frame, DateTimeOffset now, string kind, string? suffix)
    {
        string dir = Path.Combine(DayDir(now), kind);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{now:yyyyMMdd-HHmmss}{(suffix is null ? string.Empty : "-" + suffix)}.png");
        return WriteBytes(path, PngCodec.Encode(frame)) ? path : null;
    }

    private void AppendMeasure(DateTimeOffset now, RgbaImage frame, string hash, ColorRegion red, ColorRegion yellow, string rodzaj, string? path)
    {
        try
        {
            FrameMeasurement m = FrameMetrics.Measure(frame, _config.Profiles);
            WriteText(Path.Combine(_dir, $"pomiary-{now:yyyy-MM-dd}.jsonl"), JsonSerializer.Serialize(new
            {
                czas = now.ToString("yyyy-MM-dd HH:mm:ss"),
                rodzaj,
                rozmiar = $"{m.Width}x{m.Height}",
                hash,
                klasaCzerni = m.KlasaCzerni,
                motyw = m.Motyw,
                jasnoscSrednia = m.JasnoscSrednia,
                czerwien = new { udzial = Math.Round(red.Udzial, 5), bbox = red.Bbox is { } b ? new { x = b.X, y = b.Y, width = b.Width, height = b.Height } : null },
                zolc = new { udzial = Math.Round(yellow.Udzial, 5), bbox = yellow.Bbox is { } y ? new { x = y.X, y = y.Y, width = y.Width, height = y.Height } : null },
                czerwienProfilRoi = m.Czerwien.Where(c => c.PowyzejProgu).Select(c => $"{c.Profil}/{c.Roi}"),
                odcienie = m.Odcienie,
                plik = path is null ? null : Path.GetRelativePath(_dir, path),
            }, _json) + Environment.NewLine, append: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"zapis pomiaru: {ex.Message}");
        }
    }

    private void CheckBudget()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastBudgetCheck < BudgetCheckEvery && _lastBudgetCheck != DateTimeOffset.MinValue)
        {
            return;
        }

        _lastBudgetCheck = now;
        try
        {
            DiskBudgetResult r = DiskBudget.Enforce(_dir, now, _options.LimitBytes);
            Stats.DiskBytes = r.Bytes;
            _budgetInitialized = true;
            if (r.Bytes >= DataLimitBytes)
            {
                StopForDiskLimit();
                return;
            }
            if (r.Deleted > 0)
            {
                Log($"limit dysku: przerzedzono {r.Deleted} klatek z dni starszych niż {DlugiOptions.ThinAfterDays} ({r.Bytes / (1024 * 1024)} MB).");
            }

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StopForWriteFailure(ex);
        }
    }

    private string DayDir(DateTimeOffset now) => Path.Combine(_dir, now.ToString("yyyy-MM-dd"));

    private static string Head(string text) => text.Length > 80 ? text[..80] + "…" : text;

    // Rezerwa obejmuje najwyżej dwa otwarte epizody (tekst ograniczony do 600 znaków) i końcowy wpis dziennika.
    // Podsumowanie całego kolektora leży poziom wyżej i jest zapisywane także po zatrzymaniu tej maszyny.
    private long DataLimitBytes => Math.Max(0, _options.LimitBytes - FinalizationReserveBytes);

    private void StopForDiskLimit()
    {
        if (Stats.OverLimit) return;
        Stats.OverLimit = true;
        Stats.Status = "zatrzymana (limit dysku)";
        Stats.Reason = "Osiągnięto limit dysku — zakończono zbieranie klatek, epizodów, OCR i dziennika; zachowano rezerwę na domknięcie wyniku.";
        _report.Line($"{DateTimeOffset.Now:HH:mm:ss} {_machine.Name}: {Stats.Reason}");
    }

    private bool WriteText(string path, string text, bool append = false, bool bom = false)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bom && (!File.Exists(path) || new FileInfo(path).Length == 0))
                bytes = [.. Encoding.UTF8.GetPreamble(), .. bytes];
            return WriteBytes(path, bytes, append);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StopForWriteFailure(ex);
            return false;
        }
    }

    private bool WriteBytes(string path, byte[] bytes, bool append = false)
    {
        lock (_logLock)
        {
            if (Stats.WriteFailed || (Stats.OverLimit && !_finalizing)) return false;
            try
            {
                if (!_budgetInitialized)
                {
                    Stats.DiskBytes = DiskBudget.Size(_dir);
                    _budgetInitialized = true;
                }
                long previousLength = !append && File.Exists(path) ? new FileInfo(path).Length : 0;
                long limit = _finalizing ? _options.LimitBytes : DataLimitBytes;
                long growth = bytes.LongLength - previousLength;
                if (Stats.DiskBytes > limit || growth > limit - Stats.DiskBytes)
                {
                    StopForDiskLimit();
                    return false;
                }
                using (var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
                    stream.Write(bytes);
                Stats.DiskBytes += growth;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StopForWriteFailure(ex);
                return false;
            }
        }
    }

    private void StopForWriteFailure(Exception error)
    {
        if (Stats.WriteFailed) return;
        Stats.WriteFailed = true;
        Stats.Status = "zatrzymana (błąd zapisu)";
        Stats.Reason = "Nie udało się zapisać danych — zakończono zbieranie. Wynik jest niepełny.";
        Stats.LastError = error.Message;
        _report.Line($"{DateTimeOffset.Now:HH:mm:ss} {_machine.Name}: {Stats.Reason}");
    }

    /// <summary>Dziennik rotowany dziennie: <c>dziennik-yyyy-MM-dd.txt</c> w katalogu maszyny.</summary>
    private void Log(string text)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        WriteText(Path.Combine(_dir, $"dziennik-{now:yyyy-MM-dd}.txt"), $"{now:HH:mm:ss.fff} {text}{Environment.NewLine}", append: true, bom: true);
    }

    public void Dispose()
    {
        // Każdy kontrolowany zapis zamyka swój uchwyt przed powrotem.
    }
}
