using System.Text.Json;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class ChunkRoundTwoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly GroupIdentity Group = GroupFileTests.Invitation().Identity;
    private readonly HmacSigner _signer = new(HmacSigner.GenerateKey());

    /// <summary>Tak jak bus: najdłuższe Seq, SentAt z ułamkiem, InstanceId 32 znaki.</summary>
    private byte[] StampAndEncode(PeerMessage message) => MessageCodec.Encode(message with
    {
        Seq = 999_999_999_999, SentAt = T0.AddTicks(1_234_567), InstanceId = Guid.NewGuid().ToString("N"),
    }, _signer);

    /// <summary>koperta z pustej, nieostemplowanej wiadomości bez „type” — do porównania liczby porcji.</summary>
    private static int OldChunkCount<TItem, TMessage>(IReadOnlyList<TItem> items, Func<IReadOnlyList<TItem>, bool, TMessage> build,
        int maxItemsPerMessage) where TMessage : PeerMessage
    {
        int budget = MessageCodec.MaxDatagramSizeBytes - HmacSigner.SealOverheadBytes
            - JsonSerializer.SerializeToUtf8Bytes(build([], true), MessageCodec.JsonOptions).Length - 64;
        int chunks = 1, used = 0, count = 0;
        foreach (var item in items)
        {
            int size = JsonSerializer.SerializeToUtf8Bytes(item, MessageCodec.JsonOptions).Length + 1;
            if (count > 0 && (used + size > budget || count >= maxItemsPerMessage)) { chunks++; used = 0; count = 0; }
            used += size; count++;
        }
        return chunks;
    }

    private static ObservationSyncReply ObservationReply(IReadOnlyList<ObservationInterval> items, bool more) => new()
    {
        Sender = "AIDITEKOMP1", Version = "0.4.114", Group = Group, RequestId = Guid.NewGuid(), ReplyTo = "PSZEMEK",
        Intervals = [.. items], More = more, Cursor = items.Count > 0 ? items[^1].End : null,
    };

    private static DowntimeSyncReply DowntimeReply(IReadOnlyList<DowntimeEpisode> items, bool more) => new()
    {
        Sender = "AIDITEKOMP1", Version = "0.4.114", Group = Group, RequestId = Guid.NewGuid(), ReplyTo = "PSZEMEK",
        Episodes = [.. items], More = more, Cursor = items.Count > 0 ? items[^1].LastObservedAt : null,
    };

    private static InstructionsSyncReply InstructionsReply(IReadOnlyList<InstructionEntry> items, bool more) => new()
    {
        Sender = "AIDITEKOMP1", Version = "0.4.114", Group = Group, RequestId = Guid.NewGuid(), ReplyTo = "PSZEMEK",
        Entries = items.ToArray(), More = more,
    };

    // ── Instrukcje z polskimi znakami ────────────────────────────────────────────────────────

    [Fact]
    public void InstructionPortionsWithLongPolishTextsFitAfterStampingAndArriveComplete()
    {
        var random = new Random(51026);
        const string polish = "Sprawdź oś Z, wyłącz wrzeciono, odłóż ramkę <uwaga> & „cudzysłów” — żółć ąęśćńółź ";
        var entries = Enumerable.Range(0, 400).Select(i =>
        {
            int length = i % 10 == 0 ? AlarmInstructionStore.MaxTextLength : random.Next(1, AlarmInstructionStore.MaxTextLength + 1);
            string text = string.Concat(Enumerable.Repeat(polish, length / polish.Length + 1))[..length];
            return new InstructionEntry { Key = $"code:{700 + i}", Text = text, EditedAt = T0.AddMinutes(i), EditedBy = "AIDITEKOMP2" };
        }).ToArray();
        Assert.All(entries, e => Assert.True(e.IsValid()));

        var chunks = MessageCodec.Chunk(entries, InstructionsReply, 256);
        var arrived = new List<InstructionEntry>();
        foreach (var chunk in chunks)
        {
            byte[] datagram = StampAndEncode(chunk);
            Assert.InRange(datagram.Length, 1, MessageCodec.MaxDatagramSizeBytes);
            Assert.True(MessageCodec.TryDecode(datagram, _signer, new FakeClock(T0), TimeSpan.FromMinutes(5), out var decoded, out var why), why);
            arrived.AddRange(((InstructionsSyncReply)decoded!).Entries);
        }

        Assert.Equal(entries, arrived);
        Assert.True(chunks.Count > 20, $"porcji: {chunks.Count} — teksty po escapowaniu muszą dzielić listę bajtami");
    }

    [Theory]
    [InlineData('ą')]
    [InlineData('<')]
    [InlineData('"')]
    [InlineData('\u0001')] // znak sterujący → \u0001
    public void ObservationPortionsWithHundredEscapedCharactersFitAfterStamping(char letter)
    {
        foreach (int length in new[] { 1, 37, 64, 99, 100 })
        {
            string machine = new(letter, length);
            var items = Enumerable.Range(0, 700).Select(i => new ObservationInterval(machine, (ObservationState)(i % 3 + 1),
                T0.AddSeconds(-20 * i - 7).AddTicks(i % 2 == 0 ? 0 : 1_234_567), T0.AddSeconds(-20 * i).AddTicks(i % 2 == 0 ? 7_654_321 : 0))).ToArray();
            var chunks = MessageCodec.Chunk(items, ObservationReply, 256);
            int arrived = 0;
            foreach (var chunk in chunks)
            {
                byte[] datagram = StampAndEncode(chunk);
                Assert.True(MessageCodec.TryDecode(datagram, _signer, new FakeClock(T0), TimeSpan.FromMinutes(5), out var decoded, out var why), why);
                arrived += ((ObservationSyncReply)decoded!).Intervals.Count;
            }
            Assert.Equal(items.Length, arrived);
        }
    }

    // ── Pojedynczy wpis przy limicie ─────────────────────────────────────────────────────────

    [Fact]
    public void SingleItemIsRejectedOnlyNearTheRealLimitAndTheAcceptedMaximumFits()
    {
        ObservationInterval Item(int length) => new(new string('x', length), ObservationState.Observed, T0, T0.AddSeconds(1));
        bool Accepted(int length)
        {
            try { MessageCodec.Chunk([Item(length)], ObservationReply, 256); return true; }
            catch (InvalidDataException) { return false; }
        }
        bool Fits(int length)
        {
            try { StampAndEncode(ObservationReply([Item(length)], false)); return true; }
            catch (InvalidDataException) { return false; }
        }

        int lo = 1, hi = 70_000; // największa długość przyjmowana przez Chunk
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Accepted(mid)) lo = mid; else hi = mid - 1; }
        int acceptedMax = lo;
        lo = acceptedMax; hi = 70_000; // największa, która naprawdę mieści się po ostemplowaniu
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Fits(mid)) lo = mid; else hi = mid - 1; }
        int fitsMax = lo;

        Assert.True(Fits(acceptedMax));
        Assert.False(Accepted(acceptedMax + 1));
        var ex = Assert.Throws<InvalidDataException>(() => MessageCodec.Chunk([Item(acceptedMax + 1)], ObservationReply, 256));
        Assert.Contains("Pojedynczy wpis", ex.Message);
        // Zapas jest, ale mały: odrzucony bywa tylko wpis, któremu do limitu brakuje mniej niż ~130 B.
        Assert.InRange(fitsMax - acceptedMax, 0, 130);
    }

    [Fact]
    public void LongestValidDowntimeReasonOfEscapedCharactersIsAcceptedAlone()
    {
        var episode = new DowntimeEpisode("aidite2", new string('ą', 500), new string('ą', 500), T0, T0.AddMinutes(5), T0.AddMinutes(5),
            false, Guid.NewGuid(), new string('<', 100));
        var chunk = Assert.Single(MessageCodec.Chunk([episode], DowntimeReply, 256));
        Assert.InRange(StampAndEncode(chunk).Length, 1, MessageCodec.MaxDatagramSizeBytes);
    }

    // ── Ruch dla zwykłych list nie rośnie ────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1000)]
    [InlineData(12_800)]
    public void PortionCountForOrdinaryAsciiListsIsTheSameAsIn0_4_113(int count)
    {
        string[] machines = ["aidite1", "aidite7", "520-1", "amm580", "premile", "cameo1"];
        var intervals = Enumerable.Range(0, count).Select(i => new ObservationInterval(machines[i % machines.Length],
            (ObservationState)(i % 3 + 1), T0.AddSeconds(-30 * i - 10).AddTicks(1_234_567), T0.AddSeconds(-30 * i).AddTicks(7_654_321))).ToArray();
        string[] reasons = ["Servo 6", "Bazowanie (Home)", "Ramka niepobrana", "Tool life was reached", "Air pressure low M024.00"];
        var episodes = Enumerable.Range(0, count).Select(i => new DowntimeEpisode(machines[i % machines.Length], "Aidite " + i % 7,
            reasons[i % reasons.Length], T0.AddMinutes(-10 * i - 5), T0.AddMinutes(-10 * i), T0.AddMinutes(-10 * i), false,
            Guid.NewGuid(), "AIDITEKOMP1")).ToArray();

        Assert.Equal(OldChunkCount(intervals, ObservationReply, 256), MessageCodec.Chunk(intervals, ObservationReply, 256).Count);
        // Postoje (~330 B na wpis) dzielą się bajtami już przy ~180 wpisach: poprawiona koperta jest o ~100 B większa, więc
        // przy bardzo długich listach może dojść jedna porcja (12 800 wpisów: 71 → 72). Dla list do 1000 wpisów — bez zmian.
        int oldEpisodes = OldChunkCount(episodes, DowntimeReply, 256), newEpisodes = MessageCodec.Chunk(episodes, DowntimeReply, 256).Count;
        if (count <= 1000) Assert.Equal(oldEpisodes, newEpisodes);
        else Assert.InRange(newEpisodes, oldEpisodes, oldEpisodes + Math.Max(1, oldEpisodes / 50));
        Assert.Equal((count + 255) / 256, MessageCodec.Chunk(intervals, ObservationReply, 256).Count);
    }

    [Fact]
    public void TenEpisodesStillGoInOnePortion()
    {
        var episodes = Enumerable.Range(0, 10).Select(i => new DowntimeEpisode("aidite2", "Aidite 2", "Servo 6",
            T0.AddMinutes(i), T0.AddMinutes(i + 3), T0.AddMinutes(i + 3), false, Guid.NewGuid(), "A")).ToArray();
        Assert.Single(MessageCodec.Chunk(episodes, DowntimeReply, 256));
    }

    // ── Postoje z długim powodem przez węzeł grupy ───────────────────────────────────────────

    [Fact]
    public async Task DowntimeEpisodesWithLongEscapedReasonsReachThePeerCompletely()
    {
        var clock = new FakeClock(T0);
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        var sniffed = new List<DowntimeSyncReply>();
        hub.CreateBus("S").Received += m => { if (m is DowntimeSyncReply r) lock (sniffed) sniffed.Add(r); };
        using var dir = new TempDir("downtime-r2");
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "test");
        await using var b = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B"), clock, "test");
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        a.Downtime = aJournal; b.Downtime = bJournal;
        string[] letters = ["ą", "<", "ż", "a", "\""];
        for (int i = 0; i < 600; i++)
        {
            var start = T0.AddDays(-2).AddMinutes(4 * i);
            aJournal.Merge(new DowntimeEpisode("aidite" + (i % 7 + 1), "Aidite", string.Concat(Enumerable.Repeat(letters[i % 5], 500 - i % 13)),
                start, start.AddMinutes(3), start.AddMinutes(3), false, Guid.NewGuid(), "A"));
        }
        Assert.Equal(600, aJournal.Snapshot().Count);

        await a.StartAsync(default); await b.StartAsync(default);
        for (int s = 0; s < 200; s++) { await a.TickAsync(default); await b.TickAsync(default); clock.Advance(TimeSpan.FromSeconds(1)); }

        Assert.Equal(600, bJournal.Snapshot().Count);
        Assert.All(sniffed, r => Assert.InRange(MessageCodec.Encode(r, hub.Signer).Length, 1, MessageCodec.MaxDatagramSizeBytes));
        Assert.Null(b.TransportError);
        Assert.Null(a.TransportError);
    }
}
