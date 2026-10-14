using MaszynyAlert.App.Views;

namespace MaszynyAlert.App.Tests;

public sealed class AdminLoginThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BezOdmowy_NieTrzebaCzekac()
    {
        var throttle = new AdminLoginThrottle();

        Assert.Equal(TimeSpan.Zero, throttle.Remaining(T0));
    }

    [Fact]
    public void PoOdmowie_CzekaTrzySekundy()
    {
        var throttle = new AdminLoginThrottle();
        throttle.RegisterFailure(T0);

        Assert.Equal(TimeSpan.FromSeconds(2.5), throttle.Remaining(T0.AddSeconds(0.5)));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.Remaining(T0.AddSeconds(2)));
        Assert.Equal(TimeSpan.Zero, throttle.Remaining(T0.AddSeconds(3.5)));
    }

    [Theory]
    [InlineData(2.5, "Poczekaj jeszcze 3 s — hasło nie zostało sprawdzone.")]
    [InlineData(0.2, "Poczekaj jeszcze 1 s — hasło nie zostało sprawdzone.")]
    public void KomunikatOczekiwania_NieMowiONieprawidlowymHasle(double seconds, string expected)
    {
        string text = AdminLoginThrottle.WaitMessage(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, text);
        Assert.DoesNotContain("Nieprawidłowe", text);
    }
}
