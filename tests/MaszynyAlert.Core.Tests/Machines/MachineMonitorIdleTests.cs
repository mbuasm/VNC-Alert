using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Machines;

/// <summary>
/// Obserwacja „maszyna stoi bez pracy – dołóż prace” w pętli obserwatora w trybie PASKA postępu
/// (<see cref="ProgressMode.Bar"/>): jeden odczyt obejmujący sumę obszarów (pasek statusu ∪ pasek
/// postępu), detekcja postoju równolegle z wykrywaniem czerwieni, zrzut „postoj” raz na epizod oraz
/// bezpieczne zachowanie, gdy <c>idle.progressRoi</c> leży poza ekranem. Domyślny tryb przykładowej
/// konfiguracji to <see cref="ProgressMode.Number"/> (postęp jako liczba odczytywana przez OCR —
/// patrz <see cref="MachineMonitorNumberModeTests"/>), więc profile są tu jawnie przestawiane na tryb
/// paska; tryb paska zostaje jako alternatywa dla HMI, w którym pasek postępu naprawdę istnieje.
/// </summary>
public sealed class MachineMonitorIdleTests
{
    /// <summary>
    /// Profil z przykładowej konfiguracji przestawiony na tryb paska: pasek statusu i
    /// obszar postępu zmierzone, progi koloru/wypełnienia domyślne (zieleń 80°..160°, 0,97).
    /// </summary>
    private static readonly ScreenProfile Profile = WTrybiePaska(TestPaths.Profile("opencnc"));

    /// <summary>Obszar, który obserwator powinien pobierać jednym żądaniem, gdy obserwacja postoju działa.</summary>
    private static readonly RectInt Suma = Profile.StatusBarRoi.Union(Profile.Idle!.ProgressRoi);

    /// <summary>
    /// Szerokość sztucznej klatki ekranu — LICZONA z profilu, a nie wpisana na sztywno: obszary w
    /// config.example.json są dostrajane, a klatka mniejsza niż ROI wyłącza obserwację postoju w MachineMonitor —
    /// test padałby wtedy z powodu samego dostrojenia konfiguracji, nie regresji kodu.
    /// </summary>
    private static readonly int SzerokoscKlatki = Math.Max(Suma.Right, Profile.StatusBarRoi.Right);

    /// <summary>Wysokość sztucznej klatki — z zapasem pod dolną krawędzią obserwowanych obszarów (patrz <see cref="SzerokoscKlatki"/>).</summary>
    private static readonly int WysokoscKlatki = Suma.Bottom + 16;

    /// <summary>Kolor wypełnienia paska postępu (zieleń w domyślnym zakresie HSV profilu).</summary>
    private static readonly Rgba Green = new(40, 200, 60);

    /// <summary>Ile cykli pętli (po 1 s) trzeba, żeby przekroczyć próg postoju z zapasem.</summary>
    private const int CykliPonadProg = 130;

    /// <summary>Ten sam profil, ale z sekcją „idle” jawnie w trybie paska (domyślne progi koloru i wypełnienia).</summary>
    private static ScreenProfile WTrybiePaska(ScreenProfile profile) =>
        profile with { MainScreen = null, Idle = profile.Idle! with { Mode = ProgressMode.Bar } };

    private static ScreenProfile ProfilZPostepemNadStatusem()
    {
        ScreenProfile baza = WTrybiePaska(TestPaths.Profile("opencnc"));
        return baza with
        {
            StatusBarRoi = new RectInt(0, 728, 1024, 40),
            Idle = baza.Idle! with { ProgressRoi = new RectInt(440, 300, 120, 24) },
        };
    }

    private static MachineConfig Machine() => new()
    {
        Id = "aidite1",
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
        Enabled = true,
        Mode = ConnectionMode.Persistent,
    };

