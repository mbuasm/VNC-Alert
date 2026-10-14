using MaszynyAlert.Windows;

namespace MaszynyAlert.Windows.Tests;

public sealed class SharedConfigReloadRequestTests
{
    private static string Unique() => @"Local\MaszynyAlert.ReloadTest." + Guid.NewGuid().ToString("N");

    [Fact]
    public void ProductionNameIsSessionLocal() =>
        Assert.Equal(@"Local\MaszynyAlert.SharedConfigReload", SharedConfigReloadRequest.EventName);

    [Fact]
    public void WithoutAProgramNobodyIsAsked()
    {
        string name = Unique();
        Assert.False(SharedConfigReloadRequest.TryRequest(name));
        using (new SharedConfigReloadRequest(name))
            Assert.True(SharedConfigReloadRequest.TryRequest(name));
        Assert.False(SharedConfigReloadRequest.TryRequest(name));
    }

    [Fact]
    public async Task RequestBeforeListenIsKeptAndLaterRequestsArrive()
    {
        string name = Unique();
        using var listener = new SharedConfigReloadRequest(name);
        using var received = new SemaphoreSlim(0);
        Assert.True(SharedConfigReloadRequest.TryRequest(name));

        listener.Listen(() => received.Release());

        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(SharedConfigReloadRequest.TryRequest(name));
            Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void ListeningTwiceIsRefused()
    {
        using var listener = new SharedConfigReloadRequest(Unique());
        listener.Listen(() => { });
        Assert.Throws<InvalidOperationException>(() => listener.Listen(() => { }));
    }

    [Fact]
    public void DoesNotShareTheStopSignal()
    {
        Assert.NotEqual(StopRequest.EventName, SharedConfigReloadRequest.EventName);
    }
}
