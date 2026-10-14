using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Probe;

/// <summary>
/// Wczytana konfiguracja programu razem ze ścieżką pliku, z którego pochodzi (do wypisania operatorowi),
/// i listą maszyn WYBRANYCH do tego przebiegu (<see cref="Selected"/>): bez <c>--machines</c>/<c>--all</c>
/// to maszyny <c>enabled:true</c>; z <c>--machines</c> — dokładnie wskazane (także wyłączone); z <c>--all</c> —
/// wszystkie z listy. <see cref="FilterActive"/> mówi, czy operator sam wybierał (wtedy <c>enabled:false</c>
/// nie jest powodem pominięcia).
/// </summary>
public sealed record MachineListResult(AppConfig Config, string SourcePath, IReadOnlyList<MachineConfig> Selected, bool FilterActive)
{
    /// <summary>Prawda, gdy maszyna należy do wybranych w tym przebiegu.</summary>
    public bool IsSelected(MachineConfig machine) => Selected.Any(m => ReferenceEquals(m, machine) || string.Equals(m.Id, machine.Id, StringComparison.Ordinal));
}

/// <summary>
/// Wczytuje listę maszyn: plik z <c>--config</c>, a bez niego <c>config.json</c> obok <c>probe.exe</c>, jeśli
/// istnieje, w przeciwnym razie <c>config.example.json</c> obok <c>probe.exe</c> (kopiowany do katalogu wyjściowego
/// publikacji przez MaszynyAlert.Probe.csproj). Katalog exe (nie katalog bieżący!) — użytkownik uruchamia program z
/// <c>C:\probe</c>, a exe też tam jest, więc w normalnym użyciu to ta sama lokalizacja.
/// </summary>
public static class MachineList
{
    public static MachineListResult Load() => Load(new GlobalOptions("out", null, false));

    /// <summary>Wczytuje konfigurację i wybiera maszyny według <paramref name="global"/> (<c>--config</c>, <c>--machines</c>, <c>--all</c>).</summary>
    public static MachineListResult Load(GlobalOptions global)
    {
        ArgumentNullException.ThrowIfNull(global);

        string path = ResolvePath(global.ConfigPath);
        AppConfig cfg = ConfigLoader.LoadAppConfig(path);
        ConfigLoader.Validate(cfg);
        return Select(cfg, path, global);
    }

    /// <summary>Wybór maszyn z już wczytanej konfiguracji — wydzielony, żeby dało się go sprawdzić testem bez pliku na dysku.</summary>
    public static MachineListResult Select(AppConfig cfg, string sourcePath, GlobalOptions global)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(global);

        if (global.Machines is { Count: > 0 } ids)
        {
            var selected = new List<MachineConfig>();
            var unknown = new List<string>();
            foreach (string id in ids)
            {
                MachineConfig? machine = cfg.Machines.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
                if (machine is null)
                {
                    unknown.Add(id);
                }
                else if (!selected.Contains(machine))
                {
                    selected.Add(machine);
                }
            }

            if (unknown.Count > 0)
            {
                throw new ProbeUsageException(
                    $"Opcja --machines wskazuje maszyny, których nie ma w konfiguracji ({sourcePath}): {string.Join(", ", unknown)}. " +
                    $"Dostępne identyfikatory: {string.Join(", ", cfg.Machines.Select(m => m.Id))}.");
            }

            return new MachineListResult(cfg, sourcePath, selected, FilterActive: true);
        }

        if (global.All)
        {
            return new MachineListResult(cfg, sourcePath, cfg.Machines.ToList(), FilterActive: true);
        }

        return new MachineListResult(cfg, sourcePath, cfg.Machines.Where(m => m.Enabled).ToList(), FilterActive: false);
    }

    private static string ResolvePath(string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            string full = Path.GetFullPath(configPath);
            if (!File.Exists(full))
            {
                throw new ProbeUsageException($"Nie znaleziono pliku konfiguracyjnego wskazanego przez --config: {full}");
            }

            return full;
        }

        string baseDir = AppContext.BaseDirectory;
        string defaultPath = Path.Combine(baseDir, "config.json");
        string examplePath = Path.Combine(baseDir, "config.example.json");

        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        if (File.Exists(examplePath))
        {
            return examplePath;
        }

        throw new ProbeUsageException(
            $"Nie znaleziono pliku konfiguracyjnego — brak \"config.json\" i \"config.example.json\" w folderze programu ({baseDir}). " +
            "Sprawdź, czy rozpakowałeś/aś cały plik probe-<wersja>.zip (artefakt probe z GitHub Actions) do jednego folderu, " +
            "albo wskaż plik opcją --config <plik>.");
    }
}
