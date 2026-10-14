using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Tests;

[Collection(SetupViewCollection.Name)]
public sealed class SetupViewSecretsTests : IDisposable
{
    private const string ClientId = "abc123.access";
    private const string ClientSecret = "sekret-tokenu-xyz";
    private static readonly string[] SecretControls =
    [
        "NetworkKeyGroup", "NetworkKeyTextBox", "GenerateKeyButton", "CopyKeyButton", "PasteKeyButton",
        "UpdatesGroup", "UpdatesClientIdTextBox", "UpdatesSecretBox", "UpdatesSecretState", "NetworkKeyState",
    ];

    private readonly string _storedKey = Convert.ToBase64String(HmacSigner.GenerateKey());
    private readonly FakeSecrets _secrets = new();
    private readonly RecordingMessages _messages = new();
    private readonly AppConfig _config;

    public SetupViewSecretsTests()
    {
        if (AppPaths.IsInstalled) throw new InvalidOperationException("Testy SetupView tylko w wersji portable (katalog testów).");
        _config = Config(clusterEnabled: true);
        ConfigLoader.SaveAppConfig(AppPaths.ConfigPath, _config);
        _secrets.Set(SecretKeys.ClusterHmacKey, _storedKey);
        _secrets.Set(SecretKeys.UpdatesAccessClientSecret, ClientSecret);
        _secrets.Changes.Clear();
    }

    private static AppConfig Config(bool clusterEnabled)
    {
        AppConfig example = ConfigLoader.LoadAppConfig(Path.Combine(AppContext.BaseDirectory, "config.example.json"));
        AppConfig cfg = example with
        {
            Cluster = example.Cluster with { Enabled = clusterEnabled, Priority = 100 },
            Updates = example.Updates with { AccessClientId = ClientId },
            Ui = example.Ui with { WindowsNotificationsEnabled = true },
        };
        ConfigLoader.Validate(cfg);
        return cfg;
    }

    [Fact]
    public void WindowWithoutPanelPassword_HasNoKeyNorTokenInTheTreeAndNoValuesInMemory()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var window = WpfTestHost.OffScreen(new SetupWindow(viewModel, _config, _secrets, orchestrator: null, AlwaysConfirm.Instance, _messages));
            try
            {
                window.Show();
                Settle(window);

                string[] tree = Names(window);
                foreach (string name in SecretControls)
                    Assert.True(!tree.Contains(name), name + " jest w drzewie okna Ustawień bez hasła Panelu.");
                Assert.Contains("SecretsElsewhereText", tree);
                Assert.DoesNotContain(VisualText(window), t => t.Contains(_storedKey, StringComparison.Ordinal)
                    || t.Contains(ClientId, StringComparison.Ordinal));

                Assert.Equal("", viewModel.NetworkKeyBase64);
                Assert.Equal("", viewModel.UpdatesAccessClientId);
                Assert.False(window.NetworkKeyGroupVisible);
                var view = (SetupView)window.Content;
                Assert.Equal(Visibility.Visible, view.SecretsElsewhereText.Visibility);
                Assert.Contains("Panelu administracyjnym", view.SecretsElsewhereText.Text, StringComparison.Ordinal);
                Assert.Equal(Visibility.Visible, view.PriorityPanel.Visibility);
                Assert.Equal("", view.NetworkKeyTextBox.Text);
                Assert.Equal("", view.UpdatesSecretBox.Password);
                Assert.Equal("", view.UpdatesSecretState.Text);
                // Ponowne odczytanie przed pokazaniem (ShowSetup) też nie wczytuje zapisanych wartości.
                window.LoadCurrentValues(defaultAutostartOn: true);
                Assert.Equal("", viewModel.NetworkKeyBase64);
                Assert.Equal("", viewModel.UpdatesAccessClientId);
                Assert.Empty(_secrets.Changes);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveFromWindowWithoutPanelPassword_KeepsKeyIdAndSecret(bool somethingWroteIntoTheViewModel)
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: false, AlwaysConfirm.Instance, _messages);
            string otherKey = Convert.ToBase64String(HmacSigner.GenerateKey());