    /// <summary>
    /// Pełna klatka ekranu maszyny (rozmiar liczony z profilu — mieści pasek postępu) z paskiem statusu u góry
    /// (czerwony tekst błędu, gdy <paramref name="red"/>) i paskiem postępu wypełnionym w
    /// <paramref name="progress"/>.
    /// </summary>
    private static RgbaImage Frame(double progress, bool red = false)
    {
        var frame = new RgbaImage(SzerokoscKlatki, WysokoscKlatki);
        frame.Fill(SyntheticScreens.LightBackground);

        RectInt status = Profile.StatusBarRoi;
        RgbaImage bar = SyntheticScreens.StatusBar(
            status.Width, status.Height, SyntheticScreens.LightBackground,
            red ? SyntheticScreens.RedText : SyntheticScreens.WhiteText, red ? "blad" : "ok");
        frame.Blit(bar, status.X, status.Y);

        RectInt p = Profile.Idle!.ProgressRoi;
        int filled = (int)Math.Round(p.Width * progress);
        for (int x = 0; x < p.Width; x++)
        {
            for (int y = 0; y < p.Height; y++)
            {
                frame.SetPixel(p.X + x, p.Y + y, x < filled ? Green : SyntheticScreens.Light2Background);
            }
        }

        return frame;
    }

    /// <summary>Mała klatka (jak w pozostałych testach monitora) — pasek postępu z profilu leży poza jej granicami.</summary>
    private static RgbaImage MalaKlatka(bool red = false) => SyntheticScreens.FullFrame(
        Profile,
        SyntheticScreens.StatusBar(
            Profile.StatusBarRoi.Width, Profile.StatusBarRoi.Height, SyntheticScreens.LightBackground,
            red ? SyntheticScreens.RedText : SyntheticScreens.WhiteText, red ? "blad" : "ok"));

    /// <summary>Magazyn przechwyceń zapamiętujący wywołania (bez dotykania dysku).</summary>
    private sealed class RecordingCaptureStore : ICaptureStore
    {
        public List<(string MachineId, string Tag, int Width, int Height, DateTimeOffset At)> Saved { get; } = [];

        public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag)
        {
            Saved.Add((machineId, tag, image.Width, image.Height, at));
            return $"captures/{machineId}_{tag}.png";
        }

