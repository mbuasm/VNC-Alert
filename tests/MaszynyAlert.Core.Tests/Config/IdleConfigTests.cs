using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

/// <summary>
/// Sekcja <c>idle</c> profilu ekranu i powiązane pola <c>timing</c> (alert „maszyna stoi bez pracy”):
/// wczytywanie z JSON-a oraz walidacja z komunikatami po polsku wskazującymi profil i pole do poprawy.
/// </summary>
public sealed class IdleConfigTests
{
    private static ScreenProfile Profile(IdleWatchProfile? idle, string id = "p1") => new()
    {
        Id = id,
        StatusBarRoi = new RectInt(0, 0, 100, 20),
        Idle = idle,
    };

    private static MachineConfig Machine(string id = "m1", string profile = "p1") => new()
    {
        Id = id,
        Name = "Maszyna " + id,
        Host = "10.0.0.1",
        Port = 5900,
        Profile = profile,
    };

    /// <summary>Poprawna sekcja „idle” w trybie liczbowym — punkt wyjścia dla testów walidacji pól OCR.</summary>
    private static readonly IdleWatchProfile NumberIdle = new()
    {
        Mode = ProgressMode.Number,
        ProgressRoi = new RectInt(440, 380, 120, 24),
    };

    /// <summary>Minimalny plik konfiguracyjny z sekcją „idle”, do którego wstawiamy <paramref name="mode"/> (albo nic).</summary>
    private static string JsonZTrybem(string mode) => $$"""
        {
          "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
          "profiles": {
            "p1": {
              "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
              "idle": {
                "enabled": true,
                {{mode}}
                "progressRoi": { "x": 440, "y": 380, "width": 120, "height": 24 }
              }
            }
          }
        }
        """;

