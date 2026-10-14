using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorQtGuardTests
{
    private static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null };

    private const string ClosedByServer =
        "Połączenie z 192.168.1.101:5900 zostało zamknięte przez serwer w trakcie odczytu.";

    private static RgbaImage NormalFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));

    private static MachineConfig Machine() => new()
    {
        Id = "amm520-1",
        Name = "520 (1)",
        Host = "192.168.1.101",
        Port = 5900,
        Profile = "opencnc",
        Enabled = true,
        Mode = ConnectionMode.Persistent,
    };

    private sealed class Rig
    {
        private readonly int _maxCycles;
        private int _cycles;

        public Rig(int maxCycles, string desktopName)
        {
            _maxCycles = maxCycles;
            Alerts = new AlertStateMachine(Clock, Timing, new AckMemory(Clock, TimeSpan.FromHours(Timing.AckMemoryHours)));
            Alerts.Changed += Events.Add;
            Source = new FakeScreenSource("amm520-1", NormalFrame()) { DesktopName = desktopName };
            Monitor = new MachineMonitor(
                Machine(), Profile, () => Source, null, new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules),
                Alerts, Clock, Timing, new NullCaptureStore(), Loggers.CreateLogger<MachineMonitor>(), Delay);
        }

        public FakeClock Clock { get; } = new();

        public TimingConfig Timing { get; } = new() { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };

        public AlertStateMachine Alerts { get; }

        public List<AlertEvent> Events { get; } = [];

        public RecordingLoggerFactory Loggers { get; } = new();

        public FakeScreenSource Source { get; }

        public MachineMonitor Monitor { get; }

        public CancellationTokenSource Cts { get; } = new();

        public List<TimeSpan> Delays { get; } = [];

        /// <summary>Chwile (zegar monotoniczny) prób połączenia, które dotarły do serwera.</summary>
        public List<long> Attempts { get; } = [];

        public Action<int>? AfterCycle { get; set; }

        public Task RunAsync() => Monitor.RunAsync(Cts.Token);

        public void RecordAttempt() => Attempts.Add(Clock.MonotonicMs);

        public double[] AttemptGapsMinutes() =>
            Attempts.Zip(Attempts.Skip(1), (a, b) => (b - a) / 60000.0).ToArray();

        /// <summary>Chwile (zegar monotoniczny) zerwania działającej sesji przez serwer (przejęcie).</summary>
        public List<long> Kicks { get; } = [];

        /// <summary>Ile cykli minęło od ostatniego połączenia (do zaplanowania przejęcia w trakcie sesji).</summary>
        public int CyclesSinceConnect { get; private set; }

        private int _lastConnectCount;

        public void ArmKick() => Source.CaptureFailure = () =>
        {
            Source.CaptureFailure = null;
            Kicks.Add(Clock.MonotonicMs);
            return new RfbException(ClosedByServer);
        };

        /// <summary>Jednorazowe zamknięcie przed pierwszą klatką (216 ms po handshake'u).</summary>
        public void ArmEarlyClose() => Source.CaptureFailure = () =>
        {
            Source.CaptureFailure = null;
            Clock.Advance(TimeSpan.FromMilliseconds(216));
            return new RfbException(ClosedByServer);
        };

        public static double[] Minutes(IEnumerable<long> from, IEnumerable<long> to) =>
            from.Zip(to, (a, b) => Math.Round((b - a) / 60000.0, 2)).ToArray();

        private Task Delay(TimeSpan interval, CancellationToken ct)
        {
            Delays.Add(interval);
            Clock.Advance(interval);
            Alerts.Tick();
            if (Source.ConnectCount != _lastConnectCount)
            {
                _lastConnectCount = Source.ConnectCount;
                CyclesSinceConnect = 0;
            }

            CyclesSinceConnect++;
            _cycles++;
            AfterCycle?.Invoke(_cycles);
            if (_cycles >= _maxCycles) Cts.Cancel();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SerwerQt_ZamknieciePrzedPierwszaKlatka_PotemOdmowa_ProbyCo5_15_60_60Min()
    {
        var rig = new Rig(maxCycles: 300, QtEmbeddedVncServer.DesktopName);
        rig.Source.CaptureFailure = () =>
        {
            rig.RecordAttempt();
            rig.Clock.Advance(TimeSpan.FromMilliseconds(216));
            rig.Source.ConnectFailure = () =>
            {
                rig.RecordAttempt();
                return new RfbException("Nie udało się połączyć z 192.168.1.101:5900: Nie można nawiązać połączenia, ponieważ komputer docelowy aktywnie go odmawia.");
            };
            return new RfbException(ClosedByServer);
        };

        await rig.RunAsync();

        Assert.Equal(new double[] { 5, 15, 60, 60 }, rig.AttemptGapsMinutes().Take(4).Select(m => Math.Round(m, 2)));
        LogEntry first = Assert.Single(rig.Loggers.Containing("zamknął połączenie przed pierwszą klatką"));
        Assert.Equal(LogLevel.Warning, first.Level);
        Assert.Contains("serwer VNC Qt zamknął połączenie przed pierwszą klatką — możliwa awaria HMI; następna próba za 5 min", first.Message);
        Assert.Contains(rig.Loggers.Containing("bezpiecznik serwera VNC Qt"), e => e.Message.Contains("następna próba za 15 min"));
        Assert.Contains("Kolejna próba za 60 min", rig.Monitor.Snapshot.LastError);

        // Czekanie bezpiecznika podtrzymuje „Brak łączności” bez łączenia: jeden alert Offline, bez zamykania i otwierania co próbę.
        Assert.Single(rig.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.Offline);
        Assert.DoesNotContain(rig.Events, e => e.Kind == AlertEventKind.Closed && e.Alert.Kind == AlertKind.Offline);
        Assert.All(rig.Delays, d => Assert.True(d <= TimeSpan.FromSeconds(30), $"Odstęp cyklu {d} > 30 s."));
    }

    [Fact]
    public async Task SerwerQt_BladProtokolu1282_NastepnaProbaZa5Min()
    {
        var rig = new Rig(maxCycles: 40, QtEmbeddedVncServer.DesktopName);
        rig.Source.CaptureFailure = () =>
        {
            rig.RecordAttempt();
            return new RfbProtocolException("Nieobsługiwane kodowanie prostokąta aktualizacji: 1282 (serwer 192.168.1.101:5900).");
        };

        await rig.RunAsync();

        Assert.Equal(5, Math.Round(rig.AttemptGapsMinutes()[0], 2));
        Assert.Contains(rig.Loggers.Containing("przysłał dane niezgodne z protokołem"),
            e => e.Level == LogLevel.Warning && e.Message.Contains("następna próba za 5 min"));
    }

    [Fact]
    public async Task SerwerQt_PierwszaPoprawnaKlatka_ZdejmujeBezpiecznik()
    {
        // Sesja 1: zamknięcie przed klatką → 5 min. Sesja 2: klatki (bezpiecznik zdjęty), potem przejęcie → 1 min.
        // Sesja 3: znowu zamknięcie przed klatką → 5 min (od początku, nie 15), bo sesja 2 dała klatki.
        var rig = new Rig(maxCycles: 80, QtEmbeddedVncServer.DesktopName);
        var connects = new List<long>();
        rig.Source.OnConnect = () =>
        {
            connects.Add(rig.Clock.MonotonicMs);
            if (connects.Count is 1 or 3) rig.ArmEarlyClose();
        };
        rig.AfterCycle = _ =>
        {
            if (connects.Count == 2 && rig.CyclesSinceConnect == 3 && rig.Kicks.Count == 0) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.True(connects.Count >= 4, $"Połączeń: {connects.Count}.");
        Assert.Equal(5, Rig.Minutes(connects.Take(1), connects.Skip(1))[0], 1);
        Assert.Equal(1, Rig.Minutes(rig.Kicks, connects.Skip(2))[0]);
        Assert.Equal(5, Rig.Minutes(connects.Skip(2), connects.Skip(3))[0], 1);
        Assert.NotEmpty(rig.Loggers.Containing("bezpiecznik ponowień zdjęty"));
        Assert.Equal(2, rig.Loggers.Containing("zamknął połączenie przed pierwszą klatką").Count);
        Assert.Equal(MachineConnectionState.Connected, rig.Monitor.Snapshot.State);
    }

    [Fact]
    public async Task SerwerQt_PrzejecieDzialajacejSesji_NastepnaProbaZa1_5_15_15Min()
    {
        var rig = new Rig(maxCycles: 150, QtEmbeddedVncServer.DesktopName);
        var connects = new List<long>();
        rig.Source.OnConnect = () => connects.Add(rig.Clock.MonotonicMs);
        rig.AfterCycle = _ =>
        {
            if (rig.CyclesSinceConnect == 3 && rig.Source.CaptureFailure is null && rig.Kicks.Count < connects.Count) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.True(rig.Kicks.Count >= 4, $"Przejęć: {rig.Kicks.Count}.");
        Assert.Equal(new double[] { 1, 5, 15, 15 }, Rig.Minutes(rig.Kicks, connects.Skip(1)).Take(4));
        LogEntry first = rig.Loggers.Containing("inny klient VNC przejął")[0];
        Assert.Equal(LogLevel.Warning, first.Level);
        Assert.Contains("inny klient VNC przejął 520 (1) (serwer Qt, jedno połączenie naraz) — sprawdź, czy RealVNC nie łączy się z tą maszyną; następna próba za 1 min", first.Message);
        Assert.Empty(rig.Loggers.Containing("możliwa awaria HMI"));
        Assert.All(rig.Delays, d => Assert.True(d <= TimeSpan.FromSeconds(30), $"Odstęp cyklu {d} > 30 s."));
    }

    [Fact]
    public async Task SerwerQt_PrzejeciePoStabilnejSesjiPonad10Min_OdstepWracaDo1Min()
    {
        var rig = new Rig(maxCycles: 700, QtEmbeddedVncServer.DesktopName);
        var connects = new List<long>();
        rig.Source.OnConnect = () => connects.Add(rig.Clock.MonotonicMs);
        rig.AfterCycle = _ =>
        {
            bool due = (connects.Count == 1 && rig.CyclesSinceConnect == 3)
                || (connects.Count == 2 && rig.CyclesSinceConnect == 11 * 60); // sesja ~11 min przy odczycie co 1 s
            if (due && rig.Kicks.Count < connects.Count) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.Equal(2, rig.Kicks.Count);
        Assert.Equal(new double[] { 1, 1 }, Rig.Minutes(rig.Kicks, connects.Skip(1)).Take(2));
    }

    [Fact]
    public async Task SerwerInnyNizQt_ZerwanieDzialajacejSesji_ZwyklyBackoff2s()
    {
        var rig = new Rig(maxCycles: 10, "68EDA4721759");
        rig.AfterCycle = _ =>
        {
            if (rig.CyclesSinceConnect == 3 && rig.Kicks.Count == 0) rig.ArmKick();
        };

        await rig.RunAsync();

        Assert.Single(rig.Kicks);
        Assert.Contains(TimeSpan.FromSeconds(2), rig.Delays);
        Assert.Equal(2, rig.Source.ConnectCount);
        Assert.Empty(rig.Loggers.Containing("serwer VNC Qt"));
    }

    [Fact]
    public async Task SerwerQt_ZamkniecieDluzejNiz1sPoHandshake_ZwyklyBackoff()
    {
        var rig = new Rig(maxCycles: 8, QtEmbeddedVncServer.DesktopName);
        rig.Source.CaptureFailure = () =>
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            return new RfbException(ClosedByServer);
        };

        await rig.RunAsync();

        Assert.Empty(rig.Loggers.Containing("serwer VNC Qt"));
        Assert.Equal(8, rig.Source.ConnectCount);
    }

    [Theory]
    [InlineData("WinCE")]
    [InlineData("OPENCNC2")]
    [InlineData("68EDA4721759")]
    [InlineData("test")]
    public async Task SerwerInnyNizQt_ZamknieciePrzedPierwszaKlatkaIBladProtokolu_BackoffBezZmian(string desktopName)
    {
        int attempt = 0;
        var rig = new Rig(maxCycles: 6, desktopName);
        rig.Source.CaptureFailure = () => ++attempt % 2 == 0
            ? new RfbProtocolException("Nieobsługiwane kodowanie prostokąta aktualizacji: 1282 (test).")
            : new RfbException(ClosedByServer);

        await rig.RunAsync();

        Assert.Equal(new[] { 2, 5, 15, 30, 30 }.Select(s => TimeSpan.FromSeconds(s)), rig.Delays.Take(5));
        Assert.Empty(rig.Loggers.Containing("serwer VNC Qt"));
        Assert.Equal(6, rig.Source.ConnectCount);
    }
}
