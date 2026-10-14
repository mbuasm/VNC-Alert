using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Cluster;

/// <summary>Candidate contract for protocol v2; an offline machine is not a configuration failure.</summary>
public sealed record WatcherReadiness(bool CanObserve, bool OcrLimited, IReadOnlyList<string> MissingPasswordMachineIds)
{
    public static WatcherReadiness Evaluate(AppConfig config, Func<string, bool> hasPassword,
        bool ocrAvailable, GroupCompatibility compatibility)
    {
        var enabled = config.Machines.Where(m => m.Enabled).ToArray();
        var missing = config.Enabled
            ? enabled.Where(m => m.RequiresPassword && !hasPassword(m.Id)).Select(m => m.Id).ToArray()
            : [];
        return new(config.Enabled && enabled.Length > 0 && missing.Length == 0
            && compatibility == GroupCompatibility.Compatible, !ocrAvailable, missing);
    }

    public static IReadOnlyList<string> MissingPasswordsAfterEnabling(AppConfig current, AppConfig next, Func<string, bool> hasPassword)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(hasPassword);
        var enabledBefore = current.Machines.Where(m => m.Enabled).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return Evaluate(next, hasPassword, ocrAvailable: true, GroupCompatibility.Compatible).MissingPasswordMachineIds
            .Where(id => !enabledBefore.Contains(id)).ToArray();
    }
}

/// <summary>v2 ACK/closure identity, independent of the current OCR code and process instance.</summary>
public sealed record GroupEpisodeId(Guid GroupId, string MachineId, Alerts.AlertKind Kind, Guid EpisodeId)
{
    public void Validate()
    {
        if (GroupId == Guid.Empty || EpisodeId == Guid.Empty || string.IsNullOrWhiteSpace(MachineId)
            || MachineId.Length > 100 || !Enum.IsDefined(Kind))
            throw new ConfigException("Nieprawidłowa tożsamość epizodu grupy.");
    }
}
