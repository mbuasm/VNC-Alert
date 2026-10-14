using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimeReportAllDowntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ma-all-downtime-" + Guid.NewGuid().ToString("N"));
    public DowntimeReportAllDowntimeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);

    private static Alert IdleAlert(bool atZero, DateTimeOffset start, DateTimeOffset last, bool closed = true) => new()
    {
        Key = AlertKey.Idle("aidite2"), MachineId = "aidite2", MachineName = "Aidite 2", Kind = AlertKind.MachineIdle,
        ShortText = atZero ? AlertStateMachine.MachineIdleAtZeroShortText : AlertStateMachine.MachineIdleShortText,
        Category = atZero ? AlertStateMachine.IdleAtZeroCategory : AlertStateMachine.IdleCategory,
        StartedAt = start, LastSeenAt = last, Status = closed ? AlertStatus.Closed : AlertStatus.Active,
        ClosedAt = closed ? last.AddSeconds(1) : null,
    };

    private static DowntimeEpisode Ep(string machine, string reason, string? category, string start, string end) =>
        new(machine, "Nazwa " + machine, reason, At(start), At(end), At(end), EpisodeId: Guid.NewGuid(), Category: category);

    [Theory]
    [InlineData(AlertKind.Error, true)]
    [InlineData(AlertKind.MachineIdle, true)]
    [InlineData(AlertKind.Offline, false)]
    [InlineData(AlertKind.WrongScreen, false)]
    public void DziennikPrzyjmujeBledyIPostojeBezPracy(AlertKind kind, bool recorded) =>
        Assert.Equal(recorded, DowntimeJournal.IsRecordedKind(kind));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_PostojBezPracy_TrafiaDoDziennikaZPowodemIRodzajem(bool atZero)
    {
        var path = Path.Combine(_dir, "downtime.jsonl");
        var journal = new DowntimeJournal(path);
        var alert = IdleAlert(atZero, At("2026-09-15T08:00:00Z"), At("2026-09-15T08:20:00Z"));

        Assert.True(journal.Record(alert, "PC1"));

        var episode = Assert.Single(new DowntimeJournal(path).Snapshot());
        Assert.Equal(alert.ShortText, episode.Reason);
        Assert.Equal(atZero ? DowntimeCategories.IdleAtZero : DowntimeCategories.Idle, episode.Category);
        Assert.Equal(alert.EpisodeId, episode.EpisodeId);
        Assert.Equal(alert.LastSeenAt, episode.EndedAt);
    }

    [Fact]
    public void Record_InneRodzajeAlertu_NieTrafiajaDoDziennika()
    {
        var journal = new DowntimeJournal(Path.Combine(_dir, "other.jsonl"));
        var offline = IdleAlert(false, At("2026-09-15T08:00:00Z"), At("2026-09-15T08:20:00Z")) with
        { Kind = AlertKind.Offline, Key = AlertKey.Offline("aidite2") };
        Assert.False(journal.Record(offline));
        Assert.Empty(journal.Snapshot());
    }

    [Fact]
    public void FromAlert_EpizodZSieci_PostojBezPracy_TakSamoJakLokalny()
    {
        var alert = IdleAlert(false, At("2026-09-15T08:00:00Z"), At("2026-09-15T08:20:00Z"), closed: false);
        var episode = DowntimeEpisode.FromAlert(alert, "OBSERWATOR");
        Assert.Equal(DowntimeCategories.Idle, episode.Category);
        Assert.Null(episode.EndedAt);
        Assert.Equal("OBSERWATOR", episode.Origin);

        var journal = new DowntimeJournal(Path.Combine(_dir, "net.jsonl"));
        Assert.True(journal.Apply([episode]));
        Assert.True(journal.MergeClosure(alert.EpisodeId, At("2026-09-15T08:21:00Z"), "OBSERWATOR"));
        var closed = Assert.Single(journal.Snapshot());
        Assert.Equal(At("2026-09-15T08:20:00Z"), closed.EndedAt);
        Assert.False(closed.Interrupted);
        Assert.Equal(DowntimeCategories.Idle, closed.Category);
    }

    [Fact]
    public void Build_PostojeBezPracy_WRaporcieZPowodemIRodzajem()
    {
        var rows = DowntimeReport.Build([
            Ep("a", AlertStateMachine.MachineIdleShortText, AlertStateMachine.IdleCategory, "2026-09-15T08:00:00Z", "2026-09-15T08:30:00Z"),
            Ep("b", AlertStateMachine.MachineIdleAtZeroShortText, AlertStateMachine.IdleAtZeroCategory, "2026-09-15T09:00:00Z", "2026-09-15T09:10:00Z"),
        ], TimeZoneInfo.Utc);

        Assert.Equal(2, rows.Count);
        var full = rows.Single(r => r.MachineId == "a");
        Assert.Equal("Stoi bez pracy (100 %) – dołóż prace", full.Reason);
        Assert.Equal(DowntimeCategories.Idle, full.Category);
        Assert.Equal(30, full.Minutes, 6);
        var zero = rows.Single(r => r.MachineId == "b");
        Assert.Equal("Stoi na 0 % – wciśnij Start", zero.Reason);
        Assert.Equal(DowntimeCategories.IdleAtZero, zero.Category);
    }

    [Fact]
    public void Build_PostojBezKategorii_RodzajZTekstu()
    {
        // Wpis przekazany dalej przez wersję bez pola Category — rodzaj z tekstu, nie „nierozpoznany”.
        var episode = Ep("a", AlertStateMachine.MachineIdleAtZeroShortText, null, "2026-09-15T08:00:00Z", "2026-09-15T08:30:00Z");
        Assert.Equal(DowntimeCategories.IdleAtZero, Assert.Single(DowntimeReport.Build([episode], TimeZoneInfo.Utc)).Category);
        var classifier = new DowntimeCategoryClassifier(new RuleSet(), []);
        Assert.Equal(DowntimeCategories.IdleAtZero, classifier.CategoryOf(episode));
        Assert.Equal(DowntimeCategories.Idle, classifier.Classify("a", AlertStateMachine.MachineIdleShortText));
        Assert.Equal(DowntimeCategories.IdleAtZero,
            Assert.Single(DowntimeReport.Build([episode], TimeZoneInfo.Utc, classifier: classifier)).Category);
    }

    [Fact]
    public void Build_EpizodJednejMinuty_JestWRaporcie()
    {
        var episode = Ep("a", "Servo 6", "inny", "2026-09-15T08:00:00Z", "2026-09-15T08:01:00Z");
        Assert.True(DowntimeReport.IsReported(episode));
        var row = Assert.Single(DowntimeReport.Build([episode], TimeZoneInfo.Utc));
        Assert.Equal(1, row.Minutes, 6);
        Assert.Equal(TimeSpan.Zero, DowntimeReport.MinEpisode);
    }

    [Fact]
    public void Build_EpizodKilkuSekund_JestWRaporcie_ZerowyNie()
    {
        var shortOne = Ep("a", "Servo 6", "inny", "2026-09-15T08:00:00Z", "2026-09-15T08:00:05Z");
        var zero = Ep("b", "Servo 6", "inny", "2026-09-15T08:00:00Z", "2026-09-15T08:00:00Z");
        var rows = DowntimeReport.Build([shortOne, zero], TimeZoneInfo.Utc);
        Assert.Equal("a", Assert.Single(rows).MachineId);
    }

    [Fact]
    public void Build_PostojStykajacySieZBledem_NieJestScalanyZBledem()
    {
        // Błąd zamyka alert postoju w tej samej chwili — przy scalaniu „styk = jeden postój” rodzaj postoju by zniknął.
        var rows = DowntimeReport.Build([
            Ep("a", AlertStateMachine.MachineIdleShortText, AlertStateMachine.IdleCategory, "2026-09-15T08:00:00Z", "2026-09-15T08:30:00Z"),
            Ep("a", "Servo 6", "inny", "2026-09-15T08:30:00Z", "2026-09-15T08:40:00Z"),
        ], TimeZoneInfo.Utc);

        Assert.Equal(2, rows.Count);
        Assert.Equal(30, rows.Single(r => r.Category == DowntimeCategories.Idle).Minutes, 6);
        Assert.Equal(10, rows.Single(r => r.Category == "inny").Minutes, 6);
        var totals = DowntimeReport.Totals(rows);
        Assert.Equal(2, totals.All.Episodes);
        Assert.Equal(40, totals.All.Minutes, 6);
    }

    [Fact]
    public void Build_PostojNakladajacySieNaBlad_BladMaPierwszenstwo_BezPodwojnegoLiczenia()
    {
        // Błąd 08:20–08:40 w środku postoju 08:00–09:00 (np. epizod błędu wznowiony po krótkiej przerwie czerwieni).
        var idleId = Guid.NewGuid();
        var rows = DowntimeReport.Build([
            new DowntimeEpisode("a", "A", AlertStateMachine.MachineIdleShortText, At("2026-09-15T08:00:00Z"),
                At("2026-09-15T09:00:00Z"), At("2026-09-15T09:00:00Z"), EpisodeId: idleId, Category: AlertStateMachine.IdleCategory),
            Ep("a", "Servo 6", "inny", "2026-09-15T08:20:00Z", "2026-09-15T08:40:00Z"),
        ], TimeZoneInfo.Utc);

        Assert.Equal(20, rows.Where(r => r.Category == "inny").Sum(r => r.Minutes), 6);
        var idle = rows.Where(r => r.Category == DowntimeCategories.Idle).ToArray();
        Assert.Equal(40, idle.Sum(r => r.Minutes), 6);
        Assert.All(idle, r => Assert.Equal(idleId, r.EpisodeId));
        var totals = DowntimeReport.Totals(rows);
        Assert.Equal(60, totals.All.Minutes, 6);
        Assert.Equal(2, totals.All.Episodes);
    }

    [Fact]
    public void Build_PostojCalyWBledzie_ZnikaZRaportu()
    {
        var rows = DowntimeReport.Build([
            Ep("a", AlertStateMachine.MachineIdleShortText, AlertStateMachine.IdleCategory, "2026-09-15T08:10:00Z", "2026-09-15T08:20:00Z"),
            Ep("a", "Servo 6", "inny", "2026-09-15T08:00:00Z", "2026-09-15T08:30:00Z"),
        ], TimeZoneInfo.Utc);
        Assert.Equal("inny", Assert.Single(rows).Category);
    }

    [Fact]
    public void Build_PostojInnejMaszyny_NieJestPrzycinanyBledem()
    {
        var rows = DowntimeReport.Build([
            Ep("a", AlertStateMachine.MachineIdleShortText, AlertStateMachine.IdleCategory, "2026-09-15T08:00:00Z", "2026-09-15T08:30:00Z"),
            Ep("b", "Servo 6", "inny", "2026-09-15T08:00:00Z", "2026-09-15T08:30:00Z"),
        ], TimeZoneInfo.Utc);
        Assert.Equal(60, rows.Sum(r => r.Minutes), 6);
    }

    [Fact]
    public void Build_DlugiPostojBezPracy_OznaczonyISumyZIBezDlugich()
    {
        var rows = DowntimeReport.Build([
            Ep("a", AlertStateMachine.MachineIdleShortText, AlertStateMachine.IdleCategory, "2026-09-15T08:00:00Z", "2026-09-15T09:30:00Z"),
            Ep("b", "Servo 6", "inny", "2026-09-15T08:00:00Z", "2026-09-15T08:02:00Z"),
        ], TimeZoneInfo.Utc);
        Assert.All(rows.Where(r => r.MachineId == "a"), r => Assert.True(r.IsLong));
        var totals = DowntimeReport.Totals(rows);
        Assert.Equal(92, totals.All.Minutes, 6);
        Assert.Equal(2, totals.All.Episodes);
        Assert.Equal(2, totals.WithoutLong.Minutes, 6);
        Assert.Equal(1, totals.WithoutLong.Episodes);
    }

    [Theory]
    [InlineData("postoj", "Bez pracy (100 %)")]
    [InlineData("postoj-0", "Stoi na 0 %")]
    public void Prezentacja_RodzajPostoju(string category, string expected) =>
        Assert.Equal(expected, DowntimePresentation.CategoryName(category));

    [Theory]
    [InlineData("Stoi bez pracy (100 %) – dołóż prace")]
    [InlineData("Stoi na 0 % – wciśnij Start")]
    public void Prezentacja_PowodPostojuBezZmian(string reason) =>
        Assert.Equal(reason, DowntimePresentation.ErrorName(reason));

    [Fact]
    public void Build_WpisBezMaszyny_NieRzucaWyjatku()
    {
        var path = Path.Combine(_dir, "retained.jsonl");
        File.WriteAllText(path, "{\"test\":\"retained downtime\"}");
        var journal = new DowntimeJournal(path);
        var episodes = journal.Snapshot().Concat([
            new DowntimeEpisode(null!, "X", AlertStateMachine.MachineIdleShortText, At("2026-09-15T08:00:00Z"),
                At("2026-09-15T08:10:00Z"), At("2026-09-15T08:10:00Z"), EpisodeId: Guid.NewGuid(), Category: AlertStateMachine.IdleCategory),
            new DowntimeEpisode(null!, "X", "Servo 6", At("2026-09-15T08:05:00Z"),
                At("2026-09-15T08:07:00Z"), At("2026-09-15T08:07:00Z"), EpisodeId: Guid.NewGuid()),
        ]).ToArray();

        var rows = DowntimeReport.Build(episodes, TimeZoneInfo.Utc, now: At("2026-09-16T00:00:00Z"));
        Assert.Equal(10, rows.Sum(r => r.Minutes), 6); // 8 min postoju bez pracy + 2 min błędu, bez podwójnego liczenia
    }

    [Fact]
    public void StaryPlikDziennika_WczytujeSie_EpizodDwochMinutJestWRaporcie()
    {
        var path = Path.Combine(_dir, "old.jsonl");
        File.WriteAllText(path,
            "{\"MachineId\":\"aidite2\",\"MachineName\":\"Aidite 2\",\"Reason\":\"Servo 6\",\"StartedAt\":\"2026-09-15T08:00:00+00:00\","
            + "\"LastObservedAt\":\"2026-09-15T08:02:00+00:00\",\"EndedAt\":\"2026-09-15T08:02:00+00:00\",\"Interrupted\":false,"
            + "\"EpisodeId\":\"6f1c2a3b-0000-0000-0000-000000000001\",\"Origin\":\"PC1\",\"Category\":\"narzedzie\"}\n"
            + "{\"MachineId\":\"aidite3\",\"MachineName\":\"Aidite 3\",\"Reason\":\"Servo 6\",\"StartedAt\":\"2026-09-15T09:00:00+00:00\","
            + "\"LastObservedAt\":\"2026-09-15T09:10:00+00:00\",\"EndedAt\":\"2026-09-15T09:10:00+00:00\",\"Interrupted\":false,"
            + "\"EpisodeId\":\"6f1c2a3b-0000-0000-0000-000000000002\",\"Origin\":\"PC1\"}\n");
        var journal = new DowntimeJournal(path);
        Assert.Null(journal.Error);
        var rows = DowntimeReport.Build(journal.Snapshot(), TimeZoneInfo.Utc);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Single(r => r.MachineId == "aidite2").Minutes, 6);
    }
}
