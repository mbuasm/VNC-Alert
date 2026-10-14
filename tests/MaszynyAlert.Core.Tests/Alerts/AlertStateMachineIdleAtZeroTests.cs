using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachineIdleAtZeroTests
{
    /// <summary>Treść alertu „stoi na 0 %” — musi być identyczna jak stała w <see cref="AlertStateMachine"/>.</summary>
    private const string ZeroText = "Stoi na 0 % – wciśnij Start";

    private const string IdleText = "Stoi bez pracy (100 %) – dołóż prace";

    private static readonly TimingConfig Timing = new();

    /// <summary>Próg „stoi na 0 %” (domyślnie 3 minuty).</summary>
    private static readonly TimeSpan ProgZera = TimeSpan.FromMilliseconds(Timing.IdleAtZeroAfterMs);

    /// <summary>Próg „stoi na 100 %” (domyślnie 2 minuty).</summary>
    private static readonly TimeSpan ProgPelny = TimeSpan.FromMilliseconds(Timing.IdleAlertAfterMs);

    private static MachineConfig Machine(string id = "aidite1", string name = "Aidite 1") => new()
    {
        Id = id,
        Name = name,
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
    };

    private static MappedMessage TrayMessage() =>
        new("tray-grab", "Nie udało się pobrać tacki", "tacka", "Failed to grab a tray", "FAILED TO GRAB A TRAY", true);

    private sealed record Fixture(FakeClock Clock, AckMemory Acks, AlertStateMachine Machine, List<AlertEvent> Events);

    private static Fixture NewFixture(TimingConfig? timing = null)
    {
        var clock = new FakeClock();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var sm = new AlertStateMachine(clock, timing ?? Timing, acks);
        var events = new List<AlertEvent>();
        sm.Changed += events.Add;
        return new Fixture(clock, acks, sm, events);
    }

    /// <summary>Podnosi alert „stoi na 0 %”: pierwsze 0 %, upływ progu, drugie 0 % (jak kolejne cykle obserwatora).</summary>
    private static Alert RaiseZero(Fixture f, string? captureFile = null)
    {
        f.Machine.OnProgressState(Machine(), ProgressState.Zero, captureFile);
        f.Clock.Advance(ProgZera);
        f.Machine.OnProgressState(Machine(), ProgressState.Zero, captureFile);
        return Assert.Single(f.Machine.ActiveAlerts, a => a.Kind == AlertKind.MachineIdle);
    }

    [Fact]
    public void OnProgressState_ZeroKrocejNizProg_NieTworzyAlertu()
    {
        // Dlaczego: 0 % jest też NORMALNYM początkiem każdej nowej pracy z kolejki (maszyna sama ją
        // startuje i pokazuje 0 %, dopóki nie zafrezuje pierwszego procenta) — alert bez progu czasu
        // dzwoniłby przy każdym zwykłym rozruchu.
        Fixture f = NewFixture();

        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        f.Clock.Advance(ProgZera - TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnProgressState_PoTrzechMinutachNaZerze_TworzyAlertWcisnijStartBezDzwieku()
    {
        // Dlaczego: maszyna z wczytaną pracą, której nikt nie zaczął, stoi tak
        // samo jak maszyna bez pracy, tylko operator ma zrobić co innego (wcisnąć Start, a nie dołożyć pracę).
        Fixture f = NewFixture();
        DateTimeOffset poczatek = f.Clock.UtcNow;

        Alert alert = RaiseZero(f, "captures/aidite1_postoj.png");

        Assert.Equal(AlertKind.MachineIdle, alert.Kind);
        Assert.Equal(AlertKey.Idle("aidite1"), alert.Key);
        Assert.Equal(AlertStatus.Active, alert.Status);
        Assert.Equal(ZeroText, alert.ShortText);
        Assert.Null(alert.FullText);
        Assert.Equal("postoj-0", alert.Category);
        Assert.Equal(poczatek, alert.StartedAt);
        Assert.Equal("Aidite 1", alert.MachineName);
        Assert.Equal("captures/aidite1_postoj.png", alert.CaptureFile);
        Assert.False(alert.Sound);

        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Raised, e.Kind);
    }

    [Fact]
    public void OnProgressState_WlasnyProgZKonfiguracji_JestRespektowany()
    {
        var timing = new TimingConfig { IdleAtZeroAfterMs = 600000 };
        Fixture f = NewFixture(timing);

        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        f.Clock.Advance(TimeSpan.FromMinutes(9));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Equal(ZeroText, Assert.Single(f.Machine.ActiveAlerts).ShortText);
    }

    [Fact]
    public void OnProgressState_GdyMaszynaRuszy_ZamykaAlertWcisnijStartNatychmiast()
    {
        // Dlaczego: pierwszy zafrezowany procent jest jednoznacznym dowodem, że ktoś wcisnął Start —
        // pop-up ma zgasnąć w tym samym cyklu, a nie po jakimkolwiek czasie oczekiwania.
        Fixture f = NewFixture();
        Alert alert = RaiseZero(f);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.Machine.OnProgressState(Machine(), ProgressState.Running);

        Assert.Empty(f.Machine.ActiveAlerts);
        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Closed, e.Kind);
        Assert.Equal(alert.Key, e.Alert.Key);
        Assert.Contains(f.Machine.RecentHistory, a => a.Key == alert.Key && a.Status == AlertStatus.Closed);
    }

    [Fact]
    public void OnProgressState_PoOtwartymPostoju100Procent_ZeroZamykaEpizodIOdliczaWlasnyProgOdNowa()
    {
        // Dlaczego: 0 % po 100 % pojawia się dokładnie wtedy, gdy maszyna wczytała kolejną pracę i ruszyła.
        // Gdyby alert tylko zmienił treść, operator zobaczyłby „wciśnij Start” na maszynie, która właśnie
        // zaczęła frezować. Epizod „dołóż prace” kończy się, a „wciśnij Start” może powstać dopiero po
        // pełnym idleAtZeroAfterMs bez ruchu.
        Fixture f = NewFixture();
        f.Machine.OnProgressState(Machine(), ProgressState.Full);
        f.Clock.Advance(ProgPelny);
        f.Machine.OnProgressState(Machine(), ProgressState.Full);
        Alert postoj100 = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(IdleText, postoj100.ShortText);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(5));
        DateTimeOffset poczatekZera = f.Clock.UtcNow;
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);

        f.Events.Clear();
        f.Clock.Advance(ProgZera - TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Alert nowy = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(ZeroText, nowy.ShortText);
        Assert.Equal("postoj-0", nowy.Category);
        Assert.Equal(poczatekZera, nowy.StartedAt);
        Assert.Equal(AlertEventKind.Raised, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnProgressState_PoOtwartymPostojuNaZerze_StoProcentOdliczaWlasnyProgOdNowa()
    {
        // Dlaczego: sytuacja odwrotna — praca jednak ruszyła i doszła do końca. Alert „wciśnij Start”
        // przestaje być prawdziwy od razu, a „dołóż prace” ma powstać dopiero po własnym progu.
        Fixture f = NewFixture();
        RaiseZero(f);
        f.Events.Clear();

        DateTimeOffset poczatekPelnego = f.Clock.UtcNow;
        f.Machine.OnProgressState(Machine(), ProgressState.Full);
        Assert.Empty(f.Machine.ActiveAlerts);
        Assert.Equal(AlertEventKind.Closed, Assert.Single(f.Events).Kind);

        f.Events.Clear();
        f.Clock.Advance(ProgPelny);
        f.Machine.OnProgressState(Machine(), ProgressState.Full);

        Alert nowy = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(IdleText, nowy.ShortText);
        Assert.Equal("postoj", nowy.Category);
        Assert.Equal(poczatekPelnego, nowy.StartedAt);
    }

    [Fact]
    public void OnProgressState_PrzyOtwartymBledzie_LicznikZeraNieBiegnie()
    {
        // Dlaczego: podczas czerwonego błędu maszyna stoi na 0 % Z POWODU TEGO BŁĘDU — komunikat
        // „wciśnij Start” byłby wtedy myleniem operatora, który ma iść naprawić maszynę.
        Fixture f = NewFixture();
        Alert blad = f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Events.Clear();

        for (int i = 0; i < 12; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(30));   // 6 minut, czyli dwa progi 0 %
            f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        }

        Assert.Empty(f.Events);
        Alert pozostaly = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(blad.Key, pozostaly.Key);
        Assert.Equal(AlertKind.Error, pozostaly.Kind);
    }

    [Fact]
    public void OnProgressState_PoZamknieciuBledu_LicznikZeraStartujeOdZera()
    {
        // Dlaczego: Po naprawie alert błędu zamyka się
        // (EpisodeCloseAfterMs bez czerwieni), a dopiero od tego momentu liczy się 3 minuty na 0 % —
        // pop-up „wciśnij Start” ma pojawić się ok. 30 s + 3 min po zniknięciu czerwieni, nie wcześniej.
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), TrayMessage(), null);
        f.Clock.Advance(TimeSpan.FromMinutes(4));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);   // przez cały błąd maszyna stoi na 0 %

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromMilliseconds(Timing.EpisodeCloseAfterMs));
        f.Machine.Tick();
        Assert.Empty(f.Machine.ActiveAlerts);
        f.Events.Clear();

        DateTimeOffset poNaprawie = f.Clock.UtcNow;
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        f.Clock.Advance(ProgZera - TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        Assert.Empty(f.Machine.ActiveAlerts);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Alert alert = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(ZeroText, alert.ShortText);
        Assert.Equal(poNaprawie, alert.StartedAt);
    }

    [Fact]
    public void OnProgressUnknown_ZerujeLicznikZeraAleNieZamykaIstniejacegoAlertu()
    {
        // Dlaczego: nieczytelny odczyt OCR nie dowodzi, że maszyna ruszyła — istniejący alert ma przetrwać,
        // a nowy nie może powstać na podstawie próbek sprzed przerwy w odczycie.
        Fixture f = NewFixture();
        Alert alert = RaiseZero(f);
        f.Events.Clear();

        f.Machine.OnProgressUnknown("aidite1");

        Assert.Equal(alert.Key, Assert.Single(f.Machine.ActiveAlerts).Key);
        Assert.Empty(f.Events);

        // Po nieczytelnym odczycie licznik biegnie od nowa także dla nowego epizodu.
        f.Machine.OnProgressState(Machine(), ProgressState.Running);
        f.Machine.OnProgressUnknown("aidite1");
        f.Clock.Advance(ProgZera);
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Empty(f.Machine.ActiveAlerts);
    }

    [Fact]
    public void OnProgressState_PrzyKolejnychCyklachNaZerze_TylkoOdswiezaAlert()
    {
        // Dlaczego: trwający postój nie może dzwonić w kółko — jeden epizod to jedno powiadomienie.
        Fixture f = NewFixture();
        Alert alert = RaiseZero(f);
        f.Events.Clear();

        for (int i = 0; i < 5; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Machine.OnProgressState(Machine(), ProgressState.Zero);
        }

        Assert.Empty(f.Events);
        Alert odswiezony = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(alert.StartedAt, odswiezony.StartedAt);
        Assert.Equal(f.Clock.UtcNow, odswiezony.LastSeenAt);
    }

    [Fact]
    public void OnProgressState_PoPotwierdzeniu_AlertZostajePotwierdzonyIMilczy()
    {
        // Dlaczego: „✕ Skasuj” ma działać tak samo jak przy postoju na 100 % — potwierdzony alert znika
        // z pop-upu i z karty, ale nie wraca jako nowy przy kolejnych cyklach tego samego postoju.
        Fixture f = NewFixture();
        Alert alert = RaiseZero(f);
        Assert.True(f.Machine.AcknowledgeLocal(alert.Key, "PC-1", "operator"));
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        f.Machine.OnProgressState(Machine(), ProgressState.Zero);

        Assert.Empty(f.Events);
        Alert nadal = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertStatus.Acknowledged, nadal.Status);
        Assert.Equal(ZeroText, nadal.ShortText);
    }

    [Fact]
    public void OnProgressState_StaraSygnaturaZFlagaAtFull_DzialaJakPrzedZmiana()
    {
        // Dlaczego: nakładka `atFull` zostaje dla ścieżek bez odczytu procentu (i dla istniejących testów) —
        // false znaczy „maszyna pracuje”, a nie „stoi na 0 %”.
        Fixture f = NewFixture();
        RaiseZero(f);

        f.Machine.OnProgressState(Machine(), atFull: false);

        Assert.Empty(f.Machine.ActiveAlerts);
    }
}
