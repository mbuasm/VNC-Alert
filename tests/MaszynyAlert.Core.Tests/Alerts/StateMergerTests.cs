using System.Text.Json;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class StateMergerTests
{
    private static readonly DateTimeOffset T0 = FakeClock.DefaultStart;

    private static Alert Alert(
        string machineId,
        string ruleId,
        AlertStatus status = AlertStatus.Active,
        int startedOffsetSec = 0,
        int lastSeenOffsetSec = 0,
        AlertKind kind = AlertKind.Error) => new()
    {
        Key = kind == AlertKind.Offline ? AlertKey.Offline(machineId) : AlertKey.ForRule(machineId, ruleId),
        MachineId = machineId,
        MachineName = "Maszyna " + machineId,
        Kind = kind,
        ShortText = "Opis " + ruleId,
        Category = "inny",
        StartedAt = T0.AddSeconds(startedOffsetSec),
        LastSeenAt = T0.AddSeconds(lastSeenOffsetSec),
        Status = status,
        AcknowledgedAt = status == AlertStatus.Acknowledged ? T0.AddSeconds(lastSeenOffsetSec) : null,
        Sound = true,
    };

    private static AckMemory EmptyAcks() => new(new FakeClock(), TimeSpan.FromHours(2));

    [Fact]
    public void Merge_TegoSamegoAlertuOdDwochObserwatorow_ZwracaJedenWpis()
    {
        IReadOnlyList<Alert> merged = StateMerger.Merge(
            [[Alert("m1", "r1")], [Alert("m1", "r1")]],
            EmptyAcks());

        Assert.Single(merged);
    }

    [Theory]
    [InlineData(AlertStatus.Active, AlertStatus.Acknowledged, AlertStatus.Acknowledged)]
    [InlineData(AlertStatus.Acknowledged, AlertStatus.Active, AlertStatus.Acknowledged)]
    [InlineData(AlertStatus.Active, AlertStatus.Escalated, AlertStatus.Escalated)]
    [InlineData(AlertStatus.Escalated, AlertStatus.Active, AlertStatus.Escalated)]
    public void Merge_PrzyKonflikcieAktywnyIPotwierdzonegoLubEskalowanego_WygrywaWyzszaRanga(
        AlertStatus a, AlertStatus b, AlertStatus expected)
    {
        // Active(1) zawsze przegrywa z Acknowledged/Escalated — oba mają wspólną, wyższą rangę (2).
        IReadOnlyList<Alert> merged = StateMerger.Merge(
            [[Alert("m1", "r1", a)], [Alert("m1", "r1", b)]],
            EmptyAcks());

        Assert.Equal(expected, Assert.Single(merged).Status);
    }

    [Fact]
    public void Merge_PrzyKonflikcieAcknowledgedIEscalated_WygrywaNowszeZnacznikiemPrzejscia()
    {
        // obie rangi są równe — wygrywa NOWSZY znacznik ostatniego przejścia
        // (max(AcknowledgedAt, EscalatedAt)), niezależnie od tego, który to status.
        Alert acked = Alert("m1", "r1", AlertStatus.Acknowledged, lastSeenOffsetSec: 100); // AcknowledgedAt = T0+100s

        Alert escalatedPozniej = acked with
        {
            Status = AlertStatus.Escalated,
            AcknowledgedAt = T0,
            EscalatedAt = T0.AddSeconds(200),
        };
        Alert eskalacjaWygrywa = Assert.Single(StateMerger.Merge([[acked], [escalatedPozniej]], EmptyAcks()));
        Assert.Equal(AlertStatus.Escalated, eskalacjaWygrywa.Status);

        Alert escalatedWczesniej = acked with
        {
            Status = AlertStatus.Escalated,
            AcknowledgedAt = T0,
            EscalatedAt = T0.AddSeconds(10),
        };
        Alert potwierdzenieWygrywa = Assert.Single(StateMerger.Merge([[acked], [escalatedWczesniej]], EmptyAcks()));
        Assert.Equal(AlertStatus.Acknowledged, potwierdzenieWygrywa.Status);
    }

    [Fact]
    public void Merge_GdyPamiecAckowZnaKluczAEskalowanyMaNowszyZnacznik_NieNadpisujeStanuAlertu()
    {
        // nadpisanie działa tylko dla
        // alertów Active, więc Escalated (i Acknowledged) nigdy nie są nim cofane.
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        AlertKey key = AlertKey.ForRule("m1", "r1");
        acks.Record(new AckRecord(key, "PC-1", "anna", T0));

        Alert escalated = Alert("m1", "r1", AlertStatus.Escalated) with { EscalatedAt = T0.AddSeconds(500) };
        Alert result = Assert.Single(StateMerger.Merge([[escalated]], acks));

        Assert.Equal(AlertStatus.Escalated, result.Status);
    }

    [Fact]
    public void Merge_PrzyRoznychCzasachRozpoczecia_BierzeNajwczesniejszy()
    {
        IReadOnlyList<Alert> merged = StateMerger.Merge(
            [
                [Alert("m1", "r1", AlertStatus.Acknowledged, startedOffsetSec: 100, lastSeenOffsetSec: 120)],
                [Alert("m1", "r1", AlertStatus.Active, startedOffsetSec: 10, lastSeenOffsetSec: 120)],
            ],
            EmptyAcks());

        Alert result = Assert.Single(merged);
        Assert.Equal(T0.AddSeconds(10), result.StartedAt);
        Assert.Equal(AlertStatus.Acknowledged, result.Status);
    }

    [Fact]
    public void Merge_GdyPamiecAckowZnaKlucz_WymuszaStanPotwierdzony()
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        AlertKey key = AlertKey.ForRule("m1", "r1");
        acks.Record(new AckRecord(key, "PC-7", "kasia", clock.UtcNow));

        IReadOnlyList<Alert> merged = StateMerger.Merge([[Alert("m1", "r1")]], acks);

        Alert result = Assert.Single(merged);
        Assert.Equal(AlertStatus.Acknowledged, result.Status);
        Assert.Equal("PC-7", result.AckedByHost);
        Assert.Equal("kasia", result.AckedByUser);
        Assert.Equal(clock.UtcNow, result.AcknowledgedAt);
    }

    [Fact]
    public void Merge_PomijaAlertyZamkniete()
    {
        IReadOnlyList<Alert> merged = StateMerger.Merge(
            [
                [Alert("m1", "r1", AlertStatus.Closed), Alert("m2", "r2")],
                [Alert("m3", "r3", AlertStatus.Closed)],
            ],
            EmptyAcks());

        Assert.Equal("m2", Assert.Single(merged).MachineId);
    }

    [Fact]
    public void Merge_GdyJedenObserwatorWidziAlertZamknietyADrugiAktywny_ZwracaAktywny()
    {
        IReadOnlyList<Alert> merged = StateMerger.Merge(
            [[Alert("m1", "r1", AlertStatus.Closed)], [Alert("m1", "r1")]],
            EmptyAcks());

        Assert.Equal(AlertStatus.Active, Assert.Single(merged).Status);
    }

    [Fact]
    public void Merge_UkladaAktywneIEskalowanePrzedPotwierdzonymiPotemWgCzasuRozpoczecia()
    {
        IReadOnlyList<Alert> merged = StateMerger.Merge(
            [
                [
                    Alert("m1", "r1", AlertStatus.Acknowledged, startedOffsetSec: 0),
                    Alert("m2", "r2", AlertStatus.Active, startedOffsetSec: 50),
                    Alert("m3", "r3", AlertStatus.Escalated, startedOffsetSec: 20),
                ],
            ],
            EmptyAcks());

        Assert.Equal(["m3", "m2", "m1"], merged.Select(a => a.MachineId));
    }

    [Fact]
    public void Merge_PustegoWejscia_ZwracaPustaListe()
    {
        Assert.Empty(StateMerger.Merge([], EmptyAcks()));
        Assert.Empty(StateMerger.Merge([[]], EmptyAcks()));
    }

    [Fact]
    public void Merge_ZachowujeAlertyOffline()
    {
        IReadOnlyList<Alert> merged = StateMerger.Merge(
            [[Alert("m1", "-", kind: AlertKind.Offline)]],
            EmptyAcks());

        Assert.Equal(AlertKind.Offline, Assert.Single(merged).Kind);
    }

    [Fact]
    public void SerializacjaJson_Alertu_JestOdwracalnaIUzywaCamelCase()
    {
        Alert original = Alert("aidite1", "tray-grab", AlertStatus.Acknowledged, 10, 40) with
        {
            FullText = "Failed to grab a tray",
            AckedByHost = "PC-1",
            AckedByUser = "anna",
            CaptureFile = "captures/aidite1_blad.png",
            SupersededEpisodeIds = [Guid.NewGuid(), Guid.NewGuid()],
            ReconciledAt = T0,
        };

        string json = JsonSerializer.Serialize(original, MessageCodec.JsonOptions);
        Alert? roundTripped = JsonSerializer.Deserialize<Alert>(json, MessageCodec.JsonOptions);

        Assert.Contains("\"machineName\"", json, StringComparison.Ordinal);
        Assert.Contains("\"acknowledged\"", json, StringComparison.Ordinal);
        Assert.NotNull(roundTripped);
        Assert.Equal(original.EpisodeKeys, roundTripped.EpisodeKeys);
        Assert.Equal(original.SupersededEpisodeIds, roundTripped.SupersededEpisodeIds);
        Assert.Equal(original, roundTripped with { EpisodeKeys = original.EpisodeKeys,
            SupersededEpisodeIds = original.SupersededEpisodeIds });
    }

    [Fact]
    public void Merge_AlertuOdDwochObserwatorow_ZachowujePriorytetZwycieskiejWersji()
    {
        // Dlaczego: scalanie wybiera JEDNĄ wersję alertu dla danego klucza — razem z nią musi iść jej
        // priorytet, bo to on decyduje o dalszych podmianach treści w tym epizodzie. Gdyby priorytet
        // przepadał przy scalaniu, po przejęciu roli obserwatora alarm serwonapędu (90) zszedłby do 0.
        Alert starszy = Alert("m1", "servo-alarm", lastSeenOffsetSec: 5) with { Priority = 90 };
        Alert nowszy = Alert("m1", "servo-alarm", lastSeenOffsetSec: 9) with { Priority = 90, ShortText = "Alarm serwonapędu" };

        Alert merged = Assert.Single(StateMerger.Merge([[starszy], [nowszy]], EmptyAcks()));

        Assert.Equal(90, merged.Priority);
        Assert.Equal("Alarm serwonapędu", merged.ShortText);
    }

    [Fact]
    public void Merge_AlertuBezUstawionegoPriorytetu_ZostawiaZero()
    {
        // Dlaczego: stan od starszego nadawcy (albo alert postoju/braku łączności, gdzie priorytet nie ma
        // znaczenia) ma przejść przez scalanie bez zmian — 0 jest wartością neutralną, nie „brakiem danych”.
        Alert merged = Assert.Single(StateMerger.Merge([[Alert("m1", "r1")]], EmptyAcks()));

        Assert.Equal(0, merged.Priority);
    }
}
