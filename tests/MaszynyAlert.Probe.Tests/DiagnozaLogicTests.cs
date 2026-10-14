using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;

namespace MaszynyAlert.Probe.Tests;

/// <summary>Czysta logika diagnozy bez sieci: magazyn klatek z limitem, epizody koloru, limit dysku, wnioski, kody wyjścia, tryby maszyn.</summary>
public sealed class DiagnozaLogicTests
{
    // --- FrameStore ----------------------------------------------------------------------------------------------

    [Fact]
    public void FrameStore_PoPrzekroczeniuLimitu_PrzerzedzaStarszeZostawiajacCoPiata()
    {
        string dir = DiagnozaTestConfig.TempDir("store");
        try
        {
            var store = new FrameStore(Path.Combine(dir, "klatki"), maxFrames: 25);
            RgbaImage img = DiagnozaTestConfig.Solid(4, 4, new Rgba(1, 2, 3));
            DateTimeOffset t = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
            for (int i = 0; i < 40; i++)
            {
                store.Save(img, t.AddSeconds(i));
            }

            Assert.Equal(40, store.SavedTotal);
            Assert.True(store.Files.Count <= 25, $"na dysku {store.Files.Count}");
            Assert.Equal(store.Files.Count, Directory.GetFiles(Path.Combine(dir, "klatki")).Length);
            // Pierwsze klatki (test „ekran gaśnie”) i ostatnie zostają w komplecie.
            Assert.Contains(store.Files, f => f.Contains("-0000", StringComparison.Ordinal));
            Assert.Contains(store.Files, f => f.Contains("-0039", StringComparison.Ordinal));
            Assert.Contains(store.Files, f => f.Contains("-0015", StringComparison.Ordinal)); // indeks podzielny przez 5 zostaje
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- EpisodeTracker --------------------------------------------------------------------------------------------

    [Fact]
    public void EpisodeTracker_StartKlatkiIKoniecZBboxIMaksymalnymUdzialem()
    {
        var tracker = new EpisodeTracker();
        DateTimeOffset t0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var none = new ColorRegion(0, null);

        IReadOnlyList<EpisodeEvent> e0 = tracker.Update(t0, new ColorRegion(0.01, new RectInt(10, 10, 5, 5)), none);
        Assert.Equal([EpisodeEvent.Start, EpisodeEvent.SaveFrame], e0.Select(e => e.Rodzaj));
        Assert.Equal(0, e0[1].OffsetSek);
        Assert.True(tracker.IsActive(EpisodeColor.Czerwien));
        Assert.False(tracker.IsActive(EpisodeColor.Zolc));

        IReadOnlyList<EpisodeEvent> e1 = tracker.Update(t0.AddSeconds(1), new ColorRegion(0.02, new RectInt(10, 10, 5, 5)), none);
        Assert.Empty(e1); // 2 s jeszcze nie minęły

        IReadOnlyList<EpisodeEvent> e2 = tracker.Update(t0.AddSeconds(2.5), new ColorRegion(0.03, new RectInt(12, 10, 8, 5)), none);
        Assert.Single(e2);
        Assert.Equal(2, e2[0].OffsetSek);

        IReadOnlyList<EpisodeEvent> e3 = tracker.Update(t0.AddSeconds(12), new ColorRegion(0.03, null), none);
        Assert.Equal([EpisodeEvent.SaveFrame], e3.Select(e => e.Rodzaj)); // 10 s
        Assert.Equal(10, e3[0].OffsetSek);

        // Kolor znika: koniec dopiero po EndAfter (10 s) bez koloru — mignięcie go nie dzieli.
        Assert.Empty(tracker.Update(t0.AddSeconds(15), none, none));
        IReadOnlyList<EpisodeEvent> end = tracker.Update(t0.AddSeconds(26), none, none);
        EpisodeEvent ev = Assert.Single(end);
        Assert.Equal(EpisodeEvent.End, ev.Rodzaj);
        Assert.NotNull(ev.Epizod);
        Assert.Equal(t0, ev.Epizod!.Poczatek);
        Assert.Equal(t0.AddSeconds(12), ev.Epizod.Koniec);
        Assert.Equal(0.03, ev.Epizod.MaksUdzial);
        Assert.Equal(new RectInt(10, 10, 10, 5), ev.Epizod.Bbox);
        Assert.Equal(4, ev.Epizod.Klatek);
        Assert.False(tracker.IsActive(EpisodeColor.Czerwien));
    }

    [Fact]
    public void EpisodeTracker_PonizejProgu_NieZaczynaEpizodu_ACloseAllZamykaTrwajace()
    {
        var tracker = new EpisodeTracker();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;

        Assert.Empty(tracker.Update(t0, new ColorRegion(0.0001, new RectInt(0, 0, 1, 1)), new ColorRegion(0, null)));
        tracker.Update(t0, new ColorRegion(0, null), new ColorRegion(0.01, new RectInt(1, 1, 2, 2)));
        Assert.True(tracker.IsActive(EpisodeColor.Zolc));

        IReadOnlyList<EpisodeEvent> closed = tracker.CloseAll(t0.AddSeconds(5));
        Assert.Equal(EpisodeColor.Zolc, Assert.Single(closed).Kolor);
        Assert.False(tracker.IsActive(EpisodeColor.Zolc));
    }

    // --- DiskBudget ------------------------------------------------------------------------------------------------

    [Fact]
    public void DiskBudget_PrzerzedzaStareDniDoJednejNaGodzine_INieRuszaEpizodowAniDzisiaj()
    {
        string dir = DiagnozaTestConfig.TempDir("budget");
        try
        {
            DateTimeOffset now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            string oldDay = Path.Combine(dir, "2026-10-10");
            string today = Path.Combine(dir, "2026-10-20");
            foreach (string sub in new[] { "okresowe", "zmiany", "epizody" })
            {
                Directory.CreateDirectory(Path.Combine(oldDay, sub));
                Directory.CreateDirectory(Path.Combine(today, sub));
                for (int i = 0; i < 6; i++)
                {
                    File.WriteAllBytes(Path.Combine(oldDay, sub, $"20261010-10{i:00}00.png"), new byte[100]); // 6 klatek w godzinie 10
                    File.WriteAllBytes(Path.Combine(today, sub, $"20261020-10{i:00}00.png"), new byte[100]);
                }
            }

            File.WriteAllText(Path.Combine(dir, "epizody.jsonl"), "{}\n");

            DiskBudgetResult r = DiskBudget.Enforce(dir, now, limitBytes: 1000);

            Assert.Equal(10, r.Deleted); // 5 z okresowe + 5 ze zmiany starego dnia
            Assert.Single(Directory.GetFiles(Path.Combine(oldDay, "okresowe")));
            Assert.Single(Directory.GetFiles(Path.Combine(oldDay, "zmiany")));
            Assert.Equal(6, Directory.GetFiles(Path.Combine(oldDay, "epizody")).Length);
            Assert.Equal(6, Directory.GetFiles(Path.Combine(today, "okresowe")).Length);
            Assert.True(File.Exists(Path.Combine(dir, "epizody.jsonl")));
            Assert.True(r.OverLimit); // 26 plików × 100 B + jsonl > 1000
            Assert.False(DiskBudget.Enforce(dir, now, limitBytes: 1_000_000).OverLimit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- Wnioski ---------------------------------------------------------------------------------------------------

    private static MachineDiagnosticResult Connected(string profile = "amm580")
    {
        var r = new MachineDiagnosticResult("m", "M", "h", 5900, profile)
        {
            Status = MachineStatus.Connected,
            ConnectedAt = DateTimeOffset.UnixEpoch,
            Connections = 1,
            Server = new RfbServerInfo("3.8", [1], RfbSecurityType.None, 32, 32, RfbPixelFormat.Bgra32LittleEndian, "Fake"),
        };
        return r;
    }

    private static FrameSample Sample(RgbaImage img, double sec) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(sec), sec, 5, FrameMetrics.Measure(img, DiagnozaTestConfig.Profiles()), null, false);

    [Fact]
    public void Wnioski_CzernienieNaKtorejsKlatce_DajeWerdyktZCzasem()
    {
        MachineDiagnosticResult r = Connected();
        r.Frames.Add(Sample(DiagnozaTestConfig.Solid(32, 32, new Rgba(255, 255, 255)), 0));
        r.Frames.Add(Sample(DiagnozaTestConfig.Solid(32, 32, new Rgba(255, 255, 255)), 5));
        r.Frames.Add(Sample(DiagnozaTestConfig.Solid(32, 32, new Rgba(0, 0, 0)), 30));
        r.ReconnectTest = new ReconnectTestResult(true, 100, false, "wraca");

        MachineConclusion c = DiagnozaConclusions.Build(r, DiagnozaTestConfig.Profiles());

        Assert.StartsWith("czernieje po 30 s po połączeniu", c.CzarnyEkran, StringComparison.Ordinal);
        Assert.Contains("wraca", c.CzarnyEkran, StringComparison.Ordinal);
        Assert.Equal(FrameMetrics.MotywJasny, c.Motyw);
        Assert.Equal(DiagnozaConclusions.BrakCzerwieni, c.NajlepszyProfil);
        Assert.Equal("-", c.SugerowanyRoi);
        Assert.Contains("nie dowodzi", c.CoDalej, StringComparison.Ordinal);
        Assert.StartsWith("M:", c.CoDalej, StringComparison.Ordinal);
    }

    [Fact]
    public void Wnioski_CzarnaOdPoczatku_INieCzernieje()
    {
        MachineDiagnosticResult black = Connected();
        black.Frames.Add(Sample(DiagnozaTestConfig.Solid(8, 8, new Rgba(0, 0, 0)), 0));
        Assert.Equal("czarna od początku", DiagnozaConclusions.BlackVerdict(black));

        MachineDiagnosticResult ok = Connected();
        ok.Frames.Add(Sample(DiagnozaTestConfig.Solid(8, 8, new Rgba(200, 200, 200)), 0));
        Assert.Equal("nie czernieje", DiagnozaConclusions.BlackVerdict(ok));
        Assert.Equal("brak klatek", DiagnozaConclusions.BlackVerdict(Connected()));
    }

    [Fact]
    public void Wnioski_NajlepszyProfilWgCzerwieni_ISugerowanyRoiZBbox()
    {
        // Czerwony pasek u góry (0,0,32,8): przekracza próg w ROI „opencnc” (całe) i „amm580” (wycinek) — wygrywa para z najwyższym udziałem.
        MachineDiagnosticResult r = Connected("aidite5x");
        RgbaImage red = DiagnozaTestConfig.WithRed(32, 32, new RectInt(2, 1, 28, 6));
        r.Frames.Add(Sample(DiagnozaTestConfig.Solid(32, 32, new Rgba(255, 255, 255)), 0));
        r.Frames.Add(Sample(red, 10));
        r.Frames.Add(Sample(red, 20));

        MachineConclusion c = DiagnozaConclusions.Build(r, DiagnozaTestConfig.Profiles());

        Assert.NotEqual(DiagnozaConclusions.BrakCzerwieni, c.NajlepszyProfil);
        Assert.Contains("2 z 3 klatek nad progiem", c.NajlepszyProfil, StringComparison.Ordinal);
        Assert.StartsWith("0,0,", c.SugerowanyRoi, StringComparison.Ordinal); // 2-8 → 0, 1-8 → 0
        Assert.Contains("z 2 klatek z czerwienią", c.SugerowanyRoi, StringComparison.Ordinal);
        Assert.Contains("kandydat ROI", c.CoDalej, StringComparison.Ordinal);
    }

    [Fact]
    public void Wnioski_MaszynaNiepolaczona_CoDalejWedlugStatusu()
    {
        var unreachable = new MachineDiagnosticResult("p", "Premile", "192.168.1.114", 5900, "aidite-light2") { Status = MachineStatus.Unreachable, Reason = "nie odpowiada" };
        unreachable.PortScan = [new TcpProbeResult("h", 5901, true, 3, null), new TcpProbeResult("h", 9100, false, 1000, "limit")];
        MachineConclusion c1 = DiagnozaConclusions.Build(unreachable, DiagnozaTestConfig.Profiles());
        Assert.Contains("5901", c1.CoDalej, StringComparison.Ordinal);
        Assert.Contains("otwarte", c1.Wnioski[0], StringComparison.Ordinal);

        var pwd = new MachineDiagnosticResult("x", "580", "h", 5900, "amm580") { Status = MachineStatus.PasswordRequired, Reason = "wymaga hasła VNC" };
        MachineConclusion c2 = DiagnozaConclusions.Build(pwd, DiagnozaTestConfig.Profiles());
        Assert.Contains("hasło", c2.CoDalej, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("brak klatek", c2.CzarnyEkran);
    }

    // --- Podsumowanie i kody wyjścia -------------------------------------------------------------------------------

    [Fact]
    public void DiagnozaSummary_KodWyjsciaIRenderTextZawieraTabeleWnioskiICoDalej()
    {
        MachineDiagnosticResult a = Connected();
        a.Frames.Add(Sample(DiagnozaTestConfig.Solid(8, 8, new Rgba(255, 255, 255)), 0));
        var b = new MachineDiagnosticResult("b", "B", "h", 1, "amm580") { Status = MachineStatus.Unreachable, Reason = "nie odpowiada" };

        Assert.Equal(ProbeExitCodes.SomeConnected, DiagnozaSummary.ExitCode([a, b]));
        Assert.Equal(ProbeExitCodes.AllConnected, DiagnozaSummary.ExitCode([a]));
        Assert.Equal(ProbeExitCodes.NoneConnected, DiagnozaSummary.ExitCode([b]));
        Assert.Equal(ProbeExitCodes.NoneConnected, DiagnozaSummary.ExitCode([]));

        DiagnozaSchedule s = DiagnozaSchedule.For(60, 5);
        IReadOnlyList<string> text = DiagnozaSummary.RenderText([a, b], DiagnozaTestConfig.Profiles(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.Contains(text, l => l.StartsWith("maszyna", StringComparison.Ordinal) && l.Contains("czarny ekran?", StringComparison.Ordinal));
        Assert.Contains(text, l => l.Contains("Wnioski:", StringComparison.Ordinal));
        Assert.Contains(text, l => l.Contains("Co dalej:", StringComparison.Ordinal));
        Assert.Contains(text, l => l.Contains("kod wyjścia: 3", StringComparison.Ordinal));

        string json = DiagnozaSummary.RenderJson([a, b], DiagnozaTestConfig.Profiles(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), s);
        Assert.Contains("\"kodWyjscia\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"wersjaRfbNaglowek\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coDalej\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeExitCodes_FromSummary_LiczyTylkoWybraneMaszyny()
    {
        var summary = new SummaryReport();
        summary.Machine("a").Status = MachineStatus.Connected;
        summary.Machine("b").Status = MachineStatus.Disabled;
        Assert.Equal(ProbeExitCodes.AllConnected, ProbeExitCodes.FromSummary(summary));

        summary.Machine("c").Status = MachineStatus.Unreachable;
        Assert.Equal(ProbeExitCodes.SomeConnected, ProbeExitCodes.FromSummary(summary));

        var none = new SummaryReport();
        none.Machine("x").ApplySkip("Serwer 1.2.3.4:5900 wymaga hasła VNC (typ zabezpieczeń VncAuth), ale w konfiguracji nie podano hasła dla tej maszyny.");
        Assert.Equal(ProbeExitCodes.NoneConnected, ProbeExitCodes.FromSummary(none));
        Assert.Equal(MachineStatus.PasswordRequired, none.Machine("x").Status);
        Assert.Equal(ProbeExitCodes.NoneConnected, ProbeExitCodes.FromSummary(new SummaryReport()));
    }

    [Fact]
    public void MachineSummary_RenderJson_MaNowePolaStatusu()
    {
        var summary = new SummaryReport();
        MachineSummary row = summary.Machine("amm580");
        row.ApplyServerInfo(new RfbServerInfo("3.8", [129, 5], RfbSecurityType.RA2_256, 800, 600, RfbPixelFormat.Bgra32LittleEndian, "amm580")
        {
            RawVersionHeader = "RFB 005.000\\n",
        }, DateTimeOffset.UnixEpoch);
        row.AddDisconnect(DateTimeOffset.UnixEpoch.AddMinutes(1), "zerwane");
        row.NoteEncodings([5, 0, 5]);
        row.Frames = 3;

        string json = summary.RenderJson();

        foreach (string field in new[] { "\"status\": \"połączono\"", "\"powod\"", "\"ostatniBlad\": \"zerwane\"", "\"polaczonoO\"", "\"rozlaczenia\"", "\"klatek\": 3",
                     "\"czarnychKlatek\"", "\"pierwszaKlatkaPo\"", "\"serwerNazwa\": \"amm580\"", "\"wersjaRfbNaglowek\": \"RFB 005.000\\\\n\"", "\"typyZabezpieczen\"",
                     "\"oferowane\": \"129 (RA2_256), 5 (RA2)\"", "\"wybrany\": \"RA2_256\"", "\"kodowania\": \"Hextile, Raw\"" })
        {
            Assert.Contains(field, json, StringComparison.Ordinal);
        }
    }

    // --- Tryby maszyn ----------------------------------------------------------------------------------------------

    [Fact]
    public void MachineModeStore_520ZawszeOdpytywanie_SharedTestDecydujeDlaReszty_PlikZapisIOdczyt()
    {
        var r520 = new MachineDiagnosticResult("amm520-1", "520 (1)", "h", 5900, "aidite5x");
        var kicked = new MachineDiagnosticResult("premile", "Premile", "h", 5900, "aidite-light2") { SharedTest = new SharedTestResult(true, false, "wyrzuca") };
        var fine = new MachineDiagnosticResult("amm580", "580", "h", 5900, "amm580") { SharedTest = new SharedTestResult(true, true, "ok") };
        var unknown = new MachineDiagnosticResult("cameo1", "Cameo 1", "h", 5900, "aidite-light2");

        Dictionary<string, MachineModeStore.Entry> modes = MachineModeStore.FromResults([r520, kicked, fine, unknown]);

        Assert.Equal(MachineMode.Odpytywanie, modes["amm520-1"].Tryb);
        Assert.Equal(MachineMode.Odpytywanie, modes["premile"].Tryb);
        Assert.Equal(MachineMode.Trwale, modes["amm580"].Tryb);
        Assert.False(modes.ContainsKey("cameo1"));

        string dir = DiagnozaTestConfig.TempDir("modes");
        try
        {
            MachineModeStore.Save(dir, modes);
            IReadOnlyDictionary<string, MachineModeStore.Entry> loaded = MachineModeStore.Load(dir);
            Assert.Equal(MachineMode.Odpytywanie, MachineModeStore.Resolve(DiagnozaTestConfig.Machine("premile", 1, "aidite-light2"), loaded));
            Assert.Equal(MachineMode.Trwale, MachineModeStore.Resolve(DiagnozaTestConfig.Machine("cameo1", 1, "aidite-light2"), loaded));
            Assert.Equal(MachineMode.Odpytywanie, MachineModeStore.Resolve(DiagnozaTestConfig.Machine("amm520-2", 1, "aidite5x"), new Dictionary<string, MachineModeStore.Entry>()));
            Assert.Empty(MachineModeStore.Load(Path.Combine(dir, "nie-ma")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DescribeDisconnect_NagleZamknieciePoznajePrzejecieEkranu_ABladProtokoluNie()
    {
        string sudden = MachineDiagnostic.DescribeDisconnect(new RfbException("Serwer 192.168.1.101:5900 zamknął połączenie."));
        string proto = MachineDiagnostic.DescribeDisconnect(new RfbProtocolException("Nieznany typ komunikatu serwera RFB: 9."));

        Assert.Contains("inny klient VNC przejął", sudden, StringComparison.Ordinal);
        Assert.StartsWith("błąd protokołu", proto, StringComparison.Ordinal);
        Assert.DoesNotContain("przejął", proto, StringComparison.Ordinal);
    }

    [Fact]
    public void IsRetryableSkip_SiecTak_HasloNie()
    {
        Assert.True(Commands.WatchCommand.IsRetryableSkip("nie odpowiada (wyłączona?) — Nie udało się połączyć z 1.2.3.4:5900 w ciągu 5 s."));
        Assert.False(Commands.WatchCommand.IsRetryableSkip("Serwer 1.2.3.4:5900 wymaga hasła VNC (typ zabezpieczeń VncAuth), ale w konfiguracji nie podano hasła dla tej maszyny."));
        Assert.False(Commands.WatchCommand.IsRetryableSkip("Serwer 1.2.3.4:5900 oferuje tylko typy zabezpieczeń 13, 133 (wymaga RSA-AES)"));
    }
}
