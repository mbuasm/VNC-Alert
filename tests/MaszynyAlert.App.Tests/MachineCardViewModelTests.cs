using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.Tests;

public sealed class MachineCardViewModelTests
{
    private const string MachineId = "aidite3";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

    private static MachineStatusSnapshot Snapshot => new()
    {
        MachineId = MachineId,
        Name = "Aidite 3",
        Host = "unused",
        Profile = "opencnc",
        State = MachineConnectionState.Connected,
        MainScreenWatchRequired = true,
        MainScreenVisible = true,
        OcrAvailable = true,
        LastFrameAt = Now.AddSeconds(-2),
        LastProgressPercent = 42,
    };

    private static Alert Error(AlertStatus status) => new()
    {
        Key = AlertKey.ForRule(MachineId, "tacka"),
        MachineId = MachineId,
        MachineName = "Aidite 3",
        Kind = AlertKind.Error,
        ShortText = "Nie udało się pobrać tacki",
        Category = "tacka",
        Sound = true,
        Status = status,
        StartedAt = Now.AddMinutes(-9),
        LastSeenAt = Now,
        AcknowledgedAt = status == AlertStatus.Active ? null : Now.AddMinutes(-4),
    };

    private static MachineCardViewModel Card(params Alert[] alerts) =>
        MachineCardViewModel.From(MachineId, "Aidite 3", Snapshot, alerts, new TimingConfig(), Now);

    [Fact]
    public void TrzyStanyKartyMajaTrzyRozneKolory()
    {
        MachineCardViewModel error = Card(Error(AlertStatus.Active));
        MachineCardViewModel checking = Card(Error(AlertStatus.Acknowledged));
        MachineCardViewModel watched = Card();

        Assert.Equal("Błąd maszyny", error.State);
        Assert.Equal(MachineCardViewModel.ErrorAccent, error.Accent);

        Assert.Equal("Sprawdzane", checking.State);
        Assert.Equal(MachineCardViewModel.WarningAccent, checking.Accent);

        Assert.Equal(MachineCardState.WatchedState, watched.State);
        Assert.Equal(MachineCardViewModel.OkAccent, watched.Accent);
    }

    [Fact]
    public void KartaZachowujeIdentyfikatorINazweMaszyny()
    {
        // Dlaczego: przycisk „Pokaż ekran” wiąże się z Id karty, a nagłówek kafelka z nazwą.
        MachineCardViewModel card = Card();

        Assert.Equal(MachineId, card.Id);
        Assert.Equal("Aidite 3", card.Name);
        Assert.Equal("✓", card.Symbol);
    }

    [Fact]
    public void KafelekNakladkiBierzeKrotkiStanIProcentZKartyPodgladu()
    {
        // Dlaczego: nakładka i Podgląd mają jeden zestaw tekstów. Kafelek różni
        // się od karty wyłącznie tym, że bierze krótszą nazwę stanu i procent pracy.
        MachineCardViewModel error = Card(Error(AlertStatus.Active));
        MachineCardViewModel watched = Card();

        Assert.Equal("Błąd", error.ShortState);
        Assert.Equal("42 %", error.Progress);
        Assert.Equal(MachineCardState.WatchedState, watched.ShortState);
        Assert.Equal("42 %", watched.Progress);
    }

    [Fact]
    public void MigaTylkoKafelekZNiepotwierdzonymBledem()
    {
        // Dlaczego: miganie ma zwracać uwagę na awarię, a nie na maszynę, przy której ktoś już stoi.
        Assert.True(Card(Error(AlertStatus.Active)).Alarming);
        Assert.True(Card(Error(AlertStatus.Escalated)).Alarming);
        Assert.False(Card(Error(AlertStatus.Acknowledged)).Alarming);
        Assert.False(Card().Alarming);
    }

    [Fact]
    public void WyroznienieKartyNieZmieniaPozostalychPol()
    {
        // Dlaczego: kliknięcie kafelka nakładki wyróżnia kartę w Podglądzie — wyróżnienie jest dodatkiem
        // do karty, a nie inną kartą.
        MachineCardViewModel card = Card();

        MachineCardViewModel highlighted = card with { Highlighted = true };

        Assert.False(card.Highlighted);
        Assert.True(highlighted.Highlighted);
        Assert.Equal(card.State, highlighted.State);
        Assert.Equal(card.Accent, highlighted.Accent);
        Assert.NotEqual(card, highlighted);
    }

    [Theory]
    [InlineData(MachineCardTone.Error, MachineCardViewModel.ErrorAccent)]
    [InlineData(MachineCardTone.Warning, MachineCardViewModel.WarningAccent)]
    [InlineData(MachineCardTone.Ok, MachineCardViewModel.OkAccent)]
    public void KazdyKolorKartyMaSwojKodXaml(MachineCardTone tone, string expected)
    {
        // Dlaczego: nakładka statusu ma używać tej samej zamiany co Podgląd.
        Assert.Equal(expected, MachineCardViewModel.AccentFor(tone));
    }
}
