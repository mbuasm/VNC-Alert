using System.Globalization;

namespace MaszynyAlert.Windows;

public static class WatchdogTask
{
    /// <summary>Nazwa zadania w Harmonogramie zadań; ta sama w instalatorze i przy odinstalowaniu.</summary>
    public const string TaskName = "VNC Alert watchdog";

    /// <summary>Tryb startu dla watchdoga: przy działającym programie kończy się cicho, bez okna komunikatu.</summary>
    public const string Argument = "--if-not-running";

    /// <summary>Jak długo po komendzie zatrzymania watchdog ma nie wznawiać programu.</summary>
    public static readonly TimeSpan SuppressionWindow = TimeSpan.FromMinutes(10);

    /// <summary>Podkatalog danych zapisywalny dla użytkownika (ACL z <c>installer/Initialize-Data.ps1</c>).</summary>
    private const string StateDirectoryName = "state";

    private const string MarkerFileName = "stop.requested";

    public static string MarkerPath(string dataDir) =>
        Path.Combine(dataDir, StateDirectoryName, MarkerFileName);

    public static string SystemMarkerPath(string dataDir) =>
        Path.Combine(MaszynyAlert.Core.Updates.UpdateTaskContract.AdminDirectory(dataDir), MarkerFileName);

    /// <summary>
    /// Zapisuje znacznik zatrzymania. Najlepszy wysiłek: brak prawa zapisu nie może przerwać
    /// komendy <c>--stop</c> — gorszym skutkiem jest tylko powrót programu po minucie.
    /// </summary>
    /// <param name="asSystem">Komendę wydał proces SYSTEM — znacznik idzie do <see cref="SystemMarkerPath"/>, nie do <c>state</c>.</param>
    public static void Suppress(string dataDir, DateTimeOffset now, bool asSystem = false)
    {
        try
        {
            string path = asSystem ? SystemMarkerPath(dataDir) : MarkerPath(dataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Czy watchdog ma teraz milczeć: świeży znacznik operatora (<c>state</c>) albo instalatora jako SYSTEM
    /// (<c>updates-admin</c>). Nieczytelny albo pusty znacznik znaczy „nie wiadomo”, czyli
    /// NIE blokuje wznowienia: uszkodzony plik nie może na stałe wyłączyć ochrony. Znacznik z
    /// przyszłości (przestawiony zegar) liczy się jak świeży, ale tylko w tym samym oknie czasu.
    /// </summary>
    public static bool IsSuppressed(string dataDir, DateTimeOffset now) =>
        IsFresh(MarkerPath(dataDir), now) || IsFresh(SystemMarkerPath(dataDir), now);

    private static bool IsFresh(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return false;
            string content = File.ReadAllText(path).Trim();
            if (!DateTimeOffset.TryParse(content, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var requested))
                return false;
            return (now - requested).Duration() < SuppressionWindow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Kasuje znacznik: operator sam uruchomił program, więc watchdog znów ma go pilnować.
    /// Wołane przy zwykłym starcie, nigdy przy starcie z <see cref="Argument"/>. Znacznika instalatora jako SYSTEM
    /// (<see cref="SystemMarkerPath"/>) konto operatora nie usunie — zdejmuje go zadanie aktualizacji, a działającemu już
    /// programowi i tak nie przeszkadza (watchdog przy działającym programie nic nie robi).
    /// </summary>
    public static void ClearSuppression(string dataDir)
    {
        try
        {
            string path = MarkerPath(dataDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    public static bool ShouldStart(string dataDir, DateTimeOffset now, string mutexName = StopRequest.AppMutexName) =>
        !StopRequest.IsRunning(mutexName) && !IsSuppressed(dataDir, now);
}
