using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class GroupApplySessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirewallFailure_RestoresPreviousRuleAndDoesNotCommit(bool mutateFirst)
    {
        using var dir = new TempDir(); var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());
        var firewall = new FakeFirewall { Rule = new("app.exe", 47777, "LocalSubnet", false), FailApply = true, MutateFirst = mutateFirst };
        var original = firewall.Rule;
        var session = new GroupApplySession(store, firewall, dir.Path);
        Assert.Throws<IOException>(() => session.Prepare(Proposal(), null));
        Assert.Equal(original, firewall.Rule); Assert.Null(store.Read().CommittedId); Assert.Null(store.Read().InterruptedId);
    }
    [Fact]
    public void CrashAfterFirewallMutation_StartupSelectsPreviousCommit_RecoveryRestoresRule()
    {
        using var dir = new TempDir(); var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());
        var firewall = new FakeFirewall(); var writer = new GroupApplySession(store, firewall, dir.Path);
        Guid first = writer.Prepare(Proposal(), null); writer.Commit(first);
        var original = firewall.Rule;
        Guid pending = writer.Prepare(Proposal(false), first);
        Assert.False(firewall.Rule!.Enabled); Assert.Equal(first, store.ReadForStartup().CommittedId);
        Assert.Equal(pending, store.ReadForStartup().InterruptedId);
        new GroupApplySession(store, firewall, dir.Path).Recover();
        Assert.Equal(original, firewall.Rule); Assert.Equal(first, store.Read().CommittedId); Assert.Null(store.Read().InterruptedId);
    }
    [Fact]
    public void FailedRestore_KeepsUndoAndPendingUntilRetry()
    {
        using var dir = new TempDir(); var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());
        var firewall = new FakeFirewall(); var writer = new GroupApplySession(store, firewall, dir.Path);
        Guid pending = writer.Prepare(Proposal(), null); firewall.FailRestore = true;
        Assert.Throws<IOException>(() => writer.Rollback(pending));
        Assert.Equal(pending, store.Read().InterruptedId); Assert.True(File.Exists(dir.File("system-change.json")));
        firewall.FailRestore = false; writer.Recover(); Assert.Null(firewall.Rule); Assert.Null(store.Read().InterruptedId);
    }
    [Fact]
    public void LostCommitResponse_DoesNotRollBackCommittedGeneration()
    {
        using var dir = new TempDir(); var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());
        var firewall = new FakeFirewall(); var writer = new GroupApplySession(store, firewall, dir.Path);
        Guid pending = writer.Prepare(Proposal(), null); writer.Commit(pending); writer.Rollback(pending);
        Assert.Equal(pending, store.Read().CommittedId); Assert.True(firewall.Rule!.Enabled);
    }
    [Fact]
    public void MissingCommittedKey_StillLoadsPublicProfileForLocalFallback()
    {
        using var dir = new TempDir(); var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets); Guid id = store.Stage(Proposal(), null); store.Commit(id);
        secrets.Remove(secrets.Keys.Single());
        Assert.Equal(7, store.ReadForStartup().Data!.Profile.Machines.Count(m => m.Enabled));
        Assert.Throws<InvalidDataException>(() => store.ReadCommittedNetworkKey());
    }
    [Fact]
    public void RejectedPrepare_LeavesForeignPendingGenerationUntouched()
    {
        using var dir = new TempDir(); var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());
        Guid foreign = store.Stage(Proposal(), null); // another writer's staged, not yet committed generation
        var firewall = new FakeFirewall { Rule = new("app.exe", 47777, "LocalSubnet", false) };
        var original = firewall.Rule;
        Assert.ThrowsAny<Exception>(() => new GroupApplySession(store, firewall, dir.Path).Prepare(Proposal(), null));
        Assert.Equal(foreign, store.Read().InterruptedId);
        Assert.Equal(original, firewall.Rule);
        Assert.False(File.Exists(dir.File("system-change.json")));
    }
    private static GroupSettingsProposal Proposal(bool enabled = true) => new(GroupFileTests.Invitation(), new(), enabled);
    private sealed class FakeFirewall : IGroupFirewall
    {
        public GroupFirewallRule? Rule;
        public bool FailApply, MutateFirst, FailRestore;
        public GroupFirewallRule? Capture() => Rule;
        public void Apply(ClusterConfig c)
        {
            if (FailApply && !MutateFirst) throw new IOException("denied");
            Rule = new("app.exe", c.UdpPort, "LocalSubnet", c.Enabled);
            if (FailApply) throw new IOException("uncertain");
        }
        public void Restore(GroupFirewallRule? previous) { if (FailRestore) throw new IOException("denied"); Rule = previous; }
    }
}