    private static AppConfig Config(ScreenProfile profile, TimingConfig? timing = null) => new()
    {
        Machines = [Machine(profile: profile.Id)],
        Profiles = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal) { [profile.Id] = profile },
        Timing = timing ?? new TimingConfig(),
    };

    [Fact]
    public void ParseAppConfig_SekcjiIdle_WczytujeRoiKolorIProgWypelnienia()
    {
        // Dlaczego: nazwy pól w pliku są camelCase, a ROI ma własny konwerter — literówka w którejkolwiek
        // z tych rzeczy oznaczałaby po cichu wyłączoną (albo źle ustawioną) obserwację postoju.
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
              "profiles": {
                "p1": {
                  "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
                  "idle": {
                    "enabled": true,
                    "progressRoi": { "x": 60, "y": 380, "width": 400, "height": 24 },
                    "progressFill": { "hueMinDeg": 90, "hueMaxDeg": 150, "minSaturation": 0.4, "minValue": 0.45 },
                    "fullAtFraction": 0.95
                  }
                }
              }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        IdleWatchProfile idle = Assert.IsType<IdleWatchProfile>(cfg.Profiles["p1"].Idle);
        Assert.True(idle.Enabled);
        Assert.Equal(new RectInt(60, 380, 400, 24), idle.ProgressRoi);
        Assert.Equal(90, idle.ProgressFill.HueMinDeg);
        Assert.Equal(150, idle.ProgressFill.HueMaxDeg);
        Assert.Equal(0.4, idle.ProgressFill.MinSaturation);
        Assert.Equal(0.45, idle.ProgressFill.MinValue);
        Assert.Equal(0.95, idle.FullAtFraction);
    }

    [Fact]
    public void ParseAppConfig_ProfiluBezSekcjiIdle_ZostawiaPoleNull()
    {
        // Dlaczego: brak sekcji ma znaczyć „funkcja nieskonfigurowana”, a nie „domyślnie włączona z pustym
        // ROI” — inaczej stary plik konfiguracyjny przestałby przechodzić walidację.
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 } } }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        Assert.Null(cfg.Profiles["p1"].Idle);
        ConfigLoader.Validate(cfg);
    }

    [Fact]
    public void Validate_GdyProgressRoiJestPuste_RzucaConfigExceptionZNazwaProfiluIPola()
    {
        // Dlaczego: komunikat czyta osoba nietechniczna — musi wiedzieć, KTÓRY profil i KTÓRE pole poprawić.
        AppConfig cfg = Config(Profile(new IdleWatchProfile { ProgressRoi = RectInt.Empty }, "opencnc"));

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("opencnc", ex.Message, StringComparison.Ordinal);
        Assert.Contains("idle.progressRoi", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void Validate_GdyFullAtFractionPozaZakresem_RzucaConfigExceptionZNazwaPola(double fullAtFraction)
    {
        // Dlaczego: próg spoza (0, 1] nie ma sensu — 0 dawałoby alert zawsze, a >1 nigdy (cichy brak funkcji).
        // Profil MUSI być jawnie w trybie "bar": w domyślnym trybie "number" postęp jest liczbą z OCR,
        // więc fullAtFraction w ogóle nie jest używane (i celowo nie jest walidowane).
        AppConfig cfg = Config(Profile(new IdleWatchProfile
        {
            Mode = ProgressMode.Bar,
            ProgressRoi = new RectInt(10, 10, 100, 10),
            FullAtFraction = fullAtFraction,
        }));

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("idle.fullAtFraction", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdySekcjaIdleJestWylaczona_NieSprawdzaJejPol()
    {
        // Dlaczego: „enabled: false” ma być równoważne brakowi sekcji — niedokończone ROI nie może wtedy
        // blokować startu programu.
        AppConfig cfg = Config(Profile(new IdleWatchProfile
        {
            Enabled = false,
            ProgressRoi = RectInt.Empty,
            FullAtFraction = 0,
        }));

        ConfigLoader.Validate(cfg);
    }

    [Fact]
    public void Validate_GdyIdleAlertAfterMsPonizejDziesieciuSekund_RzucaConfigException()
    {
        // Dlaczego: zbyt krótki próg dzwoniłby przy każdej zwykłej zmianie pracy (maszyna stoi na 100 %
        // około minuty) — dolna granica chroni przed taką konfiguracją przez pomyłkę.
        AppConfig cfg = Config(Profile(null), new TimingConfig { IdleAlertAfterMs = 5000 });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("timing.idleAlertAfterMs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("10000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyIdleAlertAfterMsRownyDziesiecSekund_JestDozwolony()
    {
        // Dlaczego: granica ma być domknięta (≥ 10000 ms).
        ConfigLoader.Validate(Config(Profile(null), new TimingConfig { IdleAlertAfterMs = 10000 }));
    }

    [Fact]
    public void Validate_GdyIdleAtZeroAfterMsPonizejTrzydziestuSekund_RzucaConfigException()
    {
        AppConfig cfg = Config(Profile(null), new TimingConfig { IdleAtZeroAfterMs = 20000 });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("timing.idleAtZeroAfterMs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("30000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyIdleAtZeroAfterMsRownyTrzydziestuSekundom_JestDozwolony()
    {
        // Dlaczego: granica ma być domknięta (≥ 30000 ms), tak jak przy idleAlertAfterMs.
        ConfigLoader.Validate(Config(Profile(null), new TimingConfig { IdleAtZeroAfterMs = 30000 }));
    }

    [Fact]
    public void TimingConfig_DomyslnyIdleAtZeroAfterMs_ToTrzyMinuty()
    {
        Assert.Equal(180000, new TimingConfig().IdleAtZeroAfterMs);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(-5)]
    public void Validate_GdyIdlePopupSecondsPozaZakresem_RzucaConfigException(int seconds)
    {
        // Dlaczego: ponad minuta widocznego pop-upu to już nie przypomnienie, a wartość ujemna nie znaczy nic.
        AppConfig cfg = Config(Profile(null), new TimingConfig { IdlePopupSeconds = seconds });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("timing.idlePopupSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0..60", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60)]
    public void Validate_GdyIdlePopupSecondsNaGranicyZakresu_JestDozwolony(int seconds)
    {
        ConfigLoader.Validate(Config(Profile(null), new TimingConfig { IdlePopupSeconds = seconds }));
    }

    [Fact]
    public void TimingConfig_DomyslnieNieChowaPopupuPostoju()
    {
        Assert.Equal(0, new TimingConfig().IdlePopupSeconds);
    }

    [Theory]
    [InlineData("number", ProgressMode.Number)]
    [InlineData("bar", ProgressMode.Bar)]
    public void ParseAppConfig_PolaMode_WczytujeTrybOdczytuPostepu(string json, ProgressMode oczekiwany)
    {
        // Dlaczego: tryb decyduje o tym, CZY w ogóle uruchamiamy OCR postępu — literówka w nazwie
        // („Number” zamiast „number”) nie może po cichu wrócić do trybu domyślnego.
        AppConfig cfg = ConfigLoader.ParseAppConfig(JsonZTrybem($"\"mode\": \"{json}\","));

        Assert.Equal(oczekiwany, Assert.IsType<IdleWatchProfile>(cfg.Profiles["p1"].Idle).Mode);
    }

    [Fact]
    public void ParseAppConfig_BezPolaMode_DajeTrybNumber()
    {
        AppConfig cfg = ConfigLoader.ParseAppConfig(JsonZTrybem(string.Empty));

        IdleWatchProfile idle = Assert.IsType<IdleWatchProfile>(cfg.Profiles["p1"].Idle);
        Assert.Equal(ProgressMode.Number, idle.Mode);
        Assert.Equal(100, idle.FullAtPercent);
        Assert.Equal(3, idle.OcrScale);
        Assert.Equal(8, idle.OcrMarginPx);
        Assert.Null(idle.InvertForOcr);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_GdyFullAtPercentPozaZakresem_RzucaConfigExceptionZNazwaPola(int fullAtPercent)
    {
        // Dlaczego: próg spoza 1..100 nie ma sensu — 0 dawałoby alert przy każdym odczycie (także „0%”
        // świeżo startującej pracy), a ponad 100 nigdy (cichy brak funkcji).
        AppConfig cfg = Config(Profile(NumberIdle with { FullAtPercent = fullAtPercent }));

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("idle.fullAtPercent", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Validate_GdyOcrScalePozaZakresem_RzucaConfigExceptionZNazwaPola(int ocrScale)
    {
        // Dlaczego: skala 0 dałaby obraz zerowy (nic do odczytania), a bardzo duża — obraz liczony
        // w megapikselach co cykl razy 12 maszyn; zakres 1..8 z kontraktu ma być pilnowany.
        AppConfig cfg = Config(Profile(NumberIdle with { OcrScale = ocrScale }));

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("idle.ocrScale", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_GdyOcrMarginPxPozaZakresem_RzucaConfigExceptionZNazwaPola(int ocrMarginPx)
    {
        // Dlaczego: ujemny margines to błąd (obraz mniejszy od wycinka), a stupikselowy — biała ramka
        // wielokrotnie większa niż sama liczba procent.
        AppConfig cfg = Config(Profile(NumberIdle with { OcrMarginPx = ocrMarginPx }));

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("idle.ocrMarginPx", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WTrybieNumber_NieSprawdzaPolTrybuBar()
    {
        // Dlaczego: fullAtFraction dotyczy WYŁĄCZNIE trybu paska — w trybie liczbowym jest nieużywane,
        // więc jego wartość (nawet bezsensowna) nie może blokować startu programu.
        ConfigLoader.Validate(Config(Profile(NumberIdle with { FullAtFraction = 0 })));
    }

    [Fact]
    public void PrzykladowaKonfiguracja_MaWszystkieProfileWTrybieLiczbowymZNiepustymObszaremPostepu()
    {
        // Dlaczego: HMI pokazuje postęp jako liczbę procent — przykładowa konfiguracja (wzorzec dla
        // config.json użytkownika) musi być w trybie „number” we WSZYSTKICH profilach i mieć
        // wskazany obszar tej liczby.
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        Assert.Equal(6, cfg.Profiles.Count);
        foreach ((string id, ScreenProfile profile) in cfg.Profiles)
        {
            IdleWatchProfile idle = Assert.IsType<IdleWatchProfile>(profile.Idle);
            Assert.Equal(ProgressMode.Number, idle.Mode);
            Assert.False(idle.ProgressRoi.IsEmpty, $"Profil '{id}' ma puste 'idle.progressRoi'.");
            Assert.Equal(100, idle.FullAtPercent);
            Assert.InRange(idle.OcrScale, 1, 8);
            Assert.InRange(idle.OcrMarginPx, 0, 100);
        }
    }

    [Fact]
    public void PrzykladowaKonfiguracja_MaWlaczonaObserwacjePostojuWKazdymProfiluIPrzechodziWalidacje()
    {
        // Dlaczego: config.example.json jest wzorcem, z którego użytkownik tworzy swój config.json —
        // musi się wczytywać, przechodzić walidację i mieć funkcję włączoną we WSZYSTKICH profilach.
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);
        ConfigLoader.Validate(cfg);

        Assert.Equal(6, cfg.Profiles.Count);
        foreach ((string id, ScreenProfile profile) in cfg.Profiles)
        {
            IdleWatchProfile idle = Assert.IsType<IdleWatchProfile>(profile.Idle);
            Assert.True(idle.Enabled, $"Profil '{id}' powinien mieć włączoną obserwację postoju.");
            Assert.False(idle.ProgressRoi.IsEmpty, $"Profil '{id}' ma puste 'idle.progressRoi'.");
            Assert.InRange(idle.FullAtFraction, 0.9, 1.0);
        }

        Assert.Equal(120000, cfg.Timing.IdleAlertAfterMs);
        Assert.Equal(0, cfg.Timing.IdlePopupSeconds);
    }
}
