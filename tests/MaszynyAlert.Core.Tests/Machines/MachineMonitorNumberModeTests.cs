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
/// Obserwacja „maszyna stoi bez pracy – dołóż prace” w pętli obserwatora w DOMYŚLNYM trybie liczbowym
/// (<see cref="ProgressMode.Number"/>): postęp jest liczbą procent odczytywaną przez OCR, więc dochodzą
/// tu rzeczy, których tryb paska (<see cref="MachineMonitorIdleTests"/>) nie ma — oszczędzanie silnika
/// OCR pamięcią podręczną, wyłączenie obserwacji przy braku silnika oraz współistnienie dwóch odczytów
/// OCR (treść błędu i procent postępu) z tej samej klatki.
/// </summary>
public sealed class MachineMonitorNumberModeTests
{
    /// <summary>Profil z przykładowej konfiguracji (tryb „number”): pasek statusu i obszar liczby procent zmierzone (ramka komunikatu u góry po prawej, liczba procent obok „Milling Status”).</summary>
    private static readonly ScreenProfile Profile = TestPaths.Profile("opencnc") with { MainScreen = null };

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

    /// <summary>Ile cykli pętli (po 1 s) trzeba, żeby przekroczyć próg postoju (2 min) z zapasem.</summary>
    private const int CykliPonadProg = 130;

    /// <summary>Ile cykli pętli (po 1 s) trzeba, żeby przekroczyć próg postoju na 0 % (3 min) z zapasem.</summary>
    private const int CykliPonadProgZera = 190;

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
    /// Pełna klatka ekranu maszyny (rozmiar liczony z profilu — mieści obszar liczby procent) z paskiem statusu
    /// u góry (czerwony tekst błędu, gdy <paramref name="red"/>) i „liczbą procent” narysowaną w
    /// <c>idle.progressRoi</c>. Treść liczby nie ma znaczenia — tekst odczytu podaje atrapa OCR; liczą się
    /// BAJTY tego obszaru (pamięć podręczna czytnika) i to, że są stałe między cyklami.
    /// </summary>
    private static RgbaImage Frame(bool red = false)
    {
        var frame = new RgbaImage(SzerokoscKlatki, WysokoscKlatki);
        frame.Fill(SyntheticScreens.LightBackground);

        RectInt status = Profile.StatusBarRoi;
        frame.Blit(
            SyntheticScreens.StatusBar(
                status.Width, status.Height, SyntheticScreens.LightBackground,
                red ? SyntheticScreens.RedText : SyntheticScreens.WhiteText, red ? "blad" : "ok"),
            status.X, status.Y);

        RectInt p = Profile.Idle!.ProgressRoi;
        frame.Blit(
            SyntheticScreens.StatusBar(
                p.Width, p.Height, SyntheticScreens.Light2Background, SyntheticScreens.BlackText, "100%", coverage: 0.15),
            p.X, p.Y);

        return frame;
    }

    /// <summary>Atrapa OCR zwracająca ZAWSZE ten sam tekst (odczyt procentu jest w tych testach jedynym).</summary>
    private static FakeOcrEngine Ocr(string tekst) =>
        new() { DefaultResult = new OcrResult(tekst, [tekst]) };

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

