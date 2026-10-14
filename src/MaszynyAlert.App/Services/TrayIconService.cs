using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const int MaxTooltipLength = 63;

    private readonly AppOrchestrator _orchestrator;
    private readonly ISecretStore _secrets;
    private readonly AlertWindow _alertWindow;
    private readonly StatusWindow _statusWindow;
    private readonly OperatorWindow _operatorWindow;
    private readonly SetupWindow _setupWindow;
    private readonly IdlePopupWindow _idlePopupWindow;
    private readonly ScreenPopupWindow _screenPopupWindow;
    private readonly OverlayWindow _overlayWindow;
    private readonly UpdateWindow _updateWindow;
    private readonly Dispatcher _dispatcher;
    private readonly DiagnosticsService _diagnostics;
    private readonly ThemeService _theme;
    private readonly IUserMessages _messages;
    private readonly CancellationTokenSource _exportCancellation = new();
    private ToolStripItem? _exportMenuItem;
    private bool _exporting;

    private NotifyIcon? _notifyIcon;
    private (Icon? Icon, IntPtr Handle) _greenIcon;
    private (Icon? Icon, IntPtr Handle) _yellowIcon;
    private (Icon? Icon, IntPtr Handle) _redIcon;
    private DispatcherTimer? _refreshTimer;
    private HashSet<AlertKey> _knownRingingKeys = new();
    private bool _disposed;
    private bool _initialized;

    private Action _refreshFromOrchestrator = () => { };

    public TrayIconService(
        AppOrchestrator orchestrator,
        ISecretStore secrets,
        AlertWindow alertWindow,
        StatusWindow statusWindow,
        OperatorWindow operatorWindow,
        SetupWindow setupWindow,
        IdlePopupWindow idlePopupWindow,
        ScreenPopupWindow screenPopupWindow,
        OverlayWindow overlayWindow,
        DiagnosticsService diagnostics,
        ThemeService theme,
        UpdateWindow updateWindow,
        IUserMessages messages)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _alertWindow = alertWindow ?? throw new ArgumentNullException(nameof(alertWindow));
        _statusWindow = statusWindow ?? throw new ArgumentNullException(nameof(statusWindow));
        _operatorWindow = operatorWindow;
        _operatorWindow.OpenAdmin += ShowAdmin;
        _setupWindow = setupWindow ?? throw new ArgumentNullException(nameof(setupWindow));
        _idlePopupWindow = idlePopupWindow ?? throw new ArgumentNullException(nameof(idlePopupWindow));
        _screenPopupWindow = screenPopupWindow ?? throw new ArgumentNullException(nameof(screenPopupWindow));
        _overlayWindow = overlayWindow ?? throw new ArgumentNullException(nameof(overlayWindow));
        _overlayWindow.MachineChosen += machineId => _operatorWindow.ShowForMachine(machineId);
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _updateWindow = updateWindow ?? throw new ArgumentNullException(nameof(updateWindow));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _refreshFromOrchestrator = () => _dispatcher.BeginInvoke(RefreshIcon);
    }

    /// <summary>Tworzy ikonę i menu, uruchamia odświeżanie. Wołane raz, z wątku UI, po zbudowaniu okien (<see cref="App.OnStartup"/>).</summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        _greenIcon = CreateCircleIcon(Color.FromArgb(46, 125, 50));
        _yellowIcon = CreateCircleIcon(Color.FromArgb(242, 192, 55));
        _redIcon = CreateCircleIcon(Color.FromArgb(176, 0, 32));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Podgląd", null, (_, _) => ShowStatus());
        menu.Items.Add("Panel administracyjny", null, (_, _) => ShowAdmin());
        menu.Items.Add("Maszyny bez pracy – dołóż prace", null, (_, _) => _idlePopupWindow.ShowPinned());
        menu.Items.Add("Ekran HMI – włącz okno podglądu stanu maszyny", null, (_, _) => _screenPopupWindow.ShowPinned());
        menu.Items.Add(BuildOverlayMenu());
        menu.Items.Add(BuildThemeMenuItem());
        menu.Items.Add("Aktualizacja programu…", null, (_, _) => _updateWindow.ShowFromMenu());
        menu.Items.Add("Test alarmu (ten komputer)", null, (_, _) => _orchestrator.TestAlarm());
        menu.Items.Add("Test powiadomienia o postoju (ten komputer)", null, (_, _) => _orchestrator.TestIdlePopup());
        menu.Items.Add("Test powiadomienia o ekranie HMI (ten komputer)", null, (_, _) => _orchestrator.TestWrongScreenPopup());
        _exportMenuItem = menu.Items.Add("Eksportuj diagnostykę…", null, async (_, _) =>
        { if (AuthenticateAdmin()) await ExportDiagnosticsAsync(); });

        _notifyIcon = new NotifyIcon
        {
            Icon = _greenIcon.Icon,
            Visible = true,
            ContextMenuStrip = menu,
            Text = "VNC Alert",
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatus();

        _orchestrator.AlertsChanged += _refreshFromOrchestrator;
        _orchestrator.RoleChanged += _refreshFromOrchestrator;
        _orchestrator.MachinesChanged += _refreshFromOrchestrator;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshIcon();
        _refreshTimer.Start();

        RefreshIcon();

        // Nakładka statusu pojawia się na końcu — dopiero tu okna z DI są już zbudowane, a jej stan
        // (włączona/wyłączona) pochodzi z wyboru operatora zapisanego w profilu użytkownika.
        _overlayWindow.ApplyVisibility();
    }

    private ToolStripMenuItem BuildOverlayMenu()
    {
        var shown = new ToolStripMenuItem("Pokaż nakładkę") { CheckOnClick = true };
        shown.Click += (_, _) => _overlayWindow.SetEnabled(shown.Checked);
        var clickThrough = new ToolStripMenuItem("Przenikanie kliknięć (nakładka nie łapie myszy)") { CheckOnClick = true };
        clickThrough.Click += (_, _) => _overlayWindow.SetClickThrough(clickThrough.Checked);

        var layoutMenu = new ToolStripMenuItem("Układ kafelków");
        var layoutItems = new List<(OverlayLayout Layout, ToolStripMenuItem Item)>();
        foreach (OverlayLayout layout in OverlayLayouts.All)
        {
            var item = new ToolStripMenuItem(OverlayLayouts.Label(layout));
            item.Click += (_, _) => _overlayWindow.SetLayout(layout);
            layoutMenu.DropDownItems.Add(item);
            layoutItems.Add((layout, item));
        }

        var overlayMenu = new ToolStripMenuItem("Nakładka statusu");
        overlayMenu.DropDownItems.Add(shown);
        overlayMenu.DropDownItems.Add(clickThrough);
        overlayMenu.DropDownItems.Add(layoutMenu);
        overlayMenu.DropDownOpening += (_, _) =>
        {
            shown.Checked = _overlayWindow.OverlayEnabled;
            clickThrough.Checked = _overlayWindow.ClickThrough;
            OverlayLayout current = _overlayWindow.Layout;
            foreach ((OverlayLayout layout, ToolStripMenuItem item) in layoutItems)
            {
                item.Checked = layout == current;
            }
        };
        return overlayMenu;
    }

    /// <summary>
    /// Pozycja „Tryb ciemny” z ptaszkiem — ten sam przełącznik co przycisk w Podglądzie; wybór jest zapisywany
    /// w profilu użytkownika (<see cref="ThemeService"/>). Ptaszek idzie za <see cref="ThemeService.ThemeChanged"/>,
    /// bo motyw można przełączyć także z Podglądu.
    /// </summary>
    private ToolStripMenuItem BuildThemeMenuItem()
    {
        var item = new ToolStripMenuItem("Tryb ciemny") { CheckOnClick = true, Checked = _theme.IsDark };
        item.Click += (_, _) => _theme.Set(item.Checked ? AppTheme.Dark : AppTheme.Light);
        _theme.ThemeChanged += theme => item.Checked = theme == AppTheme.Dark;
        return item;
    }

    public bool NeedsInitialSetup()
    {
        bool noKey = MissingLegacyNetworkKey();
        IEnumerable<MachineConfig> relevant = _orchestrator.Config.Machines.Any(m => m.Enabled) ? _orchestrator.Config.Machines.Where(m => m.Enabled) : _orchestrator.Config.Machines;
        bool noPasswords = !relevant.Any(m => _secrets.Get(SecretKeys.VncPassword(m.Id)) is not null);
        return noKey || noPasswords;
    }

    private bool MissingLegacyNetworkKey() =>
        _orchestrator.Config.Cluster.Enabled && _orchestrator.Config.Cluster.Group is null
        && string.IsNullOrEmpty(_secrets.Get(SecretKeys.ClusterHmacKey));

    /// <summary>Pokazuje (i aktywuje) okno statusu — dwuklik na ikonę albo pozycja menu "Status".</summary>
    public void ShowStatus()
    {
        _operatorWindow.Show();
        if (_operatorWindow.WindowState == System.Windows.WindowState.Minimized)
            _operatorWindow.WindowState = System.Windows.WindowState.Normal;
        _operatorWindow.Activate();
    }

    private bool AuthenticateAdmin() => new AdminLoginWindow(_secrets).ShowDialog() == true;

    private void ShowAdmin()
    {
        if (_statusWindow.IsVisible) { _statusWindow.Activate(); return; }
        if (_secrets.Get(AdminPassword.SecretKey) is null)
        {
            System.Windows.MessageBox.Show(
                "Panel administracyjny wymaga hasła. Ustaw je teraz w oknie Ustawień.",
                "VNC Alert", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            ShowSetup();
            return;
        }
        if (!AuthenticateAdmin()) return;
        _statusWindow.Show();
        _statusWindow.Activate();
    }

    private bool _editingInstalledSettings;

    public async void ShowSetup(bool defaultAutostartOn = false)
    {
        if (AppPaths.IsInstalled)
        {
            if (_editingInstalledSettings) return;
            _editingInstalledSettings = true;
            try
            {
                if (await InstalledSetup.LaunchAsync() == 0 && !_disposed)
                {
                    if (_secrets is DpapiSecretStore store) store.Reload();
                    await _orchestrator.ReloadSecretsAndReconfigureAsync();
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // Użytkownik anulował UAC. Monitoring pozostaje uruchomiony.
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Nie udało się zastosować ustawień.\n\n" + ex.Message,
                    "VNC Alert", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            finally { _editingInstalledSettings = false; }
            return;
        }
        _setupWindow.LoadCurrentValues(defaultAutostartOn);
        _setupWindow.Show();
        _setupWindow.Activate();
    }

    private void OpenLogsFolder()
    {
        string logsDir = AppPaths.Resolve(_orchestrator.Config.Paths.LogsDir);
        try
        {
            Directory.CreateDirectory(logsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{logsDir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            System.Windows.MessageBox.Show(
                $"Nie udało się otworzyć folderu logów:\n{logsDir}\n\n{ex.Message}",
                "VNC Alert", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    internal static void NotifyDiagnosticsSaved(IUserMessages messages, DiagnosticsExportResult result)
    {
        string file = $"{Path.GetFileName(result.ArchivePath)} · pliki: {result.FilesIncluded} · zapisano lokalnie, nie wysłano.";
        if (result.Warnings.Count == 0)
        {
            messages.Notify(NotificationKind.Success, "Zapisano diagnostykę", file, result.ArchivePath);
            return;
        }

        messages.Notify(NotificationKind.Warning, $"Zapisano diagnostykę z ostrzeżeniami ({result.Warnings.Count})",
            "Część materiałów mogła zostać pominięta — lista ostrzeżeń jest w manifest.json w archiwum.\n" + file, result.ArchivePath);
    }

    private async Task ExportDiagnosticsAsync()
    {
        if (_exporting || _disposed) return;
        _exporting = true;
        if (_exportMenuItem is not null) _exportMenuItem.Enabled = false;
        CancellationToken cancellationToken = _exportCancellation.Token;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz lokalnie ZIP: logi, historia i zrzuty ekranu",
                Filter = "Archiwum diagnostyczne (*.zip)|*.zip",
                DefaultExt = ".zip", AddExtension = true, OverwritePrompt = false,
                FileName = _diagnostics.SuggestedFileName,
            };
            if (dialog.ShowDialog() != true || _disposed) return;

            // Sprawdzenie ścieżek i utworzenie nowego pliku odbywa się w tle. Eksporter odrzuca
            // istniejące archiwa i ścieżki sieciowe; odczyt dysku nie blokuje wątku interfejsu.
            DiagnosticsExportRequest request = _diagnostics.CaptureRequest();
            DiagnosticsExportResult result = await _diagnostics.ExportAsync(dialog.FileName, request, cancellationToken);
            if (_disposed) return;
            NotifyDiagnosticsSaved(_messages, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Zamknięcie aplikacji anuluje eksport; eksporter sprząta niedokończony zapis.
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _messages.Notify(NotificationKind.Error, "Nie udało się zapisać diagnostyki",
                    $"{ex.Message} Wybierz inną nazwę lub folder i ponów eksport.");
        }
        finally
        {
            _exporting = false;
            if (!_disposed && _exportMenuItem is not null) _exportMenuItem.Enabled = true;
            if (_disposed) _exportCancellation.Dispose();
        }
    }

    private void RefreshIcon()
    {
        if (_disposed || _notifyIcon is null)
        {
            return;
        }

        IReadOnlyList<Alert> visible = _orchestrator.VisibleAlerts;
        bool anyRinging = visible.Any(a => a.Sound && a.Status is AlertStatus.Active or AlertStatus.Escalated);

        bool anyMachineProblem = _orchestrator.Cluster.IsWatcher
            && _orchestrator.Supervisor.Snapshots.Any(m => m.State != MachineConnectionState.Connected && m.State != MachineConnectionState.Disabled
                || m.Source == "plik" || !m.OcrAvailable);
        // HMI którejś maszyny pokazuje inny ekran niż główny — program jest ślepy na jej błędy (żółta ikona
        // od razu, jeszcze zanim po WrongScreenAlertAfterMs pojawi się pop-up).
        bool anyWrongScreen = _orchestrator.Cluster.IsWatcher
            && _orchestrator.Supervisor.Snapshots.Any(m => m.ScreenObservationUnavailable);
        bool noNetworkKey = MissingLegacyNetworkKey();
        bool monitorStopped = _orchestrator.Config.Enabled && _orchestrator.Cluster.IsWatcher && !_orchestrator.Supervisor.IsRunning;
        bool anyWarning = monitorStopped || !_orchestrator.Config.Enabled || _orchestrator.Cluster.NoWatcher || anyMachineProblem || anyWrongScreen || !_orchestrator.Ocr.IsAvailable || noNetworkKey;

        _notifyIcon.Icon = anyRinging ? _redIcon.Icon : anyWarning ? _yellowIcon.Icon : _greenIcon.Icon;

        string role = !_orchestrator.Config.Enabled ? "monitorowanie wyłączone" : !_orchestrator.Config.Cluster.Enabled ? "jeden komputer" : _orchestrator.Cluster.IsWatcher ? "Obserwator" : "UI";

        // "alerty: N" liczy TYLKO błędy/brak łączności — alerty "dołóż prace" i
        // "włącz okno podglądu" mają osobne liczniki (dopisane tylko gdy > 0), żeby nie sugerować awarii maszyny.
        int activeCount = visible.Count(a => a.Kind is not (AlertKind.MachineIdle or AlertKind.WrongScreen) && a.Status != AlertStatus.Closed);
        int idleCount = _orchestrator.IdleAlerts.Count;
        int screenCount = _orchestrator.WrongScreenAlerts.Count;

        _notifyIcon.Text = BuildTooltip(role, activeCount, idleCount, screenCount);

        NotifyAboutNewAlerts(visible);
    }

    internal static string BuildTooltip(string role, int activeCount, int idleCount, int screenCount)
    {
        string tooltip = $"VNC Alert – {role} – alerty: {activeCount}";
        if (tooltip.Length > MaxTooltipLength) return tooltip[..MaxTooltipLength];
        foreach (string segment in new[]
        {
            idleCount > 0 ? $", stoją: {idleCount}" : null,
            screenCount > 0 ? $", ekran: {screenCount}" : null,
        })
        {
            if (segment is not null && tooltip.Length + segment.Length <= MaxTooltipLength) tooltip += segment;
        }

        return tooltip;
    }

    private void NotifyAboutNewAlerts(IReadOnlyList<Alert> visible)
    {
        // Dymek w zasobniku: dzwoniące błędy ORAZ powiadomienia "włącz okno podglądu stanu maszyny"
        // (domyślnie bez dźwięku — dymek to dodatkowy kanał obok pop-upu, żeby operator nie przegapił, że
        // program nie widzi już błędów tej maszyny).
        Dictionary<AlertKey, Alert> currentRinging = visible
            .Where(a => (a.Sound || a.Kind == AlertKind.WrongScreen) && a.Status is AlertStatus.Active or AlertStatus.Escalated)
            .ToDictionary(a => a.Key);

        foreach ((AlertKey key, Alert alert) in currentRinging)
        {
            if (_orchestrator.WindowsNotificationsEnabled && !_knownRingingKeys.Contains(key))
            {
                string title = alert.Kind == AlertKind.WrongScreen
                    ? "VNC Alert — włącz okno podglądu stanu maszyny"
                    : "VNC Alert — nowy alert";
                _notifyIcon?.ShowBalloonTip(6000, title, alert.DisplayLine(), ToolTipIcon.Warning);
            }
        }

        _knownRingingKeys = currentRinging.Keys.ToHashSet();
    }

    private static (Icon Icon, IntPtr Handle) CreateCircleIcon(Color fill)
    {
        using var bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(Color.FromArgb(255, 60, 60, 60), 1.4f);
            g.FillEllipse(brush, 1.5f, 1.5f, 12f, 12f);
            g.DrawEllipse(pen, 1.5f, 1.5f, 12f, 12f);
        }

        IntPtr handle = bmp.GetHicon();
        Icon icon = Icon.FromHandle(handle);
        return (icon, handle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _orchestrator.AlertsChanged -= _refreshFromOrchestrator;
        _orchestrator.RoleChanged -= _refreshFromOrchestrator;
        _orchestrator.MachinesChanged -= _refreshFromOrchestrator;
        _exportCancellation.Cancel();
        if (!_exporting) _exportCancellation.Dispose();

        _refreshTimer?.Stop();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _greenIcon.Icon?.Dispose();
        _yellowIcon.Icon?.Dispose();
        _redIcon.Icon?.Dispose();

        if (_greenIcon.Handle != IntPtr.Zero)
        {
            DestroyIcon(_greenIcon.Handle);
        }

        if (_yellowIcon.Handle != IntPtr.Zero)
        {
            DestroyIcon(_yellowIcon.Handle);
        }

        if (_redIcon.Handle != IntPtr.Zero)
        {
            DestroyIcon(_redIcon.Handle);
        }
    }
}
