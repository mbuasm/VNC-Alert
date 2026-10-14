using System.Net.Sockets;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;
using Xunit.Abstractions;

namespace MaszynyAlert.Rfb.Tests;

public sealed class Qt48SimulationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string NonQtName = "QWS VNC (kontrola: klient bez trybu Qt)";

    public static TheoryData<string, string> RealVncVariants => new()
    {
        { "LowColour8", "RealVnc4" },
        { "FullColour32", "RealVnc4" },
        { "VeryLowColour8", "RealVncExtended" },
        { "FullColour32", "RealVncExtended" },
    };

    // =============================================================================================================
    // VNC Alert jako pierwszy klient po starcie HMI
    // =============================================================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_NowyKlientPierwszyPoStarcieHmi_FormatZServerInitPrzedPierwszymObrazem_BezAwarii(bool clientCursorCondition)
    {
        await using Qt48VncServerSimulator sim = Start(cursorAccelerated: clientCursorCondition);
        using var cts = new CancellationTokenSource(Wait);
        await using var client = new RfbClient(Options(sim.Port));

        RfbServerInfo info = await client.ConnectAsync(cts.Token);
        RgbaImage full = await client.CaptureAsync(FullRect(sim), incremental: false, cts.Token);
        await sim.SyncAsync();

        AssertNoFailures(sim);
        Assert.Equal(QtEmbeddedVncServer.DesktopName, info.DesktopName);
        Assert.Equal(Qt48VncServerSimulator.QtDesktopName, QtEmbeddedVncServer.DesktopName);
        ImageAssert.Equal(Expected(sim), full);
        Assert.Equal(0, CountColor(full, new Rgba(0, 255, 0)));

        List<QtSimEvent> mine = sim.Events.Where(e => e.Connection == 1).ToList();
        int setPixelFormat = Index(mine, QtSimEventKind.SetPixelFormat);
        int setEncodings = Index(mine, QtSimEventKind.SetEncodings);
        int firstRequest = Index(mine, QtSimEventKind.FramebufferUpdateRequest);
        int firstImage = Index(mine, QtSimEventKind.WyslanoObraz);
        Assert.True(setPixelFormat >= 0 && setPixelFormat < setEncodings && setEncodings < firstRequest && firstRequest < firstImage, Dump(sim));
        Assert.Contains("16bpp depth 16 true-colour LE bity 5/6/5 przesunięcia 11/5/0", mine[setPixelFormat].Detail);
        Assert.Contains("[5, 1, 0, -239]", mine[setEncodings].Detail);
        var counts = System.Text.RegularExpressions.Regex.Match(mine[firstImage].Detail, @"1-kolor (\d+), 2-kolor (\d+), wielokolor (\d+), raw (\d+)");
        Assert.True(counts.Success, mine[firstImage].Detail);
        Assert.All(Enumerable.Range(1, 4), g => Assert.True(int.Parse(counts.Groups[g].Value) > 0, mine[firstImage].Detail));
        if (clientCursorCondition)
        {
            int cursorCreated = Index(mine, QtSimEventKind.KursorKlientaUtworzony);
            int cursorSent = Index(mine, QtSimEventKind.WyslanoKursor);
            Assert.True(cursorCreated >= 0 && cursorCreated < firstRequest && firstRequest < cursorSent && cursorSent < firstImage, Dump(sim));
        }
        else
        {
            Assert.DoesNotContain(mine, e => e.Kind is QtSimEventKind.KursorKlientaUtworzony or QtSimEventKind.WyslanoKursor);
        }

        // Dalsza praca: HMI zmienia ekran i kursor, klient czyta przyrostowo — nadal bez ścieżki awarii, kursor nigdy na klatce.
        for (int i = 0; i < 5; i++)
        {
            await sim.PaintAsync(100 + (i * 16), 300, 16, 16, Qt48VncServerSimulator.Rgb565(255, 0, 0));
            await sim.ChangeCursorShapeAsync();
            RgbaImage? roi = await client.TryCaptureAsync(new RectInt(0, 0, 400, 400), incremental: true, TimeSpan.FromSeconds(2), cts.Token);
            if (roi is not null) Assert.Equal(0, CountColor(roi, new Rgba(0, 255, 0)));
        }

        await sim.SyncAsync();
        AssertNoFailures(sim);
        output.WriteLine(sim.Dump(40));
    }

    [Fact]
    public async Task A_Kontrola_KlientJakPrzed156_PierwszyPoStarcieHmi_FormatNiezainicjowany_Awaria()
    {
        await using Qt48VncServerSimulator sim = Start(name: NonQtName);
        using var cts = new CancellationTokenSource(Wait);
        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);

        await Assert.ThrowsAnyAsync<RfbException>(() => client.CaptureAsync(FullRect(sim), incremental: false, cts.Token));
        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.AwariaProcesu, Wait);

        Assert.Contains(sim.Events, e => e.Kind == QtSimEventKind.FormatNiezainicjowany && e.Connection == 1);
        Assert.DoesNotContain(sim.Events, e => e.Kind == QtSimEventKind.SetPixelFormat);
        await AssertConnectionRefusedAsync(sim.Port);
        output.WriteLine(sim.Dump(30));
    }

    [Fact]
    public async Task A_Kontrola_KlientJakPrzed156_PamiecWyzerowana_SerwerZyjeAleWysylaSmieci()
    {
        await using Qt48VncServerSimulator sim = Start(name: NonQtName, onUninitialized: UninitializedFormatBehavior.ZeroedMemory);
        using var cts = new CancellationTokenSource(Wait);
        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);

        RfbException error = await Assert.ThrowsAnyAsync<RfbException>(() => client.CaptureAsync(FullRect(sim), incremental: false, cts.Token));

        await sim.SyncAsync();
        Assert.Contains(sim.Events, e => e.Kind == QtSimEventKind.FormatNiezainicjowany);
        Assert.DoesNotContain(sim.Events, e => e.Kind == QtSimEventKind.AwariaProcesu);
        output.WriteLine($"klient: {error.GetType().Name}: {error.Message}");
    }


    [Theory]
    [MemberData(nameof(RealVncVariants))]
    public async Task B_RealVncZostawiaBrudnyKursorPoRozlaczeniu_PotemNowyKlient_BezAwarii(string format, string encodings)
    {
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await PoisonByRealVncAsync(sim, format, encodings, disconnect: true, cts.Token);

        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);
        RgbaImage full = await client.CaptureAsync(FullRect(sim), incremental: false, cts.Token);
        await sim.SyncAsync();

        AssertNoFailures(sim);
        ImageAssert.Equal(Expected(sim), full);
        AssertCursorCreatedBeforeFirstRequest(sim, connection: 2);
        QtSimState state = await sim.InspectAsync();
        Assert.False(state.Poisoned);
        Assert.Equal(1, state.PoisonedStates);
    }

    [Theory]
    [MemberData(nameof(RealVncVariants))]
    public async Task B_Kontrola_KlientBezKursora_PoRozlaczeniuRealVnc_AwariaKursorNull(string format, string encodings)
    {
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await PoisonByRealVncAsync(sim, format, encodings, disconnect: true, cts.Token);

        // Bajty przeglądarki bez −239: SetEncodings [5,1,0] + FBUR, bez SetPixelFormat.
        await using ScriptedVncViewer legacy = await ScriptedVncViewer.ConnectAsync(sim.Port, cts.Token);
        await legacy.SetEncodingsAsync(ScriptedVncViewer.OldVncAlertEncodings, cts.Token);
        await legacy.RequestAsync(incremental: false, cts.Token);

        Assert.True(await legacy.ClosedByServerWithinAsync(Wait), "Serwer powinien zamknąć połączenie (proces HMI padł).");
        QtSimEvent crash = await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.AwariaKursorNull, Wait);
        Assert.Equal(2, crash.Connection);
        Assert.DoesNotContain(sim.Events, e => e.Kind == QtSimEventKind.WyslanoObraz && e.Connection == 2);
        await AssertConnectionRefusedAsync(sim.Port);
        output.WriteLine(sim.Dump(40));
    }

    [Theory]
    [InlineData("LowColour8")]
    [InlineData("VeryLowColour8")]
    public async Task B_Kontrola_RealVnc8bppBezBrudnegoKursora_KlientJakPrzed156_Kodowanie1282(string format)
    {
        await using Qt48VncServerSimulator sim = Start(name: NonQtName);
        using var cts = new CancellationTokenSource(Wait);
        await using (ScriptedVncViewer viewer = await RealVncToImageAsync(sim, format, "RealVnc4", cts.Token))
        {
        }

        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.DiscardClient && e.Connection == 1, Wait);
        Assert.False((await sim.InspectAsync()).Poisoned);

        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);
        RfbProtocolException error = await Assert.ThrowsAsync<RfbProtocolException>(() => client.CaptureAsync(FullRect(sim), incremental: false, cts.Token));

        Assert.Contains("1282", error.Message);
        await sim.SyncAsync();
        AssertNoFailures(sim);
    }

    [Theory]
    [InlineData("LowColour8")]
    [InlineData("FullColour32")]
    public async Task B_RealVncFormatPozostaje_NowyKlientUstawiaSwoj_Bez1282(string format)
    {
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await using (ScriptedVncViewer viewer = await RealVncToImageAsync(sim, format, "RealVnc4", cts.Token))
        {
        }

        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.DiscardClient && e.Connection == 1, Wait);
        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);
        RgbaImage full = await client.CaptureAsync(FullRect(sim), incremental: false, cts.Token);

        ImageAssert.Equal(Expected(sim), full);
        await sim.SyncAsync();
        AssertNoFailures(sim);
    }


    [Theory]
    [MemberData(nameof(RealVncVariants))]
    public async Task C_NowyKlientPrzejmujeSesjeRealVncZBrudnymKursorem_BezAwarii(string format, string encodings)
    {
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await using ScriptedVncViewer realVnc = await PoisonByRealVncAsync(sim, format, encodings, disconnect: false, cts.Token);

        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);
        RgbaImage full = await client.CaptureAsync(FullRect(sim), incremental: false, cts.Token);

        Assert.True(await realVnc.ClosedByServerWithinAsync(Wait), "Qt obsługuje jednego klienta — RealVNC musi zostać wyrzucony.");
        await sim.SyncAsync();
        AssertNoFailures(sim);
        ImageAssert.Equal(Expected(sim), full);
        AssertCursorCreatedBeforeFirstRequest(sim, connection: 2);
        QtSimState state = await sim.InspectAsync();
        Assert.Equal(1, state.Takeovers);
        Assert.Equal(1, state.PoisonedStates); // stan groźny był — nasz kursor (−239) go zneutralizował przed pierwszym FBUR
        Assert.False(state.Poisoned);
    }

    [Theory]
    [MemberData(nameof(RealVncVariants))]
    public async Task C_Kontrola_KlientBezKursoraPrzejmujeSesjeRealVncZBrudnymKursorem_AwariaKursorNull(string format, string encodings)
    {
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await using ScriptedVncViewer realVnc = await PoisonByRealVncAsync(sim, format, encodings, disconnect: false, cts.Token);

        await using ScriptedVncViewer legacy = await ScriptedVncViewer.ConnectAsync(sim.Port, cts.Token);
        await legacy.SetEncodingsAsync(ScriptedVncViewer.OldVncAlertEncodings, cts.Token);
        await legacy.RequestAsync(incremental: false, cts.Token);

        Assert.True(await legacy.ClosedByServerWithinAsync(Wait));
        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.AwariaKursorNull && e.Connection == 2, Wait);
        await AssertConnectionRefusedAsync(sim.Port);
    }

    [Fact]
    public async Task C_RealVncZOczekujacymZadaniem_KursorWyslanyPrzezTimer_StanNieZatruty()
    {
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await using ScriptedVncViewer realVnc = await RealVncToImageAsync(sim, "FullColour32", "RealVnc4", cts.Token);
        long seqBefore = sim.Events.Count == 0 ? 0 : sim.Events[^1].Seq;
        await realVnc.RequestAsync(incremental: true, cts.Token);
        await sim.WaitForEventAsync(e => e.Seq > seqBefore && e.Kind == QtSimEventKind.FramebufferUpdateRequest && e.Connection == 1, Wait);
        await sim.SyncAsync();
        Assert.True((await sim.InspectAsync()).WantUpdate);

        await sim.ChangeCursorShapeAsync();
        IReadOnlyList<ViewerRect> cursorOnly = await realVnc.ReadUpdateAsync(cts.Token);

        Assert.Equal(-239, Assert.Single(cursorOnly).Encoding);
        QtSimState state = await sim.InspectAsync();
        Assert.False(state.DirtyCursor);
        Assert.False(state.WantUpdate);
        AssertNoFailures(sim);
    }

    // =============================================================================================================
    // dwóch obserwatorów grupy (dwa MachineMonitor) przejmujących się nawzajem — czas wirtualny
    // =============================================================================================================

    [Theory]
    [InlineData(1777)]
    [InlineData(6007)]
    public async Task D_DwochObserwatorowGrupyPrzejmujeSieNawzajem_KilkaCykliBezpiecznika_BezAwarii(int cursorChangeEveryMs)
    {
        await using Qt48VncServerSimulator sim = Start(repaintBeforeEachRequest: true);
        var clock = new VirtualClock();
        var logA = new ListLogger(clock, "A");
        var logB = new ListLogger(clock, "B");
        MachineMonitor a = NewMonitor(clock, sim.Port, logA);
        MachineMonitor b = NewMonitor(clock, sim.Port, logB);

        int steps = await RunGroupAsync(sim, clock, a, b, cursorChangeEveryMs, TimeSpan.FromMinutes(70), stopOnCrash: false);

        QtSimState state = await sim.InspectAsync();
        output.WriteLine($"czas wirtualny {clock.MonotonicMs / 60000.0:0.0} min, kroków {steps}, połączeń {state.Connections}, przejęć {state.Takeovers}, " +
            $"stanów zatrutych {state.PoisonedStates}, kursorów {state.CursorWrites}, obrazów {state.ImageWrites}");
        foreach (var (at, _, message) in logA.Entries.Select(e => (e.AtMs, e.Level, "A: " + e.Message))
                     .Concat(logB.Entries.Select(e => (e.AtMs, e.Level, "B: " + e.Message)))
                     .Where(e => e.Item3.Contains("przejął")).OrderBy(e => e.AtMs))
            output.WriteLine($"{at / 60000.0,6:0.00} min  {message}");

        AssertNoFailures(sim);
        Assert.True(state.Takeovers >= 6, $"Przejęć: {state.Takeovers}.");
        Assert.True(state.PoisonedStates >= 3,
            $"Stan dirtyCursor przy usuniętym kursorze wystąpił {state.PoisonedStates} raz(y) — scenariusz miał go sprawdzić wielokrotnie.");
        double[] waits = logA.Entries.Concat(logB.Entries)
            .Where(e => e.Message.Contains("inny klient VNC przejął"))
            .Select(e => WaitMinutes(e.Message)).ToArray();
        Assert.All(waits, w => Assert.Contains(w, new double[] { 1, 5, 15 }));
        Assert.Contains(5.0, waits);
        Assert.Contains(15.0, waits);
        Assert.DoesNotContain(logA.Entries.Concat(logB.Entries), e => e.Message.Contains("przed pierwszą klatką"));
        Assert.DoesNotContain(logA.Entries.Concat(logB.Entries), e => e.Message.Contains("niezgodne z protokołem"));
    }

    [Fact]
    public async Task D_Charakterystyka_GrupaZMieszanymiWersjami_KlientBezKursoraPoNowym_SymulatorWykrywaAwarie()
    {
        await using Qt48VncServerSimulator sim = Start(repaintBeforeEachRequest: true);
        await using var proxy = new DesktopNameRewritingProxy(sim.Port, NonQtName);
        var clock = new VirtualClock();
        var logNew = new ListLogger(clock, "nowy");
        var logOld = new ListLogger(clock, "stary");
        MachineMonitor fresh = NewMonitor(clock, sim.Port, logNew);
        MachineMonitor legacy = NewMonitor(clock, proxy.Port, logOld);

        await RunGroupAsync(sim, clock, fresh, legacy, cursorChangeEveryMs: 1777, TimeSpan.FromMinutes(60), stopOnCrash: true);

        QtSimEvent crash = Assert.Single(sim.Events, e => e.Kind == QtSimEventKind.AwariaKursorNull);
        QtSimEvent crashingEncodings = sim.Events.Last(e => e.Kind == QtSimEventKind.SetEncodings && e.Connection == crash.Connection);
        Assert.StartsWith("[5, 1, 0] →", crashingEncodings.Detail); // wywraca się klient bez −239
        QtSimEvent poisoner = sim.Events.Last(e => e.Kind == QtSimEventKind.DiscardClient && e.Seq < crash.Seq);
        Assert.Contains("usunięto kursor klienta: tak; dirtyCursor = True", poisoner.Detail); // zatruł nasz (nowy) klient z −239
        Assert.Contains("-239", sim.Events.Last(e => e.Kind == QtSimEventKind.SetEncodings && e.Connection == poisoner.Connection).Detail);
        output.WriteLine($"awaria po {clock.MonotonicMs / 1000.0:0.0} s czasu wirtualnego; przejęć {(await sim.InspectAsync()).Takeovers}");
        output.WriteLine(sim.Dump(30));
    }

    // =============================================================================================================
    // Podgląd z Panelu podczas monitoringu
    // =============================================================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task E_PodgladZPaneluPodczasMonitoringu_StaraKlatka_BezDrugiegoPolaczenia(bool desktopNameKnown)
    {
        await using Qt48VncServerSimulator sim = Start();
        await using var monitor = await RealTimeMonitorAsync(sim.Port, "aidite5x");
        int opened = 0;
        var config = new AppConfig { Machines = [monitor.Machine], Timing = monitor.Timing };
        var preview = new MachinePreviewService(() => config, id => monitor.Monitor.PreviewFrame,
            (cfg, m) => { Interlocked.Increment(ref opened); return new VncScreenSource(m, null, cfg.Timing); },
            new OffsetClock(TimeSpan.FromMinutes(20)),
            desktopNameKnown ? id => monitor.Monitor.Snapshot.Info?.DesktopName : _ => null);

        MachinePreviewFrame frame = await preview.CaptureAsync(monitor.Machine.Id, CancellationToken.None);
        await Task.Delay(300);
        await sim.SyncAsync();

        Assert.Equal(0, opened);
        Assert.Equal("vnc", frame.Source);
        QtSimState state = await sim.InspectAsync();
        Assert.Equal(1, state.Connections);
        Assert.Equal(0, state.Takeovers);
        AssertNoFailures(sim);
    }

    [Fact]
    public async Task E_PodgladBezKlatkiMonitoringu_Komunikat_ZeroPolaczen()
    {
        await using Qt48VncServerSimulator sim = Start();
        int opened = 0;
        var machine = Machine(sim.Port, "aidite5x");
        var config = new AppConfig { Machines = [machine] };
        var preview = new MachinePreviewService(() => config, _ => null,
            (cfg, m) => { Interlocked.Increment(ref opened); return new VncScreenSource(m, null, cfg.Timing); },
            new SystemClock(), _ => null);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => preview.CaptureAsync(machine.Id, CancellationToken.None));

        Assert.Equal(MachinePreviewService.QtPreviewUnavailable, error.Message);
        Assert.Equal(0, opened);
        Assert.Equal(0, sim.ConnectionCount);
    }

    [Fact]
    public async Task E_Kontrola_PodgladMaszynyNieQt_OtwieraDrugiePolaczenie_SymulatorWidziPrzejecie()
    {
        await using Qt48VncServerSimulator sim = Start(name: NonQtName);
        await InitializeServerFormatAsync(sim);
        await using var monitor = await RealTimeMonitorAsync(sim.Port, "opencnc");
        int opened = 0;
        var config = new AppConfig { Machines = [monitor.Machine], Timing = monitor.Timing };
        var preview = new MachinePreviewService(() => config, id => monitor.Monitor.PreviewFrame,
            (cfg, m) => { Interlocked.Increment(ref opened); return new VncScreenSource(m, null, cfg.Timing); },
            new OffsetClock(TimeSpan.FromMinutes(20)), id => monitor.Monitor.Snapshot.Info?.DesktopName);

        await preview.CaptureAsync(monitor.Machine.Id, CancellationToken.None);
        await sim.SyncAsync();

        Assert.Equal(1, opened);
        Assert.True((await sim.InspectAsync()).Takeovers >= 1);
    }


    [Theory]
    [MemberData(nameof(RealVncVariants))]
    public async Task F_WarunekKursoraFalszywy_RealVncINowyKlient_NiktNieMaKursora_BezAwarii(string format, string encodings)
    {
        await using Qt48VncServerSimulator sim = Start(cursorAccelerated: false);
        using var cts = new CancellationTokenSource(Wait);
        await using (ScriptedVncViewer realVnc = await RealVncToImageAsync(sim, format, encodings, cts.Token))
        {
            await sim.ChangeCursorShapeAsync();
            Assert.False((await sim.InspectAsync()).DirtyCursor);
        }

        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.DiscardClient && e.Connection == 1, Wait);
        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);
        RgbaImage full = await client.CaptureAsync(FullRect(sim), incremental: false, cts.Token);

        await sim.SyncAsync();
        AssertNoFailures(sim);
        ImageAssert.Equal(Expected(sim), full);
        Assert.DoesNotContain(sim.Events, e => e.Kind is QtSimEventKind.KursorKlientaUtworzony or QtSimEventKind.WyslanoKursor);
        Assert.Equal(0, (await sim.InspectAsync()).PoisonedStates);
    }

    [Theory]
    [InlineData("FullColour32")]
    [InlineData("LowColour8")]
    public async Task F_Kontrola_WarunekKursoraFalszywy_KlientBezKursoraPoRealVnc_BezAwariiKursora(string format)
    {
        await using Qt48VncServerSimulator sim = Start(cursorAccelerated: false);
        using var cts = new CancellationTokenSource(Wait);
        await using (ScriptedVncViewer realVnc = await RealVncToImageAsync(sim, format, "RealVnc4", cts.Token))
        {
            await sim.ChangeCursorShapeAsync();
        }

        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.DiscardClient && e.Connection == 1, Wait);
        await using ScriptedVncViewer legacy = await ScriptedVncViewer.ConnectAsync(sim.Port, cts.Token);
        await legacy.SetEncodingsAsync(ScriptedVncViewer.OldVncAlertEncodings, cts.Token);
        await legacy.RequestAsync(incremental: false, cts.Token);

        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.WyslanoObraz && e.Connection == 2, Wait);
        await sim.SyncAsync();
        AssertNoFailures(sim);
    }

    // =============================================================================================================
    // Uzupełnienia: tryb polling, pierwszy klient po restarcie HMI
    // =============================================================================================================

    [Fact]
    public async Task Uzupelnienie_TrybPolling_KazdyOdczytNowymPolaczeniem_KursorPrzedZadaniem_BezAwarii()
    {
        await using Qt48VncServerSimulator sim = Start();
        MachineConfig machine = Machine(sim.Port, "aidite5x") with { Mode = ConnectionMode.Polling };
        await using var source = new VncScreenSource(machine, null, SimTiming());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 5; i++)
        {
            RgbaImage roi = await source.CaptureAsync(new RectInt(0, 0, 400, 400), incremental: true, cts.Token);
            Assert.Equal(0, CountColor(roi, new Rgba(0, 255, 0)));
            await sim.ChangeCursorShapeAsync();
            await sim.PaintAsync(32 * i, 340, 16, 16, Qt48VncServerSimulator.Rgb565(255, 0, 0));
        }

        await sim.SyncAsync();
        AssertNoFailures(sim);
        Assert.Equal(5, sim.ConnectionCount);
        for (int connection = 1; connection <= 5; connection++) AssertCursorCreatedBeforeFirstRequest(sim, connection);
    }

    [Fact]
    public async Task Uzupelnienie_PoAwariiIRestarcieHmi_NaszKlientPierwszy_BezAwarii()
    {
        // Ktoś inny (klient bez −239) kładzie HMI; po restarcie HMI (nowy proces, format znów niezainicjowany) pierwszy łączy się nasz klient.
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await PoisonByRealVncAsync(sim, "LowColour8", "RealVnc4", disconnect: true, cts.Token);
        await using (ScriptedVncViewer legacy = await ScriptedVncViewer.ConnectAsync(sim.Port, cts.Token))
        {
            await legacy.SetEncodingsAsync(ScriptedVncViewer.OldVncAlertEncodings, cts.Token);
            await legacy.RequestAsync(incremental: false, cts.Token);
            await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.AwariaProcesu, Wait);
        }

        await sim.RestartHmiAsync();
        await using var client = new RfbClient(Options(sim.Port));
        await client.ConnectAsync(cts.Token);
        RgbaImage full = await client.CaptureAsync(FullRect(sim), incremental: false, cts.Token);
        await sim.SyncAsync();

        ImageAssert.Equal(Expected(sim), full);
        long restart = sim.Events.Single(e => e.Kind == QtSimEventKind.RestartHmi).Seq;
        Assert.DoesNotContain(sim.Events, e => e.Seq > restart && (e.IsFailure || e.Kind == QtSimEventKind.BladSymulatora));
        Assert.Contains(sim.Events, e => e.Seq > restart && e.Kind == QtSimEventKind.SetPixelFormat);
    }


    [Fact]
    public async Task Charakterystyka_NowyKlientZostawiaBrudnyKursor_PrzegladarkaBezKursoraPoNim_AwariaKursorNull()
    {
        await using Qt48VncServerSimulator sim = Start();
        using var cts = new CancellationTokenSource(Wait);
        await using (var client = new RfbClient(Options(sim.Port)))
        {
            await client.ConnectAsync(cts.Token);
            await client.CaptureAsync(FullRect(sim), incremental: false, cts.Token);
            await sim.SyncAsync();
            Assert.False((await sim.InspectAsync()).WantUpdate); // po odpowiedzi nie trzymamy oczekującego żądania
            await sim.ChangeCursorShapeAsync();
        }

        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.DiscardClient && e.Connection == 1, Wait);
        Assert.True((await sim.InspectAsync()).Poisoned);

        await using ScriptedVncViewer viewer = await ScriptedVncViewer.ConnectAsync(sim.Port, cts.Token);
        await viewer.SetPixelFormatAsync(ScriptedVncViewer.FullColour32, cts.Token);
        await viewer.SetEncodingsAsync(ScriptedVncViewer.NoCursorEncodings, cts.Token);
        await viewer.RequestAsync(incremental: false, cts.Token);

        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.AwariaKursorNull && e.Connection == 2, Wait);
        Assert.True(await viewer.ClosedByServerWithinAsync(Wait));
    }

    [Fact]
    public async Task Charakterystyka_SerwerQtPaletowy8bpp_NowyKlientNieWysylaFormatu_FormatNiezainicjowany()
    {
        await using Qt48VncServerSimulator sim = Start(screenDepth: 8, hasSubscreen: false);
        using var cts = new CancellationTokenSource(Wait);
        await using var client = new RfbClient(Options(sim.Port));
        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        Assert.False(info.PixelFormat.TrueColour);
        await Assert.ThrowsAnyAsync<RfbException>(() => client.CaptureAsync(FullRect(sim), incremental: false, cts.Token));
        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.FormatNiezainicjowany, Wait);
        Assert.DoesNotContain(sim.Events, e => e.Kind == QtSimEventKind.SetPixelFormat);
    }


    [Fact]
    public async Task Narzedzie_SymulatorDlaPrzegladarkiZewnetrznej_TylkoGdyUstawionoZmienna()
    {
        string? portText = Environment.GetEnvironmentVariable("WER_QT_SIM_PORT");
        if (string.IsNullOrWhiteSpace(portText)) return;
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("WER_QT_SIM_MINUTES"), out int m) ? m : 3;
        await using var sim = new Qt48VncServerSimulator(int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture))
        {
            RepaintBeforeEachRequest = false,
        };
        sim.Start();
        await Task.Delay(TimeSpan.FromMinutes(minutes));
        string log = sim.Dump(5000);
        output.WriteLine(log);
        string? path = Environment.GetEnvironmentVariable("WER_QT_SIM_LOG");
        if (!string.IsNullOrWhiteSpace(path)) await File.WriteAllTextAsync(path, log);
    }


    [Fact]
    public async Task Wiernosc_ProtocolIInit_JedenKrokNaReadyRead_KlientWysylajacyWszystkoNaRazNieDostajeServerInit()
    {
        // Stany Protocol/Init nie mają pętli: wersja + ClientInit w jednym pakiecie → serwer odpowiada typem zabezpieczeń, a
        // ClientInit czeka na następny readyRead, który nie przyjdzie. Nasz klient czeka na każdą odpowiedź (testy wyżej działają).
        await using Qt48VncServerSimulator sim = Start();
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync("127.0.0.1", sim.Port);
        NetworkStream stream = tcp.GetStream();
        byte[] version = new byte[12];
        await stream.ReadExactlyAsync(version);
        await stream.WriteAsync("RFB 003.003\n\u0001"u8.ToArray());
        byte[] security = new byte[4];
        await stream.ReadExactlyAsync(security);
        await Task.Delay(500);

        Assert.Equal(0, tcp.Client.Available);
        Assert.Equal("Init", (await sim.InspectAsync()).State);
        Assert.DoesNotContain(sim.Events, e => e.Kind == QtSimEventKind.ServerInit);
    }

    [Fact]
    public async Task Wiernosc_Connected_WieleWiadomosciWJednymReadyRead_PrzetwarzaneWKolejnosci()
    {
        await using Qt48VncServerSimulator sim = Start(cursorAccelerated: false);
        using var cts = new CancellationTokenSource(Wait);
        await using ScriptedVncViewer viewer = await ScriptedVncViewer.ConnectAsync(sim.Port, cts.Token);
        byte[] batch =
        [
            0, 0, 0, 0, .. viewer.ServerFormat,
            2, 0, 0, 1, 0, 0, 0, 5,
            3, 0, 0, 0, 0, 0, 4, 0, 3, 0,
        ];
        await viewer.SendRawAsync(batch, cts.Token);
        IReadOnlyList<ViewerRect> update = await viewer.ReadUpdateAsync(cts.Token);

        Assert.NotEmpty(update);
        List<QtSimEventKind> kinds = sim.Events.Where(e => e.Connection == 1).Select(e => e.Kind)
            .Where(k => k is QtSimEventKind.SetPixelFormat or QtSimEventKind.SetEncodings or QtSimEventKind.FramebufferUpdateRequest or QtSimEventKind.WyslanoObraz)
            .ToList();
        Assert.Equal([QtSimEventKind.SetPixelFormat, QtSimEventKind.SetEncodings, QtSimEventKind.FramebufferUpdateRequest, QtSimEventKind.WyslanoObraz], kinds);
        AssertNoFailures(sim);
    }

    // =============================================================================================================
    // Pomocnicze
    // =============================================================================================================

    private static Qt48VncServerSimulator Start(
        string name = Qt48VncServerSimulator.QtDesktopName,
        bool cursorAccelerated = true,
        bool hasSubscreen = true,
        int screenDepth = 16,
        bool repaintBeforeEachRequest = false,
        UninitializedFormatBehavior onUninitialized = UninitializedFormatBehavior.Crash)
    {
        var sim = new Qt48VncServerSimulator
        {
            DesktopName = name,
            CursorAccelerated = cursorAccelerated,
            HasSubscreen = hasSubscreen,
            ScreenDepth = screenDepth,
            RepaintBeforeEachRequest = repaintBeforeEachRequest,
            OnUninitializedFormat = onUninitialized,
        };
        sim.Start();
        return sim;
    }

    private static RfbClientOptions Options(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReadTimeout = TimeSpan.FromSeconds(5),
    };

    private static RectInt FullRect(Qt48VncServerSimulator sim) => new(0, 0, sim.Width, sim.Height);

    private static byte[] Format(string name) => name switch
    {
        "LowColour8" => ScriptedVncViewer.LowColour8,
        "VeryLowColour8" => ScriptedVncViewer.VeryLowColour8,
        "FullColour32" => ScriptedVncViewer.FullColour32,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static int[] EncodingList(string name) => name switch
    {
        "RealVnc4" => ScriptedVncViewer.RealVnc4Encodings,
        "RealVncExtended" => ScriptedVncViewer.RealVncExtendedEncodings,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static async Task<ScriptedVncViewer> RealVncToImageAsync(Qt48VncServerSimulator sim, string format, string encodings, CancellationToken ct)
    {
        ScriptedVncViewer viewer = await ScriptedVncViewer.ConnectAsync(sim.Port, ct);
        await viewer.SetPixelFormatAsync(Format(format), ct);
        await viewer.SetEncodingsAsync(EncodingList(encodings), ct);
        await viewer.RequestAsync(incremental: false, ct);
        for (int i = 0; i < 5; i++)
        {
            IReadOnlyList<ViewerRect> update = await viewer.ReadUpdateAsync(ct);
            if (update.Any(r => r.Encoding is 5 or 0)) return viewer;
            await viewer.RequestAsync(incremental: true, ct);
        }

        throw new InvalidOperationException("RealVNC nie dostał obrazu.");
    }

    private static async Task<ScriptedVncViewer> PoisonByRealVncAsync(
        Qt48VncServerSimulator sim, string format, string encodings, bool disconnect, CancellationToken ct)
    {
        ScriptedVncViewer viewer = await RealVncToImageAsync(sim, format, encodings, ct);
        await sim.SyncAsync();
        QtSimState before = await sim.InspectAsync();
        Assert.True(before.HasClientCursor, "RealVNC z −239 powinien mieć kursor klienta.");
        Assert.False(before.WantUpdate);

        await sim.ChangeCursorShapeAsync();
        QtSimState dirty = await sim.InspectAsync();
        Assert.True(dirty.DirtyCursor);
        Assert.False(dirty.WantUpdate);
        if (!disconnect) return viewer;

        await viewer.DisposeAsync();
        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.DiscardClient && e.Connection == 1, Wait);
        QtSimState poisoned = await sim.InspectAsync();
        Assert.True(poisoned.Poisoned, "Po rozłączeniu RealVNC: dirtyCursor przy qvnc_cursor == 0 (S1:2031–2043 nie zeruje dirtyCursor).");
        return viewer;
    }

    /// <summary>Przeglądarka ustawia format z <c>ServerInit</c> (RGB565), bez kursora, odbiera obraz i się rozłącza.</summary>
    private static async Task InitializeServerFormatAsync(Qt48VncServerSimulator sim)
    {
        using var cts = new CancellationTokenSource(Wait);
        await using (ScriptedVncViewer viewer = await ScriptedVncViewer.ConnectAsync(sim.Port, cts.Token))
        {
            await viewer.SetPixelFormatAsync(viewer.ServerFormat, cts.Token);
            await viewer.SetEncodingsAsync(ScriptedVncViewer.OldVncAlertEncodings, cts.Token);
            await viewer.RequestAsync(incremental: false, cts.Token);
            await viewer.ReadUpdateAsync(cts.Token);
        }

        int id = sim.ConnectionCount;
        await sim.WaitForEventAsync(e => e.Kind == QtSimEventKind.DiscardClient && e.Connection == id, Wait);
    }

    private static void AssertCursorCreatedBeforeFirstRequest(Qt48VncServerSimulator sim, int connection)
    {
        List<QtSimEvent> events = sim.Events.Where(e => e.Connection == connection).ToList();
        int created = Index(events, QtSimEventKind.KursorKlientaUtworzony);
        int request = Index(events, QtSimEventKind.FramebufferUpdateRequest);
        int cursor = Index(events, QtSimEventKind.WyslanoKursor);
        int image = Index(events, QtSimEventKind.WyslanoObraz);
        Assert.True(created >= 0 && created < request && request < cursor && cursor < image, Dump(sim));
    }

    private static int Index(List<QtSimEvent> events, QtSimEventKind kind) => events.FindIndex(e => e.Kind == kind);

    private static void AssertNoFailures(Qt48VncServerSimulator sim)
    {
        List<QtSimEvent> bad = sim.Events.Where(e => e.IsFailure || e.Kind == QtSimEventKind.BladSymulatora).ToList();
        Assert.True(bad.Count == 0, "Symulator Qt zapisał ścieżkę awarii albo własny błąd:" + Environment.NewLine + Dump(sim));
    }

    private static string Dump(Qt48VncServerSimulator sim) => sim.Dump(80);

    private static async Task AssertConnectionRefusedAsync(int port)
    {
        using var tcp = new TcpClient();
        SocketException error = await Assert.ThrowsAsync<SocketException>(() => tcp.ConnectAsync("127.0.0.1", port));
        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
    }

    private static RgbaImage Expected(Qt48VncServerSimulator sim)
    {
        ushort[] screen = Qt48VncServerSimulator.DefaultScreen(sim.Width, sim.Height);
        var image = new RgbaImage(sim.Width, sim.Height);
        for (int y = 0; y < sim.Height; y++)
            for (int x = 0; x < sim.Width; x++)
                image.SetPixel(x, y, RfbPixelFormat.Rgb565LittleEndian.ToRgba(screen[(y * sim.Width) + x]));
        return image;
    }

    private static int CountColor(RgbaImage image, Rgba color)
    {
        int n = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y) == color) n++;
        return n;
    }

    private static double WaitMinutes(string message)
    {
        const string marker = "następna próba za ";
        int i = message.IndexOf(marker, StringComparison.Ordinal);
        string rest = message[(i + marker.Length)..];
        string number = new(rest.TakeWhile(char.IsDigit).ToArray());
        return double.Parse(number, System.Globalization.CultureInfo.InvariantCulture) / (rest[number.Length..].TrimStart().StartsWith("min", StringComparison.Ordinal) ? 1 : 60);
    }

    private static async Task Quiet(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Dwa monitory i HMI w czasie wirtualnym. Monitor B rusza po 3,437 s, żeby siatki cykli obu monitorów (co 5 s) nie wypadały
    /// w tej samej chwili wirtualnej — inaczej przejęcie zawsze zbiegałoby się z żądaniem ofiary (artefakt symulacji: w czasie
    /// rzeczywistym kolejność takich zdarzeń zależy od sieci). Zwraca liczbę kroków zegara.
    /// </summary>
    private static async Task<int> RunGroupAsync(
        Qt48VncServerSimulator sim, VirtualClock clock, MachineMonitor a, MachineMonitor b, int cursorChangeEveryMs, TimeSpan horizon, bool stopOnCrash)
    {
        using var cts = new CancellationTokenSource();
        Task runA = clock.Run(() => Quiet(a.RunAsync(cts.Token)));
        Task runB = clock.Run(async () =>
        {
            await Quiet(clock.SleepAsync(TimeSpan.FromMilliseconds(3437), cts.Token));
            if (!cts.IsCancellationRequested) await Quiet(b.RunAsync(cts.Token));
        });
        Task hmi = clock.Run(() => Quiet(HmiCursorActorAsync(sim, clock, cursorChangeEveryMs, cts.Token)));

        long end = (long)horizon.TotalMilliseconds;
        int steps = 0;
        while (await clock.StepAsync(end, TimeSpan.FromSeconds(60)))
        {
            steps++;
            if (stopOnCrash && sim.Events.Any(e => e.Kind == QtSimEventKind.AwariaProcesu)) break;
        }

        cts.Cancel();
        await Task.WhenAll(runA, runB, hmi).WaitAsync(TimeSpan.FromSeconds(30));
        await sim.SyncAsync();
        return steps;
    }

    private static async Task HmiCursorActorAsync(Qt48VncServerSimulator sim, VirtualClock clock, int everyMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await clock.SleepAsync(TimeSpan.FromMilliseconds(everyMs), ct);
            await sim.ChangeCursorShapeAsync();
        }
    }

    private static MachineConfig Machine(int port, string profile) => new()
    {
        Id = "amm520-1",
        Name = "520 (1)",
        Host = "127.0.0.1",
        Port = port,
        Profile = profile,
        Enabled = true,
        Mode = ConnectionMode.Persistent,
        RequiresPassword = false,
    };

    private static readonly ScreenProfile Profile = new() { Id = "aidite5x", StatusBarRoi = new RectInt(100, 200, 300, 40) };

    private static TimingConfig SimTiming() => new()
    {
        PollIntervalMs = 1000,
        IdleUpdateTimeoutMs = 200,
        ConnectTimeoutMs = 5000,
        ReadTimeoutMs = 5000,
        ReconnectBackoffMs = [2000, 5000, 15000, 30000],
    };

    private static MachineMonitor NewMonitor(VirtualClock clock, int port, ListLogger log)
    {
        MachineConfig machine = Machine(port, "aidite5x");
        // Cykl 5 s zamiast 1 s: każdy przyrostowy odczyt RfbClient czeka w czasie RZECZYWISTYM co najmniej jeden krok sondowania
        // (25 ms) — 70 min symulacji przy cyklu 1 s trwało ~2 min. Logika bezpieczników nie zależy od długości cyklu.
        TimingConfig timing = SimTiming() with { PollIntervalMs = 5000 };
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        return new MachineMonitor(machine, Profile, () => new VncScreenSource(machine, null, timing), null, new NullOcrEngine(),
            new MessageMapper(new RuleSet()), alerts, clock, timing, new NullCaptureStore(), log, (ts, ct) => clock.SleepAsync(ts, ct));
    }

    private static async Task<RealTimeMonitor> RealTimeMonitorAsync(int port, string profile)
    {
        MachineConfig machine = Machine(port, profile);
        TimingConfig timing = SimTiming() with { PollIntervalMs = 100 };
        var clock = new SystemClock();
        var alerts = new AlertStateMachine(clock, timing, new AckMemory(clock, TimeSpan.FromHours(2)));
        var monitor = new MachineMonitor(machine, Profile with { Id = profile }, () => new VncScreenSource(machine, null, timing), null,
            new NullOcrEngine(), new MessageMapper(new RuleSet()), alerts, clock, timing, new NullCaptureStore());
        var cts = new CancellationTokenSource();
        Task run = monitor.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow + Wait;
        while (monitor.PreviewFrame is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(monitor.PreviewFrame);
        return new RealTimeMonitor(machine, timing, monitor, cts, run);
    }

    private sealed record RealTimeMonitor(MachineConfig Machine, TimingConfig Timing, MachineMonitor Monitor, CancellationTokenSource Cts, Task Run)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Cts.Dispose();
        }
    }

    private sealed class OffsetClock(TimeSpan offset) : IClock
    {
        private readonly SystemClock _inner = new();

        public DateTimeOffset UtcNow => _inner.UtcNow + offset;

        public DateTimeOffset Now => _inner.Now + offset;

        public long MonotonicMs => _inner.MonotonicMs + (long)offset.TotalMilliseconds;
    }
}
