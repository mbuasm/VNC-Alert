using MaszynyAlert.Core.Config;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;

namespace MaszynyAlert.Probe.Tests;

/// <summary>Opcje <c>probe.exe diagnoza</c>, harmonogram przebiegu i wybór maszyn (<c>--config</c>/<c>--machines</c>/<c>--all</c>).</summary>
public sealed class DiagnozaOptionsTests
{
    [Fact]
    public void Domyslne_35MinutIKlatkaCo20s_ZgodneZInstrukcja()
    {
        DiagnozaOptions o = DiagnozaOptions.Parse([]);

        Assert.Equal(35 * 60, o.TotalSeconds);
        Assert.Equal(20, o.FrameIntervalSeconds);
        Assert.False(o.NoZip);
        Assert.False(o.NoOcr);
    }

    [Fact]
    public void SecondsNadpisujeMinuty_INoZipNoOcrSaCzytane()
    {
        DiagnozaOptions o = DiagnozaOptions.Parse(["--minuty", "10", "--seconds", "12", "--co-sekund", "1", "--no-zip", "--no-ocr"]);

        Assert.Equal(12, o.TotalSeconds);
        Assert.Equal(1, o.FrameIntervalSeconds);
        Assert.True(o.NoZip);
        Assert.True(o.NoOcr);
    }

    [Theory]
    [InlineData("--minuty", "0")]
    [InlineData("--co-sekund", "x")]
    [InlineData("--seconds", "-1")]
    public void BledneWartosci_RzucajaProbeUsageExceptionPoPolsku(string option, string value)
    {
        var ex = Assert.Throws<ProbeUsageException>(() => DiagnozaOptions.Parse([option, value]));
        Assert.Contains("Opcja", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OdstepWiekszyNizPrzebieg_JestBledemUzycia()
    {
        Assert.Throws<ProbeUsageException>(() => DiagnozaOptions.Parse(["--seconds", "10", "--co-sekund", "20"]));
    }

    [Fact]
    public void Harmonogram_PelnegoPrzebiegu_MaPunktyZZadania()
    {
        DiagnozaSchedule s = DiagnozaSchedule.For(35 * 60, 20);

        Assert.Equal([0, 5, 30, 60, 120], s.BlackTestOffsetsSeconds);
        Assert.Equal(180, s.DisconnectAtSeconds);
        Assert.Equal(30, s.DisconnectForSeconds);
        Assert.Equal(240, s.SharedTestAtSeconds);
        Assert.Equal(300, s.RateMeasureAtSeconds);
        Assert.Equal(60, s.RateMeasureForSeconds);
        Assert.Equal(120, s.OcrEverySeconds);
        Assert.Equal(60, s.RetryUnreachableEverySeconds);
    }

    [Fact]
    public void Harmonogram_KrotkiegoPrzebiegu_SciskaPunktyProporcjonalnieIZachowujeKolejnosc()
    {
        // Dlaczego: test na fałszywym serwerze z --seconds 12 ma przejść przez WSZYSTKIE fazy (rozłączenie, shared-test, pomiar),
        // więc każdy punkt musi wypaść przed końcem przebiegu i w tej samej kolejności, co w pełnym harmonogramie.
        DiagnozaSchedule s = DiagnozaSchedule.For(12, 1);

        Assert.True(s.BlackTestOffsetsSeconds.SequenceEqual(s.BlackTestOffsetsSeconds.OrderBy(x => x)));
        Assert.True(s.BlackTestOffsetsSeconds[^1] < s.DisconnectAtSeconds);
        Assert.True(s.DisconnectAtSeconds < s.SharedTestAtSeconds);
        Assert.True(s.SharedTestAtSeconds < s.RateMeasureAtSeconds);
        Assert.True(s.RateMeasureAtSeconds + s.RateMeasureForSeconds <= 12);
        Assert.All(s.ReconnectBackoffSeconds, b => Assert.True(b <= 2));
    }

    [Fact]
    public void DlugiOptions_DomyslneIIntervalS()
    {
        DlugiOptions d = DlugiOptions.Parse([]);
        Assert.Null(d.TotalSeconds);
        Assert.Equal(600, d.OkresowaSek);
        Assert.Equal(300, d.OdpytywanieCoSek);
        Assert.Equal(15, d.OdpytywanieTrwaSek);
        Assert.Equal(2048L * 1024 * 1024, d.LimitBytes);

        DlugiOptions t = DlugiOptions.Parse(["--seconds", "9", "--interval-s", "1", "--limit-mb", "1"]);
        Assert.Equal(9, t.TotalSeconds);
        Assert.Equal(5, t.OkresowaSek);
        Assert.Equal(3, t.OdpytywanieCoSek);
        Assert.Equal(1, t.OdpytywanieTrwaSek);
        Assert.Equal(1, t.MinOdstepZmianySek);
        Assert.Equal(1, t.MinOdczytSek);
        Assert.Equal(1024 * 1024, t.LimitBytes);
    }

    // --- Wybór maszyn ------------------------------------------------------------------------------------------

    private static AppConfig Cfg() => DiagnozaTestConfig.Config(
        DiagnozaTestConfig.Machine("a", 5900, enabled: true),
        DiagnozaTestConfig.Machine("b", 5901, enabled: false),
        DiagnozaTestConfig.Machine("c", 5902, enabled: true));

    [Fact]
    public void Select_BezFiltra_TylkoWlaczone()
    {
        MachineListResult r = MachineList.Select(Cfg(), "x", new GlobalOptions("out", null, false));

        Assert.Equal(["a", "c"], r.Selected.Select(m => m.Id));
        Assert.False(r.FilterActive);
    }

    [Fact]
    public void Select_Machines_ObejmujeWylaczoneIZachowujeKolejnoscZOpcji()
    {
        MachineListResult r = MachineList.Select(Cfg(), "x", new GlobalOptions("out", null, false, Machines: ["B", "a"]));

        Assert.Equal(["b", "a"], r.Selected.Select(m => m.Id));
        Assert.True(r.FilterActive);
    }

    [Fact]
    public void Select_All_WszystkieMaszyny()
    {
        MachineListResult r = MachineList.Select(Cfg(), "x", new GlobalOptions("out", null, false, All: true));

        Assert.Equal(["a", "b", "c"], r.Selected.Select(m => m.Id));
        Assert.True(r.FilterActive);
    }

    [Fact]
    public void Select_NieznanyIdentyfikator_BladUzyciaZListaDostepnych()
    {
        var ex = Assert.Throws<ProbeUsageException>(() => MachineList.Select(Cfg(), "x", new GlobalOptions("out", null, false, Machines: ["zzz"])));

        Assert.Contains("zzz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a, b, c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractGlobal_CzytaConfigMachinesIAll()
    {
        (GlobalOptions g, List<string> rest) = ArgsUtil.ExtractGlobal(["--config", "c.json", "--machines", "a,b;c", "--all", "--seconds", "5"]);

        Assert.Equal("c.json", g.ConfigPath);
        Assert.Equal(["a", "b", "c"], g.Machines);
        Assert.True(g.All);
        Assert.True(g.HasMachineFilter);
        Assert.Equal(["--seconds", "5"], rest);
    }

    [Fact]
    public void Load_ConfigWskazujeNieistniejacyPlik_BladUzycia()
    {
        Assert.Throws<ProbeUsageException>(() => MachineList.Load(new GlobalOptions("out", null, false, ConfigPath: Path.Combine(Path.GetTempPath(), "nie-ma-takiego.json"))));
    }
}
