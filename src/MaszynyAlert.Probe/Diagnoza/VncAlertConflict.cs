using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Probe.Diagnoza;

public static class VncAlertConflict
{
    public const string SafetyReason = "520 pominięta: pojedynczy klient VNC; także samo TCP może przerwać podgląd lub zmienić stan HMI. Dane z eksportu VNC Alert, bez nowych połączeń.";

    public static bool Is520(MachineConfig machine) =>
        string.Equals(machine.Profile, MachineModeStore.PollingProfile, StringComparison.OrdinalIgnoreCase)
        || machine.Id.StartsWith("amm520-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Domyślna ścieżka konfiguracji zainstalowanego VNC Alert.</summary>
    public static string DefaultInstalledConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MaszynyAlert", "config.json");

    public sealed record Skip(string Id, string Powod);

    /// <summary>
    /// Maszyny obserwowane przez zainstalowany VNC Alert (włączone w jego config.json), jako klucze <c>host:port</c> i <c>id</c>.
    /// Brak pliku, błąd składni albo brak uprawnień = pusta lista (nie pomijamy niczego).
    /// </summary>
    public static IReadOnlyCollection<string> WatchedByInstalledApp(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        try
        {
            if (!File.Exists(configPath))
            {
                return [];
            }

            AppConfig cfg = ConfigLoader.LoadAppConfig(configPath);
            if (!cfg.Enabled)
            {
                return [];
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MachineConfig m in cfg.Machines.Where(m => m.Enabled))
            {
                keys.Add(m.Id);
                keys.Add($"{m.Host}:{m.Port}");
            }

            return keys;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<Skip> SkipsForLongMode(IEnumerable<MachineConfig> machines, IReadOnlyCollection<string> watched, bool force)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(watched);

        var skips = new List<Skip>();
        foreach (MachineConfig m in machines)
        {
            if (Is520(m))
            {
                skips.Add(new Skip(m.Id, SafetyReason));
            }
        }

        return skips;
    }
}