        public void Prune(TimeSpan retention)
        {
        }
    }

    /// <summary>Sterownik pętli monitora — zamiast czekać, przesuwa sztuczny zegar i po N cyklach anuluje token.</summary>
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
        RecordingCaptureStore Captures,
        RecordingLoggerFactory Logs,
        List<AlertEvent> Events);

    private static Harness Build(int maxCycles, FakeScreenSource source, ScreenProfile? profile = null,
        DiagnosticsConfig? diagnostics = null)
    {
        var clock = new FakeClock();
        var driver = new LoopDriver(maxCycles, clock);
        var timing = new TimingConfig();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, timing, acks);
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        var captures = new RecordingCaptureStore();
        var logs = new RecordingLoggerFactory();

        var monitor = new MachineMonitor(
            Machine(),
            profile ?? Profile,
            () => source,
            testOverride: null,
            new FakeOcrEngine(),
            new MessageMapper(TestPaths.ExampleRules),
            alerts,
            clock,
            timing,
            captures,
            logs.CreateLogger("MachineMonitor"),
            driver.Delay,
            diagnostics);

        return new Harness(monitor, driver, clock, alerts, captures, logs, events);
    }

    [Fact]
    public async Task RunAsync_ZWlaczonaObserwacjaPostoju_PobieraSumeObszaruStatusuIPaskaPostepu()
    {
        // Dlaczego: pasek statusu i pasek postępu mają być pobierane JEDNYM żądaniem VNC (suma prostokątów),
        // a nie dwoma — maszyny WinCE ledwo wyrabiają z jednym strumieniem na 12 maszyn.
        var source = new FakeScreenSource("aidite1", Frame(1.0));
        Harness h = Build(maxCycles: 4, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        // Suma ma objąć OBA obserwowane obszary — sprawdzamy to policzone z profilu, a nie przypięte na
        // sztywno, bo ROI w config.example.json są dostrajane (patrz SzerokoscKlatki).
        RectInt status = Profile.StatusBarRoi;
        RectInt postep = Profile.Idle!.ProgressRoi;
        Assert.Equal(Math.Min(status.X, postep.X), Suma.X);
        Assert.Equal(Math.Min(status.Y, postep.Y), Suma.Y);
        Assert.Equal(Math.Max(status.Right, postep.Right), Suma.Right);
        Assert.Equal(Math.Max(status.Bottom, postep.Bottom), Suma.Bottom);

        Assert.NotEmpty(source.RequestedRois);
        Assert.All(source.RequestedRois, roi => Assert.Equal(Suma, roi));
        Assert.True(h.Monitor.Snapshot.IdleWatchEnabled);
    }

    [Fact]
    public async Task RunAsync_GdyPasekStoiNa100ProcentDluzejNizProg_TworzyAlertPostoju()
    {
        // Dlaczego: to jest cała funkcja widziana od strony pętli obserwatora — pasek na 100 % przez
        // ponad 2 minuty ma dać alert MachineIdle z prawdziwej maszyny stanów (nie z atrapy).
        var source = new FakeScreenSource("aidite1", Frame(1.0));
        Harness h = Build(maxCycles: CykliPonadProg, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertKey.Idle("aidite1"), alert.Key);
        Assert.Equal("Aidite 1", alert.MachineName);
        Assert.False(alert.Sound);
        Assert.Contains(h.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Kind == AlertKind.MachineIdle);

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.True(snapshot.IdleWatchEnabled);
        Assert.Equal(1.0, snapshot.LastProgressFraction, 3);
        Assert.True(snapshot.ProgressFullNow);
        Assert.True(snapshot.ProgressFullConfirmed);
        // Potwierdzenie „2 z 3” zapada w drugim cyklu, czyli sekundę po starcie pętli.
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(1), snapshot.ProgressFullSince);
        Assert.Equal(snapshot.ProgressFullSince, alert.StartedAt);
    }

    [Fact]
    public async Task RunAsync_ZrzutPostoju_ZapisujeDokladnieRazIDopieroPoUplywieProgu()
    {
        // Dlaczego: maszyna staje na 100 % po KAŻDEJ pracy (na ~1 minutę) — zapisywanie zrzutu przy każdym
        // 100 % dałoby setki zbędnych plików dziennie na 12 maszyn; ma powstać jeden, w chwili alertu.
        var source = new FakeScreenSource("aidite1", Frame(1.0));
        Harness h = Build(maxCycles: CykliPonadProg, source: source);
        int zrzutyPrzedProgiem = -1;
        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 100)
            {
                zrzutyPrzedProgiem = h.Captures.Saved.Count(s => s.Tag == "postoj");
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal(0, zrzutyPrzedProgiem);
        (string machineId, string tag, int width, int height, DateTimeOffset at) =
            Assert.Single(h.Captures.Saved, s => s.Tag == "postoj");
        Assert.Equal("aidite1", machineId);
        Assert.Equal("postoj", tag);
        Assert.Equal(Suma.Width / 2, width);
        Assert.Equal(Suma.Height / 2, height);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(121), at);
        Assert.Equal("captures/aidite1_postoj.png", Assert.Single(h.Alerts.ActiveAlerts).CaptureFile);
    }

    [Fact]
    public async Task RunAsync_ZrzutPostoju_PrzyKolejnychEpizodachWCiaguGodziny_ZapisujeSieTylkoRaz()
    {
        var pelny = Frame(1.0);
        var praca = Frame(0.4);
        var source = new FakeScreenSource("aidite1", pelny);
        Harness h = Build(maxCycles: 400, source: source);
        h.Driver.AfterCycle = cycle =>
        {
            // 0–130 s postój → zrzut; 131–140 s praca (epizod się kończy); od 141 s znów postój →
            // drugi epizod i drugi alert, ale wciąż w tej samej godzinie.
            if (cycle is > 130 and <= 140) source.Frame = praca;
            else source.Frame = pelny;
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        // Dwa epizody postoju (dwa alerty podniesione), ale tylko jeden zrzut pełnej klatki.
        Assert.Equal(2, h.Events.Count(e => e.Alert.Kind == AlertKind.MachineIdle && e.Kind == AlertEventKind.Raised));
        Assert.Single(h.Captures.Saved, s => s.Tag == "postoj");
    }

    [Fact]
    public async Task RunAsync_ZrzutPostoju_PrzyWlaczonychZrzutachDiagnostycznych_ZapisujeSieBezPomniejszania()
    {
        // Dlaczego: przy diagnostics.captureBreaks = true zbieramy materiał do diagnozy OCR pełnej klatki
        // gdzie liczy się każdy piksel — skalowanie 1:2 jest wtedy wyłączone.
        var source = new FakeScreenSource("aidite1", Frame(1.0));
        Harness h = Build(maxCycles: CykliPonadProg, source: source,
            diagnostics: new DiagnosticsConfig { CaptureBreaks = true });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        (string _, string _, int width, int height, DateTimeOffset _) = Assert.Single(h.Captures.Saved, s => s.Tag == "postoj");
        Assert.Equal(Suma.Width, width);
        Assert.Equal(Suma.Height, height);
    }

    [Fact]
    public async Task RunAsync_PaskaNa40Procent_NieTworzyAlertuPostoju()
    {
        // Dlaczego: maszyna w trakcie pracy (postęp 40 %) nie stoi — alert byłby fałszywy, a operator
        // przestałby ufać programowi.
        var source = new FakeScreenSource("aidite1", Frame(0.4));
        Harness h = Build(maxCycles: CykliPonadProg, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.DoesNotContain(h.Captures.Saved, s => s.Tag == "postoj");
        Assert.True(h.Monitor.Snapshot.IdleWatchEnabled);
        Assert.Equal(0.4, h.Monitor.Snapshot.LastProgressFraction, 2);
        Assert.False(h.Monitor.Snapshot.ProgressFullNow);
        Assert.False(h.Monitor.Snapshot.ProgressFullConfirmed);
        Assert.Null(h.Monitor.Snapshot.ProgressFullSince);
    }

    [Fact]
    public async Task RunAsync_GdyPasekPostepuLezyPozaEkranem_WylaczaObserwacjeOstrzegaRazIDalejWykrywaCzerwien()
    {
        // Dlaczego: źle ustawione idle.progressRoi to błąd konfiguracji, a nie powód, żeby stracić
        // najważniejszą funkcję programu (alert błędu) albo zasypać log tym samym ostrzeżeniem co sekundę.
        var source = new FakeScreenSource("aidite1", MalaKlatka(red: true));
        Harness h = Build(maxCycles: 62, source: source);   // 62 cykle = dwie pełne klatki (druga po 60 s)

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.False(h.Monitor.Snapshot.IdleWatchEnabled);
        LogEntry ostrzezenie = Assert.Single(h.Logs.Containing("idle.progressRoi"));
        Assert.Equal(LogLevel.Warning, ostrzezenie.Level);
        Assert.Contains("obserwacja postoju", ostrzezenie.Message, StringComparison.Ordinal);

        // Obszar pobierany wraca do samego paska statusu…
        Assert.All(source.RequestedRois, roi => Assert.Equal(Profile.StatusBarRoi, roi));

        // …a wykrywanie czerwieni działa dalej bez zmian.
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.True(h.Monitor.Snapshot.RedConfirmed);
        Assert.DoesNotContain(h.Captures.Saved, s => s.Tag == "postoj");
    }

    [Fact]
    public async Task RunAsync_ProfiluBezSekcjiIdle_ZachowujeSieDokladnieJakPrzedWprowadzeniemFunkcji()
    {
        // Dlaczego: regresja — profil bez sekcji „idle” ma pobierać ten sam ROI co dotąd i w ogóle nie
        // wchodzić w detekcję postoju, nawet gdy na ekranie akurat stoi pasek na 100 %.
        var source = new FakeScreenSource("aidite1", Frame(1.0));
        Harness h = Build(maxCycles: CykliPonadProg, source: source, profile: Profile with { Idle = null });

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.All(source.RequestedRois, roi => Assert.Equal(Profile.StatusBarRoi, roi));
        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Empty(h.Captures.Saved);
        Assert.Empty(h.Logs.Containing("idle.progressRoi"));

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.False(snapshot.IdleWatchEnabled);
        Assert.Equal(0.0, snapshot.LastProgressFraction);
        Assert.False(snapshot.ProgressFullNow);
        Assert.False(snapshot.ProgressFullConfirmed);
        Assert.Null(snapshot.ProgressFullSince);
    }

    [Fact]
    public async Task RunAsync_GdySerwerVncNieZglaszaZmianEkranu_NadalWykrywaPostoj()
    {
        // Dlaczego: stojąca maszyna ma NIERUCHOMY ekran, więc serwer WinCE/OpenCnc milczy na żądania
        // przyrostowe — gdyby brak odpowiedzi pomijał cykl detekcji, alert postoju nigdy by nie powstał
        // dokładnie w tej sytuacji, dla której funkcja istnieje.
        var source = new FakeScreenSource("aidite1", Frame(1.0)) { TryCaptureReturnsNull = true };
        Harness h = Build(maxCycles: CykliPonadProg, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(source.TryCaptureCount > 0);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.True(h.Monitor.Snapshot.ProgressFullConfirmed);
    }

    [Fact]
    public async Task RunAsync_PrzyPotwierdzeniu100Procent_PublikujeZmianeMigawki()
    {
        // Dlaczego: okno statusu odświeża kolumnę „Postęp” ze zdarzenia StatusChanged — bez niego operator
        // zobaczyłby „100 % od …” dopiero przy następnej zmianie czegokolwiek innego.
        var source = new FakeScreenSource("aidite1", Frame(1.0));
        Harness h = Build(maxCycles: 5, source: source);
        var snapshots = new List<MachineStatusSnapshot>();
        h.Monitor.StatusChanged += snapshots.Add;

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Contains(snapshots, s => s.ProgressFullConfirmed && s.ProgressFullSince is not null);
    }

    [Fact]
    public async Task RunAsync_GdyPasekPostepuJestPowyzejPaskaStatusu_ObieDetekcjeCzytajaWlasciweFragmenty()
    {
        ScreenProfile amm = ProfilZPostepemNadStatusem();
        RectInt status = amm.StatusBarRoi;
        RectInt progress = amm.Idle!.ProgressRoi;

        var source = new FakeScreenSource("amm580", KlatkaAmm(amm, red: true));
        Harness h = Build(maxCycles: CykliPonadProg, source: source, profile: amm);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.All(source.RequestedRois, roi => Assert.Equal(status.Union(progress), roi));
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.Equal(1.0, h.Monitor.Snapshot.LastProgressFraction, 3);
        Assert.True(h.Monitor.Snapshot.RedConfirmed);
        Assert.True(h.Monitor.Snapshot.ProgressFullConfirmed);
    }

    [Fact]
    public async Task RunAsync_GdyPasekPostepuJestPowyzejPaskaStatusu_BezCzerwieni_DajeAlertPostoju()
    {
        // Dlaczego: wariant testu wyżej BEZ czerwieni. Tam alert postoju jest wstrzymany priorytetem błędu,
        // więc sam tamten test nie dowodziłby już, że detekcja postoju czyta właściwy fragment tej
        // „odwróconej” geometrii (pasek statusu na dole, pasek postępu nad nim). Tutaj nic nie wstrzymuje
        // postoju: te same wycinki, ten sam pobierany obszar, a po 2 minutach na 100 % powstaje alert
        // MachineIdle — i to jest dowód, że przesunięcia działają też dla detekcji postępu.
        ScreenProfile amm = ProfilZPostepemNadStatusem();
        RectInt status = amm.StatusBarRoi;
        RectInt progress = amm.Idle!.ProgressRoi;

        var source = new FakeScreenSource("amm580", KlatkaAmm(amm, red: false));
        Harness h = Build(maxCycles: CykliPonadProg, source: source, profile: amm);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.All(source.RequestedRois, roi => Assert.Equal(status.Union(progress), roi));
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertKey.Idle("aidite1"), alert.Key);
        Assert.Equal(1.0, h.Monitor.Snapshot.LastProgressFraction, 3);
        Assert.False(h.Monitor.Snapshot.RedConfirmed);
    }

    [Fact]
    public async Task RunAsync_ZTejSamejKlatki_CzerwienMaPriorytet_AlertPostojuNiePowstaje()
    {
        var source = new FakeScreenSource("aidite1", Frame(1.0, red: true));
        Harness h = Build(maxCycles: CykliPonadProg, source: source);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert blad = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, blad.Kind);
        Assert.True(blad.Sound);
        // Alert postoju nie powstaje ANI RAZU (nie chodzi tylko o to, że go na końcu nie ma na liście).
        Assert.DoesNotContain(h.Events, e => e.Alert.Kind == AlertKind.MachineIdle);

        // Zrzut błędu obejmuje sam pasek statusu…
        (string _, string _, int bladW, int bladH, DateTimeOffset _) = Assert.Single(h.Captures.Saved, s => s.Tag == "blad");
        Assert.Equal(Profile.StatusBarRoi.Width, bladW);
        Assert.Equal(Profile.StatusBarRoi.Height, bladH);

        // …a zrzut „postoj” MIMO WSZYSTKO powstaje — to faktyczne zachowanie, zapisane tu świadomie:
        // monitor nic nie wie o alertach błędu (prowadzi własny licznik „100 % nieprzerwanie od” i po
        // przekroczeniu progu zapisuje jeden zrzut całego pobieranego obszaru), a priorytet błędu jest
        // realizowany dopiero w AlertStateMachine. Kosztuje to jeden zbędny plik na epizod; żaden alert
        // postoju się z nim nie wiąże, więc operator nigdy go nie zobaczy w pop-upie.
        (string _, string _, int postojW, int postojH, DateTimeOffset _) = Assert.Single(h.Captures.Saved, s => s.Tag == "postoj");
        Assert.Equal(Suma.Width / 2, postojW);
        Assert.Equal(Suma.Height / 2, postojH);

        // Obie detekcje działają — wstrzymany jest wyłącznie ALERT postoju, nie wykrywanie 100 %.
        Assert.True(h.Monitor.Snapshot.RedConfirmed);
        Assert.True(h.Monitor.Snapshot.ProgressFullConfirmed);
    }

    /// <summary>
    /// Klatka dla profilu amm580 („odwrócona” geometria: pasek statusu na DOLE ekranu, pasek postępu nad
    /// nim) — pasek postępu wypełniony w całości, pasek statusu z czerwonym tekstem błędu (gdy
    /// <paramref name="red"/>) albo zwykłym białym.
    /// </summary>
    private static RgbaImage KlatkaAmm(ScreenProfile amm, bool red)
    {
        RectInt status = amm.StatusBarRoi;
        RectInt progress = amm.Idle!.ProgressRoi;

        var frame = new RgbaImage(1024, 768);
        frame.Fill(SyntheticScreens.DarkBackground);
        frame.Blit(
            SyntheticScreens.StatusBar(
                status.Width, status.Height, SyntheticScreens.DarkBackground,
                red ? SyntheticScreens.RedText : SyntheticScreens.WhiteText, red ? "ciemny-blad" : "ciemny-ok"),
            status.X, status.Y);

        for (int x = 0; x < progress.Width; x++)
        {
            for (int y = 0; y < progress.Height; y++)
            {
                frame.SetPixel(progress.X + x, progress.Y + y, Green);
            }
        }

        return frame;
    }
}
