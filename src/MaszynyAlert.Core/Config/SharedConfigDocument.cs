using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Config;

public sealed record SharedConfigDocument
{
    /// <summary>Nadpisana sekcja <c>timing</c> albo <see langword="null"/>, gdy warstwa jej nie zmienia.</summary>
    public TimingConfig? Timing { get; init; }

    /// <summary>Nadpisana sekcja <c>ui.overlay</c> albo <see langword="null"/>, gdy warstwa jej nie zmienia.</summary>
    public OverlayConfig? Overlay { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UpdateApproval? UpdateApproval { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, SharedMachineSettings>? Machines { get; init; }

    /// <summary>Czas zapisania zmiany (UTC po stronie edytującego) — pierwsze kryterium <see cref="Wins"/>.</summary>
    public DateTimeOffset EditedAt { get; init; }

    /// <summary>Komputer, na którym zapisano zmianę — drugie kryterium <see cref="Wins"/> i informacja dla człowieka.</summary>
    public string EditedBy { get; init; } = "";

    [JsonIgnore]
    public bool IsEmpty => Timing is null && Overlay is null && Machines is null;

    /// <summary>Górny limit wpisów słownika maszyn — ochrona przed podmienionym, wielkim dokumentem z sieci.</summary>
    public const int MaxMachines = 1000;

    /// <summary>
    /// Słownik maszyn w postaci kanonicznej: klucze posortowane ordinalnie, żeby ten sam zestaw przełączników dawał ten sam
    /// odcisk niezależnie od kolejności maszyn w <c>config.json</c> komputera, na którym go zapisano.
    /// </summary>
    public static Dictionary<string, SharedMachineSettings> CanonicalMachines(IEnumerable<KeyValuePair<string, bool>> enabledById)
    {
        ArgumentNullException.ThrowIfNull(enabledById);
        var result = new Dictionary<string, SharedMachineSettings>(StringComparer.Ordinal);
        foreach (var (id, enabled) in enabledById.OrderBy(p => p.Key, StringComparer.Ordinal))
            result[id] = new SharedMachineSettings { Enabled = enabled };
        return result;
    }

    /// <summary>
    /// Wspólne ustawienia System.Text.Json dokumentu: camelCase i róg nakładki z myślnikiem
    /// (<c>"top-left"</c>) — dokładnie jak w <c>config.json</c>, żeby plik warstwy dawał się czytać i
    /// porównywać z konfiguracją stanowiska bez tłumaczenia nazw.
    /// </summary>
    internal static JsonSerializerOptions CanonicalJsonOptions { get; } = CreateCanonicalOptions();

    private static JsonSerializerOptions CreateCanonicalOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
        // Konwerter rogu nakładki musi być PRZED ogólnym (camelCase) — wygrywa pierwszy pasujący,
        // tak samo jak w ConfigLoader.
        options.Converters.Add(new JsonStringEnumConverter<OverlayPosition>(JsonNamingPolicy.KebabCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<OverlayLayout>(JsonNamingPolicy.KebabCaseLower));
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public bool IsValid()
    {
        if (EditedAt == default) return false;
        if (string.IsNullOrWhiteSpace(EditedBy) || EditedBy.Length > 100 || EditedBy.Any(char.IsControl)) return false;
        try
        {
            if (Timing is { } timing) ConfigLoader.ValidateTiming(timing);
            if (Overlay is { } overlay) ConfigLoader.ValidateOverlay(overlay);
        }
        catch (ConfigException) { return false; }
        if (Machines is { } machines)
        {
            if (machines.Count > MaxMachines) return false;
            foreach (var (id, settings) in machines)
            {
                if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(char.IsControl) || settings is null) return false;
            }
        }
        return UpdateApproval?.IsValid() != false;
    }

    /// <summary>
    /// Odcisk CAŁEGO dokumentu (razem z <see cref="EditedAt"/> i <see cref="EditedBy"/>): SHA-256 z
    /// kanonicznego JSON-a, hex małymi literami. Równy odcisk u obu komputerów znaczy „nie ma czego
    /// wysyłać”, dlatego heartbeat niesie go w każdym cyklu.
    /// </summary>
    [JsonIgnore]
    public string Hash => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, CanonicalJsonOptions))).ToLowerInvariant();

    public static bool Wins(SharedConfigDocument candidate, SharedConfigDocument? current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (current is null) return true;
        if (candidate.EditedAt != current.EditedAt) return candidate.EditedAt > current.EditedAt;
        int byHost = string.CompareOrdinal(candidate.EditedBy, current.EditedBy);
        if (byHost != 0) return byHost > 0;
        bool candidateApproval = candidate.UpdateApproval is not null, currentApproval = current.UpdateApproval is not null;
        if (candidateApproval != currentApproval) return candidateApproval;
        bool candidateMachines = candidate.Machines is not null, currentMachines = current.Machines is not null;
        if (candidateMachines != currentMachines) return candidateMachines;
        return string.CompareOrdinal(candidate.Hash, current.Hash) > 0;
    }

    /// <summary>
    /// Nakłada warstwę na konfigurację niższą (config.json + profil grupy). Puste pole zostawia wartość
    /// z <paramref name="baseConfig"/> — stąd „przywrócenie” to po prostu pusty dokument. Przełączniki maszyn
    /// (<see cref="Machines"/>) dają NOWĄ listę z kopiami zmienionych wpisów — wspólne obiekty <see cref="MachineConfig"/>
    /// z profilu grupy nie są mutowane (rekordy są niezmienne), a maszyny bez wpisu zostają tymi samymi obiektami.
    /// </summary>
    public AppConfig ApplyTo(AppConfig baseConfig)
    {
        ArgumentNullException.ThrowIfNull(baseConfig);
        return baseConfig with
        {
            Timing = Timing ?? baseConfig.Timing,
            Machines = ApplyMachines(baseConfig.Machines),
            Ui = baseConfig.Ui with
            {
                Overlay = Overlay ?? baseConfig.Ui.Overlay,
            },
        };
    }

    private List<MachineConfig> ApplyMachines(List<MachineConfig> baseMachines)
    {
        if (Machines is not { } machines) return baseMachines;
        var result = new List<MachineConfig>(baseMachines.Count);
        foreach (MachineConfig machine in baseMachines)
        {
            result.Add(machine is not null && machine.Id is not null && machines.TryGetValue(machine.Id, out SharedMachineSettings? settings)
                && settings is not null && settings.Enabled != machine.Enabled
                ? machine with { Enabled = settings.Enabled }
                : machine!);
        }
        return result;
    }
}
