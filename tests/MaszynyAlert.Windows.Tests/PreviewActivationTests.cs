using MaszynyAlert.Windows;

namespace MaszynyAlert.Windows.Tests;

public sealed class PreviewActivationTests
{
    [Fact]
    public async Task RequestDuringStartupIsRetainedAndSubsequentRequestsAreDelivered()
    {
        string name = @"Local\MaszynyAlert.PreviewTest." + Guid.NewGuid().ToString("N");
        using var listener = new PreviewActivation(name);
        using var received = new SemaphoreSlim(0);
        Assert.True(PreviewActivation.TryRequest(name));
        listener.Listen(() => received.Release());
        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(PreviewActivation.TryRequest(name));
        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void MissingOrClosedApplicationDoesNotClaimToOpenPreview()
    {
        string name = @"Local\MaszynyAlert.PreviewTest." + Guid.NewGuid().ToString("N");
        Assert.False(PreviewActivation.TryRequest(name));
        using (var listener = new PreviewActivation(name))
            Assert.True(PreviewActivation.TryRequest(name));
        Assert.False(PreviewActivation.TryRequest(name));
    }
}
