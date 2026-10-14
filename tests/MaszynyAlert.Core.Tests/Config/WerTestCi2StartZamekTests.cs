using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class WerTestCi2StartZamekTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new("test-ci-2-start");

    public void Dispose() => _dir.Dispose();

    private string LayerPath => _dir.File(Path.Combine("shared-config", "shared-config.json"));

    [Fact]
    public void Start_ZamekZajetyDluzejNiz2s_PierwszyReload_NieZostawiaProgramuBezWarstwyZPliku()
    {
        SharedConfigDocument saved = new SharedConfigStore(LayerPath).Save(null, null, Noon, "PC-A",
            machines: new Dictionary<string, bool> { ["amm580"] = true });
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), Environment.MachineName); // jak Program.cs:306

        using (new FileStream(LayerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            sync.Reload(); // Program.cs:307 — zamek trzymany przez cały odczyt (inny proces zawieszony w zapisie)
        }

        Assert.True(sync.Current is not null,
            $"program wystartował bez warstwy wspólnej, choć jest w pliku (LoadError: {sync.LoadError})");
        Assert.Equal(saved.Hash, sync.Hash);
        Assert.True(sync.Current!.Machines!["amm580"].Enabled);
    }

    /// <summary>
    /// Ten sam start, potem grupa przysyła TEN SAM dokument (uzgadnianie odcisków co ~10 s, bo ten komputer ogłasza brak warstwy):
    /// warstwa ma wrócić do pamięci.
    /// </summary>
    [Fact]
    public void Start_ZamekZajetyDluzejNiz2s_OdbiorTegoSamegoDokumentuZGrupy_PrzywracaWarstwe()
    {
        SharedConfigDocument saved = new SharedConfigStore(LayerPath).Save(null, null, Noon, "PC-A",
            machines: new Dictionary<string, bool> { ["amm580"] = true });
        var sync = new SharedConfigSync(new SharedConfigStore(LayerPath), Environment.MachineName);
        using (new FileStream(LayerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            sync.Reload();
        }

        sync.Apply(saved); // dokument z sieci = dokument w pliku

        Assert.True(sync.Current is not null, $"po odbiorze dokumentu z grupy nadal bez warstwy (LoadError: {sync.LoadError})");
        Assert.Equal(saved.Hash, sync.Hash);
    }
}
