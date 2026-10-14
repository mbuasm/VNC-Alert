using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class TextReadabilityTests
{
    private const string TekstServoAlarm = "No.705 [N]Axis 6 servo alarm";
    private const string TekstChinski = "No.705 [N]宏程序执行错误";
    private const string SmieciZNumerem = "No.1532 [N]xyz••";
    private const string SmieciBezNumeru = "No.łmo h•———";

    private static MessageMapper Mapper() => new(TestPaths.ExampleRules);

    private static MachineConfig Machine() => new()
    {
        Id = "aidite1",
        Name = "Aidite 1",
        Host = "192.168.1.108",
        Port = 9100,
        Profile = "opencnc",
    };

    // =============================================================================================
    // TextReadability.IsReadable / ExtractAlarmNumber
    // =============================================================================================

    [Theory]
    [InlineData("No.1532 [N]Tool life over")]      // pełny komunikat z numerem
    [InlineData("Axis 6 servo alarm")]             // bez numeru, ale z prawdziwymi słowami
    public void IsReadable_TekstuWygladajacegoJakKomunikat_ZwracaPrawde(string text)
    {
        // Dlaczego: taki tekst wolno pokazać operatorowi w oknie alertu („Błąd: …”) — widać z niego, co
        // maszyna wyświetla, nawet jeśli żadna reguła słownika go nie rozpoznała.
        Assert.True(TextReadability.IsReadable(text));
    }

    [Theory]
    [InlineData(SmieciBezNumeru)]     // 50 % znaków to symbole — nieudany odczyt czerwonego napisu
    [InlineData("•—— ł ó")]           // same symbole i polskie litery wstawione przez OCR pl-PL
    [InlineData("")]
    [InlineData(null)]
    [InlineData("IS32")]              // krótki fragment z cyframi: ani słowa 4+ liter, ani numeru alarmu
    [InlineData("AB")]                // dwie litery — pomyłka OCR, nie treść
    public void IsReadable_SmieciPoNieudanymOdczycie_ZwracaFalsz(string? text)
    {
        // Dlaczego: pokazanie operatorowi „Błąd: •—— ł” jest gorsze niż uczciwe „Błąd (nie odczytano
        // treści)” — z pierwszego nie wynika nic, a wygląda, jakby program się popsuł.
        Assert.False(TextReadability.IsReadable(text));
    }

    [Fact]
    public void IsReadable_KomunikatuZNumeremIChinskaTrescia_ZwracaFalsz()
    {
        // Dlaczego (zachowanie utrwalone): chińska treść to ponad 30 % znaków spoza ASCII, więc reguła
        // „≥ 70 % znaków ASCII” odrzuca cały tekst jako nieczytelny — i słusznie, bo operatorowi nic z
        // niego nie przyjdzie. Sam NUMER alarmu jest jednak odczytany poprawnie i to on ratuje alert
        // (patrz Map_KomunikatuZNumeremBezReguly_... i ExtractAlarmNumber niżej).
        Assert.False(TextReadability.IsReadable(TekstChinski));
        Assert.Equal("705", TextReadability.ExtractAlarmNumber(TekstChinski));
    }

    [Theory]
    [InlineData("No.1532 [N]Tool life over", "1532")]
    [InlineData(TekstServoAlarm, "705")]
    [InlineData(SmieciZNumerem, "1532")]
    public void ExtractAlarmNumber_TekstuZNumeremAlarmu_ZwracaSameCyfry(string text, string expected)
    {
        // Dlaczego: numer alarmu jest „kotwicą” epizodu — po nim rozpoznajemy, że to wciąż TEN SAM błąd,
        // niezależnie od tego, ile śmieci OCR doczytał po numerze.
        Assert.Equal(expected, TextReadability.ExtractAlarmNumber(text));
    }

    [Theory]
    [InlineData("No.7O5 [N]Axis 6 servo alarm", "705")]   // O zamiast 0
    [InlineData("N0.705 [N]", "705")]                      // N0 zamiast No
    [InlineData("No.I2OO [p]Request home alarm", "1200")]  // I zamiast 1, O zamiast 0
    [InlineData("No.l200", "1200")]                        // małe l zamiast 1
    [InlineData("NO 1615", "1615")]                        // bez kropki
    [InlineData("No.20 [N]PLC internal error", "20")]      // numer dwucyfrowy
    public void ExtractAlarmNumber_NumeruPrzekreconegoPrzezOcr_PoprawiaCyfry(string text, string expected)
    {
        // Dlaczego: numer alarmu jest kluczem do słownika kodów (lista AMD500DCS) — OCR bez pakietu en-US
        // myli 0/O i 1/I także w cyfrach, a bez poprawki „No.7O5” nie trafiłoby w kod 705.
        Assert.Equal(expected, TextReadability.ExtractAlarmNumber(text));
        Assert.True(TextReadability.TryExtractAlarmCode(text, out int code));
        Assert.Equal(int.Parse(expected), code);
    }

    [Theory]
    [InlineData("Axis 6 servo alarm")]
    [InlineData(SmieciBezNumeru)]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("NO OIL in spindle")]      // po „NO” same litery — to nie numer
    [InlineData("NOISE 705")]              // „NO” to początek słowa, nie token numeru
    [InlineData("No.")]                    // token bez numeru
    public void ExtractAlarmNumber_TekstuBezNumeruAlarmu_ZwracaNull(string? text)
    {
        // Dlaczego: brak numeru musi być odróżnialny od numeru „0” — inaczej wszystkie komunikaty bez
        // numeru zlałyby się w jeden alert o wspólnym kluczu.
        Assert.Null(TextReadability.ExtractAlarmNumber(text));
    }

    // =============================================================================================
    // MessageMapper — numer alarmu bez reguły / nieczytelne śmieci
    // =============================================================================================

    [Fact]
    public void Map_KomunikatuZNumeremBezRegulyWSlowniku_DajeOpisZNumeremIKluczAlarm()
    {
        MappedMessage m = Mapper().MapAidite(SmieciZNumerem);

        Assert.False(m.Matched);
        Assert.Null(m.RuleId);
        Assert.Equal("Błąd nr 1532 (brak opisu w słowniku)", m.ShortText);
        Assert.Equal("1532", m.AlarmNumber);
        Assert.Equal("alarm:1532", m.Discriminator);
        Assert.Equal(SmieciZNumerem, m.RawText);
    }

    [Fact]
    public void Map_TegoSamegoNumeruZRoznymiSmieciami_DajeTenSamKluczAlertu()
    {
        // Dlaczego: okienko HMI mruga, OCR za każdym razem doczytuje inne śmieci po
        // numerze, a bez wspólnego klucza operator dostawałby serię osobnych alertów dla JEDNEGO błędu
        // (i dzwonek przy każdym z nich).
        MessageMapper mapper = Mapper();

        MappedMessage pierwszy = mapper.MapAidite(SmieciZNumerem);
        MappedMessage drugi = mapper.MapAidite("No.1532 [N]xyz ł•— h");

        Assert.Equal(pierwszy.Discriminator, drugi.Discriminator);
        Assert.Equal(AlertKey.ForMessage("aidite1", pierwszy), AlertKey.ForMessage("aidite1", drugi));
        Assert.Equal(pierwszy.ShortText, drugi.ShortText);
    }

    [Fact]
    public void Map_NieczytelnychSmieciBezNumeru_DajeOpisONieodczytanejTresciIZachowujeSurowyTekst()
    {
        // Dlaczego: czerwień na pasku ZAWSZE ma dać alert — nawet gdy nie wiadomo, co dokładnie się stało.
        // Surowy odczyt zostaje w RawText (idzie do Alert.FullText i do logu), żeby dało się później
        // dostroić OCR/reguły, ale operator na pasku alertu widzi uczciwe „nie odczytano treści”.
        MappedMessage m = Mapper().MapAidite(SmieciBezNumeru);

        Assert.False(m.Matched);
        Assert.Null(m.AlarmNumber);
        Assert.Equal("Błąd (nie odczytano treści)", m.ShortText);
        Assert.Equal(SmieciBezNumeru, m.RawText);
    }

    [Fact]
    public void Map_CzytelnegoTekstuBezReguly_DajeDotychczasowyOpisZPrefiksemBlad()
    {
        // Dlaczego: czytelny komunikat spoza słownika ma nadal
        // trafiać na pasek alertu w całości, bo operator zwykle wie z niego, co robić.
        MappedMessage m = Mapper().MapAidite("Coolant pump warning");

        Assert.False(m.Matched);
        Assert.Null(m.AlarmNumber);
        Assert.Equal("Błąd: Coolant pump warning", m.ShortText);
    }

    // =============================================================================================
    // MappedMessage.IsBetterThan — kolejność ważności komunikatów w jednym epizodzie
    // =============================================================================================

    [Fact]
    public void IsBetterThan_KomunikatuZNumeremWobecNiedopasowanegoBezNumeru_ZwracaPrawde()
    {
        // Dlaczego: w jednym epizodzie HMI pokazuje po kolei kilka komunikatów — „Błąd nr 1532” jest dla
        // operatora zawsze użyteczniejszy niż „Błąd: <przypadkowe znaki>” albo „nie odczytano treści”.
        MessageMapper mapper = Mapper();
        MappedMessage zNumerem = mapper.MapAidite(SmieciZNumerem);
        MappedMessage bezNumeru = mapper.MapAidite(SmieciBezNumeru);

        Assert.True(zNumerem.IsBetterThan(bezNumeru));
        Assert.False(bezNumeru.IsBetterThan(zNumerem));
    }

    [Fact]
    public void IsBetterThan_DopasowanejRegulyWobecKomunikatuZNumerem_ZwracaPrawde()
    {
        // Dlaczego: polski opis ze słownika („Alarm serwonapędu osi”) mówi operatorowi wprost, co się
        // stało — numer alarmu bez opisu jest tylko ostatnią deską ratunku.
        MessageMapper mapper = Mapper();
        MappedMessage zReguly = mapper.MapAidite(TekstServoAlarm);
        MappedMessage zNumerem = mapper.MapAidite(SmieciZNumerem);

        Assert.True(zReguly.Matched);
        Assert.True(zReguly.IsBetterThan(zNumerem));
        Assert.False(zNumerem.IsBetterThan(zReguly));
    }

    // =============================================================================================
    // AlertStateMachine — numer alarmu jako spoiwo epizodu
    // =============================================================================================

    [Fact]
    public void OnRedConfirmed_TegoSamegoNumeruZRoznymiSmieciami_DajeJedenAlertBezDrugiegoDzwonka()
    {
        // Dlaczego: jeden błąd maszyny ma dać JEDEN alert.
        var clock = new FakeClock();
        var alerts = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        var events = new List<AlertEvent>();
        alerts.Changed += events.Add;
        MessageMapper mapper = Mapper();

        alerts.OnRedConfirmed(Machine(), mapper.MapAidite(SmieciZNumerem), "captures/blad.png");
        clock.Advance(TimeSpan.FromSeconds(2));
        Alert drugi = alerts.OnRedConfirmed(Machine(), mapper.MapAidite("No.1532 [N]•— xyz"), "captures/blad.png");

        Alert single = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal(new AlertKey("aidite1", "alarm:1532"), single.Key);
        Assert.Equal("Błąd nr 1532 (brak opisu w słowniku)", single.ShortText);
        Assert.Equal(single.Key, drugi.Key);
        Assert.Single(events, e => e.Kind == AlertEventKind.Raised);
    }

    [Fact]
    public void OnRedConfirmed_InnegoNumeruWTymSamymEpizodzie_ZostawiaAlertPrzyPierwszymNumerze()
    {
        // Dlaczego: alert powstały z numeru alarmu niesie ten numer w swoim kluczu („alarm:1532”) i
        // AlertStateMachine musi go stamtąd odtworzyć przy porównywaniu z kolejnym komunikatem epizodu.
        // Bez tego każdy następny numer z cyklu HMI przejmowałby alert (treść migałaby bez powodu), mimo
        // że żaden z nich nie jest „lepszy” od drugiego — oba są tak samo bez opisu w słowniku.
        var clock = new FakeClock();
        var alerts = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        MessageMapper mapper = Mapper();

        alerts.OnRedConfirmed(Machine(), mapper.MapAidite(SmieciZNumerem), null);
        clock.Advance(TimeSpan.FromSeconds(2));
        alerts.OnRedConfirmed(Machine(), mapper.MapAidite("No.1533 [N]•—"), null);

        Alert single = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal(new AlertKey("aidite1", "alarm:1532"), single.Key);
        Assert.Equal("Błąd nr 1532 (brak opisu w słowniku)", single.ShortText);
    }

    [Fact]
    public void OnRedConfirmed_PoNieczytelnychSmieciachIPozniejszymNumerze_PrzejmujeAlertNumerem()
    {
        // Dlaczego: kierunek odwrotny — gdy pierwszy odczyt epizodu był nieczytelny, a dopiero kolejny
        // przyniósł numer alarmu, operator ma zobaczyć numer. To ta sama reguła „lepszego komunikatu”.
        var clock = new FakeClock();
        var alerts = new AlertStateMachine(clock, new TimingConfig(), new AckMemory(clock, TimeSpan.FromHours(2)));
        MessageMapper mapper = Mapper();

        alerts.OnRedConfirmed(Machine(), mapper.MapAidite(SmieciBezNumeru), null);
        clock.Advance(TimeSpan.FromSeconds(2));
        alerts.OnRedConfirmed(Machine(), mapper.MapAidite(SmieciZNumerem), null);

        Alert single = Assert.Single(alerts.ActiveAlerts);
        Assert.Equal(new AlertKey("aidite1", "alarm:1532"), single.Key);
        Assert.Equal("Błąd nr 1532 (brak opisu w słowniku)", single.ShortText);
    }
}
