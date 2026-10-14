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

public sealed class MachineDiagnostic : IAsyncDisposable
{
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinOcrSpacing = TimeSpan.FromSeconds(10);

    /// <summary>Gdy na klatce jest czerwień, kolejne klatki co 5 s (zamiast co K) — każdy alarm maszyny bez listy błędów ma zostać uchwycony.</summary>
    private const int RedFrameIntervalSeconds = 5;
    private static readonly TimeSpan PortScanSpacing = TimeSpan.FromMinutes(5);

    private readonly MachineConfig _machine;
    private readonly AppConfig _config;
    private readonly ScreenProfile? _profile;
    private readonly string _dir;
    private readonly DiagnozaSchedule _schedule;
    private readonly string? _password;
    private readonly string? _username;
    private readonly Report _report;
    private readonly MachineSummary _row;
    private readonly IReadOnlyList<IOcrEngine> _ocr;
    private readonly string? _initialSkipReason;
    private readonly FrameStore _frames;
    private readonly StreamWriter _log;
    private readonly object _logLock = new();
    private readonly JsonSerializerOptions _json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private DateTimeOffset _start;
    private DateTimeOffset _deadline;
    private DateTimeOffset? _lastPortScan;
    private DateTimeOffset? _lastOcrAt;
    private string? _lastHash;
    private bool _plannedDisconnectDone;
    private bool _awaitingReconnectTest;
    private bool _sharedTestDone;
    private bool _rateMeasureDone;

    private enum SessionEnd { Deadline, Planned, Error }

    public MachineDiagnostic(
        MachineConfig machine,
        AppConfig config,
        string machineOutDir,
        DiagnozaSchedule schedule,
        string? password,
        string? username,
        Report report,
        MachineSummary row,
        IReadOnlyList<IOcrEngine> ocrEngines,
        string? initialSkipReason)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dir = machineOutDir ?? throw new ArgumentNullException(nameof(machineOutDir));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _password = password;
        _username = username;
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _row = row ?? throw new ArgumentNullException(nameof(row));
        _ocr = ocrEngines ?? throw new ArgumentNullException(nameof(ocrEngines));
        _initialSkipReason = initialSkipReason;
        config.Profiles.TryGetValue(machine.Profile, out _profile);

        Result = new MachineDiagnosticResult(machine.Id, machine.Name, machine.Host, machine.Port, machine.Profile);
        Result.OcrLanguages.AddRange(ocrEngines.Where(e => e.IsAvailable).Select(e => e.LanguageTag ?? e.Name));

