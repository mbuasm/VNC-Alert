using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

public sealed class ArgsUtilTests
{
    [Fact]
    public void ExtractGlobal_BezOpcji_DajeWartosciDomyslneIZwracaWszystkieArgumenty()
    {
        (GlobalOptions global, List<string> remaining) = ArgsUtil.ExtractGlobal(["--seconds", "30"]);

        Assert.Equal("out", global.OutDir);
        Assert.Null(global.Password);
        Assert.Null(global.Username);
        Assert.False(global.NoAuthPrompt);
        Assert.Equal(["--seconds", "30"], remaining);
    }

    [Fact]
    public void ExtractGlobal_Username_ZdejmujeOpcjeIZwracaNazwe()
    {
        (GlobalOptions global, List<string> remaining) = ArgsUtil.ExtractGlobal(
            ["info", "--username", "serwis", "--password", "tajne"]);

        Assert.Equal("serwis", global.Username);
        Assert.Equal("tajne", global.Password);
        Assert.Equal(["info"], remaining);
    }

    [Fact]
    public void MaskForReport_MaskujeHasloAleNieNazweUzytkownika()
    {
        // Nazwa użytkownika nie jest sekretem (hasło zostaje w magazynie DPAPI / w --password) i musi
        // być widoczna w raporcie — inaczej nie da się sprawdzić, z jaką nazwą próbowano się logować.
        string masked = ArgsUtil.MaskForReport(["info", "--username", "serwis", "--password", "tajne"]);

        Assert.Contains("--username serwis", masked, StringComparison.Ordinal);
        Assert.Contains("--password ***", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("tajne", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractGlobal_OpcjiGlobalnychWDowolnymMiejscu_ZdejmujeJeIZostawiaReszte()
    {
        // Dlaczego: użytkownik dopisuje --out/--password tam, gdzie mu wygodnie (zwykle na końcu) —
        // kolejność nie może decydować o tym, czy program je zauważy.
        (GlobalOptions global, List<string> remaining) = ArgsUtil.ExtractGlobal(
            ["--seconds", "30", "--out", "wyniki", "--no-auth-prompt", "--interval-min", "5", "--password", "tajne"]);

        Assert.Equal("wyniki", global.OutDir);
        Assert.Equal("tajne", global.Password);
        Assert.True(global.NoAuthPrompt);
        Assert.Equal(["--seconds", "30", "--interval-min", "5"], remaining);
    }

    [Fact]
    public void ExtractGlobal_NazwOpcjiPisanychWielkaLitera_RozpoznajeJeMimoTo()
    {
        // Dlaczego: operator kopiuje polecenie z instrukcji, czasem z autokorektą Windows — „--Out” ma
        // działać tak samo jak „--out”, zamiast cicho wylądować w argumentach komendy.
        (GlobalOptions global, List<string> remaining) = ArgsUtil.ExtractGlobal(["--OUT", "wyniki", "--No-Auth-Prompt"]);

        Assert.Equal("wyniki", global.OutDir);
        Assert.True(global.NoAuthPrompt);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ExtractGlobal_OpcjiBezWartosci_RzucaProbeUsageExceptionZPolskimKomunikatem()
    {
        // Dlaczego: „probe.exe info --out” (bez folderu) to najczęstsza literówka — ma dać jedno zdanie
        // po polsku z nazwą opcji, a nie IndexOutOfRangeException ze stosem wywołań.
        ProbeUsageException ex = Assert.Throws<ProbeUsageException>(() => ArgsUtil.ExtractGlobal(["--out"]));

        Assert.Contains("--out", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Brakuje wartości", ex.Message, StringComparison.Ordinal);

        Assert.Throws<ProbeUsageException>(() => ArgsUtil.ExtractGlobal(["info", "--password"]));
    }

    [Fact]
    public void ExtractGlobal_Null_RzucaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ArgsUtil.ExtractGlobal(null!));
    }

    [Fact]
    public void FindOption_ZwracaWartoscPierwszegoWystapienia()
    {
        // Dlaczego: na tym stoi rozbiór --seconds / --interval-min / --port w komendach auto i watch.
        Assert.Equal("30", ArgsUtil.FindOption(["--seconds", "30", "--seconds", "60"], "--seconds"));
        Assert.Equal("47777", ArgsUtil.FindOption(["--no-bus", "--port", "47777"], "--port"));
        Assert.Null(ArgsUtil.FindOption(["--no-zip"], "--seconds"));
    }

    [Fact]
    public void FindOption_OpcjiBezWartosciNaKoncu_RzucaProbeUsageException()
    {
        Assert.Throws<ProbeUsageException>(() => ArgsUtil.FindOption(["--seconds"], "--seconds"));
    }

    [Fact]
    public void FindOptionAll_ZwracaWszystkieWartosciOpcjiPowtarzalnej()
    {
        // Dlaczego: test sieci między komputerami (--peer) z natury podaje kilka adresów naraz.
        List<string> peers = ArgsUtil.FindOptionAll(["--peer", "10.0.0.1", "--peer", "10.0.0.2", "--port", "47777"], "--peer");

        Assert.Equal(["10.0.0.1", "10.0.0.2"], peers);
        Assert.Empty(ArgsUtil.FindOptionAll(["--port", "47777"], "--peer"));
    }

    [Theory]
    [InlineData(new[] { "--no-zip" }, "--no-zip", true)]
    [InlineData(new[] { "--NO-ZIP" }, "--no-zip", true)]
    [InlineData(new[] { "--no-bus", "--seconds", "30" }, "--no-bus", true)]
    [InlineData(new[] { "--seconds", "30" }, "--no-zip", false)]
    [InlineData(new string[0], "--no-bus", false)]
    public void HasFlag_RozpoznajePrzelacznikiBezWzgleduNaWielkoscLiter(string[] args, string name, bool oczekiwany)
    {
        Assert.Equal(oczekiwany, ArgsUtil.HasFlag(args, name));
    }

    [Fact]
    public void MaskForReport_ZastepujeHasloGwiazdkamiAResztaZostajeBezZmian()
    {
        string masked = ArgsUtil.MaskForReport(["auto", "--password", "tajne-haslo", "--seconds", "30"]);

        Assert.Equal("auto --password *** --seconds 30", masked);
        Assert.DoesNotContain("tajne-haslo", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskForReport_GdyPoOpcjiHaslaNicNieMa_NieWysypujeSieIZostawiaSamaOpcje()
    {
        // Dlaczego: to wywołanie i tak zaraz skończy się błędem użycia, ale raport powstaje WCZEŚNIEJ —
        // maskowanie nie może się wtedy wysypać i zabrać użytkownikowi komunikatu o literówce.
        Assert.Equal("info --password", ArgsUtil.MaskForReport(["info", "--password"]));
    }

    [Fact]
    public void MaskForReport_MaskujeKazdeWystapienieHasla()
    {
        Assert.Equal(
            "info --password *** --out wyniki --password ***",
            ArgsUtil.MaskForReport(["info", "--password", "a", "--out", "wyniki", "--password", "b"]));
    }

    [Fact]
    public void DomyslnyOdstepZrzutowOkresowych_WynosiPietnascieMinut()
    {
        Assert.Equal(15, WatchCommand.DefaultIntervalMinutes);
    }
}
