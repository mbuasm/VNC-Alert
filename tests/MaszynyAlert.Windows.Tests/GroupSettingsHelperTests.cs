using System.IO.Pipes;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Rfb.Tests.Fakes;
using static MaszynyAlert.Windows.Tests.GroupHelperTestKit;

namespace MaszynyAlert.Windows.Tests;

/// <summary>
/// Strona podniesionego helpera (`RunHelperAsync`) uruchomiona w procesie testu, bez UAC i bez PowerShell:
/// zerwany pipe po prepare, limit czasu, niepewny commit, zajęta blokada, niespójne dzienniki, odrzucone
/// przygotowanie i zakończenie dla innej generacji. Oczekiwanie: własna próba wycofana, `apply.lock`
/// zwolniony, zatwierdzona generacja nigdy nie cofnięta, a powód odmowy przekazany aplikacji.
/// </summary>
public sealed class GroupSettingsHelperTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public readonly TestDir Dir = new();
        public readonly GroupSettingsStore Store;
        public readonly FakeFirewall Firewall = new();
        public readonly GroupFirewallRule? Original;
        public readonly string Name = PipeName();
        public NamedPipeServerStream Pipe;
        public Task<int> Helper = Task.FromResult(-1);
        public Harness()
        {
            Store = new GroupSettingsStore(Dir.Path, new InMemorySecrets());
            Original = Firewall.Rule;
            Pipe = Server(Name);
        }
        public async Task Start(TimeSpan? lifetime = null, TimeProvider? time = null)
        {
            Helper = Task.Run(() => GroupSettingsClient.RunHelperAsync(Name, Dir.Path,
                () => new GroupApplySession(Store, Firewall, Dir.Path), lifetime ?? TimeSpan.FromSeconds(10), time));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Pipe.WaitForConnectionAsync(timeout.Token);
        }
        public Task Send(HelperRequest request) => GroupSettingsClient.Write(Pipe, request, Token());
        public Task<HelperResponse> Receive() => GroupSettingsClient.Read<HelperResponse>(Pipe, Token());
        public async Task<Guid> PrepareOk(Guid? expectedCurrent = null)
        {
            await Send(PrepareRequest(Proposal(), expectedCurrent));
            var response = await Receive();
            Assert.True(response.Success, response.Error);
            return response.Generation!.Value;
        }
        public Task<int> Exit() => Helper.WaitAsync(TimeSpan.FromSeconds(10));
        public void AssertClean(Guid? committed = null)
        {
            var recovery = Store.Read();
            Assert.Null(recovery.InterruptedId);
            Assert.Equal(committed, recovery.CommittedId);
            Assert.False(File.Exists(Dir.File("system-change.json")));
            using var free = Lock(Dir.Path);
        }
        private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;
        public async ValueTask DisposeAsync()
        {
            Pipe.Dispose();
            try { await Helper.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task ClientDisconnectsAfterPrepare_RollsBackOwnAttempt_AndReleasesLock()
    {
        await using var h = new Harness(); await h.Start();
        Guid pending = await h.PrepareOk();
        Assert.True(h.Firewall.Rule!.Enabled);
        h.Pipe.Dispose();
        Assert.Equal(1, await h.Exit());
        h.AssertClean();
        Assert.Equal(h.Original, h.Firewall.Rule);
        Assert.NotEqual(pending, h.Store.Read().CommittedId);
    }

    [Fact]
    public async Task LifetimeExpiresWaitingForFinish_RollsBack_AndReportsTimeout()
    {
        var clock = new ManualTimeProvider();
        await using var h = new Harness(); await h.Start(lifetime: TimeSpan.FromMilliseconds(700), time: clock);
        await h.PrepareOk();
        clock.Advance(TimeSpan.FromMilliseconds(699));
        await Task.Delay(100);
        Assert.False(h.Helper.IsCompleted, "Helper zakończył się przed upływem czasu życia.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var response = await h.Receive();
        Assert.False(response.Success);
        Assert.Contains("limit czasu", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await h.Exit());
        h.AssertClean();
        Assert.Equal(h.Original, h.Firewall.Rule);
    }

    [Fact]
    public async Task CommitThenBrokenPipe_KeepsCommittedGeneration()
    {
        await using var h = new Harness(); await h.Start();
        Guid pending = await h.PrepareOk();
        await h.Send(new HelperRequest("commit", null, null, null, pending));
        h.Pipe.Dispose(); // odpowiedź na commit nigdy nie dotrze
        await h.Exit();
        h.AssertClean(committed: pending);
        Assert.True(h.Firewall.Rule!.Enabled);
        Assert.Equal(0, h.Firewall.Restores);
    }

    [Fact]
    public async Task ApplyLockHeldByAnotherWriter_RespondsFailure_AndTouchesNothing()
    {
        await using var h = new Harness();
        using var other = Lock(h.Dir.Path);
        await h.Start();
        await h.Send(PrepareRequest(Proposal(), null));
        var response = await h.Receive();
        Assert.False(response.Success);
        Assert.Contains("inna zmiana", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await h.Exit());
        Assert.Equal(0, h.Firewall.Applies);
        Assert.Null(h.Store.Read().CommittedId); Assert.Null(h.Store.Read().InterruptedId);
    }

    [Fact]
    public async Task InconsistentJournals_RespondsFailureWithReason_AndReleasesLock()
    {
        await using var h = new Harness();
        File.WriteAllBytes(h.Dir.File("system-change.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { Generation = Guid.NewGuid(), Previous = (GroupFirewallRule?)null }));
        await h.Start();
        await h.Send(PrepareRequest(Proposal(), null));
        var response = await h.Receive();
        Assert.False(response.Success);
        Assert.Contains("nie są zgodne", response.Error);
        Assert.Equal(1, await h.Exit());
        Assert.Equal(0, h.Firewall.Applies);
        using var free = Lock(h.Dir.Path);
    }

    [Fact]
    public async Task StaleExpectedCurrent_RespondsFailureWithReason_AndLeavesCommittedUntouched()
    {
        await using var h = new Harness();
        Guid committed = h.Store.Stage(Proposal(), null); h.Store.Commit(committed);
        await h.Start();
        await h.Send(PrepareRequest(Proposal(false), expectedCurrent: null));
        var response = await h.Receive();
        Assert.False(response.Success);
        Assert.Contains("zmieniły się", response.Error);
        Assert.Equal(1, await h.Exit());
        h.AssertClean(committed: committed);
        Assert.Equal(h.Original, h.Firewall.Rule);
    }

    [Fact]
    public async Task FinishForOtherGeneration_RollsBackOwnAttempt_ThenRespondsFailure()
    {
        await using var h = new Harness(); await h.Start();
        await h.PrepareOk();
        await h.Send(new HelperRequest("commit", null, null, null, Guid.NewGuid()));
        var response = await h.Receive();
        Assert.False(response.Success);
        Assert.Contains("generacji", response.Error);
        // Odpowiedź przychodzi dopiero po wycofaniu: dziennik jest już ostateczny.
        h.AssertClean();
        Assert.Equal(h.Original, h.Firewall.Rule);
        Assert.Equal(1, await h.Exit());
    }

    [Fact]
    public async Task CommitFailure_RollsBackBeforeFailureResponse()
    {
        await using var h = new Harness(); await h.Start();
        Guid pending = await h.PrepareOk();
        File.Delete(h.Dir.File("generation-" + pending.ToString("N") + ".json")); // commit nie może potwierdzić generacji
        await h.Send(new HelperRequest("commit", null, null, null, pending));
        var response = await h.Receive();
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        h.AssertClean();
        Assert.Equal(h.Original, h.Firewall.Rule);
        Assert.Equal(1, await h.Exit());
    }

    [Fact]
    public async Task UnknownFinishOperation_RollsBack_AndRespondsFailure()
    {
        await using var h = new Harness(); await h.Start();
        Guid pending = await h.PrepareOk();
        await h.Send(new HelperRequest("apply", null, null, null, pending));
        var response = await h.Receive();
        Assert.False(response.Success);
        h.AssertClean();
        Assert.Equal(1, await h.Exit());
    }
}
