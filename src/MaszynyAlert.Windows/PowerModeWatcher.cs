using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MaszynyAlert.Windows;

[SupportedOSPlatform("windows")]
public sealed class PowerModeWatcher : IDisposable
{
    private bool _disposed;

    public PowerModeWatcher()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Komputer właśnie się wybudził ze snu/hibernacji.</summary>
    public event Action? Resumed;

    /// <summary>Komputer właśnie zaczyna usypianie.</summary>
    public event Action? Suspending;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Resume:
                Resumed?.Invoke();
                break;
            case PowerModes.Suspend:
                Suspending?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
