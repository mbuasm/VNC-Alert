using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Tests;

public sealed class WerTestCiTests
{
    /// <summary>
    /// bramkę wątku UI trzyma (3,5 s) „inna klasa testów”, a w tym czasie startuje test z końcem tuż za granicą doby.
    /// Czekanie na bramkę nie może zmienić tego, co test sprawdza.
    /// </summary>
    [Fact]
    public async Task Ci01_BramkaWatkuUiZajetaPrzezInnaKlase_KoniecTuzZaGranicaDobyNadalSprawdzaPrzeliczenie()
    {
        using var holding = new ManualResetEventSlim();
        Task holder = Task.Run(() => WpfTestHost.Run(() =>
        {
            holding.Set();
            Thread.Sleep(3500);
            return 0;
        }));
        Assert.True(holding.Wait(TimeSpan.FromSeconds(30)), "bramka wątku UI nie została zajęta");
        var test = new TestRaportPanelRefreshTests();
        await test.InitializeAsync();
        try
        {
            test.KoniecTuzZaGranicaDoby_PoUplywieCzasu_PrzeliczaRazIPotemNie();
        }
        finally
        {
            await test.DisposeAsync();
            await holder;
        }
    }

    [Fact]
    public void Ci02_KatalogBezWriteDacDlaWlasciciela_JakPracaRunneraCi_DrugiZapisWarstwyWspolnejWchodzi()
    {
        string root = Path.Combine(Path.GetTempPath(), "wer-ci-acl-" + Guid.NewGuid().ToString("N"));
        CreateLikeRunnerWork(root);
        try
        {
            string file = Path.Combine(root, "shared-config", "shared-config.json");
            var store = new SharedConfigStore(file);
            var at = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
            store.Save(null, null, at, "PC-B", machines: new Dictionary<string, bool> { ["m1"] = false, ["m2"] = true });

            var info = new FileInfo(file);
            FileSecurity acl = info.GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Allow));
            Assert.Throws<UnauthorizedAccessException>(() => info.SetAccessControl(acl));

            store.Save(null, null, at.AddSeconds(30), "PC-B", machines: new Dictionary<string, bool> { ["m1"] = true, ["m2"] = true });
            SharedConfigDocument? saved = store.Load();
            Assert.NotNull(saved);
            Assert.True(saved!.Machines!["m1"].Enabled);
            Assert.Equal(at.AddSeconds(30), saved.EditedAt);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void CreateLikeRunnerWork(string path)
    {
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), // OWNER RIGHTS
            FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.CreateDirectory(path);
    }
}