            viewModel.SamePasswordForAllMachines = true;
            view.SharedPasswordBox.Password = "haslo-vnc-1";
            view.NewAdminPassword.Password = "nowe-haslo-panelu";
            view.ConfirmAdminPassword.Password = "nowe-haslo-panelu";
            viewModel.WindowsNotificationsEnabled = false;
            if (somethingWroteIntoTheViewModel)
            {
                viewModel.NetworkKeyBase64 = otherKey;
                viewModel.UpdatesAccessClientId = "";
                view.UpdatesSecretBox.Password = "inny-sekret";
            }

            Save(view);

            Assert.Equal(_storedKey, _secrets.Get(SecretKeys.ClusterHmacKey));
            Assert.Equal(ClientSecret, _secrets.Get(SecretKeys.UpdatesAccessClientSecret));
            Assert.DoesNotContain(_secrets.Changes, c => c.Key is SecretKeys.ClusterHmacKey or SecretKeys.UpdatesAccessClientSecret);
            Assert.Empty(_secrets.Removed);
            AppConfig file = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath);
            Assert.Equal(ClientId, file.Updates.AccessClientId);
            Assert.False(file.Ui.WindowsNotificationsEnabled);
            Assert.Equal(100, file.Cluster.Priority);
            Assert.True(AdminPassword.Verify("nowe-haslo-panelu", _secrets.Get(AdminPassword.SecretKey)));
            Assert.All(_config.Machines, m => Assert.Equal("haslo-vnc-1", _secrets.Get(SecretKeys.VncPassword(m.Id))));
            Assert.Empty(_messages.Shown);
        });
    }

    [Fact]
    public void PanelWithPassword_ShowsAndSavesKeyAndTokenAsBefore()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: true, AlwaysConfirm.Instance, _messages);
            var host = WpfTestHost.OffScreen(new Window { Content = view, Width = 900, Height = 900 });
            try
            {
                host.Show();
                Settle(host);

                string[] tree = Names(host);
                Assert.All(SecretControls, name => Assert.Contains(name, tree));
                Assert.Equal("", viewModel.NetworkKeyBase64);
                Assert.Contains("zapisany", view.NetworkKeyState.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(VisualText(host), text => text.Contains(_storedKey, StringComparison.Ordinal));
                Assert.Equal(ClientId, viewModel.UpdatesAccessClientId);
                Assert.True(view.NetworkKeyGroupVisible);
                Assert.Equal(Visibility.Collapsed, view.SecretsElsewhereText.Visibility);
                Assert.Contains("Sekret jest zapisany", view.UpdatesSecretState.Text, StringComparison.Ordinal);

                // Nowy sekret z Panelu zapisuje się (zmiana klucza albo identyfikatora pokazuje okno komunikatu — poza testem).
                view.UpdatesSecretBox.Password = "nowy-sekret";
                Save(view);
                Assert.Equal("nowy-sekret", _secrets.Get(SecretKeys.UpdatesAccessClientSecret));
                Assert.Equal(_storedKey, _secrets.Get(SecretKeys.ClusterHmacKey));
                Assert.Equal(ClientId, ConfigLoader.LoadAppConfig(AppPaths.ConfigPath).Updates.AccessClientId);
                Assert.Empty(_messages.Shown);
            }
            finally { host.Close(); }
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void PriorityFollowsClusterEnabledInBothModes(bool clusterEnabled, bool includeSecrets)
    {
        WpfTestHost.Run(() =>
        {
            AppConfig cfg = Config(clusterEnabled);
            ConfigLoader.SaveAppConfig(AppPaths.ConfigPath, cfg);
            var view = new SetupView(new SetupWindowViewModel(), cfg, _secrets, orchestrator: null, includeSecrets, AlwaysConfirm.Instance, _messages);

            Assert.Equal(clusterEnabled ? Visibility.Visible : Visibility.Collapsed, view.PriorityPanel.Visibility);
            // Smoke startu (App.OnStartup) wymaga false w trybie jednego komputera; w oknie bez hasła zawsze false.
            Assert.Equal(clusterEnabled && includeSecrets, view.NetworkKeyGroupVisible);
        });
    }

    /// <summary>
    /// Ścieżka pomocnika z UAC (wersja zainstalowana) — wykonawczo nietestowalna bez instalacji. Pilnujemy tekstowo, że
    /// pomocnik dostaje TE SAME wartości co zapis bezpośredni (null przy oknie bez hasła), a pomocnik traktuje null jako
    /// „bez zmian”.
    /// </summary>
    [Fact]
    public void ElevatedHelperPath_GetsTheSameNullsAndTreatsThemAsNoChange()
    {
        string setup = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "SetupView.xaml.cs"));
        Assert.Contains("? await SaveThroughHelperAsync(newHash, newKey, priority, updatesId, updatesSecret)", setup, StringComparison.Ordinal);
        Assert.Contains(": SaveDirectly(newHash, newKey, priority, latest, updatesId, updatesSecret);", setup, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"NetworkKey = newKey,\s+AdminPasswordHash = newHash,"), setup);
        Assert.Matches(new Regex(@"UpdatesAccessClientId = updatesId,\s+UpdatesAccessClientSecret = updatesSecret,"), setup);

        string helper = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Services", "InstalledSetup.cs"));
        Assert.Contains("if (change.NetworkKey is { Length: > 0 } key) secrets.Set(SecretKeys.ClusterHmacKey, key);", helper, StringComparison.Ordinal);
        Assert.Contains("if (change.UpdatesAccessClientSecret is { Length: > 0 } updatesSecret)", helper, StringComparison.Ordinal);
        Assert.Contains("Updates = change.UpdatesAccessClientId is { } clientId", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove(SecretKeys.ClusterHmacKey", helper, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------------


    /// <summary>Anuluj w oknie potwierdzenia: nic nie zapisane (magazyn, config.json), wpisy zostają, okno się nie zamyka.</summary>
    [Fact]
    public void Confirmation_Cancel_SavesNothingAndKeepsTheEntries()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var confirmation = new SettingsChangeConfirmationTests.RecordingConfirmation(false);
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: false, confirmation, _messages);
            bool finished = false;
            view.Finished += () => finished = true;
            byte[] configBefore = File.ReadAllBytes(AppPaths.ConfigPath);

            viewModel.SamePasswordForAllMachines = true;
            view.SharedPasswordBox.Password = "haslo-vnc-1";
            view.NewAdminPassword.Password = "nowe-haslo-panelu";
            view.ConfirmAdminPassword.Password = "nowe-haslo-panelu";
            viewModel.WindowsNotificationsEnabled = false;
            Save(view);

            IReadOnlyList<SettingChange> asked = Assert.Single(confirmation.Asked);
            Assert.Equal(["Hasło VNC (wszystkie maszyny)", "Hasło Panelu administracyjnego", "Pokazuj powiadomienia Windows"], asked.Select(c => c.Name));
            Assert.All(asked.Take(2), c => Assert.True(c.IsSecret));
            Assert.DoesNotContain(asked, c => $"{c.Name}{c.Before}{c.After}{c.BeforeText}{c.AfterText}".Contains("haslo", StringComparison.Ordinal));
            Assert.Empty(_secrets.Changes);
            Assert.Empty(_secrets.Removed);
            Assert.Equal(configBefore, File.ReadAllBytes(AppPaths.ConfigPath));
            Assert.Equal("Nie zapisano — anulowano. Wpisy zostały w polach.", view.StatusText.Text);
            Assert.False(finished);
            Assert.Equal("haslo-vnc-1", view.SharedPasswordBox.Password);
            Assert.Equal("nowe-haslo-panelu", view.NewAdminPassword.Password);
            Assert.False(viewModel.WindowsNotificationsEnabled);
            Assert.Empty(_messages.Shown);
        });
    }

    [Fact]
    public void Confirmation_NoChanges_NoQuestionAndWindowStaysOpen()
    {
        WpfTestHost.Run(() =>
        {
            var confirmation = new SettingsChangeConfirmationTests.RecordingConfirmation(true);
            var view = new SetupView(new SetupWindowViewModel(), _config, _secrets, orchestrator: null, includeSecrets: true, confirmation, _messages);
            bool finished = false;
            view.Finished += () => finished = true;

            Save(view);

            Assert.Empty(confirmation.Asked);
            Assert.Equal("Brak zmian do zapisania.", view.StatusText.Text);
            Assert.False(finished);
            Assert.Empty(_secrets.Changes);
        });
    }

    [Fact]
    public void Confirmation_PerMachinePasswordsAreListedByMachineName()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var confirmation = new SettingsChangeConfirmationTests.RecordingConfirmation(false);
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: false, confirmation, _messages);
            viewModel.SamePasswordForAllMachines = false;
            var boxes = (Dictionary<string, PasswordBox>)typeof(SetupView)
                .GetField("_machinePasswordBoxes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            MachineConfig first = _config.Machines[0], second = _config.Machines[1];
            boxes[first.Id].Password = "p1";
            boxes[second.Id].Password = "p2";

            Save(view);

            Assert.Equal(["Hasło VNC — " + first.Name, "Hasło VNC — " + second.Name], Assert.Single(confirmation.Asked).Select(c => c.Name));
            Assert.Empty(_secrets.Changes);
        });
    }

    [Fact]
    public void Panel_GenerateGivesANewKey_ListedWithoutValue_StoredKeyNeverShown()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var confirmation = new SettingsChangeConfirmationTests.RecordingConfirmation(false);
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: true, confirmation, _messages);

            view.GenerateKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string generated = viewModel.NetworkKeyBase64;
            Assert.NotEqual("", generated);
            Assert.NotEqual(_storedKey, generated);
            _ = HmacSigner.FromBase64(generated);

            view.UpdatesSecretBox.Password = "s3kret-tokenu";
            Save(view);
            IReadOnlyList<SettingChange> asked = Assert.Single(confirmation.Asked);
            Assert.Contains(SettingChange.Secret("Klucz sieciowy"), asked);
            Assert.Contains(SettingChange.Secret("Sekret tokenu dostępu (katalog wydań)"), asked);
            Assert.DoesNotContain(asked, c => $"{c.Before}{c.After}{c.AfterText}".Contains(generated, StringComparison.Ordinal)
                || $"{c.Before}{c.After}{c.AfterText}".Contains("s3kret", StringComparison.Ordinal)
                || $"{c.Before}{c.After}{c.AfterText}".Contains(_storedKey, StringComparison.Ordinal));

            // Zapisany klucz wpisany ręcznie albo puste pole: bez zmiany klucza.
            view.UpdatesSecretBox.Password = "";
            viewModel.NetworkKeyBase64 = _storedKey;
            viewModel.WindowsNotificationsEnabled = false;
            Save(view);
            viewModel.NetworkKeyBase64 = "";
            Save(view);
            Assert.All(confirmation.Asked.Skip(1), list => Assert.Equal(["Pokazuj powiadomienia Windows"], list.Select(c => c.Name)));
            Assert.Equal(_storedKey, _secrets.Get(SecretKeys.ClusterHmacKey));
            Assert.Empty(_secrets.Changes);
        });
    }

    /// <summary>
    /// Priorytet w polu pochodzi z konfiguracji W PAMIĘCI programu, a „Było” i zapis — z config.json. Po zmianie priorytetu
    /// (działa dopiero po restarcie) program ma w pamięci stary; kolejny zapis czegokolwiek innego (np. powiadomień) bez
    /// dotykania priorytetu przywraca stary priorytet w config.json.
    /// Oczekiwane: nietknięty priorytet = bez zmiany.
    /// </summary>
    [Fact]
    public void UntouchedPriority_IsNotRevertedToTheInMemoryValue()
    {
        WpfTestHost.Run(() =>
        {
            ConfigLoader.SaveAppConfig(AppPaths.ConfigPath, _config with { Cluster = _config.Cluster with { Priority = 50 } });
            var viewModel = new SetupWindowViewModel();
            var confirmation = new SettingsChangeConfirmationTests.RecordingConfirmation(false);
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: true, confirmation, _messages);

            viewModel.WindowsNotificationsEnabled = false;
            Save(view);

            Assert.Equal(["Pokazuj powiadomienia Windows"], Assert.Single(confirmation.Asked).Select(c => c.Name));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void C4_PriorityComesFromConfigJson(bool includeSecrets)
    {
        WpfTestHost.Run(() =>
        {
            ConfigLoader.SaveAppConfig(AppPaths.ConfigPath, _config with { Cluster = _config.Cluster with { Priority = 50 } });
            var viewModel = new SetupWindowViewModel();
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets, AlwaysConfirm.Instance, _messages);
            Assert.Equal("50", viewModel.PriorityText);

            viewModel.WindowsNotificationsEnabled = false;
            Save(view);
            AppConfig file = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath);
            Assert.Equal(50, file.Cluster.Priority);
            Assert.False(file.Ui.WindowsNotificationsEnabled);
            Assert.Equal("50", viewModel.PriorityText); // po zapisie pole czyta znowu z pliku
            Assert.Empty(_messages.Shown); // priorytet nietknięty — bez „zadziała po ponownym uruchomieniu”
            Assert.Equal([(NotificationKind.Success, "Zapisano ustawienia", (string?)null, (string?)null)], _messages.Notified);

            var confirmation = new SettingsChangeConfirmationTests.RecordingConfirmation(false);
            var second = new SetupView(new SetupWindowViewModel(), _config, _secrets, orchestrator: null, includeSecrets, confirmation, _messages);
            ((SetupWindowViewModel)second.DataContext).PriorityText = "70";
            Save(second);
            Assert.Equal(new SettingChange("Priorytet tego komputera", "50", "70"), Assert.Single(Assert.Single(confirmation.Asked)));
        });
    }


    /// <summary>
    /// Błąd zapisu magazynu (np. DPAPI „Odmowa dostępu” w sesji narzędzia) kończy się komunikatem „VNC Alert — błąd zapisu”
    /// z tą samą treścią, przyciskiem i ikoną — w teście tylko w atrapie, bez okna na pulpicie.
    /// </summary>
    [Fact]
    public void SaveError_IsReportedThroughUserMessages_WithTheSameTextCaptionAndIcon()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: false, AlwaysConfirm.Instance, _messages);
            viewModel.SamePasswordForAllMachines = true;
            view.SharedPasswordBox.Password = "haslo-vnc-1";
            _secrets.FailOnSet = new System.Security.Cryptography.CryptographicException("Odmowa dostępu.");

            typeof(SetupView).GetMethod("SaveButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [view, new RoutedEventArgs()]);
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);

            var shown = Assert.Single(_messages.Shown);
            Assert.Equal("VNC Alert — błąd zapisu", shown.Caption);
            Assert.StartsWith("Nie udało się zapisać wszystkich ustawień. Plik haseł może być uszkodzony", shown.Text, StringComparison.Ordinal);
            Assert.Equal(MessageBoxButton.OK, shown.Button);
            Assert.Equal(MessageBoxImage.Error, shown.Icon);
            Assert.Equal("haslo-vnc-1", view.SharedPasswordBox.Password); // wpisy zostają
        });
    }

    [Fact]
    public void PriorityAndTokenIdChange_TellAboutRestartThroughUserMessages()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new SetupWindowViewModel();
            var view = new SetupView(viewModel, _config, _secrets, orchestrator: null, includeSecrets: true, AlwaysConfirm.Instance, _messages);
            viewModel.PriorityText = "70";
            viewModel.UpdatesAccessClientId = "inny.access";

            Save(view);

            Assert.Equal(70, ConfigLoader.LoadAppConfig(AppPaths.ConfigPath).Cluster.Priority);
            Assert.Empty(_messages.Shown);
            var notified = Assert.Single(_messages.Notified);
            Assert.Equal(NotificationKind.Success, notified.Kind);
            Assert.Equal("Zapisano ustawienia", notified.Title);
            Assert.Equal(
                "Zmiana klucza sieciowego lub priorytetu zadziała po ponownym uruchomieniu programu.\n"
                + "Zmiana identyfikatora tokenu dostępu do katalogu wydań zadziała po ponownym uruchomieniu programu.",
                notified.Detail);
            Assert.Null(notified.RevealPath);
        });
    }

    [Fact]
    public void ShortPanelPassword_IsRejectedThroughUserMessages()
    {
        WpfTestHost.Run(() =>
        {
            var view = new SetupView(new SetupWindowViewModel(), _config, _secrets, orchestrator: null, includeSecrets: false, AlwaysConfirm.Instance, _messages);
            view.NewAdminPassword.Password = "krotkie";
            view.ConfirmAdminPassword.Password = "krotkie";

            Save(view);

            Assert.Equal([("Nowe hasło musi mieć co najmniej 10 znaków i być identyczne w obu polach.", "VNC Alert", MessageBoxButton.OK, MessageBoxImage.None)],
                _messages.Shown);
            Assert.Empty(_messages.Notified);
            Assert.Empty(_secrets.Changes);
        });
    }

    /// <summary>Widok nie woła MessageBoxa wprost, a program rejestruje prawdziwe komunikaty i podaje je obu widokom Ustawień.</summary>
    [Fact]
    public void SetupViewUsesOnlyUserMessages_AndProgramRegistersTheMessageBoxImplementation()
    {
        string setup = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "SetupView.xaml.cs"));
        Assert.DoesNotContain("MessageBox.Show(", setup, StringComparison.Ordinal);
        Assert.Contains("_messages = messages ?? DesktopUserMessages.Instance;", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxImage.Information", setup, StringComparison.Ordinal);

        string program = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Program.cs"));
        Assert.Contains("services.AddSingleton<IUserMessages, DesktopUserMessages>();", program, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(program, @"sp\.GetRequiredService<IUserMessages>\(\)").Count);
    }

    private static void Save(SetupView view)
    {
        var task = (Task)typeof(SetupView).GetMethod("SaveSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        if (!task.IsCompleted) throw new InvalidOperationException("Zapis nie zakończył się synchronicznie (okno komunikatu?).");
        task.GetAwaiter().GetResult();
    }

    private static void Settle(Window window)
    {
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
    }

    private static string[] Names(DependencyObject root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Logical(DependencyObject node)
        {
            if (node is FrameworkElement { Name.Length: > 0 } fe) names.Add(fe.Name);
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) Logical(d);
        }
        void Visual(DependencyObject node)
        {
            if (node is FrameworkElement { Name.Length: > 0 } fe) names.Add(fe.Name);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visual(VisualTreeHelper.GetChild(node, i));
        }
        Logical(root);
        Visual(root);
        return names.ToArray();
    }

    private static IEnumerable<string> VisualText(DependencyObject node)
    {
        if (node is TextBlock tb) yield return tb.Text;
        if (node is TextBox box) yield return box.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (string text in VisualText(VisualTreeHelper.GetChild(node, i)))
                yield return text;
    }

    public void Dispose()
    {
        foreach (string file in new[] { AppPaths.ConfigPath, AppPaths.ConfigPath + ".bak" })
            if (File.Exists(file)) File.Delete(file);
    }

    private sealed class FakeSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public List<KeyValuePair<string, string>> Changes { get; } = [];
        public List<string> Removed { get; } = [];
        /// <summary>Wyjątek rzucany przy zapisie (np. DPAPI „Odmowa dostępu”); <see langword="null"/> = zapis działa.</summary>
        public Exception? FailOnSet { get; set; }
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value)
        {
            if (FailOnSet is not null) throw FailOnSet;
            _values[key] = value;
            Changes.Add(new(key, value));
        }
        public bool Remove(string key) { Removed.Add(key); return _values.Remove(key); }
        public IReadOnlyCollection<string> Keys => _values.Keys;
    }
}

/// <summary>Komunikaty i dymki widoku zapisane zamiast okna (<see cref="IUserMessages"/>).</summary>
internal sealed class RecordingMessages : IUserMessages
{
    public List<(string Text, string Caption, MessageBoxButton Button, MessageBoxImage Icon)> Shown { get; } = [];

    public List<(NotificationKind Kind, string Title, string? Detail, string? RevealPath)> Notified { get; } = [];

    public void Notify(NotificationKind kind, string title, string? detail = null, string? revealPath = null) =>
        Notified.Add((kind, title, detail, revealPath));

    public MessageBoxResult Show(string text, string caption, MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        Shown.Add((text, caption, button, icon));
        return MessageBoxResult.OK;
    }
}

/// <summary>Testy piszące config.json w katalogu testów — jeden po drugim.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SetupViewCollection
{
    public const string Name = "SetupView (config.json w katalogu testów)";
}
