using System.Diagnostics;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerNaprCi2SharedConfigZamekTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new("napr-ci-2-zamek");

    public void Dispose() => _dir.Dispose();

    private string LayerPath => _dir.File(Path.Combine("shared-config", "shared-config.json"));

    private static FileStream HoldGate(string path) => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>
    /// Krok symulacji po stronie testu (przeniesienie albo nadpisanie pliku warstwy) — ponawiany do 10 s, gdy obcy proces chwilę trzyma
    /// świeży plik bez <see cref="FileShare.Delete"/> (skaner na runnerze; ten sam powód co w <c>WerTestCi2SharedConfigKontraktTests</c>).
    /// </summary>
    private static void Retry(Action fileStep)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { fileStep(); return; }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(20); }
        }
    }

    [Fact]
    public void Reload_ZamekZajetyPonad2sWLucePodmiany_NieGubiWarstwy_BladZamka()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument saved = store.Save(null, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["m1"] = true });
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        Assert.Equal(saved.Hash, sync.Hash);

        Stopwatch watch;
        using (HoldGate(LayerPath))
        {
            Retry(() => File.Move(LayerPath, LayerPath + ".bak", overwrite: true)); // ReplaceFile: stary plik → .bak, nowy jeszcze bez nazwy
            watch = Stopwatch.StartNew();
            sync.Reload();
            watch.Stop();
        }

        Assert.NotNull(sync.Current); // warstwa nie znika
        Assert.Equal(saved.Hash, sync.Hash);
        Assert.True(sync.Current!.Machines!["m1"].Enabled);
        Assert.Contains(".lock", sync.LoadError, StringComparison.Ordinal);
        // Najpierw ~2 s czekania na zamek, potem odczyt bez zamka ponawiany (pliku nie ma) — nie od razu.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2.3), $"odczyt nie czekał na zamek i nie ponawiał: {watch.Elapsed.TotalMilliseconds:N0} ms");
    }

    /// <summary>
    /// Zamek zajęty dłużej niż 2 s, a plik w nazwie jest uszkodzony: odczyt bez zamka zgłasza to od razu, tym samym błędem co spod zamka
    /// (<see cref="InvalidDataException"/> → <see cref="SharedConfigSync.LoadError"/> z powodem), bez ponawiania jak przy „brak pliku”
    /// i bez maskowania błędem zamka; poprzedni dokument zostaje.
    /// </summary>
    [Fact]
    public void Reload_ZamekZajetyPonad2sPlikUszkodzony_BladTresciNieZamka_WarstwaZostaje()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument saved = store.Save(null, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["m1"] = true });
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();

        using (HoldGate(LayerPath))
        {
            Retry(() => File.WriteAllText(LayerPath, "{ to nie jest dokument"));
            sync.Reload();
        }

        Assert.Equal(saved.Hash, sync.Hash);
        Assert.NotNull(sync.LoadError);
        Assert.DoesNotContain(".lock", sync.LoadError, StringComparison.Ordinal);
        Assert.Contains("JSON", sync.LoadError, StringComparison.Ordinal);
    }
}
