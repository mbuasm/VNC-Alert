using System.ComponentModel;
using System.IO.Pipes;
using MaszynyAlert.Core.Config;
using static MaszynyAlert.Windows.Tests.GroupHelperTestKit;

namespace MaszynyAlert.Windows.Tests;

/// <summary>
/// Tryby awarii helpera UAC widziane od strony aplikacji: odmowa UAC, brak połączenia, zerwany pipe,
/// brak odpowiedzi i niepewny commit. Helper jest skryptem po prawdziwym pipe; UAC nie jest wywoływane.
/// </summary>
public sealed class GroupSettingsClientTests
{
    private static Task<GroupSettingsClient> Prepare(ScriptedHelper helper, HelperTimeouts? timeouts = null, CancellationToken ct = default)
        => GroupSettingsClient.PrepareAsync(Proposal(), null, helper, timeouts ?? Fast, ct);

    private static HelperTimeouts Quick(TimeSpan? prepare = null, TimeSpan? commit = null, TimeSpan? abort = null, TimeSpan? exit = null)
        => new(prepare ?? Fast.Prepare, commit ?? Fast.Commit, abort ?? Fast.Abort, exit ?? Fast.Exit);

    private static Guid Generation { get; } = Guid.NewGuid();

    /// <summary>Skrypt: odpowiada na prepare, potem oddaje sterowanie do kontynuacji.</summary>
    private static ScriptedHelper Prepared(Func<ScriptedHelper, NamedPipeClientStream, CancellationToken, Task> rest)
    {
        ScriptedHelper helper = null!;
        helper = ScriptedHelper.Script(async (pipe, ct) =>
        {
            await helper.Next(pipe, ct);
            await GroupSettingsClient.Write(pipe, new HelperResponse(true, Generation), ct);
            await rest(helper, pipe, ct);
        });
        return helper;
    }

    [Fact]
    public async Task UacDenied_ThrowsDenied_AndReleasesPipeName()
    {
        var helper = ScriptedHelper.Idle(); helper.LaunchFailure = new Win32Exception(1223);
        await Assert.ThrowsAsync<GroupSettingsDeniedException>(() => Prepare(helper));
        Assert.NotNull(helper.PipeName);
        using var reusable = Server(helper.PipeName!); // nazwa zwolniona: nic nie zostało po nieudanym starcie
    }

