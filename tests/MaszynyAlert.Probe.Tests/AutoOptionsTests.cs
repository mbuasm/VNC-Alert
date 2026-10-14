using MaszynyAlert.Probe;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

/// <summary>Opcje <c>probe.exe auto</c> / <c>watch</c> — wartości domyślne i błędy użycia po polsku.</summary>
public sealed class AutoOptionsTests
{
    [Fact]
    public void Domyslne_15MinutIPort47777_ZgodneZInstrukcja()
    {
        Assert.Equal(15, AutoCommand.ParseIntervalMinutes([]));
        Assert.Equal(15, WatchCommand.ParseIntervalMinutes([]));
        Assert.Equal(47777, AutoCommand.ParsePort([]));
        Assert.Equal(47777, AutoCommand.DefaultBusPort);
        Assert.Null(AutoCommand.ParseOptionalSeconds([]));
        Assert.Null(WatchCommand.ParseOptionalSeconds([]));
    }

    [Fact]
    public void PodaneWartosci_SaOdczytywane()
    {
        Assert.Equal(90, AutoCommand.ParseOptionalSeconds(["--seconds", "90"]));
        Assert.Equal(5, AutoCommand.ParseIntervalMinutes(["--interval-min", "5"]));
        Assert.Equal(48000, AutoCommand.ParsePort(["--port", "48000"]));
        Assert.Equal(7, WatchCommand.ParseIntervalMinutes(["--interval-min", "7"]));
    }

    [Theory]
    [InlineData("--seconds", "0")]
    [InlineData("--seconds", "abc")]
    [InlineData("--seconds", "-5")]
    [InlineData("--interval-min", "0")]
    [InlineData("--interval-min", "x")]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    public void BledneWartosci_RzucajaProbeUsageExceptionPoPolsku(string option, string value)
    {
        // Dlaczego: użytkownik nie jest programistą — błąd ma być zdaniem po polsku, nie stack trace'em.
        var ex = Assert.Throws<ProbeUsageException>(() =>
        {
            _ = AutoCommand.ParseOptionalSeconds([option, value]);
            _ = AutoCommand.ParseIntervalMinutes([option, value]);
            _ = AutoCommand.ParsePort([option, value]);
        });

        Assert.Contains("Opcja", ex.Message, StringComparison.Ordinal);
    }
}
