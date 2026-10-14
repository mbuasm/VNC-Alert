using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Tests;

[Collection(SetupViewCollection.Name)]
public sealed class SetupViewAutostartTests : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, byte[]?> _previousFiles;

    public SetupViewAutostartTests()
    {
        if (AppPaths.IsInstalled || !Path.GetFullPath(AppPaths.ConfigPath).StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test może zapisać konfigurację tylko w swoim katalogu wyjściowym.");
        _previousFiles = new[] { AppPaths.ConfigPath, AppPaths.ConfigPath + ".bak" }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        AppConfig example = ConfigLoader.LoadAppConfig(Path.Combine(AppContext.BaseDirectory, "config.example.json"));
        _config = example with
        {
            Cluster = example.Cluster with { Enabled = false },
            Ui = example.Ui with { WindowsNotificationsEnabled = true },
        };
        ConfigLoader.SaveAppConfig(AppPaths.ConfigPath, _config);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void C02_DeniedAutostartDoesNotAnnounceCompleteSaveOrFinish(bool initiallyEnabled, bool otherChange)
    {
        WpfTestHost.Run(() =>
        {
            var autostart = new FakeAutostart(initiallyEnabled) { DenyWrites = true };
            var messages = new RecordingMessages();
            var model = new SetupWindowViewModel();
            var view = new SetupView(model, _config, new InMemorySecretStore(), null, false,
                AlwaysConfirm.Instance, messages, autostart);
            int finished = 0;
            view.Finished += () => finished++;
            model.AutostartEnabled = !initiallyEnabled;
            if (otherChange) model.WindowsNotificationsEnabled = false;

            Save(view);

            Assert.Equal(1, autostart.Attempts);
            Assert.Equal(initiallyEnabled, autostart.Enabled);
            Assert.Equal(initiallyEnabled, model.AutostartEnabled);
            Assert.Equal(!otherChange, ConfigLoader.LoadAppConfig(AppPaths.ConfigPath).Ui.WindowsNotificationsEnabled);
            var warning = Assert.Single(messages.Shown);
            Assert.Contains("Brak uprawnień do zmiany autostartu", warning.Text, StringComparison.Ordinal);
            Assert.Equal(MessageBoxButton.OK, warning.Button);
            Assert.Equal(MessageBoxImage.Warning, warning.Icon);
            Assert.Empty(messages.Notified);
            Assert.Equal(0, finished);
            if (otherChange) Assert.Contains("Pozostałe zmiany ustawień zostały zapisane.", warning.Text, StringComparison.Ordinal);
            else Assert.DoesNotContain("Pozostałe zmiany", warning.Text, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulAutostartSaveNotifiesBeforeFinished(bool initiallyEnabled)
    {
        WpfTestHost.Run(() =>
        {
            var autostart = new FakeAutostart(initiallyEnabled);
            var messages = new RecordingMessages();
            var model = new SetupWindowViewModel();
            var view = new SetupView(model, _config, new InMemorySecretStore(), null, false,
                AlwaysConfirm.Instance, messages, autostart);
            int finished = 0;
            view.Finished += () =>
            {
                Assert.Single(messages.Notified);
                finished++;
            };
            model.AutostartEnabled = !initiallyEnabled;

            Save(view);

            Assert.Equal(1, autostart.Attempts);
            Assert.Equal(!initiallyEnabled, autostart.Enabled);
            Assert.Equal(!initiallyEnabled, model.AutostartEnabled);
            Assert.Empty(messages.Shown);
            var notified = Assert.Single(messages.Notified);
            Assert.Equal(NotificationKind.Success, notified.Kind);
            Assert.Equal(SetupView.SavedTitle, notified.Title);
            Assert.Equal(1, finished);
        });
    }

    [Fact]
    public void UnchangedAutostartIsNotWritten()
    {
        WpfTestHost.Run(() =>
        {
            var autostart = new FakeAutostart(true) { DenyWrites = true };
            var messages = new RecordingMessages();
            var model = new SetupWindowViewModel();
            var view = new SetupView(model, _config, new InMemorySecretStore(), null, false,
                AlwaysConfirm.Instance, messages, autostart);
            model.WindowsNotificationsEnabled = false;

            Save(view);

            Assert.Equal(0, autostart.Attempts);
            Assert.Single(messages.Notified);
            Assert.Empty(messages.Shown);
        });
    }

    [Fact]
    public void C02_PartiallySavedAdminPasswordDoesNotBlockTheNextSave()
    {
        WpfTestHost.Run(() =>
        {
            var secrets = new InMemorySecretStore();
            secrets.Set(AdminPassword.SecretKey, AdminPassword.Hash("poprzednie-haslo-panelu"));
            var autostart = new FakeAutostart(false) { DenyWrites = true };
            var messages = new RecordingMessages();
            var model = new SetupWindowViewModel();
            var view = new SetupView(model, _config, secrets, null, false,
                AlwaysConfirm.Instance, messages, autostart);
            int finished = 0;
            view.Finished += () => finished++;
            view.CurrentAdminPassword.Password = "poprzednie-haslo-panelu";
            view.NewAdminPassword.Password = "zapisane-nowe-haslo-panelu";
            view.ConfirmAdminPassword.Password = "zapisane-nowe-haslo-panelu";
            model.AutostartEnabled = true;

            Save(view);

            Assert.True(AdminPassword.Verify("zapisane-nowe-haslo-panelu", secrets.Get(AdminPassword.SecretKey)));
            Assert.Single(messages.Shown);
            Assert.Empty(messages.Notified);
            Assert.Equal(0, finished);
            string[] fieldsAfterPartialSave =
                [view.CurrentAdminPassword.Password, view.NewAdminPassword.Password, view.ConfirmAdminPassword.Password];
            model.WindowsNotificationsEnabled = false;

            Save(view);

            Assert.False(ConfigLoader.LoadAppConfig(AppPaths.ConfigPath).Ui.WindowsNotificationsEnabled);
            Assert.All(fieldsAfterPartialSave, value => Assert.Empty(value));
            Assert.Single(messages.Shown);
            Assert.Single(messages.Notified);
            Assert.Equal(1, finished);
            Assert.Equal(1, autostart.Attempts);
            Assert.True(AdminPassword.Verify("zapisane-nowe-haslo-panelu", secrets.Get(AdminPassword.SecretKey)));
        });
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void C02_PartialSaveWarningIncludesRestartNotesForSavedSettings(bool changeKey, bool changePriority, bool changeClientId)
    {
        WpfTestHost.Run(() =>
        {
            var secrets = new InMemorySecretStore();
            var autostart = new FakeAutostart(false) { DenyWrites = true };
            var messages = new RecordingMessages();
            var model = new SetupWindowViewModel();
            var view = new SetupView(model, _config, secrets, null, true,
                AlwaysConfirm.Instance, messages, autostart);
            int finished = 0;
            view.Finished += () => finished++;
            string key = Convert.ToBase64String(new byte[32]);
            if (changeKey) model.NetworkKeyBase64 = key;
            if (changePriority) model.PriorityText = "101";
            if (changeClientId) model.UpdatesAccessClientId = "c02-partial.access";
            model.AutostartEnabled = true;

            Save(view);

            if (changeKey) Assert.Equal(key, secrets.Get(SecretKeys.ClusterHmacKey));
            AppConfig saved = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath);
            if (changePriority) Assert.Equal(101, saved.Cluster.Priority);
            if (changeClientId) Assert.Equal("c02-partial.access", saved.Updates.AccessClientId);
            var warning = Assert.Single(messages.Shown);
            Assert.Equal(MessageBoxButton.OK, warning.Button);
            Assert.Equal(MessageBoxImage.Warning, warning.Icon);
            Assert.Contains("Pozostałe zmiany ustawień zostały zapisane.", warning.Text, StringComparison.Ordinal);
            if (changeKey || changePriority)
                Assert.Contains("Zmiana klucza sieciowego lub priorytetu zadziała po ponownym uruchomieniu programu.", warning.Text, StringComparison.Ordinal);
            if (changeClientId)
                Assert.Contains("Zmiana identyfikatora tokenu dostępu do katalogu wydań zadziała po ponownym uruchomieniu programu.", warning.Text, StringComparison.Ordinal);
            Assert.Empty(messages.Notified);
            Assert.Equal(0, finished);
        });
    }
    private static void Save(SetupView view)
    {
        var task = (Task)typeof(SetupView).GetMethod("SaveSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted, "Atrapy nie mogą otworzyć okna ani oczekiwać na prawdziwy autostart.");
        task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach ((string path, byte[]? content) in _previousFiles)
        {
            if (content is null) File.Delete(path);
            else File.WriteAllBytes(path, content);
        }
    }

    private sealed class FakeAutostart(bool enabled) : IAutostartSettings
    {
        public bool Enabled { get; private set; } = enabled;
        public bool DenyWrites { get; init; }
        public int Attempts { get; private set; }
        public bool IsEnabled(bool allUsers) => Enabled;
        public void Enable(string exePath, bool allUsers) => Set(true);
        public void Disable(bool allUsers) => Set(false);
        private void Set(bool value)
        {
            Attempts++;
            if (DenyWrites) throw new UnauthorizedAccessException("Atrapa: odmowa zapisu autostartu.");
            Enabled = value;
        }
    }
}
