namespace MaszynyAlert.Windows;

public static class AppPaths
{
    private const string AppFolderName = "MaszynyAlert";
    private const string ConfigFileName = "config.json";
    private const string RulesFileName = "rules.json";
    private const string SecretsFileName = "secrets.dat";
    private const string ConfigExampleFileName = "config.example.json";
    private const string RulesExampleFileName = "rules.example.json";

    /// <summary>Katalog, w którym leży uruchomiony plik .exe.</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>
    /// Czy program działa jako wersja "zainstalowana": .exe leży pod %ProgramFiles%/%ProgramFiles(x86)%,
    /// z uwzględnieniem granicy katalogu. Pozostałości danych wcześniejszej instalacji nie zmieniają
    /// trybu wersji portable ani zakresu DPAPI.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            return IsInstalledLocation(ExeDir, programFiles, programFilesX86);
        }
    }

    internal static bool IsInstalledLocation(string exeDir, params string[] programFilesRoots)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDir));
        return programFilesRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root =>
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return directory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Katalog danych aplikacji: zainstalowana → %ProgramData%\MaszynyAlert; portable → katalog .exe.</summary>
    public static string DataDir => IsInstalled ? ProgramDataAppDir : ExeDir;

    /// <summary>Katalog danych użytkownika (profil) — hasła/klucz sieciowy w wersji portable; zawsze dostępny bez admina.</summary>
    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    /// <summary>Ścieżka pliku config.json.</summary>
    public static string ConfigPath => Path.Combine(DataDir, ConfigFileName);
    public static string GroupSettingsDir => Path.Combine(DataDir, "group-settings");

    /// <summary>
    /// Domyślna ścieżka pliku rules.json. Jeśli <c>AppConfig.RulesFile</c> wskazuje inną nazwę, jej rzeczywistą
    /// ścieżkę wylicza <see cref="Resolve"/>.
    /// </summary>
    public static string RulesPath => Path.Combine(DataDir, RulesFileName);

    /// <summary>
    /// Ścieżka pliku z zaszyfrowanymi sekretami (hasła VNC, klucz sieciowy): zainstalowana → DataDir\secrets.dat
    /// (DPAPI LocalMachine); portable → UserDataDir\secrets.dat (DPAPI CurrentUser, bez uprawnień admina).
    /// </summary>
    public static string SecretsPath => IsInstalled
        ? Path.Combine(DataDir, SecretsFileName)
        : Path.Combine(UserDataDir, SecretsFileName);

    private static string ProgramDataAppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName);

    /// <summary>
    /// Rozwiązuje ścieżkę z sekcji "paths" pliku config.json (np. <c>PathsConfig.LogsDir</c> albo
    /// <c>AppConfig.RulesFile</c>) względem <see cref="DataDir"/>; ścieżkę już bezwzględną zwraca bez zmian.
    /// </summary>
    public static string Resolve(string relativeOrAbsolutePath)
    {
        ArgumentNullException.ThrowIfNull(relativeOrAbsolutePath);
        return Path.IsPathRooted(relativeOrAbsolutePath) ? relativeOrAbsolutePath : Path.Combine(DataDir, relativeOrAbsolutePath);
    }

    /// <summary>
    /// Kopiuje domyślne pliki (config.example.json → config.json, rules.example.json → rules.json) z
    /// <see cref="ExeDir"/> do <see cref="DataDir"/>, jeśli docelowy plik jeszcze nie istnieje; tworzy
    /// <see cref="DataDir"/> i <see cref="UserDataDir"/>.
    /// </summary>
    public static void EnsureDefaultsCopied()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(UserDataDir);

        CopyIfMissing(Path.Combine(ExeDir, ConfigExampleFileName), ConfigPath);
        CopyIfMissing(Path.Combine(ExeDir, RulesExampleFileName), RulesPath);
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (File.Exists(destination) || !File.Exists(source))
        {
            return;
        }

        File.Copy(source, destination, overwrite: false);
    }
}
