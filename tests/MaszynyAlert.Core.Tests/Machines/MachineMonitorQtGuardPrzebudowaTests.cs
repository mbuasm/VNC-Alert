using System.Diagnostics;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;
using static MaszynyAlert.Core.Tests.Machines.QtHmiSupervisor;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorQtGuardPrzebudowaTests
{
    // ---------------------------------------------------------------------------------------------------------------------
    // Część 1: kolejne monitory jednej maszyny ze wspólnym stanem (jak MonitorSupervisor), ręczny zegar
    // ---------------------------------------------------------------------------------------------------------------------

    private sealed class RebuildRig
    {
        public RebuildRig()
        {
            Alerts = new AlertStateMachine(Clock, Timing, new AckMemory(Clock, TimeSpan.FromHours(Timing.AckMemoryHours)));
            Alerts.Changed += Events.Add;
            Source = new FakeScreenSource("amm520-1", QtGuardTestRig.NormalFrame()) { DesktopName = QtEmbeddedVncServer.DesktopName };
            Source.OnConnect = () =>
            {
                Connects.Add(Clock.MonotonicMs);
                OnConnectNumber?.Invoke(Connects.Count);
            };
        }

        public FakeClock Clock { get; } = new();

        public TimingConfig Timing { get; } = new() { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };

        public AlertStateMachine Alerts { get; }

        public List<AlertEvent> Events { get; } = [];

        public RecordingLoggerFactory Loggers { get; } = new();

        public FakeScreenSource Source { get; }

        public QtGuardState State { get; } = new("192.168.1.101", 5900);

        public List<long> Connects { get; } = [];

        public List<long> Failures { get; } = [];

        public Action<int>? OnConnectNumber { get; set; }

        public MachineMonitor? Monitor { get; private set; }

        /// <summary>LastError migawki po każdym cyklu (wszystkie monitory po kolei).</summary>
        public List<string?> LastErrors { get; } = [];

        /// <summary>Nowy monitor tej maszyny ze wspólnym stanem; działa, aż <paramref name="stop"/>(numer cyklu tego monitora) zwróci true.</summary>
        public async Task RunMonitorAsync(Func<int, bool> stop)
        {
            using var cts = new CancellationTokenSource();
            int cycles = 0;
            MachineMonitor monitor = null!;
            monitor = new MachineMonitor(
                QtGuardTestRig.Machine(), QtGuardTestRig.Profile, () => Source, null, new FakeOcrEngine(),
                new MessageMapper(TestPaths.ExampleRules), Alerts, Clock, Timing, new NullCaptureStore(), Loggers.CreateLogger<MachineMonitor>(),
                (interval, _) =>
                {
                    cycles++;
                    LastErrors.Add(monitor.Snapshot.LastError);
                    Clock.Advance(interval);
                    Alerts.Tick();
                    if (stop(cycles)) cts.Cancel();
                    return Task.CompletedTask;
                },
                qtGuard: State);
            Monitor = monitor;
            await monitor.RunAsync(cts.Token);
        }

        public Func<Exception> Fail(Func<Exception> make, int advanceMs = 0) => () =>
        {
            if (advanceMs > 0) Clock.Advance(TimeSpan.FromMilliseconds(advanceMs));
            Failures.Add(Clock.MonotonicMs);
            return make();
        };

        /// <summary>Jednorazowe zerwanie działającej sesji przez serwer (przejęcie przez innego klienta).</summary>
        public void ArmKick() => Source.CaptureFailure = () =>
        {
            Source.CaptureFailure = null;
            Failures.Add(Clock.MonotonicMs);
            return new RfbException(QtGuardTestRig.ClosedByServer);
        };

        public long SinceLastConnectMs => Clock.MonotonicMs - Connects[^1];

        /// <summary>Odstęp od każdej awarii do następnej próby połączenia (ms; −1 — brak kolejnej próby).</summary>
        public long[] WaitsAfterFailures() => Failures
            .Select(f => Connects.FirstOrDefault(c => c > f, -1) is var next && next < 0 ? -1 : next - f)
            .ToArray();

        public IReadOnlyList<LogEntry> VncWarnings() => Loggers.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(MachineMonitor), StringComparison.Ordinal)
                && !e.Message.Contains("idle.progressRoi", StringComparison.Ordinal))
            .ToArray();
    }

    [Fact]
    public async Task Naprawa01_PrzebudowaMonitoraWTrakcieWstrzymania_BezpiecznikTrwaDoTegoSamegoTerminu_JedenAlertIJedenWrn()
    {
        var rig = new RebuildRig();
        rig.OnConnectNumber = n => rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216) : null;

        await rig.RunMonitorAsync(c => c >= 4); // cykl awarii + 3 cykle podtrzymania po 30 s = 2 min po padnięciu
        string? holdError = rig.Monitor!.Snapshot.LastError;
        Assert.Contains("Kolejna próba za 5 min", holdError);
        Assert.Single(rig.Connects);

        int beforeRebuild = rig.LastErrors.Count;
        await rig.RunMonitorAsync(c => c >= 20);

        Assert.Equal(2, rig.Connects.Count);
        Assert.Equal(300_000, rig.Connects[1] - rig.Failures[0]); // dokładnie termin z monitora 1
        Assert.All(rig.LastErrors.Skip(beforeRebuild).Take(5), e => Assert.Equal(holdError, e)); // monitor 2 w czasie wstrzymania
        Assert.Single(rig.Loggers.Containing("nadal wstrzymuje próby połączenia po przebudowie monitora"));
        Assert.Contains("następna próba za 3 min", rig.Loggers.Containing("nadal wstrzymuje")[0].Message);
        Assert.Single(rig.VncWarnings()); // tylko WRN pierwszego padnięcia
        AlertEventKind[] offline = rig.Events.Where(e => e.Alert.Kind == AlertKind.Offline).Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { AlertEventKind.Raised, AlertEventKind.Closed }, offline);
        Assert.Equal(MachineConnectionState.Connected, rig.Monitor.Snapshot.State);
        Assert.Null(rig.State.HoldError);
    }

    [Fact]
    public async Task Naprawa01_PrzebudowaPodczasWstrzymania_KrokBezpiecznikaPrzezywa_KolejnaOdmowa15Min()
    {
        // Bez wspólnego stanu nowy monitor zaczynałby od kroku 0: odmowa po wstrzymaniu = zwykły backoff 2 s (pętla prób do HMI,
        // które może się właśnie restartować). Ze stanem: krok 2 → 15 min.
        var rig = new RebuildRig();
        rig.OnConnectNumber = n =>
        {
            rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216) : null;
            rig.Source.ConnectFailure = n >= 2 ? rig.Fail(() => new RfbException(QtGuardTestRig.Refused)) : null;
        };

        await rig.RunMonitorAsync(c => c >= 3);
        await rig.RunMonitorAsync(c => c >= 40);

        Assert.Equal(new long[] { 300_000, 900_000 }, rig.WaitsAfterFailures().Take(2));
        Assert.Contains(rig.Loggers.Containing("kolejna nieudana próba"), w => w.Message.Contains("następna próba za 15 min"));
    }

    [Fact]
    public async Task Naprawa01_PrzebudowaPodczasWstrzymaniaPoPrzejeciu_KrokPrzejecPrzezywa_Kolejne5Min()
    {
        // Monitor 1: sesja z klatkami, przejęcie → 1 min; przebudowa w trakcie tej minuty. Monitor 2: po minucie sesja z klatkami,
        // kolejne przejęcie → 5 min (krok przejęć nie wrócił do 1 min przez samą przebudowę).
        var rig = new RebuildRig();

        await rig.RunMonitorAsync(c =>
        {
            if (c == 3) rig.ArmKick();
            return c >= 4; // przejęcie w cyklu 4 → przebudowa w połowie minuty wstrzymania
        });
        Assert.Single(rig.Failures);
        await rig.RunMonitorAsync(c =>
        {
            if (rig.Connects.Count == 2 && rig.Failures.Count == 1 && rig.SinceLastConnectMs >= 3000 && rig.Source.CaptureFailure is null) rig.ArmKick();
            return rig.Failures.Count == 2 && rig.Connects.Count == 3 && rig.SinceLastConnectMs >= 3000;
        });

        Assert.Equal(new long[] { 60_000, 300_000 }, rig.WaitsAfterFailures());
        Assert.Contains("następna próba za 5 min", rig.VncWarnings()[^1].Message);
    }

    [Theory]
    [InlineData(6, 5, 60_000)] // 6 + 5 min stabilnej obserwacji ≥ 10 min → krok przejęć od nowa (1 min)
    [InlineData(12, 1, 60_000)] // sesja ≥ 10 min przed przebudową
    [InlineData(2, 5, 900_000)] // 2 + 5 min < 10 min → krok 3 (15 min)
    public async Task Naprawa01_StabilnaSesjaPrzerwanaPrzebudowa_DlugoscLiczonaOdJejPoczatku_ResetKrokuPrzejec(
        int minutesBefore, int minutesAfter, long expectedWaitMs)
    {
        // Dwa szybkie przejęcia (1 min, 5 min) → następne byłoby 15 min. Potem sesja stabilna przez minutesBefore, przebudowa
        // (zatrzymanie przez nadzorcę, nie przez serwer), nowy monitor łączy się od razu i po minutesAfter przychodzi przejęcie.
        var rig = new RebuildRig();
        await rig.RunMonitorAsync(_ =>
        {
            if (rig.Failures.Count < 2 && rig.Connects.Count == rig.Failures.Count + 1 && rig.SinceLastConnectMs >= 3000
                && rig.Source.CaptureFailure is null)
            {
                rig.ArmKick();
            }

            return rig.Failures.Count == 2 && rig.Connects.Count == 3 && rig.SinceLastConnectMs >= minutesBefore * 60_000L;
        });
        Assert.Equal(new long[] { 60_000, 300_000 }, rig.WaitsAfterFailures());

        await rig.RunMonitorAsync(_ =>
        {
            if (rig.Failures.Count == 2 && rig.Connects.Count == 4 && rig.SinceLastConnectMs >= minutesAfter * 60_000L
                && rig.Source.CaptureFailure is null)
            {
                rig.ArmKick();
            }

            return rig.Failures.Count == 3 && rig.Connects.Count == 5;
        });

        Assert.Equal(expectedWaitMs, rig.WaitsAfterFailures()[2]);
        Assert.Contains($"następna próba za {expectedWaitMs / 60_000} min", rig.VncWarnings()[^1].Message);
    }

    [Fact]
    public async Task Naprawa01_AwariaPoPrzebudowie_PrzerywaCiagloscSesji_KolejnePrzejecieBezResetuKroku()
    {
        // Ciągłość dotyczy tylko sesji przerwanej samą przebudową. Dwa szybkie przejęcia (krok 2), sesja stabilna 12 min, przebudowa;
        // nowy monitor: pierwsza próba — odmowa portu (awaria przerywa ciągłość), druga — sesja 1 min i przejęcie → 15 min, nie 1 min.
        var rig = new RebuildRig();
        await rig.RunMonitorAsync(_ =>
        {
            if (rig.Failures.Count < 2 && rig.Connects.Count == rig.Failures.Count + 1 && rig.SinceLastConnectMs >= 3000
                && rig.Source.CaptureFailure is null)
            {
                rig.ArmKick();
            }

            return rig.Failures.Count == 2 && rig.Connects.Count == 3 && rig.SinceLastConnectMs >= 12 * 60_000L;
        });

        rig.OnConnectNumber = n => rig.Source.ConnectFailure = n == 4 ? rig.Fail(() => new RfbException(QtGuardTestRig.Refused)) : null;
        await rig.RunMonitorAsync(_ =>
        {
            if (rig.Failures.Count == 3 && rig.Connects.Count == 5 && rig.SinceLastConnectMs >= 60_000 && rig.Source.CaptureFailure is null)
            {
                rig.ArmKick();
            }

            return rig.Failures.Count == 4 && rig.Connects.Count == 6;
        });

        Assert.Equal(new long[] { 60_000, 300_000, 2_000, 900_000 }, rig.WaitsAfterFailures());
    }


    [Fact]
    public async Task Naprawa01_Nadzorca_D3_WylaczenieIPonowneWlaczenieWTrakcieWstrzymania_BezNowejProby_KartaZOpisemWstrzymania()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        AppConfig on = QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true);
        Fixture f = Build(on);
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");

        await ApplyLikePanelAsync(f, QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: false), "amm520-2");
        Assert.Equal(MachineConnectionState.Disabled, supervisor.Snapshots.Single(s => s.MachineId == "amm520-2").State);
        await ApplyLikePanelAsync(f, on);
        await WaitUntilAsync(() => f.Loggers.Containing("nadal wstrzymuje próby połączenia").Count == 1, "wstrzymanie po ponownym włączeniu");
        await Task.Delay(2500); // ≥ 2 cykle podtrzymania po 1 s

        Assert.Equal(1, hmi2.ConnectionCount);
        MachineStatusSnapshot card = supervisor.Snapshots.Single(s => s.MachineId == "amm520-2");
        Assert.Equal(MachineConnectionState.Error, card.State);
        Assert.Contains("Kolejna próba za 5 min — serwer VNC Qt zamknął połączenie przed pierwszą klatką", card.LastError);
        Assert.Single(f.Loggers.Containing(EarlyClose));

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task Naprawa01_Nadzorca_ZmianaPortuMaszyny_InnySerwer_NowyStan_ProbaOdRazu()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        await using var hmi3 = new QtHmiAtrapa();
        Fixture f = Build(QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true));
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");

        // Ten sam identyfikator maszyny, inny port (inny serwer) — stan bezpiecznika od zera, połączenie od razu.
        await ApplyLikePanelAsync(f, QtConfig(hmi1.Port, hmi3.Port, enabled1: false, enabled2: true));
        await WaitUntilAsync(() => hmi3.ConnectionCount >= 1, "połączenie z nowym serwerem");
        await WaitUntilAsync(() => supervisor.Snapshots.Single(s => s.MachineId == "amm520-2").State == MachineConnectionState.Connected,
            "520 (2) połączona z nowym serwerem");

        Assert.Equal(1, hmi2.ConnectionCount);
        Assert.Empty(f.Loggers.Containing("nadal wstrzymuje"));

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task Naprawa01_Nadzorca_ZapisUstawienWTrakcieWstrzymania_AlertBrakLacznosciTenSam_BezDucha()
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        Fixture f = Build(QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true));
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2)");
        f.Clock.Advance(TimeSpan.FromMinutes(4)); // > offlineAlertAfterMs (180 s), < 5 min wstrzymania
        await Task.Delay(1500);
        f.Alerts.Tick();
        AlertEvent raised = Assert.Single(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline);
        int eventsBefore = f.Events.Count;

        await supervisor.StopAsync(); // AppOrchestrator.ReloadSecretsAndReconfigureAsync (zapis Ustawień)
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing("nadal wstrzymuje").Count == 1, "nowy monitor we wstrzymaniu");
        await Task.Delay(1500);
        f.Alerts.Tick();

        Assert.Equal(1, hmi2.ConnectionCount);
        Assert.DoesNotContain(f.Events.Skip(eventsBefore), e => e.Alert.MachineId == "amm520-2"); // ani Closed, ani nowy Raised
        Alert open = Assert.Single(f.Alerts.ActiveAlerts, a => a.Kind == AlertKind.Offline && a.MachineId == "amm520-2");
        Assert.Equal(raised.Alert.StartedAt, open.StartedAt);
        Assert.Single(f.Loggers.Containing(EarlyClose));
        Assert.DoesNotContain(f.Loggers.Entries, e => e.Level >= LogLevel.Error);

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }
}
