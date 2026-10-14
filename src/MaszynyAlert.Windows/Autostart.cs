using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MaszynyAlert.Windows;

[SupportedOSPlatform("windows")]
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MaszynyAlert";

    /// <summary>Dodaje/nadpisuje wpis autostartu wskazujący na <paramref name="exePath"/> (zapisywany w cudzysłowie).</summary>
    /// <param name="allUsers">true → HKEY_LOCAL_MACHINE (wymaga uprawnień administratora); false → HKEY_CURRENT_USER.</param>
    /// <exception cref="UnauthorizedAccessException">Brak uprawnień do zapisu w wybranym kluczu — celowo przepuszczana, wywołujący pokazuje komunikat PL.</exception>
    public static void Enable(string exePath, bool allUsers)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        using RegistryKey key = OpenRunKey(allUsers, writable: true)
            ?? throw new InvalidOperationException("Nie udało się otworzyć klucza rejestru autostartu.");
        key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    /// <summary>Usuwa wpis autostartu, jeśli istnieje.</summary>
    public static void Disable(bool allUsers)
    {
        using RegistryKey? key = OpenRunKey(allUsers, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Czy autostart jest włączony — porównuje zapisaną ścieżkę ze ścieżką bieżącego procesu.</summary>
    public static bool IsEnabled(bool allUsers)
    {
        using RegistryKey? key = OpenRunKey(allUsers, writable: false);
        object? value = key?.GetValue(ValueName);
        if (value is not string storedCommand || string.IsNullOrEmpty(storedCommand))
        {
            return false;
        }

        string storedPath = storedCommand.Trim('"');
        string? currentPath = Environment.ProcessPath;
        return !string.IsNullOrEmpty(currentPath) && string.Equals(storedPath, currentPath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool RepairIfMoved(string exePath, bool allUsers)
    {
        ArgumentNullException.ThrowIfNull(exePath);

        using RegistryKey? readKey = OpenRunKey(allUsers, writable: false);
        object? value = readKey?.GetValue(ValueName);
        if (value is not string storedCommand || string.IsNullOrEmpty(storedCommand))
        {
            return false;
        }

        string storedPath = storedCommand.Trim('"');
        if (string.Equals(storedPath, exePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Enable(exePath, allUsers);
        return true;
    }

    private static RegistryKey? OpenRunKey(bool allUsers, bool writable)
    {
        RegistryKey root = allUsers ? Registry.LocalMachine : Registry.CurrentUser;
        return root.OpenSubKey(RunKeyPath, writable);
    }
}
