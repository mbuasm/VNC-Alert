using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class CaptureStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 17, 14, 5, 9, TimeSpan.Zero);

    // Nazwa pliku używa CZASU LOKALNEGO maszyny uruchamiającej testy — licząc oczekiwaną
    // nazwę tak samo (At.ToLocalTime()), test pozostaje poprawny niezależnie od strefy czasowej CI.
    private static string ExpectedStamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyyMMdd-HHmmss");

    private static RgbaImage SmallImage()
    {
        var img = new RgbaImage(4, 3);
        img.Fill(new Rgba(220, 30, 30));
        return img;
    }

    [Fact]
    public void Save_UzywaWzorcaNazwyZIdentyfikatoremCzasemIEtykieta()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);

        string? path = store.Save("aidite1", SmallImage(), At, "blad");

        Assert.NotNull(path);
        Assert.Equal($"aidite1_{ExpectedStamp(At)}_blad.png", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        Assert.Equal(4, PngCodec.DecodeFile(path).Width);
    }

    [Fact]
    public void Save_PrzyKolizjiNazwy_DodajeKolejnyNumer()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);

        string? first = store.Save("aidite1", SmallImage(), At, "blad");
        string? second = store.Save("aidite1", SmallImage(), At, "blad");
        string? third = store.Save("aidite1", SmallImage(), At, "blad");

        string stamp = ExpectedStamp(At);
        Assert.Equal($"aidite1_{stamp}_blad.png", Path.GetFileName(first));
        Assert.Equal($"aidite1_{stamp}_blad-2.png", Path.GetFileName(second));
        Assert.Equal($"aidite1_{stamp}_blad-3.png", Path.GetFileName(third));
    }

    [Fact]
    public void Save_GdyKataloguNieMa_TworzyGo()
    {
        using var dir = new TempDir("captures");
        string target = Path.Combine(dir.Path, "nowy", "podkatalog");
        var store = new FileCaptureStore(target);

        string? path = store.Save("amm580", SmallImage(), At, "pelny");

        Assert.NotNull(path);
        Assert.True(Directory.Exists(target));
        Assert.Equal($"amm580_{ExpectedStamp(At)}_pelny.png", Path.GetFileName(path));
    }

    [Fact]
    public void Prune_UsuwaPlikiStarszeNizOkresPrzechowywania()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);
        string stary = store.Save("aidite1", SmallImage(), At, "blad")!;
        string nowy = store.Save("aidite2", SmallImage(), At, "blad")!;

        File.SetLastWriteTimeUtc(stary, DateTime.UtcNow - TimeSpan.FromDays(10));
        File.SetLastWriteTimeUtc(nowy, DateTime.UtcNow - TimeSpan.FromHours(1));

        store.Prune(TimeSpan.FromDays(7));

        Assert.False(File.Exists(stary));
        Assert.True(File.Exists(nowy));
    }

    [Fact]
    public void Prune_GdyKataloguNieMa_NieRzucaWyjatku()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(Path.Combine(dir.Path, "nie-ma-takiego"));

        store.Prune(TimeSpan.FromDays(1));
    }

    [Fact]
    public void NullCaptureStore_NiczegoNieZapisujeIZwracaNull()
    {
        ICaptureStore store = new NullCaptureStore();

        Assert.Null(store.Save("aidite1", SmallImage(), At, "blad"));
        store.Prune(TimeSpan.FromDays(1));
        store.Prune(TimeSpan.FromDays(1), 1024, null);
        Assert.Equal(CaptureFolderStats.Empty, store.Describe());
    }


    /// <summary>Zapisuje plik PNG o zadanym rozmiarze i wieku, żeby test limitu nie zależał od kompresji.</summary>
    private static string WriteSized(string dir, string name, int bytes, TimeSpan age)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Prune_PoPrzekroczeniuLimitu_UsuwaNajstarszeAzDoProgu()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);
        string najstarszy = WriteSized(dir.Path, "aidite1_a_blad.png", 400_000, TimeSpan.FromHours(5));
        string sredni = WriteSized(dir.Path, "aidite1_b_blad.png", 400_000, TimeSpan.FromHours(3));
        string najnowszy = WriteSized(dir.Path, "aidite1_c_blad.png", 400_000, TimeSpan.FromHours(1));

        store.Prune(TimeSpan.FromDays(3), maxBytes: 900_000, protectedFiles: null);

        Assert.False(File.Exists(najstarszy));
        Assert.True(File.Exists(sredni));
        Assert.True(File.Exists(najnowszy));
    }

    [Fact]
    public void Prune_NieUsuwaZrzutuWskazywanegoPrzezOtwartyAlert()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);
        string chroniony = WriteSized(dir.Path, "aidite1_a_blad.png", 400_000, TimeSpan.FromDays(30));
        string zwykly = WriteSized(dir.Path, "aidite1_b_blad.png", 400_000, TimeSpan.FromHours(3));
        string najnowszy = WriteSized(dir.Path, "aidite1_c_blad.png", 400_000, TimeSpan.FromHours(1));

        // Chroniony jest jednocześnie przeterminowany (30 dni) i najstarszy — ani wiek, ani limit
        // rozmiaru nie mogą go usunąć, bo alert wciąż oferuje przy nim przycisk „Pokaż zrzut”.
        store.Prune(TimeSpan.FromDays(3), maxBytes: 900_000, protectedFiles: new[] { chroniony });

        Assert.True(File.Exists(chroniony));
        Assert.False(File.Exists(zwykly));
        Assert.True(File.Exists(najnowszy));
    }

    [Fact]
    public void Prune_NieUsuwaPlikowNiePngAniZPodkatalogow_ChocLiczyIchRozmiar()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);
        string notatka = WriteSized(dir.Path, "notatka.txt", 400_000, TimeSpan.FromDays(30));
        string wPodkatalogu = WriteSized(Path.Combine(dir.Path, "stare"), "aidite1_x_blad.png", 400_000, TimeSpan.FromDays(30));
        string png = WriteSized(dir.Path, "aidite1_a_blad.png", 400_000, TimeSpan.FromHours(1));

        // 1,2 MB przy limicie 0,9 MB: jedynym kandydatem do usunięcia jest PNG z katalogu głównego —
        // plik nie-PNG i plik z podkatalogu liczą się do zajętego miejsca, ale program ich nie kasuje,
        // bo nie on je tam położył (i nie usuwa ich też przeterminowanie po 30 dniach).
        store.Prune(TimeSpan.FromDays(3), maxBytes: 900_000, protectedFiles: null);

        Assert.True(File.Exists(notatka));
        Assert.True(File.Exists(wPodkatalogu));
        Assert.False(File.Exists(png));
    }

    [Fact]
    public void Prune_MiesciSieWLimicie_NiczegoNieUsuwa()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);
        string a = WriteSized(dir.Path, "aidite1_a_blad.png", 100_000, TimeSpan.FromHours(5));
        string b = WriteSized(dir.Path, "aidite1_b_blad.png", 100_000, TimeSpan.FromHours(1));

        store.Prune(TimeSpan.FromDays(3), maxBytes: 20L * 1024 * 1024, protectedFiles: null);

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Describe_PodajeLiczbePlikowRozmiarIWiekNajstarszego()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(dir.Path);
        WriteSized(dir.Path, "notatka.txt", 999, TimeSpan.FromDays(9));
        WriteSized(dir.Path, "aidite1_a_blad.png", 600_000, TimeSpan.FromDays(2));
        WriteSized(dir.Path, "aidite1_b_blad.png", 600_000, TimeSpan.FromHours(1));

        CaptureFolderStats stats = store.Describe();

        Assert.Equal(2, stats.Files);
        Assert.Equal(1_200_000, stats.Bytes);
        Assert.Equal(2, stats.Megabytes);
        Assert.NotNull(stats.Oldest);
        Assert.True(stats.Oldest!.Value < DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
    }

    [Fact]
    public void Describe_GdyKataloguNieMa_ZwracaPustyWynik()
    {
        using var dir = new TempDir("captures");
        var store = new FileCaptureStore(Path.Combine(dir.Path, "nie-ma-takiego"));

        Assert.Equal(CaptureFolderStats.Empty, store.Describe());
    }
}
