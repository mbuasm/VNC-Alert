namespace MaszynyAlert.Windows.Tests;

public sealed class InteractiveSessionFactAttribute : FactAttribute
{
    public InteractiveSessionFactAttribute()
    {
        if (IsServiceSession)
            Skip = "Sesja 0: Local\\ i Global\\ to ta sama przestrzeń nazw — test wymaga sesji interaktywnej (sprawdza go też Test-Installer.ps1).";
    }

    internal static bool IsServiceSession =>
        OperatingSystem.IsWindows() && System.Diagnostics.Process.GetCurrentProcess().SessionId == 0;
}
