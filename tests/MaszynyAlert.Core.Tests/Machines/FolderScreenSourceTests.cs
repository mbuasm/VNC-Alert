using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class FolderScreenSourceTests
{
    private static RgbaImage Frame(int w = 60, int h = 40)
    {
        var img = new RgbaImage(w, h);
        img.Fill(new Rgba(200, 200, 200));
        img.SetPixel(5, 5, new Rgba(220, 30, 30));
        return img;
    }

    [Fact]
    public void FilePath_SkladaSieZKataloguIIdentyfikatoraMaszyny()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);

        Assert.Equal(Path.Combine(dir.Path, "aidite1.png"), source.FilePath);
        Assert.Equal("aidite1", source.MachineId);
    }

    [Fact]
    public void FileExists_GdyPlikuNieMa_ZwracaFalsz()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);

        Assert.False(source.FileExists);
        Assert.False(source.IsConnected);
        Assert.Null(source.Info);
    }

    [Fact]
    public async Task ConnectAsync_GdyPlikIstnieje_ZwracaOpisZrodlaTypuPlik()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);
        PngCodec.EncodeToFile(Frame(), source.FilePath);

        ScreenSourceInfo info = await source.ConnectAsync(CancellationToken.None);

        Assert.True(source.FileExists);
        Assert.True(source.IsConnected);
        Assert.Equal("plik", info.Kind);
        Assert.Equal(60, info.Width);
        Assert.Equal(40, info.Height);
        Assert.Equal("aidite1.png", info.DesktopName);
        Assert.Null(info.ProtocolVersion);
    }

    [Fact]
    public async Task CaptureAsync_PrzycinaObrazDoWskazanegoObszaru()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);
        PngCodec.EncodeToFile(Frame(), source.FilePath);

        RgbaImage roi = await source.CaptureAsync(new RectInt(0, 0, 20, 10), incremental: true, CancellationToken.None);

        Assert.Equal(20, roi.Width);
        Assert.Equal(10, roi.Height);
        Assert.Equal(new Rgba(220, 30, 30), roi.GetPixel(5, 5));
    }

    [Fact]
    public async Task CaptureAsync_GdyObszarWystajePozaObraz_PrzycinaGoDoGranic()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);
        PngCodec.EncodeToFile(Frame(), source.FilePath);

        RgbaImage roi = await source.CaptureAsync(new RectInt(0, 0, 5000, 5000), incremental: false, CancellationToken.None);

        Assert.Equal(60, roi.Width);
        Assert.Equal(40, roi.Height);
    }

    [Fact]
    public async Task CaptureFullAsync_ZwracaCalyObraz()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);
        PngCodec.EncodeToFile(Frame(), source.FilePath);

        RgbaImage full = await source.CaptureFullAsync(CancellationToken.None);

        Assert.Equal(60, full.Width);
        Assert.Equal(40, full.Height);
    }

    [Fact]
    public async Task CaptureAsync_GdyPlikuNieMa_RzucaScreenSourceException()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);

        ScreenSourceException ex = await Assert.ThrowsAsync<ScreenSourceException>(
            () => source.CaptureAsync(new RectInt(0, 0, 10, 10), false, CancellationToken.None));

        Assert.Contains("nie istnieje", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(source.IsConnected);
    }

    [Fact]
    public async Task CaptureAsync_GdyPlikJestUszkodzony_RzucaScreenSourceException()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);
        File.WriteAllBytes(source.FilePath, "to nie jest PNG"u8.ToArray());

        ScreenSourceException ex = await Assert.ThrowsAsync<ScreenSourceException>(
            () => source.CaptureAsync(new RectInt(0, 0, 10, 10), false, CancellationToken.None));

        Assert.IsType<PngFormatException>(ex.InnerException);
        Assert.False(source.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_RozlaczaZrodlo()
    {
        using var dir = new TempDir("test-ekran");
        var source = new FolderScreenSource("aidite1", dir.Path);
        PngCodec.EncodeToFile(Frame(), source.FilePath);
        await source.ConnectAsync(CancellationToken.None);

        await source.DisposeAsync();

        Assert.False(source.IsConnected);
    }
}
