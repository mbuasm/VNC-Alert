using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MaszynyAlert.App.Services;
using MaszynyAlert.Windows;
using MaszynyAlert.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.App;

/// <summary>Aplikacja w zasobniku — punkt startowy WPF. Punkt wejścia procesu (Main, [STAThread]) jest w <see cref="Program"/>.</summary>
public partial class App : System.Windows.Application
{
    private TrayIconService? _tray;

    public App()
    {
        InitializeComponent();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        Views.TaskbarIcon.RegisterForAllWindows();
    }

    /// <summary>Kontener DI zbudowany przez <see cref="Program"/> — ustawiany przed wywołaniem <see cref="System.Windows.Application.Run()"/>.</summary>
    public IServiceProvider Services { get; set; } = null!;

    /// <summary>Argumenty wiersza poleceń przekazane do <see cref="Program.Main"/> (np. <c>--setup</c>, <c>--test-alarm</c>).</summary>
    public string[] StartupArgs { get; set; } = [];

    internal bool IsStartupSmokeTest { get; set; }
    internal bool IsSettingsEditor { get; set; }
    internal bool StartupSmokeTestPassed { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (IsSettingsEditor) return;

        // Motyw przed oknami: okna z DI powstają niżej, a ich pędzle (DynamicResource) i tak podążą za zmianą.
        Services.GetRequiredService<ThemeService>().Initialize();

        _tray = Services.GetRequiredService<TrayIconService>();
        _tray.Initialize();

        if (IsStartupSmokeTest)
        {
            // Tworzymy rzeczywiste okna (DI + XAML), bez pokazywania alarmu i zapisu ustawień.
            _ = Services.GetRequiredService<Views.AlertWindow>();
            _ = Services.GetRequiredService<Views.StatusWindow>();
            _ = Services.GetRequiredService<Views.SetupWindow>();
            _ = Services.GetRequiredService<Views.IdlePopupWindow>();
            _ = Services.GetRequiredService<Views.ScreenPopupWindow>();
            _ = Services.GetRequiredService<Views.OverlayWindow>();
            _ = Services.GetRequiredService<Views.SharedConfigView>();
            _ = Services.GetRequiredService<Views.UpdateWindow>();
            var status = Services.GetRequiredService<ViewModels.StatusWindowViewModel>();
            var setup = Services.GetRequiredService<Views.SetupWindow>();
            if (status.IsClusterEnabled
                || setup.NetworkKeyGroupVisible
                || !Services.GetRequiredService<AppOrchestrator>().Cluster.IsWatcher)
            {
                throw new InvalidOperationException("Tryb jednego komputera wymaga obserwatora i ukrytych ustawień klastra.");
            }
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                var orchestrator = Services.GetRequiredService<AppOrchestrator>();
                status.Refresh(DateTimeOffset.UtcNow);
                var diagnosticRow = new ViewModels.MachineRowViewModel(new MaszynyAlert.Core.Machines.MachineStatusSnapshot
                {
                    MachineId = "smoke", Name = "Smoke", Host = "unused", Profile = "test",
                    State = MaszynyAlert.Core.Machines.MachineConnectionState.Connected,
                    Source = "plik", UsingCachedFrame = true, LastFrameAt = DateTimeOffset.UtcNow,
                });
                if (!diagnosticRow.IsProblem || !diagnosticRow.Source.Contains("TEST Z PLIKU")
                    || !diagnosticRow.LastFrameText.Contains("obraz z pamięci"))
                    throw new InvalidOperationException("Status musi wyróżniać test z pliku i obraz z pamięci.");
                if (!orchestrator.Supervisor.MonitoringEnabled)
                {
                    if (orchestrator.Supervisor.IsRunning || !status.IsRoleWarning
                        || !status.RoleText.Contains("MONITOROWANIE WYŁĄCZONE"))
                        throw new InvalidOperationException("Wyłączone monitorowanie musi być widoczne i nie uruchamiać monitorów.");
                    orchestrator.TestAlarm();
                    if (!orchestrator.VisibleAlerts.Any(a => a.MachineId == "test"))
                        throw new InvalidOperationException("Lokalny test alarmu musi działać przy globalnym wyłączeniu.");
                }
                await VerifyDiagnosticsSmokeTestAsync(status);
                await VisualSmokeTest.VerifyPreviewActivationAsync(Services);
                await VisualSmokeTest.VerifyMachineScreenPreviewAsync(Services);
                VisualSmokeTest.Capture(Services);
                VisualSmokeTest.VerifyTechnicalTab(Services);
                VisualSmokeTest.VerifyDarkTheme(Services);
                await VisualSmokeTest.VerifyUpdateWindowAsync(Services);
                await VisualSmokeTest.VerifyOverlayAsync(Services);
                await VisualSmokeTest.VerifyMultiComputerWizardStartAsync(Services);
                await VisualSmokeTest.VerifyInstructionEditorAsync(Services);
                await VisualSmokeTest.VerifyAlertDismissalAsync(Services);
                await VisualSmokeTest.VerifyPopupLayoutAsync(Services);
                await VisualSmokeTest.VerifyAlertHelpPlacementAsync(Services);
                await VisualSmokeTest.VerifyResumeAsync(Services);
                StartupSmokeTestPassed = true;
                Shutdown(0);
            }));
            return;
        }

        RepairAutostartIfMoved();

        if (StartupArgs.Contains("--preview", StringComparer.OrdinalIgnoreCase))
            _tray.ShowStatus();

        bool forceSetup = StartupArgs.Contains("--setup", StringComparer.OrdinalIgnoreCase);
        bool needsInitialSetup = _tray.NeedsInitialSetup();
        if (forceSetup || needsInitialSetup)
        {
            _tray.ShowSetup(defaultAutostartOn: needsInitialSetup);
        }

        if (StartupArgs.Contains("--test-alarm", StringComparer.OrdinalIgnoreCase))
        {
            Services.GetRequiredService<AppOrchestrator>().TestAlarm();
        }
    }

    private async Task VerifyDiagnosticsSmokeTestAsync(ViewModels.StatusWindowViewModel status)
    {
        BuildIdentity build = Services.GetRequiredService<BuildIdentity>();
        if (status.VersionText != build.DisplayText)
            throw new InvalidOperationException("Status nie pokazuje tożsamości uruchomionej aplikacji.");

        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output");
        var diagnostics = Services.GetRequiredService<DiagnosticsService>();
        DiagnosticsExportRequest request = diagnostics.CaptureRequest();
        string archiveName = $"diagnostics-{Guid.NewGuid():N}.zip";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DiagnosticsExportResult result = await diagnostics.ExportAsync(Path.Combine(output, archiveName), request, cancellation.Token);
        using (var archive = ZipFile.OpenRead(result.ArchivePath))
        {
            foreach (string required in new[] { "manifest.json", "build-info.json", "status.json", "formularz-odbioru.md" })
                if (archive.GetEntry(required) is null)
                    throw new InvalidOperationException($"Diagnostyka nie zawiera {required}.");
            if (archive.Entries.Any(e => new[] { "secrets.dat", "config.json", "rules.json" }
                .Contains(Path.GetFileName(e.FullName), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Diagnostyka zawiera plik spoza dozwolonego zakresu.");
            using Stream buildStream = archive.GetEntry("build-info.json")!.Open();
            using JsonDocument buildJson = await JsonDocument.ParseAsync(buildStream, cancellationToken: cancellation.Token);
            if (buildJson.RootElement.GetProperty("version").GetString() != build.Version
                || buildJson.RootElement.GetProperty("commit").GetString() != build.Commit)
                throw new InvalidOperationException("Diagnostyka zawiera inną tożsamość aplikacji.");
            using Stream statusStream = archive.GetEntry("status.json")!.Open();
            using JsonDocument statusJson = await JsonDocument.ParseAsync(statusStream, cancellationToken: cancellation.Token);
            if (statusJson.RootElement.GetProperty("monitoringEnabled").GetBoolean()
                    != Services.GetRequiredService<AppOrchestrator>().Supervisor.MonitoringEnabled
                || statusJson.RootElement.GetProperty("effectiveClusterEnabled").GetBoolean())
                throw new InvalidOperationException("Diagnostyka zawiera nieprawidłowy stan obserwacji.");
            if (statusJson.RootElement.GetProperty("update").GetProperty("phase").GetString() != "Disabled")
                throw new InvalidOperationException("Diagnostyka ma pokazywać wyłączone aktualizacje w teście startu (portable).");
            using Stream manifestStream = archive.GetEntry("manifest.json")!.Open();
            using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellation.Token);
            if (manifestJson.RootElement.GetProperty("acceptanceStatus").GetString() != "NIE ODEBRANO")
                throw new InvalidOperationException("Diagnostyka nie może automatycznie potwierdzać odbioru na hali.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "build-identity.json"), JsonSerializer.Serialize(new
        {
            version = build.Version,
            commit = build.Commit,
            statusVersionText = status.VersionText,
            diagnosticsArchive = archiveName,
        }), cancellation.Token);
    }

    private void RepairAutostartIfMoved()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        try
        {
            if (Autostart.RepairIfMoved(exePath, AppPaths.IsInstalled))
            {
                Services.GetService<ILogger<App>>()?.LogInformation(
                    "Wpis autostartu wskazywał starą ścieżkę — poprawiono na {ExePath}.", exePath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Services.GetService<ILogger<App>>()?.LogWarning(
                ex, "Brak uprawnień do naprawy wpisu autostartu (HKLM) — poproś administratora/IT o pomoc.");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsStartupSmokeTest)
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "failure.txt"),
                e.Exception.ToString());
            e.Handled = true;
            Shutdown(1);
            return;
        }

        try
        {
            Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Nieobsłużony wyjątek w wątku interfejsu użytkownika.");
        }
        catch
        {
            // logowanie nie może samo wywołać kolejnego nieobsłużonego wyjątku
        }

        System.Windows.MessageBox.Show(
            $"Wystąpił nieoczekiwany błąd i część funkcji programu może nie działać poprawnie:\n\n{e.Exception.Message}\n\n" +
            "Szczegóły zapisano w logu (Panel administracyjny → Podgląd techniczny → Otwórz folder logów). Zalecane jest zatrzymanie programu komendą " +
            "\"vncalert killapp\" i uruchomienie go ponownie skrótem \"VNC Alert\" (menu zasobnika nie zamyka już programu).",
            "VNC Alert — błąd",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
