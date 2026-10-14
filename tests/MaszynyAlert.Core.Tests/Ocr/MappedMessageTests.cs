using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class MappedMessageTests
{
    private static MappedMessage Matched(string ruleId, int priority, string shortText = "Opis") =>
        new(ruleId, shortText, "restart", "surowy tekst", "SUROWY TEKST", true) { Priority = priority };

    private static MappedMessage Unmatched(string normalized) =>
        new(null, normalized.Length == 0 ? "Błąd (nie odczytano treści)" : "Błąd: " + normalized, "inny", normalized, normalized, false);

    [Fact]
    public void Priority_BezJawnegoUstawienia_WynosiZero()
    {
        // Dlaczego: rekord ma stały konstruktor pozycyjny używany w wielu miejscach kodu i testów —
        // priorytet dokłada się przez „with”, więc domyślne 0 musi być bezpieczne dla wszystkich starych
        // wywołań (żadne z nich nie ma nagle wygrywać z dopasowaną regułą o priorytecie 90).
        Assert.Equal(0, new MappedMessage("x", "Opis", "inny", "raw", "RAW", true).Priority);
        Assert.Equal(0, Unmatched("COS").Priority);
    }

    [Fact]
    public void IsBetterThan_Null_ZawszePrawda()
    {
        // Dlaczego: pierwszy komunikat epizodu nie ma z czym przegrać — inaczej alert zostałby bez treści.
        Assert.True(Matched("servo-alarm", 90).IsBetterThan(null));
        Assert.True(Unmatched(string.Empty).IsBetterThan(null));
    }

    [Fact]
    public void IsBetterThan_DopasowanejReguly_BijeKazdyTekstNiedopasowany()
    {
        // Dlaczego: „Alarm serwonapędu” jest dla operatora zawsze użyteczniejszy niż „Błąd: No.705 [N]Axi…”
        // albo „Błąd (nie odczytano treści)” — niezależnie od priorytetu reguły (nawet 0).
        Assert.True(Matched("macro-pause-timeout", 0).IsBetterThan(Unmatched("JAKIS TEKST")));
        Assert.True(Matched("macro-pause-timeout", 0).IsBetterThan(Unmatched(string.Empty)));

        // I odwrotnie: tekst niedopasowany NIGDY nie wypiera dopasowanej reguły.
        Assert.False(Unmatched("JAKIS TEKST").IsBetterThan(Matched("macro-pause-timeout", 0)));
        Assert.False(Unmatched(string.Empty).IsBetterThan(Matched("macro-pause-timeout", 0)));
    }

    [Theory]
    [InlineData(90, 40, true)]   // servo-alarm nad błędem makra
    [InlineData(40, 90, false)]  // słabszy komunikat cyklu nie może zepchnąć alarmu serwa
    [InlineData(50, 50, false)]  // równy priorytet — nie ma powodu migać alertem bez różnicy w treści
    public void IsBetterThan_DwochDopasowanychRegul_RozstrzygaPriorytet(int mine, int theirs, bool oczekiwany)
    {
        MappedMessage a = Matched("regula-a", mine);
        MappedMessage b = Matched("regula-b", theirs);

        Assert.Equal(oczekiwany, a.IsBetterThan(b));
    }

    [Fact]
    public void IsBetterThan_TejSamejRegulyZobaczonejPonownie_JestFalszem()
    {
        // Dlaczego: ten sam komunikat wraca w każdym obrocie cyklu — gdyby „wygrywał” sam ze sobą, alert
        // byłby przepisywany (i wysyłany do pozostałych komputerów) co kilka sekund bez żadnej zmiany.
        Assert.False(Matched("servo-alarm", 90).IsBetterThan(Matched("servo-alarm", 90)));
    }

    [Theory]
    [InlineData("JAKIS TEKST", "", true)]            // cokolwiek odczytane > całkowity brak treści
    [InlineData("", "JAKIS TEKST", false)]           // brak treści nie może wyprzeć odczytanego tekstu
    [InlineData("TEKST A", "TEKST B", false)]        // dwa różne nieznane teksty — zostaje pierwszy
    [InlineData("", "", false)]
    public void IsBetterThan_DwochTekstowNiedopasowanych_WygrywaTenNiepusty(string mine, string theirs, bool oczekiwany)
    {
        // Dlaczego: dopóki nic nie pasuje do słownika, alert pokazuje surowy odczyt — ma się „ulepszać”
        // tylko z pustego na niepusty, a nie skakać między dwoma równie nieznanymi tekstami.
        Assert.Equal(oczekiwany, Unmatched(mine).IsBetterThan(Unmatched(theirs)));
    }

    [Fact]
    public void IsBetterThan_ScenariuszCykluKomunikatowZHali_WybieraAlarmSerwonapedu()
    {
        MappedMessage[] cykl =
        [
            Matched("macro-exec-error", 40, "Błąd wykonania makra (No.520)"),
            Matched("servo-alarm", 90, "Alarm serwonapędu"),
            Matched("servo-not-enabled", 80, "Serwonapęd osi niewłączony"),
            Matched("macro-pause-timeout", 30, "Przekroczony czas makra pauzy (No.2502)"),
        ];

        MappedMessage? best = null;
        foreach (MappedMessage m in cykl)
        {
            if (m.IsBetterThan(best))
            {
                best = m;
            }
        }

        Assert.NotNull(best);
        Assert.Equal("servo-alarm", best!.RuleId);
        Assert.Equal("Alarm serwonapędu", best.ShortText);
    }
}
