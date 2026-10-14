using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorQtGuardNaprawaTests
{
    private const string Qt = QtEmbeddedVncServer.DesktopName;

    // ---------------------------------------------------------------------------------------------------------------------
    // tryb polling — nieudane odpytanie nie jest przejęciem
    // ---------------------------------------------------------------------------------------------------------------------

    private sealed class PollingRig : IAsyncDisposable
    {
        private readonly int _maxDelays;

        public PollingRig(Func<int, QtHmiBehavior> behavior, int maxDelays, Action<PollingRig>? afterDelay = null)
        {
            _maxDelays = maxDelays;
            Hmi = new QtHmiAtrapa(behavior);
            var machine = new MachineConfig
            {
                Id = "amm520-1", Name = "520 (1)", Host = "127.0.0.1", Port = Hmi.Port, Profile = "opencnc", Enabled = true,
                Mode = ConnectionMode.Polling, RequiresPassword = false,
            };
            Alerts = new AlertStateMachine(Clock, Timing, new AckMemory(Clock, TimeSpan.FromHours(2)));
            Monitor = new MachineMonitor(machine, QtGuardTestRig.Profile, () => new VncScreenSource(machine, null, Timing, Loggers), null,
                new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules), Alerts, Clock, Timing, new NullCaptureStore(),
                Loggers.CreateLogger<MachineMonitor>(), (interval, _) =>
                {
                    Delays.Add(interval);
                    Clock.Advance(interval);
                    Alerts.Tick();
                    afterDelay?.Invoke(this);
                    if (Delays.Count >= _maxDelays) Cts.Cancel();
                    return Task.CompletedTask;
                });
        }

        public QtHmiAtrapa Hmi { get; }

        public FakeClock Clock { get; } = new();

        public TimingConfig Timing { get; } = new() { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };

        public AlertStateMachine Alerts { get; }

        public RecordingLoggerFactory Loggers { get; } = new();

        public MachineMonitor Monitor { get; }

        public List<TimeSpan> Delays { get; } = [];

        public CancellationTokenSource Cts { get; } = new();

        public Task RunAsync() => Monitor.RunAsync(Cts.Token).WaitAsync(TimeSpan.FromSeconds(60));

        public IReadOnlyList<LogEntry> MonitorWarnings() => Loggers.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(MachineMonitor), StringComparison.Ordinal))
            .ToArray();

        public async ValueTask DisposeAsync()
        {
            Cts.Dispose();
            await Hmi.DisposeAsync();
        }
    }

    [Fact]
    public async Task Naprawa03_Polling_SerwerQt_OdmowaPortuPrzyOdpytaniu_ZwyklyBackoff2_5s_BezWrnOPrzejeciu()
    {
        // Połączenie 0 = ConnectAsync monitora (sam handshake), 1–3 = udane odpytania; po 3. odstępie port przestaje nasłuchiwać.
        // odmowa = zwykły backoff 2 s, 5 s (jak każda awaria bez bezpiecznika), jeden zwykły „Błąd VNC maszyny…”.
        await using var rig = new PollingRig(_ => QtHmiBehavior.Serve, maxDelays: 5, r =>
        {
            if (r.Delays.Count == 3) r.Hmi.StopListening();
        });

        await rig.RunAsync();

        TimeSpan poll = TimeSpan.FromMilliseconds(rig.Timing.PollingModeIntervalMs);
        Assert.Equal(new[] { poll, poll, poll, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, rig.Delays);
        Assert.Equal(4, rig.Hmi.ConnectionCount);
        Assert.Empty(rig.Loggers.Containing("inny klient VNC przejął"));
        Assert.Empty(rig.Loggers.Containing("serwer VNC Qt"));
        Assert.Contains(rig.MonitorWarnings(), w => w.Message.StartsWith("Błąd VNC maszyny 'amm520-1'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Naprawa03_Polling_SerwerQt_PadniecieHmiPrzyOdpytaniu_ZwyklyBackoff_PadnieciePoPonownymHandshakeu_Bezpiecznik5Min()
    {
        await using var rig = new PollingRig(i => i >= 4 ? QtHmiBehavior.CrashOnFirstRequest : QtHmiBehavior.Serve, maxDelays: 9);

        await rig.RunAsync();

        TimeSpan poll = TimeSpan.FromMilliseconds(rig.Timing.PollingModeIntervalMs);
        Assert.Equal(new[] { poll, poll, poll, TimeSpan.FromSeconds(2) }, rig.Delays.Take(4));
        Assert.All(rig.Delays.Skip(4), d => Assert.Equal(TimeSpan.FromSeconds(30), d));
        Assert.Equal(7, rig.Hmi.ConnectionCount);
        Assert.Empty(rig.Loggers.Containing("inny klient VNC przejął"));
        LogEntry guard = Assert.Single(rig.Loggers.Containing("zamknął połączenie przed pierwszą klatką"));
        Assert.Contains("następna próba za 5 min", guard.Message);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // jeden, prawdziwy czas następnej próby w LastError
    // ---------------------------------------------------------------------------------------------------------------------

    private const string AuthTail =
        "po odrzuceniu hasła program ponawia coraz rzadziej, żeby serwer nie zablokował adresu tego komputera. " +
        "Popraw hasło w Panelu → Ustawienia → Ten komputer.";

    /// <summary>WRN monitora bez jednorazowego ostrzeżenia o pasku postępu poza syntetyczną klatką 966×146 (artefakt atrapy).</summary>
    private static IReadOnlyList<LogEntry> VncWarnings(QtGuardTestRig rig) =>
        rig.MonitorWarnings().Where(w => !w.Message.Contains("idle.progressRoi", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task Naprawa05_SerwerQt_OdrzuconeHasloPrzyAktywnymBezpieczniku_LastErrorJedenCzasIUzasadnienieHasla()
    {
        // Bezpiecznik po zamknięciu przed klatką (krok 1), potem każda próba: odrzucone hasło. Odstępy 15 i 60 min (bezpiecznik
        // wygrywa z 30 s / 60 s hasła), w LastError i w każdym wpisie WRN — jeden czas następnej próby.
        var rig = new QtGuardTestRig(maxCycles: 200, Qt);
        rig.OnConnectNumber = n =>
        {
            rig.Source.ConnectFailure = n >= 2 ? rig.Fail(() => new RfbAuthenticationException("Serwer VNC odrzucił hasło (test).")) : null;
            rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216) : null;
        };

        await rig.RunAsync();

        Assert.Equal(new long[] { 300_000, 900_000, 3_600_000 }, rig.WaitsAfterFailures().Take(3));
        string[] authErrors = rig.LastErrors.Where(e => e is not null && e.Contains("odrzucił hasło", StringComparison.Ordinal))
            .Distinct().ToArray()!;
        Assert.Equal(
            [
                "Serwer VNC odrzucił hasło (test). Kolejna próba za 15 min — bezpiecznik serwera VNC Qt (wcześniej możliwa awaria HMI). Po" + AuthTail[2..],
                "Serwer VNC odrzucił hasło (test). Kolejna próba za 60 min — bezpiecznik serwera VNC Qt (wcześniej możliwa awaria HMI). Po" + AuthTail[2..],
            ],
            authErrors);
        // Wpis bezpiecznika podaje sam błąd (bez zdań o kolejnej próbie) — jeden czas także w logu; bez śladu stosu (hasło).
        IReadOnlyList<LogEntry> steps = rig.Loggers.Containing("kolejna nieudana próba");
        Assert.Equal(rig.Failures.Count - 1, steps.Count);
        Assert.Equal(
            "Maszyna 'amm520-1': bezpiecznik serwera VNC Qt (wcześniej zamknięcie przed pierwszą klatką albo błąd protokołu) — kolejna " +
            "nieudana próba; następna próba za 15 min (Serwer VNC odrzucił hasło (test).).",
            steps[0].Message);
        Assert.All(steps.Skip(1), s => Assert.EndsWith("następna próba za 60 min (Serwer VNC odrzucił hasło (test).).", s.Message));
        Assert.All(steps, s => Assert.Null(s.Exception));
        Assert.Empty(rig.Loggers.Containing("Błąd VNC maszyny"));
    }

    [Theory]
    [InlineData("68EDA4721759")]
    [InlineData("WinCE")]
    public async Task Naprawa05_SerwerInnyNizQt_OdrzuconeHaslo_LastErrorJakDotad(string desktopName)
    {
        var rig = new QtGuardTestRig(maxCycles: 3, desktopName);
        rig.Source.ConnectFailure = () => new RfbAuthenticationException("Serwer VNC odrzucił hasło (test).");

        await rig.RunAsync();

        Assert.Equal(new[] { 30, 60, 120 }.Select(s => TimeSpan.FromSeconds(s)), rig.Delays);
        Assert.Equal($"Serwer VNC odrzucił hasło (test). Kolejna próba za 30 s — {AuthTail}", rig.LastErrors[0]);
        Assert.Equal($"Serwer VNC odrzucił hasło (test). Kolejna próba za 1 min — {AuthTail}", rig.LastErrors[1]);
        Assert.Single(rig.Loggers.Containing("Błąd VNC maszyny"));
        Assert.Empty(rig.Loggers.Containing("serwer VNC Qt"));
    }

    [Fact]
    public async Task Naprawa05_SerwerQtBezAktywnegoBezpiecznika_OdrzuconeHaslo_ZwyklaSciezkaHasla()
    {
        var rig = new QtGuardTestRig(maxCycles: 12, Qt);
        rig.OnConnectNumber = n => rig.Source.ConnectFailure = n >= 2
            ? () => new RfbAuthenticationException("Serwer VNC odrzucił hasło (test).")
            : null;
        rig.AfterCycle = c =>
        {
            if (c == 3) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.True(rig.Source.ConnectCount >= 2, $"Połączeń: {rig.Source.ConnectCount}.");
        string lastError = rig.LastErrors.First(e => e is not null && e.Contains("odrzucił hasło", StringComparison.Ordinal))!;
        Assert.Equal($"Serwer VNC odrzucił hasło (test). Kolejna próba za 30 s — {AuthTail}", lastError);
        Assert.Equal("Błąd VNC maszyny 'amm520-1': Serwer VNC odrzucił hasło (test).", rig.Loggers.Containing("Błąd VNC maszyny")[0].Message);
        Assert.Single(VncWarnings(rig), w => w.Message.Contains("inny klient VNC przejął", StringComparison.Ordinal));
        Assert.Empty(rig.Loggers.Containing("kolejna nieudana próba"));
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // jeden wpis WRN na krok bezpiecznika i na przejęcie
    // ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Naprawa06_SerwerQt_KrokiBezpiecznika_JedenWrnNaKrok_SladStosuTylkoPrzyNowejTresci_PrzywrocenieLiczyKroki()
    {
        // Próba 1: handshake OK, zamknięcie 216 ms później (5 min). Próby 2–4: port odmawia (15, 60, 60 min). Próba 5: HMI działa.
        var rig = new QtGuardTestRig(maxCycles: 320, Qt);
        rig.OnConnectNumber = n =>
        {
            rig.Source.ConnectFailure = n is >= 2 and <= 4 ? rig.Fail(() => new RfbException(QtGuardTestRig.Refused)) : null;
            rig.Source.CaptureFailure = n == 1 ? rig.Fail(() => new RfbException(QtGuardTestRig.ClosedByServer), 216) : null;
        };

        await rig.RunAsync();

        Assert.Equal(new long[] { 300_000, 900_000, 3_600_000, 3_600_000 }, rig.WaitsAfterFailures());
        IReadOnlyList<LogEntry> warnings = VncWarnings(rig);
        Assert.Equal(4, warnings.Count);
        Assert.Contains("zamknął połączenie przed pierwszą klatką", warnings[0].Message);
        Assert.All(warnings.Skip(1), w => Assert.Contains("kolejna nieudana próba", w.Message));
        Assert.Empty(rig.Loggers.Containing("Błąd VNC maszyny"));
        // Ślad stosu przy pierwszym wystąpieniu każdej treści błędu w serii (jak LogVncFailure), potem bez.
        Assert.Equal(QtGuardTestRig.ClosedByServer, Assert.IsType<RfbException>(warnings[0].Exception).Message);
        Assert.Equal(QtGuardTestRig.Refused, Assert.IsType<RfbException>(warnings[1].Exception).Message);
        Assert.Null(warnings[2].Exception);
        Assert.Null(warnings[3].Exception);
        // Seria błędów liczy się dalej: wpis o przywróceniu po 4 próbach i o zdjęciu bezpiecznika.
        Assert.Single(rig.Loggers.Containing("przywrócone po 4 nieudanych próbach"));
        Assert.Single(rig.Loggers.Containing("bezpiecznik ponowień zdjęty po 4 dłuższych odstępach"));
        Assert.Equal(MachineConnectionState.Connected, rig.Monitor.Snapshot.State);
    }

    [Fact]
    public async Task Naprawa06_SerwerQt_KolejnePrzejecia_JedenWrnNaPrzejecie_ZeSlademStosu()
    {
        var rig = new QtGuardTestRig(maxCycles: 100, Qt);
        rig.AfterCycle = _ =>
        {
            if (rig.CyclesSinceConnect == 3 && rig.Source.CaptureFailure is null && rig.Failures.Count < rig.Connects.Count) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.True(rig.Failures.Count >= 3, $"Przejęć: {rig.Failures.Count}.");
        IReadOnlyList<LogEntry> warnings = VncWarnings(rig);
        Assert.Equal(rig.Failures.Count, warnings.Count);
        Assert.All(warnings, w =>
        {
            Assert.Contains("inny klient VNC przejął 520 (1)", w.Message);
            Assert.IsType<RfbException>(w.Exception); // każde przejęcie zaczyna nową serię (po nim sesja wróciła)
        });
        Assert.Empty(rig.Loggers.Containing("Błąd VNC maszyny"));
        Assert.Equal(rig.Failures.Count - 1, rig.Loggers.Containing("przywrócone po 1 nieudanych próbach").Count);
    }
}

