using System.Text.Json;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class SharedConfigCodecTests
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static HmacSigner Signer() => new(HmacSigner.GenerateKey());

    [Fact]
    public void EncodeIDecode_WspolnejKonfiguracji_ZachowujaCalyDokumentIOdcisk()
    {
        var clock = new FakeClock();
        var signer = Signer();
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig { AlertSoundRepeatMs = 30000, IdleAtZeroAfterMs = 240000 },
            Overlay = new OverlayConfig { Enabled = true, Opacity = 0.8, Scale = 1.4, Position = OverlayPosition.BottomRight },
            EditedAt = clock.UtcNow,
            EditedBy = "PC-A",
        };
        var message = new SharedConfigMessage { Sender = "PC-A", Version = "test", SentAt = clock.UtcNow, Document = document };

        byte[] datagram = MessageCodec.Encode(message, signer);

        // Dokument ma się mieścić w jednym datagramie z ogromnym zapasem — stąd brak porcjowania.
        Assert.True(datagram.Length < 4096, $"Pakiet wspólnej konfiguracji ma {datagram.Length} B.");
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason), reason);
        var received = Assert.IsType<SharedConfigMessage>(decoded);
        // Odcisk jest tożsamością całego dokumentu — i to on musi przeżyć drogę przez sieć.
        Assert.Equal(document.Hash, received.Document.Hash);
        Assert.Equal(30000, received.Document.Timing!.AlertSoundRepeatMs);
        Assert.Equal(240000, received.Document.Timing.IdleAtZeroAfterMs);
        Assert.Equal("PC-A", received.Document.EditedBy);
        Assert.Equal(OverlayPosition.BottomRight, received.Document.Overlay!.Position);
    }

    [Fact]
    public void EncodeIDecode_PrzelacznikowMaszyn_PrzenoszaJePrzezSiec()
    {
        var clock = new FakeClock();
        var signer = Signer();
        var document = new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(new Dictionary<string, bool> { ["amm580"] = true, ["cameo1"] = false }),
            EditedAt = clock.UtcNow, EditedBy = "PC-A",
        };
        var message = new SharedConfigMessage { Sender = "PC-A", Version = "test", SentAt = clock.UtcNow, Document = document };

        byte[] datagram = MessageCodec.Encode(message, signer);

        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason), reason);
        var received = Assert.IsType<SharedConfigMessage>(decoded);
        Assert.True(received.Document.Machines!["amm580"].Enabled);
        Assert.False(received.Document.Machines["cameo1"].Enabled);
        Assert.False(received.Document.IsEmpty);
        Assert.Equal(document.Hash, received.Document.Hash);
    }

    [Fact]
    public void EncodeIDecode_PustegoDokumentu_ZachowujaPrzywrocenieWartosciZConfigJson()
    {
        var clock = new FakeClock();
        var signer = Signer();
        var document = new SharedConfigDocument { EditedAt = clock.UtcNow, EditedBy = "PC-A" };
        var message = new SharedConfigMessage { Sender = "PC-A", Version = "test", SentAt = clock.UtcNow, Document = document };

        byte[] datagram = MessageCodec.Encode(message, signer);

        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason), reason);
        var received = Assert.IsType<SharedConfigMessage>(decoded);
        Assert.True(received.Document.IsEmpty);
        Assert.Equal(document.Hash, received.Document.Hash);
    }

    [Fact]
    public void Heartbeat_BezWspolnejKonfiguracji_NieNiesiePolINowszegoProtokolu()
    {
        // Dlaczego: pola o wspólnej konfiguracji są opcjonalne. Komputer, który jej nie używa, ma wysyłać
        // dokładnie taki pakiet jak przed zmianą, a odbiorca starszej wersji nie może się zakrztusić polem,
        // którego nie zna.
        var signer = Signer();
        var heartbeat = new HeartbeatMessage { Sender = "PC-A", Version = "test", Readiness = new(true, false, []) };

        byte[] datagram = MessageCodec.Encode(heartbeat, signer);
        string json = JsonSerializer.Serialize<PeerMessage>(heartbeat, MessageCodec.JsonOptions);
        Assert.DoesNotContain("sharedConfigHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sharedConfigEditedAt", json, StringComparison.Ordinal);

        Assert.True(MessageCodec.TryDecode(datagram, signer, new FakeClock(heartbeat.SentAt), Skew, out var decoded, out _));
        var received = Assert.IsType<HeartbeatMessage>(decoded);
        Assert.Null(received.SharedConfigHash);
        Assert.Null(received.SharedConfigEditedAt);
    }

    [Fact]
    public void Heartbeat_ZOdciskiemWspolnejKonfiguracji_PrzenosiGoWObieStrony()
    {
        var clock = new FakeClock();
        var signer = Signer();
        var heartbeat = new HeartbeatMessage
        {
            Sender = "PC-A", Version = "test", SentAt = clock.UtcNow, Readiness = new(true, false, []),
            SharedConfigHash = new string('a', 64), SharedConfigEditedAt = clock.UtcNow,
        };

        byte[] datagram = MessageCodec.Encode(heartbeat, signer);

        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out var decoded, out string? reason), reason);
        var received = Assert.IsType<HeartbeatMessage>(decoded);
        Assert.Equal(heartbeat.SharedConfigHash, received.SharedConfigHash);
        Assert.Equal(heartbeat.SharedConfigEditedAt, received.SharedConfigEditedAt);
    }
}
