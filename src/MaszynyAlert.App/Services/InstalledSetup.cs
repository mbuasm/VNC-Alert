using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Services;

/// <summary>Oddzielny edytor administracyjny. Nie tworzy hosta, OCR ani połączeń VNC/UDP.</summary>
internal static class InstalledSetup
{
    internal const string MutexName = "Global\\MaszynyAlert.Setup";

    internal static int RunSmokeTest()
    {
        // Brak hosta i Services jest celowy: przypadkowe uruchomienie tray/monitorów ma oblać test.
        string output = System.IO.Path.Combine(AppPaths.ExeDir, "smoke-test-output");
        System.IO.Directory.CreateDirectory(output);
        try
        {
            var app = new App
            {
                IsSettingsEditor = true, IsStartupSmokeTest = true,
                ShutdownMode = ShutdownMode.OnMainWindowClose,
            };
            var window = new SetupWindow(new SetupWindowViewModel(), new AppConfig(),
                new MaszynyAlert.Core.Abstractions.InMemorySecretStore(), null, new DialogSettingsChangeConfirmation());
            bool closed = false;
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                window.Close();
                closed = true;
            }));
            int result = app.Run(window);
            return closed ? result : 1;
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(output, "settings-failure.txt"), ex.ToString());
            return 1;
        }
    }

    public static async Task<int> LaunchAsync()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = "--setup",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppPaths.ExeDir,
        }) ?? throw new InvalidOperationException("Nie udało się otworzyć ustawień.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>Argument pomocnika zapisującego ustawienia tego komputera z uprawnieniami administratora.</summary>
    internal const string ApplyArgument = "--apply-settings";

    /// <summary>
    /// Komplet zmian z okna Ustawień, przekazywany pomocnikowi z UAC (<see cref="ApplyArgument"/>).
    /// Wszystko jest już sprawdzone w procesie głównym (<see cref="SetupView"/>) — pomocnik ma tylko
    /// zapisać wartości. Puste/<see langword="null"/> pole oznacza „zostaw dotychczasową wartość”.
    /// </summary>
    internal sealed record SettingsChange
    {
        /// <summary>Hasła VNC per identyfikator maszyny (tylko te faktycznie wpisane).</summary>
        public Dictionary<string, string> VncPasswords { get; init; } = [];

        /// <summary>Jedno hasło VNC dla wszystkich maszyn z config.json (opcja „to samo hasło”).</summary>
        public string? SharedPassword { get; init; }

        /// <summary>Nowy klucz sieciowy klastra (Base64) albo <see langword="null"/>, gdy bez zmian.</summary>
        public string? NetworkKey { get; init; }

        public string? AdminPasswordHash { get; init; }

        /// <summary>Priorytet tego komputera przy wyborze obserwatora.</summary>
        public int Priority { get; init; }

        /// <summary>Czy pokazywać powiadomienia Windows (sekcja <c>ui</c> w config.json).</summary>
        public bool WindowsNotificationsEnabled { get; init; }

        /// <summary>Czy program ma wstawać przy logowaniu (wpis autostartu dla wszystkich użytkowników).</summary>
        public bool Autostart { get; init; }

        /// <summary>
        /// Identyfikator tokenu Cloudflare Access do katalogu wydań (<c>updates.accessClientId</c>):
        /// <see langword="null"/> — bez zmian, pusty — usuń identyfikator, inny — zapisz.
        /// </summary>
        public string? UpdatesAccessClientId { get; init; }

        /// <summary>Sekret tokenu Cloudflare Access; <see langword="null"/>/pusty — bez zmian (sekretu nie da się odczytać z powrotem do pola).</summary>
        public string? UpdatesAccessClientSecret { get; init; }
    }

    /// <summary>
    /// Uruchamia pomocnika z UAC i czeka na jego zakończenie. Rekord zmian jedzie jako Base64 w wierszu
    /// poleceń — patrz uwaga o bezpieczeństwie w dokumentacji <see cref="SetupView"/>.
    /// </summary>
    internal static async Task<int> ApplyElevatedAsync(SettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppPaths.ExeDir };
        start.ArgumentList.Add(ApplyArgument);
        start.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(change,
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        using var process = Process.Start(start)
            ?? throw new IOException("Nie uruchomiono zapisu ustawień.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    internal static int ApplyElevated(string payload)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!AppPaths.IsInstalled || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return 1;
            }

            if (payload.Length > 32000) throw new ArgumentException("Zestaw ustawień jest zbyt długi.");
            var change = JsonSerializer.Deserialize<SettingsChange>(Convert.FromBase64String(payload))
                ?? throw new ArgumentException("Brak ustawień do zapisania.");

            var config = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath);
            DpapiSecretStore secrets = DpapiSecretStore.ForMachine(AppPaths.DataDir);

            if (change.SharedPassword is { Length: > 0 } shared)
            {
                foreach (MachineConfig machine in config.Machines)
                {
                    secrets.Set(SecretKeys.VncPassword(machine.Id), shared);
                }
            }
            else
            {
                foreach ((string machineId, string password) in change.VncPasswords)
                {
                    if (password.Length > 0) secrets.Set(SecretKeys.VncPassword(machineId), password);
                }
            }

            if (change.NetworkKey is { Length: > 0 } key) secrets.Set(SecretKeys.ClusterHmacKey, key);
            if (change.AdminPasswordHash is { Length: > 0 } hash) secrets.Set(AdminPassword.SecretKey, hash);
            if (change.UpdatesAccessClientSecret is { Length: > 0 } updatesSecret)
                secrets.Set(SecretKeys.UpdatesAccessClientSecret, updatesSecret);

            ConfigLoader.SaveAppConfig(AppPaths.ConfigPath, config with
            {
                Cluster = config.Cluster with { Priority = change.Priority },
                Ui = config.Ui with { WindowsNotificationsEnabled = change.WindowsNotificationsEnabled },
                Updates = change.UpdatesAccessClientId is { } clientId
                    ? config.Updates with { AccessClientId = clientId.Length == 0 ? null : clientId }
                    : config.Updates,
            });

            if (change.Autostart != Autostart.IsEnabled(allUsers: true))
            {
                string exePath = Environment.ProcessPath ?? Path.Combine(AppPaths.ExeDir, "MaszynyAlert.App.exe");
                if (change.Autostart) Autostart.Enable(exePath, allUsers: true);
                else Autostart.Disable(allUsers: true);
            }

            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Nie udało się zapisać ustawień tego komputera.\n\n" + ex.Message,
                "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    public static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            try { Environment.ExitCode = LaunchAsync().GetAwaiter().GetResult(); }
            catch (System.ComponentModel.Win32Exception) { Environment.ExitCode = 1; }
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool created);
        if (!created) return;
        try
        {
            var config = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath);
            ConfigLoader.Validate(config);
            var app = new App { IsSettingsEditor = true, ShutdownMode = ShutdownMode.OnMainWindowClose };
            // Edytor z UAC działa poza kontenerem DI, ale ma wyglądać jak reszta programu: ten sam plik wyboru
            // motywu (profil użytkownika, który uruchomił program — przy UAC tego samego konta ta sama ścieżka).
            new ThemeService(new ThemeSettingsStore(System.IO.Path.Combine(AppPaths.UserDataDir, "theme.json")), app.Resources).Initialize();
            var window = new SetupWindow(new SetupWindowViewModel(), config,
                DpapiSecretStore.ForMachine(AppPaths.DataDir), null, new DialogSettingsChangeConfirmation());
            app.Run(window);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Nie udało się otworzyć ustawień.\n\n" + ex.Message,
                "VNC Alert", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
        }
    }
}
