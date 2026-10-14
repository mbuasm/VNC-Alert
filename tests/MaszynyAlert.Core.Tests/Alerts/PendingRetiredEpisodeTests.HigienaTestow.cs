using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed partial class PendingRetiredEpisodeTests
{
    [Fact]
    public void Higiena_GroupClosureWithoutClearMarker_PendingFirstReadIsBoundedByTheExistingRejoinWindow()
    {
        var clock = new FakeClock();
        var state = State(clock, group: true);
        Alert first = state.OnRedConfirmed(Machine, Home, null);                      // t = 0
        Assert.True(state.AcknowledgeLocal(first.Key, "PC-test", "operator"));
        clock.AdvanceMs(31000);                                                       // t = 31: zamknięcie z grupy
        state.ReconcileGroupState([], [new EpisodeClosure(first.EpisodeId, Machine.Id, AlertKind.Error, clock.UtcNow)]);
        Assert.Empty(state.ActiveAlerts);
        var events = new List<AlertEvent>();
        state.Changed += events.Add;

        var read = new TaskCompletionSource<MappedMessage>();                         // OCR, który się nie kończy
        clock.AdvanceMs(1000);                                                        // t = 32: czerwień wraca
        DateTimeOffset firstRed = clock.UtcNow;
        Assert.True(state.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
        for (int second = 33; second < 151; second++)
        {
            clock.AdvanceMs(1000);
            Assert.True(state.OnRedConfirmedWithFirstRead(Machine, Contentless, null, read.Task));
            state.Tick();
            Assert.Empty(state.ActiveAlerts);                                         // oczekiwanie prywatne
        }
        Assert.Empty(events);

        clock.AdvanceMs(1000);                                                        // t = 151: 120 s od zamknięcia
        state.Tick();

        Alert raised = Assert.Single(state.ActiveAlerts);
        Assert.Equal(AlertStatus.Active, raised.Status);
        Assert.Null(raised.AcknowledgedAt);
        Assert.Equal(firstRed, raised.StartedAt);
        Assert.NotEqual(first.EpisodeId, raised.EpisodeId);
        Assert.Single(events, e => e.Kind == AlertEventKind.Raised);
    }
}
