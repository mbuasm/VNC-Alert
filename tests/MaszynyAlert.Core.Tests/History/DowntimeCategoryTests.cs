using System.Text.Json;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeCategoryTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);
    private static readonly Guid Id = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Start = At("2026-09-15T08:00:00Z");

    private static DowntimeEpisode Make(int observedMinutes, string? category, Guid? id = null, string machine = "aidite2",
        int startMinutes = 0, string reason = "Servo 6") =>
        new(machine, "Aidite 2", reason, Start.AddMinutes(startMinutes), Start.AddMinutes(observedMinutes), null,
            EpisodeId: id ?? Id, Origin: "PC-A", Category: category);

    private static Alert Alarm(string category) => new()
    {
        Key = AlertKey.ForRule("aidite1", "tray-grab"), MachineId = "aidite1", MachineName = "Aidite 1",
        ShortText = "Tacka nie pobrana", Category = category, Kind = AlertKind.Error, Status = AlertStatus.Active,
        StartedAt = Start, LastSeenAt = Start.AddMinutes(5), EpisodeId = Id,
    };

    private static string[] Lines(string path) => File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();

    // --- Record i plik ---

    [Fact]
    public void Record_ZapisujeKategorieAlertuWEpizodzieIWPliku()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);

        journal.Record(Alarm("tacka"));

        Assert.Equal("tacka", Assert.Single(journal.Snapshot()).Category);
        Assert.Contains("\"Category\":\"tacka\"", Assert.Single(Lines(path)), StringComparison.Ordinal);
        Assert.Equal("tacka", Assert.Single(new DowntimeJournal(path).Snapshot()).Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_PustaKategoriaAlertu_BezPolaWPliku(string category)
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);

        journal.Record(Alarm(category));

        Assert.Null(Assert.Single(journal.Snapshot()).Category);
        Assert.DoesNotContain("Category", Assert.Single(Lines(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Plik_WierszFormatu0_4_113_BezKategorii()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        File.WriteAllText(path, "{\"MachineId\":\"aidite2\",\"MachineName\":\"Aidite 2\",\"Reason\":\"Servo 6\","
            + "\"StartedAt\":\"2026-09-15T08:00:00+00:00\",\"LastObservedAt\":\"2026-09-15T08:09:00+00:00\","
            + "\"EndedAt\":\"2026-09-15T08:09:00+00:00\",\"Interrupted\":false,\"EpisodeId\":\"aaaaaaaa-0000-0000-0000-000000000002\","
            + "\"Origin\":\"PC-NEW\",\"End\":\"2026-09-15T08:09:00+00:00\"}\n");

        var journal = new DowntimeJournal(path);

        Assert.Null(journal.Error);
        var episode = Assert.Single(journal.Snapshot());
        Assert.Null(episode.Category);
        Assert.Equal("Servo 6", episode.Reason);
    }

    [Fact]
    public void Serializacja_BezKategoriiWierszIdentycznyJakW0_4_113()
    {
        var episode = new DowntimeEpisode("a", "A", "R", Start, Start.AddMinutes(5), null, EpisodeId: Id);
        string json = JsonSerializer.Serialize(episode);
        Assert.DoesNotContain("Category", json, StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(Old0413.From(episode)), json);
        Assert.DoesNotContain("category", JsonSerializer.Serialize(episode, MessageCodec.JsonOptions), StringComparison.Ordinal);
    }

    // --- Grupa mieszana: Merge i Apply ---

    [Fact]
    public void Merge_PustaKategoriaNigdyNieNadpisujeZnanej()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Merge(Make(5, "restart"));

        journal.Merge(Make(6, null)); // nowszy od 0.4.113 — bez pola
        Assert.Equal("restart", Assert.Single(journal.Snapshot()).Category);
        Assert.Equal(Start.AddMinutes(6), journal.Snapshot()[0].LastObservedAt);

        journal.Merge(Make(1, null)); // starszy bez pola
        Assert.Equal("restart", journal.Snapshot()[0].Category);

        journal.Merge(Make(7, "narzedzie")); // nowszy z inną znaną kategorią — idzie z tekstem
        Assert.Equal("narzedzie", journal.Snapshot()[0].Category);

        journal.Merge(Make(2, "tacka")); // starszy ze znaną — przegrywa z nowszą znaną
        Assert.Equal("narzedzie", journal.Snapshot()[0].Category);
    }

    [Fact]
    public void Merge_StarszyWpisZeZnanaKategoria_UzupelniaBrak()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Merge(Make(6, null));

        journal.Merge(Make(3, "restart"));

        var episode = Assert.Single(journal.Snapshot());
        Assert.Equal("restart", episode.Category);
        Assert.Equal(Start.AddMinutes(6), episode.LastObservedAt);
    }

    [Fact]
    public void Apply_TeSameRegulyCoMerge()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        Assert.True(journal.Apply([Make(5, "restart")]));
        journal.Apply([Make(6, null)]);
        Assert.Equal("restart", journal.Snapshot()[0].Category);
        journal.Apply([Make(7, "narzedzie")]);
        Assert.Equal("narzedzie", journal.Snapshot()[0].Category);

        var other = new DowntimeJournal(dir.File("other.jsonl"));
        other.Apply([Make(6, null)]);
        other.Apply([Make(3, "tacka")]);
        Assert.Equal("tacka", other.Snapshot()[0].Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Apply_PustyNapisKategoriiZSieciNieNadpisujeZnanej(string empty)
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Apply([Make(5, "restart")]);

        journal.Apply([Make(6, empty)]);

        Assert.Equal("restart", Assert.Single(journal.Snapshot()).Category);
    }

    [Fact]
    public void MergeOverlaps_NowszyBezKategorii_ScalonyMaKategorieStarszego()
    {
        var older = Make(10, "restart", Guid.NewGuid());
        var newer = Make(12, null, Guid.NewGuid(), startMinutes: 2);

        var merged = Assert.Single(DowntimeReport.MergeOverlaps([older, newer]));
        Assert.Equal("restart", merged.Category);
        Assert.Equal(Start.AddMinutes(12), merged.LastObservedAt);
        Assert.Equal("restart", Assert.Single(DowntimeReport.MergeOverlaps([newer, older])).Category);

        var newerKnown = newer with { Category = "narzedzie" };
        Assert.Equal("narzedzie", Assert.Single(DowntimeReport.MergeOverlaps([older, newerKnown])).Category);
        // Trzy kawałki: kategoria przeżywa łańcuch scalania.
        var third = Make(14, null, Guid.NewGuid(), startMinutes: 11);
        Assert.Equal("restart", Assert.Single(DowntimeReport.MergeOverlaps([older, newer, third])).Category);
    }

    [Fact]
    public void MergeOverlaps_PustyNapisKategoriiNieNadpisujeZnanej()
    {
        var older = Make(10, "restart", Guid.NewGuid());
        var newer = Make(12, "", Guid.NewGuid(), startMinutes: 2);

        Assert.Equal("restart", Assert.Single(DowntimeReport.MergeOverlaps([older, newer])).Category);
    }

    [Fact]
    public void Merge_ZmianaSamejKategoriiPrzyTymSamymCzasieObserwacji_ZapisNatychmiast()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        journal.Merge(Make(5, null));
        Assert.Single(Lines(path));

        Assert.True(journal.Merge(Make(5, "tacka")));

        Assert.Equal(2, Lines(path).Length);
        Assert.Equal("tacka", Assert.Single(new DowntimeJournal(path).Snapshot()).Category);
        // To samo jeszcze raz niczego nie zmienia i nie dopisuje.
        Assert.False(journal.Merge(Make(5, "tacka")));
        Assert.Equal(2, Lines(path).Length);
    }

    // --- IsValid ---

    [Fact]
    public void IsValid_KategoriaOpcjonalnaDo100Znakow()
    {
        Assert.True(Make(5, null).IsValid());
        Assert.True(Make(5, new string('k', 100)).IsValid());
        Assert.False(Make(5, new string('k', 101)).IsValid());

        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        Assert.False(journal.Apply([Make(5, new string('k', 101))]));
        Assert.Empty(journal.Snapshot());
    }

    // --- Komunikat grupy ---

    [Fact]
    public void Chunk_OdpowiedzZKategoriamiMiesciSieWLimicie()
    {
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        var episodes = Enumerable.Range(0, 200).Select(i => new DowntimeEpisode("aidite" + (i % 7 + 1), "Aidite", new string('x', 480),
            Start.AddMinutes(i), Start.AddMinutes(i + 5), Start.AddMinutes(i + 5), EpisodeId: Guid.NewGuid(), Origin: "PC-A",
            Category: new string('k', 100))).ToArray();

        var messages = MessageCodec.Chunk(episodes, (chunk, more) => new DowntimeSyncReply
        {
            Sender = "PC-A", Version = "test", ReplyTo = "PC-B", RequestId = Guid.NewGuid(),
            Episodes = [.. chunk], More = more, Cursor = chunk.Count > 0 ? chunk[^1].LastObservedAt : null,
        }, 256);

        Assert.True(messages.Count > 1);
        Assert.All(messages, m => Assert.True(MessageCodec.Encode(m, signer).Length <= MessageCodec.MaxDatagramSizeBytes));
        Assert.Equal(episodes.Select(e => e.EpisodeId), messages.SelectMany(m => m.Episodes).Select(e => e.EpisodeId));
    }

    [Fact]
    public void Komunikat_KategoriaPrzechodziPrzezKodowanieIDekodowanie()
    {
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        var clock = new FakeClock();
        var reply = new DowntimeSyncReply
        {
            Sender = "PC-A", Version = "test", ReplyTo = "PC-B", RequestId = Guid.NewGuid(), SentAt = clock.UtcNow,
            Episodes = [Make(5, "tacka"), Make(5, null, Guid.NewGuid())],
        };

        Assert.True(MessageCodec.TryDecode(MessageCodec.Encode(reply, signer), signer, clock, TimeSpan.FromMinutes(5), out var msg, out var reason), reason);
        var decoded = Assert.IsType<DowntimeSyncReply>(msg);
        Assert.Equal("tacka", decoded.Episodes[0].Category);
        Assert.Null(decoded.Episodes[1].Category);
    }

    // --- Zgodność z 0.4.113 ---

    /// <summary>Kopia sygnatury <c>DowntimeEpisode</c> z 0.4.113 — bez pola <c>Category</c>.</summary>
    private sealed record Old0413(string MachineId, string MachineName, string Reason,
        DateTimeOffset StartedAt, DateTimeOffset LastObservedAt, DateTimeOffset? EndedAt, bool Interrupted = false,
        Guid EpisodeId = default, string? Origin = null)
    {
        public DateTimeOffset End => EndedAt ?? LastObservedAt;

        public static Old0413 From(DowntimeEpisode e) =>
            new(e.MachineId, e.MachineName, e.Reason, e.StartedAt, e.LastObservedAt, e.EndedAt, e.Interrupted, e.EpisodeId, e.Origin);
    }

    private sealed record OldReply0413(Guid RequestId, string? ReplyTo, List<Old0413> Episodes, bool More);

    [Fact]
    public void Zgodnosc_RekordBezKategoriiCzytaPlikIKomunikatNowejWersji()
    {
        var episode = Make(5, "tacka");

        var fromFile = JsonSerializer.Deserialize<Old0413>(JsonSerializer.Serialize(episode));
        Assert.Equal(Old0413.From(episode), fromFile);

        var reply = new DowntimeSyncReply { Sender = "PC-A", Version = "test", RequestId = Guid.NewGuid(), Episodes = [episode] };
        string wire = JsonSerializer.Serialize<PeerMessage>(reply, MessageCodec.JsonOptions);
        Assert.Contains("\"category\":\"tacka\"", wire, StringComparison.Ordinal);
        var old = JsonSerializer.Deserialize<OldReply0413>(wire, MessageCodec.JsonOptions);
        Assert.Equal(Old0413.From(episode), Assert.Single(old!.Episodes));

        Assert.NotEqual(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MessageCodec.JsonOptions.UnmappedMemberHandling);
    }

    [Fact]
    public void Zgodnosc_NowaWersjaCzytaKomunikat0_4_113()
    {
        var old = new Old0413("aidite2", "Aidite 2", "Servo 6", Start, Start.AddMinutes(5), null, false, Id, "PC-OLD");
        var episode = JsonSerializer.Deserialize<DowntimeEpisode>(JsonSerializer.Serialize(old, MessageCodec.JsonOptions), MessageCodec.JsonOptions);
        Assert.NotNull(episode);
        Assert.Null(episode!.Category);
        Assert.True(episode.IsValid());
    }
}