        Directory.CreateDirectory(_dir);
        _frames = new FrameStore(Path.Combine(_dir, "klatki"));
        _log = new StreamWriter(Path.Combine(_dir, "dziennik.txt"), append: false, new UTF8Encoding(true)) { AutoFlush = true };
    }

    /// <summary>Wynik (stan) diagnostyki tej maszyny.</summary>
    public MachineDiagnosticResult Result { get; }

    /// <summary>Cały przebieg dla tej maszyny — nigdy nie rzuca poza <see cref="OperationCanceledException"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _start = DateTimeOffset.Now;
        _deadline = _start + TimeSpan.FromSeconds(_schedule.TotalSeconds);
        Log($"=== diagnoza {_machine.Name} ({_machine.Id}) {_machine.Host}:{_machine.Port}, profil \"{_machine.Profile}\", " +
            $"{_schedule.TotalSeconds} s, klatka co {_schedule.FrameIntervalSeconds} s, OCR: {(Result.OcrLanguages.Count == 0 ? "brak" : string.Join(", ", Result.OcrLanguages))} ===");
        Log($"hasło: {(_password is null ? "nie podano" : "podano (zamaskowane)")}, nazwa użytkownika: {_username ?? "-"}, enabled w config: {_machine.Enabled}");

        try
        {
            if (VncAlertConflict.Is520(_machine))
            {
                SetStatus(MachineStatus.Skipped, VncAlertConflict.SafetyReason);
                Log(VncAlertConflict.SafetyReason);
                return;
            }

            if (_profile is null)
            {
                SetStatus(MachineStatus.ConfigError, $"profil \"{_machine.Profile}\" nie istnieje w konfiguracji");
                Log($"BŁĄD KONFIGURACJI: {Result.Reason} — sprawdzam tylko sieć.");
                await TcpOnlyLoopAsync(ct).ConfigureAwait(false);
                return;
            }

            if (_initialSkipReason is not null && !WatchCommand.IsRetryableSkip(_initialSkipReason))
            {
                SetStatus(MachineStatus.FromSkipReason(_initialSkipReason), _initialSkipReason);
                Result.RequiresPassword = Result.Status is MachineStatus.PasswordRequired or MachineStatus.WrongPassword or MachineStatus.WrongCredentials;
                Log($"faza 0: {_initialSkipReason}");
                Log("maszyna odpowiada na handshake, ale brak/złe poświadczenia — nie ponawiam logowania, sprawdzam tylko port co " +
                    $"{_schedule.RetryUnreachableEverySeconds} s.");
                Say($"{_machine.Name}: {Result.Status} — {_initialSkipReason}");
                await TcpOnlyLoopAsync(ct).ConfigureAwait(false);
                return;
            }

            await MainLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("przerwano (Ctrl+C / koniec przebiegu).");
            throw;
        }
        catch (Exception ex)
        {
            Result.Errors.Add($"nieoczekiwany błąd przebiegu: {ex.Message}");
            Log($"NIEOCZEKIWANY BŁĄD: {ex}");
            Say($"{_machine.Name}: nieoczekiwany błąd ({ex.GetType().Name}: {ex.Message}) — diagnostyka tej maszyny przerwana, pozostałe trwają.");
        }
        finally
        {
            SyncRow();
            Log($"=== koniec: status {Result.Status}, klatek {Result.Frames.Count}, czarnych {Result.BlackFrames}, rozłączeń {Result.Disconnects.Count} ===");
        }
    }

    // ---- Pętla główna: TCP -> RFB -> sesja -> (ponowne połączenie) --------------------------------------------

    private async Task MainLoopAsync(CancellationToken ct)
    {
        int backoffIndex = 0;
        while (DateTimeOffset.Now < _deadline)
        {
            ct.ThrowIfCancellationRequested();

            TcpProbeResult tcp = await PortScanner.ProbeAsync(_machine.Host, _machine.Port, TcpTimeout, ct).ConfigureAwait(false);
            Result.TcpProbes.Add(tcp);
            Log($"TCP {tcp.Describe()}");
            if (!tcp.Open)
            {
                if (!Result.EverConnected)
                {
                    SetStatus(MachineStatus.Unreachable, $"nie odpowiada na porcie VNC {_machine.Port} ({tcp.Error})");
                }

                await MaybeScanPortsAsync(ct).ConfigureAwait(false);
                await WaitAsync(TimeSpan.FromSeconds(_schedule.RetryUnreachableEverySeconds), ct).ConfigureAwait(false);
                continue;
            }

            var sw = Stopwatch.StartNew();
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

            RfbServerInfo info;
            try
            {
                info = await client.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (RfbException ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                string reason = ConnectionHelper.BuildSkipReason(ex);
                string status = MachineStatus.FromSkipReason(reason);
                Log($"RFB: handshake nieudany — {reason}");
                if (ex is RfbAuthenticationException authEx)
                {
                    Result.RequiresPassword = status is MachineStatus.PasswordRequired or MachineStatus.WrongPassword or MachineStatus.WrongCredentials;
                    if (authEx.OfferedSecurityTypes.Count > 0)
                    {
                        Log($"RFB: typy zabezpieczeń oferowane: {RfbFormat.FormatOfferedTypes(authEx.OfferedSecurityTypes)}");
                    }
                }

                if (!WatchCommand.IsRetryableSkip(reason))
                {
                    if (!Result.EverConnected)
                    {
                        SetStatus(status, reason);
                    }

                    Say($"{_machine.Name}: {status} — {reason}");
                    Log("nie ponawiam logowania — dalej tylko sprawdzanie portu.");
                    await TcpOnlyLoopAsync(ct).ConfigureAwait(false);
                    return;
                }

                if (!Result.EverConnected)
                {
                    SetStatus(MachineStatus.Unreachable, reason);
                }
                else
                {
                    Result.Disconnects.Add(new DisconnectRecord(DateTimeOffset.Now, $"ponowne połączenie nieudane: {reason}"));
                }

                int delay = _schedule.ReconnectBackoffSeconds[Math.Min(backoffIndex++, _schedule.ReconnectBackoffSeconds.Count - 1)];
                await WaitAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                continue;
            }

            backoffIndex = 0;
            bool first = !Result.EverConnected;
            Result.ConnectedAt ??= DateTimeOffset.Now;
            Result.Connections++;
            Result.Server = info;
            Result.RequiresPassword = info.UsedSecurityType != RfbSecurityType.None;
            SetStatus(MachineStatus.Connected, null);
            _row.ApplyServerInfo(info, DateTimeOffset.Now);
            LogHandshake(info, first);

            SessionEnd end;
            Exception? error = null;
            try
            {
                end = await SessionAsync(client, info, sw, first, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is RfbException or RfbScreenResizedException or ArgumentException)
            {
                end = SessionEnd.Error;
                error = ex;
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            switch (end)
            {
                case SessionEnd.Deadline:
                    return;
                case SessionEnd.Planned:
                    Log($"planowe rozłączenie na {_schedule.DisconnectForSeconds} s (test: czy ekran wraca po ponownym połączeniu).");
                    Result.Disconnects.Add(new DisconnectRecord(DateTimeOffset.Now, "planowe (test ponownego połączenia)"));
                    _awaitingReconnectTest = true;
                    await WaitAsync(TimeSpan.FromSeconds(_schedule.DisconnectForSeconds), ct).ConfigureAwait(false);
                    break;
                case SessionEnd.Error:
                    string message = DescribeDisconnect(error!);
                    bool resize = error is RfbScreenResizedException or ArgumentException;
                    Result.Disconnects.Add(new DisconnectRecord(DateTimeOffset.Now, resize ? $"zmiana rozmiaru ekranu: {message}" : message));
                    _row.AddDisconnect(DateTimeOffset.Now, message);
                    int delay = resize ? 1 : _schedule.ReconnectBackoffSeconds[Math.Min(backoffIndex++, _schedule.ReconnectBackoffSeconds.Count - 1)];
                    Log(resize
                        ? $"zmiana rozmiaru ekranu ({message}) — łączę ponownie za {delay} s."
                        : $"ZERWANIE: {message} — łączę ponownie za {delay} s.");
                    Say($"{_machine.Name}: {(resize ? "zmiana rozmiaru ekranu" : "zerwane połączenie")} — {message}; ponawiam.");
                    if (_awaitingReconnectTest && Result.ReconnectTest is null)
                    {
                        Result.ReconnectTest = new ReconnectTestResult(false, null, null, $"ponowne połączenie nieudane: {message}");
                    }

                    await WaitAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Gdy nie ma co łączyć (złe hasło, brak profilu): tylko sprawdzanie TCP do końca przebiegu, żeby raport mówił, czy maszyna była w sieci.</summary>
    private async Task TcpOnlyLoopAsync(CancellationToken ct)
    {
        while (DateTimeOffset.Now < _deadline)
        {
            ct.ThrowIfCancellationRequested();
            TcpProbeResult tcp = await PortScanner.ProbeAsync(_machine.Host, _machine.Port, TcpTimeout, ct).ConfigureAwait(false);
            Result.TcpProbes.Add(tcp);
            Log($"TCP {tcp.Describe()}");
            if (!tcp.Open)
            {
                await MaybeScanPortsAsync(ct).ConfigureAwait(false);
            }

            await WaitAsync(TimeSpan.FromSeconds(_schedule.RetryUnreachableEverySeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task MaybeScanPortsAsync(CancellationToken ct)
    {
        if (_lastPortScan is { } last && DateTimeOffset.Now - last < PortScanSpacing)
        {
            return;
        }

        _lastPortScan = DateTimeOffset.Now;
        Log($"skan portów ({string.Join(", ", PortScanner.DefaultScanPorts)}, po 1 s)...");
        IReadOnlyList<TcpProbeResult> scan = await PortScanner.ScanAsync(_machine.Host, PortScanner.DefaultScanPorts, PortScanner.ScanTimeout, ct).ConfigureAwait(false);
        Result.PortScan = scan;
        Result.PortScans++;
        string summary = PortScanner.DescribeScan(scan);
        Log($"skan portów: {summary}");
        Say($"{_machine.Name}: nie odpowiada na porcie VNC {_machine.Port}; {summary}; ponawiam co {_schedule.RetryUnreachableEverySeconds} s.");
    }

    // ---- Sesja: połączony klient aż do końca / planowego rozłączenia / błędu -----------------------------------

    private async Task<SessionEnd> SessionAsync(RfbClient client, RfbServerInfo info, Stopwatch sinceTcp, bool firstConnection, CancellationToken ct)
    {
        int width = info.Width;
        int height = info.Height;
        DateTimeOffset connectedAt = Result.ConnectedAt!.Value;
        bool firstFrameOfSession = true;
        DateTimeOffset nextFrameAt = DateTimeOffset.Now;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.Now;
            if (now >= _deadline)
            {
                return SessionEnd.Deadline;
            }

            double elapsed = (now - connectedAt).TotalSeconds;

            if (!_plannedDisconnectDone && elapsed >= _schedule.DisconnectAtSeconds && !firstFrameOfSession)
            {
                _plannedDisconnectDone = true;
                return SessionEnd.Planned;
            }

            if (!_sharedTestDone && elapsed >= _schedule.SharedTestAtSeconds && !firstFrameOfSession)
            {
                _sharedTestDone = true;
                await SharedTestAsync(client, width, height, ct).ConfigureAwait(false);
            }

            if (!_rateMeasureDone && elapsed >= _schedule.RateMeasureAtSeconds && !firstFrameOfSession)
            {
                _rateMeasureDone = true;
                await MeasureUpdateRateAsync(client, width, height, ct).ConfigureAwait(false);
                nextFrameAt = DateTimeOffset.Now;
            }

            // Klatka: pełne żądanie (bez przyrostu) z pomiarem czasu odpowiedzi serwera.
            var sw = Stopwatch.StartNew();
            FramebufferUpdate update = await client.UpdateAsync(new RectInt(0, 0, width, height), incremental: false, ct).ConfigureAwait(false);
            sw.Stop();
            NoteEncodings(update.EncodingsUsed);

            if (client.Framebuffer.Width != width || client.Framebuffer.Height != height)
            {
                Log($"serwer zmienił rozmiar ekranu {width}x{height} -> {client.Framebuffer.Width}x{client.Framebuffer.Height} (DesktopSize) — mierzę dalej w nowym rozmiarze.");
                Say($"{_machine.Name}: zmiana rozmiaru ekranu na {client.Framebuffer.Width}x{client.Framebuffer.Height}.");
                width = client.Framebuffer.Width;
                height = client.Framebuffer.Height;
                Result.Server = info with { Width = width, Height = height };
                _row.Screen = $"{width}x{height}";
                if (width <= 0 || height <= 0)
                {
                    throw new RfbScreenResizedException(width, height);
                }
            }

            RgbaImage frame = client.Framebuffer.Crop(new RectInt(0, 0, width, height));
            now = DateTimeOffset.Now;
            double sinceConnect = (now - connectedAt).TotalSeconds;

            if (firstFrameOfSession)
            {
                firstFrameOfSession = false;
                long ms = sinceTcp.ElapsedMilliseconds;
                if (firstConnection)
                {
                    Result.FirstFrameMs = ms;
                    _row.FirstFrameAfterMs = ms;
                }

                Log($"pierwsza klatka tej sesji po {ms} ms od otwarcia TCP (odpowiedź serwera na pełne żądanie: {sw.ElapsedMilliseconds} ms).");
            }

            FrameSample sample = RecordFrame(frame, now, sinceConnect, sw.ElapsedMilliseconds, firstConnection && Result.Frames.Count == 0 ? "start" : null);

            if (_awaitingReconnectTest && Result.ReconnectTest is null)
            {
                _awaitingReconnectTest = false;
                Result.ReconnectTest = new ReconnectTestResult(true, sinceTcp.ElapsedMilliseconds, sample.Pomiar.IsBlack,
                    sample.Pomiar.IsBlack
                        ? $"po ponownym połączeniu ekran nadal {sample.Pomiar.KlasaCzerni}"
                        : "po ponownym połączeniu ekran widoczny (nie czarny)");
                Log($"test ponownego połączenia: pierwsza klatka po {sinceTcp.ElapsedMilliseconds} ms, {Result.ReconnectTest.Opis}.");
            }

            RecordBlackTestPoints(sample);
            await MaybeOcrAsync(frame, sample, now, ct).ConfigureAwait(false);
            SyncRow();

            // Następna klatka: za K s, ale wcześniej, gdy przypada punkt testu czerni albo zdarzenie z harmonogramu.
            int interval = sample.Pomiar.JakakolwiekCzerwien ? Math.Min(_schedule.FrameIntervalSeconds, RedFrameIntervalSeconds) : _schedule.FrameIntervalSeconds;
            nextFrameAt = nextFrameAt.AddSeconds(interval);
            if (nextFrameAt <= DateTimeOffset.Now)
            {
                nextFrameAt = DateTimeOffset.Now.AddSeconds(interval);
            }

            DateTimeOffset wake = NextWake(nextFrameAt, connectedAt);
            TimeSpan delay = wake - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero)
            {
                await WaitAsync(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private DateTimeOffset NextWake(DateTimeOffset nextFrameAt, DateTimeOffset connectedAt)
    {
        DateTimeOffset wake = nextFrameAt < _deadline ? nextFrameAt : _deadline;
        foreach (int offset in _schedule.BlackTestOffsetsSeconds)
        {
            if (Result.BlackTests.All(b => b.PlanSek != offset))
            {
                DateTimeOffset at = connectedAt.AddSeconds(offset);
                if (at > DateTimeOffset.Now && at < wake)
                {
                    wake = at;
                }
            }
        }

        foreach ((bool done, int at) in new[]
                 {
                     (_plannedDisconnectDone, _schedule.DisconnectAtSeconds),
                     (_sharedTestDone, _schedule.SharedTestAtSeconds),
                     (_rateMeasureDone, _schedule.RateMeasureAtSeconds),
                 })
        {
            if (!done)
            {
                DateTimeOffset when = connectedAt.AddSeconds(at);
                if (when > DateTimeOffset.Now && when < wake)
                {
                    wake = when;
                }
            }
        }

        return wake;
    }

    private FrameSample RecordFrame(RgbaImage frame, DateTimeOffset now, double sinceConnect, long responseMs, string? suffix)
    {
        FrameMeasurement m = FrameMetrics.Measure(frame, _config.Profiles);
        bool changed = _lastHash is not null && _lastHash != m.Hash;
        _lastHash = m.Hash;

        string? path = null;
        try
        {
            path = _frames.Save(frame, now, suffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Result.Errors.Add($"zapis klatki: {ex.Message}");
            Log($"nie udało się zapisać klatki: {ex.Message}");
        }

        var sample = new FrameSample(now, Math.Round(sinceConnect, 1), responseMs, m, path, changed);
        Result.Frames.Add(sample);

        try
        {
            File.AppendAllText(Path.Combine(_dir, "pomiary.jsonl"), JsonSerializer.Serialize(new
            {
                czas = now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                odPolaczeniaSek = sample.OdPolaczeniaSek,
                odpowiedzMs = responseMs,
                rozmiar = $"{m.Width}x{m.Height}",
                jasnoscSrednia = m.JasnoscSrednia,
                udzialCiemnych = m.UdzialCiemnych,
                klasaCzerni = m.KlasaCzerni,
                czerwien = m.Czerwien.Select(c => new { profil = c.Profil, roi = c.Roi, udzial = c.Udzial, powyzejProgu = c.PowyzejProgu }),
                czerwienCalaKlatka = m.CzerwienCalaKlatka,
                czerwienBbox = m.CzerwienBbox.ToDictionary(k => k.Key, k => k.Value is { } b ? new { x = b.X, y = b.Y, width = b.Width, height = b.Height } : null),
                jakakolwiekCzerwien = m.JakakolwiekCzerwien,
                odcienie = m.Odcienie,
                udzialBieli = m.UdzialBieli,
                udzialCzerni = m.UdzialCzerni,
                motyw = m.Motyw,
                hash = m.Hash,
                zmianaObrazu = changed,
                plik = path is null ? null : Path.GetFileName(path),
            }, _json) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Result.Errors.Add($"zapis pomiarów: {ex.Message}");
        }

        string redTop = m.Czerwien.Where(c => c.PowyzejProgu).Select(c => $"{c.Profil}/{c.Roi} {c.Udzial:P1}").FirstOrDefault()
            ?? (m.JakakolwiekCzerwien ? "czerwień poniżej progów ROI, ale obecna w klatce" : "brak czerwieni");
        if (m.JakakolwiekCzerwien && m.CzerwienBbox.TryGetValue(DiagnozaConclusions.BboxProfile, out RectInt? bb) && bb is { } box)
        {
            redTop += $", bbox czerwieni (progi {DiagnozaConclusions.BboxProfile}) {box.X},{box.Y},{box.Width},{box.Height}";
        }
        Log($"klatka {Result.Frames.Count} (+{sample.OdPolaczeniaSek:0.#} s): {m.Width}x{m.Height}, odpowiedź {responseMs} ms, jasność {m.JasnoscSrednia}, " +
            $"ciemne {m.UdzialCiemnych:P1} -> {m.KlasaCzerni}, motyw {m.Motyw}, {redTop}, hash {m.Hash}{(changed ? " (ZMIANA)" : string.Empty)}");
        return sample;
    }

    private void RecordBlackTestPoints(FrameSample sample)
    {
        foreach (int offset in _schedule.BlackTestOffsetsSeconds)
        {
            if (Result.BlackTests.Any(b => b.PlanSek == offset) || sample.OdPolaczeniaSek < offset)
            {
                continue;
            }

            Result.BlackTests.Add(new BlackTestPoint(offset, sample.OdPolaczeniaSek, sample.Pomiar.IsBlack, sample.Pomiar.KlasaCzerni));
            Log($"test czerni: punkt {offset} s (faktycznie +{sample.OdPolaczeniaSek:0.#} s) -> {sample.Pomiar.KlasaCzerni}.");
        }
    }

    // ---- OCR ---------------------------------------------------------------------------------------------------

    private async Task MaybeOcrAsync(RgbaImage frame, FrameSample sample, DateTimeOffset now, CancellationToken ct)
    {
        if (_ocr.Count == 0)
        {
            return;
        }

        bool firstEver = _lastOcrAt is null;
        bool due = firstEver
            || (now - _lastOcrAt!.Value).TotalSeconds >= _schedule.OcrEverySeconds
            || (sample.ZmianaObrazu && now - _lastOcrAt.Value >= MinOcrSpacing);
        if (!due)
        {
            return;
        }

        _lastOcrAt = now;
        Gray8Image gray = ImageOps.ToGray(frame);
        string ocrDir = Path.Combine(_dir, "ocr");
        Directory.CreateDirectory(ocrDir);
        foreach (IOcrEngine engine in _ocr)
        {
            if (!engine.IsAvailable)
            {
                continue;
            }

            string lang = engine.LanguageTag ?? engine.Name;
            try
            {
                var sw = Stopwatch.StartNew();
                OcrResult result = await engine.RecognizeAsync(gray, ct).ConfigureAwait(false);
                sw.Stop();
                string file = Path.Combine(ocrDir, $"{now:yyyyMMdd-HHmmss}-{SafeName(lang)}.json");
                File.WriteAllText(file, JsonSerializer.Serialize(new
                {
                    czas = now.ToString("yyyy-MM-dd HH:mm:ss"),
                    jezyk = lang,
                    silnik = engine.Name,
                    czasMs = sw.ElapsedMilliseconds,
                    klatka = sample.Plik is null ? null : Path.GetFileName(sample.Plik),
                    tekst = result.Text,
                    linie = result.Lines,
                    slowa = result.Words.Select(w => new { tekst = w.Text, x = w.Bounds.X, y = w.Bounds.Y, w = w.Bounds.Width, h = w.Bounds.Height, linia = w.LineIndex }),
                }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
                string head = result.Text.Length > 80 ? result.Text[..80] + "…" : result.Text;
                Result.OcrRuns.Add(new OcrRun(now, lang, result.Words.Count, head, Path.GetFileName(file)));
                Log($"OCR {lang}: {result.Words.Count} słów w {sw.ElapsedMilliseconds} ms: \"{head}\"");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Result.Errors.Add($"OCR {lang}: {ex.Message}");
                Log($"OCR {lang} nie powiódł się: {ex.Message}");
            }
        }

        if (firstEver && _profile is not null)
        {
            await ComputeAnchorsAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private async Task ComputeAnchorsAsync(RgbaImage frame, CancellationToken ct)
    {
        IOcrEngine? engine = _ocr.FirstOrDefault(e => e.IsAvailable && string.Equals(e.LanguageTag, "en-US", StringComparison.OrdinalIgnoreCase))
            ?? _ocr.FirstOrDefault(e => e.IsAvailable);
        if (engine is null)
        {
            return;
        }

        try
        {
            MachineAnchors anchors = await ScreenAnchors.ComputeAsync(frame, engine, _profile!, ct).ConfigureAwait(false);
            Result.Anchors = anchors;
            _row.ProgressField = ScreenAnchors.DescribeProgressField(anchors);
            _row.FileNameField = ScreenAnchors.DescribeFileNameField(anchors);
            object? Roi(RectInt? r) => r is { } v ? new { x = v.X, y = v.Y, width = v.Width, height = v.Height } : null;
            File.WriteAllText(Path.Combine(_dir, "sugerowane-roi.json"), JsonSerializer.Serialize(new
            {
                profile = _profile!.Id,
                screen = new { width = frame.Width, height = frame.Height },
                progressRoi = Roi(anchors.ProgressRoiIsGuess ? null : anchors.ProgressRoi),
                progressRoiUwaga = anchors.ProgressRoiIsGuess ? "obszar obok etykiety ZGADNIĘTY — OCR nie odczytał wartości; nie wklejaj do config.json" : null,
                progressOdczyt = anchors.ProgressRawText,
                progressProcent = anchors.ProgressPercent,
                fileNameRoi = Roi(anchors.FileNameRoi),
                fileNameOdczyt = anchors.FileNameRawText,
                statusBarRoi = Roi(anchors.AlertWindowRoi.IsEmpty ? null : anchors.AlertWindowRoi),
                statusBarZHeurystyki = anchors.AlertWindowIsHeuristic,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            Log($"kotwice OCR: pole postępu {ScreenAnchors.DescribeProgressField(anchors)}, nazwa pliku {ScreenAnchors.DescribeFileNameField(anchors)}, " +
                $"okno alertów {ScreenAnchors.FormatRoi(anchors.AlertWindowRoi)}{(anchors.AlertWindowIsHeuristic ? " (heurystyka)" : " (statusBarRoi profilu)")}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Result.Errors.Add($"kotwice OCR: {ex.Message}");
            Log($"kotwiczenie OCR nie powiodło się: {ex.Message}");
        }
    }

    // ---- shared-test i częstość aktualizacji ----------------------------------------------------------------------

    private async Task SharedTestAsync(RfbClient main, int width, int height, CancellationToken ct)
    {
        Log("shared-test: łączę drugiego klienta na 2 s (czy serwer nie rozłącza pierwszego / nie odrzuca drugiego)...");
        var second = new RfbClient(new RfbClientOptions
        {
            Host = _machine.Host,
            Port = _machine.Port,
            Password = _password,
            Username = _username,
            Shared = true,
            ConnectTimeout = ConnectTimeout,
            ReadTimeout = ReadTimeout,
        });

        bool secondOk = false;
        bool mainOk = false;
        string opis;
        try
        {
            RfbServerInfo info2 = await second.ConnectAsync(ct).ConfigureAwait(false);
            await second.UpdateAsync(new RectInt(0, 0, info2.Width, info2.Height), incremental: false, ct).ConfigureAwait(false);
            secondOk = true;
            await WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await main.UpdateAsync(new RectInt(0, 0, width, height), incremental: false, ct).ConfigureAwait(false);
            mainOk = true;
            opis = "drugi klient połączony i odebrał klatkę; pierwszy nadal odbiera — serwer znosi dwóch klientów (persistent OK)";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RfbException or RfbScreenResizedException or ArgumentException)
        {
            opis = secondOk
                ? $"drugi klient OK, ale pierwszy stracił połączenie: {ex.Message} — serwer obsługuje JEDNEGO klienta naraz i nowe połączenie zrywa poprzednie " +
                  "(cecha serwera Qt for Embedded Linux na 520, nie błąd; RealVNC Viewer i program nie mogą patrzeć naraz — w trybie długim ta maszyna będzie odpytywana)"
                : $"drugi klient odrzucony: {ex.Message}";
            if (secondOk)
            {
                Result.SharedTest = new SharedTestResult(secondOk, false, opis);
                Log($"shared-test: {opis}");
                throw;
            }
        }
        finally
        {
            await second.DisposeAsync().ConfigureAwait(false);
        }

        Result.SharedTest = new SharedTestResult(secondOk, mainOk, opis);
        Log($"shared-test: {opis}");
    }

    private async Task MeasureUpdateRateAsync(RfbClient client, int width, int height, CancellationToken ct)
    {
        int seconds = _schedule.RateMeasureForSeconds;
        Log($"pomiar częstości aktualizacji przyrostowych przez {seconds} s...");
        var rect = new RectInt(0, 0, width, height);
        int updates = 0;
        int idle = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            ct.ThrowIfCancellationRequested();
            RgbaImage? fresh = await client.TryCaptureAsync(rect, incremental: true, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (fresh is null)
            {
                idle++;
            }
            else
            {
                updates++;
            }
        }

        sw.Stop();
        string opis = updates == 0
            ? $"0 aktualizacji w {sw.Elapsed.TotalSeconds:0} s — ekran nieruchomy (serwer nie odpowiada na żądania przyrostowe bez zmian; to normalne)"
            : $"{updates} aktualizacji w {sw.Elapsed.TotalSeconds:0} s ({updates / sw.Elapsed.TotalSeconds:0.##}/s), {idle} okresów bez zmian";
        Result.UpdateRate = new UpdateRateResult(Math.Round(sw.Elapsed.TotalSeconds, 1), updates, idle, opis);
        Log($"częstość aktualizacji: {opis}");
    }

    // ---- Pomocnicze ------------------------------------------------------------------------------------------------

    internal static string DescribeDisconnect(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is RfbProtocolException)
        {
            return $"błąd protokołu: {error.Message}";
        }

        if (error is RfbException && (error.Message.Contains("zamknął połączenie", StringComparison.Ordinal) || error.Message.Contains("przerwane", StringComparison.Ordinal)))
        {
            return $"{error.Message} (nagłe zamknięcie bez błędu protokołu — możliwe, że inny klient VNC przejął ekran: serwer jednoklientowy, np. Qt na 520)";
        }

        return error.Message;
    }

    private void LogHandshake(RfbServerInfo info, bool first)
    {
        Log($"RFB połączono{(first ? string.Empty : " PONOWNIE")}: wersja {info.ProtocolVersion}, nagłówek serwera \"{info.RawVersionHeader}\", " +
            $"zabezpieczenia oferowane: {RfbFormat.FormatOfferedTypes(info.OfferedSecurityTypes)}, użyto {info.UsedSecurityType} ({(int)info.UsedSecurityType}), " +
            $"pulpit \"{info.DesktopName}\", ekran {info.Width}x{info.Height}, piksel {info.PixelFormat.Describe()}, wymaga hasła: {(info.UsedSecurityType != RfbSecurityType.None ? "tak" : "nie")}");
        if (info.DescribeCredentialSubtype() is { } subtype)
        {
            Log($"RSA-AES: wariant poświadczeń: {subtype}");
        }

        if (info.ServerKeySha256 is { } key)
        {
            Log($"RSA-AES: odcisk klucza serwera (sha256): {key} — do ewentualnego przypięcia w machines[].rsaAesKeySha256");
        }

        if (first)
        {
            Say($"{_machine.Name}: połączono (RFB {info.ProtocolVersion}, {info.UsedSecurityType}, {info.Width}x{info.Height}, \"{info.DesktopName}\").");
        }
    }

    private void NoteEncodings(IReadOnlyList<int> encodings)
    {
        foreach (int e in encodings)
        {
            string name = RfbFormat.EncodingName(e);
            if (!Result.Encodings.Contains(name))
            {
                Result.Encodings.Add(name);
            }
        }

        _row.NoteEncodings(encodings);
    }

    private void SetStatus(string status, string? reason)
    {
        Result.Status = status;
        Result.Reason = reason;
        if (status == MachineStatus.Connected)
        {
            _row.Status = MachineStatus.Connected;
            _row.Reason = "-";
        }
        else if (_row.Status != MachineStatus.Connected)
        {
            _row.Status = status;
            _row.Reason = reason ?? "-";
            if (reason is not null)
            {
                _row.LastError = reason;
            }
        }
    }

    private void SyncRow()
    {
        _row.Frames = Result.Frames.Count;
        _row.BlackFrames = Result.BlackFrames;
        if (Result.Server is { } s)
        {
            _row.ServerName = s.DesktopName;
        }
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static string SafeName(string text) =>
        string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    /// <summary>Linia dziennika maszyny (plik), ze stemplem czasu i sekundami od startu przebiegu.</summary>
    private void Log(string text)
    {
        lock (_logLock)
        {
            _log.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [+{(DateTimeOffset.Now - _start).TotalSeconds,6:0.0} s] {text}");
        }
    }

    /// <summary>Linia na konsolę i do wspólnego raport.txt (ważniejsze zdarzenia).</summary>
    private void Say(string text) => _report.Line($"{DateTimeOffset.Now:HH:mm:ss} {text}");

    public ValueTask DisposeAsync()
    {
        lock (_logLock)
        {
            _log.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
