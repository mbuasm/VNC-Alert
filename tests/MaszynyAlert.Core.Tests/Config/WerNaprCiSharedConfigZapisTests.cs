using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerNaprCiSharedConfigZapisTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new("napr-ci-warstwa");

    public void Dispose() => _dir.Dispose();

    [SupportedOSPlatform("windows")]
    private static void RestrictLikeRunnerWorkFolder(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.Modify, inherit,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), FileSystemRights.Modify, inherit,
            PropagationFlags.None, AccessControlType.Allow));
        try { new DirectoryInfo(directory).SetAccessControl(security); }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void KatalogBezWriteDacJakNaRunnerzeCi_KolejneZapisyScalanieIZgoda_PodmieniajaPlikIZostawiajaKopie()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = _dir.File("shared-config");
        Directory.CreateDirectory(directory);
        RestrictLikeRunnerWorkFolder(directory);
        string path = Path.Combine(directory, "shared-config.json");
        var store = new SharedConfigStore(path);

        SharedConfigDocument first = store.Save(null, null, Noon, "PC-B", machines: new Dictionary<string, bool> { ["m1"] = false });
        SharedConfigDocument second = store.Save(null, null, Noon.AddSeconds(30), "PC-B", machines: new Dictionary<string, bool> { ["m1"] = true });
        Assert.Equal(second.Hash, store.Load()!.Hash);
        Assert.Equal(first.Hash, new SharedConfigStore(path + ".bak").Load()!.Hash);

        Assert.True(store.Merge(second with { Overlay = new OverlayConfig { Scale = 1.5 }, EditedAt = Noon.AddMinutes(1), EditedBy = "PC-C" }));
        store.SaveApproval(new UpdateApproval
        {
            Version = "0.4.250.0", FileName = "VncAlert-Setup-0.4.250.0.exe", ApprovedBy = "PC-B", ApprovedAt = Noon.AddMinutes(2),
        }, Noon.AddMinutes(2), "PC-B");
        var sync = new SharedConfigSync(new SharedConfigStore(path), "PC-A");
        sync.Edit(null, new OverlayConfig { Scale = 1.25 }, Noon.AddMinutes(4));

        SharedConfigDocument file = store.Load()!;
        Assert.True(file.Machines!["m1"].Enabled);
        Assert.Equal("0.4.250.0", file.UpdateApproval!.Version);
        Assert.Equal(1.25, file.Overlay!.Scale);
        Assert.Null(sync.LoadError);
        Assert.Equal(file.Hash, sync.Hash);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }


    /// <summary>Uchwyt „obcego” procesu (skaner antywirusowy, Notatnik) na <paramref name="file"/> przez <paramref name="hold"/>.</summary>
    private static Thread HoldBriefly(string file, FileShare share, TimeSpan hold, ManualResetEventSlim opened)
    {
        var thread = new Thread(() =>
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, share))
            {
                opened.Set();
                Thread.Sleep(hold);
            }
        }) { IsBackground = true };
        thread.Start();
        return thread;
    }

    /// <summary>
    /// kopia <c>.bak</c> chwilę otwarta przez inny proces (skaner Defendera po podmianie —
    /// „Nie można usunąć pliku, który ma zostać zamieniony” 0x80070497) — zapis czeka zamiast padać,
    /// jak przy zajętym zamku (do 2 s).
    /// </summary>
    [Fact]
    public void KopiaBakChwilowoOtwartaPrzezInnyProces_ZapisCzekaZamiastPadac()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = _dir.File("shared-config.json");
        var store = new SharedConfigStore(path);
        store.Save(null, null, Noon, "PC-A");
        store.Save(null, null, Noon.AddSeconds(1), "PC-A"); // jest .bak
        using var opened = new ManualResetEventSlim();
        Thread holder = HoldBriefly(path + ".bak", FileShare.ReadWrite | FileShare.Delete, TimeSpan.FromMilliseconds(300), opened);
        Assert.True(opened.Wait(TimeSpan.FromSeconds(5)));

        Exception? error = Record.Exception(() => store.Save(null, new OverlayConfig { Scale = 1.5 }, Noon.AddSeconds(2), "PC-A"));
        holder.Join();

        Assert.Null(error);
        Assert.Equal(1.5, store.Load()!.Overlay!.Scale);
    }

    /// <summary>
    /// plik warstwy chwilę otwarty przez inny proces bez <see cref="FileShare.Delete"/> (Notatnik, skaner) — podmiana
    /// dostaje błąd współdzielenia 0x80070020; zapis czeka zamiast padać (do 2 s).
    /// </summary>
    [Fact]
    public void PlikWarstwyChwilowoOtwartyBezFileShareDelete_ZapisCzekaZamiastPadac()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = _dir.File("shared-config.json");
        var store = new SharedConfigStore(path);
        store.Save(null, null, Noon, "PC-A");
        using var opened = new ManualResetEventSlim();
        Thread holder = HoldBriefly(path, FileShare.Read, TimeSpan.FromMilliseconds(300), opened);
        Assert.True(opened.Wait(TimeSpan.FromSeconds(5)));

        Exception? error = Record.Exception(() => store.Save(null, new OverlayConfig { Scale = 1.5 }, Noon.AddSeconds(1), "PC-A"));
        holder.Join();

        Assert.Null(error);
        Assert.Equal(1.5, store.Load()!.Overlay!.Scale);
    }
}
