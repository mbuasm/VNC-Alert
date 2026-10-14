using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorEpisodeTests
{
    /// <summary>Profil bez obserwacji ekranu głównego HMI (sekcja mainScreen) — te testy liczą wywołania OCR paska statusu; sama obserwacja ekranu ma własne testy (<see cref="MachineMonitorMainScreenTests"/>).</summary>
    private static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null };

    private const string TekstServoAlarm = "No.705 [N]Axis 6 servo alarm";
    private const string TekstServoNotEnabled = "No.725 [N]Axis 6 servo isn't enabled";
    private const string TekstMacroExec = "No.520 [N]";
    private const string TekstMacroPause = "No.2502 [N]";

    private static readonly AlarmCodeTable Codes = new(TestPaths.ExampleAidite.Codes);

    /// <summary>Polski opis kodu alarmu wzięty WPROST z sekcji "codes" pliku config/rules.example.json.</summary>
    private static string CodeShortText(int code)
    {
        Assert.True(Codes.TryResolve(code, out ResolvedAlarmCode resolved), $"Kodu {code} nie ma w rules.example.json.");
        return resolved.ShortText;
    }

    /// <summary>Klucz alertu dla komunikatu dopasowanego po numerze alarmu (patrz MessageMapper.CodeRuleIdPrefix).</summary>
    private static AlertKey CodeKey(int code) => AlertKey.ForRule("aidite1", MessageMapper.CodeRuleIdPrefix + code);

    private static MachineConfig Machine() => new()
    {
        Id = "aidite1",
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
    };

    /// <summary>
    /// Klatka z czerwonym tekstem na pasku statusu. Różne <paramref name="seed"/>/<paramref name="coverage"/>
    /// dają WYRAŹNIE różny obraz ROI — tak jak kolejny komunikat z cyklu HMI (patrz asercja wstępna
    /// w <see cref="RunAsync_GdyTrescRoiSieZmieniPoUplywieOdstepu_PonawiaOcrMimoWczesniejszegoDopasowania"/>).
    /// </summary>
    private static RgbaImage RedFrame(string seed, double coverage) => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.RedText, seed, coverage));

    private static RgbaImage NormalFrame() => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height,
            SyntheticScreens.LightBackground, SyntheticScreens.WhiteText, "ok"));

    /// <summary>Obraz samego paska statusu wycięty z klatki — to on trafia do <c>FrameDiff</c> w monitorze.</summary>
    private static RgbaImage StatusRoi(RgbaImage frame) => frame.Crop(Profile.StatusBarRoi.Clamp(frame.Width, frame.Height));

    private sealed class RecordingCaptureStore : ICaptureStore
    {
        public List<(string MachineId, string Tag)> Saved { get; } = [];

        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag)
        {
            Saved.Add((machineId, tag));
            return $"captures/{machineId}_{tag}.png";
        }

        public void Prune(TimeSpan retention)
        {
        }
    }

    /// <summary>Sterownik pętli: zamiast czekać, przesuwa sztuczny zegar i po zadanej liczbie cykli przerywa pętlę.</summary>
    private sealed class LoopDriver
    {
        private readonly int _maxCycles;
        private int _cycles;

        public LoopDriver(int maxCycles, FakeClock clock)
        {
            _maxCycles = maxCycles;
            Clock = clock;
        }

        public FakeClock Clock { get; }

        public CancellationTokenSource Cts { get; } = new();

        public Action<int>? AfterCycle { get; set; }

        public Task Delay(TimeSpan interval, CancellationToken ct)
        {
            Clock.Advance(interval);
            _cycles++;
            AfterCycle?.Invoke(_cycles);
            if (_cycles >= _maxCycles)
            {
                Cts.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        MachineMonitor Monitor,
        LoopDriver Driver,
        FakeClock Clock,
        AlertStateMachine Alerts,
        FakeOcrEngine Ocr,
        RecordingCaptureStore Captures,
        RecordingLoggerFactory Logs,
        List<AlertEvent> Events);

    private static Harness Build(int maxCycles, FakeScreenSource source, FakeOcrEngine ocr, TimingConfig? timing = null,
        DiagnosticsConfig? diagnostics = null)
    {
        var clock = new FakeClock();
        var driver = new LoopDriver(maxCycles, clock);
        TimingConfig t = timing ?? new TimingConfig();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, t, acks);
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var captures = new RecordingCaptureStore();
        var logs = new RecordingLoggerFactory();

        var monitor = new MachineMonitor(
            Machine(),
            Profile,
            () => source,
            testOverride: null,
            ocr,
            new MessageMapper(TestPaths.ExampleRules),
            alerts,
            clock,
            t,
            captures,
            logs.CreateLogger("MachineMonitor"),
            driver.Delay,
            diagnostics);

        return new Harness(monitor, driver, clock, alerts, ocr, captures, logs, events);
    }

    [Fact]
    public async Task RunAsync_GdyTrescRoiSieZmieniPoUplywieOdstepu_PonawiaOcrMimoWczesniejszegoDopasowania()
    {
        RgbaImage klatkaA = RedFrame("blad-a", 0.02);
        RgbaImage klatkaB = RedFrame("blad-b", 0.08);

        // Warunek wstępny: dla monitora to MUSI być „inna treść” (próg FrameDiff w monitorze to 2 %).
        Assert.True(
            FrameDiff.ChangedFraction(StatusRoi(klatkaA), StatusRoi(klatkaB)) > 0.02,
            "Obie klatki testowe są zbyt podobne — test nie sprawdzałby tego, co miał.");

        var source = new FakeScreenSource("aidite1", klatkaA);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstMacroExec).Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 5, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 2000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                source.Frame = klatkaB; // HMI pokazał kolejny komunikat z cyklu
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(2, h.Ocr.CallCount);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(CodeKey(705), alert.Key);
        Assert.Equal(CodeShortText(705), alert.ShortText);
        Assert.Equal(90, alert.Priority);
    }

    [Fact]
    public async Task RunAsync_GdyTrescRoiSieZmieniPrzedUplywemOdstepu_NiePonawiaOcr()
    {
        // Dlaczego: migotanie ekranu i przerysowania HMI potrafią zmieniać ROI co sekundę — bez minimalnego
        // odstępu obserwator robiłby OCR co cykl dla 12 maszyn naraz i zajechałby procesor komputera
        // operatora (a to ten sam komputer, na którym ktoś pracuje).
        RgbaImage klatkaA = RedFrame("blad-a", 0.02);
        RgbaImage klatkaB = RedFrame("blad-b", 0.08);
        var source = new FakeScreenSource("aidite1", klatkaA);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstMacroExec).Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 5, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                source.Frame = klatkaB;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(1, h.Ocr.CallCount);
        Assert.Equal(CodeShortText(520), Assert.Single(h.Alerts.ActiveAlerts).ShortText);
    }

    [Fact]
    public async Task RunAsync_GdyTrescRoiSieNieZmienia_NiePonawiaOcrWCaleEpizodu()
    {
        // Dlaczego: nieruchome okienko z tym samym komunikatem nie niesie nic nowego — ponawianie OCR
        // byłoby czystym marnowaniem czasu procesora.
        var source = new FakeScreenSource("aidite1", RedFrame("blad-a", 0.02));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm).Enqueue(TekstMacroPause);
        Harness h = Build(maxCycles: 8, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 1000 });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(1, h.Ocr.CallCount);
        Assert.Equal(CodeKey(705), Assert.Single(h.Alerts.ActiveAlerts).Key);
    }

    [Fact]
    public async Task RunAsync_CyklTrzechKomunikatowJednegoEpizodu_DajeJedenAlertZNajwazniejszymKomunikatem()
    {
        RgbaImage a = RedFrame("blad-a", 0.02);
        RgbaImage b = RedFrame("blad-b", 0.08);
        RgbaImage c = RedFrame("blad-c", 0.14);
        var source = new FakeScreenSource("aidite1", a);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstMacroExec).Enqueue(TekstServoAlarm).Enqueue(TekstServoNotEnabled);
        Harness h = Build(maxCycles: 7, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 2000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                source.Frame = b;
            }
            else if (cycle == 5)
            {
                source.Frame = c;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(3, h.Ocr.CallCount);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(CodeKey(705), alert.Key);
        Assert.Equal(CodeShortText(705), alert.ShortText);
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.Equal(CodeShortText(705), snapshot.LastShortText);
        Assert.Equal(TekstServoNotEnabled, snapshot.LastOcrText);

        Assert.NotEmpty(h.Logs.Containing("kolejny komunikat w tym samym epizodzie"));
        Assert.NotEmpty(h.Logs.Containing("lepszy komunikat w epizodzie"));
    }

    [Fact]
    public async Task RunAsync_GdyCzerwienZniknieIWroci_ZaczynaLiczycNajlepszyKomunikatOdNowa()
    {
        // Dlaczego: „najlepszy komunikat” ma sens tylko W RAMACH jednego epizodu. Po restarcie maszyny
        // (czerwień znika i wraca) nowy błąd — choćby mniej ważny („Request home alarm”, „No.2502”) —
        // jest tym, co maszyna pokazuje TERAZ; gdyby monitor pamiętał alarm serwa sprzed restartu,
        // operator biegłby do maszyny z nieaktualnym opisem.
        RgbaImage red = RedFrame("blad-a", 0.02);
        RgbaImage normal = NormalFrame();
        var source = new FakeScreenSource("aidite1", red);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm).Enqueue(TekstMacroPause);
        // Cykl = 1 s (pollIntervalMs); czerwień znika po cyklu 2 i wraca po cyklu 15 — przerwa ~13 s,
        // czyli >= EpisodeBreakAfterMs (10 s), ale < EpisodeCloseAfterMs (30 s): stary alert jeszcze
        // wisi, a mimo to nowy komunikat ma dać NOWY alert.
        Harness h = Build(maxCycles: 18, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 2)
            {
                source.Frame = normal; // błąd skasowany / maszyna restartuje się
            }
            else if (cycle == 15)
            {
                source.Frame = red;    // po restarcie pojawia się INNY komunikat
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(2, h.Ocr.CallCount);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(CodeKey(2502), alert.Key);
        Assert.Equal(CodeShortText(2502), alert.ShortText);
        Assert.Equal(CodeShortText(2502), h.Monitor.Snapshot.LastShortText);

        // Dwa osobne epizody = dwa zrzuty ekranu „blad” i dwa dzwonki.
        Assert.Equal(2, h.Captures.Saved.Count(s => s.Tag == "blad"));
        Assert.Equal(2, h.Events.Count(e => e.Kind == AlertEventKind.Raised));
    }

    [Fact]
    public async Task RunAsync_GdyCzerwienZniknieNaChwileIWrociZInnymKomunikatem_ZostajeJedenAlert()
    {
        // Dlaczego: maszyna wywala kilka błędów naraz i pokazuje je po kolei w pętli; jeśli
        // HMI między komunikatami na 1–2 s gasi okienko, to NIE koniec błędu. Ma zostać JEDEN alert (z
        // ważniejszym komunikatem), bez drugiego dzwonka — inaczej każdy obrót pętli budziłby ludzi.
        RgbaImage red = RedFrame("blad-a", 0.02);
        RgbaImage normal = NormalFrame();
        var source = new FakeScreenSource("aidite1", red);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm).Enqueue(TekstMacroPause);
        Harness h = Build(maxCycles: 8, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 2)
            {
                source.Frame = normal; // mrugnięcie okienka między komunikatami
            }
            else if (cycle == 4)
            {
                source.Frame = red;    // kolejny (mniej ważny) komunikat z pętli
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(CodeKey(705), alert.Key);
        Assert.Equal(1, h.Events.Count(e => e.Kind == AlertEventKind.Raised));
        Assert.Empty(h.Alerts.RecentHistory);

        Assert.Equal(1, h.Captures.Saved.Count(s => s.Tag == "blad"));
        Assert.DoesNotContain(h.Captures.Saved, s => s.Tag == "przerwa");

        // Treść alertu i migawki to NAJLEPSZY komunikat epizodu (alarm serwa), a nie ten, który akurat
        // był na ekranie przed mrugnięciem albo po nim.
        Assert.Equal(CodeShortText(705), alert.ShortText);
        Assert.Equal(alert.ShortText, h.Monitor.Snapshot.LastShortText);

        // Epizod ani razu się nie zamknął — mrugnięcie krótsze niż EpisodeBreakAfterMs to wciąż ten sam błąd.
        Assert.Empty(h.Logs.Containing("epizod czerwieni zamknięty"));
    }

    [Fact]
    public async Task RunAsync_GdyOkienkoHmiMrugaPrzezDwieIPolMinuty_ZostajeJedenEpizodIDlawiZrzutyPrzerwy()
    {
        RgbaImage red = RedFrame("blad-a", 0.02);
        RgbaImage normal = NormalFrame();
        var source = new FakeScreenSource("aidite1", red);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm);
        // 150 cykli po 1 s: czerwień w cyklach nieparzystych, przerwa w parzystych (filtr „2 z 3”
        // potwierdza czerwień co drugi cykl, więc epizod cały czas trwa, ale przerwy są prawdziwe).
        Harness h = Build(maxCycles: 150, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 },
            diagnostics: new DiagnosticsConfig { CaptureBreaks = true });
        h.Driver.AfterCycle = cycle => source.Frame = cycle % 2 == 0 ? red : normal;

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(1, h.Captures.Saved.Count(s => s.Tag == "blad"));
        Assert.Equal(1, h.Events.Count(e => e.Kind == AlertEventKind.Raised));
        Assert.Equal(CodeShortText(705), Assert.Single(h.Alerts.ActiveAlerts).ShortText);

        // Zrzuty „przerwa”: pierwszy w 3. sekundzie przebiegu, kolejne dopiero po 60 s dławienia
        // (3 s → 63 s → 123 s; następny wypadłby w 183 s, czyli poza 150-sekundowym przebiegiem).
        Assert.Equal(3, h.Captures.Saved.Count(s => s.Tag == "przerwa"));

        // Dokładnie jedno otwarcie epizodu i ani jedno zamknięcie — epizod trwa nadal, gdy test się kończy.
        Assert.Single(h.Logs.Containing("epizod czerwieni otwarty"));
        Assert.Empty(h.Logs.Containing("epizod czerwieni zamknięty"));
    }

    [Fact]
    public async Task RunAsync_GdyOkienkoHmiMruga_PrzyDomyslnejKonfiguracji_NiePowstajeZadenZrzutPrzerwy()
    {
        RgbaImage red = RedFrame("blad-a", 0.02);
        RgbaImage normal = NormalFrame();
        var source = new FakeScreenSource("aidite1", red);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 150, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });
        h.Driver.AfterCycle = cycle => source.Frame = cycle % 2 == 0 ? red : normal;

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.DoesNotContain(h.Captures.Saved, s => s.Tag == "przerwa");
        Assert.Equal(1, h.Captures.Saved.Count(s => s.Tag == "blad"));
        Assert.Equal(1, h.Events.Count(e => e.Kind == AlertEventKind.Raised));
    }

    [Fact]
    public async Task RunAsync_GdyCzerwienZniknieNaDluzejNizProgPrzerwy_ZamykaEpizodJednymWpisemWLogu()
    {
        // Dlaczego: log ma dawać czytelną parę „otwarty … zamknięty” na JEDEN błąd — po niej widać w
        // rozwiązywaniu problemów, jak długo trwał epizod i z jakim komunikatem się skończył. Gdyby
        // zamknięcie logowało się przy każdej klatce bez czerwieni, para zamieniłaby się w powódź wpisów.
        RgbaImage red = RedFrame("blad-a", 0.02);
        RgbaImage normal = NormalFrame();
        var source = new FakeScreenSource("aidite1", red);
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm);
        // Czerwień w cyklach 1-3, potem już tylko spokojny ekran; przerwa dłuższa niż EpisodeBreakAfterMs
        // (10 s przy cyklu 1 s) zamyka lokalny epizod.
        Harness h = Build(maxCycles: 20, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                source.Frame = normal;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Logs.Containing("epizod czerwieni otwarty"));
        LogEntry zamkniecie = Assert.Single(h.Logs.Containing("epizod czerwieni zamknięty (powód: przerwa"));
        Assert.Contains(CodeShortText(705), zamkniecie.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_GdyLacznoscZnikaWTrakcieEpizodu_ZamykaEpizodZPowodemUtratyLacznosci()
    {
        // Dlaczego: gdy maszyna znika z sieci (typowo restartuje się po błędzie), nie wiadomo, co działo
        // się na jej ekranie — czekanie na histerezę mrugania byłoby zgadywaniem. Epizod zamykamy od
        // razu, żeby po powrocie maszyny odczyt zaczął się od zera (nowy zrzut, nowy „najlepszy komunikat”).
        var source = new FakeScreenSource("aidite1", RedFrame("blad-a", 0.02));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 6, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                source.CaptureFailure = () => new IOException("Zerwane połączenie z maszyną (test).");
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        LogEntry zamkniecie = Assert.Single(h.Logs.Containing("epizod czerwieni zamknięty (powód: utrata łączności"));
        Assert.Contains(CodeShortText(705), zamkniecie.Message, StringComparison.Ordinal);
        Assert.Single(h.Logs.Containing("epizod czerwieni otwarty"));
    }

    [Fact]
    public async Task RunAsync_GdyCzerwienTrwaNieprzerwanie_LogujeUdzialCzerwieniTylkoPrzyZmianieStanuFiltru()
    {
        // Dlaczego: udział czerwieni logowany w KAŻDYM cyklu to przy 7 maszynach i cyklu 1 s ponad 25 tys.
        // wpisów na godzinę. Interesuje nas moment, w którym
        // filtr „2 z 3” zmienia zdanie, a nie to, że przez pół godziny nic się nie zmieniło.
        var source = new FakeScreenSource("aidite1", RedFrame("blad-a", 0.02));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 10, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Logs.Containing("filtr czerwieni potwierdził czerwień"));
        Assert.Empty(h.Logs.Containing("filtr czerwieni nie potwierdza już czerwieni"));
    }

    [Fact]
    public async Task RunAsync_PoOdczytaniuKomunikatu_LogujeSurowyTekstOcrIWariantZCyframi()
    {
        // Dlaczego: gdy operator mówi „maszyna miała błąd, a program pokazał coś innego”, jedynym śladem
        // jest log. Musi w nim być DOKŁADNIE to, co zwrócił OCR, i wariant z zachowanymi cyframi — to na
        // nim dopasowują się reguły po numerze alarmu (No.705), więc bez niego nie da się zdiagnozować,
        // dlaczego reguła nie trafiła.
        var source = new FakeScreenSource("aidite1", RedFrame("blad-a", 0.02));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 3, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Logs.Containing(
            "OCR surowy: 'No.705 [N]Axis 6 servo alarm' | z cyframi: 'NO 705 N AXIS 6 SERVO ALARM'"));
    }

    [Fact]
    public async Task RunAsync_GdyPierwszyOdczytToNieczytelneSmieci_PonawiaOcrBezMaskiCzerwieniWTymSamymCyklu()
    {
        var source = new FakeScreenSource("aidite1", RedFrame("blad-a", 0.02));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("•—— ł").Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 3, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(2, h.Ocr.CallCount);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(CodeKey(705), alert.Key);
        Assert.Equal(CodeShortText(705), alert.ShortText);
        Assert.NotEmpty(h.Logs.Containing("drugie podejście OCR (bez maski czerwieni)"));
    }

    [Fact]
    public async Task RunAsync_GdyPierwszyOdczytDopasowalRegule_NiePonawiaOcrBezMaskiCzerwieni()
    {
        // Dlaczego: drugie podejście kosztuje kolejne wywołanie jedynego silnika OCR w programie (kolejka
        // dla wszystkich maszyn — patrz SerializingOcrEngine). Skoro pierwszy odczyt trafił w regułę,
        // dokładanie mu pracy byłoby marnowaniem czasu procesora operatora.
        var source = new FakeScreenSource("aidite1", RedFrame("blad-a", 0.02));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue(TekstServoAlarm).Enqueue(TekstMacroPause);
        Harness h = Build(maxCycles: 3, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 60000 });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(1, h.Ocr.CallCount);
        Assert.Equal(CodeShortText(705), Assert.Single(h.Alerts.ActiveAlerts).ShortText);
        Assert.Empty(h.Logs.Containing("drugie podejście OCR (bez maski czerwieni)"));
    }

    [Fact]
    public async Task RunAsync_GdyOcrNicNieOdczytalMimoZmianTresci_TworzyAlertONieodczytanejTresci()
    {
        // Dlaczego (zachowanie utrwalone): czerwień na pasku statusu ZAWSZE ma dać alert — nawet gdy nie
        // wiadomo, co konkretnie się stało. Cisza byłaby najgorszą z możliwych odpowiedzi.
        RgbaImage a = RedFrame("blad-a", 0.02);
        RgbaImage b = RedFrame("blad-b", 0.08);
        var source = new FakeScreenSource("aidite1", a);
        var ocr = new FakeOcrEngine();
        ocr.EnqueueEmpty(10);
        Harness h = Build(maxCycles: 6, source: source, ocr: ocr, timing: new TimingConfig { OcrRetryIntervalMs = 2000 });
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 3)
            {
                source.Frame = b;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("Błąd (nie odczytano treści)", alert.ShortText);
        Assert.Equal("text:empty", alert.Key.Discriminator);
        Assert.Equal("Błąd (nie odczytano treści)", h.Monitor.Snapshot.LastShortText);
    }

    [Fact]
    public async Task RunAsync_MigajacyPasekZTymSamymKomunikatem_DajeJedenAlertJedenDzwiekIZachowujeAck()
    {
        RgbaImage red = RedFrame("No.1248 tray", 0.06);
        RgbaImage normal = NormalFrame();
        var source = new FakeScreenSource("aidite1", red);
        var ocr = new FakeOcrEngine();
        for (int i = 0; i < 20; i++) ocr.Enqueue(TekstServoAlarm);
        Harness h = Build(maxCycles: 80, source: source, ocr: ocr);
        h.Driver.AfterCycle = cycle =>
        {
            h.Alerts.Tick();
            if (cycle == 5)
            {
                Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
                Assert.True(h.Alerts.AcknowledgeLocal(alert.Key, "PC-1", "operator"));
            }

            // Trzy pełne mrugnięcia: 11 s bez czerwieni, potem czerwień wraca na 9 s.
            if (cycle is 10 or 30 or 50) source.Frame = normal;
            if (cycle is 21 or 41 or 61) source.Frame = red;
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert only = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, only.Kind);
        Assert.Equal(AlertStatus.Acknowledged, only.Status);
        Assert.Equal(CodeKey(705), only.Key);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(1), only.StartedAt);   // początek PIERWSZEGO epizodu, nie mrugnięcia
        Assert.Single(h.Events, e => e.Kind == AlertEventKind.Raised);        // JEDEN dźwięk na cały ciąg
        Assert.DoesNotContain(h.Events, e => e.Kind == AlertEventKind.Closed);
    }
}
