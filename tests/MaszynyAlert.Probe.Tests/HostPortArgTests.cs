using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

public sealed class HostPortArgTests
{
    private const string Example = "probe.exe capture 192.168.1.108:9100";

    [Theory]
    [InlineData("192.168.1.108:9100", "192.168.1.108", 9100)]
    [InlineData("aidite1.local:5900", "aidite1.local", 5900)]
    [InlineData("10.0.0.1:1", "10.0.0.1", 1)]
    [InlineData("10.0.0.1:65535", "10.0.0.1", 65535)]
    public void Parse_PoprawnegoAdresu_ZwracaHostIPort(string arg, string host, int port)
    {
        Assert.Equal((host, port), HostPortArg.Parse(arg, Example));
    }

    [Fact]
    public void Parse_AdresuZKilkomaDwukropkami_BierzeOstatniJakoSeparatorPortu()
    {
        // Dlaczego (zachowanie utrwalone): adres IPv6 zapisuje się z dwukropkami — separatorem portu jest
        // OSTATNI dwukropek, więc „::1:5900” to host „::1” na porcie 5900.
        Assert.Equal(("::1", 5900), HostPortArg.Parse("::1:5900", Example));
    }

    [Theory]
    [InlineData("192.168.1.108")]   // brak portu
    [InlineData(":9100")]           // brak adresu
    [InlineData("192.168.1.108:")]  // dwukropek na końcu
    public void Parse_AdresuBezPortuAlboBezHosta_RzucaProbeUsageExceptionZPrzykladem(string arg)
    {
        ProbeUsageException ex = Assert.Throws<ProbeUsageException>(() => HostPortArg.Parse(arg, Example));

        Assert.Contains(Example, ex.Message, StringComparison.Ordinal);
        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("192.168.1.108:abc")]
    [InlineData("192.168.1.108:0")]
    [InlineData("192.168.1.108:70000")]
    [InlineData("192.168.1.108:-1")]
    public void Parse_NieprawidlowegoPortu_RzucaProbeUsageExceptionZZakresem(string arg)
    {
        ProbeUsageException ex = Assert.Throws<ProbeUsageException>(() => HostPortArg.Parse(arg, Example));

        Assert.Contains("1-65535", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10,20,30,40")]
    [InlineData(" 10 , 20 , 30 , 40 ")] // spacje wokół liczb są dopuszczalne
    public void ParseRoi_PoprawnejWartosci_ZwracaProstokat(string value)
    {
        Assert.Equal(new RectInt(10, 20, 30, 40), HostPortArg.ParseRoi(value));
    }

    [Theory]
    [InlineData("10,20,30")]        // za mało liczb
    [InlineData("10,20,30,40,50")]  // za dużo liczb
    [InlineData("a,b,c,d")]
    [InlineData("")]
    public void ParseRoi_NieprawidlowejWartosci_RzucaProbeUsageExceptionZOpisemFormatu(string value)
    {
        // Dlaczego: to użytkownik wpisuje cztery liczby z instrukcji — komunikat ma powtórzyć, jakie
        // to mają być liczby i w jakiej kolejności.
        ProbeUsageException ex = Assert.Throws<ProbeUsageException>(() => HostPortArg.ParseRoi(value));

        Assert.Contains("x,y,width,height", ex.Message, StringComparison.Ordinal);
    }
}