    private static Harness Build(int maxCycles, FakeScreenSource source, FakeOcrEngine ocr, ScreenProfile? profile = null)
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
            ocr,
            new MessageMapper(TestPaths.ExampleRules),
            alerts,
            clock,
            timing,
            captures,
            logs.CreateLogger("MachineMonitor"),
            driver.Delay);

        return new Harness(monitor, driver, clock, alerts, captures, logs, events);
    }

    [Fact]
    public async Task RunAsync_GdyOcrCzyta100ProcentDluzejNizProg_TworzyAlertPostojuIWypelniaMigawke()
    {
        // Dlaczego: to jest cała funkcja w domyślnym trybie widziana od strony pętli obserwatora — liczba
        // „100%” stojąca ponad 2 minuty ma dać alert MachineIdle, a migawka ma nieść odczytany procent
        // i surowy tekst OCR (z nich okno statusu buduje kolumnę „Postęp”).
        var source = new FakeScreenSource("aidite1", Frame());
        Harness h = Build(maxCycles: CykliPonadProg, source: source, ocr: Ocr("100%"));

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertKey.Idle("aidite1"), alert.Key);
        Assert.False(alert.Sound);

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.True(snapshot.IdleWatchEnabled);
        Assert.Equal(100, snapshot.LastProgressPercent);
        Assert.Equal("100%", snapshot.LastProgressText);
        Assert.Equal(1.0, snapshot.LastProgressFraction, 6);
        Assert.True(snapshot.ProgressFullNow);
        Assert.True(snapshot.ProgressFullConfirmed);
        // Potwierdzenie „2 z 3” zapada w drugim cyklu, czyli sekundę po starcie pętli.
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(1), snapshot.ProgressFullSince);
        Assert.Equal(snapshot.ProgressFullSince, alert.StartedAt);
    }

    [Fact]
    public async Task RunAsync_GdyOcrCzyta45Procent_NieTworzyAlertuPostoju()
    {
        // Dlaczego: maszyna w trakcie pracy (45 %) nie stoi — alert byłby fałszywy, a operator przestałby
        // ufać programowi; procent ma jednak trafić do migawki, bo widać go w kolumnie „Postęp”.
        var source = new FakeScreenSource("aidite1", Frame());
        Harness h = Build(maxCycles: CykliPonadProg, source: source, ocr: Ocr("45%"));

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.DoesNotContain(h.Captures.Saved, s => s.Tag == "postoj");

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.True(snapshot.IdleWatchEnabled);
        Assert.Equal(45, snapshot.LastProgressPercent);
        Assert.Equal(0.45, snapshot.LastProgressFraction, 6);
        Assert.False(snapshot.ProgressFullNow);
        Assert.False(snapshot.ProgressFullConfirmed);
        Assert.Null(snapshot.ProgressFullSince);
    }

    [Fact]
    public async Task RunAsync_GdyOcrNicNieOdczytal_NieTworzyAlertuAObserwacjaDzialaDalej()
    {
        // Dlaczego: nieczytelny odczyt (złe progressRoi, za mała czcionka) ma być WIDOCZNY jako „nie wiem”
        // (LastProgressPercent = null → „?” w kolumnie „Postęp”), a nie zamieniać się w 0 % ani — co gorsza —
        // w cichy alert postoju. Obserwacja zostaje włączona, bo następny cykl może już odczytać liczbę.
        var source = new FakeScreenSource("aidite1", Frame());
        Harness h = Build(maxCycles: CykliPonadProg, source: source, ocr: new FakeOcrEngine());

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.DoesNotContain(h.Captures.Saved, s => s.Tag == "postoj");

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.True(snapshot.IdleWatchEnabled);
        Assert.Null(snapshot.LastProgressPercent);
        Assert.Equal(0.0, snapshot.LastProgressFraction);
        Assert.False(snapshot.ProgressFullConfirmed);
    }

    [Fact]
    public async Task RunAsync_GdySilnikOcrJestNiedostepny_WylaczaObserwacjePostojuOstrzegaRazIDalejWykrywaCzerwien()
    {
        // Dlaczego: tryb liczbowy bez OCR nie ma z czego odczytać postępu — zamiast cichego „zawsze 0 %”
        // (które po 2 minutach dałoby fałszywy alert) obserwacja ma być wyłączona jak przy profilu bez
        // sekcji „idle”, z jednym ostrzeżeniem w logu. Najważniejsza funkcja programu (alert błędu) musi
        // działać dalej bez zmian.
        var source = new FakeScreenSource("aidite1", Frame(red: true));
        var ocr = new FakeOcrEngine { IsAvailable = false, Name = "brak" };
        Harness h = Build(maxCycles: 62, source: source, ocr: ocr);   // 62 cykle = dwie pełne klatki (druga po 60 s)

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.False(h.Monitor.Snapshot.IdleWatchEnabled);
        LogEntry ostrzezenie = Assert.Single(h.Logs.Containing("wymaga silnika OCR"));
        Assert.Equal(LogLevel.Warning, ostrzezenie.Level);
        Assert.Contains("obserwacja postoju", ostrzezenie.Message, StringComparison.Ordinal);
        Assert.Equal(0, ocr.CallCount);

        // Obszar pobierany to sam pasek statusu (nie ma po co pobierać obszaru liczby procent)…
        Assert.NotEmpty(source.RequestedRois);
        Assert.All(source.RequestedRois, roi => Assert.Equal(Profile.StatusBarRoi, roi));

        // …a wykrywanie czerwieni działa dalej bez zmian.
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.True(h.Monitor.Snapshot.RedConfirmed);
        Assert.DoesNotContain(h.Captures.Saved, s => s.Tag == "postoj");
    }

    [Fact]
    public async Task RunAsync_GdyEkranSieNieZmienia_WolaOcrRazZamiastWKazdymCyklu()
    {
        // Dlaczego: stojąca maszyna pokazuje piksel w piksel tę samą liczbę co sekundę — bez pamięci
        // podręcznej czytnika byłoby to 12 wywołań OCR na sekundę (po jednym na maszynę) przez cały postój.
        var source = new FakeScreenSource("aidite1", Frame());
        FakeOcrEngine ocr = Ocr("100%");
        Harness h = Build(maxCycles: CykliPonadProg, source: source, ocr: ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Single(h.Alerts.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
        Assert.InRange(ocr.CallCount, 2, 3); // odświeżenie poprawnego odczytu co 60 s
    }

    [Fact]
    public async Task RunAsync_ZTejSamejKlatki_CzerwienMaPriorytet_AleOdczytyOcrNieKoliduja()
    {
        var source = new FakeScreenSource("aidite1", Frame(red: true));
        var ocr = new FakeOcrEngine();
        ocr.Enqueue("100%").Enqueue("Failed to grab a tray");
        ocr.DefaultResult = new OcrResult("100%", ["100%"]); // okresowa kontrola nieruchomego obrazu
        Harness h = Build(maxCycles: CykliPonadProg, source: source, ocr: ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert blad = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.Error, blad.Kind);
        Assert.True(blad.Sound);
        Assert.Equal("Tacka nie pobrana", blad.ShortText);
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

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.True(snapshot.RedConfirmed);
        Assert.True(snapshot.ProgressFullConfirmed);
        Assert.Equal(100, snapshot.LastProgressPercent);
        Assert.Equal("Tacka nie pobrana", snapshot.LastShortText);
        // Odczyty OCR obejmują także okresowe odświeżenia: procent (raz, potem z pamięci podręcznej) i treść
        // błędu (raz — dopasowana reguła kończy próby). Priorytet błędu niczego tu nie zmienia: odczyt
        // procentu leci dalej normalnie, wstrzymany jest tylko ALERT postoju.
        Assert.InRange(ocr.CallCount, 3, 4); // procent odświeżany co 60 s plus opis błędu
    }

    [Fact]
    public async Task RunAsync_PostojPotemBladPotemNaprawa_ZamykaPostojPrzyBledzieIOdliczaDwieMinutyOdNowa()
    {
        var source = new FakeScreenSource("aidite1", Frame());
        var ocr = new FakeOcrEngine();
        ocr.DefaultResult = new OcrResult("100%", ["100%"]); // także okresowe kontrole świeżości
        Harness h = Build(maxCycles: 320, source: source, ocr: ocr);

        // Bajty obszaru liczby procent są w obu klatkach identyczne (czerwień dotyczy tylko paska statusu),
        // więc podmiana klatki nie unieważnia pamięci podręcznej czytnika procentu — postęp to nadal 100 %.
        RgbaImage zBledem = Frame(red: true);
        RgbaImage bezBledu = Frame();

        var idlePodczasBledu = new List<int>();
        h.Driver.AfterCycle = cycle =>
        {
            // Tick woła w prawdziwym programie AppOrchestrator (monitor sam go nie woła) — bez niego epizod
            // błędu nigdy by się nie zamknął, więc krok 3 nie miałby jak zajść.
            h.Alerts.Tick();

            switch (cycle)
            {
                case 130:
                    ocr.Enqueue("Failed to grab a tray");
                    source.Frame = zBledem;     // krok 2: pojawia się czerwony błąd
                    break;
                case 155:
                    idlePodczasBledu.Add(h.Alerts.ActiveAlerts.Count(a => a.Kind == AlertKind.MachineIdle));
                    break;
                case 160:
                    source.Frame = bezBledu;    // krok 3: błąd naprawiony, ekran znów czysty
                    break;
                case 290:
                    // 130 s po zniknięciu czerwieni: błąd już zamknięty, ale 2 minuty postoju jeszcze nie
                    // minęły od ZAMKNIĘCIA błędu — pop-up „dołóż prace” nie może tu jeszcze wrócić.
                    idlePodczasBledu.Add(h.Alerts.ActiveAlerts.Count(a => a.Kind == AlertKind.MachineIdle));
                    break;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        // W trakcie błędu i tuż po jego zamknięciu nie ma żadnego alertu postoju.
        Assert.Equal([0, 0], idlePodczasBledu);

        // Kolejność zdarzeń: postój → błąd → zamknięcie postoju → zamknięcie błędu → NOWY postój.
        List<(AlertEventKind Kind, AlertKind Rodzaj)> kolejnosc =
            h.Events.Where(e => e.Kind != AlertEventKind.Updated).Select(e => (e.Kind, e.Alert.Kind)).ToList();
        Assert.Equal(
            [
                (AlertEventKind.Raised, AlertKind.MachineIdle),
                (AlertEventKind.Raised, AlertKind.Error),
                (AlertEventKind.Closed, AlertKind.MachineIdle),
                (AlertEventKind.Closed, AlertKind.Error),
                (AlertEventKind.Raised, AlertKind.MachineIdle),
            ],
            kolejnosc);

        // Zamknięcie postoju jedzie w TEJ SAMEJ publikacji co powstanie błędu (ten sam znacznik czasu).
        AlertEvent bladPowstal = h.Events[1];
        AlertEvent postojZamkniety = h.Events[2];
        Assert.Equal(bladPowstal.Alert.StartedAt, postojZamkniety.Alert.ClosedAt);

        // Nowy postój to NOWY epizod (Raised, nie Updated — pamięć potwierdzeń zapomniała klucz), a jego
        // „od HH:MM” liczy się od chwili zamknięcia błędu, nie od pierwszego 100 % sprzed błędu.
        AlertEvent nowyPostoj = h.Events.Where(e => e.Kind != AlertEventKind.Updated).ElementAt(4);
        AlertEvent bladZamkniety = h.Events.Where(e => e.Kind != AlertEventKind.Updated).ElementAt(3);
        Assert.Equal(bladZamkniety.Alert.ClosedAt, nowyPostoj.Alert.StartedAt);
        Assert.Equal(
            TimeSpan.FromMilliseconds(new TimingConfig().IdleAlertAfterMs),
            nowyPostoj.Alert.LastSeenAt - nowyPostoj.Alert.StartedAt);

        Alert aktywny = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, aktywny.Kind);
        Assert.Equal(AlertStatus.Active, aktywny.Status);
    }

    [Fact]
    public async Task RunAsync_ZrzutPostoju_ZapisujeDokladnieRazIDopieroPoUplywieProgu()
    {
        // Dlaczego: maszyna staje na 100 % po KAŻDEJ pracy (na ~1 minutę) — zapisywanie zrzutu przy każdym
        // 100 % dałoby setki zbędnych plików dziennie na 12 maszyn; ma powstać jeden, w chwili alertu.
        var source = new FakeScreenSource("aidite1", Frame());
        Harness h = Build(maxCycles: CykliPonadProg, source: source, ocr: Ocr("100%"));
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
    public async Task RunAsync_GdySerwerVncNieZglaszaZmianEkranu_NadalWykrywaPostoj()
    {
        // Dlaczego: stojąca maszyna ma NIERUCHOMY ekran, więc serwer WinCE/OpenCnc milczy na żądania
        // przyrostowe — obserwator analizuje wtedy ostatni znany obraz. Odczyt procentu musi to przeżyć
        // (i skorzystać z pamięci podręcznej, bo bajty są te same), inaczej alert postoju nigdy by nie
        // powstał dokładnie w tej sytuacji, dla której funkcja istnieje.
        var source = new FakeScreenSource("aidite1", Frame()) { TryCaptureReturnsNull = true };
        FakeOcrEngine ocr = Ocr("100%");
        Harness h = Build(maxCycles: CykliPonadProg, source: source, ocr: ocr);

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.True(source.TryCaptureCount > 0);
        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.True(h.Monitor.Snapshot.ProgressFullConfirmed);
        Assert.Equal(100, h.Monitor.Snapshot.LastProgressPercent);
    }

    [Fact]
    public async Task RunAsync_PrzyPotwierdzeniu100Procent_PublikujeZmianeMigawki()
    {
        // Dlaczego: okno statusu odświeża kolumnę „Postęp” ze zdarzenia StatusChanged — bez niego operator
        // zobaczyłby „100 % od …” dopiero przy następnej zmianie czegokolwiek innego. W trybie liczbowym
        // publikacji wymaga też sama zmiana odczytanego procentu (np. „-” → „100 %”).
        var source = new FakeScreenSource("aidite1", Frame());
        Harness h = Build(maxCycles: 5, source: source, ocr: Ocr("100%"));
        var snapshots = new List<MachineStatusSnapshot>();
        h.Monitor.StatusChanged += snapshots.Add;

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Contains(snapshots, s => s.LastProgressPercent == 100);
        Assert.Contains(snapshots, s => s.ProgressFullConfirmed && s.ProgressFullSince is not null);
    }

    [Fact]
    public async Task RunAsync_GdyOcrCzytaZeroDluzejNizProg_TworzyAlertWcisnijStartIZrzutPostoju()
    {
        var source = new FakeScreenSource("aidite1", Frame());
        Harness h = Build(maxCycles: CykliPonadProgZera, source: source, ocr: Ocr("0%"));

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertKey.Idle("aidite1"), alert.Key);
        Assert.Equal("postoj-0", alert.Category);
        Assert.Equal("Stoi na 0 % – wciśnij Start", alert.ShortText);
        Assert.False(alert.Sound);
        Assert.Contains(h.Captures.Saved, c => c.Tag == "postoj");

        MachineStatusSnapshot snapshot = h.Monitor.Snapshot;
        Assert.Equal(0, snapshot.LastProgressPercent);
        Assert.False(snapshot.ProgressFullConfirmed);
        Assert.Null(snapshot.ProgressFullSince);
        // Potwierdzenie „2 z 3” zapada w drugim cyklu, czyli sekundę po starcie pętli.
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(1), snapshot.ProgressZeroSince);
        Assert.Equal(snapshot.ProgressZeroSince, alert.StartedAt);
    }

    [Fact]
    public async Task RunAsync_GdyZeroKrocejNizProg_NieTworzyAlertu()
    {
        // Dlaczego: 0 % to również normalny początek nowej pracy z kolejki — przez pierwsze 3 minuty
        // obserwator ma milczeć, inaczej każdy zwykły rozruch dawałby fałszywe „wciśnij Start”.
        var source = new FakeScreenSource("aidite1", Frame());
        Harness h = Build(maxCycles: 150, source: source, ocr: Ocr("0%"));   // 150 s < 180 s progu

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.DoesNotContain(h.Captures.Saved, c => c.Tag == "postoj");
        Assert.Equal(0, h.Monitor.Snapshot.LastProgressPercent);
        Assert.NotNull(h.Monitor.Snapshot.ProgressZeroSince);
    }

    [Fact]
    public async Task RunAsync_GdyPoDwochMinutachNaZerzeMaszynaRuszy_LicznikLiczyOdNowa()
    {
        // Dlaczego: pierwszy zafrezowany procent dowodzi, że ktoś wcisnął Start. Po nim próg musi ruszyć od
        // zera — inaczej maszyna, która właśnie zaczęła pracę, dostawałaby alert po chwili stania na 3 %.
        var source = new FakeScreenSource("aidite1", Frame());
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult("0%", ["0%"]) };
        Harness h = Build(maxCycles: 300, source: source, ocr: ocr);

        var stanPoStarcie = new List<int>();
        h.Driver.AfterCycle = cycle =>
        {
            switch (cycle)
            {
                case 120:
                    // 2 minuty na 0 % — próg 3 minut jeszcze nie minął, więc alertu być nie może.
                    stanPoStarcie.Add(h.Alerts.ActiveAlerts.Count);
                    ocr.DefaultResult = new OcrResult("3%", ["3%"]);   // operator wcisnął Start
                    break;
                case 260:
                    // 140 s pracy po starcie: licznik 0 % wyzerowany, migawka bez „0 % od …”.
                    stanPoStarcie.Add(h.Alerts.ActiveAlerts.Count);
                    break;
            }
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Assert.Equal([0, 0], stanPoStarcie);
        Assert.Empty(h.Alerts.ActiveAlerts);
        Assert.Equal(3, h.Monitor.Snapshot.LastProgressPercent);
        Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
    }

    [Fact]
    public async Task RunAsync_GdyOdczytStanieSieNieczytelny_NieZamykaAlertuNaZerze()
    {
        // Dlaczego: nieczytelny odczyt OCR nie dowodzi, że maszyna ruszyła (zamknięcie epizodu wymaga
        // rozstrzygniętego odczytu 1–99 %). Alert „wciśnij Start” ma przetrwać chwilową awarię odczytu,
        // tak samo jak alert postoju na 100 %.
        var source = new FakeScreenSource("aidite1", Frame());
        var ocr = new FakeOcrEngine { DefaultResult = new OcrResult("0%", ["0%"]) };
        Harness h = Build(maxCycles: 260, source: source, ocr: ocr);

        h.Driver.AfterCycle = cycle =>
        {
            if (cycle == 200) ocr.DefaultResult = OcrResult.Empty;   // OCR przestaje cokolwiek czytać
        };

        await h.Monitor.RunAsync(h.Driver.Cts.Token);

        Alert alert = Assert.Single(h.Alerts.ActiveAlerts);
        Assert.Equal("postoj-0", alert.Category);
        Assert.Null(h.Monitor.Snapshot.LastProgressPercent);
        Assert.Null(h.Monitor.Snapshot.ProgressZeroSince);
    }
}
