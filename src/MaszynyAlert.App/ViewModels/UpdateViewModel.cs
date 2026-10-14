using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task> _approveAndInstall;
    private readonly Func<CancellationToken, Task> _installNow;
    private readonly Func<CancellationToken, Task> _checkNow;
    private readonly Action _postpone;
    private readonly Func<bool> _isGroupMode;
    private readonly TimeZoneInfo _timeZone;
    private UpdateState? _state;

    /// <param name="approveAndInstall">Zgoda i instalacja (<c>UpdateCoordinator.ApproveAndInstallAsync</c>).</param>
    /// <param name="installNow">Ponowna próba instalacji (<c>UpdateCoordinator.InstallNowAsync</c>).</param>
    /// <param name="checkNow">„Sprawdź teraz” (<c>UpdateCoordinator.CheckNowAsync</c>).</param>
    /// <param name="postpone">„Później (24 h)” (<c>UpdateCoordinator.Postpone</c>).</param>
    /// <param name="isGroupMode">Czy ten komputer pracuje w grupie — czytane przy każdym stanie, bo grupę można włączyć w trakcie pracy.</param>
    /// <param name="timeZone">Strefa dat w oknie; domyślnie lokalna (testy podają UTC, żeby wynik nie zależał od komputera).</param>
    public UpdateViewModel(
        Func<CancellationToken, Task> approveAndInstall,
        Func<CancellationToken, Task> installNow,
        Func<CancellationToken, Task> checkNow,
        Action postpone,
        Func<bool> isGroupMode,
        TimeZoneInfo? timeZone = null)
    {
        _approveAndInstall = approveAndInstall ?? throw new ArgumentNullException(nameof(approveAndInstall));
        _installNow = installNow ?? throw new ArgumentNullException(nameof(installNow));
        _checkNow = checkNow ?? throw new ArgumentNullException(nameof(checkNow));
        _postpone = postpone ?? throw new ArgumentNullException(nameof(postpone));
        _isGroupMode = isGroupMode ?? throw new ArgumentNullException(nameof(isGroupMode));
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>Okno ma się schować („Zamknij” albo „Później”). Okno jest singletonem — nigdy się nie zamyka naprawdę.</summary>
    public event Action? CloseRequested;

    /// <summary>Tytuł okna i nagłówek.</summary>
    public string Title => "Aktualizacja VNC Alert";

    /// <summary>Ostatnio pokazana faza (diagnostyka i test startu).</summary>
    [ObservableProperty] private UpdatePhase _phase;

    /// <summary>„Zainstalowana wersja: 0.4.22”.</summary>
    [ObservableProperty] private string _installedVersionText = string.Empty;

    /// <summary>„Dostępna wersja: 0.4.26 (opublikowana dd.MM.yyyy HH:mm)”; pusty, gdy nowszej wersji nie ma.</summary>
    [ObservableProperty] private string _availableVersionText = string.Empty;

    [ObservableProperty] private bool _hasAvailableVersion;

    /// <summary>Kto i kiedy zatwierdził instalację w grupie; pusty bez zgody.</summary>
    [ObservableProperty] private string _approvalText = string.Empty;

    [ObservableProperty] private bool _hasApproval;

    [ObservableProperty] private string _messageText = string.Empty;

    /// <summary>Szczegół techniczny (np. kod wyjścia instalatora, błąd sieci) — małym drukiem pod komunikatem.</summary>
    [ObservableProperty] private string _detailText = string.Empty;

    [ObservableProperty] private bool _hasDetail;

    /// <summary>Podpowiedź pomocnika instalacji („Potwierdź okno Windows (UAC)…”); tylko w fazie instalacji.</summary>
    [ObservableProperty] private string _hintText = string.Empty;

    [ObservableProperty] private bool _hasHint;

    [ObservableProperty] private bool _isProgressVisible;

    /// <summary>Postęp pobierania w procentach 0–100.</summary>
    [ObservableProperty] private double _progressPercent;

    [ObservableProperty] private string _progressText = string.Empty;

    [ObservableProperty] private string _primaryButtonText = "Pobierz i zainstaluj";

    [ObservableProperty] private bool _isPrimaryVisible;

    [ObservableProperty] private bool _canPrimary;

    [ObservableProperty] private bool _canPostpone;

    [ObservableProperty] private bool _canCheckNow;

    /// <summary>Trwa polecenie wywołane z tego okna — przyciski czekają na jego koniec.</summary>
    [ObservableProperty] private bool _isBusy;

    public void Apply(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        Phase = state.Phase;

        Version installed = state.InstalledVersion ?? new Version(0, 0, 0, 0);
        InstalledVersionText = "Zainstalowana wersja: " + FormatVersion(installed);

        bool newerInCatalog = state.Latest is { } latest && latest.Version > installed;
        if (newerInCatalog)
        {
            AvailableVersionText = "Dostępna wersja: " + FormatVersion(state.Latest!.Version)
                + (state.Latest.PublishedAt is { } published ? $" (opublikowana {FormatDate(published)})" : string.Empty);
        }
        else if (state.Approval is { } approved && Version.TryParse(approved.Version, out Version? approvedVersion))
        {
            // Zgoda przyszła z grupy, a ten komputer nie zna jeszcze katalogu (np. brak tokenu dostępu albo
            // sprawdzenie dopiero przed nim) — wersję i tak trzeba pokazać.
            AvailableVersionText = "Dostępna wersja: " + FormatVersion(approvedVersion);
        }
        else
        {
            AvailableVersionText = string.Empty;
        }
        HasAvailableVersion = AvailableVersionText.Length > 0;

        HasApproval = state.Approval is not null;
        ApprovalText = state.Approval is { } approval
            ? $"Instalację zatwierdził komputer {approval.ApprovedBy} {FormatDate(approval.ApprovedAt)} — dotyczy wszystkich komputerów grupy."
            : string.Empty;

        MessageText = string.IsNullOrWhiteSpace(state.Message) ? FallbackMessage(state.Phase) : state.Message;
        DetailText = string.IsNullOrWhiteSpace(state.LastError) ? string.Empty : "Szczegóły: " + state.LastError;
        HasDetail = DetailText.Length > 0;
        if (state.Phase != UpdatePhase.Installing) SetHint(null);

        IsProgressVisible = state.Phase == UpdatePhase.Downloading;
        double progress = double.IsFinite(state.Progress) ? Math.Clamp(state.Progress, 0, 1) : 0;
        ProgressPercent = progress * 100;
        ProgressText = $"Pobrano {Math.Round(ProgressPercent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} %";

        bool windowPhase = state.Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.ReadyToInstall
            or UpdatePhase.Installing or UpdatePhase.WaitingForAdmin or UpdatePhase.Failed;
        IsPrimaryVisible = windowPhase && (HasApproval || newerInCatalog);
        PrimaryButtonText = HasApproval
            ? "Zainstaluj"
            : _isGroupMode() ? "Pobierz i zainstaluj na wszystkich komputerach grupy" : "Pobierz i zainstaluj";
        RefreshAvailability();
    }

    /// <summary>
    /// Podpowiedź pomocnika instalacji (okno UAC czeka na potwierdzenie). <see langword="null"/> czyści.
    /// Podpowiedź znika sama przy następnej fazie innej niż instalacja.
    /// </summary>
    public void SetHint(string? hint)
    {
        HintText = hint ?? string.Empty;
        HasHint = HintText.Length > 0;
    }

    /// <summary>Wersja jak w nazwie wydania, bez zbędnego „.0” na końcu: 0.4.22.0 → „0.4.22”.</summary>
    public static string FormatVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Build < 0) return version.ToString(2);
        return version.Revision <= 0 ? version.ToString(3) : version.ToString(4);
    }

    private string FormatDate(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _timeZone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string FallbackMessage(UpdatePhase phase) => phase switch
    {
        UpdatePhase.Disabled => "Aktualizacje są wyłączone na tym komputerze.",
        UpdatePhase.Idle => "Program sprawdza dostępność nowej wersji co godzinę.",
        UpdatePhase.Checking => "Sprawdzam katalog wydań…",
        UpdatePhase.UpToDate => "Program jest aktualny.",
        UpdatePhase.Available => "Jest nowa wersja programu.",
        UpdatePhase.Downloading => "Pobieram nową wersję…",
        UpdatePhase.ReadyToInstall => "Nowa wersja jest pobrana i czeka na instalację.",
        UpdatePhase.Installing => "Trwa instalacja — program zaraz uruchomi się ponownie.",
        UpdatePhase.WaitingForAdmin => "Instalacja czeka na zgodę administratora (UAC) — kliknij Zainstaluj.",
        UpdatePhase.Failed => "Aktualizacja nie powiodła się.",
        _ => string.Empty,
    };

    private void RefreshAvailability()
    {
        UpdatePhase phase = _state?.Phase ?? UpdatePhase.Disabled;
        bool working = phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;
        CanPrimary = IsPrimaryVisible && !working && !IsBusy;
        CanPostpone = !IsBusy && !HasApproval && phase is UpdatePhase.Available or UpdatePhase.Failed;
        CanCheckNow = !IsBusy && !working && phase != UpdatePhase.Disabled;
    }

    partial void OnIsBusyChanged(bool value) => RefreshAvailability();

    /// <summary>Przycisk główny: zgoda + instalacja (bez zgody) albo ponowna instalacja (ze zgodą).</summary>
    [RelayCommand]
    private Task PrimaryAsync() => !CanPrimary
        ? Task.CompletedTask
        : RunAsync(HasApproval ? _installNow : _approveAndInstall);

    /// <summary>„Sprawdź teraz”.</summary>
    [RelayCommand]
    private Task CheckNowAsync() => !CanCheckNow ? Task.CompletedTask : RunAsync(_checkNow);

    [RelayCommand]
    private void Postpone()
    {
        if (!CanPostpone) return;
        _postpone();
        CloseRequested?.Invoke();
    }

    /// <summary>„Zamknij” — tylko chowa okno; aktualizacja toczy się dalej w tle.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex)
        {
            DetailText = "Szczegóły: " + ex.Message;
            HasDetail = true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
