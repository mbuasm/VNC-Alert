using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertStateMachinePriorityTests
{
    private static readonly TimingConfig Timing = new();

    private static MachineConfig Machine(string id = "aidite1", string name = "Aidite 1") => new()
    {
        Id = id,
        Name = name,
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
    };

    /// <summary>Komunikat dopasowany do reguły o zadanym priorytecie (jak zwraca <c>MessageMapper</c>).</summary>
    private static MappedMessage Rule(string ruleId, string shortText, int priority, string rawText) =>
        new(ruleId, shortText, "restart", rawText, TextNormalizer.Normalize(rawText), true) { Priority = priority };

    private static MappedMessage ServoAlarm() =>
        Rule("servo-alarm", "Alarm serwonapędu", 90, "No.705 [N]Axis 6 servo alarm");

    private static MappedMessage ServoNotEnabled() =>
        Rule("servo-not-enabled", "Serwonapęd osi niewłączony", 80, "No.725 [N]Axis 6 servo isn't enabled");

    private static MappedMessage HomeRequest() =>
        Rule("home-request", "Wymaga bazowania (Home) po restarcie", 50, "No.1200 [p]Request home alarm");

    private static MappedMessage MacroExecError() =>
        Rule("macro-exec-error", "Błąd wykonania makra (No.520)", 40, "No.520 [N]");

    private static MappedMessage MacroPauseTimeout() =>
        Rule("macro-pause-timeout", "Przekroczony czas makra pauzy (No.2502)", 30, "No.2502 [N]");

    /// <summary>Komunikat NIEdopasowany do żadnej reguły, ale z odczytanym tekstem.</summary>
    private static MappedMessage UnknownText(string rawText = "Jakis nieznany komunikat") =>
        new(null, "Błąd: " + rawText, "inny", rawText, TextNormalizer.Normalize(rawText), false);

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

    // ---------------------------------------------------------------------------------------------
    // (a) Epizod NIEPRZERWANY — kilka różnych komunikatów w jednym okienku statusu.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OnRedConfirmed_WTymSamymEpizodzieKomunikatuOWyzszymPriorytecie_PodmieniaTrescTegoSamegoAlertu()
    {
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), MacroExecError(), "captures/blad.png");
        f.Machine.Acknowledge(first.Key, "PC-3", "jkowalski", f.Clock.UtcNow);
        DateTimeOffset started = first.StartedAt;
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(5));
        Alert swapped = f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(swapped, only);
        Assert.Equal(AlertKey.ForRule("aidite1", "servo-alarm"), only.Key);
        Assert.Equal("Alarm serwonapędu", only.ShortText);
        Assert.Equal(90, only.Priority);
        Assert.Equal(started, only.StartedAt);
        Assert.Equal(f.Clock.UtcNow, only.LastSeenAt);

        // Potwierdzenie („Idę sprawdzić”) zostaje — to nadal ten sam epizod, tylko lepiej rozpoznany.
        Assert.Equal(AlertStatus.Acknowledged, only.Status);
        Assert.Equal("PC-3", only.AckedByHost);
        Assert.Equal("captures/blad.png", only.CaptureFile);

        AlertEvent e = Assert.Single(f.Events);
        Assert.Equal(AlertEventKind.Updated, e.Kind);
        Assert.Empty(f.Machine.RecentHistory); // stary klucz znika bez zamknięcia epizodu
    }

    [Fact]
    public void OnRedConfirmed_WTymSamymEpizodzieKomunikatuONizszymPriorytecie_ZostawiaTrescIOdswiezaTylkoLastSeen()
    {
        // Dlaczego: gdy już pokazujemy alarm serwonapędu, kolejny obrót cyklu („servo isn't enabled”,
        // chińskie makro) nie może go zepchnąć — inaczej treść alertu migałaby co kilka sekund i operator
        // nie wiedziałby, co właściwie się stało.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(3));
        Alert refreshed = f.Machine.OnRedConfirmed(Machine(), ServoNotEnabled(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "servo-alarm"), only.Key);
        Assert.Equal("Alarm serwonapędu", only.ShortText);
        Assert.Equal(90, only.Priority);
        Assert.Equal(first.StartedAt, only.StartedAt);
        Assert.Equal(f.Clock.UtcNow, refreshed.LastSeenAt);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnRedConfirmed_WTymSamymEpizodzieKomunikatuORownymPriorytecie_NieZmieniaTresci()
    {
        // Dlaczego: przy równej ważności nie ma po co przepisywać alertu (i rozsyłać go do pozostałych
        // pięciu komputerów) — zostaje ten, który był pierwszy.
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), Rule("regula-a", "Opis A", 50, "A"), null);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.Machine.OnRedConfirmed(Machine(), Rule("regula-b", "Opis B", 50, "B"), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "regula-a"), only.Key);
        Assert.Equal("Opis A", only.ShortText);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void OnRedConfirmed_PoTekscieNierozpoznanym_PodmieniaGoNaDopasowanaRegule()
    {
        // Dlaczego: dopasowana reguła jest ZAWSZE lepsza
        // niż „Błąd: <surowy tekst OCR>” — nawet gdy jej priorytet wynosi 0.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), UnknownText(), null);
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Machine.OnRedConfirmed(Machine(), Rule("tray-grab", "Nie udało się pobrać tacki", 0, "Failed to grab a tray"), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "tray-grab"), only.Key);
        Assert.Equal(first.StartedAt, only.StartedAt);
        Assert.Equal(AlertEventKind.Updated, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void OnRedConfirmed_CalegoCykluKomunikatowZHali_ZostawiaJedenAlertZAlarmemSerwonapedu()
    {
        Fixture f = NewFixture();

        foreach (MappedMessage m in new[] { MacroExecError(), ServoAlarm(), ServoNotEnabled(), MacroPauseTimeout() })
        {
            f.Machine.OnRedConfirmed(Machine(), m, null);
            f.Clock.Advance(TimeSpan.FromSeconds(2));
        }

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal("Alarm serwonapędu", only.ShortText);
        Assert.Equal(90, only.Priority);
        Assert.Equal("No.705 [N]Axis 6 servo alarm", only.FullText);
        Assert.Single(f.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Empty(f.Machine.RecentHistory);
    }

    // ---------------------------------------------------------------------------------------------
    // (c)–(f) Epizod PRZERWANY — czerwień zniknęła albo maszyna straciła łączność (restart).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OnRedConfirmed_PoZniknieciuCzerwieniIInnymKomunikacie_ZamykaStaryAlertITworzyNowyZDzwiekiem()
    {
        // Dlaczego: po restarcie maszyny po błędzie serwa HMI pokazuje na czerwono „No.1200 Request home
        // alarm” (maszyna wymaga bazowania). Mimo NIŻSZEGO
        // priorytetu niż alarm serwa ma to być NOWY alert z dźwiękiem, a nie cicha podmiana treści.
        Fixture f = NewFixture();
        Alert stary = f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);
        f.Machine.Acknowledge(stary.Key, "PC-2", "anna", f.Clock.UtcNow);
        f.Events.Clear();

        f.Machine.OnRedCleared("aidite1"); // restart maszyny — czerwień znika
        f.Clock.Advance(TimeSpan.FromSeconds(10)); // wciąż PRZED EpisodeCloseAfterMs (30 s)

        Alert nowy = f.Machine.OnRedConfirmed(Machine(), HomeRequest(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "home-request"), only.Key);
        Assert.Equal(nowy.Key, only.Key);
        Assert.Equal(AlertStatus.Active, only.Status); // nowy epizod = nowy alert, bez starego ACK
        Assert.True(only.Sound);
        Assert.Equal(f.Clock.UtcNow, only.StartedAt);
        Assert.Equal(50, only.Priority);

        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Key == only.Key);

        // Stary alert kończy epizod „po bożemu”: Closed + historia + zapomniane ACK (jak w Tick).
        AlertEvent closed = Assert.Single(f.Events, e => e.Kind == AlertEventKind.Closed);
        Assert.Equal(stary.Key, closed.Alert.Key);
        Assert.Equal(AlertStatus.Closed, closed.Alert.Status);
        Assert.Equal(f.Clock.UtcNow, closed.Alert.ClosedAt);
        Assert.Contains(f.Machine.RecentHistory, a => a.Key == stary.Key && a.Status == AlertStatus.Closed);
        Assert.False(f.Acks.IsAcked(stary.Key));
    }

    [Fact]
    public void OnRedConfirmed_PoUtracieLacznosciIInnymKomunikacie_TezTworzyNowyAlert()
    {
        // Dlaczego: przy restarcie maszyny czerwień często nie „gaśnie” na oczach obserwatora — po prostu
        // znika łączność VNC. Utrata łączności musi przerywać epizod tak samo jak zniknięcie czerwieni,
        // inaczej alert po restarcie („Request home alarm”) byłby tylko cichą zmianą treści starego.
        Fixture f = NewFixture();
        Alert stary = f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);
        f.Events.Clear();

        f.Machine.OnMachineOffline(Machine(), f.Clock.UtcNow);
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        f.Machine.OnMachineOnline("aidite1");

        Alert nowy = f.Machine.OnRedConfirmed(Machine(), HomeRequest(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "home-request"), only.Key);
        Assert.Equal(f.Clock.UtcNow, nowy.StartedAt);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised && e.Alert.Key == only.Key);
        Assert.Contains(f.Machine.RecentHistory, a => a.Key == stary.Key && a.Status == AlertStatus.Closed);
    }

    [Fact]
    public void OnRedConfirmed_PoPrzerwaniuTegoSamegoKomunikatu_ToNadalTenSamAlert()
    {
        // Dlaczego: krótka przerwa w czerwieni (jeden cykl bez odczytu, mrugnięcie ekranu) przy TYM SAMYM
        // błędzie nie może wywołać drugiego dzwonka — dopóki Tick nie zamknął epizodu, to ten sam alert.
        Fixture f = NewFixture();
        Alert first = f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);
        f.Events.Clear();

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(5)); // < EpisodeCloseAfterMs
        Alert again = f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(first.Key, only.Key);
        Assert.Equal(first.StartedAt, only.StartedAt);
        Assert.Equal(f.Clock.UtcNow, again.LastSeenAt);
        Assert.Empty(f.Events); // ani Raised, ani Closed — nic się nie zmieniło
        Assert.Empty(f.Machine.RecentHistory);
    }

    [Fact]
    public void OnRedConfirmed_ZdejmujeZnacznikPrzerwaniaEpizodu_WiecKolejnyKomunikatToJuzTenSamEpizod()
    {
        // Dlaczego: po powrocie czerwieni epizod znów trwa NIEPRZERWANIE — kolejny, inny komunikat z cyklu
        // ma być porównany priorytetem (jeden alert), a nie potraktowany jako następny „nowy błąd”.
        // Bez zdejmowania znacznika każda maszyna po pierwszej przerwie sypałaby nowym alertem przy
        // każdym obrocie cyklu komunikatów.
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);
        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null); // czerwień wróciła — znacznik zdjęty
        f.Events.Clear();

        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Machine.OnRedConfirmed(Machine(), MacroPauseTimeout(), null); // niższy priorytet, bez przerwy

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "servo-alarm"), only.Key);
        Assert.Empty(f.Events);
        Assert.Empty(f.Machine.RecentHistory);
    }

    [Fact]
    public void OnRedConfirmed_PoPrzerwaniuIInnymKomunikacie_DzialaTakZeNowyAlertPowstajeTakzePrzyWyzszymPriorytecie()
    {
        // Dlaczego: „przerwany epizod = nowy błąd” działa NIEZALEŻNIE od priorytetu — także wtedy, gdy nowy
        // komunikat jest ważniejszy od starego (wtedy i tak potrzebny jest nowy dzwonek, a stary epizod
        // musi się domknąć w historii, żeby raport pokazał dwa osobne zdarzenia, a nie jedno).
        Fixture f = NewFixture();
        Alert stary = f.Machine.OnRedConfirmed(Machine(), MacroExecError(), null);
        f.Events.Clear();

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(12)); // >= EpisodeBreakAfterMs (10 s), < EpisodeCloseAfterMs (30 s)
        f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "servo-alarm"), only.Key);
        Assert.Equal(f.Clock.UtcNow, only.StartedAt);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Contains(f.Machine.RecentHistory, a => a.Key == stary.Key && a.Status == AlertStatus.Closed);
    }

    // ---------------------------------------------------------------------------------------------
    // (g) KRÓTKA przerwa czerwieni (< EpisodeBreakAfterMs) — okienko HMI mruga między komunikatami w pętli.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OnRedConfirmed_PoKrotkiejPrzerwieIInnymKomunikacieONizszymPriorytecie_ZostajeJedenAlert()
    {
        // Jeśli HMI między komunikatami na chwilę gasi okienko (czerwień
        // znika na 1–3 s), to NIE jest koniec błędu — bez progu EpisodeBreakAfterMs każdy obrót pętli dawałby
        // nowy dzwonek.
        Fixture f = NewFixture();
        f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);
        f.Events.Clear();

        f.Machine.OnRedCleared("aidite1");
        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(3)); // < EpisodeBreakAfterMs (10 s)
        f.Machine.OnRedConfirmed(Machine(), MacroPauseTimeout(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "servo-alarm"), only.Key);
        Assert.Empty(f.Events);
        Assert.Empty(f.Machine.RecentHistory);
    }

    [Fact]
    public void OnRedConfirmed_PoKrotkiejPrzerwieIInnymKomunikacieOWyzszymPriorytecie_PodmieniaTrescBezNowegoDzwonka()
    {
        // Dlaczego: jak wyżej, tylko ważniejszy komunikat po mrugnięciu — treść alertu ma się poprawić
        // (Updated), ale to wciąż ten sam epizod: bez drugiego dzwonka i bez wpisu w historii.
        Fixture f = NewFixture();
        Alert stary = f.Machine.OnRedConfirmed(Machine(), MacroExecError(), null);
        f.Events.Clear();

        f.Machine.OnRedCleared("aidite1");
        f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "servo-alarm"), only.Key);
        Assert.Equal(stary.StartedAt, only.StartedAt);
        Assert.DoesNotContain(f.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Updated);
        Assert.Empty(f.Machine.RecentHistory);
    }

    [Fact]
    public void OnRedConfirmed_LiczyPrzerweOdPierwszegoZnikniecia_NieOdOstatniego()
    {
        // Dlaczego: OnRedCleared jest wołane co cykl (co sekundę) przez cały czas braku czerwieni — gdyby
        // próg liczył się od OSTATNIEGO wywołania, przerwa nigdy nie „urosłaby” do 10 s i restart maszyny
        // nie dawałby nowego alertu.
        Fixture f = NewFixture();
        Alert stary = f.Machine.OnRedConfirmed(Machine(), ServoAlarm(), null);
        f.Events.Clear();

        for (int i = 0; i < 11; i++)
        {
            f.Machine.OnRedCleared("aidite1");
            f.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        f.Machine.OnRedConfirmed(Machine(), HomeRequest(), null);

        Alert only = Assert.Single(f.Machine.ActiveAlerts);
        Assert.Equal(AlertKey.ForRule("aidite1", "home-request"), only.Key);
        Assert.Contains(f.Events, e => e.Kind == AlertEventKind.Raised);
        Assert.Contains(f.Machine.RecentHistory, a => a.Key == stary.Key && a.Status == AlertStatus.Closed);
    }
}
