using System.IO;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.App.Services;

public sealed class AppOrchestrator : BackgroundService
{
    private static readonly AlertKey TestAlertKey = new("test", "test");

    /// <summary>Klucz lokalnego alertu testowego "postój" (menu zasobnika, patrz <see cref="TestIdlePopup"/>).</summary>
    private static readonly AlertKey TestIdleAlertKey = AlertKey.Idle("test");

    /// <summary>Klucz lokalnego alertu testowego "niewłaściwy ekran HMI" (menu zasobnika, patrz <see cref="TestWrongScreenPopup"/>).</summary>
    private static readonly AlertKey TestScreenAlertKey = AlertKey.WrongScreen("test");

    private const int MaintenanceEveryTicks = 600; // 600 x 1 s = 10 min

    /// <summary>
    /// Maksymalny czas życia lokalnego testowego alertu postoju, licząc od <see cref="Alert.StartedAt"/>, NAWET
    /// gdy nikt go nie skasuje (w odróżnieniu od <see cref="_testAlert"/>, który wisi bez limitu, dopóki nie
    /// zostanie potwierdzony — test postoju ma dodatkowo górny limit, żeby zapomniane okno testowe samo zniknęło).
    /// </summary>
    private static readonly TimeSpan TestIdleAlertMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Okno przekazywane do <see cref="AlertStateMachine.RecentlyClosedKeys"/> / <see cref="ClusterRuntime.SetLocalState"/>
    /// — musi pokrywać się z oknem po stronie odbiorcy w <c>ClusterRuntime</c> (patrz jego dokumentacja), żeby
    /// zapomnienie starego ACK-u po zamknięciu epizodu dotarło do wszystkich komputerów.
    /// </summary>
    private static readonly TimeSpan RecentlyClosedWindow = TimeSpan.FromMinutes(10);

    private readonly ClusterRuntime _cluster;
    private readonly MonitorSupervisor _supervisor;
    private readonly AlertStateMachine _alerts;
    private readonly HistoryWriter _history;
    public DowntimeJournal Downtime { get; }

    public ObservationJournal Observation { get; }

    /// <summary>Komunikat, gdy wpisy historii alertów czekają w pamięci na zapis history.csv; inaczej null.</summary>
    public string? HistoryError => _history.Error;
    public AlarmHelpService Help { get; }
    private int _downtimeTicks;
    private readonly HashSet<(string, DateTimeOffset)> _testDowntimeEpisodes = new();
    private readonly object _downtimeGate = new();
    private readonly PowerModeWatcher _power;
    private readonly IClock _clock;
    private AppConfig _cfg;

    /// <summary>
    /// Konfiguracja BEZ warstwy z Panelu (config.json + profil grupy) — patrz <see cref="BaseConfig"/>.
    /// Każda zmiana warstwy liczy nową efektywną od niej, nigdy od poprzedniego wyniku.
    /// </summary>
    private AppConfig _baseCfg;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly ISecretStore _secrets;
    private IOcrEngine _ocr;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly ILogger<AppOrchestrator> _logger;

    private Alert? _testAlert;
    private Alert? _testIdleAlert;
    private Alert? _testScreenAlert;

    /// <summary>
    /// Zatrzymywanie <see cref="MonitorSupervisor"/> uruchomione w tle po utracie roli obserwatora (patrz
    /// <see cref="TickOnceAsync"/>) — <see cref="MonitorSupervisor.StopAsync"/> może trwać do 10 s, a tik
    /// klastra (heartbeaty co 1 s, timeout peera 15 s) nie może na to czekać. Dopóki to
    /// zadanie nie jest ukończone, tik NIE wywołuje <see cref="MonitorSupervisor.StartAsync"/> nawet, gdy
    /// rola obserwatora wróci — inaczej nowy zestaw monitorów startowałby, gdy stary jeszcze żyje (dwa
    /// komplety połączeń VNC do tych samych maszyn). <see langword="null"/>, gdy nic nie jest w toku.
    /// </summary>
    private Task? _stopping;

    /// <summary>
    /// Czy ostrzeżenie "Czekam na hasła VNC" zostało już zalogowane dla BIEŻĄCEGO oczekiwania (patrz
    /// <see cref="TickOnceAsync"/>) — żeby nie powtarzać go co sekundę, dopóki żadna włączona maszyna nie
    /// ma zapisanego hasła. Resetowane (na <see langword="false"/>) w chwili, gdy hasła się pojawią i
    /// nadzorca faktycznie startuje, żeby KOLEJNE takie oczekiwanie (np. po usunięciu wszystkich haseł w
    /// Ustawieniach) znów zalogowało ostrzeżenie.
    /// </summary>
    private bool _waitingForVncPasswordsWarned;

    /// <summary>
    /// Zastosowanie ustawień grupy w jednej transakcji: przygotowanie przez helper UAC (poza bramką cyklu),
    /// przełączenie runtime pod <see cref="_cycleGate"/> i zatwierdzenie. Bramka jest zwalniana także po
    /// wyjątku podczas tworzenia migawki; rollback runtime wykonuje wszystkie kroki mimo błędu jednego
    /// z nich, a nieudane cofnięcie helpera nie zasłania pierwotnego błędu i oznacza przerwaną generację.
    /// </summary>
    public async Task ApplyGroupSettingsAsync(GroupSettingsProposal proposal, CancellationToken ct = default)
    {
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previousConfig = _cfg;
            var previousBase = _baseCfg;
            var previousProfile = GroupProfile.Capture(_cfg, Help.Rules, Help.ProfileInstructions);
            // Re-import is the operator's way out of a committed generation whose DPAPI key is gone, so the
            // pointer is read without the key; a missing key only means the rollback returns to local fallback.
            var previousRecovery = GroupSettingsClient.OpenStore().ReadForStartup();
            string? previousKey = previousRecovery.Data is not null ? TryReadCommittedKey() : _secrets.Get(SecretKeys.ClusterHmacKey);
            var data = proposal.GetData();
            // Nowa baza to profil grupy nałożony na config.json (NIE na dotychczasowy wynik), a warstwa
            // z Panelu wraca na wierzch: inaczej zatwierdzenie ustawień grupy skasowałoby wspólną
            // konfigurację albo — przy drugim zastosowaniu — utrwaliło ją w bazie.
            var nextBase = data.ApplyTo(_baseCfg);
            var nextConfig = EffectiveAfterGroupSettings(SharedConfig.Current, nextBase, _cluster.Configuration, data.Cluster);
            var nextKey = proposal.ExportInvitation().NetworkKey;
            // Preparation/UAC leaves normal observation and heartbeats running.
            var writer = await GroupSettingsClient.PrepareAsync(proposal, previousRecovery.CommittedId, ct).ConfigureAwait(false);
            Exception? failure = null;
            try
            {
                await ApplyPreparedAsync(writer, previousConfig, previousBase, previousProfile, previousKey, data,
                    nextConfig, nextBase, nextKey, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                // Abort/cleanup of the helper must neither hide the original failure nor leave the change unknown.
                try { await writer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (failure is not null)
                {
                    // The helper undoes its own attempt even when its answer is lost; only the journal says whether it did.
                    _cluster.InterruptedGeneration = ResolveInterrupted(writer.Generation);
                    _logger.LogError(ex, "Nie potwierdzono cofnięcia przygotowanej generacji {Generation} po nieudanym zastosowaniu ustawień grupy (przerwana: {Interrupted}).",
                        writer.Generation, _cluster.InterruptedGeneration is not null);
                }
            }
            // ApplyPreparedAsync wraca bez wyjątku wyłącznie wtedy, gdy nowa generacja działa (zatwierdzona — także po utraconej
            // odpowiedzi na zatwierdzenie); po wycofaniu rzuca. Od teraz program używa jej słownika i profili — ostrzeżenia do logu
            // jak przy starcie.
            LogConfigWarnings(_logger, nextBase, data.Profile.Rules);
        }
        finally { _applyGate.Release(); }
    }

    internal static AppConfig EffectiveAfterGroupSettings(SharedConfigDocument? layer, AppConfig nextBase, ClusterConfig current, ClusterConfig next)
    {
        ArgumentNullException.ThrowIfNull(nextBase);
        if (layer is not null && ClusterRuntime.JoinsAnotherGroup(current, next)) layer = layer with { Machines = null };
        return layer?.ApplyTo(nextBase) ?? nextBase;
    }

    internal static IReadOnlyList<string> LogConfigWarnings(ILogger logger, AppConfig config, RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(logger);
        IReadOnlyList<string> warnings = [.. ConfigLoader.Warnings(config), .. ConfigLoader.Warnings(rules, config)];
        foreach (string warning in warnings) logger.LogWarning("{Warning}", warning);
        return warnings;
    }

    private string? TryReadCommittedKey()
    {
        try { return GroupSettingsClient.OpenStore().ReadCommittedNetworkKey(); }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Brak klucza zatwierdzonej generacji; rollback zmiany ustawień wróci do obserwacji lokalnej.");
            return null;
        }
    }

