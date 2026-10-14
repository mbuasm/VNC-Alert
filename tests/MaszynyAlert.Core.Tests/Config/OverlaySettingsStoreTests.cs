using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class OverlaySettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vnc-alert-overlay").FullName;

    private OverlaySettingsStore Store(string name = "overlay.json") =>
        new(Path.Combine(_directory, name));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void BrakPlikuDajePusteUstawienia()
    {
        OverlaySettings settings = Store().Load();

        Assert.Null(settings.Enabled);
        Assert.Null(settings.ClickThrough);
        Assert.False(settings.HasPlacement);
    }

    [Fact]
    public void ZapisIOdczytZachowujaWyborOperatora()
    {
        OverlaySettingsStore store = Store();

        Assert.True(store.Save(new OverlaySettings
        {
            Enabled = true,
            ClickThrough = true,
            Left = 240.5,
            Top = 12,
        }));

        OverlaySettings read = Store().Load();
        Assert.True(read.Enabled);
        Assert.True(read.ClickThrough);
        Assert.Equal(240.5, read.Left);
        Assert.Equal(12, read.Top);
        Assert.True(read.HasPlacement);
    }

    [Fact]
    public void ZapisPamietaUstawienieStanowiskaZChwiliWyboru()
    {
        var configured = new OverlayConfig { Enabled = true, ClickThrough = false };
        OverlaySettingsStore store = Store();

        Assert.True(store.Save(new OverlaySettings()
            .WithEnabled(false, configured)
            .WithClickThrough(true, configured)));

        OverlaySettings read = Store().Load();
        Assert.False(read.Enabled);
        Assert.True(read.EnabledDefault);
        Assert.True(read.ClickThrough);
        Assert.False(read.ClickThroughDefault);
        // Ustawienie stanowiska się nie zmieniło, więc wybór operatora nadal obowiązuje.
        Assert.False(read.ResolveEnabled(configured));
        // Po zmianie ui.overlay.enabled w config.json wygrywa plik konfiguracyjny.
        Assert.False(read.ResolveEnabled(configured with { Enabled = false }));
    }

    [Fact]
    public void PlikZWersji020BezZapamietanegoUstawieniaNieBlokujeNakladki()
    {
        OverlaySettingsStore store = Store();
        File.WriteAllText(store.Path, """{ "enabled": false }""");

        OverlaySettings read = store.Load();

        Assert.False(read.Enabled);
        Assert.Null(read.EnabledDefault);
        Assert.True(read.ResolveEnabled(new OverlayConfig { Enabled = true }));
    }

    [Fact]
    public void KolejnyZapisNadpisujePoprzedni()
    {
        OverlaySettingsStore store = Store();
        store.Save(new OverlaySettings { Enabled = true });

        store.Save(new OverlaySettings { Enabled = false });

        Assert.False(Store().Load().Enabled);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void UszkodzonyPlikNiePrzewracaProgramu()
    {
        OverlaySettingsStore store = Store();
        File.WriteAllText(store.Path, "{ to nie jest JSON");

        OverlaySettings settings = store.Load();

        Assert.Null(settings.Enabled);
    }

    [Fact]
    public void PolozenieBezObuWspolrzednychNieJestUzywane()
    {
        OverlaySettingsStore store = Store();
        store.Save(new OverlaySettings { Left = 100 });

        OverlaySettings read = store.Load();

        Assert.Equal(100, read.Left);
        Assert.False(read.HasPlacement);
    }

    [Fact]
    public void ZapisTworzyBrakujacyKatalogProfilu()
    {
        var store = new OverlaySettingsStore(Path.Combine(_directory, "nowy", "overlay.json"));

        Assert.True(store.Save(new OverlaySettings { Enabled = true }));
        Assert.True(store.Load().Enabled);
    }
}
