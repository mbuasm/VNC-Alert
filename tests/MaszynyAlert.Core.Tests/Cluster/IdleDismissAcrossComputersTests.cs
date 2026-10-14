using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class IdleDismissAcrossComputersTests
{
    /// <summary>
    /// Sekundy obserwacji „100 % nieprzerwanie” na obserwatorze, z tikiem klastra po każdej z nich.
    /// <c>TimingConfig.IdleAlertAfterMs</c> to 2 minuty, więc alert postoju powstaje po 120 sekundach.
    /// </summary>
    private static async Task ObserveIdle(GroupRuntimeTests.Pair pair, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            if (pair.A.IsWatcher) pair.AState.OnProgressState(pair.Machine, ProgressState.Full);
            if (pair.B.IsWatcher) pair.BState.OnProgressState(pair.Machine, ProgressState.Full);
            pair.AState.Tick(); pair.BState.Tick();
            await pair.Tick(1);
        }
    }

    private static Alert Idle(GroupClusterNode node) =>
        node.MergedAlerts.Single(a => a.Kind == AlertKind.MachineIdle);

    private static void AssertDismissedEverywhere(GroupRuntimeTests.Pair pair)
    {
        Assert.Equal(AlertStatus.Acknowledged, Idle(pair.A).Status);
        Assert.Equal(AlertStatus.Acknowledged, Idle(pair.B).Status);
    }

    [Fact]
    public async Task SkasowanieNaKomputerzeBezObserwacji_GasiPostojTakzeUObserwatora()
    {
        await using var pair = new GroupRuntimeTests.Pair(); await pair.Start();
        await ObserveIdle(pair, 125);
        Assert.True(pair.A.IsWatcher); Assert.False(pair.B.IsWatcher);
        Assert.Equal(AlertStatus.Active, Idle(pair.B).Status);

        await pair.B.SendAckAsync(Idle(pair.B).Key, "operator", default);
        await ObserveIdle(pair, 3);

        AssertDismissedEverywhere(pair);
        // Obserwator ma to też w swoim stanie lokalnym — inaczej jego kolejny STATE wskrzesiłby pozycję.
        Assert.Equal(AlertStatus.Acknowledged,
            pair.AState.ActiveAlerts.Single(a => a.Kind == AlertKind.MachineIdle).Status);
    }

    [Fact]
    public async Task SkasowanieUObserwatora_GasiPostojNaDrugimKomputerze()
    {
        await using var pair = new GroupRuntimeTests.Pair(); await pair.Start();
        await ObserveIdle(pair, 125);

        await pair.A.SendAckAsync(Idle(pair.A).Key, "operator", default);
        await ObserveIdle(pair, 3);

        AssertDismissedEverywhere(pair);
    }

    [Fact]
    public async Task ZgubionyPakietAck_NadrabiaPowtorkaWHeartbeacie()
    {
        // Dlaczego: UDP gubi pakiety, a heartbeat niesie potwierdzenia z ostatnich 60 s (GroupClusterNode.TickAsync).
        await using var pair = new GroupRuntimeTests.Pair(); await pair.Start();
        await ObserveIdle(pair, 125);

        pair.BBus.DropAck = true;
        await pair.B.SendAckAsync(Idle(pair.B).Key, "operator", default);
        await ObserveIdle(pair, 5);

        AssertDismissedEverywhere(pair);
    }

    [Fact]
    public async Task SkasowanyPostoj_NieWracaPrzyDalszymStaniuNa100Procent()
    {
        // Dlaczego: obserwator co sekundę odświeża ten sam epizod postoju i rozgłasza go w STATE —
        // potwierdzenie nie może się na żadnym komputerze „zgubić” w kolejnych migawkach.
        await using var pair = new GroupRuntimeTests.Pair(); await pair.Start();
        await ObserveIdle(pair, 125);

        await pair.B.SendAckAsync(Idle(pair.B).Key, "operator", default);
        await ObserveIdle(pair, 180);

        AssertDismissedEverywhere(pair);
    }

    [Fact]
    public async Task SkasowanyPostoj_ZostajeSkasowanyPoPrzejeciuObserwacji()
    {
        // Dlaczego: po wyłączeniu obserwatora rolę przejmuje drugi komputer i odtwarza epizody z sieci —
        // przejęcie nie może wskrzesić skasowanej pozycji (pop-up wróciłby bez powodu).
        await using var pair = new GroupRuntimeTests.Pair(); await pair.Start();
        await ObserveIdle(pair, 125);
        await pair.B.SendAckAsync(Idle(pair.B).Key, "operator", default);
        await ObserveIdle(pair, 3);

        pair.A.SetReadiness(new(false, false, []));
        await ObserveIdle(pair, 40);

        Assert.True(pair.B.IsWatcher);
        Assert.Equal(AlertStatus.Acknowledged, Idle(pair.B).Status);
    }
}
