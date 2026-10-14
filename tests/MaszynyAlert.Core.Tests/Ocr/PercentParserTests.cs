using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Ocr;

/// <summary>
/// Parser procentu postępu z surowego tekstu OCR (tryb <c>ProgressMode.Number</c>): typowe pomyłki
/// silnika w polu liczbowym ('O'→'0', 'I'/'l'/'|'→'1'), wybór liczby stojącej przed znakiem '%'
/// oraz odrzucanie odczytów spoza zakresu 0..100.
/// </summary>
public sealed class PercentParserTests
{
    [Theory]
    [InlineData("100%", 100)]
    [InlineData("1OO %", 100)]
    [InlineData("99", 99)]
    [InlineData("Progress: 45%", 45)]
    // Warianty tej samej liczby, które realnie wypluwa OCR małej czcionki.
    [InlineData("100 %", 100)]
    [InlineData("l00%", 100)]
    [InlineData("|00%", 100)]
    [InlineData("45 %", 45)]
    [InlineData("0%", 0)]
    public void TryParse_TypowychOdczytow_ZwracaProcent(string tekst, int oczekiwany)
    {
        // Dlaczego: od tego odczytu zależy, czy maszyna zostanie uznana za stojącą na 100 % — pomyłka
        // „l”/„|” zamiast „1” albo „O” zamiast „0” nie może zamienić pełnego postępu w nieczytelny odczyt.
        Assert.True(PercentParser.TryParse(tekst, out int percent), tekst);
        Assert.Equal(oczekiwany, percent);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("150%")]
    [InlineData("")]
    // Odczyt pusty/sam znak procentu też musi być odrzucony, a nie „domyślony” na zero.
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("%")]
    public void TryParse_TekstuBezSensownejLiczby_ZwracaFalse(string? tekst)
    {
        // Dlaczego: zachowawczo — nieczytelny odczyt ma dać „nie wiem” (brak alertu), a nie zgadywanie.
        // Zgadnięte 0 % ukryłoby źle ustawione progressRoi, a zgadnięte 100 % budziłoby ludzi bez powodu.
        Assert.False(PercentParser.TryParse(tekst, out int percent));
        Assert.Equal(0, percent);
    }

    [Fact]
    public void TryParse_TekstuZBialymiZnakamiINowaLinia_ZwracaProcent()
    {
        // Dlaczego: Windows.Media.Ocr zwraca tekst linia po linii — wokół liczby zostają spacje i znak
        // końca linii, które nie mogą psuć odczytu.
        Assert.True(PercentParser.TryParse("  100%\n", out int percent));
        Assert.Equal(100, percent);
    }

    [Fact]
    public void TryParse_GdyWTekscieSaDwieLiczbyAProcentMaDrugaZNich_WybieraTeZeZnakiemProcentu()
    {
        // Dlaczego: w ROI może się zmieścić kawałek sąsiedniej etykiety (np. numer narzędzia) — procentem
        // jest ta liczba, po której stoi '%', a nie pierwsza napotkana cyfra.
        Assert.True(PercentParser.TryParse("T3 45%", out int percent));
        Assert.Equal(45, percent);
    }

    [Fact]
    public void TryParse_GdyZadnaLiczbaNieMaZnakuProcentu_BierzePierwsza()
    {
        // Dlaczego: HMI potrafi pokazać samą liczbę bez znaku '%' (kontraktowy przykład „99”) — wtedy
        // nie ma czego preferować i liczy się pierwsza znaleziona.
        Assert.True(PercentParser.TryParse("99 100", out int percent));
        Assert.Equal(99, percent);
    }

    [Fact]
    public void TryParse_LiczbyCzterocyfrowej_ZwracaFalse()
    {
        // Dlaczego: „1000%” to sklejony/zniekształcony odczyt, a nie postęp — kontrakt mówi, że wynik
        // musi być w 0..100, inaczej odczyt jest nieczytelny (brak alertu i widoczne „?” w kolumnie
        // „Postęp”), a nie „0 %”, które wyglądałoby jak prawdziwy odczyt świeżo startującej pracy.
        Assert.False(PercentParser.TryParse("1000%", out int percent));
        Assert.Equal(0, percent);
    }

    [Fact]
    public void TryParse_LiczbyZUlamkiem_CzytaCzescPoKropce_ZachowanieUtrwalone()
    {
        // Dlaczego: HMI pokazuje procent z częścią ułamkową („Milling Status 79.08%”, „12.5%”) — parser
        // rozpoznaje separator [.,] i część ułamkową jako całość liczby (żeby jej cyfry nie zostały
        // pomyłkowo odczytane jako kolejna, osobna liczba), ale WYNIKIEM jest wyłącznie część całkowita,
        // obcięta w dół (bez zaokrąglania) — „12.5%” to postęp 12%, nie 13% i nie 5%.
        Assert.True(PercentParser.TryParse("12.5%", out int percent));
        Assert.Equal(12, percent);
    }

    [Theory]
    [InlineData("79.08%", 79)]
    [InlineData("100.00%", 100)]
    [InlineData("0.00% .", 0)]
    [InlineData("100,00 %", 100)]
    [InlineData("99.99%", 99)]
    [InlineData("Milling Status 79.08%", 79)]
    [InlineData("1OO.OO%", 100)]
    [InlineData("100.5", 100)]
    public void TryParse_ProcentuZCzesciaUlamkowa_ZwracaCzescCalkowita(string tekst, int oczekiwany)
    {
        Assert.True(PercentParser.TryParse(tekst, out int percent), tekst);
        Assert.Equal(oczekiwany, percent);
    }

    [Fact]
    public void TryParse_TekstuZPolemDSIPolemMillingStatus_BierzePierwszaLiczbeZeZnakiemProcentu()
    {
        // Dlaczego: na ekranie HMI obok postępu stoi pułapka „DS: 100%” (override posuwu), która ZAWSZE
        // pokazuje 100 %. Parser jest czystą funkcją od tekstu i NIE rozróżnia pól — gdy do jednego ROI
        // trafią oba pola, wygra pierwsza liczba ze znakiem '%', czyli właśnie pułapka. Dlatego to nie
        // parser, tylko wołający (probe.exe / aplikacja) MUSI zakotwiczyć ROI na etykiecie „Milling Status”
        // (Vision.LabelAnchorLocator) — ten test utrwala, że sam parser takiego zabezpieczenia nie daje.
        Assert.True(PercentParser.TryParse("DS: 100% Milling Status 45.10%", out int percent));
        Assert.Equal(100, percent);
    }

    [Theory]
    [InlineData("Progress: 10%", 10)]
    [InlineData("Progress:   0%", 0)]
    [InlineData("Progress: 100%", 100)]
    // Przekreślone zero tego HMI Windows OCR czyta jako „e” (zmierzone na natywnej klatce: „0.000” →
    // „9. eee”, „0.470” → „e. 470”) — „e” przylegające do cyfry albo do '%' to zero.
    [InlineData("Progress: 1e%", 10)]
    [InlineData("Progress: e%", 0)]
    [InlineData("1ee%", 100)]
    public void TryParse_WierszaPostepuZEtykieta_CzytaSamaLiczbe(string tekst, int oczekiwany)
    {
        Assert.True(PercentParser.TryParse(tekst, out int percent), tekst);
        Assert.Equal(oczekiwany, percent);
    }

    [Theory]
    [InlineData("Progress:")]
    [InlineData("Progress: ")]
    [InlineData("Milling Status")]
    [InlineData("Progress")]
    public void TryParse_SamejEtykietyBezLiczby_ZwracaFalse(string tekst)
    {
        Assert.False(PercentParser.TryParse(tekst, out int percent), tekst);
        Assert.Equal(0, percent);
    }

    [Fact]
    public void TryParse_LiczbyCzterocyfrowejZCzesciaUlamkowa_ZwracaFalse()
    {
        // Dlaczego: „1000.00%” to sklejony/zniekształcony odczyt — tak samo nieczytelny jak „1000%”
        // (patrz test wyżej), więc musi dać „nie wiem”, a nie wiarygodnie wyglądające 0 %, które w kolumnie
        // „Postęp” wygląda jak dopiero rozpoczęta praca i ukrywa źle ustawione ROI.
        Assert.False(PercentParser.TryParse("1000.00%", out int percent));
        Assert.Equal(0, percent);
    }
    [Theory]
    [InlineData("0.20%", 0, true)]
    [InlineData("0.2%", 0, true)]
    [InlineData("0,05 %", 0, true)]
    [InlineData("Progress: 0.01%", 0, true)]
    [InlineData("Milling Status 0.2e%", 0, true)]
    [InlineData("0.00%", 0, false)]
    [InlineData("0.0%", 0, false)]
    [InlineData("0%", 0, false)]
    [InlineData("Progress: 0.ee%", 0, false)]
    [InlineData("0", 0, false)]
    [InlineData("79.08%", 79, true)]
    [InlineData("100.00%", 100, false)]
    [InlineData("12%", 12, false)]
    public void TryParse_CzescUlamkowa_RozrozniaPrawdziweZeroOdPoczatkuPracy(string tekst, int oczekiwany, bool ulamekNiezerowy)
    {
        Assert.True(PercentParser.TryParse(tekst, out int percent, out bool niezerowyUlamek), tekst);
        Assert.Equal(oczekiwany, percent);
        Assert.Equal(ulamekNiezerowy, niezerowyUlamek);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Progress:")]
    [InlineData("150.50%")]
    public void TryParse_OdczytNieczytelny_NieZglaszaUlamka(string tekst)
    {
        Assert.False(PercentParser.TryParse(tekst, out int percent, out bool niezerowyUlamek), tekst);
        Assert.Equal(0, percent);
        Assert.False(niezerowyUlamek);
    }

    [Theory]
    [InlineData("Progress: łez", 10)]
    [InlineData("Progress: łz", 1)]
    [InlineData("Progress: 5z", 5)]
    [InlineData("Progress: 2z", 2)]
    [InlineData("Progress: łeez", 100)]
    [InlineData("Progress: ŁeZ", 10)]
    [InlineData("Progress:łez", 10)]
    [InlineData("Progress: łez.", 10)]
    [InlineData("Progress: 22%", 22)]
    [InlineData("Progress: 22z", 22)]
    [InlineData("Progress: ł2%", 12)]
    [InlineData("Progress. 285883 3666829 9.", 9)]
    public void TryParse_PolskiegoOcrZLiteramiLiZ_CzytaLiczbe(string tekst, int oczekiwany)
    {
        Assert.True(PercentParser.TryParse(tekst, out int percent, out bool niezerowyUlamek), tekst);
        Assert.Equal(oczekiwany, percent);
        Assert.False(niezerowyUlamek);
    }

    [Theory]
    // Zero przez polski OCR („0%” → „ez”) to wiarygodne 0 % z zerowym ułamkiem — ma móc zgłosić „Stoi na 0 %”.
    [InlineData("Progress: ez", 0, false)]
    [InlineData("Progress: e.eez", 0, false)]
    [InlineData("Progress: e.łz", 0, true)]
    [InlineData("Progress: łe.5z", 10, true)]
    public void TryParse_PolskiegoOcrZUlamkiem_RozrozniaZeroOdPracy(string tekst, int oczekiwany, bool ulamekNiezerowy)
    {
        Assert.True(PercentParser.TryParse(tekst, out int percent, out bool niezerowyUlamek), tekst);
        Assert.Equal(oczekiwany, percent);
        Assert.Equal(ulamekNiezerowy, niezerowyUlamek);
    }

    [Theory]
    [InlineData("Progress:")]
    [InlineData("Progress: xyz")]
    [InlineData("Progress: z")]
    [InlineData("Progress: Z")]
    [InlineData("Progress: łza")]
    [InlineData("Progress: zł")]
    [InlineData("Progress: Liza")]
    [InlineData("Progress: Zoom")]
    [InlineData("Progress: łe z")]
    [InlineData("Progress: łeza")]
    [InlineData("Progress: gz")]
    [InlineData("Progress: łgz")]
    [InlineData("Błąd")]
    [InlineData("Progress: e e e")]
    // Liczba poza zakresem albo za długa nadal jest „nie wiem”, także z 'z' zamiast '%'.
    [InlineData("Progress: 150z")]
    [InlineData("Progress: łeeez")]
    public void TryParse_ZwyklychSlowZLiteramiLiZ_ZwracaFalse(string tekst)
    {
        Assert.False(PercentParser.TryParse(tekst, out int percent, out bool niezerowyUlamek), tekst);
        Assert.Equal(0, percent);
        Assert.False(niezerowyUlamek);
    }

    [Fact]
    public void TryParse_LiteryLPozaLiczba_ZostajaLiterami()
    {
        // 'ł' w zwykłym słowie nie przylega do cyfry ani '%', więc nie udaje jedynki: „Błąd 5” to 5, nie 15.
        Assert.True(PercentParser.TryParse("Błąd 5", out int percent));
        Assert.Equal(5, percent);
    }

    [Theory]
    [InlineData("Progress: eoz", 0, true)]    // „0%”, język pl, skala ×2
    [InlineData("eoz", 0, true)]
    [InlineData("Progress: lez", 10, true)]   // „10%”, język en-US
    [InlineData("Progress: 17%", 17, true)]
    [InlineData("99", 99, false)]             // goła liczba — przyjęta, ale bez znaku '%'
    [InlineData("Progress: 6", 6, false)]
    public void TryParse_MowiCzyLiczbaStalaPrzedZnakiemProcentu(string tekst, int oczekiwany, bool zeZnakiem)
    {
        Assert.True(PercentParser.TryParse(tekst, out int percent, out _, out bool hasPercentSign), tekst);
        Assert.Equal(oczekiwany, percent);
        Assert.Equal(zeZnakiem, hasPercentSign);
    }
}
