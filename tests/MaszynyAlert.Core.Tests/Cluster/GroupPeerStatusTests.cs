using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class GroupPeerStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Alive = TimeSpan.FromSeconds(15);
    private const string Local = "0.4.79+0123456789abcdef0123456789abcdef01234567";

    private static PeerInfo Peer(string host, string version, GroupCompatibility compatibility,
        PeerRole role = PeerRole.Ui, DateTimeOffset? seen = null) =>
        new(host, 100, role, version, seen ?? Now, 1, null, 9, 9) { Compatibility = compatibility };

    [Fact]
    public void OtherProfileWithOtherVersion_NamesVersionAndGroupSettingsWithRemedy()
    {
        string text = GroupPeerStatus.Describe(Peer("PSZEMEK", "0.4.32+abc", GroupCompatibility.OtherProfile), Local);
        Assert.Equal("inne ustawienia grupy albo inna wersja programu (0.4.32) — nie współpracuje z tym komputerem; zainstaluj tę samą wersję, a jeśli to nie pomoże — wczytaj na obu komputerach ten sam, aktualny plik grupy", text);
        Assert.False(GroupPeerStatus.Cooperates(Peer("PSZEMEK", "0.4.32", GroupCompatibility.OtherProfile)));
    }

    [Fact]
    public void OtherProfileWithSameVersion_IsNamedAsOtherGroupSettings()
    {
        string text = GroupPeerStatus.Describe(Peer("PC-B", "0.4.79+ffff", GroupCompatibility.OtherProfile), Local);
        Assert.StartsWith("inne ustawienia grupy — nie współpracuje z tym komputerem", text);
        Assert.DoesNotContain("profileHash", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompatiblePeer_Cooperates_AndOtherVersionIsOnlyANote()
    {
        Assert.Equal("współpracuje", GroupPeerStatus.Describe(Peer("PC-B", Local, GroupCompatibility.Compatible), Local));
        string other = GroupPeerStatus.Describe(Peer("PC-B", "0.4.85", GroupCompatibility.Compatible), Local);
        Assert.StartsWith("współpracuje", other);
        Assert.Contains("0.4.85", other);
        Assert.Equal("ten komputer", GroupPeerStatus.Describe(Peer("PC-A", Local, GroupCompatibility.Compatible), Local, isThisComputer: true));
    }

    [Fact]
    public void Summary_CountsIncompatibleAndLiveObservers()
    {
        var peers = new[]
        {
            Peer("AIDITEKOMP1", Local, GroupCompatibility.Compatible, PeerRole.Watcher),
            Peer("PSZEMEK", "0.4.32", GroupCompatibility.OtherProfile, PeerRole.Watcher),
            Peer("AIDITEKOMP2", Local, GroupCompatibility.Compatible),
            // Dawny obserwator, którego od dawna nie widać, nie jest „drugim obserwatorem”.
            Peer("STARY", Local, GroupCompatibility.Compatible, PeerRole.Watcher, Now.AddMinutes(-5)),
        };
        var summary = GroupPeerStatus.Summarize(peers, Local, Now, Alive);
        Assert.Equal(["PSZEMEK — inne ustawienia grupy albo inna wersja programu (0.4.32)"], summary.Incompatible);
        Assert.Equal(["AIDITEKOMP1", "PSZEMEK"], summary.Watchers);
    }

    /// <summary>Obaj obserwatorzy potwierdzeni (stan trwa od 60 s — <see cref="WatcherOverlapTracker"/>).</summary>
    private static readonly string[] Both = ["AIDITEKOMP1", "PSZEMEK"];

    [Fact]
    public void Warnings_AreLoggedOncePerPeerAndReasonWithinThrottle()
    {
        var warnings = new GroupPeerWarnings();
        // Oba komputery wysyłają sygnał co sekundę — w chwili sprawdzenia są „żywe”.
        PeerInfo[] PeersAt(DateTimeOffset at) =>
        [
            Peer("AIDITEKOMP1", Local, GroupCompatibility.Compatible, PeerRole.Watcher, at),
            Peer("PSZEMEK", "0.4.32", GroupCompatibility.OtherProfile, PeerRole.Watcher, at),
        ];
        var first = warnings.Collect(PeersAt(Now), "AIDITEKOMP1", Local, Now, Alive, Both);
        Assert.Equal(2, first.Count);
        Assert.Contains(first, w => w.Contains("PSZEMEK", StringComparison.Ordinal)
            && w.Contains("0.4.32", StringComparison.Ordinal) && w.Contains("nie współpracuje", StringComparison.Ordinal));
        Assert.Contains(first, w => w.Contains("drugi obserwator", StringComparison.Ordinal));
        for (int s = 1; s < 600; s++)
            Assert.Empty(warnings.Collect(PeersAt(Now.AddSeconds(s)), "AIDITEKOMP1", Local, Now.AddSeconds(s), Alive, Both));
        var later = Now.Add(GroupPeerWarnings.Throttle);
        Assert.Equal(2, warnings.Collect(PeersAt(later), "AIDITEKOMP1", Local, later, Alive, Both).Count);
        // Inny powód (np. ten sam komputer po aktualizacji do innej wersji) to nowy wpis od razu.
        var updated = new[] { PeersAt(later.AddSeconds(1))[0],
            Peer("PSZEMEK", "0.4.40", GroupCompatibility.OtherProfile, seen: later.AddSeconds(1)) };
        Assert.Single(warnings.Collect(updated, "AIDITEKOMP1", Local, later.AddSeconds(1), Alive, Both));
    }

    [Fact]
    public void Warnings_IgnoreComputersNotSeenRecently()
    {
        var peers = new[] { Peer("PSZEMEK", "0.4.32", GroupCompatibility.OtherProfile, seen: Now.AddMinutes(-3)) };
        Assert.Empty(new GroupPeerWarnings().Collect(peers, "AIDITEKOMP1", Local, Now, Alive, []));
    }

    [Fact]
    public async Task DifferentProfileHash_BothObserve_AndRoleChangeReasonNamesIt()
    {
        var clock = new FakeClock();
        var file = GroupFileTests.Invitation();
        var config = new ClusterConfig { Enabled = true, Group = file.Identity };
        var other = config with { Priority = 50, Group = file.Identity with { ProfileHash = new string('0', 64) } };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(file.NetworkKey), clock);
        await using var a = new GroupClusterNode(config, hub.CreateBus("A"), clock, "0.4.79");
        await using var b = new GroupClusterNode(other, hub.CreateBus("B"), clock, "0.4.32");
        a.SetReadiness(new(true, false, [])); b.SetReadiness(new(true, false, []));
        await a.StartAsync(default); await b.StartAsync(default);
        for (int i = 0; i < 25; i++) { clock.Advance(TimeSpan.FromSeconds(1)); await a.TickAsync(default); await b.TickAsync(default); }

        Assert.True(a.IsWatcher); Assert.True(b.IsWatcher);
        Assert.NotNull(a.LastRoleChangeReason);
        Assert.Contains("brak zgodnego obserwatora", a.LastRoleChangeReason);
        Assert.Contains("B", a.LastRoleChangeReason);
        var seen = Assert.Single(a.Peers, p => p.Host == "B");
        Assert.Equal(GroupCompatibility.OtherProfile, seen.Compatibility);
    }

    [Fact]
    public async Task YieldingToCompatibleObserver_NamesTheComputer()
    {
        await using var pair = new GroupRuntimeTests.Pair(bPriority: 50);
        await pair.StartA();
        Assert.True(pair.A.IsWatcher);
        await pair.StartB(); await pair.Tick(30);
        Assert.False(pair.A.IsWatcher);
        Assert.Contains("ustąpienie komputerowi B", pair.A.LastRoleChangeReason);
    }


    [Fact]
    public void Warnings_CollectFromTwoThreadsAtOnce_NeitherThrowsNorDuplicates()
    {
        var peers = Enumerable.Range(0, 40)
            .Select(i => Peer("PC" + i, "0.4.32", GroupCompatibility.OtherProfile))
            .Append(Peer("JA", Local, GroupCompatibility.Compatible, PeerRole.Watcher)).ToArray();
        int errors = 0, duplicates = 0;
        string first = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            var warnings = new GroupPeerWarnings();
            int total = 0;
            using var start = new Barrier(2);
            Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, _ =>
            {
                start.SignalAndWait();
                try { Interlocked.Add(ref total, warnings.Collect(peers, "JA", Local, Now, Alive, []).Count); }
                catch (Exception ex) { if (Interlocked.Increment(ref errors) == 1) first = ex.Message; }
            });
            if (total > 40) duplicates++;
        }
        Assert.True(errors == 0, $"wyjątki: {errors}, pierwszy: {first}");
        Assert.Equal(0, duplicates);
    }

    [Fact]
    public void Warnings_ForgetEntriesOlderThanThrottle()
    {
        var warnings = new GroupPeerWarnings();
        for (int v = 0; v < 20; v++)
            warnings.Collect([Peer("PSZEMEK", $"0.4.{v}", GroupCompatibility.OtherProfile)], "JA", Local, Now, Alive, []);
        Assert.Equal(20, warnings.TrackedCount);
        var later = Now.Add(GroupPeerWarnings.Throttle).AddSeconds(1);
        warnings.Collect([Peer("PSZEMEK", "0.4.99", GroupCompatibility.OtherProfile, seen: later)], "JA", Local, later, Alive, []);
        Assert.Equal(1, warnings.TrackedCount);
    }

    [Fact]
    public void Warnings_SecondObserverOnlyWhenOverlapConfirmed_IncompatibleImmediately()
    {
        PeerInfo[] PeersAt(DateTimeOffset at) =>
        [
            Peer("AIDITEKOMP1", Local, GroupCompatibility.Compatible, PeerRole.Watcher, at),
            Peer("AIDITEKOMP2", Local, GroupCompatibility.Compatible, PeerRole.Watcher, at),
            Peer("PSZEMEK", "0.4.32", GroupCompatibility.OtherProfile, seen: at),
        ];
        var warnings = new GroupPeerWarnings();
        var unconfirmed = warnings.Collect(PeersAt(Now), "AIDITEKOMP1", Local, Now, Alive, []);
        Assert.Single(unconfirmed);
        Assert.Contains("PSZEMEK", unconfirmed[0], StringComparison.Ordinal);
        var later = Now.AddSeconds(60);
        var confirmed = warnings.Collect(PeersAt(later), "AIDITEKOMP1", Local, later, Alive, ["AIDITEKOMP1", "AIDITEKOMP2"]);
        var second = Assert.Single(confirmed);
        Assert.Contains("drugi obserwator — AIDITEKOMP2", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlap_ShowsOnlyAfterSixtySecondsOfContinuousOverlap()
    {
        var clock = new FakeClock();
        var tracker = new WatcherOverlapTracker();
        string[] two = ["A", "B"];
        Assert.Empty(tracker.Update(two, clock.MonotonicMs));
        for (int s = 1; s < 60; s++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Empty(tracker.Update(two, clock.MonotonicMs));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(two, tracker.Update(two, clock.MonotonicMs));
        Assert.Equal(two, tracker.Confirmed);
    }

    [Fact]
    public void Overlap_DropToOneObserverRestartsTheCount_ChangedPairDoesNot()
    {
        var clock = new FakeClock();
        var tracker = new WatcherOverlapTracker();
        for (int s = 0; s < 30; s++) { tracker.Update(["A", "B"], clock.MonotonicMs); clock.Advance(TimeSpan.FromSeconds(1)); }
        tracker.Update(["A"], clock.MonotonicMs);
        clock.Advance(TimeSpan.FromSeconds(1));
        // Po spadku do jednego obserwatora liczenie od nowa: 30 s wcześniej się nie sumuje.
        for (int s = 0; s < 60; s++) { Assert.Empty(tracker.Update(["A", "B"], clock.MonotonicMs)); clock.Advance(TimeSpan.FromSeconds(1)); }
        // Inna para (B ustąpił, C przejął) bez chwili z jednym obserwatorem — dalej „więcej niż jeden nieprzerwanie”.
        Assert.Equal(["A", "C"], tracker.Update(["A", "C"], clock.MonotonicMs));
        tracker.Update([], clock.MonotonicMs);
        Assert.Empty(tracker.Confirmed);
    }

    [Fact]
    public void Overlap_SleepOrGroupSwitchOrLongGapRestartsTheCount()
    {
        var clock = new FakeClock();
        var tracker = new WatcherOverlapTracker();
        tracker.Update(["A", "B"], clock.MonotonicMs);
        clock.Advance(TimeSpan.FromSeconds(30));
        tracker.Update(["A", "B"], clock.MonotonicMs);
        // Uśpienie: skok zegara monotonicznego nie może od razu „odliczyć” 60 s.
        clock.JumpMonotonic(3_600_000);
        Assert.Empty(tracker.Update(["A", "B"], clock.MonotonicMs));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(tracker.Update(["A", "B"], clock.MonotonicMs));
        // Jawne zerowanie (wykryte uśpienie, przełączenie grupy).
        for (int s = 0; s < 70; s++) { clock.Advance(TimeSpan.FromSeconds(1)); tracker.Update(["A", "B"], clock.MonotonicMs); }
        Assert.NotEmpty(tracker.Confirmed);
        tracker.Reset();
        Assert.Empty(tracker.Confirmed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(tracker.Update(["A", "B"], clock.MonotonicMs));
    }

    [Fact]
    public void OtherProfileWithOtherVersion_NamesBothCauses_AndGivesBothRemedies()
    {
        var peer = Peer("PSZEMEK", "0.4.32+abc", GroupCompatibility.OtherProfile);
        Assert.Equal("inne ustawienia grupy albo inna wersja programu (0.4.32)", GroupPeerStatus.IncompatibilityReason(peer, Local));
        Assert.Equal("zainstaluj tę samą wersję, a jeśli to nie pomoże — wczytaj na obu komputerach ten sam, aktualny plik grupy",
            GroupPeerStatus.Remedy(peer, Local));
    }

    [Fact]
    public void PeerWithoutVersion_OrLocalVersionUnknown_DoesNotShowEmptyParentheses()
    {
        Assert.Equal("inne ustawienia grupy albo inna wersja programu (nieznana)",
            GroupPeerStatus.IncompatibilityReason(Peer("PC-B", "", GroupCompatibility.OtherProfile), Local));
        // Własna wersja nieznana = nie porównujemy wersji.
        Assert.Equal("inne ustawienia grupy",
            GroupPeerStatus.IncompatibilityReason(Peer("PC-B", "0.4.32", GroupCompatibility.OtherProfile), null));
    }

    [Fact]
    public void SummaryAdvice_FollowsTheReason()
    {
        var sameVersion = GroupPeerStatus.Summarize([Peer("PC-B", Local, GroupCompatibility.OtherProfile)], Local, Now, Alive);
        Assert.Equal("Wczytaj na wszystkich komputerach grupy ten sam, aktualny plik grupy.", sameVersion.Advice);
        Assert.Contains(sameVersion.Advice, sameVersion.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("tę samą wersję", sameVersion.Warning, StringComparison.Ordinal);

        var otherVersion = GroupPeerStatus.Summarize([Peer("PSZEMEK", "0.4.32", GroupCompatibility.OtherProfile)], Local, Now, Alive);
        Assert.Equal("Zainstaluj na wszystkich komputerach grupy tę samą wersję programu; jeśli to nie pomoże, wczytaj na nich "
            + "ten sam, aktualny plik grupy.", otherVersion.Advice);
    }

    [Fact]
    public void Summary_IgnoresIncompatibleComputerNotSeenRecently()
    {
        var summary = GroupPeerStatus.Summarize(
            [Peer("PSZEMEK", "0.4.32", GroupCompatibility.OtherProfile, seen: Now.AddMinutes(-5))], Local, Now, Alive);
        Assert.Empty(summary.Incompatible);
        Assert.Equal("", summary.Warning);
    }
}
