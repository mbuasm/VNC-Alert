using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class ChunkEnvelopeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    private static readonly GroupIdentity Group = new()
    {
        GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111"), GroupName = "Hala",
        KeyId = Guid.Parse("22222222-2222-2222-2222-222222222222"), ProtocolVersion = 2, ProfileRevision = 3, ProfileHash = "abc",
    };

    private const string DowntimeReply0_4_113 =
        """{"type":"downtimeReply","requestId":"33333333-3333-3333-3333-333333333333","replyTo":"PSZEMEK","episodes":[{"machineId":"aidite2","machineName":"Aidite 2","reason":"Servo \u003C6\u003E \u0105","startedAt":"2026-10-05T08:00:00+00:00","lastObservedAt":"2026-10-05T08:05:00+00:00","endedAt":"2026-10-05T08:05:00+00:00","interrupted":false,"episodeId":"44444444-4444-4444-4444-444444444444","origin":"AIDITEKOMP1","end":"2026-10-05T08:05:00+00:00"}],"more":true,"cursor":"2026-10-05T08:05:00+00:00","group":{"groupId":"11111111-1111-1111-1111-111111111111","groupName":"Hala","keyId":"22222222-2222-2222-2222-222222222222","protocolVersion":2,"profileRevision":3,"profileHash":"abc"},"sender":"AIDITEKOMP1","seq":7,"sentAt":"2026-10-05T08:00:00+00:00","version":"0.4.113","instanceId":"0123456789abcdef0123456789abcdef"}""";

    private static DowntimeEpisode Episode(string reason, int minute) =>
        new("aidite2", "Aidite 2", reason, T0.AddMinutes(minute), T0.AddMinutes(minute + 5), T0.AddMinutes(minute + 5), false,
            Guid.Parse("44444444-4444-4444-4444-" + minute.ToString("D12")), "AIDITEKOMP1");

    [Fact]
    public void DowntimeReplyWireFormatIsByteForByteTheSameAsIn0_4_113()
    {
        PeerMessage reply = new DowntimeSyncReply
        {
            Sender = "AIDITEKOMP1", Version = "0.4.113", Group = Group, Seq = 7, SentAt = T0,
            InstanceId = "0123456789abcdef0123456789abcdef",
            RequestId = Guid.Parse("33333333-3333-3333-3333-333333333333"), ReplyTo = "PSZEMEK",
            Episodes = [Episode("Servo <6> ą", 0) with { EpisodeId = Guid.Parse("44444444-4444-4444-4444-444444444444") }],
            More = true, Cursor = T0.AddMinutes(5),
        };

        Assert.Equal(DowntimeReply0_4_113, JsonSerializer.Serialize(reply, MessageCodec.JsonOptions));
        // I w drugą stronę: pakiet w formacie 0.4.113 dekoduje się do tej samej wiadomości.
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        var clock = new FakeClock(T0);
        Assert.True(MessageCodec.TryDecode(signer.Seal(System.Text.Encoding.UTF8.GetBytes(DowntimeReply0_4_113)), signer, clock,
            TimeSpan.FromMinutes(5), out var decoded, out var reason), reason);
        var episodes = Assert.IsType<DowntimeSyncReply>(decoded).Episodes;
        Assert.Equal("Servo <6> ą", Assert.Single(episodes).Reason);
    }

    /// <summary>
    /// Postoje z tekstem escapowanym przez System.Text.Json (<c>ą</c> → 6 B, <c>&lt;</c> → 6 B) dają porcje ograniczone
    /// bajtami, nie limitem 256 wpisów. Każda porcja po ostemplowaniu przez bus mieści się w datagramie i dekoduje się
    /// w całości.
    /// </summary>
    [Theory]
    [InlineData('ą')]
    [InlineData('<')]
    [InlineData('a')]
    public void DowntimePortionsWithEscapedTextFitInOneDatagramAfterStamping(char letter)
    {
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        var clock = new FakeClock(T0.AddDays(1));
        var failures = new List<string>();
        for (int length = 400; length <= 500; length += 3)
        {
            var episodes = Enumerable.Range(0, 120).Select(i => Episode(new string(letter, length - i % 7), i)).ToArray();
            var chunks = MessageCodec.Chunk(episodes, (items, more) => new DowntimeSyncReply
            {
                Sender = "DESKTOP-CD8GBG4", Version = "0.4.114-pr118", Group = Group,
                RequestId = Guid.NewGuid(), ReplyTo = "AIDITEKOMP2", Episodes = [.. items], More = more,
                Cursor = items.Count > 0 ? items[^1].LastObservedAt : null,
            }, 256);
            int received = 0;
            foreach (var chunk in chunks)
            {
                var stamped = chunk with { Seq = long.MaxValue, SentAt = clock.UtcNow.AddTicks(1_234_567), InstanceId = Guid.NewGuid().ToString("N") };
                try
                {
                    var datagram = MessageCodec.Encode(stamped, signer);
                    Assert.True(MessageCodec.TryDecode(datagram, signer, clock, TimeSpan.FromMinutes(5), out var decoded, out var why), why);
                    received += ((DowntimeSyncReply)decoded!).Episodes.Count;
                }
                catch (InvalidDataException) { failures.Add($"długość {length}: porcja {chunk.Episodes.Count} wpisów"); }
            }
            if (received != episodes.Length && failures.Count == 0) failures.Add($"długość {length}: dotarło {received}/{episodes.Length}");
        }

        Assert.Empty(failures);
    }

    /// <summary>Podział zmienia tylko granice porcji: kolejność i komplet elementów, „More” na wszystkich porcjach poza ostatnią.</summary>
    [Fact]
    public void ChunkKeepsEveryItemInOrderAndMarksAllButTheLastPortion()
    {
        var episodes = Enumerable.Range(0, 300).Select(i => Episode(new string('ą', 450), i)).ToArray();
        var chunks = MessageCodec.Chunk(episodes, (items, more) => new DowntimeSyncReply
        {
            Sender = "A", Version = "t", Group = Group, Episodes = [.. items], More = more,
        }, 256);

        Assert.True(chunks.Count > 1);
        Assert.Equal(episodes, chunks.SelectMany(c => c.Episodes));
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.True(c.More));
        Assert.False(chunks[^1].More);
    }

    /// <summary>Pusta lista nadal daje jedną pustą wiadomość („nic nie mam”).</summary>
    [Fact]
    public void EmptyListStillGivesOneEmptyMessage()
    {
        var chunks = MessageCodec.Chunk(Array.Empty<ObservationInterval>(), (items, more) => new ObservationSyncReply
        {
            Sender = "A", Version = "t", Group = Group, Intervals = [.. items], More = more,
        });

        Assert.Empty(Assert.Single(chunks).Intervals);
        Assert.False(chunks[0].More);
    }
}
