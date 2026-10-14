using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>
/// Alert „maszyna stoi bez pracy” w komunikacji między komputerami i w historii: rozgłoszenie stanu
/// (<see cref="MessageCodec"/>), scalanie stanów od obserwatorów (<see cref="StateMerger"/>) oraz wpis
/// w pliku <c>history.csv</c> (<see cref="HistoryWriter"/>).
/// </summary>
public sealed class MachineIdleMessageTests
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset T0 = FakeClock.DefaultStart;

    private static HmacSigner Signer() => new(HmacSigner.GenerateKey());

    private static Alert IdleAlert(string machineId = "aidite3", AlertStatus status = AlertStatus.Active) => new()
    {
        Key = AlertKey.Idle(machineId),
        MachineId = machineId,
        MachineName = "Aidite 3",
        Kind = AlertKind.MachineIdle,
        ShortText = "Stoi bez pracy (100 %) – dołóż prace",
        FullText = null,
        Category = "postoj",
        StartedAt = T0,
        LastSeenAt = T0.AddMinutes(3),
        Status = status,
        CaptureFile = "captures/aidite3_postoj.png",
        Sound = false,
    };

    private static MachineStatusSnapshot IdleSnapshot(string machineId = "aidite3") => new()
    {
        MachineId = machineId,
        Name = "Aidite 3",
        Host = "192.168.1.107",
        Port = 9100,
        Profile = "opencnc",
        State = MachineConnectionState.Connected,
        Source = "vnc",
        UpdatedAt = T0.AddMinutes(3),
        IdleWatchEnabled = true,
        LastProgressFraction = 0.998,
        ProgressFullNow = true,
        ProgressFullConfirmed = true,
        ProgressFullSince = T0,
    };

    [Fact]
    public void EncodeIDecode_StanuZAlertemPostoju_ZachowujeRodzajTrescIBrakDzwieku()
    {
        // Dlaczego: alert postoju jest rozgłaszany na wszystkie 6 komputerów — po drodze nie może zgubić
        // rodzaju (MachineIdle), kategorii „postoj” ani informacji, że NIE ma dzwonić.
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new StateMessage
        {
            Sender = "PC-A",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = [IdleAlert()],
            Machines = [IdleSnapshot()],
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        StateMessage state = Assert.IsType<StateMessage>(decoded);
        Alert alert = Assert.Single(state.Alerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertKey.Idle("aidite3"), alert.Key);
        Assert.Equal("Stoi bez pracy (100 %) – dołóż prace", alert.ShortText);
        Assert.Equal("postoj", alert.Category);
        Assert.Null(alert.FullText);
        Assert.Equal(T0, alert.StartedAt);
        Assert.Equal("captures/aidite3_postoj.png", alert.CaptureFile);
        Assert.False(alert.Sound);
    }

    [Fact]
    public void EncodeIDecode_MigawkiMaszyny_ZachowujeNowePolaObserwacjiPostoju()
    {
        // Dlaczego: okno statusu na komputerze BEZ roli obserwatora pokazuje kolumnę „Postęp” wyłącznie
        // z danych odebranych po sieci — pola postoju muszą przetrwać serializację (w tym data „100 % od”).
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new StateMessage
        {
            Sender = "PC-B",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Machines = [IdleSnapshot()],
        };

        Assert.True(MessageCodec.TryDecode(MessageCodec.Encode(msg, signer), signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        MachineStatusSnapshot snapshot = Assert.Single(Assert.IsType<StateMessage>(decoded).Machines);
        Assert.True(snapshot.IdleWatchEnabled);
        Assert.Equal(0.998, snapshot.LastProgressFraction, 6);
        Assert.True(snapshot.ProgressFullNow);
        Assert.True(snapshot.ProgressFullConfirmed);
        Assert.Equal(T0, snapshot.ProgressFullSince);
    }

    [Fact]
    public void EncodeIDecode_MigawkiMaszyny_ZachowujeOdczytanyProcentISurowyTekstOcr()
    {
        // Dlaczego: w trybie liczbowym kolumna „Postęp” pokazuje odczytany procent (albo „? (OCR nie
        // odczytał: …)” z surowym tekstem) — na komputerze BEZ roli obserwatora bierze się to wyłącznie
        // z migawki odebranej po sieci, więc oba pola muszą przetrwać serializację, także nieczytelny odczyt.
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        var msg = new StateMessage
        {
            Sender = "PC-B",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Machines =
            [
                IdleSnapshot() with { LastProgressPercent = 100, LastProgressText = "100%" },
                IdleSnapshot("aidite4") with
                {
                    LastProgressFraction = 0,
                    ProgressFullNow = false,
                    ProgressFullConfirmed = false,
                    ProgressFullSince = null,
                    LastProgressPercent = null,
                    LastProgressText = "1OO0/o",
                },
            ],
        };

        Assert.True(MessageCodec.TryDecode(MessageCodec.Encode(msg, signer), signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        IReadOnlyList<MachineStatusSnapshot> machines = Assert.IsType<StateMessage>(decoded).Machines;
        Assert.Equal(2, machines.Count);

        MachineStatusSnapshot stojaca = Assert.Single(machines, m => m.MachineId == "aidite3");
        Assert.Equal(100, stojaca.LastProgressPercent);
        Assert.Equal("100%", stojaca.LastProgressText);

        MachineStatusSnapshot nieczytelna = Assert.Single(machines, m => m.MachineId == "aidite4");
        Assert.Null(nieczytelna.LastProgressPercent);
        Assert.Equal("1OO0/o", nieczytelna.LastProgressText);
    }

    [Fact]
    public void Merge_StanuZAlertemPostoju_ZachowujeGoWWynikuScalania()
    {
        // Dlaczego: przy dwóch obserwatorach (np. chwilowa partycja sieci) ten sam postój przychodzi
        // dwa razy — ma się zdeduplikować do jednego wpisu, bez utraty rodzaju alertu.
        var acks = new AckMemory(new FakeClock(), TimeSpan.FromHours(2));

        IReadOnlyList<Alert> merged = StateMerger.Merge([[IdleAlert()], [IdleAlert()]], acks);

        Alert alert = Assert.Single(merged);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertStatus.Active, alert.Status);
        Assert.False(alert.Sound);
    }

    [Fact]
    public void Merge_GdyPamiecAckZnaKluczPostoju_OznaczaAlertJakoPotwierdzony()
    {
        // Dlaczego: „✕ Skasuj” wciśnięte na jednym komputerze ma wyciszyć postój także na komputerze,
        // który dopiero dostał stan od obserwatora (i jeszcze nie zna tego potwierdzenia z alertu).
        var acks = new AckMemory(new FakeClock(), TimeSpan.FromHours(2));
        acks.Record(new AckRecord(AlertKey.Idle("aidite3"), "PC-4", "anna", T0.AddMinutes(1)));

        Alert alert = Assert.Single(StateMerger.Merge([[IdleAlert()]], acks));

        Assert.Equal(AlertStatus.Acknowledged, alert.Status);
        Assert.Equal("PC-4", alert.AckedByHost);
        Assert.Equal("anna", alert.AckedByUser);
        Assert.Equal(T0.AddMinutes(1), alert.AcknowledgedAt);
    }

    [Fact]
    public void EncodeIDecode_StanuZPostojemNaZerze_ZachowujeKategorieIGodzineZera()
    {
        var clock = new FakeClock();
        HmacSigner signer = Signer();
        Alert alert = IdleAlert() with
        {
            ShortText = AlertStateMachine.MachineIdleAtZeroShortText,
            Category = AlertStateMachine.IdleAtZeroCategory,
        };
        MachineStatusSnapshot snapshot = IdleSnapshot() with
        {
            LastProgressFraction = 0,
            LastProgressPercent = 0,
            ProgressFullNow = false,
            ProgressFullConfirmed = false,
            ProgressFullSince = null,
            ProgressZeroSince = T0,
        };
        var msg = new StateMessage
        {
            Sender = "PC-A",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = [alert],
            Machines = [snapshot],
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        StateMessage back = Assert.IsType<StateMessage>(decoded);
        Alert odebrany = Assert.Single(back.Alerts);
        Assert.Equal(AlertKind.MachineIdle, odebrany.Kind);
        Assert.Equal("postoj-0", odebrany.Category);
        Assert.Equal(AlertStateMachine.MachineIdleAtZeroShortText, odebrany.ShortText);
        Assert.False(odebrany.Sound);

        MachineStatusSnapshot odebranaMigawka = Assert.Single(back.Machines);
        Assert.Equal(T0, odebranaMigawka.ProgressZeroSince);
        Assert.Null(odebranaMigawka.ProgressFullSince);
    }

    [Fact]
    public void Append_NowegoAlertuPostojuNaZerze_ZapisujeWierszHistoriiZKategoriaPostoj0()
    {
        // Dlaczego: obie odmiany postoju trafiają do history.csv tą samą ścieżką, ale muszą dać się od
        // siebie odróżnić w arkuszu — bez nowej kolumny, samą kategorią.
        using var dir = new TempDir("historia-postoj-0");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        writer.Append(new AlertEvent(AlertEventKind.Raised, IdleAlert() with
        {
            ShortText = AlertStateMachine.MachineIdleAtZeroShortText,
            Category = AlertStateMachine.IdleAtZeroCategory,
        }));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "2026-03-17 08:00:00;Aidite 3;nowy;Stoi na 0 % – wciśnij Start;postoj-0;;;",
            lines[1]);
    }

    [Fact]
    public void Append_NowegoAlertuPostoju_ZapisujeWierszHistoriiZKategoriaPostoj()
    {
        // Dlaczego: historia (history.csv) służy do rozliczenia „ile razy maszyna stała bez pracy” —
        // wiersz musi mieć nazwę maszyny, opis i kategorię „postoj”, a pusty pełny tekst nie może go zepsuć.
        using var dir = new TempDir("historia-postoj");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        writer.Append(new AlertEvent(AlertEventKind.Raised, IdleAlert()));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "2026-03-17 08:00:00;Aidite 3;nowy;Stoi bez pracy (100 %) – dołóż prace;postoj;;;",
            lines[1]);
    }
}
