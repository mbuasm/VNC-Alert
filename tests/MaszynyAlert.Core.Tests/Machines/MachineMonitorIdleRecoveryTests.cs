using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Ocr;
using static MaszynyAlert.Core.Tests.Machines.MachineMonitorAsyncOcrTests;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineMonitorIdleRecoveryTests
{
    private static string Tekst(int setne) => $"{setne / 100}.{setne % 100:00}%";

    /// <summary>
    /// Inny obraz pola postępu dla każdej wartości (unika 240 — to kolor tła sztucznej klatki), żeby
    /// obserwator widział zmianę cyfr dokładnie tak, jak na prawdziwym ekranie.
    /// </summary>
    private static byte Piksel(int setne) => (byte)(10 + (setne % 200));

    /// <summary>Ustawia na „ekranie” maszyny wartość postępu: zarówno obraz pola, jak i to, co przeczyta OCR.</summary>
    private static void Pokaz(Harness h, int setne)
    {
        h.Source.Frame = Frame(progress: Piksel(setne));
        h.Ocr.Progress = new(Tekst(setne), [Tekst(setne)]);
    }

    private static int SekundaZamkniecia(Harness h, string kategoria) =>
        Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Closed
            && e.Event.Alert.Kind == AlertKind.MachineIdle && e.Event.Alert.Category == kategoria).Second;

    private static int SekundaAlertu(Harness h, string kategoria) =>
        Assert.Single(h.Events, e => e.Event.Kind == AlertEventKind.Raised
            && e.Event.Alert.Kind == AlertKind.MachineIdle && e.Event.Alert.Category == kategoria).Second;

    // ---------------------------------------------------------------------------------------------
    // Po alercie „100 %” (dołóż prace)
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Po100_NowaPraca_CyfryCoKilkaSekund_AlertZamykaSieOdRazu(bool watchHmi)
    {
        // Praca „w toku” z liczbą ≥ 1 % (niezależnie od odczytu ułamków poniżej 1 %): wystarczy jeden
        // rozstrzygnięty odczyt z niezmienionego wycinka, żeby zamknąć epizod postoju.
        using var h = new Harness(watchHmi, idle: true);
        await h.Run(400, second =>
        {
            if (second >= 150) Pokaz(h, 1234 + ((second - 150) / 4));
        });
        int raised = SekundaAlertu(h, AlertStateMachine.IdleCategory);
        Assert.InRange(raised, 120, 125);
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleCategory), 151, 154);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Po100_CyfryZmieniajaSieCoCykl_AlertZamykaNajpozniejBrakOdswiezenia(bool watchHmi)
    {
        using var h = new Harness(watchHmi, idle: true);
        await h.Run(400, second =>
        {
            if (second >= 150) Pokaz(h, 1234 + (second - 150));
        });
        Assert.InRange(SekundaAlertu(h, AlertStateMachine.IdleCategory), 120, 125);
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleCategory), 151, 185);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised && e.Second > 150);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Theory]
    [InlineData(1)]   // cyfry co cykl — OCR nie nadąża
    [InlineData(4)]   // cyfry co 4 s — typowa dłuższa praca
    [InlineData(10)]  // cyfry co 10 s — bardzo długa praca
    public async Task Po100_NowaPracaOdZera_UlamekProcentu_NiePodnosiAlertuStoiNaZero(int coIleSekund)
    {
        // „Maszyna wczytała kolejną pracę”: 100 % → krótko 0.00 % → 0.01 %, 0.02 %… Postęp poniżej 1 %
        // to praca w toku, nie „Stoi na 0 %”: przez 5 minut nie może powstać alert kategorii postoj-0,
        // a alert „dołóż prace” ma się zamknąć.
        using var h = new Harness(watchHmi: true, idle: true);
        await h.Run(460, second =>
        {
            if (second is >= 150 and < 153) Pokaz(h, 0);
            if (second >= 153) Pokaz(h, 1 + ((second - 153) / coIleSekund));
        });
        Assert.InRange(SekundaAlertu(h, AlertStateMachine.IdleCategory), 120, 125);
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleCategory), 151, 185);
        Assert.DoesNotContain(h.Events, e => e.Event.Alert.Category == AlertStateMachine.IdleAtZeroCategory);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task Po100_NowaPraca_UlamekProcentuStojacyDlugo_NiePodnosiAlertuStoiNaZero()
    {
        // Wolna praca albo HMI, które odświeża liczbę rzadko: „0.20%” stoi na ekranie dłużej niż
        // idleAtZeroAfterMs (3 min). To wciąż praca w toku — ułamek procentu nie jest zerem.
        using var h = new Harness(watchHmi: true, idle: true);
        await h.Run(460, second =>
        {
            if (second is >= 150 and < 153) Pokaz(h, 0);
            if (second >= 153) Pokaz(h, 20);
        });
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleCategory), 151, 160);
        Assert.DoesNotContain(h.Events, e => e.Event.Alert.Category == AlertStateMachine.IdleAtZeroCategory);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    // ---------------------------------------------------------------------------------------------
    // Po alercie „0 %” (wciśnij Start)
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Po0_Start_UlamekProcentu_AlertStoiNaZeroZamykaSie(int coIleSekund)
    {
        // „Maszyna stała dłużej”: praca wczytana, 0.00 % ponad 3 minuty → alert „Stoi na 0 %”. Operator
        // wciska Start, HMI pokazuje 0.20 %, 0.21 %… — alert ma zniknąć (od razu przy cyfrach co kilka
        // sekund, najpóźniej po 30 s bez odświeżenia przy cyfrach co cykl) i nie wrócić.
        using var h = new Harness(watchHmi: true, idle: true);
        Pokaz(h, 0);
        await h.Run(560, second =>
        {
            if (second >= 250) Pokaz(h, 20 + ((second - 250) / coIleSekund));
        });
        Assert.InRange(SekundaAlertu(h, AlertStateMachine.IdleAtZeroCategory), 180, 186);
        int closed = SekundaZamkniecia(h, AlertStateMachine.IdleAtZeroCategory);
        Assert.InRange(closed, 251, coIleSekund == 1 ? 285 : 256);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised && e.Second > 250);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task Po0_Start_PracaPowyzejJednegoProcenta_AlertZamykaSieOdRazu()
    {
        // Kontrola bez udziału ułamków procentu: ta sama droga, ale od razu 12.34 % — rozstrzygnięty
        // odczyt „w toku” zamyka epizod w najbliższym cyklu po odczycie.
        using var h = new Harness(watchHmi: true, idle: true);
        Pokaz(h, 0);
        await h.Run(400, second =>
        {
            if (second >= 250) Pokaz(h, 1234 + ((second - 250) / 4));
        });
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleAtZeroCategory), 251, 254);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    // ---------------------------------------------------------------------------------------------
    // Wolny OCR i śmieci z OCR
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WolnyOcr_WynikPoKilkuCyklach_AlertPostojuZamykaSie(int opoznienieCykli)
    {
        // OCR postępu czeka w kolejce SerializingOcrEngine za innymi maszynami i kończy się dopiero po
        // 2–3 cyklach. Cyfry zmieniają się co 5 s, więc część odczytów zdąży na niezmieniony wycinek.
        using var h = new Harness(watchHmi: true, idle: true);
        await h.Run(400, second =>
        {
            if (second >= 150)
            {
                Pokaz(h, 1234 + ((second - 150) / 5));
                // Zwolnienie oczekujących odczytów tylko co „opóźnienie” cykli.
                h.Ocr.HoldProgress = (second - 150) % opoznienieCykli != 0;
            }
        });
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleCategory), 151, 160);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task WolnyOcr_CyfryZawszeSzybszeNizOdczyt_AlertWygasaPoBrakuOdswiezenia()
    {
        // Każdy wynik OCR trafia na zmieniony wycinek. Żaden nie jest dowodem ruszenia, ale też żaden
        // nie odświeża alertu — musi wygasnąć po episodeCloseAfterMs, zamiast wisieć bez końca.
        using var h = new Harness(watchHmi: true, idle: true);
        await h.Run(400, second =>
        {
            if (second >= 150)
            {
                Pokaz(h, 1234 + (second - 150));
                h.Ocr.HoldProgress = (second - 150) % 3 != 0;
            }
        });
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleCategory), 151, 185);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task OcrCzasemZwracaSmieci_AlertPostojuZamykaSieIPozostajeZamkniety()
    {
        // Co trzeci odczyt to śmieci (odblask, przejście klatki). Nieczytelny odczyt nie jest ani
        // postojem, ani ruszeniem — pozostałe odczyty „w toku” mają zamknąć alert i nie dopuścić
        // do nowego.
        using var h = new Harness(watchHmi: true, idle: true);
        await h.Run(460, second =>
        {
            if (second < 150) return;
            int setne = 3456 + ((second - 150) / 4);
            h.Source.Frame = Frame(progress: Piksel(setne));
            h.Ocr.Progress = second % 3 == 0 ? new("#@~", ["#@~"]) : new(Tekst(setne), [Tekst(setne)]);
        });
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleCategory), 151, 160);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised && e.Second > 150);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task OcrCzasemZwracaSmieci_UlamekProcentuPo0_AlertStoiNaZeroZamykaSie()
    {
        using var h = new Harness(watchHmi: true, idle: true);
        Pokaz(h, 0);
        await h.Run(560, second =>
        {
            if (second < 250) return;
            int setne = 20 + ((second - 250) / 4);
            h.Source.Frame = Frame(progress: Piksel(setne));
            h.Ocr.Progress = second % 3 == 0 ? new("#@~", ["#@~"]) : new(Tekst(setne), [Tekst(setne)]);
        });
        Assert.InRange(SekundaZamkniecia(h, AlertStateMachine.IdleAtZeroCategory), 251, 260);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised && e.Second > 250);
        Assert.Empty(h.Alerts.ActiveAlerts);
    }

    [Fact]
    public async Task NaDwochCyfrachPoPrzecinku_StaleZeroZeroDalejPodnosiAlertStoiNaZero()
    {
        // prawdziwe „0.00%” (maszyna wczytała pracę i nikt nie wcisnął Start) nadal
        // ma dawać alert „Stoi na 0 %” po idleAtZeroAfterMs — ułamki nie mogą go wyłączyć.
        using var h = new Harness(watchHmi: true, idle: true);
        Pokaz(h, 0);
        await h.Run(240);
        Assert.InRange(SekundaAlertu(h, AlertStateMachine.IdleAtZeroCategory), 180, 186);
        Assert.Single(h.Alerts.ActiveAlerts);
    }
    [Fact]
    public async Task CyfryPonizejJednegoProcentuZmieniajaSieCoCykl_KolumnaPostepuPokazujeOdczyt()
    {
        // Próbka z wycinka, który zmienił się w trakcie OCR, zasila tylko wyświetlanie.
        using var h = new Harness(watchHmi: false, idle: true);
        bool widzianyOdczyt = false;
        await h.Run(200, second =>
        {
            if (second >= 150) Pokaz(h, 20 + (second - 150));
            if (second >= 160 && h.Monitor.Snapshot.LastProgressPercent == 0) widzianyOdczyt = true;
        });
        Assert.True(widzianyOdczyt);
        Assert.DoesNotContain(h.Events, e => e.Event.Kind == AlertEventKind.Raised
            && e.Event.Alert.Category == AlertStateMachine.IdleAtZeroCategory);
    }
}
