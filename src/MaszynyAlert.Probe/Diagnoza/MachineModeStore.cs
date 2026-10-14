using System.Text.Encodings.Web;
using System.Text.Json;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>Tryb połączenia maszyny w kolektorze: trwale (jeden klient przez cały czas) albo odpytywanie (połącz na chwilę, rozłącz).</summary>
public static class MachineMode
{
    public const string Trwale = "trwale";
    public const string Odpytywanie = "odpytywanie";
}

public static class MachineModeStore
{
    public const string FileName = "tryb-maszyn.json";

    public const string PollingProfile = "aidite5x";

    public sealed record Entry(string Tryb, string Powod);

    public static IReadOnlyDictionary<string, Entry> Load(string outDir)
    {
        string path = Path.Combine(outDir, FileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    public static void Save(string outDir, IReadOnlyDictionary<string, Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, FileName), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    public static Dictionary<string, Entry> FromResults(IEnumerable<MachineDiagnosticResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (MachineDiagnosticResult r in results)
        {
            if (string.Equals(r.Profile, PollingProfile, StringComparison.OrdinalIgnoreCase))
            {
                map[r.Id] = new Entry(MachineMode.Odpytywanie, "profil aidite5x (520): serwer Qt przyjmuje jednego klienta naraz");
            }
            else if (r.SharedTest is { DrugiPolaczony: true, PierwszyPrzezyl: false })
            {
                map[r.Id] = new Entry(MachineMode.Odpytywanie, "shared-test: nowe połączenie zrywa poprzednie");
            }
            else if (r.SharedTest is { DrugiPolaczony: true, PierwszyPrzezyl: true })
            {
                map[r.Id] = new Entry(MachineMode.Trwale, "shared-test: serwer znosi dwóch klientów");
            }
        }

        return map;
    }

    public static string Resolve(MachineConfig machine, IReadOnlyDictionary<string, Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(entries);

        if (string.Equals(machine.Profile, PollingProfile, StringComparison.OrdinalIgnoreCase))
        {
            return MachineMode.Odpytywanie;
        }

        return entries.TryGetValue(machine.Id, out Entry? e) && string.Equals(e.Tryb, MachineMode.Odpytywanie, StringComparison.OrdinalIgnoreCase)
            ? MachineMode.Odpytywanie
            : MachineMode.Trwale;
    }
}
