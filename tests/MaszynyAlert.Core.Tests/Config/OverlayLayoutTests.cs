using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class OverlayLayoutTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vnc-alert-overlay-layout").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AppConfig ConfigWith(string overlayJson) => ConfigLoader.ParseAppConfig($$"""
        {
          "machines": [],
          "profiles": { "opencnc": { "statusBarRoi": { "x": 0, "y": 0, "width": 10, "height": 10 } } },
          "ui": { "overlay": {{overlayJson}} }
        }
        """);

    [Fact]
    public void DomyslnyUkladToJedenWierszJakDotad()
    {
        Assert.Equal(OverlayLayout.OneRow, new OverlayConfig().Layout);
        Assert.Equal(OverlayLayout.OneRow, new UiConfig().Overlay.Layout);
        Assert.Equal(OverlayLayout.OneRow, TestPaths.ExampleConfig.Ui.Overlay.Layout);
    }

    [Fact]
    public void PrzykladowaKonfiguracjaOpisujeUklad()
    {
        // Administrator ma zobaczyć klucz i dozwolone nazwy w config.example.json, mimo że wartość domyślna
        // nie wymaga wpisu.
        string example = File.ReadAllText(TestPaths.ConfigExample);

        Assert.Contains("\"layout\"", example, StringComparison.Ordinal);
        foreach (OverlayLayout layout in OverlayLayouts.All)
        {
            Assert.Contains(OverlayLayouts.Name(layout), example, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("one-row", OverlayLayout.OneRow)]
    [InlineData("two-rows", OverlayLayout.TwoRows)]
    [InlineData("one-column", OverlayLayout.OneColumn)]
    [InlineData("two-columns", OverlayLayout.TwoColumns)]
    [InlineData("three-columns", OverlayLayout.ThreeColumns)]
    public void UkladCzytaSieIZapisujeZMyslnikiem(string text, OverlayLayout expected)
    {
        AppConfig cfg = ConfigWith($$"""{ "layout": "{{text}}" }""");

        Assert.Equal(expected, cfg.Ui.Overlay.Layout);
        Assert.Equal(text, OverlayLayouts.Name(expected));
        Assert.True(OverlayLayouts.TryParse(text, out OverlayLayout parsed));
        Assert.Equal(expected, parsed);
        if (expected != OverlayLayout.OneRow)
        {
            Assert.Contains($"\"layout\": \"{text}\"", ConfigLoader.Serialize(cfg), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DomyslnyUkladNieJestZapisywanyDoPliku()
    {
        // Dlaczego: sekcja ui.overlay jest replikowana w grupie jako całość. Gdyby "layout": "one-row" trafiał
        // do JSON-a, odcisk dokumentu wspólnej konfiguracji różniłby się między komputerem z tą wersją
        // a komputerem ze starszą (która pola nie zna) i para odsyłałaby sobie ten sam dokument co 10 s.
        AppConfig cfg = ConfigWith("{ }");

        Assert.DoesNotContain("layout", ConfigLoader.Serialize(cfg), StringComparison.Ordinal);
    }

    [Fact]
    public void NieznanaNazwaUkladuWPlikuJestBledemKonfiguracji()
    {
        ConfigException error = Assert.Throws<ConfigException>(() => ConfigWith("""{ "layout": "spirala" }"""));

        Assert.Contains("JSON", error.Message, StringComparison.Ordinal);
        Assert.False(OverlayLayouts.TryParse("spirala", out _));
        Assert.False(OverlayLayouts.TryParse("", out _));
    }

    [Fact]
    public void NieznanaWartoscUkladuJestOdrzucanaPrzyWalidacji()
    {
        AppConfig cfg = TestPaths.ExampleConfig with
        {
            Ui = new UiConfig { Overlay = new OverlayConfig { Layout = (OverlayLayout)99 } },
        };

        ConfigException error = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains("ui.overlay.layout", error.Message, StringComparison.Ordinal);
        Assert.Contains("two-rows", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KazdyUkladMaNazweOpisIKlucz()
    {
        Assert.Equal(5, OverlayLayouts.All.Count);
        Assert.Equal(Enum.GetValues<OverlayLayout>().Order(), OverlayLayouts.All.Order());
        Assert.Equal(OverlayLayouts.All.Count, OverlayLayouts.All.Select(OverlayLayouts.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(OverlayLayouts.All.Count, OverlayLayouts.All.Select(OverlayLayouts.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (OverlayLayout layout in OverlayLayouts.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(OverlayLayouts.Label(layout)));
            Assert.Matches("^[a-z]+(-[a-z]+)*$", OverlayLayouts.Name(layout));
        }
    }

    [Theory]
    [InlineData(OverlayLayout.TwoRows, 6, 2, 3)]
    [InlineData(OverlayLayout.TwoRows, 7, 2, 4)]
    [InlineData(OverlayLayout.TwoRows, 9, 2, 5)]
    [InlineData(OverlayLayout.TwoRows, 1, 2, 1)]
    [InlineData(OverlayLayout.OneRow, 9, 1, 9)]
    [InlineData(OverlayLayout.OneRow, 3, 1, 3)]
    [InlineData(OverlayLayout.OneColumn, 3, 3, 1)]
    [InlineData(OverlayLayout.OneColumn, 9, 9, 1)]
    [InlineData(OverlayLayout.TwoColumns, 9, 5, 2)]
    [InlineData(OverlayLayout.TwoColumns, 3, 2, 2)]
    [InlineData(OverlayLayout.ThreeColumns, 9, 3, 3)]
    [InlineData(OverlayLayout.ThreeColumns, 7, 3, 3)]
    [InlineData(OverlayLayout.ThreeColumns, 2, 1, 3)]
    public void SiatkaDajeTyleWierszyIKolumnIleTrzeba(OverlayLayout layout, int tiles, int rows, int columns)
    {
        (int Rows, int Columns) shape = OverlayLayouts.Shape(layout, tiles);

        Assert.Equal((rows, columns), shape);
        Assert.True(shape.Rows * shape.Columns >= tiles, "siatka musi pomieścić wszystkie kafelki");
    }

    [Fact]
    public void SiatkaBezKafelkowJestPusta()
    {
        foreach (OverlayLayout layout in OverlayLayouts.All)
        {
            (int rows, int columns) = OverlayLayouts.Shape(layout, 0);
            Assert.Equal(0, rows * columns);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayLayouts.Shape(OverlayLayout.OneRow, -1));
    }

    /// <summary>
    /// Stały wymiar siatki WPF (<c>UniformGrid</c>): wiersze dla układów poziomych, kolumny dla pionowych;
    /// zero znaczy „policz z liczby kafelków”.
    /// </summary>
    [Theory]
    [InlineData(OverlayLayout.OneRow, 1, 0)]
    [InlineData(OverlayLayout.TwoRows, 2, 0)]
    [InlineData(OverlayLayout.OneColumn, 0, 1)]
    [InlineData(OverlayLayout.TwoColumns, 0, 2)]
    [InlineData(OverlayLayout.ThreeColumns, 0, 3)]
    public void StalyWymiarSiatki(OverlayLayout layout, int rows, int columns)
    {
        Assert.Equal((rows, columns), OverlayLayouts.FixedGrid(layout));
    }

    [Fact]
    public void WyborOperatoraWygrywaDopokiKonfiguracjaSieNieZmienia()
    {
        var jedenWiersz = new OverlayConfig { Layout = OverlayLayout.OneRow };
        var trzyKolumny = new OverlayConfig { Layout = OverlayLayout.ThreeColumns };

        Assert.Equal(OverlayLayout.OneRow, new OverlaySettings().ResolveLayout(jedenWiersz));
        Assert.Equal(OverlayLayout.ThreeColumns, new OverlaySettings().ResolveLayout(trzyKolumny));

        OverlaySettings settings = new OverlaySettings().WithLayout(OverlayLayout.TwoRows, jedenWiersz);
        Assert.Equal(OverlayLayout.TwoRows, settings.ResolveLayout(jedenWiersz));
        // Administrator zmienił ui.overlay.layout (config.json albo Panel) — wygrywa nowe ustawienie stanowiska.
        Assert.Equal(OverlayLayout.ThreeColumns, settings.ResolveLayout(trzyKolumny));
    }

    [Fact]
    public void ZapisUkladuNieRuszaPozostalychPol()
    {
        var settings = new OverlaySettings { Enabled = true, EnabledDefault = false, Left = 120, Top = 40 };

        OverlaySettings updated = settings.WithLayout(OverlayLayout.OneColumn, new OverlayConfig());

        Assert.Equal(OverlayLayout.OneColumn, updated.Layout);
        Assert.Equal(OverlayLayout.OneRow, updated.LayoutDefault);
        Assert.True(updated.Enabled);
        Assert.False(updated.EnabledDefault);
        Assert.Equal(120, updated.Left);
        Assert.Equal(40, updated.Top);
    }

    [Fact]
    public void PlikOperatoraZapamietujeUkladZMyslnikiem()
    {
        var store = new OverlaySettingsStore(Path.Combine(_directory, "overlay.json"));

        Assert.True(store.Save(new OverlaySettings().WithLayout(OverlayLayout.TwoRows, new OverlayConfig())));

        Assert.Contains("\"layout\": \"two-rows\"", File.ReadAllText(store.Path), StringComparison.Ordinal);
        OverlaySettings loaded = store.Load();
        Assert.Equal(OverlayLayout.TwoRows, loaded.Layout);
        Assert.Equal(OverlayLayout.OneRow, loaded.LayoutDefault);
    }

    [Fact]
    public void StaryPlikOperatoraBezUkladuDajeUkladZeStanowiska()
    {
        var store = new OverlaySettingsStore(Path.Combine(_directory, "overlay.json"));
        File.WriteAllText(store.Path, """{ "enabled": true, "enabledDefault": true, "left": 10, "top": 20 }""");

        OverlaySettings loaded = store.Load();

        Assert.Null(loaded.Layout);
        Assert.Equal(OverlayLayout.TwoColumns, loaded.ResolveLayout(new OverlayConfig { Layout = OverlayLayout.TwoColumns }));
        Assert.True(loaded.HasPlacement);
    }

    [Fact]
    public void DokumentWspolnejKonfiguracjiNiesieUkladIZmieniaOdcisk()
    {
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        SharedConfigDocument Document(OverlayLayout layout) => new()
        {
            Overlay = new OverlayConfig { Enabled = true, Layout = layout }, EditedAt = at, EditedBy = "PC-A",
        };

        string jsonDefault = JsonSerializer.Serialize(Document(OverlayLayout.OneRow), SharedConfigDocument.CanonicalJsonOptions);
        string jsonTwoRows = JsonSerializer.Serialize(Document(OverlayLayout.TwoRows), SharedConfigDocument.CanonicalJsonOptions);

        // Domyślny układ: dokument wygląda dokładnie jak przed tą zmianą (ten sam odcisk u starszej wersji).
        Assert.DoesNotContain("layout", jsonDefault, StringComparison.Ordinal);
        Assert.Contains("\"layout\":\"two-rows\"", jsonTwoRows, StringComparison.Ordinal);
        Assert.NotEqual(Document(OverlayLayout.OneRow).Hash, Document(OverlayLayout.TwoRows).Hash);
        Assert.True(Document(OverlayLayout.TwoRows).IsValid());
        Assert.False((Document(OverlayLayout.TwoRows) with { Overlay = new OverlayConfig { Layout = (OverlayLayout)7 } }).IsValid());

        SharedConfigDocument? roundTrip = JsonSerializer.Deserialize<SharedConfigDocument>(jsonTwoRows, SharedConfigDocument.CanonicalJsonOptions);
        Assert.Equal(OverlayLayout.TwoRows, roundTrip!.Overlay!.Layout);
    }


    [Theory]
    [InlineData(1000, 1.0, 6)] // obszar roboczy ekranu 1024×768
    [InlineData(2560, 2.0, 8)]
    [InlineData(3440, 1.0, 24)]
    [InlineData(100, 1.0, 1)] // nigdy zero kolumn
    public void MaxColumns_IleKafelkowMiesciSieWSzerokosciObszaruRoboczego(double workAreaWidth, double scale, int expected)
    {
        Assert.Equal(expected, OverlayLayouts.MaxColumns(workAreaWidth, scale));
    }

    [Theory]
    [InlineData(OverlayLayout.OneRow, 7, 6, 0, 6)] // 7 nie mieści się w wierszu → 2 wiersze po 6
    [InlineData(OverlayLayout.OneRow, 6, 6, 1, 0)] // mieści się → bez zmian
    [InlineData(OverlayLayout.TwoRows, 12, 4, 0, 4)] // 2×6 za szerokie → 3×4
    [InlineData(OverlayLayout.TwoRows, 6, 6, 2, 0)]
    [InlineData(OverlayLayout.ThreeColumns, 9, 2, 0, 2)] // nawet 3 kolumny za szerokie → 2
    [InlineData(OverlayLayout.OneColumn, 9, 6, 0, 1)]
    public void FitGrid_UkladZawijaGdyNieMiesciSieNaEkranie(OverlayLayout layout, int tiles, int maxColumns, int rows, int columns)
    {
        Assert.Equal((rows, columns), OverlayLayouts.FitGrid(layout, tiles, maxColumns));
    }
}
