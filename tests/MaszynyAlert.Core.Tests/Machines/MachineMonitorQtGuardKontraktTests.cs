using System.Diagnostics;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;
using static MaszynyAlert.Core.Tests.Machines.QtHmiSupervisor;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorQtGuardKontraktTests
{
    private const string Qt = QtEmbeddedVncServer.DesktopName;

    [Theory]
    [InlineData(999, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public async Task SerwerQt_ZamknieciePrzedKlatkaNaGranicyOkna1s_KlasyfikacjaDokladnieWgGranicy(int closeAfterMs, bool guard)
    {
        var rig = new QtGuardTestRig(maxCycles: 15, Qt);
        rig.OnConnectNumber = n =>
        {
            rig.Source.ConnectFailure = n == 1 ? null : rig.Fail(() => new RfbException(QtGuardTestRig.Refused));
            rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), closeAfterMs) : null;
        };

        await rig.RunAsync();

        Assert.True(rig.Connects.Count >= 2, $"Połączeń: {rig.Connects.Count}.");
        if (guard)
        {
            Assert.Equal(300_000, rig.WaitsAfterFailures()[0]);
            Assert.Single(rig.Loggers.Containing("zamknął połączenie przed pierwszą klatką"));
        }
        else
        {
            Assert.Equal(2_000, rig.Connects[1] - rig.Connects[0]);
            Assert.Empty(rig.Loggers.Containing("serwer VNC Qt"));
        }
    }

    [Fact]
    public async Task SerwerQt_ZamknieciePrzedServerInitPrzyAktywnymBezpieczniku_NazwaPamietana_BezpiecznikTrwa_5_15_60()
    {
        var rig = new QtGuardTestRig(maxCycles: 200, Qt);
        rig.OnConnectNumber = n =>
        {
            rig.Source.ConnectFailure = n == 1 ? null : rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 80);
            rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216) : null;
        };

        await rig.RunAsync();

        Assert.Equal(new long[] { 300_000, 900_000, 3_600_000 }, rig.WaitsAfterFailures().Take(3));
    }

    [Fact]
    public async Task SerwerZamykajacyPrzedServerInit_SwiezyMonitorBezHandshake_ZwyklyBackoffJakPrzed156()
    {
        var rig = new QtGuardTestRig(maxCycles: 6, Qt);
        rig.Source.ConnectFailure = () => new RfbException(QtGuardTestRig.ClosedByServer);

        await rig.RunAsync();

        Assert.Equal(new[] { 2, 5, 15, 30, 30 }.Select(s => TimeSpan.FromSeconds(s)), rig.Delays.Take(5));
        Assert.Empty(rig.Loggers.Containing("serwer VNC Qt"));
    }

    [Fact]
    public async Task SerwerQt_PortWracaIHmiZnowuPadaPrzedKlatka_UdanyHandshakeNieZerujeBezpiecznika_5_15_60_60()
    {
        var rig = new QtGuardTestRig(maxCycles: 320, Qt);
        rig.OnConnectNumber = n =>
        {
            rig.Source.ConnectFailure = n is 2 or >= 4 ? rig.Fail(() => new RfbException(QtGuardTestRig.Refused)) : null;
            rig.Source.CaptureFailure = n switch
            {
                1 => rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216),
                3 => rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 75),
                _ => null,
            };
        };

        await rig.RunAsync();

        Assert.True(rig.Connects.Count >= 5, $"Połączeń: {rig.Connects.Count}.");
        long[] waits = rig.WaitsAfterFailures().Take(4).ToArray();
        Assert.Equal(new long[] { 300_000, 900_000, 3_600_000, 3_600_000 }, waits);
        Assert.Equal(waits, rig.AnnouncedWaitsMs().Take(4));
        Assert.Equal(2, rig.Loggers.Containing("zamknął połączenie przed pierwszą klatką").Count);
        Assert.Empty(rig.Loggers.Containing("bezpiecznik ponowień zdjęty"));
        Assert.Single(rig.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline);
        Assert.DoesNotContain(rig.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Offline);
        Assert.All(rig.Delays, d => Assert.True(d <= TimeSpan.FromSeconds(30), $"Odstęp cyklu {d} > 30 s."));
    }

    [Theory]
    [InlineData(600_000, 60_000)]
    [InlineData(599_999, 300_000)]
    public async Task SerwerQt_DrugiePrzejeciePoSesjiNaGranicy10Min_OdstepWgGranicyResetu(long sessionMs, long expectedWaitMs)
    {
        var rig = new QtGuardTestRig(maxCycles: 700, Qt);
        rig.AfterCycle = _ =>
        {
            if (rig.Connects.Count == 1 && rig.CyclesSinceConnect == 3 && rig.Failures.Count == 0)
            {
                rig.ArmKick();
            }
            else if (rig.Connects.Count == 2 && rig.Failures.Count == 1 && rig.Source.CaptureFailure is null
                && rig.Clock.MonotonicMs >= rig.Connects[1] + sessionMs - 3000)
            {
                rig.ArmKick(rig.Connects[1] + sessionMs);
            }
        };

        await rig.RunAsync();

        Assert.Equal(2, rig.Failures.Count);
        Assert.Equal(sessionMs, rig.Failures[1] - rig.Connects[1]);
        Assert.Equal(new long[] { 60_000, expectedWaitMs }, rig.WaitsAfterFailures().Take(2));
        Assert.Equal(new long[] { 60_000, expectedWaitMs }, rig.AnnouncedWaitsMs().Take(2));
    }

    [Fact]
    public async Task SerwerQt_KolejnePrzejecia_AlertBrakLacznosciJedenNaKazdeWstrzymanieDluzszeNiz3Min_ZamknietyPoPowrocie()
    {
        // Przejęcia co sesję 3 s: wstrzymania 1, 5, 15, 15 min. offlineAlertAfterMs = 180 s → alert tylko przy 5 i 15 min, jeden na
        // wstrzymanie (bez migania), zamknięty przy powrocie (OnMachineOnline); „następna próba za …” = rzeczywisty odstęp.
        var rig = new QtGuardTestRig(maxCycles: 100, Qt);
        rig.AfterCycle = _ =>
        {
            if (rig.CyclesSinceConnect == 3 && rig.Source.CaptureFailure is null && rig.Failures.Count < rig.Connects.Count) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.True(rig.Failures.Count >= 4, $"Przejęć: {rig.Failures.Count}.");
        long[] waits = rig.WaitsAfterFailures().Take(4).ToArray();
        Assert.Equal(new long[] { 60_000, 300_000, 900_000, 900_000 }, waits);
        Assert.Equal(waits, rig.AnnouncedWaitsMs().Take(4));
        AlertEventKind[] offline = rig.Events.Where(e => e.Alert.Kind == AlertKind.Offline).Select(e => e.Kind).ToArray();
        Assert.Equal(
            new[] { AlertEventKind.Raised, AlertEventKind.Closed, AlertEventKind.Raised, AlertEventKind.Closed, AlertEventKind.Raised, AlertEventKind.Closed },
            offline.Take(6));
        Assert.All(rig.Delays, d => Assert.True(d <= TimeSpan.FromSeconds(30), $"Odstęp cyklu {d} > 30 s."));
    }

    [Fact]
    public async Task D3_WylaczenieMaszynyWTrakcieWstrzymania_KonczyBezWyjatku_ZamykaBrakLacznosci_BezDucha_PonowneWlaczenieToNowyEpizod()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        var on = QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true);
        Fixture f = Build(on);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");

        // 4 z 5 minut wstrzymania (zegar ręczny) → alert „Brak łączności” (offlineAlertAfterMs = 180 s); wstrzymanie trwa.
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(1500); // co najmniej jeden cykl podtrzymania po przesunięciu zegara
        f.Alerts.Tick();
        Assert.Single(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline && e.Alert.MachineId == "amm520-2");
        Assert.Equal(1, hmi2.ConnectionCount);

        var sw = Stopwatch.StartNew();
        await ApplyLikePanelAsync(f, QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: false), "amm520-2");
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Przełączenie trwało {sw.Elapsed.TotalSeconds:0.0} s.");
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Offline && e.Alert.MachineId == "amm520-2");
        Assert.Equal(MachineConnectionState.Disabled, supervisor.Snapshots.Single(s => s.MachineId == "amm520-2").State);
        Assert.DoesNotContain(f.Loggers.Entries, e => e.Level >= LogLevel.Error || e.Message.Contains("Nieoczekiwany błąd", StringComparison.Ordinal));

        int eventsAtDisable = f.Events.Count;
        await Task.Delay(1500);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        f.Alerts.Tick();
        Assert.DoesNotContain(f.Events.Skip(eventsAtDisable), e => e.Alert.MachineId == "amm520-2");
        Assert.Equal(1, hmi2.ConnectionCount);

        // Ponowne włączenie = nowy epizod: alert offline z początkiem po ponownym włączeniu (nie od pierwszego padnięcia).
        DateTimeOffset reenabledAt = f.Clock.UtcNow;
        await ApplyLikePanelAsync(f, on);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 2, "drugie padnięcie po ponownym włączeniu");
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(1500);
        f.Alerts.Tick();
        AlertEvent again = Assert.Single(f.Events.Skip(eventsAtDisable),
            e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline && e.Alert.MachineId == "amm520-2");
        Assert.True(again.Alert.StartedAt >= reenabledAt, $"Początek alertu {again.Alert.StartedAt:O} sprzed ponownego włączenia {reenabledAt:O}.");

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }
}
