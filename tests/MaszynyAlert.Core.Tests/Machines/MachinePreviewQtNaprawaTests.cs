using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachinePreviewQtNaprawaTests
{
    private const string ClosedByServer = "Połączenie z 127.0.0.1:5900 zostało zamknięte przez serwer w trakcie odczytu.";

    private sealed class Rig
    {
        public Rig(string desktopName, string profile = "opencnc", bool enabled = true)
        {
            Machine = new MachineConfig
            {
                Id = "amm520-1", Name = "520 (1)", Host = "127.0.0.1", Port = 5900, Profile = profile, Enabled = enabled,
                RequiresPassword = false, Mode = ConnectionMode.Persistent,
            };
            Config = new AppConfig { Enabled = true, Machines = [Machine] };
            Alerts = new AlertStateMachine(Clock, Timing, new AckMemory(Clock, TimeSpan.FromHours(2)));
            Source = new FakeScreenSource("amm520-1", PreviewQtMonitorRig.NormalFrame()) { DesktopName = desktopName };
        }

        public MachineConfig Machine { get; }

        public AppConfig Config { get; }

        public FakeClock Clock { get; } = new();

        public TimingConfig Timing { get; } = new() { ReconnectBackoffMs = [2000, 5000, 15000, 30000] };

        public AlertStateMachine Alerts { get; }

        public FakeScreenSource Source { get; }

        public QtGuardState State { get; } = new("127.0.0.1", 5900);

        /// <summary>Nowy monitor tej maszyny ze wspólnym stanem bezpieczników (jak po przebudowie przez nadzorcę).</summary>
        public MachineMonitor NewMonitor(MachineConfig? machine = null, Func<int, bool>? stop = null, CancellationTokenSource? cts = null)
        {
            int cycles = 0;
            return new MachineMonitor(
                machine ?? Machine, PreviewQtMonitorRig.Profile, () => Source, null, new FakeOcrEngine(), new MessageMapper(TestPaths.ExampleRules),
                Alerts, Clock, Timing, new NullCaptureStore(), null,
                (interval, _) =>
                {
                    Clock.Advance(interval);
                    Alerts.Tick();
                    if (stop?.Invoke(++cycles) == true) cts?.Cancel();
                    return Task.CompletedTask;
                },
                qtGuard: State);
        }

        /// <summary>Podgląd złożony jak w <c>AppOrchestrator</c> na obserwatorze: klatka, nazwa pulpitu i migawka monitora.</summary>
        public MachinePreviewService Preview(MachineMonitor monitor, Action opened, Func<AppConfig, MachineConfig, IScreenSource?>? testFile = null) =>
            new(
                () => Config,
                _ => monitor.PreviewFrame,
                (_, _) =>
                {
                    opened();
                    return new FakeScreenSource("amm520-1", PreviewQtMonitorRig.NormalFrame());
                },
                Clock,
                _ => monitor.Snapshot.Info?.DesktopName,
                testFile,
                _ => monitor.Snapshot);

        /// <summary>Pierwszy monitor: handshake, serwer zamyka 216 ms później przed pierwszą klatką → bezpiecznik 5 min; zatrzymany po 2 cyklach.</summary>
        public async Task RunFirstMonitorUntilHoldAsync()
        {
            Source.OnConnect = () =>
            {
                if (Source.ConnectCount == 1)
                {
                    Source.CaptureFailure = () =>
                    {
                        Source.CaptureFailure = null;
                        Clock.Advance(TimeSpan.FromMilliseconds(216));
                        return new RfbException(ClosedByServer);
                    };
                }
            };
            using var cts = new CancellationTokenSource();
            MachineMonitor first = NewMonitor(stop: c => c >= 2, cts: cts);
            await first.RunAsync(cts.Token);
            Assert.Contains("Kolejna próba za 5 min", first.Snapshot.LastError);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // nazwa pulpitu z handshake'u, także po przebudowie monitora
    // ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PodgladNaprawa01_PrzebudowaMonitoraWTrakcieWstrzymania_NowyMonitorZnaSerwerQt_PodgladBezPolaczenia()
    {
        var rig = new Rig(QtEmbeddedVncServer.DesktopName);
        await rig.RunFirstMonitorUntilHoldAsync();

        MachineMonitor second = rig.NewMonitor();
        Assert.True(QtEmbeddedVncServer.IsQtEmbedded(second.Snapshot.Info?.DesktopName),
            $"Migawka nowego monitora: Info.DesktopName = „{second.Snapshot.Info?.DesktopName ?? "null"}”.");
        Assert.Equal(MachineConnectionState.Error, second.Snapshot.State);
        Assert.Contains("Kolejna próba za 5 min", second.Snapshot.LastError);
        int opened = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Preview(second, () => opened++).CaptureAsync("amm520-1", CancellationToken.None));

        Assert.Equal(MachinePreviewService.QtPreviewNoConnection + " " + second.Snapshot.LastError, error.Message);
        Assert.DoesNotContain("jeszcze", error.Message);
        Assert.DoesNotContain("komputerze-obserwatorze", error.Message);
        Assert.Equal(0, opened);
        Assert.Equal(1, rig.Source.ConnectCount);
    }

    [Theory]
    [InlineData("68EDA4721759")]
    [InlineData("WinCE")]
    [InlineData("OPENCNC2")]
    public async Task PodgladNaprawa01_SerwerInnyNizQt_ZamknieciePrzedPierwszaKlatka_MigawkaBezOpisuJakDotad(string desktopName)
    {
        var rig = new Rig(desktopName);
        rig.Source.OnConnect = () =>
        {
            if (rig.Source.ConnectCount == 1) rig.Source.CaptureFailure = () => { rig.Source.CaptureFailure = null; return new RfbException(ClosedByServer); };
        };
        using var cts = new CancellationTokenSource();
        MachineMonitor first = rig.NewMonitor(stop: c => c >= 1, cts: cts);

        await first.RunAsync(cts.Token);

        Assert.Null(first.Snapshot.Info);
        MachineMonitor second = rig.NewMonitor();
        Assert.Null(second.Snapshot.Info);
        Assert.Equal(MachineConnectionState.Disconnected, second.Snapshot.State);
        Assert.Null(second.Snapshot.LastError);
    }

    [Fact]
    public async Task PodgladNaprawa01_MaszynaWylaczonaD3_MigawkaBezOpisuSerweraQt()
    {
        var rig = new Rig(QtEmbeddedVncServer.DesktopName);
        await rig.RunFirstMonitorUntilHoldAsync();

        MachineMonitor disabled = rig.NewMonitor(rig.Machine with { Enabled = false });

        Assert.Equal(MachineConnectionState.Disabled, disabled.Snapshot.State);
        Assert.Null(disabled.Snapshot.Info);
        Assert.Null(disabled.Snapshot.LastError);
        MachineMonitor enabledAgain = rig.NewMonitor();
        Assert.True(QtEmbeddedVncServer.IsQtEmbedded(enabledAgain.Snapshot.Info?.DesktopName));
        Assert.Equal(MachineConnectionState.Error, enabledAgain.Snapshot.State);
    }


    private static Func<AppConfig, MachineConfig, IScreenSource?> TestFileIn(string dir, Action? called = null) => (_, machine) =>
    {
        called?.Invoke();
        var file = new FolderScreenSource(machine.Id, dir);
        return file.FileExists ? file : null;
    };

    [Fact]
    public async Task PodgladNaprawa03_MaszynaQt_BezKlatkiMonitora_PlikTestEkran_ObrazZPlikuBezVnc()
    {
        using var dir = new TempDir("podglad-qt-plik");
        var image = new RgbaImage(64, 48);
        image.Fill(new Rgba(200, 30, 30));
        File.WriteAllBytes(dir.File("amm520-1.png"), PngCodec.Encode(image));
        var rig = new Rig(QtEmbeddedVncServer.DesktopName, profile: "aidite5x");
        int opened = 0;

        MachinePreviewFrame frame = await rig.Preview(rig.NewMonitor(), () => opened++, TestFileIn(dir.Path))
            .CaptureAsync("amm520-1", CancellationToken.None);

        Assert.Equal("plik", frame.Source); // okno: „TEST Z PLIKU — nie jest to ekran maszyny”
        Assert.Equal(PngCodec.Encode(image), PngCodec.Encode(frame.Image));
        Assert.Equal(0, opened);
        Assert.Equal(0, rig.Source.ConnectCount);
    }

    [Fact]
    public async Task PodgladNaprawa03_MaszynaQt_UszkodzonyPlikTestEkran_BladPliku_NigdyVnc()
    {
        using var dir = new TempDir("podglad-qt-plik-zly");
        File.WriteAllBytes(dir.File("amm520-1.png"), [1, 2, 3, 4]);
        var rig = new Rig(QtEmbeddedVncServer.DesktopName, profile: "aidite5x");
        int opened = 0;

        await Assert.ThrowsAsync<ScreenSourceException>(() => rig.Preview(rig.NewMonitor(), () => opened++, TestFileIn(dir.Path))
            .CaptureAsync("amm520-1", CancellationToken.None));

        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task PodgladNaprawa03_MaszynaQt_KlatkaMonitoraMaPierwszenstwoPrzedPlikiem_BezSprawdzaniaPliku()
    {
        // Obserwator z klatką monitora: zwraca ją, pliku nie sprawdza — monitor sam przechodzi na plik, gdy ten istnieje.
        using var dir = new TempDir("podglad-qt-plik-obs");
        File.WriteAllBytes(dir.File("amm520-1.png"), PngCodec.Encode(new RgbaImage(8, 8)));
        var rig = new Rig(QtEmbeddedVncServer.DesktopName, profile: "aidite5x");
        using var cts = new CancellationTokenSource();
        MachineMonitor monitor = rig.NewMonitor(stop: c => c >= 3, cts: cts);
        await monitor.RunAsync(cts.Token);
        MachinePreviewFrame? local = monitor.PreviewFrame;
        Assert.NotNull(local);
        int fileChecks = 0;

        MachinePreviewFrame frame = await rig.Preview(monitor, () => { }, TestFileIn(dir.Path, () => fileChecks++))
            .CaptureAsync("amm520-1", CancellationToken.None);

        Assert.Same(local, frame);
        Assert.Equal(0, fileChecks);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // komunikat zależny od tego, czy ten komputer obserwuje maszynę
    // ---------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, MachineConnectionState?, string?, string> Komunikaty() => new()
    {
        // opis przypadku, stan migawki obserwatora (null — ten komputer nie obserwuje), LastError, oczekiwany komunikat
        { "komputer bez obserwacji", null, null, MachinePreviewService.QtPreviewUnavailable },
        { "obserwator, bezpiecznik", MachineConnectionState.Error, "Błąd X. Kolejna próba za 5 min — powód.",
            MachinePreviewService.QtPreviewNoConnection + " Błąd X. Kolejna próba za 5 min — powód." },
        { "obserwator, błąd bez opisu", MachineConnectionState.Error, null, MachinePreviewService.QtPreviewNoConnection },
        { "obserwator, łączy się", MachineConnectionState.Disconnected, null, MachinePreviewService.QtPreviewConnecting },
        { "obserwator, połączony bez klatki", MachineConnectionState.Connected, null, MachinePreviewService.QtPreviewConnecting },
    };

    [Theory]
    [MemberData(nameof(Komunikaty))]
    public async Task PodgladNaprawa04_MaszynaQtBezKlatki_KomunikatWgRoliKomputera_ZeroPolaczen(
        string przypadek, MachineConnectionState? state, string? lastError, string expected)
    {
        var machine = new MachineConfig { Id = "amm520-1", Name = "520 (1)", Host = "127.0.0.1", Port = 5900, Profile = "aidite5x" };
        var config = new AppConfig { Enabled = true, Machines = [machine] };
        MachineStatusSnapshot? observed = state is { } s
            ? new MachineStatusSnapshot { MachineId = "amm520-1", Name = "520 (1)", Host = "127.0.0.1", Profile = "aidite5x", State = s, LastError = lastError }
            : null;
        int opened = 0;
        var service = new MachinePreviewService(() => config, _ => null, (_, _) =>
        {
            opened++;
            return new FakeScreenSource("amm520-1", PreviewQtMonitorRig.NormalFrame());
        }, new FakeClock(), _ => null, (_, _) => null, _ => observed);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync("amm520-1", CancellationToken.None));

        Assert.True(expected == error.Message, $"{przypadek}: „{error.Message}”");
        Assert.Equal(0, opened);
        if (state is not null)
        {
            Assert.DoesNotContain("komputerze-obserwatorze", error.Message);
        }
    }
}
