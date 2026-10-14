using System.Text.Json.Nodes;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class GroupSettingsStoreTests
{
    private static GroupSettingsProposal Proposal(GroupFile? file = null) =>
        new(file ?? GroupFileTests.Invitation(), new ClusterConfig { Priority = 57 }, enabled: true);

    [Fact]
    public void PreparationAndCancel_HaveNoSideEffects_AndProposalCannotBeMutated()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var file = GroupFileTests.Invitation();
        var proposal = Proposal(file);
        file.Profile.Machines.Clear();
        proposal.GetData().Profile.Machines.Clear();
        Assert.Equal(12, proposal.GetData().Profile.Machines.Count);
        Assert.Empty(secrets.Keys);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Commit_SelectsWholeGeneration_PreservingLocalSecretsAndPreferences()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        secrets.Set(SecretKeys.VncPassword("aidite1"), "LOCAL-VNC-SECRET");
        secrets.Set("admin:password", "LOCAL-ADMIN-HASH");
        secrets.Set(SecretKeys.ClusterHmacKey, "LEGACY-KEY");
        var store = new GroupSettingsStore(dir.Path, secrets);
        var invitation = GroupFileTests.Invitation();
        Guid id = store.Stage(Proposal(invitation), expectedCurrent: null);
        Assert.Null(store.Read().Data);
        Assert.Equal(id, store.Read().InterruptedId);
        Assert.Equal(57, store.ReadStaged(id).Cluster.Priority);
        store.Commit(id);
        store.Commit(id); // A repeated result check must not generate another key or commit.
        var loaded = new GroupSettingsStore(dir.Path, secrets).Read();
        Assert.Equal(1, loaded.Number);
        Assert.Equal(id, loaded.CommittedId);
        Assert.Null(loaded.InterruptedId);
        Assert.Equal(invitation.NetworkKey, store.ReadCommittedNetworkKey());
        Assert.Equal("LOCAL-VNC-SECRET", secrets.Get(SecretKeys.VncPassword("aidite1")));
        Assert.Equal("LOCAL-ADMIN-HASH", secrets.Get("admin:password"));
        Assert.Equal("LEGACY-KEY", secrets.Get(SecretKeys.ClusterHmacKey));
        var local = TestPaths.ExampleConfig with { Enabled = false };
        var applied = loaded.Data!.ApplyTo(local);
        Assert.False(applied.Enabled);
        Assert.Equal(local.Ui, applied.Ui);
        Assert.Equal(local.Paths, applied.Paths);
        Assert.Equal(7, applied.Machines.Count(m => m.Enabled));
        foreach (var path in Directory.GetFiles(dir.Path))
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain(invitation.NetworkKey, text);
            Assert.DoesNotContain("LOCAL-VNC-SECRET", text);
            Assert.DoesNotContain("LOCAL-ADMIN-HASH", text);
        }
    }

    [Fact]
    public void GenerationWrittenByOlderProgramVersion_StillLoads_WithRecomputedFingerprint()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        var invitation = GroupFileTests.Invitation();
        Guid id = store.Stage(Proposal(invitation), expectedCurrent: null);
        store.Commit(id);

        string path = Directory.GetFiles(dir.Path, "generation-*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["data"]!["cluster"]!["group"]!["profileHash"] = GroupFileTests.OlderVersionProfileHash(
            json["data"]!["profile"]!, GroupFileTests.FieldsAddedOn15092026);
        File.WriteAllText(path, json.ToJsonString());

        var reloaded = new GroupSettingsStore(dir.Path, secrets);
        var startup = reloaded.ReadForStartup();
        Assert.NotNull(startup.Data);
        Assert.True(startup.ProfileFingerprintRenewed);
        Assert.Equal(invitation.Identity.GroupId, startup.Data!.Cluster.Group!.GroupId);
        Assert.Equal(7, startup.Data.ApplyTo(TestPaths.ExampleConfig).Machines.Count(m => m.Enabled));
        // Odcisk jest wyliczony z profilu, więc oba komputery grupy dostają tę samą wartość.
        Assert.Equal(GroupFileCodec.ProfileHash(startup.Data.Profile), startup.Data.Cluster.Group.ProfileHash);
        // Ta sama droga z kluczem DPAPI (Read) daje ten sam wynik, a klucz grupy nadal się odczytuje.
        Assert.True(reloaded.Read().Data!.Cluster.Group!.ProfileHash.SequenceEqual(startup.Data.Cluster.Group.ProfileHash));
        Assert.Equal(invitation.NetworkKey, reloaded.ReadCommittedNetworkKey());
    }

    [Fact]
    public void InterruptedSecondApply_RecoversPreviousCommit_AndCanBeAbortedRepeatedly()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        var first = GroupFileTests.Invitation();
        var current = store.Stage(Proposal(first), null);
        store.Commit(current);
        var pending = store.Stage(Proposal(first.RotateKey()), current);
        var restarted = new GroupSettingsStore(dir.Path, secrets);
        Assert.Equal(first.Identity, restarted.Read().Data!.Cluster.Group);
        Assert.Equal(first.NetworkKey, restarted.ReadCommittedNetworkKey());
        Assert.Equal(pending, restarted.Read().InterruptedId);
        restarted.Abort(pending);
        restarted.Abort(pending);
        Assert.Null(restarted.Read().InterruptedId);
        Assert.Single(secrets.Keys);
        Assert.Equal(current, restarted.Read().CommittedId);
        Assert.Throws<InvalidOperationException>(() => restarted.Abort(current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecretWriteFailureBeforeOrAfterWrite_LeavesRecoverableIntent(bool afterWrite)
    {
        using var dir = new TempDir();
        var secrets = new FailingSecrets { FailSet = true, FailAfterWrite = afterWrite };
        var store = new GroupSettingsStore(dir.Path, secrets);
        Assert.Throws<IOException>(() => store.Stage(Proposal(), null));
        var result = store.Read();
        Assert.Null(result.CommittedId);
        Assert.NotNull(result.InterruptedId);
        Assert.ThrowsAny<IOException>(() => store.Commit(result.InterruptedId!.Value));
        secrets.FailSet = false;
        store.Abort(result.InterruptedId!.Value);
        Assert.Empty(secrets.Keys);
        Assert.Null(store.Read().InterruptedId);
    }

    [Fact]
    public void FailedCommitWrite_DoesNotChangeSelectedConfiguration()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        Guid current = store.Stage(Proposal(), null);
        store.Commit(current);
        Guid next = store.Stage(Proposal(), current);
        var journal = File.ReadAllBytes(dir.File("journal.json"));
        var key = store.ReadCommittedNetworkKey();
        var failing = FailingJournalPublisher(dir, secrets);
        Assert.Throws<IOException>(() => failing.Commit(next));
        Assert.Equal(journal, File.ReadAllBytes(dir.File("journal.json")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        var restarted = new GroupSettingsStore(dir.Path, secrets);
        Assert.Equal(current, restarted.Read().CommittedId);
        Assert.Equal(next, restarted.Read().InterruptedId);
        Assert.Equal(key, restarted.ReadCommittedNetworkKey());
        Assert.Equal(2, secrets.Keys.Count);
        store.Abort(next);
        Assert.Single(secrets.Keys);
        Assert.Null(store.Read().InterruptedId);
    }

    [Fact]
    public void CorruptStagedGenerationOrMissingKey_CannotBeCommitted()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        Guid pending = store.Stage(Proposal(), null);
        string generation = dir.File($"generation-{pending:N}.json");
        string valid = File.ReadAllText(generation);
        File.WriteAllText(generation, "{ corrupt");
        Assert.Throws<InvalidDataException>(() => store.Commit(pending));
        Assert.Null(store.Read().Data);
        File.WriteAllText(generation, valid);
        secrets.Remove(secrets.Keys.Single());
        Assert.Throws<InvalidDataException>(() => store.Commit(pending));
        store.Abort(pending);
    }

    [Fact]
    public void InterruptedBeforeGenerationWrite_KeepsPreviousCommit()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        Guid current = store.Stage(Proposal(), null);
        store.Commit(current);
        Guid pending = store.Stage(Proposal(), current);
        File.Delete(dir.File($"generation-{pending:N}.json"));
        var restarted = new GroupSettingsStore(dir.Path, secrets);
        Assert.Equal(current, restarted.Read().CommittedId);
        Assert.Equal(pending, restarted.Read().InterruptedId);
        Assert.Throws<FileNotFoundException>(() => restarted.Commit(pending));
        restarted.Abort(pending);
        Assert.Single(secrets.Keys);
    }

    [Fact]
    public void FailedIntentWrite_DoesNotAddSecretOrAlterCommit()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        Guid current = store.Stage(Proposal(), null);
        store.Commit(current);
        var journal = File.ReadAllBytes(dir.File("journal.json"));
        var key = store.ReadCommittedNetworkKey();
        var failing = FailingJournalPublisher(dir, secrets);
        Assert.Throws<IOException>(() => failing.Stage(Proposal(), current));
        Assert.Equal(journal, File.ReadAllBytes(dir.File("journal.json")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Single(Directory.GetFiles(dir.Path, "generation-*.json"));
        var restarted = new GroupSettingsStore(dir.Path, secrets);
        Assert.Equal(current, restarted.Read().CommittedId);
        Assert.Null(restarted.Read().InterruptedId);
        Assert.Equal(key, restarted.ReadCommittedNetworkKey());
        Assert.Single(secrets.Keys);
    }

    [Fact]
    public void OldPreviewAndConcurrentApply_AreRejected()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var first = new GroupSettingsStore(dir.Path, secrets);
        var second = new GroupSettingsStore(dir.Path, secrets);
        Guid pending = first.Stage(Proposal(), null);
        Assert.Throws<InvalidOperationException>(() => second.Stage(Proposal(), null));
        first.Commit(pending);
        Assert.Throws<InvalidOperationException>(() => second.Stage(Proposal(), null));
        using var gate = new FileStream(dir.File("settings.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => second.Stage(Proposal(), pending));
    }

    [Fact]
    public void CorruptJournal_DoesNotOverwriteConfigurationOrSecrets()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretStore();
        var store = new GroupSettingsStore(dir.Path, secrets);
        const string badJournal = "{\"version\":1,\"number\":1,\"current\":\"../../config.json\"}";
        File.WriteAllText(dir.File("journal.json"), badJournal);
        Assert.Throws<InvalidDataException>(() => store.Stage(Proposal(), null));
        Assert.Empty(secrets.Keys);
        Assert.Equal(badJournal, File.ReadAllText(dir.File("journal.json")));
    }

    [Fact]
    public void ProfilePreview_ListsMachineEnableChangesWithoutApplyingThem()
    {
        var file = GroupFileTests.Invitation();
        var changed = file.UpdateProfile(file.Profile with
        { Machines = file.Profile.Machines.Select(m => m with { Enabled = !m.Enabled }).ToList() });
        var diff = Proposal(changed).CompareWith(file.Profile);
        Assert.True(diff.ProfileChanged);
        Assert.Equal(5, diff.EnabledMachines.Count);
        Assert.Equal(7, diff.DisabledMachines.Count);
        Assert.Equal(7, file.Profile.Machines.Count(m => m.Enabled));
    }

    private static GroupSettingsStore FailingJournalPublisher(TempDir dir, ISecretStore secrets) =>
        new(dir.Path, secrets, (temporary, target) =>
        {
            Assert.Equal(dir.File("journal.json"), target);
            Assert.True(File.Exists(temporary));
            Assert.NotEmpty(File.ReadAllBytes(temporary));
            // FileShare.Read does not deny rename on Unix. Fail at the actual publication boundary.
            throw new IOException("Simulated journal replacement failure.");
        });

    private sealed class FailingSecrets : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public bool FailSet { get; set; }
        public bool FailAfterWrite { get; set; }
        public string? Get(string key) => _inner.Get(key);
        public void Set(string key, string value)
        {
            if (FailSet && !FailAfterWrite) throw new IOException("Simulated secret write failure.");
            _inner.Set(key, value);
            if (FailSet) throw new IOException("Simulated uncertain secret write result.");
        }
        public bool Remove(string key) => _inner.Remove(key);
        public IReadOnlyCollection<string> Keys => _inner.Keys;
    }
}
