using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerTestCiSharedConfigOdczytTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new("test-ci-warstwa");

    public void Dispose() => _dir.Dispose();

    private string LayerPath => _dir.File(Path.Combine("shared-config", "shared-config.json"));

    private static FileStream HoldGate(string path) =>
        new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void Reload_ZamekZajetyChwile_CzekaIWczytujeNowyDokument()
    {
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument saved = store.Save(null, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["m1"] = true });
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        var held = HoldGate(LayerPath);
        // Osobny wątek, nie pula (jak SharedConfigStoreTests: kontynuacja Task.Delay potrafiła na runnerze nie dostać wątku w 2 s).
        var release = new Thread(() => { Thread.Sleep(300); held.Dispose(); }) { IsBackground = true };
        release.Start();

        sync.Reload();
        release.Join();

        Assert.Null(sync.LoadError);
        Assert.Equal(saved.Hash, sync.Hash);
    }

    [Fact]
    public void Reload_ZamekZajetyDluzejNiz2s_CzekanieOgraniczone_WarstwaWPamieciZostaje()
    {
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument saved = store.Save(null, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["m1"] = true });
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        Assert.Equal(saved.Hash, sync.Hash);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (HoldGate(LayerPath))
        {
            sync.Reload();
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"odczyt czekał {watch.Elapsed}");
        Assert.Equal(saved.Hash, sync.Hash);
        sync.Reload();
        Assert.Null(sync.LoadError);
        Assert.Equal(saved.Hash, sync.Hash);
    }

    [Fact]
    public void Reload_BezPlikuIBezZamka_BezWarstwy_NieZakladaPlikow()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LayerPath)!);
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");

        sync.Reload();

        Assert.Null(sync.Current);
        Assert.Null(sync.LoadError);
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(LayerPath)!));
    }

    [Fact]
    public void Reload_KatalogBezPrawaZapisu_CzytaBezZamka_NieZakladaZamka()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument saved = store.Save(null, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["m1"] = true });
        string directory = Path.GetDirectoryName(LayerPath)!;
        File.Delete(LayerPath + ".lock");
        var info = new DirectoryInfo(directory);
        RestrictToReadOnly(info);
        try
        {
            // Warunek wstępny: tego katalogu bieżące konto nie zapisze (bez wpisu Administratorów — także w sesji administratora).
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(directory, "proba.txt"), "x"));

            var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
            sync.Reload();

            Assert.Null(sync.LoadError);
            Assert.Equal(saved.Hash, sync.Hash);
            Assert.False(File.Exists(LayerPath + ".lock"));
        }
        finally
        {
            var inherited = new DirectorySecurity();
            inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            info.SetAccessControl(inherited);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToReadOnly(DirectoryInfo directory)
    {
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
