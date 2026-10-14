using System.Security.Cryptography;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Core.Config;

/// <summary>Secret invitation, exported only by explicit admin action. Never log or diagnose this object.</summary>
public sealed record GroupFile
{
    public const int CurrentSchemaVersion = 1;
    public required int SchemaVersion { get; init; }
    public required GroupIdentity Identity { get; init; }
    public required string NetworkKey { get; init; }
    public required int UdpPort { get; init; }
    public required List<string> Peers { get; init; }
    public required GroupProfile Profile { get; init; }

    public static GroupFile Create(string name, GroupProfile profile, int udpPort = 47777)
    {
        var copy = GroupFileCodec.Clone(profile);
        var file = new GroupFile
        {
            SchemaVersion = CurrentSchemaVersion,
            Identity = new GroupIdentity
            {
                GroupId = Guid.NewGuid(), GroupName = name, KeyId = Guid.NewGuid(),
                ProtocolVersion = GroupIdentity.CurrentProtocolVersion, ProfileRevision = 1,
                ProfileHash = GroupFileCodec.ProfileHash(copy)
            },
            NetworkKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            UdpPort = udpPort, Peers = [], Profile = copy
        };
        GroupFileCodec.Validate(file);
        return file;
    }

    public GroupFile UpdateProfile(GroupProfile profile)
    {
        var copy = GroupFileCodec.Clone(profile);
        var updated = this with
        {
            Profile = copy,
            Identity = Identity with
            {
                ProfileRevision = checked(Identity.ProfileRevision + 1),
                ProfileHash = GroupFileCodec.ProfileHash(copy)
            }
        };
        GroupFileCodec.Validate(updated);
        return updated;
    }

    public GroupFile RotateKey() => this with
    {
        Identity = Identity with { KeyId = Guid.NewGuid() },
        NetworkKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    };

    public override string ToString() => "GroupFile { [secret invitation] }";
}
