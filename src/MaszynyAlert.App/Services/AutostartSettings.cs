using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Services;

/// <summary>Autostart odczytywany i zapisywany przez Ustawienia; testy podstawiają atrapę bez rejestru.</summary>
public interface IAutostartSettings
{
    bool IsEnabled(bool allUsers);
    void Enable(string exePath, bool allUsers);
    void Disable(bool allUsers);
}

internal sealed class WindowsAutostartSettings : IAutostartSettings
{
    public static readonly WindowsAutostartSettings Instance = new();

    public bool IsEnabled(bool allUsers) => Autostart.IsEnabled(allUsers);
    public void Enable(string exePath, bool allUsers) => Autostart.Enable(exePath, allUsers);
    public void Disable(bool allUsers) => Autostart.Disable(allUsers);
}
