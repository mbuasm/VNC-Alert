using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

/// <summary>
/// Alert „włącz okno podglądu stanu maszyny” (<see cref="AlertKind.WrongScreen"/>) w komunikacie STATE i w
/// historii: nowa wartość enumu i nowe pola migawki muszą przejść przez kodek bez strat
/// oraz trafić do <c>history.csv</c> z kategorią „ekran”.
/// </summary>
public sealed class WrongScreenMessageTests
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset T0 = FakeClock.DefaultStart;

    private static Alert WrongScreenAlert(AlertStatus status = AlertStatus.Active) => new()
    {
        Key = AlertKey.WrongScreen("aidite3"),
        MachineId = "aidite3",
        MachineName = "Aidite 3",
        Kind = AlertKind.WrongScreen,
        ShortText = AlertStateMachine.WrongScreenShortText,
        FullText = null,
        Category = "ekran",
        StartedAt = T0,
        LastSeenAt = T0.AddMinutes(3),
        Status = status,
        CaptureFile = "captures/aidite3_ekran.png",
        Sound = false,
    };

    private static MachineStatusSnapshot Snapshot() => new()
    {
        MachineId = "aidite3",
        Name = "Aidite 3",
        Host = "192.168.1.107",
        Port = 9100,
        Profile = "opencnc",
        State = MachineConnectionState.Connected,
        Source = "vnc",
        UpdatedAt = T0.AddMinutes(3),
        MainScreenWatchEnabled = true,
        MainScreenVisible = false,
        WrongScreenSince = T0,
        LastMainScreenCheckAt = T0.AddMinutes(3),
    };

    [Fact]
    public void EncodeIDecode_StanuZAlertemEkranu_ZachowujeRodzajKategorieIPolaMigawki()
    {
        var clock = new FakeClock();
        var signer = new HmacSigner(HmacSigner.GenerateKey());
        var msg = new StateMessage
        {
            Sender = "PC-A",
            Version = "0.1.0",
            SentAt = clock.UtcNow,
            Alerts = [WrongScreenAlert()],
            Machines = [Snapshot()],
        };

        byte[] datagram = MessageCodec.Encode(msg, signer);
        Assert.True(MessageCodec.TryDecode(datagram, signer, clock, Skew, out PeerMessage? decoded, out string? reason), reason);

        StateMessage state = Assert.IsType<StateMessage>(decoded);
        Alert alert = Assert.Single(state.Alerts);
        Assert.Equal(AlertKind.WrongScreen, alert.Kind);
        Assert.Equal(AlertKey.WrongScreen("aidite3"), alert.Key);
        Assert.Equal("ekran", alert.Category);
        Assert.Equal(AlertStateMachine.WrongScreenShortText, alert.ShortText);
        Assert.False(alert.Sound);

        MachineStatusSnapshot snapshot = Assert.Single(state.Machines);
        Assert.True(snapshot.MainScreenWatchEnabled);
        Assert.False(snapshot.MainScreenVisible);
        Assert.Equal(T0, snapshot.WrongScreenSince);
        Assert.Equal(T0.AddMinutes(3), snapshot.LastMainScreenCheckAt);
    }

    [Fact]
    public void Merge_AlertuEkranu_PrzechodziPrzezScalanieJakKazdyInny()
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));

        IReadOnlyList<Alert> merged = StateMerger.Merge([[WrongScreenAlert()], [WrongScreenAlert(AlertStatus.Acknowledged) with { AcknowledgedAt = T0.AddMinutes(1) }]], acks);

        Alert result = Assert.Single(merged);
        Assert.Equal(AlertKind.WrongScreen, result.Kind);
        Assert.Equal(AlertStatus.Acknowledged, result.Status);
    }

    [Fact]
    public void HistoryWriter_ZdarzenieAlertuEkranu_ZapisujeKategorieEkran()
    {
        using var dir = new TempDir("historia-ekran");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        writer.Append(new AlertEvent(AlertEventKind.Raised, WrongScreenAlert()));

        string[] lines = File.ReadAllLines(path);
        Assert.Contains(lines, l => l.Contains("Aidite 3", StringComparison.Ordinal) && l.Contains(";ekran;", StringComparison.Ordinal));
    }
}
