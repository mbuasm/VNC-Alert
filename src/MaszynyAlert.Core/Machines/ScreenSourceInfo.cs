namespace MaszynyAlert.Core.Machines;

/// <summary>
/// Informacje o źródle obrazu ekranu maszyny, zebrane przy ostatnim udanym połączeniu/odczycie —
/// pokazywane w oknie statusu (wersja RFB, typ zabezpieczeń, format piksela) i w raporcie <c>probe.exe</c>.
/// </summary>
/// <param name="Kind">Rodzaj źródła: "vnc" (<see cref="VncScreenSource"/>) albo "plik" (<see cref="FolderScreenSource"/>, tryb testowy).</param>
/// <param name="ProtocolVersion">Wersja protokołu RFB (np. "3.8"); null dla źródła plikowego.</param>
/// <param name="SecurityType">Typ zabezpieczeń użyty przy połączeniu VNC (np. "VncAuth"); null dla źródła plikowego.</param>
/// <param name="PixelFormat">Krótki opis formatu piksela zgłoszonego przez serwer VNC; null dla źródła plikowego.</param>
/// <param name="Width">Szerokość obrazu (całego ekranu/pliku) w pikselach.</param>
/// <param name="Height">Wysokość obrazu (całego ekranu/pliku) w pikselach.</param>
/// <param name="DesktopName">Nazwa pulpitu zgłoszona przez serwer VNC, albo nazwa pliku w trybie testowym.</param>
public sealed record ScreenSourceInfo(
    string Kind,
    string? ProtocolVersion,
    string? SecurityType,
    string? PixelFormat,
    int Width,
    int Height,
    string? DesktopName);
