namespace MaszynyAlert.Core.Config;

/// <summary>
/// Ścieżki plików danych aplikacji. Ścieżki względne są liczone względem katalogu danych aplikacji
/// (patrz <c>AppPaths</c> w MaszynyAlert.Windows — portable: katalog programu; zainstalowana: %ProgramData%).
/// </summary>
public sealed record PathsConfig
{
    /// <summary>Katalog plików dziennika (logów).</summary>
    public string LogsDir { get; init; } = "logs";

    /// <summary>Katalog zrzutów ekranu (przechwyceń) zapisywanych przy nowych epizodach błędów.</summary>
    public string CapturesDir { get; init; } = "captures";

    /// <summary>Katalog plików PNG symulujących ekran maszyny w trybie testowym.</summary>
    public string TestScreensDir { get; init; } = "test-ekran";

    /// <summary>Nazwa pliku historii zdarzeń (CSV).</summary>
    public string HistoryFile { get; init; } = "history.csv";

    /// <summary>Ile dni przechowywać pliki dziennika, zanim zostaną usunięte.</summary>
    public int LogRetentionDays { get; init; } = 14;

    public int CaptureRetentionDays { get; init; } = 3;

    /// <summary>
    /// Twardy limit miejsca zajmowanego przez katalog zrzutów (MB). Po usunięciu plików przeterminowanych
    /// porządkowanie kasuje najstarsze zrzuty, dopóki suma przekracza ten limit — żaden próg czasu nie
    /// chroni dysku w nietypowym dniu (np. migoczący pasek statusu HMI). Dozwolony zakres 20–5000 MB.
    /// Plik wskazywany przez otwarty alert („Pokaż zrzut”) nigdy nie jest usuwany.
    /// </summary>
    public int CapturesMaxMegabytes { get; init; } = 200;
}
