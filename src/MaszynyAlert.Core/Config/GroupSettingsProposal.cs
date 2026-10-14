namespace MaszynyAlert.Core.Config;

/// <summary>Immutable preparation for the future admin apply operation. Construction has no side effects.</summary>
public sealed class GroupSettingsProposal
{
    private readonly byte[] _invitation;
    private readonly ClusterConfig _localCluster;
    private readonly bool _enabled;

    public GroupSettingsProposal(GroupFile invitation, ClusterConfig localCluster, bool enabled)
    {
        _invitation = GroupFileCodec.Serialize(invitation);
        _localCluster = GroupFileCodec.Clone(localCluster);
        _enabled = enabled;
    }

    /// <summary>The same invitation and local settings with the LAN interface chosen in the wizard. The address is
    /// local to this computer (never part of the invitation) and is saved with the group settings on apply.</summary>
    public GroupSettingsProposal WithLocalAddress(string? localAddress)
        => new(GroupFileCodec.Parse(_invitation), _localCluster with { LocalAddress = localAddress }, _enabled);

    public GroupSettingsData GetData()
    {
        var file = GroupFileCodec.Parse(_invitation);
        return new GroupSettingsData
        {
            Profile = file.Profile,
            Cluster = _localCluster with
            {
                Enabled = _enabled, Group = file.Identity, UdpPort = file.UdpPort, Peers = file.Peers
            }
        };
    }

    public GroupProfileDifference CompareWith(GroupProfile local)
    {
        var remote = GetData().Profile;
        var before = local.Machines.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var after = remote.Machines.ToDictionary(m => m.Id, StringComparer.Ordinal);
        return new(
            GroupFileCodec.ProfileHash(local) != GroupFileCodec.ProfileHash(remote),
            !local.Instructions.OrderBy(p => p.Key, StringComparer.Ordinal)
                .SequenceEqual(remote.Instructions.OrderBy(p => p.Key, StringComparer.Ordinal)),
            after.Keys.Except(before.Keys).ToArray(), before.Keys.Except(after.Keys).ToArray(),
            after.Keys.Intersect(before.Keys).Where(id => before[id] != after[id]).ToArray(),
            after.Values.Where(m => m.Enabled && (!before.TryGetValue(m.Id, out var old) || !old.Enabled)).Select(m => m.Id).ToArray(),
            before.Values.Where(m => m.Enabled && (!after.TryGetValue(m.Id, out var next) || !next.Enabled)).Select(m => m.Id).ToArray());
    }

    internal string NetworkKey => GroupFileCodec.Parse(_invitation).NetworkKey;
    public GroupFile ExportInvitation() => GroupFileCodec.Parse(_invitation);
    public override string ToString() => "GroupSettingsProposal { [secret invitation] }";
}

public sealed record GroupProfileDifference(bool ProfileChanged, bool InstructionsChanged, IReadOnlyList<string> AddedMachines,
    IReadOnlyList<string> RemovedMachines, IReadOnlyList<string> ChangedMachines,
    IReadOnlyList<string> EnabledMachines, IReadOnlyList<string> DisabledMachines);

/// <summary>Public part of one settings generation. The key lives exclusively in ISecretStore.</summary>
public sealed record GroupSettingsData
{
    public required GroupProfile Profile { get; init; }
    public required ClusterConfig Cluster { get; init; }

    public AppConfig ApplyTo(AppConfig local) => Profile.ApplyTo(local) with { Cluster = GroupFileCodec.Clone(Cluster) };

    /// <summary>
    /// Odcisk profilu wyliczony z profilu zapisanego w tej generacji — patrz <see cref="GroupFileCodec.Renew"/>.
    /// Zapisane ustawienia grupy przeżywają dzięki temu aktualizację programu, która dokłada pole do profilu.
    /// </summary>
    internal GroupSettingsData Renewed()
    {
        if (Cluster.Group is not { } identity) return this;
        string hash = GroupFileCodec.ProfileHash(Profile);
        return string.Equals(identity.ProfileHash, hash, StringComparison.OrdinalIgnoreCase)
            ? this : this with { Cluster = Cluster with { Group = identity with { ProfileHash = hash } } };
    }

    internal void Validate(string networkKey)
    {
        GroupFileCodec.Validate(new GroupFile
        {
            SchemaVersion = GroupFile.CurrentSchemaVersion, Identity = Cluster.Group
                ?? throw new InvalidDataException("Brak tożsamości zapisanej grupy."),
            NetworkKey = networkKey, UdpPort = Cluster.UdpPort, Peers = Cluster.Peers, Profile = Profile
        });
        // Even disabled groups must be ready to re-enable, without deferring validation to the runtime.
        ConfigLoader.Validate(ApplyTo(new AppConfig()) with { Cluster = Cluster with { Enabled = true } });
    }
}
