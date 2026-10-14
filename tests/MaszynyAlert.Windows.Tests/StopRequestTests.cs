using MaszynyAlert.Windows;

namespace MaszynyAlert.Windows.Tests;

public sealed class StopRequestTests
{
    // Testy nie mogą dotknąć prawdziwych nazw (Global\MaszynyAlert.App), bo na stanowisku deweloperskim
    // mógłby akurat działać program: każdy test dostaje własną parę nazw w obrębie sesji.
    private static string Unique(string kind) => $@"Local\MaszynyAlert.StopTest.{kind}." + Guid.NewGuid().ToString("N");

    /// <summary>Ta sama nazwa w przestrzeni Global\ — liczona tu niezależnie od kodu programu.</summary>
    private static string GlobalOf(string localName) => @"Global\" + localName[@"Local\".Length..];

    [Fact]
    public async Task RequestDuringStartupIsRetainedAndSubsequentRequestsAreDelivered()
    {
        string name = Unique("Event");
        using var listener = new StopRequest(name);
        using var received = new SemaphoreSlim(0);
        Assert.True(StopRequest.TryRequest(name));
        listener.Listen(() => received.Release());
        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(StopRequest.TryRequest(name));
        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void MissingApplicationDoesNotClaimToHaveAskedAnybody()
    {
        string name = Unique("Event");
        Assert.False(StopRequest.TryRequest(name));
        using (var listener = new StopRequest(name))
            Assert.True(StopRequest.TryRequest(name));
        Assert.False(StopRequest.TryRequest(name));
    }

    [Fact]
    public void StopCommandWaitsUntilTheApplicationReleasesTheSingleInstanceMutex()
    {
        string eventName = Unique("Event");
        string mutexName = Unique("Mutex");
        using var ready = new ManualResetEventSlim();
        var application = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
            using var stopRequest = new StopRequest(eventName);
            using var shutdown = new ManualResetEventSlim();
            stopRequest.Listen(() => shutdown.Set());
            ready.Set();
            shutdown.Wait(TimeSpan.FromSeconds(20));
        })
        { IsBackground = true };
        application.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(StopRequest.IsRunning(mutexName));

        Assert.Equal(0, StopRequest.RequestStop(TimeSpan.FromSeconds(10), eventName, mutexName));
        Assert.True(application.Join(TimeSpan.FromSeconds(5)));
        Assert.False(StopRequest.IsRunning(mutexName));
    }

    [Fact]
    public void StopCommandReportsFailureWhenTheApplicationKeepsRunning()
    {
        string eventName = Unique("Event");
        string mutexName = Unique("Mutex");
        // Program trzyma mutex, ale nikt nie obsługuje sygnału (np. zawieszony wątek interfejsu).
        using var mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        Assert.True(createdNew);
        using var ignored = new StopRequest(eventName);

        Assert.Equal(1, StopRequest.RequestStop(TimeSpan.FromMilliseconds(300), eventName, mutexName));
    }

    [Fact]
    public void GlobalCounterpartOfTheSessionSignal()
    {
        Assert.Equal(@"Global\MaszynyAlert.StopRequest", StopRequest.GlobalNameFor(StopRequest.EventName));
        Assert.Null(StopRequest.GlobalNameFor(@"Global\Cokolwiek"));
    }

    [Fact]
    public void StopCommandFromAnotherSessionReachesTheApplicationThroughTheGlobalSignal()
    {
        string eventName = Unique("Event");
        string mutexName = Unique("Mutex");
        using var ready = new ManualResetEventSlim();
        var application = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
            using var global = new EventWaitHandle(false, EventResetMode.AutoReset, GlobalOf(eventName));
            ready.Set();
            global.WaitOne(TimeSpan.FromSeconds(20));
        })
        { IsBackground = true };
        application.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal(0, StopRequest.RequestStop(TimeSpan.FromSeconds(5), eventName, mutexName));
        Assert.True(application.Join(TimeSpan.FromSeconds(5)));
    }

    [InteractiveSessionFact]
    public void ListenerReceivesTheGlobalSignalToo()
    {
        string name = Unique("Event");
        using var listener = new StopRequest(name);
        using var received = new SemaphoreSlim(0);
        listener.Listen(() => received.Release());

        Assert.True(StopRequest.TryRequest(GlobalOf(name)));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
    }

    [InteractiveSessionFact]
    public void GlobalSignalCreatedBySomebodyElseIsIgnored()
    {
        string name = Unique("Event");
        using var squatter = new EventWaitHandle(false, EventResetMode.AutoReset, GlobalOf(name));
        using var listener = new StopRequest(name);
        using var received = new SemaphoreSlim(0);
        listener.Listen(() => received.Release());

        squatter.Set();

        Assert.False(received.Wait(TimeSpan.FromMilliseconds(500)));
        Assert.True(StopRequest.TryRequest(name));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void StopCommandSucceedsWhenTheApplicationIsNotRunning()
    {
        Assert.Equal(0, StopRequest.RequestStop(TimeSpan.FromSeconds(10), Unique("Event"), Unique("Mutex")));
    }
}