    [Fact]
    public async Task OtherLaunchFailure_IsNotReportedAsDenial()
    {
        var helper = ScriptedHelper.Idle(); helper.LaunchFailure = new Win32Exception(2);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Prepare(helper));
        Assert.IsNotType<GroupSettingsDeniedException>(ex);
    }

    [Fact]
    public async Task HelperNeverConnects_ThrowsNoResponse_AndReleasesHandle()
    {
        var helper = ScriptedHelper.Idle();
        var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => Prepare(helper, Quick(prepare: Short)));
        Assert.Equal(GroupSettingsHelperOutcome.NoResponse, ex.Outcome);
        Assert.Equal(1, helper.Disposals);
    }

    [Fact]
    public async Task CallerCancelsWhileWaiting_ThrowsOperationCanceled_NotHelperFailure()
    {
        var helper = ScriptedHelper.Idle();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prepare(helper, Fast, cts.Token));
        Assert.IsNotType<GroupSettingsDeniedException>(ex);
    }

    [Fact]
    public async Task HelperClosesPipeDuringPrepare_ThrowsDisconnected()
    {
        ScriptedHelper helper = null!;
        helper = ScriptedHelper.Script(async (pipe, ct) => { await helper.Next(pipe, ct); });
        var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => Prepare(helper));
        Assert.Equal(GroupSettingsHelperOutcome.Disconnected, ex.Outcome);
        Assert.Null(ex.Generation);
    }

    [Fact]
    public async Task HelperRejectsPrepare_ThrowsRejectedWithHelperReason()
    {
        ScriptedHelper helper = null!;
        helper = ScriptedHelper.Script(async (pipe, ct) =>
        {
            await helper.Next(pipe, ct);
            await GroupSettingsClient.Write(pipe, new HelperResponse(false, null, "Najpierw odzyskaj niedokończoną zmianę grupy."), ct);
        });
        var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => Prepare(helper));
        Assert.Equal(GroupSettingsHelperOutcome.Rejected, ex.Outcome);
        Assert.Contains("odzyskaj niedokończoną zmianę", ex.Message);
    }

    [Fact]
    public async Task HelperSilentAfterPrepare_ThrowsNoResponse()
    {
        ScriptedHelper helper = null!;
        helper = ScriptedHelper.Script(async (pipe, ct) => { await helper.Next(pipe, ct); await Task.Delay(Timeout.Infinite, ct); });
        try
        {
            var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => Prepare(helper, Quick(prepare: Short)));
            Assert.Equal(GroupSettingsHelperOutcome.NoResponse, ex.Outcome);
        }
        finally { helper.Stop(); }
    }

    [Fact]
    public async Task CommitAcknowledged_DisposeSendsNothingMore()
    {
        var helper = Prepared(async (h, pipe, ct) =>
        {
            var finish = await h.Next(pipe, ct);
            await GroupSettingsClient.Write(pipe, new HelperResponse(true, finish.Generation), ct);
            await h.Drain(pipe);
        });
        var client = await Prepare(helper);
        Assert.Equal(Generation, client.Generation);
        await client.CommitAsync(CancellationToken.None);
        await client.DisposeAsync();
        await helper.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "prepare", "commit" }, helper.Seen);
    }

    [Fact]
    public async Task CommitWithoutResponse_IsUncertain_AndDisposeDoesNotAbort()
    {
        var helper = Prepared(async (h, pipe, ct) => { await h.Next(pipe, ct); /* helper zapisał generację i zginął */ });
        var client = await Prepare(helper);
        var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => client.CommitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(GroupSettingsHelperOutcome.Uncertain, ex.Outcome);
        Assert.Equal(Generation, ex.Generation);
        await client.DisposeAsync(); // wynik należy do dziennika; żadnego abort ani wyjątku
        Assert.Equal(new[] { "prepare", "commit" }, helper.Seen);
    }

    [Fact]
    public async Task CommitTimesOut_IsUncertain_AndAbortIsNeverSent()
    {
        var helper = Prepared(async (h, pipe, ct) =>
        {
            await h.Next(pipe, ct);
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            await h.Drain(pipe); // po zakończeniu: czy aplikacja dopisała abort?
        });
        var client = await Prepare(helper, Quick(commit: Short, exit: Short));
        try
        {
            var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => client.CommitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(GroupSettingsHelperOutcome.Uncertain, ex.Outcome);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { helper.Stop(); }
        await helper.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "prepare", "commit" }, helper.Seen);
    }

    [Fact]
    public async Task CallerCancelsDuringCommit_IsUncertain()
    {
        var helper = Prepared(async (h, pipe, ct) =>
        {
            await h.Next(pipe, ct);
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        });
        var client = await Prepare(helper, Quick(exit: Short));
        using var cts = new CancellationTokenSource(Short);
        try
        {
            var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => client.CommitAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(GroupSettingsHelperOutcome.Uncertain, ex.Outcome);
        }
        finally { helper.Stop(); await client.DisposeAsync(); }
    }

    [Fact]
    public async Task CommitRejectedByHelper_ThrowsRejected_AndDisposeDoesNotAbortAgain()
    {
        var helper = Prepared(async (h, pipe, ct) =>
        {
            await h.Next(pipe, ct);
            await GroupSettingsClient.Write(pipe, new HelperResponse(false, null, "Dziennik ustawień grupy jest zablokowany."), ct);
            await h.Drain(pipe);
        });
        var client = await Prepare(helper);
        var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => client.CommitAsync(CancellationToken.None));
        Assert.Equal(GroupSettingsHelperOutcome.Rejected, ex.Outcome);
        Assert.Contains("zablokowany", ex.Message);
        await client.DisposeAsync();
        await helper.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "prepare", "commit" }, helper.Seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbortWithoutResponse_ThrowsWithGeneration(bool helperReadsAbortFirst)
    {
        var helper = Prepared(async (h, pipe, ct) => { if (helperReadsAbortFirst) await h.Next(pipe, ct); });
        var client = await Prepare(helper);
        await helper.Run.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
        var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(GroupSettingsHelperOutcome.Disconnected, ex.Outcome);
        Assert.Equal(Generation, ex.Generation);
    }

    [Fact]
    public async Task AbortSilent_ThrowsNoResponse()
    {
        var helper = Prepared(async (h, pipe, ct) => { await h.Next(pipe, ct); await Task.Delay(Timeout.Infinite, ct); });
        var client = await Prepare(helper, Quick(abort: Short));
        try
        {
            var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(GroupSettingsHelperOutcome.NoResponse, ex.Outcome);
            Assert.Equal(Generation, ex.Generation);
        }
        finally { helper.Stop(); }
    }

    [Fact]
    public async Task AbortAcknowledged_DisposeSucceeds()
    {
        var helper = Prepared(async (h, pipe, ct) =>
        {
            var finish = await h.Next(pipe, ct);
            await GroupSettingsClient.Write(pipe, new HelperResponse(finish.Operation == "abort" && finish.Generation == Generation, finish.Generation), ct);
        });
        var client = await Prepare(helper);
        await client.DisposeAsync();
        Assert.Equal(new[] { "prepare", "abort" }, helper.Seen);
    }

    [Fact]
    public async Task AbortRejected_ThrowsRejectedWithReason()
    {
        var helper = Prepared(async (h, pipe, ct) =>
        {
            await h.Next(pipe, ct);
            await GroupSettingsClient.Write(pipe, new HelperResponse(false, null, "Cofnięcie zapory nie powiodło się."), ct);
        });
        var client = await Prepare(helper);
        var ex = await Assert.ThrowsAsync<GroupSettingsHelperException>(() => client.DisposeAsync().AsTask());
        Assert.Equal(GroupSettingsHelperOutcome.Rejected, ex.Outcome);
        Assert.Contains("zapory", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealHelperInProcess_CommitOrAbort_LeavesJournalConsistentAndLockFree(bool commit)
    {
        using var dir = new TestDir();
        var store = new GroupSettingsStore(dir.Path, new InMemorySecrets());
        var firewall = new FakeFirewall(); var original = firewall.Rule;
        var helper = ScriptedHelper.Raw((name, _) => GroupSettingsClient.RunHelperAsync(name, dir.Path,
            () => new GroupApplySession(store, firewall, dir.Path), TimeSpan.FromSeconds(10)));
        var client = await Prepare(helper);
        if (commit) await client.CommitAsync(CancellationToken.None);
        await client.DisposeAsync();
        await helper.Run.WaitAsync(TimeSpan.FromSeconds(10));
        var recovery = store.Read();
        Assert.Null(recovery.InterruptedId);
        Assert.Equal(commit ? client.Generation : null, recovery.CommittedId);
        Assert.Equal(commit, firewall.Rule!.Enabled);
        if (!commit) Assert.Equal(original, firewall.Rule);
        Assert.False(File.Exists(dir.File("system-change.json")));
        using var free = Lock(dir.Path);
    }
}
