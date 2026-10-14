using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace MaszynyAlert.Windows.Tests;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MaszynyAlert-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "secrets.dat");

    public DpapiSecretStoreTests() => Directory.CreateDirectory(_directory);
    private DpapiSecretStore Open() => DpapiSecretStore.ForCurrentUser(_directory);

    [Fact]
    public void ReloadReadsSeparateEditorChangesAndPreservesCacheOnCorruption()
    {
        var reader = Open();
        reader.Set("machine", "old");
        var editor = Open();
        editor.Set("machine", "new");
        Assert.Equal("old", reader.Get("machine"));
        reader.Reload();
        Assert.Equal("new", reader.Get("machine"));
        File.WriteAllText(FilePath, "broken");
        Assert.Throws<InvalidDataException>(() => reader.Reload());
        Assert.Equal("new", reader.Get("machine"));
    }

    [Fact]
    public void CorruptFileCannotBeOverwrittenAfterTolerantRead()
    {
        const string broken = "{ broken JSON";
        File.WriteAllText(FilePath, broken);
        var store = Open();
        Assert.Null(store.Get("machine"));
        Assert.Throws<InvalidDataException>(() => store.Set("machine", "replacement"));
        Assert.Equal(broken, File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"machine\":null}")]
    public void InvalidDocumentCannotBeOverwritten(string json)
    {
        File.WriteAllText(FilePath, json);
        Assert.Throws<InvalidDataException>(() => Open().Set("other", "replacement"));
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedWriteDoesNotChangeMemoryOrDisk(bool remove)
    {
        var store = Open();
        store.Set("machine", "original");
        byte[] original = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? error = Record.Exception(() =>
            {
                if (remove) store.Remove("machine");
                else store.Set("machine", "replacement");
            });
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a blocked write, got {error?.GetType().Name ?? "success"}.");
            Assert.Equal("original", store.Get("machine"));
            Assert.Equal(original, File.ReadAllBytes(FilePath));
            Assert.Single(Directory.GetFiles(_directory, "secrets.dat*"));
        }
        store.Set("machine", "retry");
        Assert.Equal("retry", Open().Get("machine"));
    }

    [Theory]
    [InlineData(DataProtectionScope.CurrentUser)]
    [InlineData(DataProtectionScope.LocalMachine)]
    public void RealDpapiSurvivesReopenAndReplacement(DataProtectionScope scope)
    {
        var store = new DpapiSecretStore(FilePath, scope);
        store.Set("first", "synthetic-secret-ąć");
        store.Set("second", "second-secret");
        store.Set("first", "changed-secret");
        var reopened = new DpapiSecretStore(FilePath, scope);
        Assert.Equal("changed-secret", reopened.Get("first"));
        Assert.Equal("second-secret", reopened.Get("second"));
        Assert.DoesNotContain("secret", File.ReadAllText(FilePath));
        Assert.True(reopened.Remove("first"));
        Assert.False(reopened.Remove("absent"));
        Assert.Null(new DpapiSecretStore(FilePath, scope).Get("first"));
        Assert.Single(Directory.GetFiles(_directory, "secrets.dat*"));
    }

    [Fact]
    public void InvalidCiphertextDoesNotHideOtherEntries()
    {
        Open().Set("good", "value");
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))!;
        entries["broken"] = "not base64";
        File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
        var store = Open();
        Assert.Null(store.Get("broken"));
        Assert.Equal("value", store.Get("good"));
        store.Set("new", "new-value");
        Assert.Contains("broken", Open().Keys);
    }

    [Fact]
    public void ReplacementPreservesExplicitFilePermissions()
    {
        var store = Open();
        store.Set("machine", "original");
        var file = new FileInfo(FilePath);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(acl);
        string before = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        store.Set("machine", "replacement");
        Assert.Equal(before, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void SequentialWritersPreserveEntriesWrittenSinceTheirLastRead()
    {
        var first = Open();
        var second = Open();
        Assert.Empty(first.Keys);
        Assert.Empty(second.Keys);
        first.Set("first", "value-one");
        second.Set("second", "value-two");
        var result = Open();
        Assert.Equal("value-one", result.Get("first"));
        Assert.Equal("value-two", result.Get("second"));
    }

    [Fact]
    public async Task ConcurrentWritersFromIndependentInstances_LoseNoEntries()
    {
        // Edytor UAC i helper grupy piszą różne klucze w tym samym czasie; każdy zapis musi przetrwać.
        const int writers = 6, perWriter = 12;
        using var go = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var store = new DpapiSecretStore(FilePath, DataProtectionScope.CurrentUser) { WriteLockTimeout = TimeSpan.FromSeconds(120) };
            go.Wait();
            for (int i = 0; i < perWriter; i++) store.Set($"w{w}-k{i}", $"v{w}-{i}");
        })).ToArray();
        go.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(180));
        var result = Open();
        Assert.Equal(writers * perWriter, result.Keys.Count);
        Assert.Equal("v3-7", result.Get("w3-k7"));
    }

    [Fact]
    public async Task WriteWaitsForAnotherProcessHoldingTheWriteLock()
    {
        Open().Set("machine", "original");
        var store = Open();
        var hold = TimeSpan.FromMilliseconds(400);
        using var released = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var locked = new FileStream(Path.Combine(_directory, "secrets.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            released.Set(); Thread.Sleep(hold);
        });
        released.Wait();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        store.Set("other", "value");
        Assert.True(clock.Elapsed >= hold - TimeSpan.FromMilliseconds(100), $"zapis nie czekał na blokadę ({clock.ElapsedMilliseconds} ms)");
        await holder;
        Assert.Equal("original", Open().Get("machine"));
        Assert.Equal("value", Open().Get("other"));
    }

    [Fact]
    public void WriteLockHeldTooLong_RefusesWrite_WithoutTouchingFileOrCache()
    {
        Open().Set("machine", "original");
        var store = new DpapiSecretStore(FilePath, DataProtectionScope.CurrentUser) { WriteLockTimeout = TimeSpan.FromMilliseconds(200) };
        Assert.Equal("original", store.Get("machine"));
        byte[] before = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(Path.Combine(_directory, "secrets.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => store.Set("other", "value"));
            Assert.Contains("zajęty", ex.Message);
            Assert.Throws<IOException>(() => store.Remove("machine"));
        }
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Null(store.Get("other")); Assert.Equal("original", store.Get("machine"));
        store.Set("other", "value");
        Assert.Equal("value", Open().Get("other"));
    }

    [Fact]
    public void UnreadableFileBlocksWritesAndCanBeRetriedAfterUnlock()
    {
        Open().Set("machine", "original");
        var store = Open();
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(store.Get("machine"));
            Assert.ThrowsAny<IOException>(() => store.Set("other", "value"));
        }
        store.Set("other", "value");
        Assert.Equal("original", Open().Get("machine"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
