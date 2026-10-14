using System.Text.RegularExpressions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;
using static MaszynyAlert.Core.Tests.Machines.QtHmiSupervisor;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorQtGuardUwagiTests
{
    private const string Qt = QtEmbeddedVncServer.DesktopName;


    [Theory]
    [InlineData("d3-wlaczenie-520-1")]
    [InlineData("zapis-ustawien")]
    public async Task UWAGA_01_RestartMonitorowZPanelu_ZerujeBezpiecznikQt_NatychmiastowaProbaDoHmiWeWstrzymaniu(string action)
    {
        await using var hmi1 = new QtHmiAtrapa();
        await using var hmi2 = new QtHmiAtrapa(_ => QtHmiBehavior.CrashOnFirstRequest);
        Fixture f = Build(QtConfig(hmi1.Port, hmi2.Port, enabled1: false, enabled2: true));
        await using MonitorSupervisor supervisor = f.Supervisor;

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => f.Loggers.Containing(EarlyClose).Count == 1, "bezpiecznik 520 (2) po pierwszym padnięciu HMI");
        await Task.Delay(3000); // ≥ 3 cykle podtrzymania (po 1 s) — bez zmian ustawień żadnej próby
        Assert.Equal(1, hmi2.ConnectionCount);

        long switchedAt = hmi2.NowMs;
        if (action == "d3-wlaczenie-520-1")
        {
            await ApplyLikePanelAsync(f, QtConfig(hmi1.Port, hmi2.Port, enabled1: true, enabled2: true));
            await WaitUntilAsync(() => hmi1.ConnectionCount >= 1, "monitor 520 (1) po włączeniu w Panelu");
        }
        else
        {
            await supervisor.StopAsync(); // AppOrchestrator.ReloadSecretsAndReconfigureAsync
            await supervisor.StartAsync(CancellationToken.None);
        }

        await Task.Delay(3000);

        long[] times = hmi2.ConnectionTimesMs;
        string after = times.Length > 1 ? $"{times[1] - switchedAt} ms po akcji „{action}”" : "-";
        Assert.True(
            hmi2.ConnectionCount == 1,
            $"520 (2) we wstrzymaniu bezpiecznika (5 min) dostała {hmi2.ConnectionCount - 1} nową próbę ({after}); " +
            $"WRN „{EarlyClose}”: {f.Loggers.Containing(EarlyClose).Count}.");

        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(Patience);
    }


    [Fact]
    public async Task UWAGA_02_Charakterystyka_SerwerQt_ZerwanieSesjiPoKlatkach_PotemOdmowaPortu_Przejecie1Min_PotemZwyklyBackoff()
    {
        var rig = new QtGuardTestRig(maxCycles: 40, Qt);
        rig.OnConnectNumber = n => rig.Source.ConnectFailure = n >= 2 ? rig.Fail(() => new RfbException(QtGuardTestRig.Refused)) : null;
        rig.AfterCycle = c =>
        {
            if (c == 3) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.Equal(new long[] { 60_000, 5_000, 15_000, 30_000, 30_000, 30_000 }, rig.WaitsAfterFailures().Take(6));
        LogEntry takeover = Assert.Single(rig.Loggers.Containing("inny klient VNC przejął"));
        Assert.Contains("następna próba za 1 min", takeover.Message);
        Assert.Empty(rig.Loggers.Containing("zamknął połączenie przed pierwszą klatką"));
        Assert.Empty(rig.Loggers.Containing("kolejna nieudana próba"));
    }


    [Theory]
    [InlineData("odmowa")]
    [InlineData("padniecie")]
    public async Task UWAGA_03_Polling_SerwerQt_BladJednegoOdpytania_NieJestPrzejeciemInnegoKlienta(string failure)
    {
        await using var hmi = new QtHmiAtrapa(i => failure == "padniecie" && i >= 4 ? QtHmiBehavior.CrashOnFirstRequest : QtHmiBehavior.Serve);
        var clock = new FakeClock();
        var timing = new TimingConfig { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };
        var machine = new MachineConfig
        {
            Id = "amm520-1", Name = "520 (1)", Host = "127.0.0.1", Port = hmi.Port, Profile = "opencnc", Enabled = true,
            Mode = ConnectionMode.Polling, RequiresPassword = false,
        };
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var loggers = new RecordingLoggerFactory();
        var delays = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        var monitor = new MachineMonitor(machine, QtGuardTestRig.Profile, () => new VncScreenSource(machine, null, timing, loggers), null,
            new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules), alerts, clock, timing, new NullCaptureStore(),
            loggers.CreateLogger<MachineMonitor>(), Delay);

        Task Delay(TimeSpan interval, CancellationToken ct)
        {
            delays.Add(interval);
            clock.Advance(interval);
            alerts.Tick();
            if (delays.Count == 3 && failure == "odmowa") hmi.StopListening();
            if (delays.Count >= 6) cts.Cancel();
            return Task.CompletedTask;
        }

        await monitor.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(hmi.ConnectionCount >= 4, $"Połączeń z atrapą: {hmi.ConnectionCount}.");
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(timing.PollingModeIntervalMs), 3), delays.Take(3));
        IReadOnlyList<LogEntry> takeover = loggers.Containing("inny klient VNC przejął");
        Assert.True(
            takeover.Count == 0,
            $"Awaria odpytania ({failure}) w trybie polling sklasyfikowana jako przejęcie: odstęp {delays[3].TotalSeconds:0.#} s, WRN: " +
            string.Join(" | ", takeover.Select(w => w.Message[..Math.Min(150, w.Message.Length)])));
    }


    [Fact]
    public async Task UWAGA_04_SerwerQt_PoTrybiePlikuTestowego_OdmowaPortu_NieJestPrzejeciem()
    {
        using var dir = new TempDir("test-ekran");
        string file = Path.Combine(dir.Path, "amm520-1.png");
        var rig = new QtGuardTestRig(maxCycles: 8, Qt, new FolderScreenSource("amm520-1", dir.Path));
        rig.AfterCycle = c =>
        {
            if (c == 3) PngCodec.EncodeToFile(QtGuardTestRig.NormalFrame(), file);
            if (c == 5)
            {
                File.Delete(file);
                rig.Source.ConnectFailure = () => new RfbException(QtGuardTestRig.Refused);
            }
        };

        await rig.RunAsync();

        Assert.True(
            rig.Loggers.Containing("inny klient VNC przejął").Count == 0 && rig.Delays[5] == TimeSpan.FromSeconds(2),
            $"Po pliku testowym odmowa portu dała odstęp {rig.Delays[5].TotalSeconds:0.#} s i WRN: " +
            string.Join(" | ", rig.MonitorWarnings().Select(w => w.Message[..Math.Min(110, w.Message.Length)])));
    }


    [Fact]
    public async Task UWAGA_05_SerwerQt_OdrzuconeHasloWTrakcieBezpiecznika_LastErrorPodajeJedenCzasNastepnejProby()
    {
        // Bezpiecznik aktywny (krok 1), następna próba kończy się RfbAuthenticationException: odstęp = max(15 min, AuthRetryDelay(1)
        // = 30 s) = 15 min, ale LastError (karta maszyny w Panelu) podaje dwa różne czasy: „za 30 s — po odrzuceniu hasła”
        // i „za 15 min — bezpiecznik”.
        var rig = new QtGuardTestRig(maxCycles: 50, Qt);
        rig.OnConnectNumber = n =>
        {
            rig.Source.ConnectFailure = n >= 2 ? rig.Fail(() => new RfbAuthenticationException("Serwer VNC odrzucił hasło (test).")) : null;
            rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216) : null;
        };

        await rig.RunAsync();

        Assert.Equal(900_000, rig.WaitsAfterFailures()[1]); // faktyczny odstęp po odrzuconym haśle: 15 min
        string lastError = rig.LastErrors.First(e => e is not null && e.Contains("odrzucił hasło", StringComparison.Ordinal))!;
        MatchCollection next = Regex.Matches(lastError, @"Kolejna próba za (\d+ (?:s|min))");
        Assert.True(
            next.Count == 1,
            $"LastError podaje {next.Count} czasy następnej próby ({string.Join(", ", next.Select(m => m.Groups[1].Value))}): „{lastError}”.");
    }


    [Fact]
    public async Task UWAGA_06_SerwerQt_KazdyKrokBezpiecznikaToJednoWrn()
    {
        var rig = new QtGuardTestRig(maxCycles: 320, Qt);
        rig.OnConnectNumber = n =>
        {
            rig.Source.ConnectFailure = n >= 2 ? rig.Fail(() => new RfbException(QtGuardTestRig.Refused)) : null;
            rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216) : null;
        };

        await rig.RunAsync();

        IReadOnlyList<LogEntry> warnings = rig.MonitorWarnings();
        Assert.True(rig.Failures.Count >= 4, $"Kroków: {rig.Failures.Count}.");
        Assert.True(
            warnings.Count == rig.Failures.Count,
            $"{warnings.Count} WRN na {rig.Failures.Count} kroków bezpiecznika:{Environment.NewLine}" +
            string.Join(Environment.NewLine, warnings.Select(w => "  " + w.Message[..Math.Min(140, w.Message.Length)])));
    }
}
