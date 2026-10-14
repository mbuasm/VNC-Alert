using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class StatusWindowViewModel : ObservableObject
{
    private readonly AppOrchestrator _orchestrator;
    private readonly InMemoryLogSink _logSink;
    private readonly Func<AlertKey, Task> _acknowledge;
    private readonly Func<AlertKey, Task> _dismissIdle;
    private readonly Func<AlertKey, Task> _dismissScreen;

    public StatusWindowViewModel(AppOrchestrator orchestrator, InMemoryLogSink logSink, BuildIdentity build)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _acknowledge = key => _orchestrator.AcknowledgeAsync(key);
        _dismissIdle = key => _orchestrator.DismissIdleAlertAsync(key);
        _dismissScreen = key => _orchestrator.DismissWrongScreenAlertAsync(key);

        VersionText = build.DisplayText;
        ModeText = "Tryb: " + (AppPaths.IsInstalled ? "zainstalowana" : "portable (testowa)");
        DataDirText = "Dane: " + AppPaths.DataDir;
        OcrText = _orchestrator.Ocr.IsAvailable ? $"Język OCR: {_orchestrator.Ocr.LanguageTag}" : "OCR niedostępny — brak kontroli HMI i postoju; kolor nadal może wywołać alarm";
        IsOcrWarning = !_orchestrator.Ocr.IsAvailable;
        Captures = new CapturesPanelViewModel(orchestrator);
    }

    public CapturesPanelViewModel Captures { get; }

    public ObservableCollection<MachineRowViewModel> Machines { get; } = new();
    public ObservableCollection<PeerRowViewModel> Peers { get; } = new();
    public ObservableCollection<AlertItemViewModel> ActiveAlerts { get; } = new();
    public ObservableCollection<IdleAlertItemViewModel> IdleAlerts { get; } = new();
    public ObservableCollection<ScreenAlertItemViewModel> WrongScreenAlerts { get; } = new();

    public bool IsClusterEnabled => _orchestrator.Cluster.Configuration.Enabled;

    public ObservableCollection<string> HistoryLines { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Nazwy maszyn (dla ComboBoxa "Pokaż ekran"), Id → Nazwa.</summary>
    public ObservableCollection<MachinePickerItem> MachinePicker { get; } = new();

    [ObservableProperty]
    private string _roleText = "";

    [ObservableProperty]
    private bool _isRoleWarning;

    [ObservableProperty]
    private string _peersWarning = "";

    /// <summary>
    /// Zdanie pod listą „Maszyny bez pracy” o zasięgu „✕ Skasuj” (patrz
    /// <see cref="Services.AppOrchestrator.DismissScopeNote"/>) — bez grupy potwierdzenie zostaje na tym komputerze.
    /// </summary>
    [ObservableProperty]
    private string _dismissScopeNote = "";

    /// <summary>Czy <see cref="DismissScopeNote"/> jest ostrzeżeniem (skasowanie może nie dojść do pozostałych komputerów).</summary>
    [ObservableProperty]
    private bool _isDismissScopeWarning;

    [ObservableProperty]
    private MachinePickerItem? _selectedMachine;

    public string VersionText { get; }
    public string ModeText { get; }
    public string DataDirText { get; }
    [ObservableProperty]
    private string _ocrText = "";
    [ObservableProperty]
    private bool _isOcrWarning;

    /// <summary>Odświeża wszystkie kolekcje/pola na podstawie bieżącego stanu orkiestratora. Wołane na wątku UI.</summary>
    public void Refresh(DateTimeOffset now)
    {
        ClusterRuntime cluster = _orchestrator.Cluster;
        OnPropertyChanged(nameof(IsClusterEnabled));

        OcrText = _orchestrator.Ocr.IsAvailable ? $"Język OCR: {_orchestrator.Ocr.LanguageTag}" : "OCR niedostępny — brak kontroli HMI i postoju; kolor nadal może wywołać alarm";
        IsOcrWarning = !_orchestrator.Ocr.IsAvailable;
        Captures.Refresh();
        if (!_orchestrator.Supervisor.MonitoringEnabled)
        {
            RoleText = "MONITOROWANIE WYŁĄCZONE — lokalne testy okien i dźwięku są dostępne";
            IsRoleWarning = true;
        }
        else if (cluster.IsWatcher && !_orchestrator.Supervisor.IsRunning)
        {
            RoleText = _orchestrator.Supervisor.AnyEnabledMachineIsReadyToConnect()
                ? "MONITOR ZATRZYMANY — brak obserwacji maszyn"
                : "BRAK HASEŁ VNC — zapisz hasła w Ustawieniach; maszyny nie są obserwowane";
            IsRoleWarning = true;
        }
        else if (cluster.Failure is { } failure)
        {
            RoleText = failure; IsRoleWarning = true;
        }
        else if (cluster.InterruptedGeneration is not null)
        {
            RoleText = "Niedokończona zmiana grupy — działa poprzednia konfiguracja"; IsRoleWarning = true;
        }
        else if (IsClusterEnabled && !cluster.StateFresh && !cluster.IsWatcher)
        {
            RoleText = "Brak aktualnych danych grupy — znane alarmy pozostają aktywne"; IsRoleWarning = true;
        }
        else if (!IsClusterEnabled)
        {
            RoleText = "Ten komputer pilnuje maszyn (tryb jednego komputera — bez komunikacji z innymi komputerami)";
            IsRoleWarning = false;
        }
        else if (cluster.NoWatcher)
        {
            RoleText = "BRAK OBSERWATORA — żaden komputer aktualnie nie pilnuje maszyn";
            IsRoleWarning = true;
        }
        else if (cluster.IsWatcher)
        {
            RoleText = "Ten komputer: Obserwator";
            IsRoleWarning = false;
        }
        else
        {
            RoleText = $"Ten komputer: tylko alerty — obserwatorem jest {cluster.WatcherHost}";
            IsRoleWarning = false;
        }

        IReadOnlyList<MachineStatusSnapshot> machines = cluster.IsWatcher
            ? _orchestrator.Supervisor.Snapshots
            : cluster.RemoteMachines;

        SyncMachines(Machines, machines.Select(m => new MachineRowViewModel(m)));

        var aliveTimeout = TimeSpan.FromMilliseconds(_orchestrator.Cluster.Configuration.PeerTimeoutMs);
        var peers = cluster.Peers;
        SyncPeers(Peers, peers
            .OrderBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PeerRowViewModel(p, now, aliveTimeout,
                string.Equals(p.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase), cluster.Version)));
        PeersWarning = IsClusterEnabled
            ? (GroupPeerStatus.Summarize(peers, cluster.Version, now, aliveTimeout)
                with { Watchers = _orchestrator.ConfirmedWatcherHosts }).Warning
            : "";

        // Alerty postoju ("dołóż prace") i "włącz okno podglądu" mają własne GroupBoxy — tu tylko błędy/brak łączności.
        var activeItems = _orchestrator.VisibleAlerts
            .Where(a => a.Status != AlertStatus.Closed && a.Kind is not (AlertKind.MachineIdle or AlertKind.WrongScreen))
            .Select(a => new AlertItemViewModel(a, _acknowledge, _orchestrator.Help.For)).ToArray();
        SyncActiveAlerts(ActiveAlerts, activeItems);

        // Zasięg „✕ Skasuj”: czy potwierdzenie zgaśnie także na pozostałych komputerach grupy, czy zostanie tutaj.
        DismissScopeNote = _orchestrator.DismissScopeNote;
        IsDismissScopeWarning = _orchestrator.IsDismissScopeWarning;
        SyncIdleAlerts(IdleAlerts, _orchestrator.IdleAlerts
            .Select(a => new IdleAlertItemViewModel(a, _dismissIdle, _orchestrator.DismissButtonHint)));
        SyncScreenAlerts(WrongScreenAlerts, _orchestrator.WrongScreenAlerts.Select(a => new ScreenAlertItemViewModel(a, _dismissScreen)));

        SyncLines(HistoryLines, _orchestrator.RecentHistory.Take(50)
            .Select(h => $"{(h.ClosedAt ?? h.LastSeenAt).ToLocalTime():HH:mm:ss}  {h.MachineName} — {h.ShortText}"));
        SyncLines(LogLines, _logSink.Lines.TakeLast(50));

        if (MachinePicker.Count != machines.Count)
        {
            MachinePicker.Clear();
            foreach (MachineStatusSnapshot m in machines)
            {
                MachinePicker.Add(new MachinePickerItem(m.MachineId, m.Name));
            }

            SelectedMachine ??= MachinePicker.FirstOrDefault();
        }
    }

    internal static void SyncMachines(ObservableCollection<MachineRowViewModel> target, IEnumerable<MachineRowViewModel> rows) =>
        CollectionSync.SyncByKey(target, rows, r => r.MachineId, (row, fresh) => { row.CopyFrom(fresh); return row; });

    internal static void SyncPeers(ObservableCollection<PeerRowViewModel> target, IEnumerable<PeerRowViewModel> rows) =>
        CollectionSync.SyncByKey(target, rows, r => r.Host, (row, fresh) => { row.CopyFrom(fresh); return row; });

    // Alerty zostają tymi samymi obiektami, dopóki ich treść się nie zmieni (rozwinięta pomoc „Jak rozwiązać ten
    // problem?” nie zwija się co sekundę); zmieniony alert jest podmieniany w swoim miejscu.
    internal static void SyncActiveAlerts(ObservableCollection<AlertItemViewModel> target, IEnumerable<AlertItemViewModel> items) =>
        CollectionSync.SyncByKey(target, items, a => a.Key, (item, fresh) =>
            item.Line1 == fresh.Line1 && item.Line2 == fresh.Line2 && item.StatusText == fresh.StatusText
            && item.CaptureFile == fresh.CaptureFile && item.Solution == fresh.Solution ? item : fresh);

    internal static void SyncIdleAlerts(ObservableCollection<IdleAlertItemViewModel> target, IEnumerable<IdleAlertItemViewModel> items) =>
        CollectionSync.SyncByKey(target, items, a => a.Key, (item, fresh) =>
            item.Line1 == fresh.Line1 && item.DismissHint == fresh.DismissHint ? item : fresh);

    internal static void SyncScreenAlerts(ObservableCollection<ScreenAlertItemViewModel> target, IEnumerable<ScreenAlertItemViewModel> items) =>
        CollectionSync.SyncByKey(target, items, a => a.Key, (item, fresh) =>
            item.Line1 == fresh.Line1 && item.Sound == fresh.Sound ? item : fresh);

    internal static void SyncLines(ObservableCollection<string> target, IEnumerable<string> lines) => CollectionSync.SyncLines(target, lines);

    /// <summary>Wpis w ComboBoxie wyboru maszyny do podglądu ekranu.</summary>
    public sealed record MachinePickerItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
