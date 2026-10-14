using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MachineCardStateTests
{
    private const string MachineId = "aidite3";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

    private static MachineStatusSnapshot Snapshot => new()
    {
        MachineId = MachineId, Name = "Aidite 3", Host = "unused", Profile = "opencnc",
        State = MachineConnectionState.Connected, MainScreenWatchRequired = true,
        MainScreenVisible = true, OcrAvailable = true, LastFrameAt = Now.AddSeconds(-2),
        LastProgressPercent = 42,
    };

    private static Alert Error(AlertStatus status) => new()
    {
        Key = AlertKey.ForRule(MachineId, "tacka"), MachineId = MachineId, MachineName = "Aidite 3",
        Kind = AlertKind.Error, ShortText = "Nie udało się pobrać tacki", Category = "tacka", Sound = true,
        Status = status, StartedAt = Now.AddMinutes(-9), LastSeenAt = Now,
        AcknowledgedAt = status is AlertStatus.Active ? null : Now.AddMinutes(-4),
    };

    private static Alert Idle(AlertStatus status = AlertStatus.Active) => new()
    {
        Key = AlertKey.Idle(MachineId), MachineId = MachineId, MachineName = "Aidite 3",
        Kind = AlertKind.MachineIdle, ShortText = "Dołóż kolejne prace", Category = "postoj", Sound = false,
        Status = status, StartedAt = Now.AddMinutes(-6), LastSeenAt = Now,
    };

    /// <summary>Druga odmiana postoju: maszyna stoi na 0 %, bo nikt nie wcisnął „Start”.</summary>
    private static Alert IdleAtZero(AlertStatus status = AlertStatus.Active) => new()
    {
        Key = AlertKey.Idle(MachineId), MachineId = MachineId, MachineName = "Aidite 3",
        Kind = AlertKind.MachineIdle, ShortText = AlertStateMachine.MachineIdleAtZeroShortText,
        Category = AlertStateMachine.IdleAtZeroCategory, Sound = false,
        Status = status, StartedAt = Now.AddMinutes(-4), LastSeenAt = Now,
    };

    private static MachineCardState Card(MachineStatusSnapshot? snapshot, params Alert[] alerts) =>
        MachineCardState.From(MachineId, snapshot, alerts, new TimingConfig(), Now);

    [Fact]
    public void NiepotwierdzonyBladDajeCzerwonaKarteZOpisemBledu()
    {
        // Dlaczego: dopóki nikt nie kliknął „Idę sprawdzić”, karta ma krzyczeć tak jak dotychczas.
        MachineCardState card = Card(Snapshot, Error(AlertStatus.Active));

        Assert.Equal("Błąd maszyny", card.State);
        Assert.Equal("Nie udało się pobrać tacki", card.Detail);
        Assert.Equal(MachineCardTone.Error, card.Tone);
    }

    [Fact]
    public void PoIdeSprawdzicKartaPokazujeSprawdzaneZGodzinaZgloszenia()
    {
        // Potwierdzony błąd ma być bursztynowy i mówić, że ktoś idzie.
        Alert acked = Error(AlertStatus.Acknowledged);

        MachineCardState card = Card(Snapshot, acked);

        Assert.Equal("Sprawdzane", card.State);
        Assert.Equal(MachineCardTone.Warning, card.Tone);
        Assert.Contains($"od {acked.AcknowledgedAt!.Value.ToLocalTime():HH:mm}", card.Detail, StringComparison.Ordinal);
        Assert.Contains("Nie udało się pobrać tacki", card.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EskalacjaPoAckWracaDoCzerwonejKarty()
    {
        MachineCardState card = Card(Snapshot, Error(AlertStatus.Escalated));

        Assert.Equal("Błąd maszyny", card.State);
        Assert.Equal(MachineCardTone.Error, card.Tone);
    }

    [Fact]
    public void PostojWyprzedzaOgraniczonaObserwacjeIPodajeGodzineOd100Procent()
    {
        var since = Now.AddMinutes(-8);
        MachineStatusSnapshot unresolved = Snapshot with { MainScreenVisible = null, ProgressFullSince = since };

        Assert.NotNull(ObservationWarning.For(unresolved, Now, new TimingConfig()));

        MachineCardState card = Card(unresolved, Idle());

        Assert.Equal("Stoi bez pracy (100 %)", card.State);
        Assert.Equal(MachineCardTone.Warning, card.Tone);
        Assert.Contains($"Od {since.ToLocalTime():HH:mm}", card.Detail, StringComparison.Ordinal);
        Assert.Contains("dołóż prace", card.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BezGodzinyStuProcentPostojPokazujePoczatekAlertu()
    {
        // Dlaczego: migawka z innego komputera (klaster) może nie mieć ProgressFullSince — karta nadal ma
        // podać operatorowi godzinę, a nie pusty tekst.
        Alert idle = Idle();

        MachineCardState card = Card(Snapshot with { ProgressFullSince = null }, idle);

        Assert.Contains($"Od {idle.StartedAt.ToLocalTime():HH:mm}", card.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PostojNaZerzeDajeBursztynowaKarteZPoleceniemWcisnijStart()
    {
        // Dlaczego: po resecie błędu bez „Start” maszyna stoi na 0 %
        // Ma pokazywać postój i konkretne polecenie.
        var since = Now.AddMinutes(-5);
        MachineStatusSnapshot zero = Snapshot with { LastProgressPercent = 0, ProgressZeroSince = since };

        MachineCardState card = Card(zero, IdleAtZero());

        Assert.Equal("Stoi na 0 %", card.State);
        Assert.Equal(MachineCardTone.Warning, card.Tone);
        Assert.Contains($"Od {since.ToLocalTime():HH:mm}", card.Detail, StringComparison.Ordinal);
        Assert.Contains("wciśnij Start", card.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BezGodzinyZeraPostojNaZerzePokazujePoczatekAlertu()
    {
        // Dlaczego: jak przy postoju na 100 % — migawka z innego komputera grupy może nie mieć
        // ProgressZeroSince, a karta i tak ma podać godzinę początku postoju.
        Alert idle = IdleAtZero();

        MachineCardState card = Card(Snapshot with { LastProgressPercent = 0, ProgressZeroSince = null }, idle);

        Assert.Equal("Stoi na 0 %", card.State);
        Assert.Contains($"Od {idle.StartedAt.ToLocalTime():HH:mm}", card.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroProcentBezAlertuPostojuZostajePodObserwacja()
    {
        // Dlaczego: przez pierwsze idleAtZeroAfterMs 0 % to normalny rozruch nowej pracy — karta ma wtedy
        // wyglądać jak dotychczas, a nie straszyć postojem, zanim próg minie.
        MachineCardState card = Card(Snapshot with { LastProgressPercent = 0 });

        Assert.Equal(MachineCardState.WatchedState, card.State);
        Assert.Equal(MachineCardTone.Ok, card.Tone);
        Assert.Contains("0%", card.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SkasowanyPostojZnikaZKarty()
    {
        // Dlaczego: „✕ Skasuj” (ACK alertu postoju) chowa pozycję od razu w pop-upie i w sekcji „Maszyny bez
        // pracy”; karta musi mówić to samo, inaczej Podgląd sam sobie przeczy.
        MachineCardState card = Card(Snapshot, Idle(AlertStatus.Acknowledged));

        Assert.Equal(MachineCardState.WatchedState, card.State);
        Assert.Equal(MachineCardTone.Ok, card.Tone);
    }

    [Fact]
    public void BladWyprzedzaPostojAInnyEkranWyprzedzaPostoj()
    {
        // Dlaczego: kolejność gałęzi jest częścią umowy — błąd jest ważniejszy od postoju, a przy innym
        // ekranie HMI odczyt procentu i tak nie jest wiarygodny.
        Assert.Equal("Błąd maszyny", Card(Snapshot, Idle(), Error(AlertStatus.Active)).State);
        Assert.Equal("Przełącz ekran maszyny", Card(Snapshot with { MainScreenVisible = false }, Idle()).State);
    }

    [Fact]
    public void BrakMigawkiIBrakPolaczeniaDajaBrakObserwacji()
    {
        // Dlaczego: maszyna bez migawki i maszyna rozłączona wyglądają
        // tak samo, a łączenie się ma własny tekst.
        Assert.Equal("Brak obserwacji", Card(null).State);
        Assert.Equal("Nie można teraz sprawdzić stanu maszyny.",
            Card(Snapshot with { State = MachineConnectionState.Error }).Detail);
        Assert.Equal("Łączę się z maszyną…",
            Card(Snapshot with { State = MachineConnectionState.Connecting }).Detail);
    }

    [Fact]
    public void ObrazZPlikuIOgraniczonaObserwacjaZostajaBezZmian()
    {
        Assert.Equal("Tryb testowy", Card(Snapshot with { Source = "plik" }).State);

        MachineCardState limited = Card(Snapshot with { OcrAvailable = false });
        Assert.Equal("Ograniczona obserwacja", limited.State);
        Assert.Equal(ObservationWarning.For(Snapshot with { OcrAvailable = false }, Now, new TimingConfig()), limited.Detail);
    }

    [Fact]
    public void PracujacaMaszynaJestZielonaZProcentem()
    {
        MachineCardState card = Card(Snapshot);

        Assert.Equal(MachineCardState.WatchedState, card.State);
        Assert.Equal("Postęp pracy: 42%", card.Detail);
        Assert.Equal(MachineCardTone.Ok, card.Tone);

        Assert.Equal("Brak wykrytego błędu. Postęp nie jest dostępny.",
            Card(Snapshot with { LastProgressPercent = null }).Detail);
    }

    [Fact]
    public void KazdyStanMaKrotkaNazweNaKafelekNakladki()
    {
        // Dlaczego: nakładka statusu ma wąskie kafelki, ale nie wolno jej mieć
        // własnego słownika stanów; krótka nazwa powstaje razem z pełną, w jednym miejscu.
        Assert.Equal("Błąd", Card(Snapshot, Error(AlertStatus.Active)).ShortState);
        Assert.Equal("Sprawdzane", Card(Snapshot, Error(AlertStatus.Acknowledged)).ShortState);
        Assert.Equal("Stoi 100 %", Card(Snapshot, Idle()).ShortState);
        Assert.Equal("Stoi 0 %", Card(Snapshot with { ProgressZeroSince = Now.AddMinutes(-4) }, IdleAtZero()).ShortState);
        Assert.Equal("Zły ekran", Card(Snapshot with { MainScreenVisible = false }).ShortState);
        Assert.Equal("Test", Card(Snapshot with { Source = "plik" }).ShortState);
        Assert.Equal("Ograniczona", Card(Snapshot with { OcrAvailable = false }).ShortState);
        Assert.Equal("Brak podglądu", Card(null).ShortState);
        Assert.Equal(MachineCardState.WatchedState, Card(Snapshot).ShortState);
    }

    [Fact]
    public void ProcentPracyJestPodawanyPrzyKazdymStanie()
    {
        // Dlaczego: kafelek nakładki pokazuje postęp także przy błędzie i postoju, gdzie opis karty mówi
        // o czymś innym. Brak odczytu ma dać kreskę, a nie puste miejsce ani „0 %”.
        Assert.Equal("42 %", Card(Snapshot).Progress);
        Assert.Equal("42 %", Card(Snapshot, Error(AlertStatus.Active)).Progress);
        Assert.Equal("100 %", Card(Snapshot with { LastProgressPercent = 100 }, Idle()).Progress);
        Assert.Equal(MachineCardState.NoProgress, Card(Snapshot with { LastProgressPercent = null }).Progress);
        Assert.Equal(MachineCardState.NoProgress, Card(null).Progress);
    }

    [Fact]
    public void AlertyInnychMaszynNieWplywajaNaKarte()
    {
        // Dlaczego: From() dostaje pełną listę widocznych alertów (także z innych komputerów grupy).
        Alert other = Error(AlertStatus.Active) with { MachineId = "aidite7", Key = AlertKey.ForRule("aidite7", "tacka") };

        Assert.Equal(MachineCardState.WatchedState, Card(Snapshot, other).State);
    }
}
