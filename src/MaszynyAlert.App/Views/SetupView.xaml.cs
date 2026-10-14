using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Views;

public partial class SetupView : System.Windows.Controls.UserControl
{
    private readonly SetupWindowViewModel _viewModel;
    private readonly AppConfig _cfg;
    private readonly ISecretStore _secrets;
    private readonly AppOrchestrator? _orchestrator;
    private readonly bool _includeSecrets;
    private readonly ISettingsChangeConfirmation _confirmation;
    private readonly IUserMessages _messages;
    private readonly IAutostartSettings _autostart;
    private readonly Dictionary<string, PasswordBox> _machinePasswordBoxes = new(StringComparer.Ordinal);

    /// <summary>Kliknięto „Zapisz” (po udanym zapisie) albo „Anuluj” — okno hostujące ma się schować/zamknąć.</summary>
    public event Action? Finished;

    /// <summary>Bieżąca konfiguracja: z orkiestratora (zmienia się po zastosowaniu ustawień grupy), a bez niego wstrzyknięta kopia (pomocnik UAC).</summary>
    private AppConfig Cfg => _orchestrator?.Config ?? _cfg;

    /// <summary>
    /// Czy zapis ma iść przez pomocnika z UAC: wersja zainstalowana otwarta z Panelu administracyjnego.
    /// Wersja portable i osobny edytor <c>--setup</c> (bez orkiestratora) zapisują bezpośrednio.
    /// </summary>
    private bool UseElevatedHelper => AppPaths.IsInstalled && _orchestrator is not null;

    public SetupView(SetupWindowViewModel viewModel, AppConfig cfg, ISecretStore secrets, AppOrchestrator? orchestrator,
        bool includeSecrets, ISettingsChangeConfirmation confirmation, IUserMessages? messages = null, IAutostartSettings? autostart = null)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _orchestrator = orchestrator;
        _includeSecrets = includeSecrets;
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _messages = messages ?? DesktopUserMessages.Instance;
        _autostart = autostart ?? WindowsAutostartSettings.Instance;
        DataContext = _viewModel;

        if (!_includeSecrets)
        {
            // Usunięte z drzewa, nie tylko zwinięte: schowanej kontrolki nie znajdzie automatyzacja UI ani nie
            // odsłoni jej przyszła zmiana widoczności.
            NetworkKeyGroup.Visibility = Visibility.Collapsed;
            UpdatesGroup.Visibility = Visibility.Collapsed;
            SectionsPanel.Children.Remove(NetworkKeyGroup);
            SectionsPanel.Children.Remove(UpdatesGroup);
            SecretsElsewhereText.Visibility = Visibility.Visible;
        }

