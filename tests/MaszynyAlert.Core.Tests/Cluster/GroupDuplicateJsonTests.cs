using System.Text;
using System.Text.Json;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class GroupDuplicateJsonTests
{
    [Theory]
    [InlineData("state", "seq")]
    [InlineData("state", "group")]
    [InlineData("state", "alerts")]
    [InlineData("state", "episodeId")]
    [InlineData("state", "machineId")]
    [InlineData("state", "kind")]
    [InlineData("state", "groupId")]
    [InlineData("ack", "episodeId")]
    [InlineData("ack", "machineId")]
    [InlineData("ack", "ackedAt")]
    [InlineData("ack", "group")]
    [InlineData("ack", "groupId")]
    public void DuplicateProperty_IsRejected(string type, string property)
    {
        var clock = new FakeClock(); var signer = new HmacSigner(HmacSigner.GenerateKey());
        var id = Guid.NewGuid();
        PeerMessage message = type == "state" ? new StateMessage
        {
            Sender = "B", Version = "test", Group = GroupFileTests.Invitation().Identity, SentAt = clock.UtcNow,
            Alerts = [new Alert { EpisodeId = id, MachineId = "aidite1", MachineName = "Aidite 1",
                Key = new("aidite1", "test"), Kind = AlertKind.Error, ShortText = "Error", Category = "inny",
                StartedAt = clock.UtcNow, LastSeenAt = clock.UtcNow }],
        } : new AckMessage
        {
            Sender = "B", Version = "test", Group = GroupFileTests.Invitation().Identity, SentAt = clock.UtcNow,
            EpisodeId = id, Key = new("aidite1", "test"), User = "operator", AckedAt = clock.UtcNow,
        };
        string json = JsonSerializer.Serialize(message, MessageCodec.JsonOptions);
        Assert.True(MessageCodec.TryDecode(signer.Seal(Encoding.UTF8.GetBytes(json)), signer, clock,
            TimeSpan.FromMinutes(5), out _, out _));
        using var document = JsonDocument.Parse(json);
        string? value = Find(document.RootElement, property);
        Assert.NotNull(value);
        string token = JsonSerializer.Serialize(property) + ":";
        int start = json.IndexOf(token, StringComparison.Ordinal);
        // Escaped spelling must also count as the same property. For 'group', the last value
        // removes v2 identity: detection must inspect the wire, not just the deserialized object.
        string duplicateName = "\"\\u" + ((int)property[0]).ToString("x4") + property[1..] + "\"";
        int end = start + token.Length + value.Length;
        json = json.Insert(end, "," + duplicateName + ":" + (property == "group" ? "null" : value));
        Assert.False(MessageCodec.TryDecode(signer.Seal(Encoding.UTF8.GetBytes(json)), signer, clock,
            TimeSpan.FromMinutes(5), out var decoded, out var reason));
        Assert.Null(decoded);
        Assert.NotEmpty(reason!);
    }

    private static string? Find(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name == name) return p.Value.GetRawText();
                if (Find(p.Value, name) is { } found) return found;
            }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (Find(item, name) is { } found) return found;
        return null;
    }
}
