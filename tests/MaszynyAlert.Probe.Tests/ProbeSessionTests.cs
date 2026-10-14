using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Probe.Tests;

public sealed class ProbeSessionTests
{
    private static ProbeSession NewSession(string? password = null, bool noAuthPrompt = false) =>
        new(new GlobalOptions("out", password, noAuthPrompt));

    [Fact]
    public void NowaSesja_MaPustyRaportBrakNasluchuINieZadaZipa()
    {
        // Dlaczego: zip pakuje się tylko wtedy, gdy komenda o to poprosi (robi to `auto`, o ile nie podano
        // --no-zip). Domyślne „true” oznaczałoby, że każda komenda (info, capture…) zostawia po sobie
        // plik zip na pulpicie operatora.
        ProbeSession session = NewSession();

        Assert.False(session.ZipRequested);
        Assert.False(session.IsAutoRun); // pojedyncza komenda ma nadal wypisywać „Co dalej: wpisz…”
        Assert.Null(session.Bus);
        Assert.NotNull(session.Summary);
        Assert.Empty(session.Summary.Rows);
        Assert.NotNull(session.Connections);
    }

    [Fact]
    public void Konstruktor_BezOpcjiGlobalnych_RzucaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ProbeSession(null!));
    }

    [Fact]
    public void RememberPassword_ZapamietujeHasloDlaKonkretnejMaszyny()
    {
        // Dlaczego: krok `watch` łączy się z tymi samymi maszynami co `info` — bez pamięci haseł operator
        // musiałby wpisać hasło drugi i trzeci raz, w środku przebiegu, przy 12 maszynach.
        ProbeSession session = NewSession();

        session.RememberPassword("aidite1", "tajne");

        Assert.True(session.HasKnownPassword("aidite1"));
        Assert.Equal("tajne", session.KnownPassword("aidite1"));
    }

    [Fact]
    public void RememberPassword_ZWartosciaNull_TezOznaczaMaszyneZnana()
    {
        // Dlaczego: „ta maszyna NIE wymaga hasła” to pełnoprawna, zapamiętana odpowiedź — gdyby null
        // znaczył „nie wiem”, program pytałby o hasło do maszyny, która go w ogóle nie chce.
        ProbeSession session = NewSession();

        session.RememberPassword("amm580", null);

        Assert.True(session.HasKnownPassword("amm580"));
        Assert.Null(session.KnownPassword("amm580"));
    }

    [Fact]
    public void HasKnownPassword_MaszynyJeszczeNieProbowanej_JestFalszem()
    {
        ProbeSession session = NewSession();

        Assert.False(session.HasKnownPassword("aidite7"));
        Assert.Null(session.KnownPassword("aidite7"));
    }

    [Fact]
    public void RememberPassword_PonownieDlaTejSamejMaszyny_NadpisujeWczesniejszeHaslo()
    {
        // Dlaczego: operator mógł się pomylić i poprawić hasło w kolejnej próbie — zapamiętane ma zostać
        // to, które faktycznie zadziałało jako ostatnie.
        ProbeSession session = NewSession();

        session.RememberPassword("aidite1", "zle");
        session.RememberPassword("aidite1", "dobre");

        Assert.Equal("dobre", session.KnownPassword("aidite1"));
    }

    [Fact]
    public void HasKnownPassword_RozrozniaWielkoscLiterWIdentyfikatorzeMaszyny()
    {
        // Dlaczego (zachowanie utrwalone): identyfikatory maszyn pochodzą wprost z config.json i są
        // porównywane dokładnie (StringComparer.Ordinal) — tak samo jak klucze alertów w Core.
        ProbeSession session = NewSession();
        session.RememberPassword("aidite1", "tajne");

        Assert.False(session.HasKnownPassword("Aidite1"));
    }

    [Fact]
    public void SetAnchors_ZapamietujeKotwiceDlaMaszynyNaPotrzebyKrokuWatch()
    {
        // Dlaczego: kotwiczenie ROI jest kosztowne (kilka przebiegów OCR na pełnej klatce) — `watch` ma
        // brać gotowy wynik z kroku `ocr`, a nie liczyć go ponownie dla każdej z 12 maszyn.
        ProbeSession session = NewSession();
        var anchors = new MachineAnchors { ProgressRoi = new RectInt(440, 380, 120, 24) };

        session.SetAnchors("aidite1", anchors);

        Assert.Same(anchors, session.GetAnchors("aidite1"));
        Assert.Null(session.GetAnchors("amm580"));
    }

    [Fact]
    public void Summary_JestTaSamaInstancjaDlaWszystkichKrokowPrzebiegu()
    {
        // Dlaczego: info, ocr i watch dopisują fakty do JEDNEJ tabeli podsumowującej — gdyby każdy krok
        // dostawał własną, końcowe podsumowanie zawierałoby tylko to, co zebrał ostatni z nich.
        ProbeSession session = NewSession();

        session.Summary.Machine("aidite1").Rfb = "3.8";
        session.Summary.Machine("aidite1").Screen = "1024x768";

        MachineSummary row = Assert.Single(session.Summary.Rows);
        Assert.Equal("3.8", row.Rfb);
        Assert.Equal("1024x768", row.Screen);
    }
}