        BuildMachinePasswordRows();
        LoadCurrentValues();
        UpdatePasswordPanelsEnabled();
    }

    /// <summary>Czy pole klucza sieciowego jest widoczne (tryb jednego komputera je chowa) — sprawdzane w teście startu.</summary>
    public bool NetworkKeyGroupVisible => _includeSecrets && NetworkKeyGroup.Visibility == Visibility.Visible;

    /// <summary>Odświeża pola przed pokazaniem (np. gdy klucz/priorytet zmienił się od ostatniego otwarcia) — checkbox autostartu pokazuje FAKTYCZNY stan wpisu.</summary>
    public void LoadCurrentValues() => LoadCurrentValues(defaultAutostartOn: false);

    public void LoadCurrentValues(bool defaultAutostartOn)
    {
        _viewModel.NetworkKeyBase64 = string.Empty;
        NetworkKeyState.Text = !_includeSecrets ? string.Empty
            : string.IsNullOrEmpty(_secrets.Get(SecretKeys.ClusterHmacKey))
                ? "Klucz sieciowy nie jest zapisany na tym komputerze."
                : "✓ Klucz sieciowy jest zapisany na tym komputerze. Puste pole zostawia zapisany klucz; „Generuj” tworzy nowy do skopiowania na pozostałe komputery.";
        StatusText.Text = string.Empty;
        // Tryb jednego komputera (cluster.enabled = false w config.json): klucz sieciowy nie jest do niczego
        // potrzebny, więc nie pokazujemy pola, żeby nie sugerować, że trzeba coś tu wpisać.
        NetworkKeyGroup.Visibility = _includeSecrets && Cfg.Cluster.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _viewModel.AutostartEnabled = defaultAutostartOn || _autostart.IsEnabled(AppPaths.IsInstalled);

        foreach (MachineConfig m in Cfg.Machines)
        {
            // Hasła nigdy nie są wczytywane z powrotem do pól (bezpieczeństwo) — tylko czyścimy pola przy ponownym otwarciu.
            if (_machinePasswordBoxes.TryGetValue(m.Id, out PasswordBox? box))
            {
                box.Password = string.Empty;
            }
        }

        SharedPasswordBox.Password = string.Empty;
        CurrentAdminPassword.Clear();
        NewAdminPassword.Clear();
        ConfirmAdminPassword.Clear();
        PriorityPanel.Visibility = Cfg.Cluster.Enabled ? Visibility.Visible : Visibility.Collapsed;
        AppConfig fileConfig = File.Exists(AppPaths.ConfigPath) ? ConfigLoader.LoadAppConfig(AppPaths.ConfigPath) : Cfg;
        _viewModel.PriorityText = fileConfig.Cluster.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _viewModel.WindowsNotificationsEnabled = fileConfig.Ui.WindowsNotificationsEnabled;
        UpdatesSecretBox.Password = string.Empty;
        if (!_includeSecrets)
        {
            _viewModel.UpdatesAccessClientId = string.Empty;
            UpdatesSecretState.Text = string.Empty;
            return;
        }
        _viewModel.UpdatesAccessClientId = fileConfig.Updates.AccessClientId ?? string.Empty;
        UpdatesSecretState.Text = string.IsNullOrEmpty(_secrets.Get(SecretKeys.UpdatesAccessClientSecret))
            ? "Sekret nie jest zapisany — bez niego katalog wydań odpowie prośbą o logowanie i program nie sprawdzi aktualizacji."
            : "✓ Sekret jest zapisany na tym komputerze. Puste pole zostawia zapisany sekret.";
    }

    private void BuildMachinePasswordRows()
    {
        MachinePasswordsPanel.Children.Clear();
        _machinePasswordBoxes.Clear();

        foreach (MachineConfig m in Cfg.Machines)
        {
            bool alreadySaved = _secrets.Get(SecretKeys.VncPassword(m.Id)) is not null;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = $"{m.Name} ({m.Host}:{m.Port})",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);

            var box = new PasswordBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(box, 1);

            var savedMark = new TextBlock
            {
                Text = alreadySaved ? "✓ zapisane" : string.Empty,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            savedMark.SetResourceReference(TextBlock.ForegroundProperty, "OkGreenBrush");
            Grid.SetColumn(savedMark, 2);

            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(savedMark);

            MachinePasswordsPanel.Children.Add(row);
            _machinePasswordBoxes[m.Id] = box;
        }
    }

    private void SamePasswordCheckBox_CheckedChanged(object sender, RoutedEventArgs e) => UpdatePasswordPanelsEnabled();

    private void UpdatePasswordPanelsEnabled()
    {
        bool same = _viewModel.SamePasswordForAllMachines;
        SharedPasswordPanel.IsEnabled = same;
        MachinePasswordsPanel.IsEnabled = !same;
        MachinePasswordsPanel.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GenerateKeyButton_Click(object sender, RoutedEventArgs e) =>
        _viewModel.NetworkKeyBase64 = Convert.ToBase64String(HmacSigner.GenerateKey());

    private void CopyKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.NetworkKeyBase64))
        {
            System.Windows.Clipboard.SetText(_viewModel.NetworkKeyBase64);
        }
    }

    private void PasteKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                _viewModel.NetworkKeyBase64 = System.Windows.Clipboard.GetText().Trim();
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            // schowek chwilowo zajęty przez inny program — nic nie robimy
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Security.Cryptography.CryptographicException)
        {
            _messages.Show(
                "Nie udało się zapisać wszystkich ustawień. Plik haseł może być uszkodzony, zajęty albo niedostępny do zapisu.\n\n" +
                "Wcześniej zapisane zmiany pozostają zachowane. Okno pozostaje otwarte; wpisane hasła nie zostały wyczyszczone.",
                "VNC Alert — błąd zapisu", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveSettingsAsync()
    {
        string? newHash = null;
        if (NewAdminPassword.Password.Length > 0 || ConfirmAdminPassword.Password.Length > 0)
        {
            if (NewAdminPassword.Password.Length < 10 || NewAdminPassword.Password != ConfirmAdminPassword.Password)
            {
                _messages.Show("Nowe hasło musi mieć co najmniej 10 znaków i być identyczne w obu polach.", "VNC Alert");
                return;
            }
            string? oldHash = _secrets.Get(AdminPassword.SecretKey);
            if (oldHash is not null && !AdminPassword.Verify(CurrentAdminPassword.Password, oldHash))
            {
                _messages.Show("Obecne hasło panelu jest nieprawidłowe.", "VNC Alert");
                return;
            }
            newHash = AdminPassword.Hash(NewAdminPassword.Password);
        }

        string? newKey = null;
        string entered = _includeSecrets ? _viewModel.NetworkKeyBase64.Trim() : string.Empty;
        if (!string.IsNullOrEmpty(entered) && !string.Equals(entered, _secrets.Get(SecretKeys.ClusterHmacKey), StringComparison.Ordinal))
        {
            try
            {
                _ = HmacSigner.FromBase64(entered);
            }
            catch (ArgumentException)
            {
                _messages.Show(
                    "Wpisany klucz sieciowy jest nieprawidłowy — musi to być tekst Base64 wygenerowany przyciskiem \"Generuj\" (lub skopiowany z innego komputera).",
                    "VNC Alert — błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            newKey = entered;
        }

        AppConfig latest;
        try
        {
            latest = File.Exists(AppPaths.ConfigPath) ? ConfigLoader.LoadAppConfig(AppPaths.ConfigPath) : Cfg;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            _messages.Show($"Nie udało się odczytać konfiguracji. {ex.Message}",
                "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        int priority = int.TryParse(_viewModel.PriorityText, out int enteredPriority) ? enteredPriority : latest.Cluster.Priority;
        bool priorityChanged = priority != latest.Cluster.Priority;

        string enteredUpdatesId = _viewModel.UpdatesAccessClientId.Trim();
        string? updatesId = !_includeSecrets
            || string.Equals(enteredUpdatesId, latest.Updates.AccessClientId ?? string.Empty, StringComparison.Ordinal)
            ? null : enteredUpdatesId;
        string? updatesSecret = _includeSecrets && UpdatesSecretBox.Password.Trim() is { Length: > 0 } secret ? secret : null;
        if (updatesSecret is not null && (updatesSecret.Length > 1000 || updatesSecret.Any(char.IsControl)))
        {
            _messages.Show("Sekret tokenu dostępu ma niedozwolone znaki albo jest za długi — wklej go jeszcze raz z Zero Trust.",
                "VNC Alert — błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (updatesId is not null)
        {
            try
            {
                ConfigLoader.Validate(latest with { Updates = WithAccessClientId(latest.Updates, updatesId) });
            }
            catch (ConfigException ex)
            {
                _messages.Show(ex.Message, "VNC Alert — błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var vncPasswords = CollectVncPasswords();
        IReadOnlyList<SettingChange> changes = SetupChanges.Build(new SetupChangeInput
        {
            SharedVncPasswordEntered = _viewModel.SamePasswordForAllMachines && vncPasswords.Count > 0,
            VncPasswordMachines = _viewModel.SamePasswordForAllMachines ? []
                : vncPasswords.Select(p => Cfg.Machines.FirstOrDefault(m => m.Id == p.MachineId)?.Name ?? p.MachineId).ToArray(),
            AdminPasswordChanged = newHash is not null,
            NetworkKeyChanged = newKey is not null,
            UpdatesSecretChanged = updatesSecret is not null,
            UpdatesIdBefore = latest.Updates.AccessClientId ?? string.Empty,
            UpdatesIdAfter = updatesId,
            PriorityBefore = latest.Cluster.Priority,
            PriorityAfter = priority,
            NotificationsBefore = latest.Ui.WindowsNotificationsEnabled,
            NotificationsAfter = _viewModel.WindowsNotificationsEnabled,
            AutostartBefore = _autostart.IsEnabled(AppPaths.IsInstalled),
            AutostartAfter = _viewModel.AutostartEnabled,
        });
        if (changes.Count == 0)
        {
            StatusText.Text = "Brak zmian do zapisania.";
            return;
        }
        if (!_confirmation.Confirm(changes))
        {
            StatusText.Text = "Nie zapisano — anulowano. Wpisy zostały w polach.";
            return;
        }

        SaveResult saved = UseElevatedHelper
            ? await SaveThroughHelperAsync(newHash, newKey, priority, updatesId, updatesSecret)
            : SaveDirectly(newHash, newKey, priority, latest, updatesId, updatesSecret);
        if (saved == SaveResult.Failed)
        {
            return;
        }

        if (_orchestrator is not null)
        {
            // Pomocnik zapisał sekrety w SWOIM procesie — tutejszy magazyn trzeba wczytać ponownie.
            if (UseElevatedHelper && _secrets is DpapiSecretStore store)
            {
                store.Reload();
            }

            await _orchestrator.ReloadSecretsAndReconfigureAsync().ConfigureAwait(true);
        }

        var restartNotes = new List<string>();
        if (newKey is not null || priorityChanged)
        {
            restartNotes.Add("Zmiana klucza sieciowego lub priorytetu zadziała po ponownym uruchomieniu programu.");
        }

        if (updatesId is not null)
        {
            // Sekret program czyta przy każdym zapytaniu do katalogu, ale identyfikator jest częścią konfiguracji
            // aktualizacji wczytanej przy starcie (UpdateService).
            restartNotes.Add("Zmiana identyfikatora tokenu dostępu do katalogu wydań zadziała po ponownym uruchomieniu programu.");
        }

        LoadCurrentValues();
        if (saved == SaveResult.AutostartDenied)
        {
            // To nie jest sukces całego zapisu: widok zostaje otwarty, a błąd Ustawień nadal wymaga OK.
            string message = "Brak uprawnień do zmiany autostartu na tym komputerze. Poproś administratora/IT o pomoc.";
            if (changes.Count > 1) message += "\n\nPozostałe zmiany ustawień zostały zapisane.";
            if (restartNotes.Count > 0) message += "\n\n" + string.Join('\n', restartNotes);
            _messages.Show(message, "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _messages.Notify(NotificationKind.Success, SavedTitle, restartNotes.Count == 0 ? null : string.Join('\n', restartNotes));
        Finished?.Invoke();
    }

    /// <summary>Pierwszy wiersz dymka po udanym zapisie ustawień tego komputera.</summary>
    internal const string SavedTitle = "Zapisano ustawienia";

    private enum SaveResult
    {
        Failed,
        Complete,
        AutostartDenied,
    }

    /// <summary>Zapis w procesie, który ma już potrzebne uprawnienia (portable albo elevated <c>--setup</c>).</summary>
    private SaveResult SaveDirectly(string? newHash, string? newKey, int priority, AppConfig latest, string? updatesId, string? updatesSecret)
    {
        foreach ((string machineId, string password) in CollectVncPasswords())
        {
            _secrets.Set(SecretKeys.VncPassword(machineId), password);
        }

        if (newKey is not null)
        {
            _secrets.Set(SecretKeys.ClusterHmacKey, newKey);
        }

        try
        {
            ConfigLoader.SaveAppConfig(AppPaths.ConfigPath, latest with
            {
                Cluster = latest.Cluster with { Priority = priority },
                Ui = latest.Ui with { WindowsNotificationsEnabled = _viewModel.WindowsNotificationsEnabled },
                Updates = updatesId is null ? latest.Updates : WithAccessClientId(latest.Updates, updatesId),
            });
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            _messages.Show($"Nie udało się zapisać konfiguracji. {ex.Message}",
                "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Error);
            return SaveResult.Failed;
        }

        if (newHash is not null) _secrets.Set(AdminPassword.SecretKey, newHash);
        if (updatesSecret is not null) _secrets.Set(SecretKeys.UpdatesAccessClientSecret, updatesSecret);

        bool currentAutostart = _autostart.IsEnabled(AppPaths.IsInstalled);
        if (_viewModel.AutostartEnabled != currentAutostart)
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Path.Combine(AppPaths.ExeDir, "MaszynyAlert.App.exe");
                if (_viewModel.AutostartEnabled)
                {
                    _autostart.Enable(exePath, AppPaths.IsInstalled);
                }
                else
                {
                    _autostart.Disable(AppPaths.IsInstalled);
                }
            }
            catch (UnauthorizedAccessException)
            {
                _viewModel.AutostartEnabled = _autostart.IsEnabled(AppPaths.IsInstalled);
                return SaveResult.AutostartDenied;
            }
        }

        return SaveResult.Complete;
    }

    /// <summary>
    /// Zapis w wersji zainstalowanej z Panelu administracyjnego: krótkotrwały proces z UAC
    /// (<c>--apply-settings</c>) zapisuje sekrety, <c>config.json</c> i autostart, po czym kończy pracę.
    /// Anulowanie okna UAC (Win32 1223) jest ciche — wpisane wartości zostają w polach.
    /// </summary>
    private async Task<SaveResult> SaveThroughHelperAsync(string? newHash, string? newKey, int priority, string? updatesId, string? updatesSecret)
    {
        Dictionary<string, string> perMachine = _viewModel.SamePasswordForAllMachines
            ? []
            : CollectVncPasswords().ToDictionary(p => p.MachineId, p => p.Password, StringComparer.Ordinal);
        string? shared = _viewModel.SamePasswordForAllMachines && SharedPasswordBox.Password.Length > 0
            ? SharedPasswordBox.Password : null;

        var change = new InstalledSetup.SettingsChange
        {
            VncPasswords = perMachine,
            SharedPassword = shared,
            NetworkKey = newKey,
            AdminPasswordHash = newHash,
            Priority = priority,
            WindowsNotificationsEnabled = _viewModel.WindowsNotificationsEnabled,
            Autostart = _viewModel.AutostartEnabled,
            UpdatesAccessClientId = updatesId,
            UpdatesAccessClientSecret = updatesSecret,
        };

        try
        {
            int exitCode = await InstalledSetup.ApplyElevatedAsync(change);
            if (exitCode == 0) return SaveResult.Complete;

            _messages.Show(
                "Nie udało się zapisać ustawień tego komputera. Dotychczasowe ustawienia pozostają bez zmian.",
                "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Warning);
            return SaveResult.Failed;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Administrator anulował okno UAC — wpisane wartości zostają, nic nie zapisano.
            return SaveResult.Failed;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _messages.Show(
                $"Nie udało się uruchomić zapisu ustawień z uprawnieniami administratora (kod {ex.NativeErrorCode}: {ex.Message}).\n\n" +
                "Możliwe, że blokują to zasady tego komputera. Dotychczasowe ustawienia pozostają bez zmian — zgłoś to administratorowi.",
                "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Warning);
            return SaveResult.Failed;
        }
    }

    /// <summary>Identyfikator tokenu dostępu do katalogu wydań; pusty tekst usuwa go z <c>config.json</c>.</summary>
    private static UpdatesConfig WithAccessClientId(UpdatesConfig updates, string clientId) =>
        updates with { AccessClientId = clientId.Length == 0 ? null : clientId };

    /// <summary>Hasła VNC wpisane w tym otwarciu — tylko niepuste pola (puste = „zostaw dotychczasowe”).</summary>
    private List<(string MachineId, string Password)> CollectVncPasswords()
    {
        var result = new List<(string, string)>();
        if (_viewModel.SamePasswordForAllMachines)
        {
            if (SharedPasswordBox.Password.Length > 0)
            {
                foreach (MachineConfig m in Cfg.Machines)
                {
                    result.Add((m.Id, SharedPasswordBox.Password));
                }
            }

            return result;
        }

        foreach ((string machineId, PasswordBox box) in _machinePasswordBoxes)
        {
            if (box.Password.Length > 0)
            {
                result.Add((machineId, box.Password));
            }
        }

        return result;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // „Anuluj” w Panelu administracyjnym odrzuca wpisy i przywraca to, co jest zapisane.
        LoadCurrentValues();
        Finished?.Invoke();
    }
}
