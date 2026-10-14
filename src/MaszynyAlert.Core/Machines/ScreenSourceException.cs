namespace MaszynyAlert.Core.Machines;

/// <summary>
/// Zgłaszany przez <see cref="FolderScreenSource"/>, gdy plik testowego ekranu istnieje, ale w danej
/// chwili nie da się go odczytać — jest zablokowany przez inny program (użytkownik go właśnie
/// nadpisuje) albo jego zawartość jest niekompletna/uszkodzona (np. w trakcie kopiowania).
/// <see cref="MachineMonitor"/> traktuje to jak brak pliku w TYM cyklu (spróbuje ponownie w kolejnym) —
/// w odróżnieniu od błędów VNC, NIE liczy się to jako utrata łączności z maszyną.
/// </summary>
public sealed class ScreenSourceException : Exception
{
    public ScreenSourceException(string message)
        : base(message)
    {
    }

    public ScreenSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
