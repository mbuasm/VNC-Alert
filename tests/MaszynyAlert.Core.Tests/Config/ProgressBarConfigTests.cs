using System.Text;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ProgressBarConfigTests
{
    private static readonly ProgressBarSource Bar = new()
    {
        TrackRoi = new RectInt(422, 277, 350, 10),
        Fill = new HsvRange { HueMinDeg = 140, HueMaxDeg = 215, MinSaturation = 0.55, MinValue = 0.35 },
        ColumnsPerPercent = 3.54,
        ColumnsOffset = 2.5,
    };

    private static AppConfig Config(IdleWatchProfile idle)
    {
        var profile = new ScreenProfile { Id = "p1", StatusBarRoi = new RectInt(0, 0, 100, 20), Idle = idle };
        return new AppConfig
        {
            Machines = [new MachineConfig { Id = "m1", Name = "M 1", Host = "10.0.0.1", Port = 5900, Profile = "p1" }],
            Profiles = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal) { ["p1"] = profile },
        };
    }

    private static IdleWatchProfile NumberIdle(ProgressBarSource? bar) => new()
    {
        Mode = ProgressMode.Number,
        ProgressRoi = new RectInt(420, 246, 370, 30),
        ProgressBar = bar,
    };

    private static ConfigException Invalid(ProgressBarSource bar) =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(NumberIdle(bar))));

    // --- config.json ------------------------------------------------------------------------------------------

    [Fact]
    public void ParseAppConfig_SekcjiProgressBar_WczytujeWszystkiePola()
    {
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
              "profiles": {
                "p1": {
                  "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
                  "idle": {
                    "mode": "number",
                    "progressRoi": { "x": 420, "y": 246, "width": 370, "height": 30 },
                    "progressBar": {
                      "enabled": true,
                      "trackRoi": { "x": 422, "y": 277, "width": 350, "height": 10 },
                      "fill": { "hueMinDeg": 140, "hueMaxDeg": 215, "minSaturation": 0.55, "minValue": 0.35 },
                      "trackMaxSaturation": 0.08,
                      "trackMinValue": 0.42,
                      "surroundMaxValue": 0.33,
                      "surroundRows": 4,
                      "columnFillMinFraction": 0.45,
                      "columnsPerPercent": 3.54,
                      "columnsOffset": 2.5
                    }
                  }
                }
              }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);
        ConfigLoader.Validate(cfg);

        ProgressBarSource bar = Assert.IsType<ProgressBarSource>(cfg.Profiles["p1"].Idle!.ProgressBar);
        Assert.True(bar.Enabled);
        Assert.Equal(new RectInt(422, 277, 350, 10), bar.TrackRoi);
        Assert.Equal(new HsvRange { HueMinDeg = 140, HueMaxDeg = 215, MinSaturation = 0.55, MinValue = 0.35 }, bar.Fill);
        Assert.Equal(0.08, bar.TrackMaxSaturation);
        Assert.Equal(0.42, bar.TrackMinValue);
        Assert.Equal(0.33, bar.SurroundMaxValue);
        Assert.Equal(4, bar.SurroundRows);
        Assert.Equal(0.45, bar.ColumnFillMinFraction);
        Assert.Equal(3.54, bar.ColumnsPerPercent);
        Assert.Equal(2.5, bar.ColumnsOffset);
    }

    [Fact]
    public void ParseAppConfig_BezSekcjiProgressBar_ZostawiaNull_IDomyslneWartosciSaOstrozne()
    {
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
                "idle": { "progressRoi": { "x": 420, "y": 246, "width": 370, "height": 30 } } } }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);
        ConfigLoader.Validate(cfg);

        Assert.Null(cfg.Profiles["p1"].Idle!.ProgressBar);

        var defaults = new ProgressBarSource();
        Assert.True(defaults.Enabled);
        Assert.True(defaults.TrackRoi.IsEmpty);
        Assert.Equal(0.10, defaults.TrackMaxSaturation);
        Assert.Equal(0.40, defaults.TrackMinValue);
        Assert.Equal(0.35, defaults.SurroundMaxValue);
        Assert.Equal(5, defaults.SurroundRows);
        Assert.Equal(0.5, defaults.ColumnFillMinFraction);
        Assert.Equal(0, defaults.ColumnsPerPercent);
        Assert.Equal(0, defaults.ColumnsOffset);
    }

    [Fact]
    public void Validate_PasekPrzyTrybieBar_RzucaConfigExceptionZNazwaPolaITrybu()
    {
        // Dlaczego: tryb "bar" ma własny detektor („pełny / niepełny”, bez procentu) — sekcja paska jako
        // źródła procentu nic by w nim nie robiła, a konfiguracja udawałaby, że działa.
        AppConfig cfg = Config(NumberIdle(Bar) with { Mode = ProgressMode.Bar });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("p1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("idle.progressBar", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"number\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WylaczonyPasekAlboWylaczonaSekcjaIdle_NieSprawdzaPaska()
    {
        // Dlaczego: "enabled": false ma być równoważne brakowi sekcji — niedokończony pasek nie może
        // blokować startu programu (tak samo jak przy całej sekcji idle).
        ConfigLoader.Validate(Config(NumberIdle(Bar with { Enabled = false, TrackRoi = RectInt.Empty }) with { Mode = ProgressMode.Bar }));
        ConfigLoader.Validate(Config(NumberIdle(Bar with { TrackRoi = RectInt.Empty }) with { Enabled = false }));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(422, 277, 9, 10)]
    [InlineData(422, 277, 350, 0)]
    public void Validate_PustyAlboZbytWaskiTor_RzucaConfigException(int x, int y, int width, int height)
    {
        // Dlaczego: na torze węższym niż 10 kolumn procent nie ma sensu (a zaokrąglone końce zjadają 6 kolumn).
        ConfigException ex = Invalid(Bar with { TrackRoi = new RectInt(x, y, width, height) });

        Assert.Contains("idle.progressBar.trackRoi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TorZOtoczeniemNadGornaKrawedziaEkranu_RzucaConfigException()
    {
        // Dlaczego: 5 wierszy otoczenia nad torem na y 3 wychodzi poza ekran — wycinek byłby zawsze za mały
        // i pasek po cichu nigdy nic by nie czytał.
        ConfigException ex = Invalid(Bar with { TrackRoi = new RectInt(10, 3, 100, 10) });

        Assert.Contains("idle.progressBar.trackRoi", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trackMaxSaturation", 0.0)]
    [InlineData("trackMaxSaturation", 1.5)]
    [InlineData("trackMinValue", -0.1)]
    [InlineData("surroundMaxValue", 0.0)]
    [InlineData("surroundMaxValue", double.NaN)]
    [InlineData("columnFillMinFraction", 0.0)]
    [InlineData("columnFillMinFraction", 1.01)]
    public void Validate_ProgPozaZakresem_RzucaConfigExceptionZNazwaPola(string field, double value)
    {
        // Dlaczego: próg 0 albo ponad 1 wyłącza (albo psuje) jeden z warunków widoczności toru — wtedy pasek
        // mógłby podać procent z czegoś, co paskiem nie jest.
        ProgressBarSource bar = field switch
        {
            "trackMaxSaturation" => Bar with { TrackMaxSaturation = value },
            "trackMinValue" => Bar with { TrackMinValue = value },
            "surroundMaxValue" => Bar with { SurroundMaxValue = value },
            "columnFillMinFraction" => Bar with { ColumnFillMinFraction = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        ConfigException ex = Invalid(bar);

        Assert.Contains($"idle.progressBar.{field}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(0, 1]", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    public void Validate_SurroundRowsPozaZakresem_RzucaConfigException(int rows)
    {
        ConfigException ex = Invalid(Bar with { SurroundRows = rows });

        Assert.Contains("idle.progressBar.surroundRows", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0..20", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_UjemnyAlboNieliczbowyModel_RzucaConfigException()
    {
        Assert.Contains("idle.progressBar.columnsPerPercent",
            Invalid(Bar with { ColumnsPerPercent = -3.54 }).Message, StringComparison.Ordinal);
        Assert.Contains("idle.progressBar.columnsOffset",
            Invalid(Bar with { ColumnsOffset = double.PositiveInfinity }).Message, StringComparison.Ordinal);

        // 0 = model domyślny (szerokość / 100) — dozwolone.
        ConfigLoader.Validate(Config(NumberIdle(Bar with { ColumnsPerPercent = 0, ColumnsOffset = 0 })));
    }

    [Fact]
    public void Validate_ZlaBarwaWypelnienia_RzucaConfigExceptionZNazwaPola()
    {
        ConfigException ex = Invalid(Bar with { Fill = Bar.Fill with { HueMaxDeg = 400 } });
        Assert.Contains("idle.progressBar.fill.hueMaxDeg", ex.Message, StringComparison.Ordinal);

        ConfigException missing = Invalid(Bar with { Fill = null! });
        Assert.Contains("idle.progressBar.fill", missing.Message, StringComparison.Ordinal);
    }

    // --- Plik grupy -------------------------------------------------------------------------------------------

    [Fact]
    public void PlikGrupy_ZPaskiem580_PrzechodziRoundTripZeWszystkimiPolami()
    {
        // Dlaczego: profil grupy nakłada profile ekranu przy każdym starcie — gdyby pasek ginął w drodze przez
        // plik grupy (albo plik z nim był odrzucany przez „nieznane pole”), grupa po cichu wracałaby do OCR.
        GroupFile source = GroupFileTests.Invitation();
        Assert.NotNull(source.Profile.Profiles["amm580"].Idle!.ProgressBar);

        GroupFile copy = GroupFileCodec.Parse(GroupFileCodec.Serialize(source));

        Assert.Equal(source.Profile.Profiles["amm580"].Idle!.ProgressBar, copy.Profile.Profiles["amm580"].Idle!.ProgressBar);
        Assert.Equal(source.Identity.ProfileHash, copy.Identity.ProfileHash);
    }

    [Fact]
    public void PlikGrupyZeStarszejWersji_BezSekcjiProgressBar_WczytujeSie()
    {
        JsonNode json = JsonNode.Parse(GroupFileCodec.Serialize(GroupFileTests.Invitation()))!;
        Assert.True(json["profile"]!["profiles"]!["amm580"]!["idle"]!.AsObject().Remove("progressBar"));

        GroupFile file = GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Null(file.Profile.Profiles["amm580"].Idle!.ProgressBar);
        Assert.Equal(GroupFileCodec.ProfileHash(file.Profile), file.Identity.ProfileHash);
    }

    [Fact]
    public void OdciskProfiluBezPaska_NieZmieniaSieWzgledemWersjiSprzedPaska()
    {
        GroupProfile original = GroupFileTests.Invitation().Profile;
        var withoutBar = original with
        {
            Profiles = original.Profiles.ToDictionary(
                p => p.Key,
                p => p.Value.Idle is null ? p.Value : p.Value with { Idle = p.Value.Idle with { ProgressBar = null } },
                StringComparer.Ordinal),
        };

        string json = Encoding.UTF8.GetString(GroupFileCodec.Serialize(GroupFile.Create("Hala", withoutBar)));
        Assert.DoesNotContain("progressBar", json, StringComparison.Ordinal);

        JsonNode profile = JsonNode.Parse(json)!["profile"]!;
        Assert.Equal(GroupFileTests.OlderVersionProfileHash(profile), GroupFileCodec.ProfileHash(withoutBar));

        // Kontrola: pasek W PROFILU zmienia odcisk — to jest ustawienie wykrywania i ma rozstrzygać zgodność.
        Assert.NotEqual(GroupFileCodec.ProfileHash(withoutBar), GroupFileCodec.ProfileHash(original));
    }

    [Theory]
    [InlineData("progressBar.trackRoi.x", "-1")]
    [InlineData("progressBar.fill", "null")]
    [InlineData("progressBar.columnFillMinFraction", "0")]
    [InlineData("progressBar.surroundRows", "99")]
    [InlineData("progressBar.unknownField", "1")]
    [InlineData("mode", "\"bar\"")]
    public void PlikGrupyZZepsutymPaskiem_JestOdrzucany(string field, string value)
    {
        // Dlaczego: plik grupy przychodzi z innego komputera — pasek z zepsutym obszarem, progiem albo trybem
        // ma być odrzucony tak samo jak zepsute pole OCR, bez cytowania treści pliku.
        JsonNode json = JsonNode.Parse(GroupFileCodec.Serialize(GroupFileTests.Invitation()))!;
        JsonNode parent = json["profile"]!["profiles"]!["amm580"]!["idle"]!;
        string[] parts = field.Split('.');
        foreach (string part in parts[..^1])
        {
            parent = parent[part]!;
        }

        parent[parts[^1]] = JsonNode.Parse(value);

        Assert.Throws<InvalidDataException>(() => GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
}
