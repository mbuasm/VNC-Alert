using System.Text;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>Protokół v2 wymaga jawnego UUID epizodu w STATE; brak pola nie może stać się losowym, „nowym” epizodem.</summary>
public sealed class GroupStateUuidTests
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static StateMessage State(FakeClock clock, bool group) => new()
    {
        Sender = "PC-A", Version = "test", Seq = 1, SentAt = clock.UtcNow, InstanceId = Guid.NewGuid().ToString(),
        Group = group ? GroupFileTests.Invitation().Identity : null,
        Alerts = [new Alert
        {
            Key = new AlertKey("aidite1", "rule:code-705"), MachineId = "aidite1", MachineName = "Aidite 1",
            Kind = AlertKind.Error, ShortText = "Servo 6", Category = "restart", Status = AlertStatus.Active,
            StartedAt = clock.UtcNow, LastSeenAt = clock.UtcNow, Sound = true,
        }],
    };

    private static byte[] WithoutEpisodeId(byte[] datagram, HmacSigner signer)
    {
        Assert.True(signer.TryOpen(datagram, out byte[] payload, out _));
        JsonObject root = JsonNode.Parse(payload)!.AsObject();
        JsonObject alert = root["alerts"]!.AsArray()[0]!.AsObject();
        Assert.True(alert.Remove("episodeId"), "Pole 'episodeId' nie jest serializowane — test straciłby sens.");
        return signer.Seal(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    [Fact]
    public void GroupState_WithEpisodeId_IsAccepted()
    {
        var clock = new FakeClock(); var signer = new HmacSigner(HmacSigner.GenerateKey());
        byte[] datagram = MessageCodec.Encode(State(clock, group: true), signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason), reason);
        Assert.NotEqual(Guid.Empty, Assert.Single(Assert.IsType<StateMessage>(decoded).Alerts).EpisodeId);
    }

    [Fact]
    public void GroupState_WithoutEpisodeId_IsRejectedInsteadOfInventingOne()
    {
        var clock = new FakeClock(); var signer = new HmacSigner(HmacSigner.GenerateKey());
        byte[] datagram = WithoutEpisodeId(MessageCodec.Encode(State(clock, group: true), signer), signer);
        Assert.False(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason));
        Assert.Null(decoded);
        Assert.Contains("UUID", reason);
    }

    [Fact]
    public void LegacyState_WithoutEpisodeId_StillDecodesForCompatibility()
    {
        var clock = new FakeClock(); var signer = new HmacSigner(HmacSigner.GenerateKey());
        byte[] datagram = WithoutEpisodeId(MessageCodec.Encode(State(clock, group: false), signer), signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason), reason);
        Assert.IsType<StateMessage>(decoded);
    }
}
