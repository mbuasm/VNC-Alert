using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

public sealed class WatchProgressRoiTests
{
    private static readonly RectInt Measured = new(420, 246, 370, 30);
    private static readonly RectInt Guessed = new(501, 245, 134, 32);

    private static IdleWatchProfile Idle(RectInt roi, bool enabled = true) =>
        new() { Enabled = enabled, ProgressRoi = roi };

    private static MachineAnchors Anchors(RectInt roi, bool guess) =>
        new() { ProgressRoi = roi, ProgressRoiIsGuess = guess };

    [Fact]
    public void ZgadnietaKotwica_NieWypieraZmierzonegoObszaruZProfilu()
    {
        Assert.Equal(Measured, WatchCommand.ChooseProgressRoi(Idle(Measured), Anchors(Guessed, guess: true)));
    }

    [Fact]
    public void KotwicaZOdczytanaWartoscia_MaPierwszenstwoPrzedProfilem()
    {
        var trafiona = new RectInt(164, 93, 82, 28);
        Assert.Equal(trafiona, WatchCommand.ChooseProgressRoi(Idle(Measured), Anchors(trafiona, guess: false)));
    }

    [Fact]
    public void BezKotwicy_CzytamyZProfilu_ZamiastNieCzytacWcale()
    {
        Assert.Equal(Measured, WatchCommand.ChooseProgressRoi(Idle(Measured), anchors: null));
        Assert.Equal(Measured, WatchCommand.ChooseProgressRoi(Idle(Measured), new MachineAnchors()));
    }

    [Fact]
    public void GdyProfilNieMaObszaru_ZostajeNawetZgadnietaKotwica()
    {
        // Lepsze zgadywanie niż nic: bez obszaru w profilu nie ma czego wybrać, a kolumna „pole postępu”
        // w raporcie i tak mówi wprost, że wartość jest zgadnięta.
        Assert.Equal(Guessed, WatchCommand.ChooseProgressRoi(idle: null, Anchors(Guessed, guess: true)));
        Assert.Equal(Guessed, WatchCommand.ChooseProgressRoi(Idle(RectInt.Empty), Anchors(Guessed, guess: true)));
        Assert.Equal(Guessed, WatchCommand.ChooseProgressRoi(Idle(Measured, enabled: false), Anchors(Guessed, guess: true)));
    }

    [Fact]
    public void ObszarPaskaPostepu_TrybBar_NieJestCzytanyJakoLiczba()
    {
        // W trybie "bar" progressRoi opisuje PASEK, nie liczbę — OCR wymyślałby z niego procenty.
        // Krok ocr odmawia w tym samym przypadku (DescribeConfiguredProgressAsync).
        var bar = new IdleWatchProfile { Enabled = true, Mode = ProgressMode.Bar, ProgressRoi = Measured };

        Assert.Null(WatchCommand.ConfiguredProgressRoi(bar));
        Assert.Null(WatchCommand.ChooseProgressRoi(bar, anchors: null));
        Assert.Equal(Guessed, WatchCommand.ChooseProgressRoi(bar, Anchors(Guessed, guess: true)));
    }

    [Fact]
    public void GdyNieMaAniProfiluAniKotwicy_NieMaObszaru()
    {
        Assert.Null(WatchCommand.ChooseProgressRoi(idle: null, anchors: null));
        Assert.Null(WatchCommand.ChooseProgressRoi(Idle(RectInt.Empty), new MachineAnchors()));
    }
}
