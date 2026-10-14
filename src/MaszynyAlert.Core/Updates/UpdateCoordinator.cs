using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Updates;

/// <summary>Etap aktualizacji widoczny w oknie „Aktualizacja VNC Alert” i w diagnostyce.</summary>
public enum UpdatePhase
{
    /// <summary>Aktualizacje wyłączone (<c>updates.enabled = false</c>) albo wersja portable.</summary>
    Disabled,

    /// <summary>Nic się nie dzieje: przed pierwszym sprawdzeniem albo katalog chwilowo niedostępny.</summary>
    Idle,

    /// <summary>Trwa odczyt spisu wydań.</summary>
    Checking,

    /// <summary>Zainstalowana wersja jest najnowsza.</summary>
    UpToDate,

    /// <summary>Jest nowsza wersja, a nikt w grupie jeszcze nie zgodził się jej zainstalować.</summary>
    Available,

    /// <summary>Pobieranie instalatora (postęp w <see cref="UpdateState.Progress"/>).</summary>
    Downloading,

    /// <summary>Instalator pobrany i sprawdzony; czeka na koniec alarmu.</summary>
    ReadyToInstall,

    /// <summary>Instalator uruchomiony — program zaraz zostanie zatrzymany i uruchomiony ponownie.</summary>
    Installing,

    /// <summary>Instalacja czeka na człowieka: UAC odrzucone, poprzednia próba nieudana albo komputer nie instaluje sam.</summary>
    WaitingForAdmin,

    /// <summary>Pobranie albo instalacja się nie udały (szczegół w <see cref="UpdateState.LastError"/>).</summary>
    Failed,
}

/// <summary>
/// Niezmienny obraz stanu aktualizacji — okno i diagnostyka dostają gotowy snapshot, bez blokad po swojej
/// stronie (<see cref="UpdateCoordinator.State"/>).
/// </summary>
public sealed record UpdateState
{
    /// <summary>Bieżący etap.</summary>
    public UpdatePhase Phase { get; init; }

    /// <summary>Wersja tego programu (czteroczęściowa).</summary>
    public Version InstalledVersion { get; init; } = new(0, 0, 0, 0);

    /// <summary>Najnowsze wydanie z katalogu (gdy znane).</summary>
    public ReleaseEntry? Latest { get; init; }

    /// <summary>Obowiązująca zgoda z grupy (tylko gdy dotyczy wersji nowszej od zainstalowanej).</summary>
    public UpdateApproval? Approval { get; init; }

    /// <summary>Postęp pobierania 0..1.</summary>
    public double Progress { get; init; }

    /// <summary>Tekst po polsku dla okna i diagnostyki — bez żargonu, dla operatora.</summary>
    public string Message { get; init; } = "";

    /// <summary>Czas ostatniego sprawdzenia katalogu (udanego albo nie).</summary>
    public DateTimeOffset? LastCheckAt { get; init; }

    /// <summary>Szczegół ostatniego błędu (może być techniczny) albo <see langword="null"/>.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? SnoozedUntil { get; init; }

    [JsonIgnore]
    public bool ShouldShowWindow =>
        Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.ReadyToInstall or UpdatePhase.Installing
            or UpdatePhase.WaitingForAdmin or UpdatePhase.Failed
        && (SnoozedUntil is null || Approval is not null);
}

public sealed class UpdateCoordinator
{
    /// <summary>Pierwsze sprawdzenie po starcie — nie dokładamy sieci do startu programu i połączeń VNC.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Wewnętrzny takt: ponowna ocena zgody i końca alarmu (bez odpytywania serwera).</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>Jak długo „Później” chowa okno.</summary>
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromHours(24);

    /// <summary>Nazwa znacznika próby instalacji w katalogu pobrań.</summary>
    public const string PendingInstallFileName = "pending-install.json";

