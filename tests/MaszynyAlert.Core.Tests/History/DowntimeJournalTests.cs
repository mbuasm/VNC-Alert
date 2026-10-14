using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeJournalTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);
    private static readonly Guid Episode = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static DowntimeEpisode Make(DateTimeOffset started, DateTimeOffset observed, DateTimeOffset? ended = null,
        bool interrupted = false, string reason = "Servo 6", string? origin = null, Guid? id = null,
        string machineId = "aidite2") =>
        new(machineId, "Aidite 2", reason, started, observed, ended, interrupted, id ?? Episode, origin);

    [Fact]
    public void EarliestStartAndLatestObservationWin()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        // PC-A widział epizod od 08:00, PC-B dołączył później, ale ma świeższą obserwację.
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:02:00Z"), origin: "PC-A"));
        journal.Merge(Make(At("2026-09-15T08:01:00Z"), At("2026-09-15T08:05:00Z"), origin: "PC-B"));

        var episode = Assert.Single(journal.Snapshot());
        Assert.Equal(At("2026-09-15T08:00:00Z"), episode.StartedAt);
        Assert.Equal(At("2026-09-15T08:05:00Z"), episode.LastObservedAt);
        Assert.Equal("PC-B", episode.Origin);
    }

    [Fact]
    public void RealClosureAlwaysReplacesInterruptedClosureEvenIfEarlier()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        // PC-A restartował się w środku epizodu i przy starcie wymusił zamknięcie na LastObservedAt (Interrupted).
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:10:00Z"),
            At("2026-09-15T08:10:00Z"), interrupted: true, origin: "PC-A"));
        // PC-B obserwował dalej i naprawdę domknął epizod wcześniejszą, ale prawdziwą chwilą.
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:09:00Z"),
            At("2026-09-15T08:09:00Z"), interrupted: false, origin: "PC-B"));

        var episode = Assert.Single(journal.Snapshot());
        Assert.Equal(At("2026-09-15T08:09:00Z"), episode.EndedAt);
        Assert.False(episode.Interrupted);
    }

    [Fact]
    public void TwoRealClosures_LaterOneWins()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:09:00Z"), At("2026-09-15T08:09:00Z")));
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:09:30Z"), At("2026-09-15T08:09:30Z")));

        Assert.Equal(At("2026-09-15T08:09:30Z"), Assert.Single(journal.Snapshot()).EndedAt);
    }

    [Fact]
    public void UnknownEpisodeIdCreatesNewEntry()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        Assert.True(journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:01:00Z"))));
        Assert.Single(journal.Snapshot());
    }

    [Fact]
    public void UnchangedMergeReturnsFalseAndDoesNotAppend()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        var item = Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:01:00Z"));
        Assert.True(journal.Merge(item));
        long sizeAfterFirst = new FileInfo(path).Length;
        Assert.False(journal.Merge(item));
        Assert.Equal(sizeAfterFirst, new FileInfo(path).Length);
    }

    [Fact]
    public void ForeignMachineUnderSameEpisodeIdIsRejected()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:01:00Z"), machineId: "aidite2"));
        Assert.False(journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:02:00Z"), machineId: "aidite5")));
        Assert.Equal("aidite2", Assert.Single(journal.Snapshot()).MachineId);
    }

    [Fact]
    public void TestMachineIsExcludedFromMerge()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        Assert.False(journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:01:00Z"), machineId: "test")));
        Assert.Empty(journal.Snapshot());
    }

    [Fact]
    public void MergeClosureCompletesAlreadyKnownEpisodeAtTheLastRedObservation()
    {
        // Dlaczego nie o 08:04: EpisodeClosure.At to ClosedAt obserwatora, a ten niesie opóźnienie
        // potwierdzenia naprawy (episodeCloseAfterMs, domyślnie 30 s). Obserwator zapisuje u siebie
        // LastSeenAt (patrz komentarz w Record), więc komputer bez obserwacji musi zrobić to samo —
        // inaczej wspólny raport pokazywałby na każdym komputerze inny czas tego samego postoju.
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:03:00Z")));
        Assert.True(journal.MergeClosure(Episode, At("2026-09-15T08:04:00Z"), "PC-B"));

        var episode = Assert.Single(journal.Snapshot());
        Assert.Equal(At("2026-09-15T08:03:00Z"), episode.EndedAt);
        Assert.False(episode.Interrupted);
        Assert.Equal("PC-B", episode.Origin);
    }

    [Fact]
    public void MergeClosureEarlierThanTheLastObservationWins()
    {
        // Odwrotny kierunek: gdy zamknięcie jest WCZEŚNIEJSZE niż nasza ostatnia obserwacja (obserwacja
        // z sieci dotarła po zamknięciu albo zegary się rozjechały), koniec bierzemy z zamknięcia —
        // postój nie może „trwać" po chwili, w której obserwator uznał go za skończony.
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:03:00Z")));
        Assert.True(journal.MergeClosure(Episode, At("2026-09-15T08:02:30Z")));

        Assert.Equal(At("2026-09-15T08:02:30Z"), Assert.Single(journal.Snapshot()).EndedAt);
    }

    [Fact]
    public void PeerStillObservingReopensAnEpisodeInterruptedByOurRestart()
    {
        // Najważniejszy przypadek wspólnego raportu: PC-A zabito w trakcie postoju, więc przy starcie
        // zamknął epizod na swojej ostatniej obserwacji (Interrupted). PC-B obserwował dalej. Bez
        // odwołania tego wymuszonego zamknięcia epizod zostałby zamrożony na chwili restartu i raport
        // pokazałby postój krótszy niż rzeczywisty — czyli dokładnie to, czego wspólny raport ma nie robić.
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var before = new DowntimeJournal(path);
        before.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:10:00Z"), origin: "PC-A"));

        var journal = new DowntimeJournal(path);
        var restored = Assert.Single(journal.Snapshot());
        Assert.True(restored.Interrupted);

        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:14:00Z"), origin: "PC-B"));
        var episode = Assert.Single(journal.Snapshot());
        Assert.Null(episode.EndedAt);
        Assert.False(episode.Interrupted);
        Assert.Equal(At("2026-09-15T08:14:00Z"), episode.LastObservedAt);

        // …a prawdziwe zamknięcie od PC-B kończy epizod tam, gdzie PC-B go widział po raz ostatni.
        Assert.True(journal.MergeClosure(Episode, At("2026-09-15T08:14:30Z"), "PC-B"));
        Assert.Equal(At("2026-09-15T08:14:00Z"), Assert.Single(journal.Snapshot()).EndedAt);
    }

    [Fact]
    public void StaleOpenObservationDoesNotReopenAnInterruptedEpisode()
    {
        // Odwrotność testu wyżej: spóźniony pakiet z obserwacją STARSZĄ niż wymuszone zamknięcie nie
        // ma prawa otwierać epizodu z powrotem — wtedy przerwany epizod nigdy by się nie domknął.
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        new DowntimeJournal(path).Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:10:00Z")));

        var journal = new DowntimeJournal(path);
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:07:00Z")));

        var episode = Assert.Single(journal.Snapshot());
        Assert.True(episode.Interrupted);
        Assert.Equal(At("2026-09-15T08:10:00Z"), episode.EndedAt);
    }

    [Fact]
    public void LiveReplicationDoesNotAppendALinePerSecond()
    {
        // Replikacja na żywo dostarcza ten sam otwarty epizod co sekundę (StateMessage u odbiorcy,
        // SetLocalState u obserwatora), a LastObservedAt przesuwa się za każdym razem. Bez progu zapisu
        // downtime.jsonl rósłby o wiersz na sekundę na każdy otwarty błąd, na każdym komputerze grupy.
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        var start = At("2026-09-15T08:00:00Z");
        for (int second = 0; second <= 60; second++)
            journal.Merge(Make(start, start.AddSeconds(second)));

        int lines = File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
        Assert.InRange(lines, 1, 7); // pierwszy wpis + najwyżej co dziesiąta sekunda z minuty
        // W pamięci epizod jest mimo to zawsze aktualny — próg dotyczy wyłącznie zapisu.
        Assert.Equal(start.AddSeconds(60), Assert.Single(journal.Snapshot()).LastObservedAt);
    }

    [Fact]
    public void FlushWritesTheObservationHeldBackByTheWriteThreshold()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        var start = At("2026-09-15T08:00:00Z");
        journal.Merge(Make(start, start));
        journal.Merge(Make(start, start.AddSeconds(3)));
        // Bez Flush restart zgubiłby te 3 s: na dysku jest dopiero pierwsza wersja wpisu.
        Assert.Equal(start, Assert.Single(new DowntimeJournal(path).Snapshot()).LastObservedAt);

        journal.Flush();
        Assert.Equal(start.AddSeconds(3), Assert.Single(new DowntimeJournal(path).Snapshot()).LastObservedAt);
    }

    [Fact]
    public void ClosureIsWrittenImmediatelyDespiteTheWriteThreshold()
    {
        // Próg zapisu dotyczy WYŁĄCZNIE przesuwania się czasu obserwacji. Koniec epizodu (jak i tekst
        // błędu czy komputer) musi trafić na dysk od razu, bo to jego zgubienie zmienia raport.
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        var start = At("2026-09-15T08:00:00Z");
        journal.Merge(Make(start, start));
        journal.Merge(Make(start, start.AddSeconds(2), start.AddSeconds(2)));

        var reloaded = Assert.Single(new DowntimeJournal(path).Snapshot());
        Assert.Equal(start.AddSeconds(2), reloaded.EndedAt);
        Assert.False(reloaded.Interrupted);
    }

    [Fact]
    public void MergeClosureOfUnknownEpisodeIsANoOp()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        Assert.False(journal.MergeClosure(Guid.NewGuid(), At("2026-09-15T08:04:00Z")));
        Assert.Empty(journal.Snapshot());
    }

    // --- dziennik jako źródło i cel uzupełniania po włączeniu komputera ---

    [Fact]
    public void EpisodesSinceReturnsOnlyTheFreshTailInAStableOrder()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        journal.Merge(Make(At("2026-09-10T08:00:00Z"), At("2026-09-10T08:05:00Z"), id: Guid.NewGuid()));
        journal.Merge(Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:05:00Z"), id: Guid.NewGuid()));
        journal.Merge(Make(At("2026-09-15T09:00:00Z"), At("2026-09-15T09:05:00Z"), id: Guid.NewGuid()));

        var tail = journal.EpisodesSince(At("2026-09-15T00:00:00Z"), 10);

        Assert.Equal(2, tail.Count);
        // Kolejność rosnąca po ostatniej obserwacji: na niej opiera się znacznik dalszego ciągu
        // (DowntimeSyncReply.Cursor), więc porcje nie mogą przychodzić w przypadkowej kolejności.
        Assert.Equal(At("2026-09-15T08:05:00Z"), tail[0].LastObservedAt);
        Assert.Equal(At("2026-09-15T09:05:00Z"), tail[1].LastObservedAt);
        Assert.Equal(At("2026-09-15T09:05:00Z"), journal.LatestObservedAt);
    }

    [Fact]
    public void EpisodesSinceStopsAtTheLimitSoOneReplySeriesStaysBounded()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        for (int i = 0; i < 20; i++)
            journal.Merge(Make(At("2026-09-15T08:00:00Z").AddMinutes(i), At("2026-09-15T08:05:00Z").AddMinutes(i),
                id: Guid.NewGuid()));

        var page = journal.EpisodesSince(At("2026-09-01T00:00:00Z"), 5);

        Assert.Equal(5, page.Count);
        // Najstarsze najpierw: proszący dopyta od kursora, a nie zacznie od świeżych i zgubi resztę.
        Assert.Equal(At("2026-09-15T08:05:00Z"), page[0].LastObservedAt);
    }

    [Fact]
    public void EmptyJournalHasNoLatestObservation()
    {
        using var dir = new TempDir("downtime");
        Assert.Null(new DowntimeJournal(dir.File("downtime.jsonl")).LatestObservedAt);
    }

    [Fact]
    public void ApplyMergesForeignEpisodesAndReportsWhetherAnythingChanged()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        var incoming = Make(At("2026-09-15T08:00:00Z"), At("2026-09-15T08:05:00Z"),
            ended: At("2026-09-15T08:05:00Z"), origin: "PC-A");

        Assert.True(journal.Apply([incoming]));
        // Powtórzona porcja (zgubiony ACK, druga prośba) nie może nic zmienić ani nic dopisać.
        Assert.False(journal.Apply([incoming]));
        Assert.Single(journal.Snapshot());
    }

    [Theory]
    [InlineData("brak identyfikatora")]
    [InlineData("brak maszyny")]
    [InlineData("koniec przed początkiem")]
    [InlineData("tekst ponad limit")]
    [InlineData("maszyna testowa")]
    public void ApplyRejectsAnEntryThatCouldNotHaveComeFromARealObservation(string why)
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        var start = At("2026-09-15T08:00:00Z");
        DowntimeEpisode bad = why switch
        {
            "brak identyfikatora" => new("aidite2", "Aidite 2", "Servo", start, start, null),
            "brak maszyny" => new("", "Aidite 2", "Servo", start, start, null, EpisodeId: Guid.NewGuid()),
            "koniec przed początkiem" => new("aidite2", "Aidite 2", "Servo", start, start.AddMinutes(-5), null,
                EpisodeId: Guid.NewGuid()),
            "tekst ponad limit" => new("aidite2", "Aidite 2", new string('x', 501), start, start, null,
                EpisodeId: Guid.NewGuid()),
            _ => new("test", "Test", "Servo", start, start, null, EpisodeId: Guid.NewGuid()),
        };

        Assert.False(journal.Apply([bad]));
        Assert.Empty(journal.Snapshot());
        // Nie wolno też zostawić po nim śladu w pliku: dziennik czyta się w całości przy każdym starcie.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ApplyKeepsTheGoodEntriesFromAPartlyDamagedChunk()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        var start = At("2026-09-15T08:00:00Z");
        var good = Make(start, start.AddMinutes(5), ended: start.AddMinutes(5), id: Guid.NewGuid());
        var bad = new DowntimeEpisode("aidite2", "Aidite 2", "Servo", start, start, null);

        // Jeden zepsuty wpis w porcji nie może kasować pozostałych: partner przysyła setki naraz.
        Assert.True(journal.Apply([bad, good]));
        Assert.Equal(good.EpisodeId, Assert.Single(journal.Snapshot()).EpisodeId);
    }

    [Fact]
    public void LegacyRowWithoutEpisodeIdGetsStableDeterministicId()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        // Wiersz w formacie bez pola EpisodeId (i bez Origin).
        File.WriteAllText(path,
            """{"MachineId":"aidite2","MachineName":"Aidite 2","Reason":"Servo 6","StartedAt":"2026-09-15T08:00:00+00:00","LastObservedAt":"2026-09-15T08:05:00+00:00","EndedAt":"2026-09-15T08:05:00+00:00","Interrupted":false}""" + "\n");

        var expected = DowntimeEpisode.LegacyId("aidite2", At("2026-09-15T08:00:00Z"));
        var first = Assert.Single(new DowntimeJournal(path).Snapshot());
        Assert.Equal(expected, first.EpisodeId);
        // Wczytanie ponownie tego samego pliku musi dać ten sam identyfikator — inaczej dwa uruchomienia
        // programu widziałyby ten sam stary wiersz jako dwa różne epizody.
        var second = Assert.Single(new DowntimeJournal(path).Snapshot());
        Assert.Equal(expected, second.EpisodeId);
    }
}
