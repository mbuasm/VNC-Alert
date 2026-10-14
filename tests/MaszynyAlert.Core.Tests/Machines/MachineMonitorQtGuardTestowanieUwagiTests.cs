using System.Globalization;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorQtGuardTestowanieUwagiTests
{
    internal sealed class Rig
    {
        public Rig()
        {
            Alerts = new AlertStateMachine(Clock, Timing, new AckMemory(Clock, TimeSpan.FromHours(Timing.AckMemoryHours)));
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

        public RecordingLoggerFactory Loggers { get; } = new();

        public FakeScreenSource Source { get; }

        public QtGuardState State { get; } = new("192.168.1.101", 5900);

        public List<long> Connects { get; } = [];

        public List<long> Failures { get; } = [];

        public Action<int>? OnConnectNumber { get; set; }

        public long SinceLastConnectMs => Clock.MonotonicMs - Connects[^1];

        /// <summary>
        /// Nowy monitor tej maszyny ze wspólnym stanem — utworzony (konstruktor czyta stan), ale bez uruchomionej pętli; pętla działa,
        /// aż <paramref name="stop"/>(numer cyklu tego monitora) zwróci true.
        /// </summary>
        public (MachineMonitor Monitor, Func<Task> RunAsync) Create(Func<int, bool> stop)
        {
            var cts = new CancellationTokenSource();
            int cycles = 0;
            var monitor = new MachineMonitor(
                QtGuardTestRig.Machine(), QtGuardTestRig.Profile, () => Source, null, new FakeOcrEngine(),
                new MessageMapper(TestPaths.ExampleRules), Alerts, Clock, Timing, new NullCaptureStore(), Loggers.CreateLogger<MachineMonitor>(),
                (interval, _) =>
                {
                    cycles++;
                    Clock.Advance(interval);
                    Alerts.Tick();
                    if (stop(cycles)) cts.Cancel();
                    return Task.CompletedTask;
                },
                qtGuard: State);
            return (monitor, async () =>
            {
                using (cts)
                {
                    await monitor.RunAsync(cts.Token);
                }
            });
        }

        public Task RunMonitorAsync(Func<int, bool> stop) => Create(stop).RunAsync();

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

        /// <summary>Odstęp od każdej awarii do następnej próby połączenia (ms; −1 — brak kolejnej próby).</summary>
        public long[] WaitsAfterFailures() => Failures
            .Select(f => Connects.FirstOrDefault(c => c > f, -1) is var next && next < 0 ? -1 : next - f)
            .ToArray();

        public IReadOnlyList<LogEntry> VncWarnings() => Loggers.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(MachineMonitor), StringComparison.Ordinal)
                && !e.Message.Contains("idle.progressRoi", StringComparison.Ordinal))
            .ToArray();

        public long[] AnnouncedWaitsMs() => VncWarnings()
            .Select(w => Regex.Match(w.Message, @"następna próba za (\d+) (min|s)"))
            .Where(m => m.Success)
            .Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value == "min" ? 60_000 : 1_000))
            .ToArray();
    }

    [Fact]
    public async Task TB01_Charakterystyka_WstrzymanieMijaPrzedPierwszymCyklem_WlasneWstrzymanieMonitoraZWpisemPoPrzebudowie()
    {
        var rig = new Rig();
        long monitor1StartMs = rig.Clock.MonotonicMs;
        rig.OnConnectNumber = _ => rig.Source.CaptureFailure = rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216);

        await rig.RunMonitorAsync(c => c >= 2); // padnięcie + 1 cykl podtrzymania (30 s)
        Assert.Equal(monitor1StartMs, Assert.Single(rig.Connects));
        Assert.Equal(monitor1StartMs + 216, Assert.Single(rig.Failures));
        Assert.Equal<long?>(rig.Failures[0] + 300_000, rig.State.NextAttemptMs); // wstrzymanie 5 min założone przez monitor 1

        var rebuildInfosAfterCycle = new List<int>();
        var (monitor2, run2) = rig.Create(c =>
        {
            rebuildInfosAfterCycle.Add(rig.Loggers.Containing("po przebudowie monitora").Count);
            return c >= 3;
        });
        Assert.Equal(MachineConnectionState.Error, monitor2.Snapshot.State);
        Assert.Contains("Kolejna próba za 5 min", rig.State.HoldError);
        Assert.Equal(rig.State.HoldError, monitor2.Snapshot.LastError);
        rig.Clock.Advance(TimeSpan.FromMinutes(5)); // termin wstrzymania mija, zanim pętla monitora 2 wykona pierwszy cykl
        long monitor2StartMs = rig.Clock.MonotonicMs;
        await run2();

        // Monitor 2 połączył się sam w pierwszym cyklu (wstrzymanie już minęło), HMI padło po 216 ms → jego WŁASNE wstrzymanie 15 min.
        Assert.Equal(new[] { monitor1StartMs, monitor2StartMs }, rig.Connects);
        Assert.Equal(new[] { monitor1StartMs + 216, monitor2StartMs + 216 }, rig.Failures);
        Assert.Equal(new long[] { 300_000, 900_000 }, rig.AnnouncedWaitsMs());
        Assert.Equal(2, rig.State.GuardStep);
        Assert.Equal<long?>(rig.Failures[1] + 900_000, rig.State.NextAttemptMs);

        // flaga przeżyła pierwszy cykl (bez wstrzymania), więc pierwszy cykl czekania na WŁASNE wstrzymanie
        // monitora (cykl 2) loguje dokładnie jeden wpis INF „po przebudowie monitora” z czasem tego wstrzymania (870 216 ms → „15 min”),
        // potem flaga gaśnie (cykl 3 bez wpisu).
        Assert.Equal(new[] { 0, 1, 1 }, rebuildInfosAfterCycle);
        LogEntry rebuild = Assert.Single(rig.Loggers.Containing("po przebudowie monitora"));
        Assert.Equal(LogLevel.Information, rebuild.Level);
        Assert.Equal(
            "Maszyna 'amm520-1': bezpiecznik serwera VNC Qt nadal wstrzymuje próby połączenia po przebudowie monitora — następna próba za 15 min.",
            rebuild.Message);
    }

    [Fact]
    public async Task TB02_Charakterystyka_StabilnaSesjaZatrzymanaNaDwieGodziny_PrzerwaBezMonitoraLiczonaJakoObserwacja_PrzejeciePoPowrocie60s()
    {
        var rig = new Rig();
        // Monitor 1: dwa szybkie przejęcia (1 min, 5 min — krok przejęć 2), potem stabilna sesja 1 min i zatrzymanie przez nadzorcę.
        await rig.RunMonitorAsync(_ =>
        {
            if (rig.Failures.Count < 2 && rig.Connects.Count == rig.Failures.Count + 1 && rig.SinceLastConnectMs >= 3000
                && rig.Source.CaptureFailure is null)
            {
                rig.ArmKick();
            }

            return rig.Failures.Count == 2 && rig.Connects.Count == 3 && rig.SinceLastConnectMs >= 60_000;
        });
        Assert.Equal(new long[] { 60_000, 300_000 }, rig.WaitsAfterFailures());
        Assert.Equal(2, rig.State.TakeoverStep);
        long stoppedAtMs = rig.Clock.MonotonicMs;
        Assert.Equal(60_000L, stoppedAtMs - rig.Connects[2]); // stabilna sesja 1 min przed zatrzymaniem przez nadzorcę
        Assert.Equal<long?>(rig.Connects[2], rig.State.StableSinceMs); // zapamiętany POCZĄTEK tej sesji, nie chwila zatrzymania

        // Dwie godziny bez monitora tej maszyny na tym komputerze (StopAsync bez StartAsync: obserwuje inny komputer grupy).
        rig.Clock.Advance(TimeSpan.FromHours(2));

        // Monitor 2 (ten sam stan): sesja 1 min i przejęcie.
        await rig.RunMonitorAsync(_ =>
        {
            if (rig.Failures.Count == 2 && rig.Connects.Count == 4 && rig.SinceLastConnectMs >= 60_000 && rig.Source.CaptureFailure is null)
            {
                rig.ArmKick();
            }

            return rig.Failures.Count == 3 && rig.Connects.Count == 5;
        });

        // Monitor 2 łączy się od razu po powrocie, sesja trwa 1 min — z monitorem łącznie 1 + 1 min, ale długość sesji liczona od
        // StableSinceMs to 2 h 2 min (≥ 10 min), więc kod zeruje krok przejęć: po trzecim przejęciu następna próba po 60 s
        // (zapowiedź WRN „za 1 min”), nie po 900 s; krok przejęć = 1 (wyzerowany i zwiększony; bez resetu byłoby 3).
        Assert.Equal(stoppedAtMs + 7_200_000, rig.Connects[3]);
        Assert.Equal(60_000L, rig.Failures[2] - rig.Connects[3]);
        Assert.Equal(new long[] { 60_000, 300_000, 60_000 }, rig.WaitsAfterFailures());
        Assert.Equal(new long[] { 60_000, 300_000, 60_000 }, rig.AnnouncedWaitsMs());
        Assert.Equal(1, rig.State.TakeoverStep);
    }
}