    /// <summary>The generation counts as interrupted only while the durable journal still holds it
    /// (pending or, after a runtime rollback, committed). An unreadable journal is reported as interrupted.</summary>
    private Guid? ResolveInterrupted(Guid generation)
    {
        try
        {
            var recovery = GroupSettingsClient.OpenStore().ReadForStartup();
            return recovery.InterruptedId == generation || recovery.CommittedId == generation ? generation : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie odczytano dziennika ustawień grupy; generację {Generation} zgłaszam jako przerwaną.", generation);
            return generation;
        }
    }

    private async Task ApplyPreparedAsync(GroupSettingsClient writer, AppConfig previousConfig, AppConfig previousBase,
        GroupProfile previousProfile, string? previousKey, GroupSettingsData data, AppConfig nextConfig,
        AppConfig nextBase, string nextKey, CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var episodes = _alerts.ActiveAlerts;
            bool wasRunning = _supervisor.IsRunning;
            bool switched = false;
            bool otherGroup = previousConfig.Cluster.Group is { } oldGroup && oldGroup.GroupId != nextConfig.Cluster.Group?.GroupId;
            bool observationChanged = GroupFileCodec.ProfileHash(previousProfile with { Instructions = [] })
                != GroupFileCodec.ProfileHash(data.Profile with { Instructions = [] });
            var oldOcr = _ocr is SerializingOcrEngine serial ? serial.Inner : _ocr;
            try
            {
                if (observationChanged || otherGroup)
                {
                    await _supervisor.StopAsync().ConfigureAwait(false);
                    await _supervisor.StoppedTask.WaitAsync(ct).ConfigureAwait(false);
                }
                if (nextConfig.Ocr != previousConfig.Ocr)
                {
                    var serializing = _ocr as SerializingOcrEngine ?? new SerializingOcrEngine(_ocr);
                    await serializing.ReplaceInnerAsync(() => nextConfig.Ocr.Engine == "windows"
                        ? new WindowsOcrEngine(nextConfig.Ocr.Language) : new NullOcrEngine(), ct).ConfigureAwait(false);
                    _ocr = serializing;
                }
                if (observationChanged) await _supervisor.ReconfigureAsync(nextConfig, new MessageMapper(data.Profile.Rules), _ocr, ct).ConfigureAwait(false);
                if (otherGroup) _alerts.RestoreObservation(episodes, preserveAcknowledgements: false);
                _alerts.ConfigureTiming(nextConfig.Timing);
                _alerts.UseGroupProtocol(nextConfig.Cluster.Enabled && nextConfig.Cluster.Group is not null);
                _cfg = nextConfig; _baseCfg = nextBase; UpdateReadiness(); ScheduleFirewallStartupCheck();
                UseGroupInstructions(nextConfig.Cluster);
                _cluster.SetLocalState(_alerts.ActiveAlerts, _supervisor.Snapshots);
                await _cluster.SwitchAsync(nextConfig.Cluster, nextKey, ct).ConfigureAwait(false);
                _watcherOverlap.Reset(); // nowa grupa — „dwóch obserwatorów” liczone od nowa
                switched = true;
                if (_cluster.DetachedMachines is { } detached)
                    _logger.LogInformation("Zmiana grupy na „{Group}”: zdjęto przełączniki maszyn zapisane na tym komputerze poza tą grupą "
                        + "(włączone: {Enabled}; wyłączone: {Disabled}) — obowiązują przełączniki grupy.",
                        nextConfig.Cluster.Group?.GroupName, SwitchNames(detached, previousConfig, enabled: true),
                        SwitchNames(detached, previousConfig, enabled: false));
                Help.UseProfile(data.Profile);
                if (wasRunning && _cluster.IsWatcher) await _supervisor.StartAsync(ct).ConfigureAwait(false);
                await writer.CommitAsync(ct).ConfigureAwait(false);
                _cluster.InterruptedGeneration = null;
            }
            catch (Exception failure)
            {
                // Resolve a lost commit response from the durable pointer before any rollback. A failing
                // read must not prevent the rollback: the change is then reported as interrupted.
                bool committed = false;
                bool uncertain = failure is GroupSettingsHelperException { Outcome: GroupSettingsHelperOutcome.Uncertain };
                try
                {
                    var recovery = GroupSettingsClient.OpenStore().ReadForStartup();
                    committed = recovery.CommittedId == writer.Generation;
                    // Before a commit attempt the helper still waits for our abort, so a pending entry is normal.
                    // After one, nobody aborts it from here: a pending entry is a real interruption for the panel.
                    _cluster.InterruptedGeneration = uncertain && recovery.InterruptedId == writer.Generation ? writer.Generation : null;
                }
                catch (Exception ex)
                {
                    _cluster.InterruptedGeneration = writer.Generation;
                    _logger.LogError(ex, "Nie odczytano dziennika ustawień grupy po błędzie zastosowania; cofam runtime.");
                }
                if (committed) { _cluster.InterruptedGeneration = null; return; }
                if (uncertain)
                    _logger.LogError(failure, "Zatwierdzenie ustawień grupy bez potwierdzenia; dziennik nie zawiera zatwierdzonej generacji {Generation} — przywracam poprzednią konfigurację.", writer.Generation);
                else
                    _logger.LogError(failure, "Zastosowanie ustawień grupy nie powiodło się — przywracam poprzednią konfigurację.");
                _cfg = previousConfig; _baseCfg = previousBase;
                if (_ocr is SerializingOcrEngine s && s.Inner != oldOcr)
                    await Undo("silnik OCR", () => s.ReplaceInnerAsync(() => oldOcr, CancellationToken.None)).ConfigureAwait(false);
                if (observationChanged)
                    await Undo("konfiguracja monitorów", () => _supervisor.ReconfigureAsync(previousConfig, new MessageMapper(previousProfile.Rules), _ocr, CancellationToken.None)).ConfigureAwait(false);
                if (otherGroup) _alerts.RestoreObservation(episodes);
                _alerts.ConfigureTiming(previousConfig.Timing);
                _alerts.UseGroupProtocol(previousConfig.Cluster.Enabled && previousConfig.Cluster.Group is not null);
                Help.UseProfile(previousProfile); UseGroupInstructions(previousConfig.Cluster); UpdateReadiness();
                _cluster.SetLocalState(_alerts.ActiveAlerts, _supervisor.Snapshots);
                if (switched)
                {
                    // RevertSwitchAsync, nie SwitchAsync: powrót do poprzedniej konfiguracji oddaje warstwie przełączniki maszyn zdjęte
                    // przy przejściu do nowej grupy — _cfg = previousConfig liczy się właśnie z nimi.
                    await Undo("transport grupy", () => _cluster.RevertSwitchAsync(previousConfig.Cluster, previousKey, CancellationToken.None)).ConfigureAwait(false);
                    _watcherOverlap.Reset();
                }
                if (wasRunning && _cluster.IsWatcher)
                    await Undo("monitory maszyn", () => _supervisor.StartAsync(CancellationToken.None)).ConfigureAwait(false);
                throw;
            }
        }
        finally { _cycleGate.Release(); MachinesChanged?.Invoke(); RoleChanged?.Invoke(); AlertsChanged?.Invoke(); ConfigChanged?.Invoke(); }

