using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerTestCi3SharedConfigZamekTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new("test-ci-3-warstwa");

    public void Dispose() => _dir.Dispose();

    private string LayerPath => _dir.File(Path.Combine("shared-config", "shared-config.json"));

    private static Dictionary<string, bool> D3() => new() { ["amm580"] = true, ["aidite1"] = false };

    private SharedConfigDocument SaveLayer() => new SharedConfigStore(LayerPath).Save(null, null, Noon, "PC-A", machines: D3());

    private static FileStream HoldGate(string layerPath) =>
        new(layerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static void AssertD3InEffectiveConfig(SharedConfigDocument layer)
    {
        AppConfig baseConfig = TestPaths.ExampleConfig;
        Assert.False(baseConfig.Machines.Single(m => m.Id == "amm580").Enabled);
        AppConfig effective = layer.ApplyTo(baseConfig);
        ConfigLoader.Validate(effective);
        Assert.True(effective.Machines.Single(m => m.Id == "amm580").Enabled, "580 włączona w Panelu nie jest w konfiguracji efektywnej");
        Assert.False(effective.Machines.Single(m => m.Id == "aidite1").Enabled, "Aidite 1 wyłączona w Panelu jest w konfiguracji efektywnej");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public void Start_ZamekTrzymanyPrzezInnyProces_WarstwaWczytanaPo2s_PrzelacznikiD3WKonfiguracjiEfektywnej(int holdSeconds)
    {
        if (!OperatingSystem.IsWindows()) return; // inny proces trzyma zamek przez przekierowanie cmd.exe
        SharedConfigDocument saved = SaveLayer();
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B"); // Program.cs:306

        TimeSpan reload;
        bool heldDuringReload, heldWhole;
        using (var holder = LockHolderProcess.Start(LayerPath + ".lock"))
        {
            var held = Stopwatch.StartNew();
            AssertGateBusy(LayerPath);
            var watch = Stopwatch.StartNew();
            sync.Reload(); // Program.cs:307 — w pamięci jeszcze nic
            reload = watch.Elapsed;
            heldDuringReload = holder.IsAlive;
            TimeSpan rest = TimeSpan.FromSeconds(holdSeconds) - held.Elapsed;
            if (rest > TimeSpan.Zero) Thread.Sleep(rest);
            heldWhole = holder.IsAlive;
        }

        Assert.True(heldDuringReload && heldWhole, "proces trzymający zamek zakończył się sam — scenariusz niesprawdzony");
        Assert.True(sync.Current is not null, $"program wystartował bez warstwy wspólnej, choć jest w pliku (LoadError: {sync.LoadError})");
        Assert.Null(sync.LoadError);
        Assert.Equal(saved.Hash, sync.Hash);
        AssertD3InEffectiveConfig(sync.Current!);
        Assert.True(reload >= TimeSpan.FromSeconds(1.9), $"odczyt nie czekał na zajęty zamek: {reload.TotalMilliseconds:N0} ms");
        if (holdSeconds >= 10)
            Assert.True(reload < TimeSpan.FromSeconds(holdSeconds - 2), $"odczyt czekał {reload.TotalSeconds:N1} s przy zamku trzymanym {holdSeconds} s");

        sync.Reload(); // zamek wolny
        Assert.Null(sync.LoadError);
        Assert.Equal(saved.Hash, sync.Hash);
    }

    [Fact]
    public void WTrakciePracy_ZamekTrzymanyPrzezInnyProces10s_OdczytBezBledu_ZapisyPadaja_WarstwaWPamieciIPlikBezZmian()
    {
        if (!OperatingSystem.IsWindows()) return;
        SharedConfigDocument saved = SaveLayer();
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        Assert.Equal(saved.Hash, sync.Hash);
        SharedConfigDocument fromGroup = saved with
        {
            EditedAt = Noon.AddMinutes(5), EditedBy = "PC-C",
            Machines = SharedConfigDocument.CanonicalMachines(new Dictionary<string, bool> { ["amm580"] = false }),
        };

        TimeSpan reload, edit, apply;
        Exception? editError, applyError;
        string? loadErrorAfterReload;
        bool heldWhole;
        using (var holder = LockHolderProcess.Start(LayerPath + ".lock"))
        {
            var held = Stopwatch.StartNew();
            AssertGateBusy(LayerPath);

            var watch = Stopwatch.StartNew();
            sync.Reload(); // np. po odbiorze z sieci albo „Odśwież” w Panelu
            reload = watch.Elapsed;
            loadErrorAfterReload = sync.LoadError;

            watch.Restart();
            editError = Record.Exception(() => sync.Edit(null, new OverlayConfig { Scale = 1.5 }, Noon.AddMinutes(1)));
            edit = watch.Elapsed;

            watch.Restart();
            applyError = Record.Exception(() => sync.Apply(fromGroup)); // GroupClusterNode łapie IOException i powtórzy uzgadnianie
            apply = watch.Elapsed;

            TimeSpan rest = TimeSpan.FromSeconds(10) - held.Elapsed;
            if (rest > TimeSpan.Zero) Thread.Sleep(rest);
            heldWhole = holder.IsAlive;
        }

        Assert.True(heldWhole, "proces trzymający zamek zakończył się sam — scenariusz niesprawdzony");
        Assert.Null(loadErrorAfterReload);
        Assert.True(reload >= TimeSpan.FromSeconds(1.9), $"odczyt nie czekał na zajęty zamek: {reload.TotalMilliseconds:N0} ms");
        var editIo = Assert.IsAssignableFrom<IOException>(editError);
        Assert.Contains(".lock", editIo.Message, StringComparison.Ordinal);
        Assert.True(edit >= TimeSpan.FromSeconds(1.9), $"zapis nie czekał na zajęty zamek: {edit.TotalMilliseconds:N0} ms");
        var applyIo = Assert.IsAssignableFrom<IOException>(applyError);
        Assert.Contains(".lock", applyIo.Message, StringComparison.Ordinal);
        Assert.True(apply >= TimeSpan.FromSeconds(1.9), $"scalenie nie czekało na zajęty zamek: {apply.TotalMilliseconds:N0} ms");
        Assert.Equal(saved.Hash, sync.Hash);
        Assert.Null(sync.LoadError);
        Assert.Equal(saved.Hash, new SharedConfigStore(LayerPath).Load()!.Hash);
        AssertD3InEffectiveConfig(sync.Current!);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(LayerPath)!, "*.tmp"));

        // Po zwolnieniu: dokument z grupy wchodzi (uzgadnianie powtórzone), odczyt bez błędu.
        Assert.True(sync.Apply(fromGroup));
        Assert.Null(sync.LoadError);
        Assert.Equal(fromGroup.Hash, sync.Hash);
    }

    /// <summary>
    /// Zamek zajęty dłużej niż 2 s, a pliku warstwy w tej chwili nie ma (podmiana utknęła po przeniesieniu starego pliku na <c>.bak</c>):
    /// w trakcie pracy zostaje POPRZEDNI dokument (ten sam obiekt) z powodem „zamek”; na starcie — brak warstwy Z POWODEM (ostrzeżenie
    /// w logu programu), nigdy cichy „brak warstwy”. Mutacja „odczyt bez zamka zwraca brak pliku jako wynik” czerwieni oba warianty.
    /// </summary>
    [Fact]
    public void ZamekZajetyDluzejNiz2s_PlikaWarstwyNieMa_PoprzedniaWarstwaZostaje_NaStarcieBladZamkaZamiastCichegoBraku()
    {
        SharedConfigDocument saved = SaveLayer();
        var working = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        working.Reload();
        SharedConfigDocument loaded = working.Current!;
        var starting = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");

        TimeSpan reload;
        using (HoldGate(LayerPath))
        {
            File.Move(LayerPath, LayerPath + ".bak", overwrite: true); // ReplaceFile: stary plik już .bak, nowy jeszcze bez nazwy
            var watch = Stopwatch.StartNew();
            working.Reload();
            reload = watch.Elapsed;
            starting.Reload();
            File.Move(LayerPath + ".bak", LayerPath); // podmiana dokończona przed zwolnieniem zamka
        }

        Assert.Same(loaded, working.Current); // brak pliku przy zajętym zamku nie jest „brakiem warstwy”
        Assert.Contains(".lock", working.LoadError ?? "(brak LoadError)", StringComparison.Ordinal);
        Assert.True(reload >= TimeSpan.FromSeconds(1.9), $"błąd zamka bez czekania: {reload.TotalMilliseconds:N0} ms");
        Assert.Null(starting.Current);
        Assert.Contains(".lock", starting.LoadError ?? "(brak LoadError — cichy brak warstwy)", StringComparison.Ordinal);

        working.Reload();
        starting.Reload();
        Assert.Null(working.LoadError);
        Assert.Equal(saved.Hash, working.Hash);
        Assert.Null(starting.LoadError);
        Assert.Equal(saved.Hash, starting.Hash);
    }

    [Fact]
    public void Start_ZamekZajety_PlikChwilowoTrzymanyBezWspoldzielenia_OdczytBezZamkaPonawia()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None na pliku warstwy — semantyka współdzielenia Windows
        SharedConfigDocument saved = SaveLayer();
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");

        TimeSpan reload;
        using (HoldGate(LayerPath))
        {
            var fileHeld = new FileStream(LayerPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var releaser = new Thread(() => { Thread.Sleep(2150); fileHeld.Dispose(); }) { IsBackground = true };
            var watch = Stopwatch.StartNew();
            releaser.Start();
            sync.Reload();
            reload = watch.Elapsed;
            releaser.Join();
        }

        if (sync.LoadError is null)
        {
            Assert.Equal(saved.Hash, sync.Hash);
            AssertD3InEffectiveConfig(sync.Current!);
        }
        else
        {
            Assert.Contains(".lock", sync.LoadError, StringComparison.Ordinal);
            Assert.True(reload >= TimeSpan.FromSeconds(2.4),
                $"błąd zamka po {reload.TotalMilliseconds:N0} ms — odczyt bez zamka nie ponawiał w oknie (plik zwolniony po ~2,15 s)");
        }
    }

    /// <summary>
    /// Zamek zajęty dłużej niż 2 s, plik warstwy uszkodzony: powód to uszkodzenie (jak spod zamka), nie „zamek zajęty” — odczyt bez
    /// zamka nie przykrywa prawdziwego błędu. Warstwa w pamięci zostaje.
    /// </summary>
    [Fact]
    public void ZamekZajetyDluzejNiz2s_PlikUszkodzony_PowodemUszkodzenieNieZamek_WarstwaZostaje()
    {
        SaveLayer();
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        SharedConfigDocument loaded = sync.Current!;

        string expected;
        using (HoldGate(LayerPath))
        {
            File.WriteAllText(LayerPath, "{ to nie jest JSON");
            expected = Assert.Throws<InvalidDataException>(() => new SharedConfigStore(LayerPath).Load()).Message;
            sync.Reload();
        }

        Assert.Same(loaded, sync.Current);
        Assert.Equal(expected, sync.LoadError);
    }

    /// <summary>
    /// Zamek zajęty dłużej niż 2 s, a to konto nie ma prawa ODCZYTU pliku warstwy: powód to odmowa dostępu, nie „zamek zajęty”.
    /// </summary>
    [Fact]
    public void ZamekZajetyDluzejNiz2s_BrakPrawaOdczytuPliku_PowodemOdmowaDostepuNieZamek_WarstwaZostaje()
    {
        if (!OperatingSystem.IsWindows()) return;
        SaveLayer();
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        SharedConfigDocument loaded = sync.Current!;

        string expected;
        var file = new FileInfo(LayerPath);
        using (HoldGate(LayerPath))
        {
            DenyRead(file);
            try
            {
                expected = Assert.Throws<UnauthorizedAccessException>(() => new SharedConfigStore(LayerPath).Load()).Message;
                sync.Reload();
            }
            finally { AllowRead(file); }
        }

        Assert.Same(loaded, sync.Current);
        Assert.Equal(expected, sync.LoadError);
        sync.Reload();
        Assert.Null(sync.LoadError);
    }

    private static void AssertGateBusy(string layerPath)
    {
        try
        {
            using (HoldGate(layerPath)) { }
        }
        catch (IOException)
        {
            return;
        }
        Assert.Fail("zamek nie jest zajęty przez inny proces — scenariusz niesprawdzony");
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule ReadDenied() =>
        new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);

    [SupportedOSPlatform("windows")]
    private static void DenyRead(FileInfo file)
    {
        FileSecurity security = file.GetAccessControl();
        security.AddAccessRule(ReadDenied());
        file.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AllowRead(FileInfo file)
    {
        FileSecurity security = file.GetAccessControl();
        security.RemoveAccessRule(ReadDenied());
        file.SetAccessControl(security);
    }

    /// <summary>
    /// Inny PROCES trzyma plik zamka: <c>cmd.exe</c> z przekierowaniem <c>&gt;&gt;</c> do pliku zamka (uchwyt do zapisu — blokuje
    /// <c>FileShare.None</c> programu tak samo jak zamek innego procesu programu). Trzyma, dopóki nie zamkniemy mu wejścia (<c>set /p</c>);
    /// gotowość = pierwszy wiersz w pliku zamka.
    /// </summary>
    private sealed class LockHolderProcess : IDisposable
    {
        private readonly Process _process;

        private LockHolderProcess(Process process) => _process = process;

        public bool IsAlive => !_process.HasExited;

        public static LockHolderProcess Start(string lockPath)
        {
            long before = File.Exists(lockPath) ? new FileInfo(lockPath).Length : 0;
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                Arguments = $"/d /s /c \"(echo zamek& set /p x=)>>\"{lockPath}\"\"",
            };
            var holder = new LockHolderProcess(Process.Start(start)!);
            var watch = Stopwatch.StartNew();
            while (!(File.Exists(lockPath) && new FileInfo(lockPath).Length > before))
            {
                if (!holder.IsAlive || watch.Elapsed > TimeSpan.FromSeconds(30))
                {
                    holder.Dispose();
                    Assert.Fail("proces trzymający zamek nie wystartował");
                }
                Thread.Sleep(10);
            }
            return holder;
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.Close(); }
                catch (IOException) { }
                if (!_process.WaitForExit(10_000)) _process.Kill();
            }
            _process.Dispose();
        }
    }
}
