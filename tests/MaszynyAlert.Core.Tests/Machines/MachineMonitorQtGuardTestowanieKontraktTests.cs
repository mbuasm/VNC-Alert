using System.Collections;
using System.Reflection;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;
using static MaszynyAlert.Core.Tests.Machines.QtHmiSupervisor;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorQtGuardTestowanieKontraktTests
{
    [Fact]
    public async Task TB03_RownoleglePrzebudowyWTrakcieWstrzymania_ZeroProbDoHmi_PoUplywieDokladnieJednaProba()
    {
        // Wyścigi na wspólnym stanie: AppOrchestrator.ReloadSecretsAndReconfigureAsync (StopAsync → StartAsync, poza bramką cyklu)
        // w tym samym czasie co tik (StartAsync), potem kolejne ApplyEffectiveAsync (ReconfigureAsync → StartAsync). Gdyby dwa monitory
        // tej samej maszyny żyły naraz na jednym stanie, po upływie wstrzymania HMI dostałoby więcej niż jedną próbę.
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        AppConfig cfg = QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true);
        Fixture f = Build(cfg);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");

        // 4 z 5 min wstrzymania → alert „Brak łączności” (offlineAlertAfterMs 180 s); przez całą burzę przebudów ma trwać bez migania.
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(1500);
        f.Alerts.Tick();
        AlertEvent raised = Assert.Single(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline);
        int eventsBefore = f.Events.Count;

        Task reload = Task.Run(async () =>
        {
            for (int i = 0; i < 15; i++)
            {
                await supervisor.StopAsync();
                await supervisor.StartAsync(CancellationToken.None);
            }
        });
        Task tick = Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++)
            {
                await supervisor.StartAsync(CancellationToken.None);
                await Task.Delay(3);
            }
        });
        await Task.WhenAll(reload, tick).WaitAsync(Patience);
        for (int i = 0; i < 5; i++)
        {
            await ApplyLikePanelAsync(f, cfg);
        }

        await supervisor.StartAsync(CancellationToken.None);
        await Task.Delay(1500); // ≥ 1 cykl podtrzymania ostatniego monitora
        f.Alerts.Tick();

        Assert.True(supervisor.IsRunning);
        Assert.Equal(1, hmi2.ConnectionCount); // ani jednej próby do HMI w czasie wstrzymania
        MachineStatusSnapshot card = supervisor.Snapshots.Single(s => s.MachineId == "amm520-2");
        Assert.Equal(MachineConnectionState.Error, card.State);
        Assert.Contains("Kolejna próba za 5 min", card.LastError);
        Assert.DoesNotContain(f.Events.Skip(eventsBefore), e => e.Alert.MachineId == "amm520-2"); // ani Closed, ani nowy Raised
        Alert open = Assert.Single(f.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Offline && a.MachineId == "amm520-2");
        Assert.Equal(raised.Alert.StartedAt, open.StartedAt);

        // Wstrzymanie mija: dokładnie JEDNA próba (jeden monitor tej maszyny); HMI znowu pada → krok 2 (15 min), dalej cisza.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 2, "drugie padnięcie po wstrzymaniu");
        await Task.Delay(2500);
        Assert.Equal(2, hmi2.ConnectionCount);
        Assert.Equal(2, f.Loggers.Containing(EarlyClose).Count);
        Assert.Contains("następna próba za 15 min", f.Loggers.Containing(EarlyClose)[^1].Message);
        Assert.DoesNotContain(f.Loggers.Entries, e => e.Level >= LogLevel.Error);

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task TB04_RolaObserwatoraWracaPoTrzechMinutach_WstrzymanieDoPierwotnegoTerminu_PotemJednaProba()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(i => i == 0 ? QtHmiBehavior.CrashOnFirstRequest : QtHmiBehavior.Serve);
        Fixture f = Build(QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true));
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing("nadal wstrzymuje").Count == 1, "wstrzymanie po powrocie roli");
        Assert.Contains("następna próba za 2 min", f.Loggers.Containing("nadal wstrzymuje")[0].Message);
        await Task.Delay(1500);
        Assert.Equal(1, hmi2.ConnectionCount);

        f.Clock.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => supervisor.Snapshots.Single(s => s.MachineId == "amm520-2").State == MachineConnectionState.Connected,
            "połączenie po pierwotnym terminie");
        Assert.Equal(2, hmi2.ConnectionCount);

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task TB05_UsuniecieIPonowneDodanieMaszynyWTrakcieWstrzymania_WstrzymanieTrwa_SlownikStanowNieRosnie()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        AppConfig cfg = QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true);
        AppConfig without = cfg with { Machines = [cfg.Machines[0]] };
        Fixture f = Build(cfg);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");

        for (int i = 0; i < 10; i++)
        {
            await ApplyLikePanelAsync(f, without, "amm520-2");
            Assert.Null(supervisor.Get("amm520-2"));
            Assert.Equal(2, QtGuardCount(supervisor)); // wpis usuniętej maszyny zostaje
            await ApplyLikePanelAsync(f, cfg);
        }

        await Task.Delay(1500);
        Assert.Equal(1, hmi2.ConnectionCount);
        Assert.Equal(2, QtGuardCount(supervisor));
        MachineStatusSnapshot card = supervisor.Snapshots.Single(s => s.MachineId == "amm520-2");
        Assert.Equal(MachineConnectionState.Error, card.State);
        Assert.Contains("Kolejna próba za 5 min", card.LastError);

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task TB06_ZmianaNazwyIProfiluMaszynyWTrakcieWstrzymania_TenSamAdres_WstrzymanieIOpisSerweraQtZostaja()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        AppConfig cfg = QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true);
        AppConfig renamed = cfg with
        {
            Machines = [cfg.Machines[0], cfg.Machines[1] with { Name = "520 (2) po zmianie", Profile = "opencnc" }],
        };
        Fixture f = Build(cfg);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");

        await ApplyLikePanelAsync(f, renamed);
        await WaitUntilAsync(() => f.Loggers.Containing("nadal wstrzymuje").Count == 1, "wstrzymanie po zmianie nazwy i profilu");
        await Task.Delay(1500);

        Assert.Equal(1, hmi2.ConnectionCount);
        MachineStatusSnapshot card = supervisor.Snapshots.Single(s => s.MachineId == "amm520-2");
        Assert.Equal("520 (2) po zmianie", card.Name);
        Assert.Equal("opencnc", card.Profile);
        Assert.Equal(MachineConnectionState.Error, card.State);
        Assert.Equal(QtEmbeddedVncServer.DesktopName, card.Info?.DesktopName);

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task TB07_D3_WylaczenieIPonowneWlaczenieWTrakcieWstrzymania_AlertBrakLacznosciToNowyEpizod()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        AppConfig on = QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true);
        AppConfig off = QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: false);
        Fixture f = Build(on);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(1500);
        f.Alerts.Tick();
        AlertEvent first = Assert.Single(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline);

        await ApplyLikePanelAsync(f, off, "amm520-2");
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Offline);
        int eventsAtDisable = f.Events.Count;
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        DateTimeOffset reenabledAt = f.Clock.UtcNow;
        await ApplyLikePanelAsync(f, on);
        await WaitUntilAsync(() => f.Loggers.Containing("nadal wstrzymuje").Count == 1, "wstrzymanie po ponownym włączeniu");
        Assert.Equal(1, hmi2.ConnectionCount);

        f.Clock.Advance(TimeSpan.FromMinutes(3.5)); // wstrzymanie minęło; > offlineAlertAfterMs od ponownego włączenia
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 2, "próba po terminie wstrzymania");
        f.Alerts.Tick();

        AlertEvent again = Assert.Single(f.Events.Skip(eventsAtDisable),
            e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline && e.Alert.MachineId == "amm520-2");
        Assert.True(again.Alert.StartedAt >= reenabledAt,
            $"Początek alertu {again.Alert.StartedAt:O} sprzed ponownego włączenia {reenabledAt:O} (pierwszy epizod: {first.Alert.StartedAt:O}).");
        Assert.Equal(2, hmi2.ConnectionCount);
        Assert.DoesNotContain(f.Loggers.Entries, e => e.Level >= LogLevel.Error);

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task TB08_Charakterystyka_ZmianyRoliZPrzejeciemSesji_KrokPrzejecRosnie_WracajacyObserwatorCzekaDoSwojegoTerminu()
    {
        var rig = new MachineMonitorQtGuardTestowanieUwagiTests.Rig();
        for (int flap = 0; flap < 3; flap++)
        {
            int connectsAtStart = rig.Connects.Count;
            int failuresAtStart = rig.Failures.Count;
            await rig.RunMonitorAsync(_ =>
            {
                if (rig.Connects.Count > connectsAtStart && rig.Failures.Count == failuresAtStart && rig.SinceLastConnectMs >= 120_000
                    && rig.Source.CaptureFailure is null)
                {
                    rig.ArmKick();
                }

                return rig.Failures.Count > failuresAtStart; // cykl zerwania (+30 s podtrzymania) → utrata roli, monitor zatrzymany
            });
        }

        int connectsBeforeLastReturn = rig.Connects.Count;
        await rig.RunMonitorAsync(_ => rig.Connects.Count > connectsBeforeLastReturn); // rola wraca po raz trzeci

        Assert.Equal(new long[] { 60_000, 300_000, 900_000 }, rig.WaitsAfterFailures());
        Assert.Equal(3, rig.Loggers.Containing("nadal wstrzymuje próby połączenia po przebudowie monitora").Count);
    }

    private static int QtGuardCount(MonitorSupervisor supervisor) =>
        ((ICollection)typeof(MonitorSupervisor)
            .GetField("_qtGuards", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(supervisor)!).Count;
}