        async Task Undo(string step, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Rollback ustawień grupy: krok „{Step}” nie powiódł się.", step); }
        }
    }

    public AppOrchestrator(
        ClusterRuntime cluster,
        MonitorSupervisor supervisor,
        AlertStateMachine alerts,
        HistoryWriter history,
        PowerModeWatcher power,
        IClock clock,
        StartupConfig startup,
        SharedConfigSync sharedConfig,
        IOcrEngine ocr,
        ILogger<AppOrchestrator> logger, AlarmHelpService help, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(startup);
        AppConfig cfg = startup.Effective;
        _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(startup));
        _baseCfg = startup.Base ?? throw new ArgumentNullException(nameof(startup));
        SharedConfig = sharedConfig ?? throw new ArgumentNullException(nameof(sharedConfig));
        Downtime = new DowntimeJournal(Path.Combine(Path.GetDirectoryName(AppPaths.Resolve(cfg.Paths.HistoryFile))!, "downtime.jsonl"));
        // W katalogu 'state', nie obok downtime.jsonl: w instalacji katalog danych jest dla operatora tylko do odczytu,
        // a prawo zapisu mają wyłącznie pliki założone przez instalator (downtime.jsonl, history.csv) i katalogi takie
        // jak 'state' (Modify dla Users). Dziennik tworzy nowy plik i scala się przez plik tymczasowy z podmianą —
        // potrzebuje więc zapisywalnego KATALOGU, a tego bez zmiany instalatora daje tylko 'state'.
        Observation = new ObservationJournal(Path.Combine(Path.GetDirectoryName(AppPaths.Resolve(cfg.Paths.HistoryFile))!, "state", "observation.jsonl"), logger);
        WindowsNotificationsEnabled = cfg.Ui.WindowsNotificationsEnabled;
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Help = help;
        _secrets = secrets;
        Preview = new MachinePreviewService(() => Config,
            id => _cluster.IsWatcher && _supervisor.IsRunning ? _supervisor.Get(id)?.PreviewFrame : null,
            (previewConfig, machine) =>
            {
                var file = new FolderScreenSource(machine.Id, AppPaths.Resolve(previewConfig.Paths.TestScreensDir));
                return file.FileExists ? file : new VncScreenSource(machine,
                    _secrets.Get(SecretKeys.VncPassword(machine.Id)), previewConfig.Timing);
            }, _clock,
            PreviewDesktopName,
            testFile: (previewConfig, machine) =>
            {
                var file = new FolderScreenSource(machine.Id, AppPaths.Resolve(previewConfig.Paths.TestScreensDir));
                return file.FileExists ? file : null;
            },
            observedSnapshot: id => _cluster.IsWatcher && _supervisor.IsRunning ? _supervisor.Get(id)?.Snapshot : null);
        _alerts.UseGroupProtocol(cfg.Cluster.Enabled && cfg.Cluster.Group is not null);
        UpdateReadiness();
        Help.Changed += () => AlertsChanged?.Invoke();
        // Wspólne instrukcje naprawy grupy: ten sam zbiór na każdym komputerze.
        // Zmiana zapisana tutaj idzie w świat od razu, a zmiana z sieci trafia do pliku i do pomocy.
        UseGroupInstructions(cfg.Cluster);
        _cluster.Instructions = Help.GroupInstructions;
        Help.LocalEdit += entry => _ = BroadcastInstructionAsync(entry);
        _cluster.InstructionsChanged += () =>
            _logger.LogInformation("Instrukcje naprawy: zastosowano zmianę z innego komputera grupy.");
        _cluster.SharedConfig = SharedConfig;
        _cluster.SharedConfigChanged += OnSharedConfigReceived;
        // Wspólny raport postojów grupy: dziennik tego komputera jest
        // jednocześnie źródłem odpowiedzi dla partnerów i miejscem, gdzie trafiają ich uzupełnienia.
        _cluster.Downtime = Downtime;
        _cluster.DowntimeChanged += () =>
            _logger.LogInformation("Raport postojów: uzupełniono epizody z innego komputera grupy.");
        _cluster.Observation = Observation;
        _cluster.ObservationChanged += () =>
            _logger.LogDebug("Czas obserwacji: uzupełniono przedziały z innego komputera grupy.");
    }

    /// <summary>Klaster (rola, peerzy, scalone alerty) — do odczytu przez zasobnik/okna statusu.</summary>
    public ClusterRuntime Cluster => _cluster;

    private string? PreviewDesktopName(string machineId)
    {
        string? local = _supervisor.IsRunning ? _supervisor.Get(machineId)?.Snapshot.Info?.DesktopName : null;
        if (!string.IsNullOrWhiteSpace(local))
        {
            return local;
        }

        foreach (MachineStatusSnapshot remote in _cluster.RemoteMachines)
        {
            if (string.Equals(remote.MachineId, machineId, StringComparison.Ordinal))
            {
                return remote.Info?.DesktopName;
            }
        }

        return null;
    }

    /// <summary>Instrukcje są wspólne dopiero wtedy, gdy grupa jest włączona i skonfigurowana.</summary>
    private void UseGroupInstructions(ClusterConfig cluster) =>
        Help.UseGroup(cluster.Enabled && cluster.Group is not null);

    /// <summary>
    /// Zdanie do Panelu administracyjnego o tym, czy instrukcje są już takie same na komputerach grupy
    /// Poza grupą pusty tekst — nie ma o czym mówić.
    /// </summary>
    public string InstructionsSyncStatus
    {
        get
        {
            if (!Help.GroupMode) return string.Empty;
            string mine = Help.GroupInstructions.Hash;
            var peers = _cluster.Peers.Where(p => !string.Equals(p.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                && p.Compatibility == GroupCompatibility.Compatible).ToArray();
            if (peers.Length == 0) return "Instrukcje są wspólne dla grupy. Na razie nie widać innego komputera.";
            var timeout = TimeSpan.FromMilliseconds(Math.Max(_cfg.Cluster.PeerTimeoutMs, 60000));
            var alive = peers.Where(p => p.IsAlive(_clock.UtcNow, timeout)).ToArray();
            int agreed = alive.Count(p => string.Equals(p.InstructionsHash, mine, StringComparison.OrdinalIgnoreCase));
            string text = $"Instrukcje są wspólne dla grupy; zgodne z {agreed} z {peers.Length} komputerami.";
            var waiting = peers.Except(alive).Select(p => p.Host).Order(StringComparer.Ordinal).ToArray();
            if (waiting.Length > 0)
                text += $" {string.Join(", ", waiting)} nie odpowiada — dostanie zmiany po włączeniu.";
            return text;
        }
    }

    /// <summary>
    /// Czy raport postojów jest raportem CAŁEJ grupy — ten sam warunek, co dla
    /// wspólnych instrukcji: grupa włączona i skonfigurowana. Poza grupą raport pozostaje lokalny.
    /// </summary>
    public bool GroupReportMode => _cfg.Cluster.Enabled && _cfg.Cluster.Group is not null;

    /// <summary>Nazwa grupy do nagłówka raportu; pusty tekst poza grupą.</summary>
    public string GroupReportName => _cfg.Cluster.Group?.GroupName ?? string.Empty;

    public string GroupReportSourcesText
    {
        get
        {
            if (!GroupReportMode) return string.Empty;
            var peers = _cluster.Peers
                .Where(p => !string.Equals(p.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Host, StringComparer.OrdinalIgnoreCase).ToArray();
            if (peers.Length == 0)
                return "Na razie nie widać innego komputera grupy. Raport pokazuje obserwacje tego komputera; "
                    + "postoje z pozostałych dojdą, gdy się połączą.";
            var parts = peers.Select(p => p.DowntimeSyncedAt is { } at
                ? $"{p.Host}: {at.ToLocalTime():HH:mm}"
                : $"{p.Host}: brak — jego obserwacje mogą w raporcie brakować");
            return "Raport uzupełniony przez — " + string.Join("; ", parts) + ".";
        }
    }

    private async Task BroadcastInstructionAsync(InstructionEntry entry)
    {
        try { await _cluster.BroadcastInstructionAsync(entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Nieudane rozgłoszenie nie może wywrócić zapisu: zmiana jest już w pliku, a pozostałe
            // komputery dostaną ją przy najbliższym uzgadnianiu odcisków.
            _logger.LogWarning(ex, "Nie rozgłoszono zmiany instrukcji \"{Key}\"; dojdzie przy uzgadnianiu.", entry.Key);
        }
    }

    /// <summary>Wynik odczytu profilu sieci i zapory dla interfejsu grupy, wykonanego bez UAC przy starcie z włączoną
    /// grupą i po każdym udanym zastosowaniu ustawień grupy. Null, gdy grupa jest wyłączona albo odczyt jeszcze trwa.
    /// Profil, który po restarcie wrócił do Publicznego, ma być nazwany w panelu, nie ukryty za „oczekiwaniem”.</summary>
    public FirewallStartupCheck? StartupFirewallCheck { get; private set; }

    private void ScheduleFirewallStartupCheck()
    {
        var cluster = _cfg.Cluster;
        if (!cluster.Enabled) { StartupFirewallCheck = null; return; }
        _ = Task.Run(() =>
        {
            try
            {
                var report = new GroupFirewallReadiness(Environment.ProcessPath!).Check(cluster);
                var check = FirewallStartupCheck.From(report, DateTimeOffset.Now);
                StartupFirewallCheck = check;
                if (check.Problem) _logger.LogWarning("Zapora grupy: {Text}", check.Text);
                else _logger.LogInformation("Zapora grupy: {Text}", check.Text);
            }
            catch (Exception ex)
            {
                StartupFirewallCheck = new(false, "Odczyt profilu sieci przy starcie nie powiódł się; użyj bloku gotowości w kreatorze.", DateTimeOffset.Now);
                _logger.LogWarning(ex, "Nie udało się odczytać profilu sieci i zapory przy starcie.");
            }
        });
    }

    /// <summary>Nadzorca maszyn (migawki, "Pokaż ekran") — do odczytu przez okno statusu.</summary>
    public MonitorSupervisor Supervisor => _supervisor;
    public MachinePreviewService Preview { get; }

    /// <summary>Silnik OCR — do odczytu dostępności/języka w oknie statusu i ikonie zasobnika.</summary>
    public IOcrEngine Ocr => _ocr;

    /// <summary>Bieżąca konfiguracja programu — EFEKTYWNA (baza z nałożoną wspólną konfiguracją z Panelu).</summary>
    public AppConfig Config => _cfg;

    /// <summary>
    /// Konfiguracja bez warstwy z Panelu (<c>config.json</c> + profil grupy). Panel pokazuje przy każdym
    /// ustawieniu, jaka jest wartość „z config.json”, i od niej liczy „Przywróć”.
    /// </summary>
    public AppConfig BaseConfig => _baseCfg;

    /// <summary>
    /// Wspólna konfiguracja (warstwa z Panelu administracyjnego) — plik
    /// <c>shared-config/shared-config.json</c> i, w grupie, jego replikacja.
    /// </summary>
    public SharedConfigSync SharedConfig { get; }

    /// <summary>
    /// Zmieniła się <see cref="Config"/> — po zapisaniu w Panelu albo po odebraniu warstwy z innego
    /// komputera grupy. Zgłaszane z dowolnego wątku; okna przełączają odbiór na Dispatcher.
    /// </summary>
    public event Action? ConfigChanged;

    public async Task ApplySharedConfigAsync(TimingConfig? timing, OverlayConfig? overlay,
        IReadOnlyDictionary<string, bool>? machines = null, CancellationToken ct = default)
    {
        var preview = new SharedConfigDocument
        {
            Timing = timing, Overlay = overlay,
            Machines = machines is null ? SharedConfig.Current?.Machines : SharedConfigDocument.CanonicalMachines(machines),
            EditedAt = _clock.UtcNow, EditedBy = Environment.MachineName,
        };
        ConfigLoader.Validate(preview.ApplyTo(_baseCfg));
        var document = SharedConfig.Edit(timing, overlay, _clock.UtcNow, machines);
        var next = document.ApplyTo(_baseCfg);
        await ApplyEffectiveAsync(next, ct).ConfigureAwait(false);
        try { await _cluster.BroadcastSharedConfigAsync(document, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nie rozgłoszono wspólnej konfiguracji; dojdzie przy uzgadnianiu.");
        }
    }

    public async Task ReloadSharedConfigFromDiskAsync(CancellationToken ct = default)
    {
        SharedConfig.Reload();
        if (SharedConfig.LoadError is { } error)
        {
            _logger.LogWarning("Wspólna konfiguracja zapisana poza Panelem nie dała się wczytać — zostawiam dotychczasowe ustawienia. {Error}", error);
            return;
        }

        var next = SharedConfig.Current?.ApplyTo(_baseCfg) ?? _baseCfg;
        try { ConfigLoader.Validate(next); }
        catch (ConfigException ex)
        {
            _logger.LogWarning(ex, "Wspólna konfiguracja zapisana poza Panelem ma wartości, których ta wersja programu nie przyjmuje — zostawiam dotychczasowe ustawienia.");
            return;
        }

        await ApplyEffectiveAsync(next, ct).ConfigureAwait(false);
        _logger.LogInformation("Wspólna konfiguracja: zastosowano zmianę zapisaną na tym komputerze poza Panelem administracyjnym.");
        if (SharedConfig.Current is not { } document) return;
        try { await _cluster.BroadcastSharedConfigAsync(document, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nie rozgłoszono wspólnej konfiguracji; dojdzie przy uzgadnianiu.");
        }
    }

    public Task BroadcastSharedConfigAsync(SharedConfigDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _cluster.BroadcastSharedConfigAsync(document, ct);
    }

    /// <summary>
    /// Zdanie do Panelu administracyjnego o tym, czy wspólna konfiguracja jest już taka sama na komputerach
    /// grupy — jak <see cref="InstructionsSyncStatus"/>. Poza grupą pusty tekst: warstwa działa wtedy
    /// lokalnie i nie ma z czym jej porównywać.
    /// </summary>
    public string SharedConfigSyncStatus
    {
        get
        {
            if (!GroupReportMode) return string.Empty;
            string? mine = SharedConfig.Hash;
            var peers = _cluster.Peers.Where(p => !string.Equals(p.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                && p.Compatibility == GroupCompatibility.Compatible).ToArray();
            if (peers.Length == 0) return "Konfiguracja jest wspólna dla grupy. Na razie nie widać innego komputera.";
            var timeout = TimeSpan.FromMilliseconds(Math.Max(_cfg.Cluster.PeerTimeoutMs, 60000));
            var alive = peers.Where(p => p.IsAlive(_clock.UtcNow, timeout)).ToArray();
            int agreed = alive.Count(p => string.Equals(p.SharedConfigHash, mine, StringComparison.OrdinalIgnoreCase));
            string text = $"Konfiguracja jest wspólna dla grupy; zgodna z {agreed} z {peers.Length} komputerami.";
            var waiting = peers.Except(alive).Select(p => p.Host).Order(StringComparer.Ordinal).ToArray();
            if (waiting.Length > 0)
                text += $" {string.Join(", ", waiting)} nie odpowiada — dostanie zmiany po włączeniu.";
            return text;
        }
    }

    /// <summary>
    /// Warstwa przyszła z innego komputera grupy: liczymy efektywną od nowa i stosujemy. Gdy wynik nie
    /// przechodzi kontroli (ta wersja programu może nie znać jakiegoś zakresu), zostaje ostrzeżenie w logu,
    /// a runtime pracuje bez zmiany. Plik warstwy zostaje nietknięty, żeby dalej się replikował.
    /// </summary>
    private void OnSharedConfigReceived()
    {
        var next = SharedConfig.Current?.ApplyTo(_baseCfg) ?? _baseCfg;
        try { ConfigLoader.Validate(next); }
        catch (ConfigException ex)
        {
            _logger.LogWarning(ex, "Wspólna konfiguracja z innego komputera grupy ma wartości, których ta wersja programu nie przyjmuje — zostawiam dotychczasowe ustawienia.");
            return;
        }
        _logger.LogInformation("Wspólna konfiguracja: zastosowano zmianę z innego komputera grupy.");
        _ = ApplySharedConfigInBackgroundAsync(next);
    }

    private async Task ApplySharedConfigInBackgroundAsync(AppConfig next)
    {
        try { await ApplyEffectiveAsync(next, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Odbiór pakietów nie może się wywrócić na nieudanym przełączeniu monitorów — kolejna zmiana
            // (albo restart programu) spróbuje jeszcze raz, a plik warstwy jest już zapisany.
            _logger.LogError(ex, "Nie udało się zastosować wspólnej konfiguracji odebranej z grupy.");
        }
    }

    /// <summary>
    /// Przełącza runtime na nową konfigurację efektywną. Monitory maszyn czytają <c>timing</c> przy starcie,
    /// więc zmiana tej sekcji wymaga ich przebudowania — dokładnie jak przy zmianie ustawień grupy. Bramka
    /// cyklu (<see cref="_cycleGate"/>) chroni przed wyścigiem z tikiem, a czekanie na <see cref="_stopping"/>
    /// — przed startem nowego kompletu monitorów, gdy stary jeszcze się zatrzymuje.
    /// </summary>
    private async Task ApplyEffectiveAsync(AppConfig next, CancellationToken ct)
    {
        if (_stopping is { IsCompleted: false } stopping) await stopping.ConfigureAwait(false);
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string[] disabledNow = DisabledNow(_cfg.Machines, next.Machines);
            bool machinesChanged = disabledNow.Length > 0 || !SameEnabledMachines(next.Machines, _cfg.Machines);
            if (!SameTiming(next.Timing, _cfg.Timing) || machinesChanged)
            {
                bool wasRunning = _supervisor.IsRunning;
                await _supervisor.ReconfigureAsync(next, new MessageMapper(Help.Rules), _ocr, ct).ConfigureAwait(false);
                foreach (string machineId in disabledNow) _alerts.OnMachineDisabled(machineId);
                if (wasRunning && _cluster.IsWatcher) await _supervisor.StartAsync(ct).ConfigureAwait(false);
            }
            _alerts.ConfigureTiming(next.Timing);
            _cfg = next;
            if (machinesChanged)
            {
                UpdateReadiness();
                _logger.LogInformation("Wspólna konfiguracja: maszyny włączone: {Enabled}; wyłączone: {Disabled}.",
                    Names(next.Machines, enabled: true), Names(next.Machines, enabled: false));
            }
        }
        finally { _cycleGate.Release(); }
        MachinesChanged?.Invoke(); AlertsChanged?.Invoke(); ConfigChanged?.Invoke();
    }

    /// <summary>Identyfikatory maszyn, które były włączone, a w <paramref name="next"/> są wyłączone (albo zniknęły z listy).</summary>
    private static string[] DisabledNow(IReadOnlyList<MachineConfig> current, IReadOnlyList<MachineConfig> next) =>
        current.Where(m => m.Enabled && !next.Any(n => n.Id == m.Id && n.Enabled)).Select(m => m.Id).ToArray();

    /// <summary>Czy zbiór włączonych maszyn (po identyfikatorze) jest ten sam.</summary>
    private static bool SameEnabledMachines(IReadOnlyList<MachineConfig> a, IReadOnlyList<MachineConfig> b) =>
        a.Where(m => m.Enabled).Select(m => m.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(b.Where(m => m.Enabled).Select(m => m.Id).Order(StringComparer.Ordinal));

    private static string Names(IEnumerable<MachineConfig> machines, bool enabled)
    {
        string text = string.Join(", ", machines.Where(m => m.Enabled == enabled).Select(m => m.Name));
        return text.Length > 0 ? text : "(brak)";
    }

    /// <summary>Nazwy maszyn z przełącznikami <paramref name="enabled"/> (nazwa z konfiguracji, a bez niej — identyfikator).</summary>
    private static string SwitchNames(IReadOnlyDictionary<string, SharedMachineSettings> switches, AppConfig config, bool enabled)
    {
        string text = string.Join(", ", switches.Where(p => p.Value.Enabled == enabled)
            .Select(p => config.Machines.FirstOrDefault(m => m.Id == p.Key)?.Name ?? p.Key));
        return text.Length > 0 ? text : "(brak)";
    }

    /// <summary>Wspólna, pusta tablica — patrz <see cref="SameTiming"/>.</summary>
    private static readonly int[] NoBackoff = [];

    /// <summary>
    /// Czy obie sekcje <c>timing</c> znaczą to samo. Zwykłe porównanie rekordów tu nie wystarcza:
    /// <see cref="TimingConfig.ReconnectBackoffMs"/> jest tablicą, a tablice porównują się przez
    /// referencję — konfiguracja wczytana z pliku warstwy nigdy nie byłaby „równa” tej z pamięci i każda
    /// zmiana samej nakładki niepotrzebnie przebudowywałaby monitory maszyn.
    /// </summary>
    private static bool SameTiming(TimingConfig a, TimingConfig b) =>
        a.ReconnectBackoffMs.AsSpan().SequenceEqual(b.ReconnectBackoffMs)
        && (a with { ReconnectBackoffMs = NoBackoff }) == (b with { ReconnectBackoffMs = NoBackoff });

    /// <summary>
    /// Bieżąca lokalna gotowość do obserwacji. Odczyt nie przełącza runtime ani nie zapisuje ustawień;
    /// przy globalnie wyłączonym monitorowaniu nie zagląda do magazynu haseł.
    /// </summary>
    public WatcherReadiness GetWatcherReadiness() => WatcherReadiness.Evaluate(_cfg,
        HasLocalVncPassword, _ocr.IsAvailable, GroupCompatibility.Compatible);

    /// <summary>
    /// Sprawdza wyłącznie obecność lokalnego hasła VNC maszyny. Nie zwraca wartości sekretu, nie zapisuje
    /// niczego i nie kopiuje haseł z pliku grupy; służy ocenie gotowości dla listy maszyn z zaproszenia.
    /// </summary>
    public bool HasLocalVncPassword(string machineId) =>
        _secrets.Get(SecretKeys.VncPassword(machineId)) is not null;

    /// <summary>
    /// Zapisuje hasło VNC maszyny wyłącznie w lokalnym magazynie sekretów tego komputera, tą samą ścieżką
    /// co okno ustawień. Nie zapisuje konfiguracji, sekretu grupy ani zapory i nie przełącza działającego
    /// runtime; nowe hasło wchodzi do obserwacji dopiero przy kolejnym przeładowaniu sekretów.
    /// </summary>
    public void SetLocalVncPassword(string machineId, string password)
    {
        if (string.IsNullOrWhiteSpace(machineId))
            throw new ArgumentException("Pusty identyfikator maszyny.", nameof(machineId));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Puste hasło VNC.", nameof(password));
        _secrets.Set(SecretKeys.VncPassword(machineId), password);
    }

    public bool WindowsNotificationsEnabled { get; private set; } = true;

    /// <summary>Zmienił się zestaw widocznych alertów (<see cref="VisibleAlerts"/>) — nowy alert, ACK, eskalacja, zamknięcie.</summary>
    public event Action? AlertsChanged;

    /// <summary>Zmieniła się rola tego komputera (obserwator ⇄ tylko UI).</summary>
    public event Action? RoleChanged;

    /// <summary>Zmieniła się tabela znanych komputerów.</summary>
    public event Action? PeersChanged;

    /// <summary>Zmienił się stan którejkolwiek monitorowanej maszyny.</summary>
    public event Action? MachinesChanged;

    public IReadOnlyList<Alert> VisibleAlerts
    {
        get
        {
            List<Alert> result;
            if (!_cluster.Configuration.Enabled || !_cluster.NetworkActive)
            {
                // Migawka klastra jest odświeżana co sekundę i może nadal zawierać
                // właśnie zamknięty alert. Bez zdalnych obserwatorów lokalna lista jest kompletna.
                result = _alerts.ActiveAlerts.ToList();
            }
            else if (_cluster.IsWatcher && _cluster.GroupNode is null)
            {
                var byKey = new Dictionary<AlertKey, Alert>();
                foreach (Alert a in _cluster.MergedAlerts)
                {
                    byKey[a.Key] = a;
                }

                foreach (Alert a in _alerts.ActiveAlerts)
                {
                    byKey[a.Key] = a; // lokalny preferowany
                }

                result = byKey.Values.ToList();
            }
            else
            {
                result = _cluster.MergedAlerts.ToList();
            }

            if (_testAlert is { } test)
            {
                result.Add(test);
            }

            if (_testIdleAlert is { } testIdle)
            {
                result.Add(testIdle);
            }

            if (_testScreenAlert is { } testScreen)
            {
                result.Add(testScreen);
            }

            return result
                .OrderBy(a => a.Status is AlertStatus.Active or AlertStatus.Escalated ? 0 : 1)
                .ThenBy(a => a.StartedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Alerty "dołóż prace" (maszyna stoi bez pracy) do pokazania w pop-upie/oknie statusu na TYM komputerze: z
    /// <see cref="VisibleAlerts"/> te o <see cref="AlertKind.MachineIdle"/> ze statusem Active lub Escalated.
    /// Celowo BEZ Acknowledged — "Skasuj" ma chować pozycję od razu na wszystkich komputerach, bez 60-sekundowego
    /// okna "sprawdza ..." jak przy błędach (postój nie ma kto "sprawdzać" — to tylko przypomnienie).
    /// </summary>
    public IReadOnlyList<Alert> IdleAlerts => VisibleAlerts
        .Where(a => a.Kind == AlertKind.MachineIdle && a.Status is AlertStatus.Active or AlertStatus.Escalated)
        .ToList();

    /// <summary>Zasięg potwierdzenia i liczba widocznych komputerów grupy — patrz <see cref="CurrentDismissScope"/>.</summary>
    /// <param name="Reach">Dokąd sięga potwierdzenie (<see cref="DismissScope"/>).</param>
    /// <param name="LiveOthers">Ile INNYCH komputerów grupy widać teraz (żywy heartbeat, zgodny profil).</param>
    public readonly record struct DismissScopeState(DismissReach Reach, int LiveOthers);

    /// <summary>
    /// Dokąd sięgnie „✕ Skasuj” / „Idę sprawdzić” naciśnięte TERAZ na tym komputerze (patrz
    /// <see cref="DismissScope"/>). Pop-up postoju, Podgląd i okno statusu pokazują to zdaniem pod
    /// listą (<see cref="DismissScopeNote"/>).
    /// <para>
    /// Zasięg i liczba komputerów liczą się RAZ w jednym odczycie — okna czytają je co sekundę, a każde
    /// odwołanie do <c>Peers</c> bierze zamek węzła klastra (tego samego, na którym pracuje odbiór pakietów).
    /// </para>
    /// </summary>
    public DismissScopeState CurrentDismissScope
    {
        get
        {
            bool active = _cluster.Configuration.Enabled && _cluster.NetworkActive;
            int others = active
                ? DismissScope.LiveOtherComputers(_cluster.Peers, Environment.MachineName, _clock.UtcNow,
                    TimeSpan.FromMilliseconds(_cluster.Configuration.PeerTimeoutMs))
                : 0;
            return new(DismissScope.For(active, others), others);
        }
    }

    /// <summary>Zdanie pod listą postojów o zasięgu skasowania (patrz <see cref="CurrentDismissScope"/>).</summary>
    public string DismissScopeNote
    {
        get { var scope = CurrentDismissScope; return DismissScope.Note(scope.Reach, scope.LiveOthers); }
    }

    /// <summary>Czy <see cref="DismissScopeNote"/> jest ostrzeżeniem (potwierdzenie może nie dojść do pozostałych).</summary>
    public bool IsDismissScopeWarning => DismissScope.IsWarning(CurrentDismissScope.Reach);

    /// <summary>Podpowiedź (tooltip) przy przycisku kasującym powiadomienie o postoju.</summary>
    public string DismissButtonHint => DismissScope.ButtonHint(CurrentDismissScope.Reach);

    /// <summary>
    /// Powiadomienia "włącz okno podglądu stanu maszyny" (HMI pokazuje inny ekran niż główny — patrz
    /// <see cref="AlertKind.WrongScreen"/>) do pokazania w pop-upie <c>ScreenPopupWindow</c>/oknie statusu na TYM
    /// komputerze: z <see cref="VisibleAlerts"/> te o tym rodzaju ze statusem Active lub Escalated (Escalated =
    /// przypomnienie ponowione po <c>TimingConfig.WrongScreenRemindAfterMs</c>, gdy ekran nadal jest niewłaściwy).
    /// Jak <see cref="IdleAlerts"/> — bez Acknowledged, bo "✕" ma chować pozycję od razu.
    /// </summary>
    public IReadOnlyList<Alert> WrongScreenAlerts => VisibleAlerts
        .Where(a => a.Kind == AlertKind.WrongScreen && a.Status is AlertStatus.Active or AlertStatus.Escalated)
        .ToList();

    /// <summary>Ostatnie zamknięte alerty (lokalny stan obserwatora) — dla okna statusu.</summary>
    public IReadOnlyList<Alert> RecentHistory => _alerts.RecentHistory;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _power.Resumed += OnResumed;
        _power.Suspending += OnSuspending;
        _alerts.Changed += OnAlertsMachineChanged;
        _cluster.AckReceived += OnAckReceived;
        _cluster.EpisodeStateReceived += _alerts.ReconcileGroupState;
        _cluster.EpisodeStateReceived += OnRemoteEpisodesReceived;
        _cluster.RoleChanged += OnRoleChanged;
        _cluster.PeersChanged += OnPeersChanged;
        _cluster.AlertsChanged += OnClusterAlertsChanged;
        _cluster.SleepDetected += OnSleepDetected;
        _supervisor.SnapshotsChanged += OnSnapshotsChanged;

        try
        {
            await _cluster.StartAsync(stoppingToken).ConfigureAwait(false);
            ScheduleFirewallStartupCheck();

            await RunMaintenanceAsync().ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            int tickCount = 0;
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
                tickCount++;
                if (tickCount % MaintenanceEveryTicks == 0)
                {
                    await RunMaintenanceAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // oczekiwane przy zatrzymywaniu programu
        }
        finally
        {
            // Jeśli w tle wciąż trwa zatrzymywanie po utracie roli (patrz _stopping / TickOnceAsync) — poczekaj
            // na jego koniec (ma własny limit 10 s), zanim wywołamy StopAsync ponownie. Samo w sobie jest ono
            // bezpiecznym no-opem, gdy nadzorca jest już zatrzymany.
            if (_stopping is { } stopping)
            {
                await stopping.ConfigureAwait(false);
            }

            await _supervisor.StopAsync().ConfigureAwait(false);
            await _cluster.StopAsync().ConfigureAwait(false);

            _power.Resumed -= OnResumed;
            _power.Suspending -= OnSuspending;
            _alerts.Changed -= OnAlertsMachineChanged;
            _cluster.AckReceived -= OnAckReceived;
            _cluster.EpisodeStateReceived -= _alerts.ReconcileGroupState;
            _cluster.EpisodeStateReceived -= OnRemoteEpisodesReceived;
            _cluster.RoleChanged -= OnRoleChanged;
            _cluster.PeersChanged -= OnPeersChanged;
            _cluster.AlertsChanged -= OnClusterAlertsChanged;
            _cluster.SleepDetected -= OnSleepDetected;
            _supervisor.SnapshotsChanged -= OnSnapshotsChanged;
            // Ostatnia obserwacja otwartych epizodów czeka za progiem zapisu dziennika
            // (DowntimeJournal.ObservationWriteInterval) — przy czystym zamknięciu dopisujemy ją,
            // żeby raport po restarcie nie skracał trwającego postoju o te kilka sekund.
            Downtime.Flush();
            // To samo dla otwartych przedziałów czasu obserwacji: domknięcie na ostatniej obserwacji i zapis
            // (wyjątek dziennika nie może pominąć zapisu historii alertów).
            GuardObservation(Observation.CloseAll);
            _history.Flush();
        }
    }

    /// <summary>Potwierdza alert ("Idę sprawdzić") — local-first (patrz <see cref="ClusterRuntime.SendAckAsync"/>) i rozgłasza po sieci.</summary>
    public async Task AcknowledgeAsync(AlertKey key, CancellationToken ct = default)
    {
        if (key.Equals(TestAlertKey))
        {
            AcknowledgeTestAlert();
            return;
        }

        if (key.Equals(TestIdleAlertKey))
        {
            AcknowledgeTestIdleAlert();
            return;
        }

        if (key.Equals(TestScreenAlertKey))
        {
            AcknowledgeTestScreenAlert();
            return;
        }

        string user = Environment.UserName;
        if (_cluster.GroupNode is null) _alerts.AcknowledgeLocal(key, Environment.MachineName, user);
        await _cluster.SendAckAsync(key, user, ct).ConfigureAwait(false);
        // Ślad w logu: musi dać się sprawdzić
        // z dziennika, czy potwierdzenie w ogóle miało dokąd pójść (grupa wyłączona/bez transportu), czy
        // poszło w sieć, ale nikt go nie odebrał (wtedy drugi komputer nie ma go w swoim logu).
        var scope = CurrentDismissScope;
        _logger.LogInformation("Potwierdzono alert {Key} (użytkownik {User}); zasięg: {Reach}, widoczne inne komputery grupy: {Peers}.",
            key, user, scope.Reach, scope.LiveOthers);
    }

    /// <summary>
    /// Skasowanie pozycji w pop-upie/oknie "Maszyny bez pracy – dołóż prace" (przycisk "✕ Skasuj") — dokładnie to
    /// samo co <see cref="AcknowledgeAsync"/> ("Idę sprawdzić" dla błędów): potwierdza epizod lokalnie i
    /// rozgłasza ACK po sieci, więc pozycja znika na WSZYSTKICH komputerach naraz (obsługuje też lokalny
    /// klucz testowy, patrz <see cref="TestIdlePopup"/>).
    /// </summary>
    public Task DismissIdleAlertAsync(AlertKey key) => AcknowledgeAsync(key);

    /// <summary>
    /// Zamknięcie pozycji w pop-upie "Włącz okno podglądu stanu maszyny" (przycisk "✕") — to samo co
    /// <see cref="AcknowledgeAsync"/>: potwierdza epizod, pozycja znika od razu; gdy HMI nadal pokazuje inny
    /// ekran po <c>TimingConfig.WrongScreenRemindAfterMs</c>, przypomnienie wraca (patrz <see cref="WrongScreenAlerts"/>).
    /// </summary>
    public Task DismissWrongScreenAlertAsync(AlertKey key) => AcknowledgeAsync(key);

    /// <summary>
    /// Wywołuje lokalny alert testowy (menu zasobnika "Test alarmu (ten komputer)") — widoczny TYLKO w oknie
    /// alertu tego komputera, nigdy nie jest rozgłaszany po sieci klastra.
    /// </summary>
    public void TestAlarm()
    {
        DateTimeOffset now = _clock.UtcNow;
        _testAlert = new Alert
        {
            Key = TestAlertKey,
            MachineId = "test",
            MachineName = "TEST",
            Kind = AlertKind.Error,
            ShortText = "Test alarmu – ten komputer",
            FullText = null,
            Category = "inny",
            StartedAt = now,
            LastSeenAt = now,
            Status = AlertStatus.Active,
            Sound = true,
        };
        AlertsChanged?.Invoke();
    }

    /// <summary>
    /// Wywołuje lokalny alert testowy "dołóż prace" (menu zasobnika "Test powiadomienia o postoju (ten
    /// komputer)") — pokazuje pop-up <c>IdlePopupWindow</c> TYLKO na tym komputerze, nigdy nie jest
    /// rozgłaszany po sieci klastra. Znika po skasowaniu (<see cref="DismissIdleAlertAsync"/>) albo
    /// automatycznie po <see cref="TestIdleAlertMaxAge"/> (patrz <see cref="ExpireTestIdleAlertIfNeeded"/>).
    /// </summary>
    public void TestIdlePopup()
    {
        DateTimeOffset now = _clock.UtcNow;
        _testIdleAlert = new Alert
        {
            Key = TestIdleAlertKey,
            MachineId = "test",
            MachineName = "TEST",
            Kind = AlertKind.MachineIdle,
            ShortText = AlertStateMachine.MachineIdleShortText,
            FullText = null,
            Category = AlertStateMachine.IdleCategory,
            StartedAt = now,
            LastSeenAt = now,
            Status = AlertStatus.Active,
            Sound = false,
        };
        AlertsChanged?.Invoke();
    }

    /// <summary>
    /// Wywołuje lokalny alert testowy "włącz okno podglądu stanu maszyny" (menu zasobnika "Test powiadomienia o
    /// ekranie HMI (ten komputer)") — pokazuje pop-up <c>ScreenPopupWindow</c> TYLKO na tym komputerze, nigdy nie
    /// jest rozgłaszany po sieci. Znika po zamknięciu (✕) albo automatycznie po <see cref="TestIdleAlertMaxAge"/>.
    /// </summary>
    public void TestWrongScreenPopup()
    {
        DateTimeOffset now = _clock.UtcNow;
        _testScreenAlert = new Alert
        {
            Key = TestScreenAlertKey,
            MachineId = "test",
            MachineName = "TEST",
            Kind = AlertKind.WrongScreen,
            ShortText = AlertStateMachine.WrongScreenShortText,
            FullText = null,
            Category = "ekran",
            StartedAt = now,
            LastSeenAt = now,
            Status = AlertStatus.Active,
            Sound = _cfg.Timing.WrongScreenAlertSound,
        };
        AlertsChanged?.Invoke();
    }

    /// <summary>
    /// Wywoływane po zapisaniu Ustawień: hasła VNC zmienione → restart nadzorcy maszyn (jeśli ten komputer jest
    /// obserwatorem I nadzorca już działa), żeby użyć nowych haseł od razu. Restartuje TYLKO DZIAŁAJĄCEGO
    /// nadzorcę — gdy nadzorca jeszcze nie działa (np. ten komputer właśnie czeka na pierwsze hasło, patrz
    /// bramka <see cref="MonitorSupervisor.AnyEnabledMachineIsReadyToConnect"/> w <see cref="TickOnceAsync"/>),
    /// ta metoda nic nie robi: KOLEJNY tik sam zauważy, że hasło się pojawiło, i uruchomi nadzorcę bez
    /// pomocy stąd. Zmiana klucza sieciowego (HMAC) NIE jest tu obsługiwana — wymaga ponownego uruchomienia
    /// programu (okno Ustawienia informuje o tym osobno).
    /// </summary>
    public async Task ReloadSecretsAndReconfigureAsync()
    {
        WindowsNotificationsEnabled = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath).Ui.WindowsNotificationsEnabled;
        // Wywoływane po zapisaniu Ustawień, poza tikiem (1 s) — await tutaj jest OK. Jeśli akurat trwa w tle
        // zatrzymywanie po utracie roli (_stopping, patrz TickOnceAsync), poczekaj na jego koniec najpierw,
        // żeby StopAsync/StartAsync niżej nie ścigały się z tamtym zatrzymaniem.
        if (_stopping is { IsCompleted: false } stopping)
        {
            await stopping.ConfigureAwait(false);
        }

        _stopping = null;

        if (!_supervisor.IsRunning)
        {
            // Nadzorca nie działał (np. czekał na pierwsze hasło VNC, patrz XML-doc metody) — nic tu nie
            // startujemy; kolejny tik (TickOnceAsync) sam sprawdzi bramkę i wystartuje go, jeśli już może.
            return;
        }

        await _supervisor.StopAsync().ConfigureAwait(false);
        await _supervisor.StartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _restartAfterSleep, 0) != 0)
                _stopping = StopSupervisorInBackgroundAsync();
            // Kolejność celowa: najpierw lokalny stan alertów (Tick +
            // ewentualny alert testowy), potem publikacja go do ClusterRuntime.SetLocalState, i dopiero na końcu
            // ClusterRuntime.TickAsync — żeby STATE wysłany w TYM tiku niósł już świeży stan, a nie ten sprzed 1 s.
            UpdateReadiness();
            if (_cluster.IsWatcher) _alerts.Tick();
            if (++_downtimeTicks % 10 == 0)
                foreach (var alert in _alerts.ActiveAlerts) RecordDowntime(alert);
            ExpireTestAlertIfNeeded();
            ExpireTestIdleAlertIfNeeded();
            ExpireTestScreenAlertIfNeeded();

            bool stopInProgress = _stopping is { IsCompleted: false };

            if (_cluster.IsWatcher)
            {
                if (!_supervisor.IsRunning && !stopInProgress)
                {
                    // Bramka: bez ŻADNEGO zapisanego hasła VNC (żadna włączona maszyna) start nadzorcy dałby
                    // tylko serię identycznych "Błąd VNC ... brak hasła" w logu przy każdej próbie połączenia
                    // — czekamy (logując ostrzeżenie RAZ, nie co tik) na pierwsze hasło zapisane w Ustawieniach.
                    if (!_supervisor.MonitoringEnabled)
                    {
                        await _supervisor.StartAsync(ct).ConfigureAwait(false); // tylko migawki Disabled
                    }
                    else if (!_supervisor.AnyEnabledMachineIsReadyToConnect())
                    {
                        if (!_waitingForVncPasswordsWarned)
                        {
                            _waitingForVncPasswordsWarned = true;
                            _logger.LogWarning(
                                "Czekam na hasła VNC — żadna włączona maszyna nie ma zapisanego hasła (Ustawienia → hasła VNC); nadzorca maszyn wystartuje sam po ich zapisaniu.");
                        }
                    }
                    else
                    {
                        if (_waitingForVncPasswordsWarned)
                        {
                            _waitingForVncPasswordsWarned = false;
                            _logger.LogInformation("Hasła VNC zapisane — uruchamiam nadzorcę maszyn.");
                        }

                        // Poprzednie zatrzymanie (jeśli w ogóle było) już się ukończyło — bezpiecznie startujemy
                        // nowy komplet monitorów dopiero teraz (patrz dokumentacja pola _stopping).
                        _stopping = null;
                        await _supervisor.StartAsync(ct).ConfigureAwait(false);
                    }
                }
                // W przeciwnym razie: albo nadzorca już działa, albo wciąż trwa w tle zatrzymywanie poprzedniej
                // instancji (_stopping) — start odkładamy do kolejnego tiku, żeby nigdy nie mieć dwóch kompletów
                // połączeń VNC do tych samych maszyn naraz.

                _cluster.SetLocalState(
                    _alerts.ActiveAlerts,
                    _supervisor.Snapshots,
                    _alerts.RecentlyClosedKeys(RecentlyClosedWindow));
            }
            else if (_supervisor.IsRunning && !stopInProgress)
            {
                // NIE await — MonitorSupervisor.StopAsync może trwać do 10 s, a tik nie może się na to
                // blokować (przez ten czas nie szłyby heartbeaty i mogłaby ruszyć kaskada zmian ról).
                // Zatrzymanie leci w tle; błędy z kontynuacji są logowane, nigdy nie wyciekają jako
                // nieobserwowany wyjątek zadania.
                _stopping = StopSupervisorInBackgroundAsync();
            }

            RecordObservation(stopInProgress);

            await _cluster.TickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nieoczekiwany błąd w pętli orkiestratora — kontynuuję w następnym cyklu.");
        }
        finally { _cycleGate.Release(); }
    }

    private void RecordObservation(bool stopInProgress) => GuardObservation(() =>
    {
        if (_cluster.IsWatcher && _supervisor.IsRunning && !stopInProgress)
            Observation.Observe(_supervisor.Snapshots
                .Select(s => (s.MachineId, State: ObservationInterval.StateOf(s)))
                .Where(p => p.State is not null)
                .Select(p => (p.MachineId, p.State!.Value)), _clock.UtcNow);
        else
            Observation.CloseAll();
    });

    /// <summary>Ostatni wpis w logu o wyjątku z dziennika czasu obserwacji (zegar monotoniczny); dławienie do raz na minutę.</summary>
    private long? _observationFailureLoggedAt;

    private void GuardObservation(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            long now = _clock.MonotonicMs;
            if (_observationFailureLoggedAt is { } last && now - last < 60_000) return;
            _observationFailureLoggedAt = now;
            _logger.LogWarning(ex, "Dziennik czasu obserwacji: nieoczekiwany błąd — obserwacja maszyn i grupa działają dalej, czas obserwacji może być niepełny.");
        }
    }

    /// <summary>
    /// Zatrzymuje <see cref="_supervisor"/> na osobnym zadaniu (patrz <see cref="_stopping"/>) — wywoływane
    /// wyłącznie z <see cref="TickOnceAsync"/>, nigdy nie jest await-owane w tiku.
    /// </summary>
    private Task StopSupervisorInBackgroundAsync() => Task.Run(async () =>
    {
        try
        {
            await _supervisor.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd podczas zatrzymywania nadzorcy maszyn w tle (po utracie roli obserwatora).");
        }
    });

    private async Task RunMaintenanceAsync()
    {
        try
        {
            await _supervisor.PruneCapturesAsync().ConfigureAwait(false);
            PruneOldLogs();
            // Wpisy historii czekające na zwolnienie history.csv (plik otwarty w arkuszu) — bez czekania na
            // kolejny alert.
            _history.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się wykonać okresowego porządkowania plików (zrzuty/logi).");
        }
    }

    private void PruneOldLogs()
    {
        string logsDir = AppPaths.Resolve(_cfg.Paths.LogsDir);
        if (!Directory.Exists(logsDir))
        {
            return;
        }

        DateTime threshold = DateTime.UtcNow - TimeSpan.FromDays(_cfg.Paths.LogRetentionDays);
        foreach (string file in Directory.EnumerateFiles(logsDir, "app-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < threshold)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Nie udało się usunąć przeterminowanego pliku logu \"{File}\".", file);
            }
        }
    }

    private void ExpireTestAlertIfNeeded()
    {
        if (_testAlert is { Status: AlertStatus.Acknowledged, AcknowledgedAt: { } at }
            && _clock.UtcNow - at > TimeSpan.FromSeconds(5))
        {
            _testAlert = null;
            AlertsChanged?.Invoke();
        }
    }

    private void AcknowledgeTestAlert()
    {
        if (_testAlert is null)
        {
            return;
        }

        _testAlert = _testAlert with
        {
            Status = AlertStatus.Acknowledged,
            AcknowledgedAt = _clock.UtcNow,
            AckedByHost = Environment.MachineName,
            AckedByUser = Environment.UserName,
        };
        AlertsChanged?.Invoke();
    }

    /// <summary>
    /// Sprząta lokalny testowy alert postoju: gdy skasowany (Acknowledged) dłużej niż 5 s temu — ten sam
    /// krótki bufor jak przy <see cref="ExpireTestAlertIfNeeded"/> (tylko higiena pamięci — <see cref="IdleAlerts"/>
    /// i tak ukrywa go z listy natychmiast po ACK, patrz jego XML-doc) — ALBO gdy minęło
    /// <see cref="TestIdleAlertMaxAge"/> od utworzenia, nawet jeśli nikt go nie skasował (zapomniane okno
    /// testowe nie ma wisieć w nieskończoność).
    /// </summary>
    private void ExpireTestIdleAlertIfNeeded()
    {
        if (_testIdleAlert is not { } alert)
        {
            return;
        }

        bool ackedLongAgo = alert is { Status: AlertStatus.Acknowledged, AcknowledgedAt: { } ackedAt }
            && _clock.UtcNow - ackedAt > TimeSpan.FromSeconds(5);
        bool tooOld = _clock.UtcNow - alert.StartedAt > TestIdleAlertMaxAge;

        if (ackedLongAgo || tooOld)
        {
            _testIdleAlert = null;
            AlertsChanged?.Invoke();
        }
    }

    private void AcknowledgeTestIdleAlert()
    {
        if (_testIdleAlert is null)
        {
            return;
        }

        _testIdleAlert = _testIdleAlert with
        {
            Status = AlertStatus.Acknowledged,
            AcknowledgedAt = _clock.UtcNow,
            AckedByHost = Environment.MachineName,
            AckedByUser = Environment.UserName,
        };
        AlertsChanged?.Invoke();
    }

    /// <summary>Sprząta lokalny testowy alert "niewłaściwy ekran HMI" — te same zasady co <see cref="ExpireTestIdleAlertIfNeeded"/>.</summary>
    private void ExpireTestScreenAlertIfNeeded()
    {
        if (_testScreenAlert is not { } alert)
        {
            return;
        }

        bool ackedLongAgo = alert is { Status: AlertStatus.Acknowledged, AcknowledgedAt: { } ackedAt }
            && _clock.UtcNow - ackedAt > TimeSpan.FromSeconds(65);
        bool tooOld = _clock.UtcNow - alert.StartedAt > TestIdleAlertMaxAge;

        if (ackedLongAgo || tooOld)
        {
            _testScreenAlert = null;
            AlertsChanged?.Invoke();
        }
    }

    private void AcknowledgeTestScreenAlert()
    {
        if (_testScreenAlert is null)
        {
            return;
        }

        _testScreenAlert = _testScreenAlert with
        {
            Status = AlertStatus.Acknowledged,
            AcknowledgedAt = _clock.UtcNow,
            AckedByHost = Environment.MachineName,
            AckedByUser = Environment.UserName,
        };
        AlertsChanged?.Invoke();
    }

    private void OnAlertsMachineChanged(AlertEvent e)
    {
        if (e.Kind == AlertEventKind.Closed) _cluster.RecordClosure(e.Alert);
        if (_cluster.GroupNode is not null && _cluster.IsWatcher)
            _cluster.SetLocalState(_alerts.ActiveAlerts, _supervisor.Snapshots);
        _history.Append(e);
        RecordDowntime(e.Alert);
        AlertsChanged?.Invoke();
    }

    private void RecordDowntime(Alert alert)
    {
        if (!DowntimeJournal.IsRecordedKind(alert.Kind)) return;
        lock (_downtimeGate)
        {
            var episode = (alert.MachineId, alert.StartedAt);
            if (File.Exists(Path.Combine(AppPaths.Resolve(_cfg.Paths.TestScreensDir), alert.MachineId + ".png"))
                || _supervisor.Snapshots.Any(s => s.MachineId == alert.MachineId && s.Source == "plik"))
                _testDowntimeEpisodes.Add(episode);
            if (!_testDowntimeEpisodes.Contains(episode)) Downtime.Record(alert);
        }
    }

    private void OnRemoteEpisodesReceived(IReadOnlyList<Alert> alerts, IReadOnlyList<EpisodeClosure> closures)
    {
        // Obserwator grupy (u siebie: ten komputer) jest źródłem tych obserwacji — stąd Origin wpisu.
        // Obie migawki czytamy RAZ i przed zamkiem: każde odwołanie bierze zamek węzła grupy, a tego
        // nie chcemy trzymać pod _downtimeGate ani powtarzać dla każdego alertu z osobna.
        string? origin = _cluster.WatcherHost;
        var machines = _cluster.RemoteMachines;
        lock (_downtimeGate)
        {
            foreach (var alert in alerts)
            {
                if (!DowntimeJournal.IsRecordedKind(alert.Kind)) continue;
                var episode = (alert.MachineId, alert.StartedAt);
                if (machines.Any(s => s.MachineId == alert.MachineId && s.Source == "plik"))
                    _testDowntimeEpisodes.Add(episode);
                // Raz uznany za testowy zostaje testowy do końca — dokładnie jak w RecordDowntime.
                // Bez tego skasowanie pliku test-ekran w połowie epizodu wpuściłoby jego resztę do raportu.
                if (_testDowntimeEpisodes.Contains(episode)) continue;
                Downtime.Merge(DowntimeEpisode.FromAlert(alert, origin));
            }
            // Zamknięcie epizodu, którego tu nie ma (wykluczony jako testowy albo nigdy nieodebrany),
            // jest w dzienniku no-opem — nie trzeba go odsiewać po raz drugi.
            foreach (var closure in closures)
            {
                if (!DowntimeJournal.IsRecordedKind(closure.Kind)) continue;
                Downtime.MergeClosure(closure.EpisodeId, closure.At, origin);
            }
        }
    }

    private void OnAckReceived(AckRecord ack)
    {
        _alerts.ApplyRemoteAck(ack);
        // Tylko cudze potwierdzenia: własne (wysłane stąd) zapisuje już AcknowledgeAsync, a ten sam
        // rekord wraca tu również z węzła lokalnego. Drugi wpis to dowód, że ACK przeszedł przez sieć.
        if (!string.Equals(ack.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            _logger.LogInformation("Odebrano potwierdzenie alertu {Key} z komputera {Host} (użytkownik {User}).",
                ack.Key, ack.Host, ack.User);
    }

    private void OnRoleChanged(bool isWatcher)
    {
        if (isWatcher && _cluster.GroupNode is { } group)
            _alerts.RestoreObservation(group.TakeoverEpisodes());
        if (_cluster.GroupNode is { } node)
            _logger.LogInformation("Grupa: ten komputer {Change} — {Reason}.",
                isWatcher ? "przejął obserwację maszyn" : "przestał obserwować maszyny (tylko alerty)",
                node.LastRoleChangeReason ?? "powód nieznany");
        RoleChanged?.Invoke();
    }

    private void UpdateReadiness() => _cluster.SetReadiness(GetWatcherReadiness());

    private readonly GroupPeerWarnings _peerWarnings = new();

    private readonly WatcherOverlapTracker _watcherOverlap = new();

    /// <summary>
    /// Obserwatorzy, gdy kilku obserwuje naraz NIEPRZERWANIE od 60 s; inaczej pusta lista. Z tego biorą karta
    /// „Dwóch obserwatorów naraz”, ostrzeżenie nad tabelą „Komputery” i wpis w logu — mówią to samo.
    /// </summary>
    public IReadOnlyList<string> ConfirmedWatcherHosts => _watcherOverlap.Confirmed;

    /// <summary>Zegar programu (Panel liczy „żywy komputer” tym samym zegarem co orkiestrator).</summary>
    public IClock Clock => _clock;

    private void OnPeersChanged() => LogPeerWarningsThenNotify(LogPeerWarnings, PeersChanged,
        ex => _logger.LogDebug(ex, "Grupa: nie udało się zebrać ostrzeżeń o komputerach grupy."));

    private void LogPeerWarnings()
    {
        if (_cluster.GroupNode is null)
        {
            _watcherOverlap.Reset();
            return;
        }
        var peers = _cluster.Peers;
        var aliveTimeout = TimeSpan.FromMilliseconds(_cluster.Configuration.PeerTimeoutMs);
        DateTimeOffset now = _clock.UtcNow;
        var confirmed = _watcherOverlap.Update(
            GroupPeerStatus.Summarize(peers, _cluster.Version, now, aliveTimeout).Watchers, _clock.MonotonicMs);
        foreach (string warning in _peerWarnings.Collect(peers, _cluster.LocalHost, _cluster.Version, now, aliveTimeout, confirmed))
            _logger.LogWarning("{Warning}", warning);
    }

    internal static void LogPeerWarningsThenNotify(Action logWarnings, Action? notify, Action<Exception> onError)
    {
        try { logWarnings(); }
        catch (Exception ex) { onError(ex); }
        notify?.Invoke();
    }

    private void OnClusterAlertsChanged(IReadOnlyList<Alert> alerts) => AlertsChanged?.Invoke();

    private void OnSnapshotsChanged() => MachinesChanged?.Invoke();

    private int _restartAfterSleep;

    private void OnSleepDetected()
    {
        Interlocked.Exchange(ref _restartAfterSleep, 1);
        _watcherOverlap.Reset();
        GuardObservation(Observation.CloseAll);
        _logger.LogWarning("Wykryto wybudzenie lub przerwę w pracy — ponownie łączę maszyny VNC.");
    }

    private void OnResumed()
    {
        _cluster.ForceYield();
        _logger.LogInformation("Komputer wybudzony ze snu — zaplanowano ponowne połączenie z maszynami VNC.");
    }

    private void OnSuspending() => _logger.LogInformation("Komputer usypia.");
}
