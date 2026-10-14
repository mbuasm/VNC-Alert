using System.IO;
using System.Net.Http;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.Updates;
using MaszynyAlert.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.App.Services;

public sealed class UpdateService : BackgroundService
{
    private readonly HttpClientHandler _handler;
    private readonly AppOrchestrator _orchestrator;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(AppConfig config, AppOrchestrator orchestrator, ISecretStore secrets, BuildIdentity build,
        IClock clock, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = loggerFactory.CreateLogger<UpdateService>();

        (DownloadDirectory, AdminDirectory) = DirectoriesFor(AppPaths.DataDir);
        _handler = CreateHandler();
        UpdatesConfig updatesConfig = config.Updates;
        HttpUpdateSource source;
        try
        {
            source = new HttpUpdateSource(_handler, updatesConfig,
                () => ReadSecret(secrets), clock, loggerFactory.CreateLogger<HttpUpdateSource>());
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // Konfiguracja przechodzi kontrolę przy starcie, więc to nie powinno się zdarzyć — ale błędny adres
            // katalogu nie może zatrzymać programu: aktualizacje są wtedy wyłączone, a powód jest w logu.
            _logger.LogError(ex, "Nieprawidłowy adres katalogu wydań — aktualizacje automatyczne wyłączone.");
            updatesConfig = new UpdatesConfig { Enabled = false };
            source = new HttpUpdateSource(_handler, updatesConfig,
                () => null, clock, loggerFactory.CreateLogger<HttpUpdateSource>());
        }
        var taskRegistration = new UpdateTaskRegistration(AppPaths.ExeDir, AppPaths.DataDir);
        Installer = new WindowsUpdateInstaller(DownloadDirectory,
            Environment.ProcessPath ?? Path.Combine(AppPaths.ExeDir, "MaszynyAlert.App.exe"),
            loggerFactory.CreateLogger<WindowsUpdateInstaller>(),
            AdminDirectory, taskRegistration.IsUsable);
        Coordinator = new UpdateCoordinator(
            updatesConfig,
            AppPaths.IsInstalled,
            ReleaseCatalog.ParseInstalledVersion(build.Version),
            source,
            Installer,
            orchestrator.SharedConfig,
            document => orchestrator.BroadcastSharedConfigAsync(document),
            DownloadDirectory,
            Environment.MachineName,
            clock,
            CanInstallNow,
            loggerFactory.CreateLogger<UpdateCoordinator>(),
            AdminDirectory,
            taskRegistration.IsUsable);
        // Zgoda na instalację przychodzi z innego komputera grupy razem z dokumentem wspólnej konfiguracji.
        orchestrator.SharedConfig.Changed += Coordinator.OnSharedConfigChanged;
    }

    public Task ApproveAndInstallAsync(CancellationToken ct) => RunOnWorkerAsync(Coordinator.ApproveAndInstallAsync, ct);
    public Task InstallNowAsync(CancellationToken ct) => RunOnWorkerAsync(Coordinator.InstallNowAsync, ct);
    public Task CheckNowAsync(CancellationToken ct) => RunOnWorkerAsync(Coordinator.CheckNowAsync, ct);
    internal static Task RunOnWorkerAsync(Func<CancellationToken, Task> action, CancellationToken ct) =>
        Task.Run(() => action(ct), ct);

    public UpdateCoordinator Coordinator { get; }

    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseProxy = true };

    public WindowsUpdateInstaller Installer { get; }

    /// <summary>
    /// Katalogi aktualizacji w katalogu danych: pobrania (operator) i katalog roboczy zadania „VNC Alert update”
    /// (tylko administratorzy/SYSTEM — znacznik próby, wyniki, log instalatora).
    /// </summary>
    internal static (string Download, string Admin) DirectoriesFor(string dataDirectory) =>
        (UpdateTaskContract.DownloadDirectory(dataDirectory), UpdateTaskContract.AdminDirectory(dataDirectory));

    /// <summary><c>%ProgramData%\MaszynyAlert\state\updates</c> (operator; portable: obok EXE) — pobrane instalatory, żądanie dla zadania i pomocnik operatora.</summary>
    public string DownloadDirectory { get; }

    /// <summary><c>%ProgramData%\MaszynyAlert\updates-admin</c> — katalog zadania „VNC Alert update” (tylko administratorzy/SYSTEM; operator czyta).</summary>
    public string AdminDirectory { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Coordinator.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Zamykanie programu.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aktualizacje automatyczne zatrzymały się z błędem — monitoring działa dalej.");
        }
    }

    private bool CanInstallNow()
    {
        try
        {
            return !_orchestrator.VisibleAlerts.Any(a => a.Kind == AlertKind.Error && a.Status != AlertStatus.Closed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie odczytano stanu alarmów przed instalacją aktualizacji.");
            return false;
        }
    }

    private string? ReadSecret(ISecretStore secrets)
    {
        try { return secrets.Get(SecretKeys.UpdatesAccessClientSecret); }
        catch (Exception ex)
        {
            // Uszkodzony magazyn sekretów nie może zatrzymać sprawdzania — bez sekretu katalog odpowie 302,
            // a okno pokaże komunikat o logowaniu.
            _logger.LogWarning(ex, "Nie odczytano sekretu tokenu dostępu do katalogu wydań.");
            return null;
        }
    }

    public override void Dispose()
    {
        _orchestrator.SharedConfig.Changed -= Coordinator.OnSharedConfigChanged;
        base.Dispose();
        _handler.Dispose();
    }
}
