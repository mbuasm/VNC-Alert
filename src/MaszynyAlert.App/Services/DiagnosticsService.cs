using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.Updates;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Services;

/// <summary>Tworzy jawną migawkę opublikowanego stanu przed rozpoczęciem zapisu na dysku.</summary>
public sealed class DiagnosticsService(AppOrchestrator orchestrator, BuildIdentity build, UpdateCoordinator updates)
{
    public BuildIdentity Build => build;

    public string SuggestedFileName =>
        $"VncAlert-diagnostyka-{DateTime.Now:yyyyMMdd-HHmmss}-{(build.Commit is { Length: >= 8 } commit ? commit[..8] : "lokalna")}.zip";

    public DiagnosticsExportRequest CaptureRequest()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var machines = orchestrator.Supervisor.Snapshots.ToArray();
        var alerts = orchestrator.VisibleAlerts.ToArray();
        UpdateState update = updates.State;
        // Nie serializujemy konfiguracji, usług, sekretów ani środowiska. Opublikowane rekordy
        // są niezmienne; UpdatedAt opisuje czas obserwacji każdej maszyny w tej migawce.
        string statusJson = JsonSerializer.Serialize(new
        {
            createdAt,
            monitoringEnabled = orchestrator.Supervisor.MonitoringEnabled,
            supervisorRunning = orchestrator.Supervisor.IsRunning,
            effectiveClusterEnabled = orchestrator.Cluster.NetworkActive,
            clusterRequested = orchestrator.Cluster.Configuration.Enabled,
            clusterFailure = orchestrator.Cluster.Failure ?? orchestrator.Cluster.GroupNode?.TransportError,
            interruptedGeneration = orchestrator.Cluster.InterruptedGeneration,
            groupStateFresh = orchestrator.Cluster.StateFresh,
            groupStateAgeMs = orchestrator.Cluster.GroupNode?.StateAgeMs,
            isWatcher = orchestrator.Cluster.IsWatcher,
            noWatcher = orchestrator.Cluster.NoWatcher,
            ocr = new { available = orchestrator.Ocr.IsAvailable, languageTag = orchestrator.Ocr.LanguageTag },
            machines = machines.Select(m => new
            {
                m.MachineId, m.Name, m.Host, m.Port, m.Profile,
                state = m.State.ToString(), m.LastError, m.LastFrameAt, m.UsingCachedFrame,
                m.Source, m.UpdatedAt, m.OfflineSince, m.OcrAvailable, m.LastRedFraction,
                m.RedNow, m.RedConfirmed, m.LastOcrText, m.LastShortText,
                info = m.Info is { } info ? new
                {
                    info.Kind, info.ProtocolVersion, info.SecurityType, info.PixelFormat,
                    info.Width, info.Height,
                } : null,
                m.IdleWatchEnabled, m.LastProgressFraction, m.LastProgressPercent,
                m.LastProgressText, m.LastProgressSource, m.LastProgressBarText, m.ProgramStateText,
                m.ProgramAborted, m.ProgressFullNow, m.ProgressFullConfirmed, m.ProgressFullSince,
                m.ProgressZeroSince,
                m.MainScreenWatchEnabled, m.MainScreenWatchRequired, m.MainScreenVisible,
                m.ScreenObservationUnavailable, m.MainScreenUnknownStreak, m.MainScreenUnknownReason,
                m.WrongScreenSince, m.LastMainScreenCheckAt,
            }).ToArray(),
            alerts = alerts.Select(a => new
            {
                key = a.Key.ToString(), a.MachineId, a.MachineName, kind = a.Kind.ToString(),
                a.ShortText, a.FullText, a.Category, a.StartedAt, a.LastSeenAt,
                status = a.Status.ToString(), a.AcknowledgedAt, a.EscalatedAt, a.ClosedAt,
                a.Priority, a.Sound,
            }).ToArray(),
            update = new
            {
                phase = update.Phase.ToString(),
                installedVersion = update.InstalledVersion?.ToString(),
                latestVersion = update.Latest?.Version.ToString(),
                latestFile = update.Latest?.FileName,
                approval = update.Approval is { } approval
                    ? new { approval.Version, approval.FileName, approval.ApprovedBy, approval.ApprovedAt }
                    : null,
                update.LastCheckAt,
                update.LastError,
                update.Message,
                update.SnoozedUntil,
            },
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return new DiagnosticsExportRequest
        {
            Build = build, CreatedAt = createdAt, StatusJson = statusJson,
            LogsDirectory = AppPaths.Resolve(orchestrator.Config.Paths.LogsDir),
            CapturesDirectory = AppPaths.Resolve(orchestrator.Config.Paths.CapturesDir),
            HistoryPath = AppPaths.Resolve(orchestrator.Config.Paths.HistoryFile),
            UpdatesDirectory = System.IO.Path.Combine(AppPaths.DataDir, "state", "updates"),
            UpdateTaskDirectory = Core.Updates.UpdateTaskContract.AdminDirectory(AppPaths.DataDir),
        };
    }

    public Task<DiagnosticsExportResult> ExportAsync(string destination, DiagnosticsExportRequest request,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => new DiagnosticsExporter().Export(destination, request, cancellationToken), cancellationToken);
}
