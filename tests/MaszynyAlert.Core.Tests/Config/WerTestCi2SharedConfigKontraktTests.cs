using System.Collections.Concurrent;
using System.Diagnostics;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerTestCi2SharedConfigKontraktTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new("test-ci-2-warstwa");

    public void Dispose()
    {
        foreach (string file in Directory.Exists(_dir.Path) ? Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories) : [])
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _dir.Dispose();
    }

    private string LayerPath => _dir.File(Path.Combine("shared-config", "shared-config.json"));

    private static Dictionary<string, bool> Machine(bool enabled) => new() { ["m1"] = enabled };

    /// <summary>
    /// Krok symulacji po stronie testu (przeniesienie/kopia pliku warstwy) — ponawiany, gdy obcy proces chwilę trzyma świeży plik bez
    /// <see cref="FileShare.Delete"/> (skaner na runnerze).
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

    /// <summary>
    /// Stan pośredni podmiany pod zamkiem: zapisujący trzyma <c>.lock</c>, stary plik jest już pod nazwą <c>.bak</c>, nowego jeszcze
    /// nie ma. Reload wołany w tej chwili musi poczekać na zamek i przeczytać nowy dokument — albo (zamek dłużej niż 2 s, np. zatrzymany
    /// wątek) zostawić poprzedni dokument z powodem w <see cref="SharedConfigSync.LoadError"/>. Nigdy „brak warstwy”.
    /// </summary>
    [Fact]
    public void Reload_WLucePodmianyPodZamkiem_CzekaNaZapis_NieWidziBrakuWarstwy()
    {
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument before = store.Save(null, null, Noon, "PC-A", machines: Machine(true));
        // Nowy dokument przygotowany obok (ten sam format, zapis tą samą klasą) — „plik tymczasowy” podmiany.
        string prepared = _dir.File(Path.Combine("przygotowany", "shared-config.json"));
        SharedConfigDocument after = new SharedConfigStore(prepared).Save(null, null, Noon.AddMinutes(1), "PC-A", machines: Machine(false));
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        Assert.Equal(before.Hash, sync.Hash);

        Thread reload;
        long reloadMs = -1;
        using (new FileStream(LayerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Retry(() => File.Move(LayerPath, LayerPath + ".bak", overwrite: true)); // ReplaceFile: stary plik → .bak
            using var started = new ManualResetEventSlim();
            reload = new Thread(() =>
            {
                started.Set();
                var watch = Stopwatch.StartNew();
                sync.Reload();
                reloadMs = watch.ElapsedMilliseconds;
            }) { IsBackground = true };
            reload.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Thread.Sleep(300); // Reload trafia na lukę: pliku warstwy nie ma, zamek zajęty
            Retry(() => File.Copy(prepared, LayerPath)); // ReplaceFile: nowy plik dostaje nazwę
        }
        Assert.True(reload.Join(TimeSpan.FromSeconds(30)), "Reload nie wrócił");

        Assert.NotNull(sync.Current);
        if (sync.LoadError is null)
        {
            Assert.Equal(after.Hash, sync.Hash);
            Assert.False(sync.Current!.Machines!["m1"].Enabled);
        }
        else
        {
            // Maszyna zatrzymana na ponad 2 s między startem wątku a zwolnieniem zamka: zaprojektowany błąd zamka, warstwa zostaje.
            Assert.Contains(".lock", sync.LoadError, StringComparison.Ordinal);
            Assert.True(reloadMs >= 1900, $"błąd zamka bez czekania do 2 s ({reloadMs} ms)");
            Assert.Equal(before.Hash, sync.Hash);
        }
    }

    [Fact]
    public async Task ZapisyRownolegleZOdczytem_ZamekPonad2sDopuszczalny_CzerwoneUtrataWarstwyIInneBledy()
    {
        const int Saves = 100;
        if (!OperatingSystem.IsWindows()) return; // luka podmiany i błędy współdzielenia — semantyka ReplaceFile (Windows)
        var store = new SharedConfigStore(LayerPath);
        store.Save(null, null, Noon, "PC-A", machines: Machine(true)); // sekunda 0 — parzysta: m1 włączona
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        Assert.NotNull(sync.Current);

        // Rodzaj problemu → liczba i pierwszy przykład (w komunikacie porażki widać, CO się zepsuło, nie tylko że coś).
        var failures = new ConcurrentDictionary<string, (int Count, string Example)>();
        void Fail(string kind, string example) => failures.AddOrUpdate(kind, (1, example), (_, f) => (f.Count + 1, f.Example));
        int saves = 0, saveLockTimeouts = 0, reads = 0, cleanReads = 0, lockErrors = 0;
        SharedConfigDocument? lastSaved = null;
        using var done = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            try
            {
                for (int i = 1; i <= Saves; i++)
                {
                    var saveWatch = Stopwatch.StartNew();
                    try
                    {
                        lastSaved = store.Save(null, null, Noon.AddSeconds(i), "PC-A", machines: Machine(i % 2 == 0));
                        saves++;
                    }
                    catch (IOException ex) when (ex.Message.Contains(".lock", StringComparison.Ordinal) && saveWatch.ElapsedMilliseconds >= 1900)
                    {
                        saveLockTimeouts++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        Fail("nieudany zapis", $"zapis {i}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                    }
                    Thread.Sleep(20);
                }
            }
            finally { done.Cancel(); }
        });
        Task reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                SharedConfigDocument? previous = sync.Current;
                var watch = Stopwatch.StartNew();
                sync.Reload();
                long reloadMs = watch.ElapsedMilliseconds;
                reads++;
                SharedConfigDocument? current = sync.Current;
                if (current is null) { Fail("odczyt bez warstwy (Current == null)", sync.LoadError ?? "bez LoadError"); continue; }
                if (current.Machines is not { } machines || !machines.TryGetValue("m1", out var m1))
                {
                    Fail("dokument bez przełączników maszyn", current.Hash);
                    continue;
                }
                // Każdy zapisany dokument ma m1 = (sekunda od południa parzysta) — spójność przełącznika z wersją dokumentu.
                int second = (int)Math.Round((current.EditedAt - Noon).TotalSeconds);
                if (m1.Enabled != (second % 2 == 0) || current.EditedBy != "PC-A")
                    Fail("niespójny dokument", $"sekunda {second}, m1 {m1.Enabled}, autor {current.EditedBy}");
                if (sync.LoadError is not { } error) { cleanReads++; continue; }
                // Pod zamkiem jedyny spodziewany błąd odczytu to zamek zajęty > 2 s. Błąd współdzielenia na SAMYM pliku albo „brak pliku”
                // znaczy, że odczyt i podmiana spotkały się na pliku — dokładnie to, przed czym chroni zamek.
                if (!error.Contains(".lock", StringComparison.Ordinal)) Fail("błąd odczytu inny niż zajęty zamek", error);
                else if (!ReferenceEquals(current, previous)) Fail("błąd zamka, a warstwa w pamięci się zmieniła", error);
                // Błąd zamka jest zaprojektowany dopiero po ~2 s czekania (OpenGate) — szybszy znaczy, że odczyt nie czeka na zapis.
                else if (reloadMs < 1900) Fail("błąd zamka bez czekania do 2 s", $"{reloadMs} ms: {error}");
                else lockErrors++;
            }
        });
        await Task.WhenAll(writer, reader);

        string summary = $"zapisów udanych {saves}/{Saves} (zamek zajęty > 2 s przy zapisie {saveLockTimeouts}), odczytów {reads} (bez błędu {cleanReads}, zamek zajęty > 2 s z zachowaną warstwą "
            + $"{lockErrors}); problemy: " + (failures.IsEmpty ? "brak" : string.Join(" | ",
                failures.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}: {f.Value.Count} (np. {f.Value.Example})")));
        Assert.True(failures.IsEmpty, summary);
        Assert.True(cleanReads >= 10, "test nic nie sprawdził — prawie każdy odczyt trafiał na zajęty zamek: " + summary);
        Assert.True(saves >= Saves / 2, "test nic nie sprawdził — prawie każdy zapis trafiał na zajęty zamek: " + summary);
        sync.Reload(); // zapisy skończone — zamek wolny
        Assert.Null(sync.LoadError);
        Assert.Equal(lastSaved!.Hash, sync.Hash); // ostatni udany zapis
    }

    /// <summary>
    /// Plik warstwy z atrybutem „tylko do odczytu” (ręcznie zmieniony): podmiana dostaje odmowę dostępu — ponawianie ani
    /// <c>ignoreMetadataErrors</c> jej nie przeczekują ani nie przykrywają; zapis pada od razu, plik i kopia bez zmian.
    /// </summary>
    [Fact]
    public void Zapis_PlikWarstwyTylkoDoOdczytu_OdmowaOdRazu_BezPonawiania_PlikIKopiaBezZmian()
    {
        if (!OperatingSystem.IsWindows()) return; // atrybut ReadOnly blokuje podmianę tylko na Windows
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument first = store.Save(null, null, Noon, "PC-A", machines: Machine(true));
        SharedConfigDocument second = store.Save(null, null, Noon.AddSeconds(1), "PC-A", machines: Machine(false));
        File.SetAttributes(LayerPath, FileAttributes.ReadOnly);

        var watch = Stopwatch.StartNew();
        Exception? error = Record.Exception(() => store.Save(null, null, Noon.AddSeconds(2), "PC-A", machines: Machine(true)));
        watch.Stop();

        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.9), $"odmowa dostępu przeczekiwana jak zajęty plik: {watch.Elapsed.TotalMilliseconds:N0} ms");
        Assert.Equal(second.Hash, store.Load()!.Hash);
        Assert.Equal(first.Hash, new SharedConfigStore(LayerPath + ".bak").Load()!.Hash);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(LayerPath)!, "*.tmp"));
        // Zamek zwolniony mimo błędu: po zdjęciu atrybutu zapis przechodzi od razu.
        File.SetAttributes(LayerPath, FileAttributes.Normal);
        store.Save(null, null, Noon.AddSeconds(3), "PC-A", machines: Machine(true));
        Assert.True(store.Load()!.Machines!["m1"].Enabled);
    }

    /// <summary>
    /// Plik warstwy trzymany przez inny proces DŁUŻEJ niż 2 s: ponawianie ma granicę — zapis kończy się błędem współdzielenia z oryginalnym
    /// kodem (0x80070020) po ~2 s, zanim uchwyt zniknie; plik i kopia bez zmian, bez <c>.tmp</c>, zamek zwolniony. Wcześniejszy odczyt
    /// zostaje w pamięci (<see cref="SharedConfigSync"/> nie traci warstwy po nieudanym zapisie).
    /// </summary>
    [Fact]
    public void Zapis_PlikTrzymanyDluzejNiz2s_BladZOryginalnymKodemPoOkolo2s_StanBezZmian()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare bez Delete blokuje podmianę tylko na Windows
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument first = store.Save(null, null, Noon, "PC-A", machines: Machine(true));
        SharedConfigDocument second = store.Save(null, null, Noon.AddSeconds(1), "PC-A", machines: Machine(false));
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();

        using var release = new ManualResetEventSlim();
        using var opened = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (new FileStream(LayerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                opened.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(opened.Wait(TimeSpan.FromSeconds(10)));

        var watch = Stopwatch.StartNew();
        Exception? error;
        try { error = Record.Exception(() => sync.Edit(null, new OverlayConfig { Scale = 1.5 }, Noon.AddSeconds(2))); }
        finally { watch.Stop(); release.Set(); holder.Join(); }

        var io = Assert.IsAssignableFrom<IOException>(error);
        // Oryginalny kod błędu współdzielenia (lokalnie zawsze 0x80070020; ReplaceFile zgłasza tę samą przeszkodę też jako 0x80070497).
        Assert.Contains(io.HResult, new[] { unchecked((int)0x80070020), unchecked((int)0x80070497) });
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.9), $"zapis nie przeczekał zajętego pliku: {watch.Elapsed.TotalMilliseconds:N0} ms");
        Assert.Equal(second.Hash, store.Load()!.Hash);
        Assert.Equal(first.Hash, new SharedConfigStore(LayerPath + ".bak").Load()!.Hash);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(LayerPath)!, "*.tmp"));
        Assert.Equal(second.Hash, sync.Hash);
        store.Save(null, null, Noon.AddSeconds(3), "PC-A", machines: Machine(true)); // zamek wolny po błędzie
    }

    [Fact]
    public void ZamekTylkoDoOdczytu_ZapisOdmowaOdRazu_OdczytBezZamkaBezBledu()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SharedConfigStore(LayerPath);
        SharedConfigDocument saved = store.Save(null, null, Noon, "PC-A", machines: Machine(true));
        File.SetAttributes(LayerPath + ".lock", FileAttributes.ReadOnly);

        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), "PC-B");
        sync.Reload();
        Assert.Null(sync.LoadError);
        Assert.Equal(saved.Hash, sync.Hash);

        var watch = Stopwatch.StartNew();
        Exception? error = Record.Exception(() => store.Save(null, null, Noon.AddSeconds(1), "PC-A", machines: Machine(false)));
        watch.Stop();
        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.9), $"odmowa dostępu do zamka przeczekiwana: {watch.Elapsed.TotalMilliseconds:N0} ms");
        Assert.Equal(saved.Hash, store.Load()!.Hash);
    }
}