    private static readonly JsonSerializerOptions PendingJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly UpdatesConfig _config;
    private readonly bool _enabled;
    private readonly Version _installedVersion;
    private readonly IUpdateSource _source;
    private readonly IUpdateInstaller _installer;
    private readonly SharedConfigSync _shared;
    private readonly Func<SharedConfigDocument, Task> _broadcast;
    private readonly string _downloadDirectory;
    private readonly string _pendingDirectory;
    private readonly Func<bool>? _taskRegistered;
    private readonly string _host;
    private readonly IClock _clock;
    private readonly Func<bool> _canInstallNow;
    private readonly ILogger<UpdateCoordinator> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private UpdateState _state;

    // Pola niżej czyta i zmienia wyłącznie kod pod semaforem _work.
    private IReadOnlyList<ReleaseEntry> _releases = [];
    private readonly HashSet<Version> _autoAttempted = [];
    private Version? _previousAttemptVersion;
    private bool _startupChecked;
    private Version? _autoBlockedUntilNextCheck;
    private Version? _catalogLookedUpFor;

    private Version? _approvalNotInCatalog;
    private (Version Version, string Path)? _downloaded;
    private volatile bool _waitingForInstaller;

    internal TimeSpan InstallerAnswerLimit { get; set; } = TimeSpan.FromMinutes(10);

    public UpdateCoordinator(UpdatesConfig config, bool installed, Version installedVersion,
        IUpdateSource source, IUpdateInstaller installer, SharedConfigSync shared, Func<SharedConfigDocument, Task> broadcast,
        string downloadDirectory, string host, IClock clock, Func<bool> canInstallNow, ILogger<UpdateCoordinator> logger,
        string? pendingDirectory = null, Func<bool>? taskRegistered = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(installedVersion);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(broadcast);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(canInstallNow);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _enabled = installed && config.Enabled;
        _installedVersion = Normalize(installedVersion);
        _source = source;
        _installer = installer;
        _shared = shared;
        _broadcast = broadcast;
        _downloadDirectory = downloadDirectory;
        _pendingDirectory = string.IsNullOrWhiteSpace(pendingDirectory) ? downloadDirectory : pendingDirectory;
        _taskRegistered = taskRegistered;
        _host = host;
        _clock = clock;
        _canInstallNow = canInstallNow;
        _logger = logger;

        string message = !installed
            ? "Wersja portable — aktualizacje tylko dla wersji zainstalowanej."
            : !config.Enabled
                ? "Automatyczne aktualizacje są wyłączone w ustawieniach programu."
                : "Sprawdzanie aktualizacji ruszy kilka minut po starcie programu.";
        _state = new UpdateState
        {
            Phase = _enabled ? UpdatePhase.Idle : UpdatePhase.Disabled,
            InstalledVersion = _installedVersion,
            Message = message,
        };
    }

    /// <summary>Niezmienny snapshot stanu — bezpieczny do czytania z dowolnego wątku.</summary>
    public UpdateState State { get { lock (_gate) return _state; } }

    /// <summary>Stan się zmienił (wołane z wątku roboczego — okno musi przejść na swój Dispatcher).</summary>
    public event Action? StateChanged;

    private string PendingPath => Path.Combine(PendingDirectory, PendingInstallFileName);

    private string PendingDirectory
    {
        get
        {
            if (_taskRegistered is null || _pendingDirectory == _downloadDirectory) return _pendingDirectory;
            try { return _taskRegistered() ? _pendingDirectory : _downloadDirectory; }
            catch (Exception ex)
            {
                // Tak jak instalator aktualizacji: błąd sprawdzenia zadania = ścieżka z UAC.
                _logger.LogDebug(ex, "Nie sprawdzono zadania aktualizacji — znacznik próby w katalogu pobrań.");
                return _downloadDirectory;
            }
        }
    }

    /// <summary>Treść znacznika próby — ta sama dla programu i dla zadania „VNC Alert update” (<see cref="UpdateTaskVerifier"/>).</summary>
    internal static string SerializePending(Version version, DateTimeOffset startedAt) =>
        JsonSerializer.Serialize(new PendingInstall(version.ToString(4), startedAt), PendingJson);

