using MaszynyAlert.App.Services;

namespace MaszynyAlert.App.Tests;

public sealed class PeersChangedNotificationTests
{
    [Fact]
    public void FailingPeerWarnings_NeverBlockTheRefreshOfThePanel()
    {
        int notified = 0;
        Exception? reported = null;

        AppOrchestrator.LogPeerWarningsThenNotify(
            () => throw new InvalidOperationException("Operations that change non-concurrent collections must have exclusive access."),
            () => notified++,
            ex => reported = ex);

        Assert.Equal(1, notified);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void WarningsAreLoggedBeforeThePanelRefresh()
    {
        var order = new List<string>();
        AppOrchestrator.LogPeerWarningsThenNotify(() => order.Add("log"), () => order.Add("panel"), _ => order.Add("błąd"));
        Assert.Equal(["log", "panel"], order);
    }
}
