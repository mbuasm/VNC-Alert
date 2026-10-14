using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class AuditA2JournalRecoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A2_01_RemoteIntervalIsRetriedAfterWriteLockAndSurvivesRestart()
    {
        using var dir = new TempDir("audit-a2");
        var path = dir.File("observation.jsonl");
        File.WriteAllText(path, "");
        var journal = new ObservationJournal(path);
        var missed = new ObservationInterval("aidite1", ObservationState.Observed, T0, T0.AddMinutes(10));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(journal.Apply([missed]));
            Assert.NotNull(journal.Error);
            Assert.Contains(missed, journal.Snapshot());
        }
        journal.Apply([missed]); // identyczna ponowna odpowiedź grupy po zwolnieniu pliku
        var next = new ObservationInterval("aidite2", ObservationState.Observed, T0, T0.AddMinutes(1));
        journal.Apply([next]);
        Assert.Null(journal.Error); // program już raportuje odzyskany zapis
        journal.CloseAll(); // czyste zamknięcie programu

        Assert.Contains(missed, new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void A2_01_LocallyClosedIntervalIsRetriedAfterWriteLockAndSurvivesRestart()
    {
        using var dir = new TempDir("audit-a2");
        var path = dir.File("observation.jsonl");
        File.WriteAllText(path, "");
        var journal = new ObservationJournal(path);
        journal.Observe([("aidite1", ObservationState.Observed)], T0);
        journal.Observe([("aidite1", ObservationState.Observed)], T0.AddSeconds(5));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal.CloseAll();
            Assert.NotNull(journal.Error);
        }
        journal.CloseAll(); // normalne następne tiki bez roli obserwatora i zamknięcie
        journal.Apply([new ObservationInterval("aidite2", ObservationState.Observed, T0, T0.AddMinutes(1))]);
        Assert.Null(journal.Error);

        Assert.Contains(new ObservationInterval("aidite1", ObservationState.Observed, T0, T0.AddSeconds(5)),
            new ObservationJournal(path).Snapshot());
    }

    [Fact]
    public void A2_02_FirstEventAfterATornCsvWriteIsASeparateCompleteRecord()
    {
        using var dir = new TempDir("audit-a2");
        var path = dir.File("history.csv");
        const string header = "czas;maszyna;zdarzenie;opis;kategoria;pelny_tekst;komputer;uzytkownik";
        File.WriteAllText(path, header + "\n2026-10-07 07:59:59;Aidite 1;now", new UTF8Encoding(true));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        var alert = new Alert
        {
            Key = AlertKey.ForRule("aidite1", "servo"), MachineId = "aidite1", MachineName = "Aidite 1",
            Kind = AlertKind.Error, ShortText = "Servo", FullText = "Servo", Category = "inny",
            StartedAt = T0, LastSeenAt = T0, Status = AlertStatus.Active,
        };
        writer.Append(new AlertEvent(AlertEventKind.Raised, alert), "PC-A", "operator");
        writer.Flush();
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);

        Assert.Contains("2026-10-07 08:00:00;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator", File.ReadAllLines(path));
    }

    [Fact]
    public void A2_01_ExplicitCompactionRecoversIntervalsLeftOnlyInMemory()
    {
        using var dir = new TempDir("audit-a2");
        var path = dir.File("observation.jsonl");
        File.WriteAllText(path, "");
        var journal = new ObservationJournal(path);
        var missed = new ObservationInterval("aidite1", ObservationState.Observed, T0, T0.AddMinutes(10));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal.Apply([missed]);
            Assert.NotNull(journal.Error);
        }
        Assert.Empty(new ObservationJournal(path).Snapshot());
        journal.Compact();
        Assert.Null(journal.Error);
        Assert.Equal(journal.Snapshot(), new ObservationJournal(path).Snapshot());
    }
    [Fact]
    public async Task A2_03_FirstDowntimeSyncBackfillsAnOlderGapEvenWhenALocalEpisodeIsRecent()
    {
        using var dir = new TempDir("audit-a2");
        var clock = new FakeClock(T0);
        var group = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = group.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), clock);
        var aJournal = new DowntimeJournal(dir.File("a.jsonl"));
        var bJournal = new DowntimeJournal(dir.File("b.jsonl"));
        var missed = new DowntimeEpisode("aidite1", "Aidite 1", "Servo", T0.AddDays(-3), T0.AddDays(-3).AddMinutes(15),
            T0.AddDays(-3).AddMinutes(15), EpisodeId: Guid.NewGuid());
        aJournal.Merge(missed);
        bJournal.Merge(new DowntimeEpisode("aidite2", "Aidite 2", "Tool", T0.AddMinutes(-10), T0.AddMinutes(-1),
            T0.AddMinutes(-1), EpisodeId: Guid.NewGuid()));
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "test") { Downtime = aJournal };
        await using var b = new GroupClusterNode(config with { Priority = 200 }, hub.CreateBus("B"), clock, "test") { Downtime = bJournal };
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default);
        for (int second = 0; second < 700; second++)
        {
            await a.TickAsync(default); await b.TickAsync(default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Contains(bJournal.Snapshot(), episode => episode.EpisodeId == missed.EpisodeId);
    }
}