    /// <summary>
    /// Pętla tła: po <see cref="StartupDelay"/> pierwsze sprawdzenie, potem co <see cref="TickInterval"/>
    /// ocena zgody i alarmu, a co <c>updates.checkIntervalMinutes</c> — sprawdzenie katalogu. Przy
    /// wyłączonych aktualizacjach kończy się od razu. Nie rzuca: anulowanie kończy ją normalnie, a każdy
    /// inny wyjątek jest logowany i pętla jedzie dalej.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!_enabled) return;
        using var link = ct.Register(() => { try { _stopping.Cancel(); } catch (ObjectDisposedException) { } });
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
            await SafeAsync(CheckNowAsync, ct).ConfigureAwait(false);
            var interval = TimeSpan.FromMinutes(Math.Clamp(_config.CheckIntervalMinutes, 5, 1440));
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TickInterval, ct).ConfigureAwait(false);
                var lastCheck = State.LastCheckAt;
                if (lastCheck is null || _clock.UtcNow - lastCheck.Value >= interval)
                    await SafeAsync(CheckNowAsync, ct).ConfigureAwait(false);
                else
                    await SafeAsync(EvaluateAsync, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Zamykanie programu.
        }
    }

    /// <summary>„Sprawdź teraz” (i takt sprawdzenia w pętli): odczyt katalogu, potem <see cref="EvaluateAsync"/>.</summary>
    public async Task CheckNowAsync(CancellationToken ct)
    {
        if (!_enabled) return;
        if (!await EnterAsync(ct).ConfigureAwait(false)) return;
        try
        {
            await CheckCoreAsync(ct).ConfigureAwait(false);
            await EvaluateCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _work.Release(); }
    }

    /// <summary>
    /// Kliknięcie zgody na tym komputerze: zapis zgody we wspólnej konfiguracji i rozgłoszenie (błąd
    /// rozgłoszenia tylko w logu — pozostałe komputery i tak dostaną dokument przy uzgadnianiu odcisków),
    /// pobranie i uruchomienie instalatora — bez względu na alarm, bo zdecydował człowiek.
    /// </summary>
    public async Task ApproveAndInstallAsync(CancellationToken ct)
    {
        if (!_enabled) return;
        if (!await EnterAsync(ct).ConfigureAwait(false)) return;
        try
        {
            if (State.Latest is not { } known || known.Version <= _installedVersion)
                await CheckCoreAsync(ct).ConfigureAwait(false);
            if (State.Latest is not { } release || release.Version <= _installedVersion)
            {
                Update(s => s with { Message = $"Brak nowszej wersji do zainstalowania — zainstalowana {Display(_installedVersion)} jest najnowsza." });
                return;
            }

            var approval = new UpdateApproval
            {
                Version = release.Version.ToString(4), FileName = release.FileName, ApprovedBy = _host, ApprovedAt = _clock.UtcNow,
            };
            SharedConfigDocument document;
            try { document = _shared.Approve(approval, _clock.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                _logger.LogError(ex, "Nie udało się zapisać zgody na aktualizację do {Version}.", approval.Version);
                Update(s => s with
                {
                    Phase = UpdatePhase.Failed,
                    Message = "Nie udało się zapisać zgody na aktualizację — spróbuj ponownie albo zgłoś administratorowi.",
                    LastError = ex.Message,
                });
                return;
            }
            _logger.LogInformation("Zgoda na aktualizację do {Version} zapisana na {Host}.", approval.Version, _host);
            Update(s => s with { Approval = approval, SnoozedUntil = null });

            try { await _broadcast(document).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Nie udało się rozgłosić zgody na aktualizację — pozostałe komputery dostaną ją przy uzgadnianiu.");
            }

            _autoAttempted.Add(release.Version);
            await DownloadAndLaunchAsync(release, ct).ConfigureAwait(false);
        }
        finally { _work.Release(); }
    }

    /// <summary>
    /// „Zainstaluj” z okna (po odrzuconym UAC, nieudanej próbie albo na komputerze, który nie instaluje
    /// sam): pobranie, jeśli trzeba, i uruchomienie instalatora — bez względu na alarm. Celem jest wersja
    /// z obowiązującej zgody, a bez zgody — najnowsza z katalogu.
    /// </summary>
    public async Task InstallNowAsync(CancellationToken ct)
    {
        if (!_enabled) return;
        if (!await EnterAsync(ct).ConfigureAwait(false)) return;
        try
        {
            var approval = CurrentApproval();
            ReleaseEntry? release;
            if (approval is not null)
            {
                release = Find(approval);
                if (release is null)
                {
                    await CheckCoreAsync(ct).ConfigureAwait(false);
                    release = Find(approval);
                }
                if (release is null) { NotInCatalog(approval); return; }
            }
            else
            {
                if (State.Latest is not { } known || known.Version <= _installedVersion)
                    await CheckCoreAsync(ct).ConfigureAwait(false);
                release = State.Latest is { } latest && latest.Version > _installedVersion ? latest : null;
                if (release is null)
                {
                    Update(s => s with { Message = $"Brak nowszej wersji do zainstalowania — zainstalowana {Display(_installedVersion)} jest najnowsza." });
                    return;
                }
            }

            _autoAttempted.Add(release.Version);
            await DownloadAndLaunchAsync(release, ct).ConfigureAwait(false);
        }
        finally { _work.Release(); }
    }

    /// <summary>„Później”: okno chowa się na <see cref="SnoozeDuration"/> (zgoda z grupy i tak je pokaże).</summary>
    public void Postpone()
    {
        var until = _clock.UtcNow + SnoozeDuration;
        Update(s => s with { SnoozedUntil = until });
        _logger.LogInformation("Okno aktualizacji odłożone do {Until:u}.", until);
    }

    /// <summary>
    /// Wspólna konfiguracja zmieniła się z sieci (<see cref="SharedConfigSync.Changed"/>) — ocena zgody w tle.
    /// Nie blokuje wołającego (zdarzenie przychodzi z wątku sieci grupy).
    /// </summary>
    public void OnSharedConfigChanged()
    {
        if (!_enabled) return;
        var token = _stopping.Token;
        _ = Task.Run(() => SafeAsync(EvaluateAsync, token), CancellationToken.None);
    }

    /// <summary>
    /// Ocena reguł bez odpytywania katalogu (chyba że zatwierdzonego wydania nie ma w ostatnim spisie):
    /// sprzątanie po udanej instalacji, zgoda z grupy, automatyczne pobranie i instalacja po zakończeniu
    /// alarmu, upływ „Później”. Publiczna dla testów i dla pętli.
    /// </summary>
    public async Task EvaluateAsync(CancellationToken ct)
    {
        if (!_enabled) return;
        if (!await EnterAsync(ct).ConfigureAwait(false)) return;
        try { await EvaluateCoreAsync(ct).ConfigureAwait(false); }
        finally { _work.Release(); }
    }

    private async Task<bool> EnterAsync(CancellationToken ct)
    {
        while (!await _work.WaitAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false))
        {
            if (_waitingForInstaller)
            {
                Update(s => s with
                {
                    Message = "Instalacja już trwa — potwierdź okno Windows (UAC) albo poczekaj, aż wygaśnie; potem kliknij Zainstaluj.",
                });
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------------------------------------
    // Rdzeń (wołany wyłącznie pod semaforem _work)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Odczyt katalogu; <see langword="true"/>, gdy spis udało się odczytać.</summary>
    private async Task<bool> CheckCoreAsync(CancellationToken ct)
    {
        Update(s => s.Phase is UpdatePhase.Idle or UpdatePhase.UpToDate
            ? s with { Phase = UpdatePhase.Checking, Message = "Sprawdzanie, czy jest nowsza wersja…" }
            : s);

        UpdateCheckResult result;
        try { result = await _source.CheckAsync(_installedVersion, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { RestoreAfterCancelledCheck(); throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sprawdzenie aktualizacji nie powiodło się.");
            result = new UpdateCheckResult(UpdateCheckOutcome.Unavailable, null, ex.Message, _clock.UtcNow);
        }

        var now = _clock.UtcNow;
        // Nowy spis = nowa szansa: zatwierdzone wydanie można znów wyszukać, a nieudane pobranie ponowić.
        _autoBlockedUntilNextCheck = null;
        _catalogLookedUpFor = null;

        if (result.Outcome is UpdateCheckOutcome.UpToDate or UpdateCheckOutcome.Available && result.Latest is { } latest)
        {
            _releases = result.Releases.Count > 0 ? result.Releases : [latest];
            bool newer = latest.Version > _installedVersion;
            // Błąd sprzed sprawdzenia (np. nieudany zapis zgody) bez obowiązującej zgody nie ma już czego
            // pilnować — świeży spis ustala etap od nowa. Ze zgodą etap ustala EvaluateCoreAsync.
            bool resetFailed = CurrentApproval() is null;
            Update(s =>
            {
                var next = s with { Latest = latest, LastCheckAt = now, LastError = null };
                if (s.Phase is UpdatePhase.Checking or UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Available
                    || (resetFailed && s.Phase == UpdatePhase.Failed))
                {
                    next = newer
                        ? next with { Phase = UpdatePhase.Available, Message = AvailableMessage(latest) }
                        : next with { Phase = UpdatePhase.UpToDate, Message = $"Zainstalowana wersja {Display(_installedVersion)} jest najnowsza." };
                }
                return next;
            });
            if (newer) _logger.LogInformation("Dostępna wersja {Version} ({File}).", Display(latest.Version), latest.FileName);
            return true;
        }

        string error = string.IsNullOrWhiteSpace(result.Error) ? "Katalog wydań jest niedostępny." : result.Error;
        string message = result.Outcome == UpdateCheckOutcome.NeedsLogin
            ? "Nie można sprawdzić aktualizacji: katalog wydań wymaga logowania — administrator musi wpisać token dostępu w Panel → Ustawienia → Ten komputer. Program działa dalej normalnie."
            : "Nie udało się sprawdzić aktualizacji — katalog wydań jest chwilowo niedostępny. Program działa dalej normalnie.";
        _logger.LogInformation("Aktualizacje: {Outcome} — {Error}", result.Outcome, error);
        Update(s => s.Phase is UpdatePhase.Checking or UpdatePhase.Idle or UpdatePhase.UpToDate
            ? s with { Phase = UpdatePhase.Idle, Message = message, LastError = error, LastCheckAt = now }
            : s with { LastError = error, LastCheckAt = now });
        return false;
    }

    private void RestoreAfterCancelledCheck() =>
        Update(s => s.Phase == UpdatePhase.Checking ? s with { Phase = s.Latest is null ? UpdatePhase.Idle : PhaseForLatest(s.Latest) } : s);

    private UpdatePhase PhaseForLatest(ReleaseEntry latest) =>
        latest.Version > _installedVersion ? UpdatePhase.Available : UpdatePhase.UpToDate;

    private async Task EvaluateCoreAsync(CancellationToken ct)
    {
        CheckPendingInstallOnce();

        var now = _clock.UtcNow;
        Update(s => s.SnoozedUntil is { } until && until <= now ? s with { SnoozedUntil = null } : s);

        var approval = CurrentApproval();
        Update(s => Equals(s.Approval, approval) ? s : s with { Approval = approval });

        if (approval is null)
        {
            // Bez zgody: oferta nowszej wersji, o ile nic nie jest w toku.
            Update(s => s.Latest is { } latest && latest.Version > _installedVersion
                        && s.Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Checking
                    ? s with { Phase = UpdatePhase.Available, Message = AvailableMessage(latest) }
                    : s);
            return;
        }

        var version = approval.ParsedVersion;
        var phase = State.Phase;
        if (phase == UpdatePhase.Installing && _autoAttempted.Contains(version)) return;

        if (_autoAttempted.Contains(version) || !_config.AutoInstallWhenApproved)
        {
            if (phase is UpdatePhase.WaitingForAdmin or UpdatePhase.Failed or UpdatePhase.Installing) return;
            string message = _previousAttemptVersion == version
                ? $"Poprzednia instalacja wersji {Display(version)} nie doszła do skutku — kliknij Zainstaluj i potwierdź okno Windows (UAC)."
                : _autoAttempted.Contains(version)
                    ? "Instalacja czeka na zgodę administratora (UAC) — kliknij Zainstaluj."
                    : $"Wersja {Display(version)} zatwierdzona na komputerze {approval.ApprovedBy} — ten komputer nie instaluje sam, kliknij Zainstaluj.";
            Update(s => s with { Phase = UpdatePhase.WaitingForAdmin, Message = message });
            return;
        }

        // Nieudane pobranie / brak w katalogu: kolejna próba dopiero po następnym sprawdzeniu katalogu,
        // nie co takt (serwer i operator dostawaliby to samo co 30 s).
        if (_autoBlockedUntilNextCheck == version) return;

        var release = Find(approval);
        if (release is null && _catalogLookedUpFor != version)
        {
            bool read = await CheckCoreAsync(ct).ConfigureAwait(false);
            _catalogLookedUpFor = version;
            release = Find(approval);
            if (!read && release is null)
            {
                _autoBlockedUntilNextCheck = version;
                Update(s => s with
                {
                    Phase = UpdatePhase.Failed,
                    Message = $"Nie udało się sprawdzić katalogu wydań, żeby pobrać zatwierdzoną wersję {Display(version)} — kolejna próba przy następnym sprawdzeniu.",
                });
                return;
            }
        }
        if (release is null)
        {
            _autoBlockedUntilNextCheck = version;
            NotInCatalog(approval);
            return;
        }

        string? path = await DownloadAsync(release, ct, automaticRetry: true).ConfigureAwait(false);
        if (path is null) { _autoBlockedUntilNextCheck = version; return; }

        if (!CanInstallNow())
        {
            Update(s => s with
            {
                Phase = UpdatePhase.ReadyToInstall,
                Progress = 1.0,
                Message = $"Pobrano wersję {Display(version)}; instalacja po zakończeniu alarmu.",
            });
            return;
        }

        _autoAttempted.Add(version);
        await LaunchAsync(release, path, ct).ConfigureAwait(false);
    }

    private async Task DownloadAndLaunchAsync(ReleaseEntry release, CancellationToken ct)
    {
        string? path = await DownloadAsync(release, ct).ConfigureAwait(false);
        if (path is null) return;
        await LaunchAsync(release, path, ct).ConfigureAwait(false);
    }

    private async Task<string?> DownloadAsync(ReleaseEntry release, CancellationToken ct, bool automaticRetry = false)
    {
        if (_downloaded is { } done && done.Version == release.Version && File.Exists(done.Path)) return done.Path;

        Update(s => s with { Phase = UpdatePhase.Downloading, Progress = 0, Message = $"Pobieranie wersji {Display(release.Version)}…" });
        try
        {
            string path = await _source.DownloadAsync(release, _downloadDirectory, new StateProgress(this), ct).ConfigureAwait(false);
            _downloaded = (release.Version, path);
            Update(s => s with { Progress = 1.0, LastError = null });
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (IsOrdinaryTransferError(ex)) _logger.LogWarning("Pobranie {File} nie powiodło się: {Error}", release.FileName, ex.Message);
            else _logger.LogWarning(ex, "Pobranie {File} nie powiodło się.", release.FileName);
            Update(s => s with
            {
                Phase = UpdatePhase.Failed,
                Message = automaticRetry
                    ? $"Nie udało się pobrać wersji {Display(release.Version)} — kolejna próba przy następnym sprawdzeniu albo kliknij Zainstaluj."
                    : $"Nie udało się pobrać wersji {Display(release.Version)} — kliknij Zainstaluj, żeby spróbować ponownie.",
                LastError = ex.Message,
            });
            return null;
        }
    }

    internal static bool IsOrdinaryTransferError(Exception ex) =>
        ex is IOException or InvalidDataException or HttpRequestException or TimeoutException or TaskCanceledException;

    private async Task LaunchAsync(ReleaseEntry release, string path, CancellationToken ct)
    {
        if (!await HttpUpdateSource.MatchesAsync(path, release.SizeBytes, release.Sha256 ?? "", ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Plik {File} nie zgadza się ze spisem wydań (rozmiar albo SHA-256) — usunięty, pobieranie od nowa.", release.FileName);
            _downloaded = null;
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            string? fresh = await DownloadAsync(release, ct).ConfigureAwait(false);
            if (fresh is null) return;
            path = fresh;
        }

        WritePending(release.Version);
        Update(s => s with
        {
            Phase = UpdatePhase.Installing,
            Message = $"Uruchamianie instalatora wersji {Display(release.Version)} — potwierdź okno Windows (UAC), jeśli się pojawi.",
        });
        _logger.LogInformation("Uruchamianie instalatora {File}.", release.FileName);

        (InstallLaunchResult Result, string? Error) outcome;
        _waitingForInstaller = true;
        try
        {
            outcome = await _installer.LaunchAsync(path, release.Version, release.Sha256, ct)
                .WaitAsync(InstallerAnswerLimit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TimeoutException)
        {
            outcome = (InstallLaunchResult.CancelledByUser,
                $"Brak odpowiedzi pomocnika instalacji (okno Windows UAC albo zadanie aktualizacji) przez {(int)InstallerAnswerLimit.TotalMinutes} min.");
        }
        catch (Exception ex) { outcome = (InstallLaunchResult.Failed, ex.Message); }
        finally { _waitingForInstaller = false; }

        switch (outcome.Result)
        {
            case InstallLaunchResult.Started:
                Update(s => s with { Phase = UpdatePhase.Installing, Message = "Trwa instalacja — program zaraz uruchomi się ponownie." });
                break;
            case InstallLaunchResult.CancelledByUser:
                _logger.LogWarning("Instalacja {Version} czeka na UAC (anulowane albo bez odpowiedzi).", Display(release.Version));
                Update(s => s with
                {
                    Phase = UpdatePhase.WaitingForAdmin,
                    Message = "Instalacja czeka na zgodę administratora (UAC) — kliknij Zainstaluj.",
                    LastError = outcome.Error,
                });
                break;
            default:
                _logger.LogError("Instalacja {Version} nie powiodła się: {Error}", Display(release.Version), outcome.Error);
                Update(s => s with
                {
                    Phase = UpdatePhase.Failed,
                    Message = $"Instalacja wersji {Display(release.Version)} nie powiodła się — kliknij Zainstaluj, żeby spróbować ponownie.",
                    LastError = outcome.Error ?? "Instalator zgłosił błąd.",
                });
                break;
        }
    }

    private void NotInCatalog(UpdateApproval approval)
    {
        if (State.Latest is { } latest && latest.Version > _installedVersion && latest.Version != approval.ParsedVersion)
        {
            _approvalNotInCatalog = approval.ParsedVersion;
            _logger.LogWarning("Zatwierdzonej wersji {Approved} nie ma w katalogu wydań; proponowana dostępna {Latest}.",
                approval.Version, Display(latest.Version));
            Update(s => s with
            {
                Phase = UpdatePhase.Available,
                Approval = null,
                Message = $"Zatwierdzona wersja {Display(approval.ParsedVersion)} nie jest już w katalogu wydań. Dostępna jest wersja " +
                    $"{Display(latest.Version)} — zatwierdź ją, żeby zainstalować.",
                LastError = $"Brak pliku {approval.FileName} w spisie INDEX.md.",
            });
            return;
        }
        Update(s => s with
        {
            Phase = UpdatePhase.Failed,
            Message = $"Wydanie {approval.FileName} nie jest już w katalogu wydań — sprawdź aktualizacje i zatwierdź instalację ponownie.",
            LastError = $"Brak pliku {approval.FileName} w spisie INDEX.md.",
        });
    }

    /// <summary>Zgoda z grupy, jeśli jest poprawna i dotyczy wersji nowszej od zainstalowanej.</summary>
    private UpdateApproval? CurrentApproval()
    {
        var approval = _shared.Current?.UpdateApproval;
        if (approval is null || !approval.IsValid()) return null;
        if (approval.ParsedVersion == _approvalNotInCatalog && Find(approval) is null) return null;
        return approval.ParsedVersion > _installedVersion ? approval : null;
    }

    private ReleaseEntry? Find(UpdateApproval approval)
    {
        var version = approval.ParsedVersion;
        return _releases.FirstOrDefault(r => string.Equals(r.FileName, approval.FileName, StringComparison.OrdinalIgnoreCase))
            ?? (State.Latest is { } latest && string.Equals(latest.FileName, approval.FileName, StringComparison.OrdinalIgnoreCase) ? latest : null)
            ?? _releases.FirstOrDefault(r => r.Version == version);
    }

    private bool CanInstallNow()
    {
        try { return _canInstallNow(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się sprawdzić, czy trwa alarm — instalacja wstrzymana.");
            return false;
        }
    }

    /// <summary>znacznik po udanej instalacji sprzątamy, po nieudanej — pamiętamy próbę.</summary>
    private void CheckPendingInstallOnce()
    {
        if (_startupChecked) return;
        _startupChecked = true;
        string pendingPath = PendingPath;
        if (!File.Exists(pendingPath)) return;
        Version? pending = null;
        try
        {
            var file = JsonSerializer.Deserialize<PendingInstall>(File.ReadAllText(pendingPath), PendingJson);
            if (file?.Version is { } text && ReleaseCatalog.TryBuildVersion(text.Trim(), out var parsed)) pending = parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Znacznik próby instalacji {Path} jest nieczytelny — usuwam go.", pendingPath);
        }

        if (pending is null || pending <= _installedVersion)
        {
            if (pending is not null) _logger.LogInformation("Zaktualizowano do wersji {Version}.", Display(_installedVersion));
            TryDeletePending(pendingPath);
            return;
        }

        _autoAttempted.Add(pending);
        _previousAttemptVersion = pending;
        _logger.LogWarning("Poprzednia instalacja wersji {Version} nie doszła do skutku (program nadal w {Installed}).",
            Display(pending), Display(_installedVersion));
    }

    private void WritePending(Version version)
    {
        string directory = PendingDirectory;
        string path = Path.Combine(directory, PendingInstallFileName);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, SerializePending(version, _clock.UtcNow));
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Znacznik próby instalacji {Path} zapisze zadanie aktualizacji (katalog tylko dla administratorów).", path);
        }
        catch (IOException ex)
        {
            // Bez znacznika po restarcie spróbujemy automatycznie jeszcze raz — gorzej, ale nie groźnie.
            _logger.LogWarning(ex, "Nie udało się zapisać znacznika próby instalacji {Path}.", path);
        }
    }

    private void TryDeletePending(string pendingPath)
    {
        try { File.Delete(pendingPath); }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Znacznik próby instalacji {Path} zostaje — katalog tylko dla administratorów.", pendingPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Nie udało się usunąć znacznika próby instalacji {Path}.", pendingPath);
        }
    }

    private string AvailableMessage(ReleaseEntry latest)
    {
        string published = latest.PublishedAt is { } at
            ? " (opublikowana " + at.ToOffset(_clock.Now.Offset).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + ")"
            : "";
        return $"Dostępna wersja {Display(latest.Version)}{published}, zainstalowana {Display(_installedVersion)}.";
    }

    /// <summary>Wersja dla człowieka: „0.4.26” zamiast „0.4.26.0” (czwarta część tylko, gdy niezerowa).</summary>
    private static string Display(Version version)
    {
        var full = Normalize(version);
        return full.Revision > 0 ? full.ToString(4) : full.ToString(3);
    }

    private static Version Normalize(Version version) =>
        new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private async Task SafeAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "Błąd w obsłudze aktualizacji — program działa dalej."); }
    }

    private void Update(Func<UpdateState, UpdateState> change)
    {
        bool changed;
        lock (_gate)
        {
            var next = change(_state);
            changed = !Equals(next, _state);
            _state = next;
        }
        if (!changed) return;
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception ex) { _logger.LogError(ex, "Obsługa zmiany stanu aktualizacji rzuciła wyjątek."); }
        }
    }

    /// <summary>
    /// Postęp pobierania zapisywany od razu do stanu (nie przez <see cref="Progress{T}"/>, który zgłasza
    /// asynchronicznie i mógłby nadpisać etap już po zakończeniu pobierania).
    /// </summary>
    private sealed class StateProgress(UpdateCoordinator owner) : IProgress<double>
    {
        public void Report(double value)
        {
            double clamped = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            owner.Update(s => s.Phase == UpdatePhase.Downloading ? s with { Progress = clamped } : s);
        }
    }

    private sealed record PendingInstall(string? Version, DateTimeOffset? StartedAt);
}
