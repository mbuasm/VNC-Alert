using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Config;

/// <summary>
/// Wczytywanie, zapisywanie i sprawdzanie poprawności plików konfiguracyjnych (config.json, rules.json).
/// Format: JSON z dopuszczalnymi komentarzami <c>//</c> i przecinkami końcowymi, nazwy pól camelCase.
/// </summary>
public static class ConfigLoader
{
    /// <summary>Wspólne ustawienia System.Text.Json używane do wczytywania i zapisu konfiguracji.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        // Róg nakładki statusu zapisujemy z myślnikiem ("top-left"),
        // konwerter musi być PRZED ogólnym (camelCase), bo wygrywa pierwszy pasujący.
        options.Converters.Add(new JsonStringEnumConverter<OverlayPosition>(JsonNamingPolicy.KebabCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<OverlayLayout>(JsonNamingPolicy.KebabCaseLower));
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new RectIntJsonConverter());
        return options;
    }

    // ---------------------------------------------------------------------
    // AppConfig (config.json)
    // ---------------------------------------------------------------------

    /// <summary>Wczytuje i parsuje config.json z pliku pod <paramref name="path"/>.</summary>
    public static AppConfig LoadAppConfig(string path) => LoadAppConfigCore(path, warnings: null);

    /// <summary>
    /// Jak <see cref="LoadAppConfig(string)"/>, a ostrzeżenia z wczytania pliku, które nie blokują startu, dopisuje do
    /// <paramref name="warnings"/> — patrz <see cref="ParseAppConfig(string, ICollection{string})"/>.
    /// </summary>
    public static AppConfig LoadAppConfig(string path, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        return LoadAppConfigCore(path, warnings);
    }

    private static AppConfig LoadAppConfigCore(string path, ICollection<string>? warnings)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException(
                $"Nie można odczytać pliku konfiguracyjnego '{path}'. Sprawdź, czy plik istnieje i czy program ma do niego dostęp. Szczegóły: {ex.Message}",
                ex);
        }

        return ParseAppConfigCore(json, warnings);
    }

    /// <summary>
    /// Parsuje treść pliku config.json (bez odczytu z dysku). Identyfikatorem profilu ekranu (<see cref="ScreenProfile.Id"/>)
    /// jest zawsze jego klucz w sekcji <c>"profiles"</c> — także wtedy, gdy obiekt profilu ma pole <c>"id"</c> o innej
    /// wartości (patrz <see cref="ParseAppConfig(string, ICollection{string})"/>).
    /// </summary>
    public static AppConfig ParseAppConfig(string json) => ParseAppConfigCore(json, warnings: null);

    public static AppConfig ParseAppConfig(string json, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        return ParseAppConfigCore(json, warnings);
    }

    private static AppConfig ParseAppConfigCore(string json, ICollection<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(json);

        AppConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigException(
                $"Plik konfiguracyjny ma nieprawidłową składnię JSON i nie mógł zostać odczytany. Szczegóły: {ex.Message}",
                ex);
        }

        if (cfg is null)
        {
            throw new ConfigException("Plik konfiguracyjny jest pusty.");
        }

        // Klucz w słowniku "profiles" JEST identyfikatorem profilu: po nim maszyna wskazuje profil, a raport postojów
        // i ostrzeżenia słownika wybierają bazę błędów; alarm na żywo wybiera ją po ScreenProfile.Id. Dlatego Id = klucz
        // zawsze — gdy pola "id" nie ma i gdy ma inną wartość (np. blok skopiowany z innego profilu config.json
        // zapisanego przez program, który ma "id" w każdym profilu). Inna wartość to ostrzeżenie, nie błąd: istniejące
        // stanowisko nie może przestać startować. Profil grupy ma Id = klucz z definicji
        // (GroupFileCodec.ValidateProfile), więc odcisk profilu grupy się nie zmienia.
        RequireSection(cfg.Profiles, "profiles");
        if (cfg.Profiles.Count > 0)
        {
            var fixedProfiles = new Dictionary<string, ScreenProfile>(cfg.Profiles.Count, StringComparer.Ordinal);
            foreach ((string key, ScreenProfile profile) in cfg.Profiles)
            {
                RequireSection(profile, $"profiles.{key}");
                if (string.Equals(profile.Id, key, StringComparison.Ordinal))
                {
                    fixedProfiles[key] = profile;
                    continue;
                }

                if (!string.IsNullOrEmpty(profile.Id)) warnings?.Add(ProfileIdWarning(key, profile.Id));
                fixedProfiles[key] = profile with { Id = key };
            }

            cfg = cfg with { Profiles = fixedProfiles };
        }

        if (cfg.Cluster is { Peers: null } cluster)
        {
            cfg = cfg with { Cluster = cluster with { Peers = [] } };
        }

        return cfg;
    }

    /// <summary>Zapisuje AppConfig do postaci tekstowej JSON (do zapisu na dysk przez wywołującego).</summary>
    public static string Serialize(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return JsonSerializer.Serialize(cfg, JsonOptions);
    }

    /// <summary>Zapisuje sprawdzoną konfigurację atomowo, zachowując poprzednią jako .bak.</summary>
    public static void SaveAppConfig(string path, AppConfig cfg)
    {
        Validate(cfg);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, cfg, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            ReplaceWithRetry(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ReplaceWithRetry(string temporary, string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                if (!File.Exists(path)) File.Move(temporary, path);
                else
                {
                    try { File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true); }
                    catch (UnauthorizedAccessException)
                    {
                        File.Copy(path, path + ".bak", overwrite: true);
                        File.Move(temporary, path, overwrite: true);
                    }
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                && ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
                && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    // ---------------------------------------------------------------------
    // RuleSet (rules.json)
    // ---------------------------------------------------------------------

    /// <summary>Wczytuje i parsuje rules.json z pliku pod <paramref name="path"/>.</summary>
    public static RuleSet LoadRules(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException(
                $"Nie można odczytać pliku reguł '{path}'. Sprawdź, czy plik istnieje i czy program ma do niego dostęp. Szczegóły: {ex.Message}",
                ex);
        }

        return ParseRules(json);
    }

    /// <summary>Parsuje treść pliku rules.json (bez odczytu z dysku).</summary>
    public static RuleSet ParseRules(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        RuleSet? rules;
        try
        {
            rules = JsonSerializer.Deserialize<RuleSet>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigException(
                $"Plik reguł (rules.json) ma nieprawidłową składnię JSON i nie mógł zostać odczytany. Szczegóły: {ex.Message}",
                ex);
        }

        return rules ?? throw new ConfigException("Plik reguł (rules.json) jest pusty.");
    }

    /// <summary>Zapisuje RuleSet do postaci tekstowej JSON (do zapisu na dysk przez wywołującego).</summary>
    public static string Serialize(RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return JsonSerializer.Serialize(rules, JsonOptions);
    }

    // ---------------------------------------------------------------------
    // Walidacja
    // ---------------------------------------------------------------------

    /// <summary>
    /// Sprawdza spójność konfiguracji (unikalne id maszyn, istnienie wskazanych profili, poprawny port,
    /// niepuste host/ROI, sensowny próg czerwieni). Rzuca <see cref="ConfigException"/> z komunikatem
    /// po polsku wskazującym, co i gdzie poprawić.
    /// </summary>
    public static void Validate(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        RequireSection(cfg.Machines, "machines");
        RequireSection(cfg.Profiles, "profiles");
        RequireSection(cfg.Paths, "paths");
        RequireSection(cfg.Ocr, "ocr");
        RequireSection(cfg.Cluster, "cluster");
        RequireSection(cfg.Timing, "timing");
        RequireSection(cfg.Ui, "ui");
        RequireSection(cfg.Diagnostics, "diagnostics");
        RequireSection(cfg.Updates, "updates");
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MachineConfig m in cfg.Machines)
        {
            RequireSection(m, "machines[]");
            if (string.IsNullOrWhiteSpace(m.Id))
            {
                throw new ConfigException("W konfiguracji jest maszyna bez identyfikatora (pole 'id' jest puste) — uzupełnij je.");
            }

            if (!seenIds.Add(m.Id))
            {
                throw new ConfigException($"Identyfikator maszyny '{m.Id}' powtarza się w konfiguracji — identyfikatory (id) muszą być unikalne.");
            }

            if (m.Name is null)
            {
                throw new ConfigException($"Maszyna '{m.Id}' ma pole 'name' o wartości null — wpisz nazwę wyświetlaną na karcie maszyny.");
            }

            if (string.IsNullOrWhiteSpace(m.Host))
            {
                throw new ConfigException($"Maszyna '{m.Id}' ma puste pole 'host' — podaj adres IP lub nazwę komputera maszyny.");
            }

            if (m.RsaAesKeySha256 is { } pin
                && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
            {
                throw new ConfigException($"Maszyna '{m.Id}' ma nieprawidłowe pole 'rsaAesKeySha256' — wpisz 64 znaki szesnastkowe odcisku z logu programu albo usuń pole.");
            }

            if (m.Port is < 1 or > 65535)
            {
                throw new ConfigException($"Maszyna '{m.Id}' ma nieprawidłowy port ({m.Port}) — dozwolony zakres to 1–65535.");
            }

            if (string.IsNullOrWhiteSpace(m.Profile) || !cfg.Profiles.ContainsKey(m.Profile))
            {
                throw new ConfigException($"Maszyna '{m.Id}' wskazuje profil '{m.Profile}', którego nie ma w sekcji profiles.");
            }
        }

        foreach ((string id, ScreenProfile profile) in cfg.Profiles)
        {
            RequireSection(profile, $"profiles.{id}");
            RequireSection(profile.Red, $"profiles.{id}.red");
            ValidateHsv($"profiles.{id}.red", profile.Red.HueMinDeg, profile.Red.HueMaxDeg,
                profile.Red.MinSaturation, profile.Red.MinValue);
            if (profile.StatusBarRoi.IsEmpty)
            {
                throw new ConfigException($"Profil '{id}' ma puste pole 'statusBarRoi' (obszar paska statusu) — podaj x/y/width/height większe od zera.");
            }

            if (profile.MinRedFraction <= 0 || profile.MinRedFraction > 1)
            {
                throw new ConfigException($"Profil '{id}' ma nieprawidłową wartość 'minRedFraction' ({profile.MinRedFraction}) — dozwolony zakres to (0, 1].");
            }

            if (profile.Idle is { Enabled: true } idle)
            {
                if (idle.ProgressRoi.IsEmpty)
                {
                    throw new ConfigException($"Profil '{id}' ma puste pole 'idle.progressRoi' (obszar postępu) — podaj x/y/width/height większe od zera.");
                }

                if (idle.Mode == ProgressMode.Number)
                {
                    if (idle.FullAtPercent is < 1 or > 100)
                    {
                        throw new ConfigException($"Profil '{id}' ma nieprawidłową wartość 'idle.fullAtPercent' ({idle.FullAtPercent}) — dozwolony zakres to 1..100.");
                    }

                    if (idle.OcrScale is < 1 or > 8)
                    {
                        throw new ConfigException($"Profil '{id}' ma nieprawidłową wartość 'idle.ocrScale' ({idle.OcrScale}) — dozwolony zakres to 1..8.");
                    }

                    if (idle.OcrMarginPx is < 0 or > 100)
                    {
                        throw new ConfigException($"Profil '{id}' ma nieprawidłową wartość 'idle.ocrMarginPx' ({idle.OcrMarginPx}) — dozwolony zakres to 0..100.");
                    }
                }
                else
                {
                    RequireSection(idle.ProgressFill, $"profiles.{id}.idle.progressFill");
                    if (idle.FullAtFraction <= 0 || idle.FullAtFraction > 1)
                    {
                        throw new ConfigException($"Profil '{id}' ma nieprawidłową wartość 'idle.fullAtFraction' ({idle.FullAtFraction}) — dozwolony zakres to (0, 1].");
                    }
                }

                if (idle.ProgressBar is { Enabled: true } bar)
                {
                    ValidateProgressBar(id, idle, bar);
                }
            }
        }

        foreach ((string id, ScreenProfile profile) in cfg.Profiles)
        {
            if (profile.MainScreen is { Enabled: true } mainScreen)
            {
                ValidateMainScreen(id, mainScreen);
            }

            if (profile.ProgramState is { Enabled: true } programState)
            {
                ValidateProgramState(id, programState);
            }
        }

        ValidateTiming(cfg.Timing);

        if (cfg.Ui.AlertOpacity is < 0.2 or > 1.0)
        {
            throw new ConfigException($"ui.alertOpacity musi być w zakresie 0,2–1,0 (jest: {cfg.Ui.AlertOpacity}).");
        }

        RequireSection(cfg.Ui.Overlay, "ui.overlay");
        ValidateOverlay(cfg.Ui.Overlay);

        ValidateUpdates(cfg.Updates);

        if (cfg.Enabled && cfg.Machines.Count == 0)
            throw new ConfigException("Sekcja 'machines' jest pusta — dodaj maszyny albo świadomie wyłącz obserwację przez enabled:false.");
        if (cfg.Paths.CaptureRetentionDays < 1 || cfg.Paths.LogRetentionDays < 1)
            throw new ConfigException("Pola 'paths.captureRetentionDays' i 'paths.logRetentionDays' muszą wynosić co najmniej 1 dzień.");
        if (cfg.Paths.CapturesMaxMegabytes is < 20 or > 5000)
            throw new ConfigException($"Pole 'paths.capturesMaxMegabytes' musi być w zakresie 20–5000 MB (jest: {cfg.Paths.CapturesMaxMegabytes}).");
        foreach (var (name, value) in new[] { ("logsDir", cfg.Paths.LogsDir), ("capturesDir", cfg.Paths.CapturesDir),
            ("testScreensDir", cfg.Paths.TestScreensDir), ("historyFile", cfg.Paths.HistoryFile) })
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
                throw new ConfigException($"Pole 'paths.{name}' musi zawierać poprawną, niepustą ścieżkę.");
        }
        if (cfg.Ocr.Engine?.Trim().ToLowerInvariant() is not ("windows" or "none"))
            throw new ConfigException("Pole 'ocr.engine' obsługuje 'windows' albo 'none'; Tesseract nie jest dołączony do tej wersji.");
        if (cfg.Ocr.Language is null)
            throw new ConfigException("Pole 'ocr.language' nie może mieć wartości null — usuń je (zostanie wartość domyślna) albo wpisz np. \"pl\" lub \"en-US\".");
        cfg.Cluster.Group?.Validate();
        if (cfg.Cluster.Enabled && ClusterProblem(cfg.Cluster) is { } problem)
            throw new ConfigException(problem);
    }

    public static IReadOnlyList<string> Warnings(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Cluster is null || cfg.Cluster.Enabled || ClusterProblem(cfg.Cluster) is not { } problem) return [];
        return ["Sekcja 'cluster' jest wyłączona (\"enabled\": false), ale ma nieprawidłowe wartości — popraw je przed włączeniem grupy komputerów. " + problem];
    }

    /// <summary>
    /// Ostrzeżenie z wczytania config.json (<see cref="ParseAppConfig(string, ICollection{string})"/>): profil ekranu
    /// <paramref name="key"/> ma pole <c>"id"</c> = <paramref name="declaredId"/>, inne niż klucz (porównanie z wielkością
    /// liter, tak jak klucz alertu i instrukcji naprawy „IDLE: Abort” powstaje z <see cref="ScreenProfile.Id"/>).
    /// </summary>
    internal static string ProfileIdWarning(string key, string declaredId) =>
        $"Profil ekranu '{key}' w config.json ma pole \"id\": \"{declaredId}\", inne niż nazwa profilu (klucz w sekcji 'profiles'). " +
        $"Program używa nazwy '{key}' — według niej maszyny wskazują profil, a alarm i raport postojów wybierają bazę błędów " +
        "z rules.json — a pole \"id\" pomija (zostaje np. po skopiowaniu bloku innego profilu z config.json zapisanego przez " +
        $"program). Popraw pole na \"id\": \"{key}\" albo je usuń. Wersje do 0.4.249 włącznie wybierały w alarmie bazę błędów " +
        $"po polu \"id\" ('{declaredId}'), a w raporcie postojów — po nazwie profilu.";

    private static string? ClusterProblem(ClusterConfig c)
    {
        if (c.UdpPort is < 1 or > 65535)
            return "Pole 'cluster.udpPort' musi być w zakresie 1–65535.";
        if (c.HeartbeatIntervalMs < 100 || c.StateIntervalMs < 100 || c.PeerTimeoutMs <= c.HeartbeatIntervalMs
            || c.TakeoverDelayMs < 0 || c.ClockSkewToleranceSec < 1 || c.SleepJumpThresholdMs <= c.HeartbeatIntervalMs)
            return "Sekcja 'cluster': interwały heartbeat/state muszą wynosić ≥100 ms, peerTimeout i sleepJumpThreshold muszą być większe od heartbeatInterval, takeoverDelay ≥0, clockSkewTolerance ≥1 s.";
        if (c.DowntimeSyncIntervalMs < 10000 || c.DowntimeSyncWindowDays is < 1 or > 3650)
            return "Sekcja 'cluster': downtimeSyncIntervalMs musi wynosić ≥10 000 ms, a downtimeSyncWindowDays 1–3650 dni.";
        if (c.Peers is null || c.Peers.Any(p => !System.Net.IPAddress.TryParse(p, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            return "Pole 'cluster.peers' musi być listą poprawnych adresów IPv4.";
        return null;
    }

    private static void RequireSection(object? value, string name)
    {
        if (value is null) throw new ConfigException($"Pole '{name}' nie może mieć wartości null.");
    }

    private static void ValidateHsv(string name, double minHue, double maxHue, double saturation, double value)
    {
        foreach (var (field, number, maximum) in new[] { ("hueMinDeg", minHue, 360d), ("hueMaxDeg", maxHue, 360d),
            ("minSaturation", saturation, 1d), ("minValue", value, 1d) })
            if (!double.IsFinite(number) || number < 0 || number > maximum)
                throw new ConfigException($"Pole '{name}.{field}' musi być w zakresie 0–{maximum}.");
    }

    private static void ValidateProgressBar(string profileId, IdleWatchProfile idle, ProgressBarSource bar)
    {
        // Pasek daje LICZBĘ procent obok OCR liczby — tryb "bar" (sam „pełny / niepełny”, bez procentu)
        // ma własny detektor i własne pola, więc ta sekcja w nim nic by nie robiła, a udawałaby, że działa.
        if (idle.Mode != ProgressMode.Number)
        {
            throw new ConfigException(
                $"Profil '{profileId}' ma sekcję 'idle.progressBar' przy 'idle.mode' = \"{idle.Mode.ToString().ToLowerInvariant()}\" — pasek jako źródło procentu działa tylko w trybie \"number\" (OCR liczby jest wtedy zapasem). Ustaw \"mode\": \"number\" albo usuń sekcję 'progressBar' (lub daj w niej \"enabled\": false).");
        }

        if (bar.TrackRoi.IsEmpty || bar.TrackRoi.Width < 10)
        {
            throw new ConfigException(
                $"Profil '{profileId}' ma nieprawidłowe pole 'idle.progressBar.trackRoi' (wnętrze toru paska postępu) — podaj x/y/width/height, szerokość co najmniej 10 pikseli i wysokość większą od zera.");
        }

        if (bar.SurroundRows is < 0 or > 20)
        {
            throw new ConfigException(
                $"Profil '{profileId}' ma nieprawidłową wartość 'idle.progressBar.surroundRows' ({bar.SurroundRows}) — dozwolony zakres to 0..20.");
        }

        if (bar.TrackRoi.X < 0 || bar.TrackRoi.Y - bar.SurroundRows < 0)
        {
            throw new ConfigException(
                $"Profil '{profileId}' ma pole 'idle.progressBar.trackRoi' razem z 'surroundRows' wychodzące poza lewą albo górną krawędź ekranu — tor i {bar.SurroundRows} wierszy nad nim muszą leżeć na ekranie.");
        }

        RequireSection(bar.Fill, $"profiles.{profileId}.idle.progressBar.fill");
        ValidateHsv($"profiles.{profileId}.idle.progressBar.fill", bar.Fill.HueMinDeg, bar.Fill.HueMaxDeg,
            bar.Fill.MinSaturation, bar.Fill.MinValue);

        foreach (var (field, value) in new[]
        {
            ("trackMaxSaturation", bar.TrackMaxSaturation), ("trackMinValue", bar.TrackMinValue),
            ("surroundMaxValue", bar.SurroundMaxValue), ("columnFillMinFraction", bar.ColumnFillMinFraction),
        })
        {
            if (!double.IsFinite(value) || value <= 0 || value > 1)
            {
                throw new ConfigException(
                    $"Profil '{profileId}' ma nieprawidłową wartość 'idle.progressBar.{field}' ({value}) — dozwolony zakres to (0, 1].");
            }
        }

        if (!double.IsFinite(bar.ColumnsPerPercent) || bar.ColumnsPerPercent < 0)
        {
            throw new ConfigException(
                $"Profil '{profileId}' ma nieprawidłową wartość 'idle.progressBar.columnsPerPercent' ({bar.ColumnsPerPercent}) — musi być ≥ 0 (0 = szerokość toru / 100).");
        }

        if (!double.IsFinite(bar.ColumnsOffset))
        {
            throw new ConfigException(
                $"Profil '{profileId}' ma nieprawidłową wartość 'idle.progressBar.columnsOffset' ({bar.ColumnsOffset}) — musi być liczbą.");
        }
    }

    /// <summary>Sprawdza sekcję <c>mainScreen</c> profilu (obserwacja ekranu głównego HMI). Rzuca <see cref="ConfigException"/> z nazwą profilu i pola.</summary>
    private static void ValidateMainScreen(string profileId, MainScreenWatchProfile mainScreen)
    {
        RequireSection(mainScreen.AnchorLabels, $"profiles.{profileId}.mainScreen.anchorLabels");
        RequireSection(mainScreen.OtherScreenLabels, $"profiles.{profileId}.mainScreen.otherScreenLabels");
        for (int i = 0; i < mainScreen.AnchorLabels.Count; i++)
        {
            List<string> variant = mainScreen.AnchorLabels[i];
            if (variant is null || variant.Count == 0 || variant.Any(string.IsNullOrWhiteSpace))
            {
                throw new ConfigException(
                    $"Profil '{profileId}' ma nieprawidłowy wariant etykiety 'mainScreen.anchorLabels' na pozycji {i} — każdy wariant to niepusta lista niepustych słów, np. [\"Milling\", \"Status\"].");
            }
        }

        for (int i = 0; i < mainScreen.OtherScreenLabels.Count; i++)
        {
            List<string> variant = mainScreen.OtherScreenLabels[i];
            if (variant is null || variant.Count == 0 || variant.Any(string.IsNullOrWhiteSpace))
            {
                throw new ConfigException(
                    $"Profil '{profileId}' ma nieprawidłowy wariant napisu 'mainScreen.otherScreenLabels' na pozycji {i} — każdy wariant to niepusta lista niepustych słów, np. [\"Alarm\", \"Information\"].");
            }
        }

        if (mainScreen.MaxDistance is < 0 or > 5)
        {
            throw new ConfigException(
                $"Profil '{profileId}' ma nieprawidłową wartość 'mainScreen.maxDistance' ({mainScreen.MaxDistance}) — dozwolony zakres to 0..5.");
        }
    }

    private static void ValidateProgramState(string profileId, ProgramStateProfile state)
    {
        string prefix = $"Profil '{profileId}' ma nieprawidłową sekcję 'programState'";
        if (state.BoxRoi.IsEmpty)
        {
            throw new ConfigException($"{prefix}: pole 'programState.boxRoi' (pudełko stanu programu) jest puste — podaj x/y/width/height większe od zera.");
        }

        if (!double.IsFinite(state.RunMinFraction) || state.RunMinFraction <= 0 || state.RunMinFraction > 1)
        {
            throw new ConfigException($"{prefix}: 'programState.runMinFraction' ({state.RunMinFraction}) — dozwolony zakres to (0, 1].");
        }

        RequireSection(state.RunFill, $"profiles.{profileId}.programState.runFill");
        ValidateHsv($"profiles.{profileId}.programState.runFill", state.RunFill.HueMinDeg, state.RunFill.HueMaxDeg,
            state.RunFill.MinSaturation, state.RunFill.MinValue);

        if (state.AbortMaxDistance is < 0 or > 2)
        {
            throw new ConfigException($"{prefix}: 'programState.abortMaxDistance' ({state.AbortMaxDistance}) — dozwolony zakres to 0..2.");
        }

        // Słowo, z którego po normalizacji (tylko litery a–z) zostają mniej niż 3 litery, nigdy by nie
        // zadziałało albo pasowałoby do byle czego — lepiej odmówić startu z jasnym komunikatem.
        if (state.AbortWords is null || state.AbortWords.Count == 0
            || state.AbortWords.Any(w => string.IsNullOrWhiteSpace(w) || Vision.ProgramStateDetector.NormalizeWord(w).Length < 3))
        {
            throw new ConfigException(
                $"{prefix}: 'programState.abortWords' musi być niepustą listą słów, z których każde ma co najmniej 3 litery, np. [\"abort\"].");
        }

        if (state.AbortPattern is { } pattern)
        {
            IReadOnlyList<string>? rows = pattern.Rows;
            if (rows is null || rows.Count == 0 || rows.Any(r => string.IsNullOrEmpty(r))
                || rows.Any(r => r.Length != rows[0].Length) || rows.Any(r => r.Any(c => c is not ('#' or '.'))))
            {
                throw new ConfigException(
                    $"{prefix}: 'programState.abortPattern.rows' musi być niepustą listą wierszy tej samej długości, złożonych wyłącznie ze znaków '#' i '.'.");
            }

            var patternRect = new RectInt(pattern.X, pattern.Y, rows[0].Length, rows.Count);
            if (patternRect.Intersect(state.BoxRoi) != patternRect)
            {
                throw new ConfigException(
                    $"{prefix}: wzorzec 'programState.abortPattern' ({pattern.X},{pattern.Y}, {patternRect.Width}×{patternRect.Height}) nie mieści się w pudełku 'programState.boxRoi'.");
            }

            if (pattern.MaxMismatch < 0)
            {
                throw new ConfigException($"{prefix}: 'programState.abortPattern.maxMismatch' ({pattern.MaxMismatch}) nie może być ujemne.");
            }

            if (pattern.MaxShift is < 0 or > 3)
            {
                throw new ConfigException($"{prefix}: 'programState.abortPattern.maxShift' ({pattern.MaxShift}) — dozwolony zakres to 0..3.");
            }

            if (pattern.WhiteMinChannel is < 1 or > 255)
            {
                throw new ConfigException($"{prefix}: 'programState.abortPattern.whiteMinChannel' ({pattern.WhiteMinChannel}) — dozwolony zakres to 1..255.");
            }
        }
    }

    public static void ValidateOverlay(OverlayConfig overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        if (double.IsNaN(overlay.Opacity) || overlay.Opacity is < 0.2 or > 1.0)
        {
            throw new ConfigException($"ui.overlay.opacity musi być w zakresie 0,2–1,0 (jest: {overlay.Opacity}).");
        }

        if (double.IsNaN(overlay.Scale) || overlay.Scale is < 0.6 or > 2.0)
        {
            throw new ConfigException($"ui.overlay.scale musi być w zakresie 0,6–2,0 (jest: {overlay.Scale}).");
        }

        if (!Enum.IsDefined(overlay.Position))
        {
            throw new ConfigException(
                $"ui.overlay.position musi być jedną z wartości: top-left, top-right, bottom-left, bottom-right (jest: {overlay.Position}).");
        }

        if (!Enum.IsDefined(overlay.Layout))
        {
            throw new ConfigException(
                $"ui.overlay.layout musi być jedną z wartości: {OverlayLayouts.NamesText} (jest: {overlay.Layout}).");
        }
    }

    public static void ValidateUpdates(UpdatesConfig updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        if (string.IsNullOrWhiteSpace(updates.Url)
            || !Uri.TryCreate(updates.Url.Trim(), UriKind.Absolute, out Uri? uri)
            || !(uri.Scheme == Uri.UriSchemeHttps
                 || (uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1")))
        {
            throw new ConfigException(
                $"Pole 'updates.url' musi być pełnym adresem https:// katalogu wydań (http:// tylko dla localhost/127.0.0.1) (jest: '{updates.Url}').");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ConfigException(
                "Pole 'updates.url' nie może zawierać nazwy użytkownika ani hasła (użytkownik:hasło@) — dostęp do katalogu wydań daje token w Ustawieniach.");
        }

        if (updates.CheckIntervalMinutes is < 5 or > 1440)
        {
            throw new ConfigException(
                $"Pole 'updates.checkIntervalMinutes' musi być w zakresie 5–1440 minut (jest: {updates.CheckIntervalMinutes}).");
        }

        if (updates.AccessClientId is { } clientId && (clientId.Length > 200 || clientId.Any(char.IsControl)))
        {
            throw new ConfigException(
                "Pole 'updates.accessClientId' może mieć najwyżej 200 znaków i nie może zawierać znaków sterujących (np. końca wiersza).");
        }
    }

    public static void ValidateTiming(TimingConfig timing)
    {
        ArgumentNullException.ThrowIfNull(timing);

        if (timing.PollIntervalMs < 100)
        {
            throw new ConfigException($"Pole 'timing.pollIntervalMs' ({timing.PollIntervalMs}) musi wynosić co najmniej 100 ms.");
        }

        if (timing.PollingModeIntervalMs < 500)
        {
            throw new ConfigException($"Pole 'timing.pollingModeIntervalMs' ({timing.PollingModeIntervalMs}) musi wynosić co najmniej 500 ms.");
        }

        if (timing.FullFrameIntervalMs < timing.PollIntervalMs)
        {
            throw new ConfigException(
                $"Pole 'timing.fullFrameIntervalMs' ({timing.FullFrameIntervalMs}) nie może być mniejsze niż 'timing.pollIntervalMs' ({timing.PollIntervalMs}).");
        }

        if (timing.EpisodeCloseAfterMs < 1000)
        {
            throw new ConfigException($"Pole 'timing.episodeCloseAfterMs' ({timing.EpisodeCloseAfterMs}) musi wynosić co najmniej 1000 ms.");
        }

        if (timing.EpisodeBreakAfterMs < 0)
        {
            throw new ConfigException($"Pole 'timing.episodeBreakAfterMs' ({timing.EpisodeBreakAfterMs}) nie może być ujemne.");
        }

        if (timing.EpisodeBreakAfterMs > timing.EpisodeCloseAfterMs)
        {
            throw new ConfigException(
                $"Pole 'timing.episodeBreakAfterMs' ({timing.EpisodeBreakAfterMs}) nie może być większe niż 'timing.episodeCloseAfterMs' ({timing.EpisodeCloseAfterMs}).");
        }

        if (timing.SameMessageRejoinMs < 0)
        {
            throw new ConfigException($"Pole 'timing.sameMessageRejoinMs' ({timing.SameMessageRejoinMs}) nie może być ujemne (0 = bez łączenia powracającego komunikatu).");
        }

        if (timing.SameMessageRejoinMs > 0 && timing.SameMessageRejoinMs < timing.EpisodeBreakAfterMs)
        {
            throw new ConfigException(
                $"Pole 'timing.sameMessageRejoinMs' ({timing.SameMessageRejoinMs}) nie może być mniejsze niż 'timing.episodeBreakAfterMs' ({timing.EpisodeBreakAfterMs}).");
        }

        if (timing.EscalationAfterAckMs < 0)
        {
            throw new ConfigException($"Pole 'timing.escalationAfterAckMs' ({timing.EscalationAfterAckMs}) nie może być ujemne (0 = eskalacja wyłączona).");
        }

        if (timing.AlertSoundRepeatMs < 1000)
        {
            throw new ConfigException($"Pole 'timing.alertSoundRepeatMs' ({timing.AlertSoundRepeatMs}) musi wynosić co najmniej 1000 ms.");
        }

        if (timing.MainScreenCheckIntervalMs < 1000)
        {
            throw new ConfigException($"Pole 'timing.mainScreenCheckIntervalMs' ({timing.MainScreenCheckIntervalMs}) musi wynosić co najmniej 1000 ms.");
        }

        if (timing.MainScreenOcrGraceMs < 5000)
        {
            throw new ConfigException($"Pole 'timing.mainScreenOcrGraceMs' ({timing.MainScreenOcrGraceMs}) musi wynosić co najmniej 5000 ms.");
        }

        if (timing.WrongScreenAlertAfterMs < 0)
        {
            throw new ConfigException($"Pole 'timing.wrongScreenAlertAfterMs' ({timing.WrongScreenAlertAfterMs}) nie może być ujemne (0 = alert od razu).");
        }

        if (timing.WrongScreenRemindAfterMs < 0)
        {
            throw new ConfigException($"Pole 'timing.wrongScreenRemindAfterMs' ({timing.WrongScreenRemindAfterMs}) nie może być ujemne (0 = bez ponawiania).");
        }

        if (timing.OfflineAlertAfterMs < 0)
        {
            throw new ConfigException($"Pole 'timing.offlineAlertAfterMs' ({timing.OfflineAlertAfterMs}) nie może być ujemne.");
        }

        if (timing.OcrMaxAttempts < 0)
        {
            throw new ConfigException($"Pole 'timing.ocrMaxAttempts' ({timing.OcrMaxAttempts}) nie może być ujemne.");
        }

        if (timing.OcrRetryIntervalMs < 0)
        {
            throw new ConfigException($"Pole 'timing.ocrRetryIntervalMs' ({timing.OcrRetryIntervalMs}) nie może być ujemne.");
        }

        if (timing.ConnectTimeoutMs < 500)
        {
            throw new ConfigException($"Pole 'timing.connectTimeoutMs' ({timing.ConnectTimeoutMs}) musi wynosić co najmniej 500 ms.");
        }

        if (timing.ReadTimeoutMs < 1000)
        {
            throw new ConfigException($"Pole 'timing.readTimeoutMs' ({timing.ReadTimeoutMs}) musi wynosić co najmniej 1000 ms.");
        }

        if (timing.IdleUpdateTimeoutMs < 100 || timing.IdleUpdateTimeoutMs > timing.ReadTimeoutMs)
        {
            throw new ConfigException(
                $"Pole 'timing.idleUpdateTimeoutMs' ({timing.IdleUpdateTimeoutMs}) musi mieścić się w zakresie od 100 ms do wartości 'timing.readTimeoutMs' ({timing.ReadTimeoutMs}) włącznie.");
        }

        for (int i = 0; i < timing.ReconnectBackoffMs.Length; i++)
        {
            if (timing.ReconnectBackoffMs[i] < 0)
            {
                throw new ConfigException(
                    $"Pole 'timing.reconnectBackoffMs' zawiera ujemną wartość ({timing.ReconnectBackoffMs[i]}) na pozycji {i + 1}.");
            }
        }

        if (timing.IdleAlertAfterMs < 10000)
        {
            throw new ConfigException($"Pole 'timing.idleAlertAfterMs' ({timing.IdleAlertAfterMs}) musi wynosić co najmniej 10000 ms.");
        }

        // Krótszy próg niż 30 s zamieniłby normalny rozruch nowej pracy (postęp stoi na 0%, zanim
        // maszyna zafrezuje pierwszy procent) w fałszywy alert „wciśnij Start” — patrz TimingConfig.IdleAtZeroAfterMs.
        if (timing.IdleAtZeroAfterMs < 30000)
        {
            throw new ConfigException($"Pole 'timing.idleAtZeroAfterMs' ({timing.IdleAtZeroAfterMs}) musi wynosić co najmniej 30000 ms.");
        }

        if (timing.IdlePopupSeconds is < 0 or > 60)
        {
            throw new ConfigException(
                $"Pole 'timing.idlePopupSeconds' ({timing.IdlePopupSeconds}) musi mieścić się w zakresie 0..60 " +
                "(0 = pop-up czeka na skasowanie przez operatora).");
        }
    }

    public static void Validate(RuleSet rules, AppConfig? cfg)
    {
        ArgumentNullException.ThrowIfNull(rules);

        // Ręcznie edytowany plik może mieć "codes": null, "rules": null albo "databases": null — deserializator
        // wpisuje wtedy null w miejsce domyślnej pustej listy. Bez tej kontroli program kończył się przy
        // starcie wyjątkiem NullReferenceException, bez komunikatu dla operatora.
        if (rules.Codes is null || rules.Rules is null || rules.Databases is null)
        {
            throw new ConfigException(
                "W rules.json sekcja 'codes', 'rules' albo 'databases' ma wartość null — usuń ją albo wpisz pustą listę [] (dla 'databases' pusty obiekt {}).");
        }

        ValidateCodes(rules.Codes, "listach wspólnych");

        // Identyfikatory reguł muszą być unikalne w CAŁYM pliku (a nie tylko w jednej bazie): to z nich
        // powstaje klucz alertu i klucz zapisanej instrukcji naprawy, więc dwie różne reguły o tym samym
        // id dzieliłyby instrukcję mimo że opisują błędy różnych maszyn.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        ValidateRules(rules.Rules, seenIds, cfg, "listach wspólnych", insideDatabase: false);

        // Przedrostki numerów alarmów: pusty ma najwyżej JEDNA baza z kodami, i tylko gdy wspólna lista
        // kodów jest pusta — inaczej „code-705” z dwóch źródeł znaczyłoby dwa różne błędy pod jednym kluczem.
        var seenPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rules.Codes.Count > 0)
        {
            seenPrefixes[string.Empty] = "wspólna lista kodów";
        }

        // Nazwy baz porównuje się BEZ wielkości liter — tak bazę maszyny wybierają Ocr.MessageMapper (alarm na żywo) i raport
        // postojów (History.DowntimeCategoryClassifier). Słownik wczytany z pliku rozróżnia wielkość liter, więc bez tej
        // kontroli dwie takie bazy przechodziły walidację, a o tym, której używa maszyna, decydowała kolejność w pliku.
        var seenKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, RuleDatabase> entry in rules.Databases)
        {
            string key = entry.Key;
            RuleDatabase? database = entry.Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ConfigException("W sekcji 'databases' pliku rules.json jest baza błędów bez nazwy profilu (pusty klucz) — wpisz identyfikator profilu z sekcji 'profiles' w config.json.");
            }

            if (!seenKeys.TryAdd(key, key))
            {
                throw new ConfigException(
                    $"Bazy błędów '{seenKeys[key]}' i '{key}' w rules.json różnią się tylko wielkością liter. Program dobiera bazę " +
                    "do profilu ekranu maszyny bez rozróżniania wielkości liter, więc nie wie, której z nich użyć. Zostaw jedną: " +
                    "przenieś kody i reguły do bazy nazwanej dokładnie jak profil z sekcji 'profiles' w config.json, a drugą usuń.");
            }

            if (database is null)
            {
                throw new ConfigException($"Baza błędów '{key}' w rules.json jest pusta (null) — usuń wpis albo uzupełnij go sekcjami 'codes'/'rules'.");
            }

            // Baza bez profilu ekranu o tym kluczu NIE jest tu błędem — patrz Warnings(RuleSet, AppConfig).

            if (database.Codes is null || database.Rules is null)
            {
                throw new ConfigException($"Baza błędów '{key}' w rules.json ma sekcję 'codes' albo 'rules' o wartości null — usuń ją albo wpisz pustą listę [].");
            }

            string where = $"bazie '{key}'";
            ValidateCodes(database.Codes, where);
            ValidateRules(database.Rules, seenIds, cfg, where, insideDatabase: true);

            if (!string.IsNullOrWhiteSpace(database.CodePattern))
            {
                try
                {
                    _ = new Regex(database.CodePattern, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    throw new ConfigException($"Baza błędów '{key}' ma nieprawidłowe wyrażenie regularne w polu 'codePattern': {ex.Message}", ex);
                }
            }

            // Przedrostek pilnujemy także w bazie BEZ kodów: numer spoza słownika też trafia do klucza
            // alertu i instrukcji, więc dwie bazy z tym samym przedrostkiem zlałyby
            // swoje numery w jeden wpis.
            string prefix = database.CodeIdPrefix ?? string.Empty;
            if (seenPrefixes.TryGetValue(prefix, out string? owner))
            {
                throw new ConfigException(prefix.Length == 0
                    ? $"Baza błędów '{key}' nie ma własnego przedrostka numerów alarmów ('codeIdPrefix'), a bez przedrostka jest już {owner}. " +
                      $"Pusty przedrostek może mieć tylko jedno źródło numerów — pozostałym nadaj własny, np. \"codeIdPrefix\": \"{key}-\"."
                    : $"Przedrostek numerów alarmów '{prefix}' ('codeIdPrefix') powtarza się: mają go {owner} i baza '{key}'. Przedrostek musi być unikalny.");
            }

            seenPrefixes[prefix] = $"baza '{key}'";
        }
    }

    public static IReadOnlyList<string> Warnings(RuleSet rules, AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(cfg);
        if (rules.Databases is null || cfg.Profiles is null) return [];

        return [.. rules.Databases.Keys
            .Where(key => !string.IsNullOrWhiteSpace(key)
                && !cfg.Profiles.Keys.Any(profile => string.Equals(profile, key, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key =>
                $"Baza błędów '{key}' w rules.json (w grupie komputerów: w profilu grupy) nie odpowiada żadnemu profilowi ekranu " +
                "z sekcji 'profiles' — nie używa jej żadna maszyna, więc program ją pomija i działa dalej. Jeśli to literówka, " +
                "popraw nazwę bazy albo profilu: do tego czasu maszyny profilu bez własnej bazy dostają opisy z list wspólnych " +
                "(we wzorcowym rules.json pustych — „Błąd: <odczytany tekst>”), a alarm z koloru działa bez zmian. Brakujący " +
                "profil przenieś z nowszego config.example.json (sekcja 'profiles').")];
    }

    /// <summary>
    /// Sprawdza listę reguł jednej bazy błędów (albo listy wspólnej): niepuste i niepowtarzające się
    /// identyfikatory w CAŁYM pliku, poprawne wyrażenia regularne, istniejące profile. Reguła WEWNĄTRZ bazy
    /// nie może mieć pola <c>profile</c> — baza już jest przypisana do typu maszyn i drugie przypisanie
    /// tylko myli.
    /// </summary>
    private static void ValidateRules(IReadOnlyList<Rule> rules, HashSet<string> seenIds, AppConfig? cfg, string where, bool insideDatabase)
    {
        foreach (Rule? r in rules)
        {
            if (r is null)
            {
                throw new ConfigException($"W {where} (rules.json) lista 'rules' zawiera element null — usuń go albo wpisz pełną regułę.");
            }

            if (string.IsNullOrWhiteSpace(r.Id))
            {
                throw new ConfigException($"W {where} (rules.json) jest reguła bez identyfikatora (pole 'id' jest puste) — uzupełnij je.");
            }

            if (!seenIds.Add(r.Id))
            {
                throw new ConfigException($"Identyfikator reguły '{r.Id}' powtarza się w rules.json — identyfikatory (id) muszą być unikalne w całym pliku, także między bazami błędów.");
            }

            if (r.Phrases is null)
            {
                throw new ConfigException($"Reguła '{r.Id}' w {where} (rules.json) ma pole 'phrases' o wartości null — wpisz listę fraz albo pustą listę [].");
            }

            if (!string.IsNullOrEmpty(r.Pattern))
            {
                try
                {
                    _ = new Regex(r.Pattern, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    throw new ConfigException($"Reguła '{r.Id}' ma nieprawidłowe wyrażenie regularne w polu 'pattern': {ex.Message}", ex);
                }
            }

            if (insideDatabase && !string.IsNullOrEmpty(r.Profile))
            {
                throw new ConfigException(
                    $"Reguła '{r.Id}' w {where} ma pole 'profile' ('{r.Profile}') — reguła w bazie błędów dotyczy z definicji tylko tej bazy. Usuń to pole.");
            }

            if (cfg is not null && !string.IsNullOrEmpty(r.Profile) && !cfg.Profiles.ContainsKey(r.Profile))
            {
                throw new ConfigException($"Reguła '{r.Id}' wskazuje profil '{r.Profile}', którego nie ma w sekcji profiles.");
            }
        }
    }

    private static void ValidateCodes(IReadOnlyList<AlarmCode> codes, string where)
    {
        // (from, to, label) każdego poprawnego wpisu — do wykrycia nakładających się numerów.
        var spans = new List<(int From, int To, string Label)>(codes.Count);
        foreach (AlarmCode? c in codes)
        {
            if (c is null)
            {
                throw new ConfigException($"Lista 'codes' w rules.json ({where}) zawiera element null — usuń go albo wpisz pełny kod alarmu.");
            }

            if (string.IsNullOrWhiteSpace(c.ShortText))
            {
                throw new ConfigException($"Kod alarmu '{c.Label}' w rules.json ({where}, sekcja codes) ma pusty opis (pole 'shortText') — uzupełnij go.");
            }

            int from;
            int to;
            if (c.IsRange)
            {
                if (c.Code.HasValue)
                {
                    throw new ConfigException($"Kod alarmu '{c.Label}' w rules.json ({where}, sekcja codes) ma jednocześnie pole 'code' i zakres 'from'/'to' — zostaw jedno z nich.");
                }

                if (!c.From.HasValue || !c.To.HasValue || c.From.Value > c.To.Value)
                {
                    throw new ConfigException($"Kod alarmu '{c.Label}' w rules.json ({where}, sekcja codes): zakres wymaga obu pól 'from' i 'to', przy czym from ≤ to.");
                }

                from = c.From.Value;
                to = c.To.Value;
            }
            else if (c.Code.HasValue)
            {
                from = to = c.Code.Value;
            }
            else
            {
                throw new ConfigException($"Wpis kodu alarmu \"{c.ShortText}\" w rules.json ({where}, sekcja codes) nie ma numeru — podaj pole 'code' albo zakres 'from'/'to'.");
            }

            if (from < 0)
            {
                throw new ConfigException($"Kod alarmu '{c.Label}' w rules.json ({where}, sekcja codes) ma ujemny numer.");
            }

            foreach ((int otherFrom, int otherTo, string otherLabel) in spans)
            {
                if (from <= otherTo && to >= otherFrom)
                {
                    throw new ConfigException($"Kody alarmów '{otherLabel}' i '{c.Label}' w rules.json ({where}, sekcja codes) nakładają się — każdy numer może mieć tylko jeden wpis.");
                }
            }

            spans.Add((from, to, c.Label));
        }
    }

    /// <summary>Konwerter RectInt jako obiekt JSON {"x","y","width","height"} (zamiast domyślnej serializacji rekordu).</summary>
    private sealed class RectIntJsonConverter : JsonConverter<RectInt>
    {
        public override RectInt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Oczekiwano obiektu {x, y, width, height} dla pola opisującego prostokąt (ROI).");
            }

            int x = 0, y = 0, width = 0, height = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Nieprawidłowy format obiektu opisującego prostokąt (ROI).");
                }

                string propertyName = reader.GetString() ?? "";
                reader.Read();
                int value = reader.GetInt32();
                switch (propertyName.ToLowerInvariant())
                {
                    case "x": x = value; break;
                    case "y": y = value; break;
                    case "width": width = value; break;
                    case "height": height = value; break;
                }
            }

            return new RectInt(x, y, width, height);
        }

        public override void Write(Utf8JsonWriter writer, RectInt value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("width", value.Width);
            writer.WriteNumber("height", value.Height);
            writer.WriteEndObject();
        }
    }
}
