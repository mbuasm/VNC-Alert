using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeReportTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);
    private static DowntimeEpisode Episode(string start, string end) =>
        new("aidite2", "Aidite 2", "Servo 6", At(start), At(end), At(end));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(180, 1)]
    [InlineData(181, 1)]
    public void ThresholdIsStrictAndCountsWholeEpisode(int seconds, int count)
    {
        var start = At("2026-09-15T08:00:00Z");
        var episode = new DowntimeEpisode("a", "A", "Błąd", start, start.AddSeconds(seconds), null);
        var rows = DowntimeReport.Build([episode], TimeZoneInfo.Utc);
        Assert.Equal(count, rows.Count);
        if (count > 0) Assert.Equal(seconds / 60d, rows.Sum(r => r.Minutes));
    }

    [Fact]
    public void SplitsAtShiftBoundaryWithoutLosingMinutes()
    {
        var rows = DowntimeReport.Build([Episode("2026-09-15T13:58:00Z", "2026-09-15T14:04:00Z")], TimeZoneInfo.Utc);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Minutes);
        Assert.Equal("06:00–14:00", rows[0].Shift);
        Assert.Equal(4, rows[1].Minutes);
        Assert.Equal("14:00–22:00", rows[1].Shift);
    }

    [Fact]
    public void NightBelongsToDayItStartedAndSplitsAtSix()
    {
        var rows = DowntimeReport.Build([Episode("2026-09-16T05:57:00Z", "2026-09-16T06:05:00Z")], TimeZoneInfo.Utc);
        Assert.Equal(new DateOnly(2026, 9, 15), rows[0].Day);
        Assert.Equal("22:00–06:00", rows[0].Shift);
        Assert.Equal(new DateOnly(2026, 9, 16), rows[1].Day);
        Assert.Equal(8, rows.Sum(r => r.Minutes));
    }

    [Theory]
    [InlineData("2026-03-25T01:50:00+01:00", "2026-03-25T03:10:00+02:00", 20)]
    [InlineData("2026-10-28T02:50:00+02:00", "2026-10-28T02:10:00+01:00", 20)]
    public void DaylightSavingCountsActualElapsedTime(string start, string end, int minutes)
    {
        var zone = TestZones.WednesdayDst(2);
        var rows = DowntimeReport.Build([Episode(start, end)], zone);
        var row = Assert.Single(rows);
        Assert.Equal(minutes, rows.Sum(r => r.Minutes));
        Assert.Equal("22:00–06:00", row.Shift);
    }

    // Ta sama noc w Europe/Warsaw to noc z soboty na niedzielę i z niedzieli — w całości weekend.
    [Theory]
    [InlineData("2026-03-29T01:50:00+01:00", "2026-03-29T03:10:00+02:00")]
    [InlineData("2026-10-25T02:50:00+02:00", "2026-10-25T02:10:00+01:00")]
    public void DaylightSavingNightInWarsawIsWeekendAndNotReported(string start, string end)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        Assert.Empty(DowntimeReport.Build([Episode(start, end)], zone));
    }

    [Fact]
    public void ConcurrentMachineDowntimesAreSummed()
    {
        var first = Episode("2026-09-15T08:00:00Z", "2026-09-15T09:00:00Z");
        var second = first with { MachineId = "aidite3", MachineName = "Aidite 3" };
        Assert.Equal(120, DowntimeReport.Build([first, second], TimeZoneInfo.Utc).Sum(r => r.Minutes));
    }


    [Theory]
    [InlineData(3600, false)]
    [InlineData(3601, true)]
    public void GornyProgGodziny_DokladnieGodzinaZwyklyDluzszyOznaczonyJakoDlugi(int seconds, bool isLong)
    {
        var start = At("2026-09-15T08:00:00Z");
        var episode = new DowntimeEpisode("a", "A", "Błąd", start, start.AddSeconds(seconds), null);
        var rows = DowntimeReport.Build([episode], TimeZoneInfo.Utc);
        Assert.NotEmpty(rows);
        Assert.Equal(seconds / 60d, rows.Sum(r => r.Minutes), 6);
        Assert.All(rows, r => Assert.Equal(isLong, r.IsLong));
    }

    [Fact]
    public void PostojPrzezNoc_JestWRaporcieJakoDlugiINieOznaczaInnychEpizodow()
    {
        // Typowy przypadek: maszyna zostaje z komunikatem od końca zmiany do rana. Taki
        // epizod dzieli się na trzy zmiany — wszystkie kawałki są w raporcie z oznaczeniem długiego postoju,
        // a zwykły 10-minutowy postój innej maszyny tego samego wieczoru zostaje bez oznaczenia.
        var overnight = Episode("2026-09-15T20:30:00Z", "2026-09-16T07:15:00Z");
        var normal = Episode("2026-09-15T20:40:00Z", "2026-09-15T20:50:00Z") with { MachineId = "aidite3", MachineName = "Aidite 3" };

        var rows = DowntimeReport.Build([overnight, normal], TimeZoneInfo.Utc);
        var night = rows.Where(r => r.Machine == "Aidite 2").ToArray();
        Assert.Equal(3, night.Length);
        Assert.All(night, r => Assert.True(r.IsLong));
        Assert.Equal(645, night.Sum(r => r.Minutes));
        var row = Assert.Single(rows, r => r.Machine == "Aidite 3");
        Assert.Equal(10, row.Minutes);
        Assert.False(row.IsLong);
    }

    [Fact]
    public void GornyProg_LiczySiePoScaleniuObserwatorow()
    {
        // Dwa obserwatory, każdy po 40 minut, zachodzące na siebie: to JEDEN postój 80 minut, czyli długi.
        // Gdyby próg liczył się przed scaleniem, żaden kawałek nie byłby oznaczony.
        var first = Piece("2026-09-15T08:00:00Z", "2026-09-15T08:40:00Z", Guid.NewGuid());
        var second = Piece("2026-09-15T08:40:00Z", "2026-09-15T09:20:00Z", Guid.NewGuid());

        var rows = DowntimeReport.Build([first, second], TimeZoneInfo.Utc);
        Assert.Equal(80, rows.Sum(r => r.Minutes));
        Assert.All(rows, r => Assert.True(r.IsLong));
        Assert.Equal(1, DowntimeReport.Totals(rows).All.Episodes);
    }

    [Fact]
    public void TrwajacyEpizod_PoGodzinieZostajeWRaporcieJakoDlugi()
    {
        // Otwarty epizod liczy się do ostatniej obserwacji. Przez pierwszą godzinę jest zwykłym postojem;
        // gdy przekroczy godzinę, zostaje w raporcie (nadal otwarty) i dostaje oznaczenie długiego postoju.
        var start = At("2026-09-15T08:00:00Z");
        var early = new DowntimeEpisode("a", "A", "Błąd", start, start.AddMinutes(50), null);
        var late = early with { LastObservedAt = start.AddMinutes(61) };

        var earlyRow = Assert.Single(DowntimeReport.Build([early], TimeZoneInfo.Utc));
        Assert.Equal(50, earlyRow.Minutes);
        Assert.False(earlyRow.IsLong);
        Assert.True(earlyRow.Ongoing);
        var lateRows = DowntimeReport.Build([late], TimeZoneInfo.Utc);
        Assert.Equal(61, lateRows.Sum(r => r.Minutes), 6);
        Assert.All(lateRows, r => { Assert.True(r.IsLong); Assert.True(r.Ongoing); });
        Assert.True(DowntimeReport.IsReported(early));
        Assert.True(DowntimeReport.IsReported(late));
        Assert.False(DowntimeReport.IsLong(early));
        Assert.True(DowntimeReport.IsLong(late));
    }


    // --- raport bez podwójnego liczenia ---

    private static DowntimeEpisode Piece(string start, string end, Guid id, bool interrupted = false,
        string reason = "Servo 6") =>
        new("aidite2", "Aidite 2", reason, At(start), At(end), At(end), interrupted, id);

    [Fact]
    public void NakladajaceSieEpizodyDwochObserwatorowLiczaSieRaz()
    {
        // Dlaczego: po podziale sieci obie połowy grupy mają własnego obserwatora i ten sam fizyczny
        // postój dostaje DWA różne EpisodeId. Bez scalenia raport pokazałby 20 minut zamiast 12.
        var first = Piece("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", Guid.NewGuid());
        var second = Piece("2026-09-15T08:02:00Z", "2026-09-15T08:12:00Z", Guid.NewGuid());

        Assert.Equal(12, DowntimeReport.Build([first, second], TimeZoneInfo.Utc).Sum(r => r.Minutes));
    }

    [Fact]
    public void RozlaczneEpizodyTejSamejMaszynyZostajaOsobno()
    {
        var first = Piece("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", Guid.NewGuid());
        var second = Piece("2026-09-15T09:00:00Z", "2026-09-15T09:10:00Z", Guid.NewGuid());

        var rows = DowntimeReport.Build([first, second], TimeZoneInfo.Utc);
        Assert.Equal(2, rows.Count);
        Assert.Equal(20, rows.Sum(r => r.Minutes));
    }

    [Fact]
    public void ScalanieNieLaczyRoznychMaszyn()
    {
        // Równoległe postoje dwóch maszyn nadal sumują się osobno — to nie jest podwójne liczenie.
        var first = Piece("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", Guid.NewGuid());
        var second = first with { MachineId = "aidite3", MachineName = "Aidite 3", EpisodeId = Guid.NewGuid() };

        Assert.Equal(20, DowntimeReport.Build([first, second], TimeZoneInfo.Utc).Sum(r => r.Minutes));
    }

    [Fact]
    public void ScalanieIdziePrzedProgiemTrzechMinut()
    {
        // Dwa kawałki po 2 minuty, zachodzące na siebie o minutę, to jeden postój 3,5 minuty — czyli
        // taki, który do raportu WCHODZI. Gdyby próg liczył się przed scaleniem, zniknąłby.
        var first = Piece("2026-09-15T08:00:00Z", "2026-09-15T08:02:00Z", Guid.NewGuid());
        var second = Piece("2026-09-15T08:01:30Z", "2026-09-15T08:03:30Z", Guid.NewGuid());

        var row = Assert.Single(DowntimeReport.Build([first, second], TimeZoneInfo.Utc));
        Assert.Equal(3.5, row.Minutes, 6);
    }

    [Fact]
    public void TrwajacyEpizodWygrywaNadWczesniejszymZamknieciem()
    {
        // Jeden obserwator zdążył domknąć swój epizod, drugi wciąż widzi ten sam postój. Wynik ma być
        // otwarty i sięgać ostatniej obserwacji, nie kończyć się na pierwszym zamknięciu.
        var closed = Piece("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", Guid.NewGuid());
        var ongoing = new DowntimeEpisode("aidite2", "Aidite 2", "Servo 1", At("2026-09-15T08:05:00Z"),
            At("2026-09-15T08:20:00Z"), null, false, Guid.NewGuid());

        var rows = DowntimeReport.Build([closed, ongoing], TimeZoneInfo.Utc);
        Assert.Equal(20, rows.Sum(r => r.Minutes));
        Assert.All(rows, r => Assert.True(r.Ongoing));
        Assert.All(rows, r => Assert.Equal("Servo 1", r.Reason));
    }

    [Fact]
    public void ScalanieJestIdempotentne()
    {
        var first = Piece("2026-09-15T08:00:00Z", "2026-09-15T08:10:00Z", Guid.NewGuid());
        var second = Piece("2026-09-15T08:02:00Z", "2026-09-15T08:12:00Z", Guid.NewGuid());

        var once = DowntimeReport.MergeOverlaps([first, second]);
        Assert.Equal(once, DowntimeReport.MergeOverlaps(once));
    }

    // EpisodeId jest kluczem scalania w DowntimeJournal. Testy
    // poniżej budują kilka NIEZALEŻNYCH instancji Alert dla "tego samego" epizodu (bez łańcucha `with`, jak
    // robi to prawdziwy AlertStateMachine) — LegacyId(machineId, start) daje im tę samą tożsamość,
    // żeby te testy sprawdzały scalanie, a nie tworzenie nowych wpisów.
    private static Alert Alarm(DateTimeOffset start, int minutes, AlertStatus status = AlertStatus.Active) => new()
    {
        Key = AlertKey.ForRule("aidite2", "code-705"), MachineId = "aidite2", MachineName = "Aidite 2",
        ShortText = "Servo 6", Category = "restart", Kind = AlertKind.Error,
        StartedAt = start, LastSeenAt = start.AddMinutes(minutes), Status = status,
        EpisodeId = DowntimeEpisode.LegacyId("aidite2", start),
    };

    [Fact]
    public void AckAndBetterCodeDoNotSplitOrEndEpisode()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        var start = At("2026-09-15T08:00:00Z");
        journal.Record(Alarm(start, 0));
        journal.Record(Alarm(start, 4, AlertStatus.Acknowledged));
        journal.Record(Alarm(start, 5) with { Key = AlertKey.ForRule("aidite2", "code-700"), ShortText = "Servo 1" });
        var episode = Assert.Single(journal.Snapshot());
        Assert.Null(episode.EndedAt);
        Assert.Equal("Servo 1", episode.Reason);
        Assert.Equal(5, DowntimeReport.Build([episode], TimeZoneInfo.Utc).Single().Minutes);
    }

    [Fact]
    public void RestartDoesNotCountUnobservedTimeAndDoesNotLoseHistory()
    {
        using var dir = new TempDir("downtime");
        var path = dir.File("downtime.jsonl");
        var journal = new DowntimeJournal(path);
        journal.Record(Alarm(At("2026-09-15T08:00:00Z"), 5));
        var recovered = new DowntimeJournal(path);
        var episode = Assert.Single(recovered.Snapshot());
        Assert.True(episode.Interrupted);
        Assert.Equal(episode.LastObservedAt, episode.EndedAt);
        Assert.Null(recovered.Error);
        Assert.Equal(5, DowntimeReport.Build([episode], TimeZoneInfo.Utc).Single().Minutes);
    }

    [Fact]
    public void ClosedUsesLastRedObservationAndCannotBeReopenedByStaleTick()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        var alert = Alarm(At("2026-09-15T08:00:00Z"), 5, AlertStatus.Closed) with { ClosedAt = At("2026-09-15T08:05:30Z") };
        journal.Record(alert);
        journal.Record(alert with { Status = AlertStatus.Active });
        Assert.Equal(alert.LastSeenAt, Assert.Single(journal.Snapshot()).EndedAt);
    }

    [Fact]
    public void NonErrorAndSyntheticMenuTestsAreExcluded()
    {
        using var dir = new TempDir("downtime");
        var journal = new DowntimeJournal(dir.File("downtime.jsonl"));
        var alert = Alarm(At("2026-09-15T08:00:00Z"), 5);
        journal.Record(alert with { Kind = AlertKind.Offline });
        journal.Record(alert with { Kind = AlertKind.WrongScreen });
        journal.Record(alert with { MachineId = "test" });
        Assert.Empty(journal.Snapshot());
    }

    [Fact]
    public void TornWriteIsReportedAndLaterValidRecordsSurvive()
    {
        using var dir = new TempDir("downtime");
        string path = dir.File("downtime.jsonl");
        File.WriteAllText(path, "{broken");
        var journal = new DowntimeJournal(path);
        Assert.NotNull(journal.Error);
        journal.Record(Alarm(At("2026-09-15T08:00:00Z"), 5));
        Assert.Single(new DowntimeJournal(path).Snapshot());
    }
}
