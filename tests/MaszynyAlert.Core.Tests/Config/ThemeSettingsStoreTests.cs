using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ThemeSettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vnc-alert-theme").FullName;

    private ThemeSettingsStore Store(string name = "theme.json") => new(Path.Combine(_directory, name));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void BrakPlikuDajeJasnyMotyw()
    {
        ThemeSettings settings = Store().Load();

        Assert.Null(settings.Theme);
        Assert.Equal(AppTheme.Light, settings.Resolve());
    }

    [Fact]
    public void ZapisIOdczytZachowujaWyborCiemnegoMotywu()
    {
        ThemeSettingsStore store = Store();

        Assert.True(store.Save(new ThemeSettings { Theme = AppTheme.Dark }));

        ThemeSettings read = Store().Load();
        Assert.Equal(AppTheme.Dark, read.Theme);
        Assert.Equal(AppTheme.Dark, read.Resolve());
        // Nazwa motywu jest w pliku słownie, żeby dało się ją odczytać i poprawić ręcznie.
        Assert.Contains("\"Dark\"", File.ReadAllText(store.Path));
    }

    [Fact]
    public void PowrotDoJasnegoNadpisujePlik()
    {
        ThemeSettingsStore store = Store();
        store.Save(new ThemeSettings { Theme = AppTheme.Dark });

        Assert.True(store.Save(new ThemeSettings { Theme = AppTheme.Light }));

        Assert.Equal(AppTheme.Light, Store().Load().Resolve());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"theme\": ")]
    [InlineData("{ \"theme\": \"Fioletowy\" }")]
    [InlineData("[1, 2, 3]")]
    public void UszkodzonyAlboNieznanyPlikDajeJasnyMotyw(string content)
    {
        ThemeSettingsStore store = Store();
        File.WriteAllText(store.Path, content);

        Assert.Equal(AppTheme.Light, store.Load().Resolve());
    }

    [Fact]
    public void NazwaMotywuBezWzgleduNaWielkoscLiter()
    {
        ThemeSettingsStore store = Store();
        File.WriteAllText(store.Path, "{ \"theme\": \"dark\" }");

        Assert.Equal(AppTheme.Dark, store.Load().Resolve());
    }

    [Fact]
    public void NieudanyZapisNieRzucaWyjatku()
    {
        // Ścieżka, której katalogiem nadrzędnym jest zwykły plik — Directory.CreateDirectory musi się nie udać.
        string blocker = Path.Combine(_directory, "plik");
        File.WriteAllText(blocker, "x");
        var store = new ThemeSettingsStore(Path.Combine(blocker, "theme.json"));

        Assert.False(store.Save(new ThemeSettings { Theme = AppTheme.Dark }));
        Assert.Equal(AppTheme.Light, store.Load().Resolve());
    }
}
