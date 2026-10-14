using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class MessageCodecTests
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static HmacSigner Signer() => new(HmacSigner.GenerateKey());

    private static Alert SampleAlert(string machineId = "aidite1", string ruleId = "tray-grab") => new()
    {
        Key = AlertKey.ForRule(machineId, ruleId),
        MachineId = machineId,
        MachineName = "Aidite 1",
        Kind = AlertKind.Error,
        ShortText = "Nie udało się pobrać tacki",
        FullText = "Failed to grab a tray",
        Category = "tacka",
        StartedAt = FakeClock.DefaultStart,
        LastSeenAt = FakeClock.DefaultStart.AddSeconds(5),
        Status = AlertStatus.Active,
        Sound = true,
    };

    private static MachineStatusSnapshot SampleSnapshot(string machineId = "aidite1") => new()
    {
        MachineId = machineId,
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
        State = MachineConnectionState.Connected,
        LastRedFraction = 0.021,
        RedNow = true,
        RedConfirmed = true,
        LastOcrText = "Failed to grab a tray",
        LastShortText = "Nie udało się pobrać tacki",
        Info = new ScreenSourceInfo("vnc", "3.8", "None", "32bpp BGRA LE", 1024, 768, "OpenCnc"),
        Source = "vnc",
        UpdatedAt = FakeClock.DefaultStart,
    };

    [Fact]
    public void EncodeIDecode_Heartbeatu_ZachowujaTypIWartosci()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new HeartbeatMessage
        {
            Sender = "PC-A",
            Version = "0.1.0",
            Seq = 7,
            SentAt = clock.UtcNow,
            Priority = 30,
            Role = PeerRole.Watcher,
            BelievedWatcher = "PC-A",
            MachinesConnected = 11,
            MachinesTotal = 12,
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        bool ok = MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason);

        Assert.True(ok, reason);
        HeartbeatMessage hb = Assert.IsType<HeartbeatMessage>(decoded);
        Assert.Equal("PC-A", hb.Sender);
        Assert.Equal(7, hb.Seq);
        Assert.Equal(30, hb.Priority);
        Assert.Equal(PeerRole.Watcher, hb.Role);
        Assert.Equal("PC-A", hb.BelievedWatcher);
        Assert.Equal(11, hb.MachinesConnected);
        Assert.Equal(12, hb.MachinesTotal);
    }

    [Fact]
    public void EncodeIDecode_WiadomosciStanu_ZachowujaAlertyIMigawkiMaszyn()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new StateMessage
        {
            Sender = "PC-B",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = [SampleAlert(), SampleAlert("amm580", "spindle")],
            Machines = [SampleSnapshot(), SampleSnapshot("amm580")],
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        StateMessage state = Assert.IsType<StateMessage>(decoded);
        Assert.Equal(2, state.Alerts.Count);
        Assert.Equal(2, state.Machines.Count);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), state.Alerts[0].Key);
        Assert.Equal("Nie udało się pobrać tacki", state.Alerts[0].ShortText);
        Assert.Equal(AlertStatus.Active, state.Alerts[0].Status);
        Assert.Equal(SampleAlert().StartedAt, state.Alerts[0].StartedAt);
        Assert.Equal("vnc", state.Machines[0].Info?.Kind);
        Assert.Equal(MachineConnectionState.Connected, state.Machines[0].State);
    }

    [Fact]
    public void EncodeIDecode_WiadomosciAck_ZachowujaKluczAlertu()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new AckMessage
        {
            Sender = "PC-C",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Key = AlertKey.ForRule("cameo1", "door"),
            User = "jkowalski",
            AckedAt = clock.UtcNow,
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out _));

        AckMessage ack = Assert.IsType<AckMessage>(decoded);
        Assert.Equal(AlertKey.ForRule("cameo1", "door"), ack.Key);
        Assert.Equal("jkowalski", ack.User);
        Assert.Equal(clock.UtcNow, ack.AckedAt);
    }

    [Fact]
    public void Encode_ZapisujeRozroznikTypuWiadomosci()
    {
        HmacSigner signer = Signer();
        var msg = new HeartbeatMessage { Sender = "PC-A", Version = "1.0", SentAt = FakeClock.DefaultStart };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        string json = Encoding.UTF8.GetString(datagram, 36, datagram.Length - 36);

        Assert.Contains("\"type\":\"heartbeat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sender\":\"PC-A\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TryDecode_PakietuSprzedDziesieciuMinut_OdrzucaGoZPowodemCzasu()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new HeartbeatMessage
        {
            Sender = "PC-A",
            Version = "1.0",
            SentAt = clock.UtcNow - TimeSpan.FromMinutes(10),
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        bool ok = MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason);

        Assert.False(ok);
        Assert.Null(decoded);
        Assert.Contains("okna czasowego", reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zegar", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDecode_PakietuZPrzyszlosci_TakzeGoOdrzuca()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new HeartbeatMessage { Sender = "PC-A", Version = "1.0", SentAt = clock.UtcNow + TimeSpan.FromMinutes(10) };

        Assert.False(MessageCodec.TryDecode(MessageCodec.Encode(msg, signer), signer, clock, Skew, out _, out _));
    }

    [Fact]
    public void TryDecode_PakietuZNiepoprawnymJson_OdrzucaGoZPowodemJson()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        byte[] datagram = signer.Seal("{ to na pewno nie jest json"u8);

        bool ok = MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason);

        Assert.False(ok);
        Assert.Null(decoded);
        Assert.Contains("JSON", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDecode_PakietuZeZlymPodpisem_OdrzucaGo()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var other = new HmacSigner(HmacSigner.GenerateKey());
        byte[] datagram = MessageCodec.Encode(
            new HeartbeatMessage { Sender = "PC-A", Version = "1.0", SentAt = clock.UtcNow }, other);

        Assert.False(MessageCodec.TryDecode(datagram, signer, clock, Skew, out _, out string? reason));
        Assert.Contains("HMAC", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Encode_OgromnegoStanu_PrzycinaGoDoLimituBezWyjatku()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new StateMessage
        {
            Sender = "PC-A",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = Enumerable.Range(0, 400).Select(i => SampleAlert($"maszyna{i}", $"regula{i}")).ToList(),
            Machines = Enumerable.Range(0, 400).Select(i => SampleSnapshot($"maszyna{i}")).ToList(),
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);

        Assert.True(
            datagram.Length <= MessageCodec.MaxDatagramSizeBytes,
            $"Pakiet ma {datagram.Length} B, a limit to {MessageCodec.MaxDatagramSizeBytes} B.");
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);
        StateMessage state = Assert.IsType<StateMessage>(decoded);
        Assert.True(state.Alerts.Count > 0);
        Assert.True(state.Alerts.Count < 400 || state.Machines.Count < 400);
    }

    [Fact]
    public void Encode_JednegoAlertuZGigantycznymFullText_PrzycinaTrescIZachowujeAlert()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        Alert hugeAlert = SampleAlert() with { FullText = new string('X', 200_000) };
        var msg = new StateMessage
        {
            Sender = "PC-A",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = [hugeAlert],
            Machines = [],
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);

        Assert.True(
            datagram.Length <= MessageCodec.MaxDatagramSizeBytes,
            $"Pakiet ma {datagram.Length} B, a limit to {MessageCodec.MaxDatagramSizeBytes} B.");
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        StateMessage state = Assert.IsType<StateMessage>(decoded);
        Alert alert = Assert.Single(state.Alerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), alert.Key);
        Assert.NotNull(alert.FullText);
        Assert.True(alert.FullText!.Length <= 501, $"FullText ma {alert.FullText.Length} znaków.");
        Assert.EndsWith("…", alert.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_MaleogoStanu_NieJestPrzycinany()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new StateMessage
        {
            Sender = "PC-A",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = [SampleAlert()],
            Machines = [SampleSnapshot()],
        };

        MessageCodec.TryDecode(MessageCodec.Encode(msg, signer), signer, clock, Skew, out PeerMessage? decoded, out _);

        StateMessage state = Assert.IsType<StateMessage>(decoded);
        Assert.Single(state.Alerts);
        Assert.Single(state.Machines);
    }

    [Fact]
    public void EncodeIDecode_AlertuZPriorytetemReguly_ZachowujaPriorytet()
    {
        // Dlaczego: Alert.Priority rozstrzyga, czy KOLEJNY komunikat tego samego epizodu podmieni treść
        // alertu (patrz AlertStateMachine.OnRedConfirmed). Obserwator może się w każdej chwili zmienić —
        // gdyby priorytet gubił się po drodze przez sieć, nowy obserwator uznałby alarm serwonapędu (90)
        // za komunikat o priorytecie 0 i pozwolił zepchnąć go byle chińskiemu makru.
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new StateMessage
        {
            Sender = "PC-B",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = [SampleAlert() with { Priority = 90 }],
            Machines = [],
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        StateMessage state = Assert.IsType<StateMessage>(decoded);
        Assert.Equal(90, Assert.Single(state.Alerts).Priority);
    }

    [Fact]
    public void Decode_AlertuOdStarszegoNadawcyBezPolaPriority_DajePriorytetZero()
    {
        string json = JsonSerializer.Serialize(SampleAlert() with { Priority = 90 }, MessageCodec.JsonOptions);
        JsonObject bezPriorytetu = JsonNode.Parse(json)!.AsObject();
        Assert.True(bezPriorytetu.Remove("priority"), "Pole 'priority' nie jest serializowane — test straciłby sens.");

        Alert alert = JsonSerializer.Deserialize<Alert>(bezPriorytetu.ToJsonString(), MessageCodec.JsonOptions)!;

        Assert.Equal(0, alert.Priority);
        Assert.Equal("Nie udało się pobrać tacki", alert.ShortText);
    }

    [Fact]
    public void Heartbeat_BezWspolnychInstrukcji_NieNiesiePolINowszegoProtokolu()
    {
        // Dlaczego: pola o wspólnych instrukcjach są opcjonalne. Komputer,
        // który ich nie używa, ma wysyłać dokładnie taki pakiet jak przed zmianą, a odbiorca starszej
        // wersji nie może się zakrztusić polem, którego nie zna.
        var signer = Signer();
        var heartbeat = new HeartbeatMessage { Sender = "PC-A", Version = "test", Readiness = new(true, false, []) };

        byte[] datagram = MessageCodec.Encode(heartbeat, signer);
        string json = JsonSerializer.Serialize<PeerMessage>(heartbeat, MessageCodec.JsonOptions);
        Assert.DoesNotContain("instructionsHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("instructionsEditedAt", json, StringComparison.Ordinal);

        Assert.True(MessageCodec.TryDecode(datagram, signer, new FakeClock(heartbeat.SentAt), Skew, out var decoded, out _));
        var received = Assert.IsType<HeartbeatMessage>(decoded);
        Assert.Null(received.InstructionsHash);
        Assert.Null(received.InstructionsEditedAt);
    }

    [Fact]
    public void Chunk_DzieliWpisyNaPakietyMieszczaceSieWLimicieIZachowujeKolejnosc()
    {
        // Dlaczego: wspólne instrukcje i przyszły wspólny raport postojów idą tą
        // samą drogą. Porcja musi się zmieścić w datagramie RAZEM z podpisem, a odbiorca musi wiedzieć,
        // że po niej idzie następna.
        var signer = Signer();
        var entries = Enumerable.Range(0, 100).Select(i => new MaszynyAlert.Core.Ocr.InstructionEntry
        {
            Key = $"code:{1000 + i}", Text = new string('x', 4000),
            EditedAt = FakeClock.DefaultStart, EditedBy = "PC-A",
        }).ToArray();

        var messages = MessageCodec.Chunk(entries, (chunk, more) => new InstructionsSyncReply
        { Sender = "PC-A", Version = "test", Entries = chunk.ToArray(), More = more, ReplyTo = "PC-B" }, 256);

        Assert.True(messages.Count > 1, "Sto wpisów po 4000 znaków musi wymagać więcej niż jednego pakietu.");
        Assert.All(messages.Take(messages.Count - 1), m => Assert.True(m.More));
        Assert.False(messages[^1].More);
        Assert.All(messages, m => Assert.True(MessageCodec.Encode(m, signer).Length <= MessageCodec.MaxDatagramSizeBytes));
        Assert.Equal(entries.Select(e => e.Key), messages.SelectMany(m => m.Entries).Select(e => e.Key));
    }

    [Fact]
    public void Chunk_PostojePonad60KBIdaKilkomaPakietamiBezUcinaniaWpisuWSrodku()
    {
        // Dlaczego: uzupełnianie raportu postojów odpowiada CAŁYMI wpisami dziennika.
        // Pakiet ucięty w środku wpisu byłby dla odbiorcy niepoprawnym JSON-em i cała porcja
        // wylądowałaby w koszu — a raport grupy zostałby z luką, o której nikt by się nie dowiedział.
        var signer = Signer();
        var start = FakeClock.DefaultStart;
        var episodes = Enumerable.Range(0, 200).Select(i => new MaszynyAlert.Core.History.DowntimeEpisode(
            "aidite" + (i % 7 + 1), "Aidite " + (i % 7 + 1), new string('x', 480),
            start.AddMinutes(i), start.AddMinutes(i + 5), start.AddMinutes(i + 5),
            EpisodeId: Guid.NewGuid(), Origin: "PC-A")).ToArray();

        var messages = MessageCodec.Chunk(episodes, (chunk, more) => new DowntimeSyncReply
        {
            Sender = "PC-A", Version = "test", ReplyTo = "PC-B", RequestId = Guid.NewGuid(),
            Episodes = [.. chunk], More = more, Cursor = chunk.Count > 0 ? chunk[^1].LastObservedAt : null,
        }, 256);

        Assert.True(messages.Count > 1, "Dwieście wpisów po ~480 znaków powodu musi wymagać więcej niż jednego pakietu.");
        Assert.All(messages.Take(messages.Count - 1), m => Assert.True(m.More));
        Assert.False(messages[^1].More);
        Assert.All(messages, m => Assert.True(MessageCodec.Encode(m, signer).Length <= MessageCodec.MaxDatagramSizeBytes));
        // Żaden wpis się nie zgubił i kolejność (podstawa kursora) została zachowana.
        Assert.Equal(episodes.Select(e => e.EpisodeId), messages.SelectMany(m => m.Episodes).Select(e => e.EpisodeId));
        Assert.All(messages, m => Assert.Equal(m.Episodes[^1].LastObservedAt, m.Cursor));
    }

    [Fact]
    public void Chunk_PustaListaDajeJednaPustaOdpowiedz()
    {
        var messages = MessageCodec.Chunk(Array.Empty<MaszynyAlert.Core.Ocr.InstructionEntry>(),
            (chunk, more) => new InstructionsSyncReply { Sender = "PC-A", Version = "test", Entries = chunk.ToArray(), More = more });

        var single = Assert.Single(messages);
        Assert.Empty(single.Entries);
        Assert.False(single.More);
    }
}
