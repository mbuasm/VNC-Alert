using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Cluster;

/// <summary>Public identity of a group. Never contains the network key.</summary>
public sealed record GroupIdentity
{
    public const int CurrentProtocolVersion = 2;
    public required Guid GroupId { get; init; }
    public required string GroupName { get; init; }
    public required Guid KeyId { get; init; }
    public required int ProtocolVersion { get; init; }
    public required long ProfileRevision { get; init; }
    public required string ProfileHash { get; init; }

    public void Validate()
    {
        if (GroupId == Guid.Empty || KeyId == Guid.Empty)
            throw new ConfigException("Grupa i generacja klucza muszą mieć niepuste identyfikatory UUID.");
        if (string.IsNullOrWhiteSpace(GroupName) || GroupName.Length > 100 || GroupName.Any(char.IsControl))
            throw new ConfigException("Nazwa grupy musi mieć od 1 do 100 znaków bez znaków sterujących.");
        if (ProtocolVersion != CurrentProtocolVersion)
            throw new ConfigException("Nieobsługiwana wersja protokołu grupy.");
        if (ProfileRevision < 1 || ProfileHash is null || ProfileHash.Length != 64
            || ProfileHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ConfigException("Nieprawidłowa rewizja lub suma SHA-256 profilu grupy.");
    }

    public GroupCompatibility Compare(GroupIdentity other)
    {
        if (GroupId != other.GroupId) return GroupCompatibility.OtherGroup;
        if (ProtocolVersion != other.ProtocolVersion) return GroupCompatibility.OtherProtocol;
        if (KeyId != other.KeyId) return GroupCompatibility.OtherKey;
        if (ProfileRevision != other.ProfileRevision
            || !string.Equals(ProfileHash, other.ProfileHash, StringComparison.OrdinalIgnoreCase))
            return GroupCompatibility.OtherProfile;
        return GroupCompatibility.Compatible;
    }
}

public enum GroupCompatibility { Compatible, OtherGroup, OtherProtocol, OtherKey, OtherProfile }
