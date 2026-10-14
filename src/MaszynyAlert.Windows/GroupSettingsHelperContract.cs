using System.ComponentModel;
using System.Diagnostics;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Windows;

/// <summary>How the elevated helper failed. <see cref="Uncertain"/> means a commit request left this
/// process, so only the durable journal decides whether the generation is committed.</summary>
public enum GroupSettingsHelperOutcome { NoResponse, Disconnected, Rejected, Uncertain }

/// <summary>Helper failure with a Polish message for the operator; never declares success.</summary>
public sealed class GroupSettingsHelperException : IOException
{
    public GroupSettingsHelperOutcome Outcome { get; }
    public Guid? Generation { get; }
    public GroupSettingsHelperException(GroupSettingsHelperOutcome outcome, string message, Guid? generation = null, Exception? inner = null)
        : base(message, inner) { Outcome = outcome; Generation = generation; }
}

/// <summary>The operator declined the UAC prompt: nothing was started, nothing changed.</summary>
public sealed class GroupSettingsDeniedException : OperationCanceledException
{
    public GroupSettingsDeniedException(Exception inner)
        : base("Odmówiono uprawnień administratora. Ustawienia grupy nie zostały zmienione.", inner) { }
}

/// <summary>Handle of the launched helper; disposing never kills it (an elevated process cannot be
/// terminated from here), it only releases the handle.</summary>
internal interface IHelperProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken ct);
}

/// <summary>Starts the elevated helper that connects to the named pipe. UAC denial surfaces as
/// <see cref="Win32Exception"/> with native code 1223 (ERROR_CANCELLED).</summary>
internal interface IHelperLauncher
{
    IHelperProcess Launch(string pipeName);
}

internal sealed record HelperTimeouts(TimeSpan Prepare, TimeSpan Commit, TimeSpan Abort, TimeSpan Exit)
{
    // Prepare covers the UAC prompt plus two sequential PowerShell firewall calls (Capture then Apply), each capped
    // at GroupFirewall.Timeout (90 s).
    public static readonly HelperTimeouts Default = new(
        Prepare: TimeSpan.FromSeconds(240), Commit: TimeSpan.FromSeconds(60),
        Abort: TimeSpan.FromSeconds(45), Exit: TimeSpan.FromSeconds(15));
}

internal sealed record HelperRequest(string Operation, string? Invitation, ClusterConfig? Cluster, Guid? ExpectedCurrent, Guid? Generation)
{ public override string ToString() => "Group settings request [redacted]"; }

internal sealed record HelperResponse(bool Success, Guid? Generation, string? Error = null);

internal sealed class ProcessHelperLauncher : IHelperLauncher
{
    public static readonly ProcessHelperLauncher Instance = new();
    public IHelperProcess Launch(string pipeName)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--group-settings-helper"); start.ArgumentList.Add(pipeName);
        var process = Process.Start(start) ?? throw new IOException("Nie uruchomiono procesu ustawień.");
        return new Handle(process);
    }
    private sealed class Handle(Process process) : IHelperProcess
    {
        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);
        public void Dispose() => process.Dispose();
    }
}
