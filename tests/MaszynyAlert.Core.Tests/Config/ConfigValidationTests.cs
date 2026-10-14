using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ConfigValidationTests
{
    private static ScreenProfile Profile(string id = "p1") => new()
    {
        Id = id,
        StatusBarRoi = new RectInt(0, 0, 100, 20),
    };

    private static MachineConfig Machine(string id, string profile = "p1", int port = 5900, string host = "10.0.0.1") => new()
    {
        Id = id,
        Name = "Maszyna " + id,
        Host = host,
        Port = port,
        Profile = profile,
    };

    private static AppConfig Config(IEnumerable<MachineConfig> machines, params ScreenProfile[] profiles) => new()
    {
        Machines = machines.ToList(),
        Profiles = profiles.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal),
    };

    [Fact]
    public void Validate_GdyIdentyfikatorMaszynySieDubluje_RzucaConfigExceptionZIdMaszyny()
    {
        AppConfig cfg = Config([Machine("aidite1"), Machine("aidite1")], Profile());

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("aidite1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("powtarza się", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyMaszynaWskazujeNieistniejacyProfil_RzucaConfigExceptionZIdMaszyny()
    {
        AppConfig cfg = Config([Machine("cameo1", profile: "nie-ma-takiego")], Profile());

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("cameo1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nie-ma-takiego", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Validate_GdyPortPozaZakresem_RzucaConfigExceptionZIdMaszyny(int port)
    {
        AppConfig cfg = Config([Machine("amm580", port: port)], Profile());

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("amm580", ex.Message, StringComparison.Ordinal);
        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_GdyHostPusty_RzucaConfigExceptionZIdMaszyny()
    {
        AppConfig cfg = Config([Machine("premile", host: "   ")], Profile());

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("premile", ex.Message, StringComparison.Ordinal);
        Assert.Contains("host", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_GdyIdMaszynyPuste_RzucaConfigException()
    {
        AppConfig cfg = Config([Machine(" ")], Profile());

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("bez identyfikatora", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(0)]
    [InlineData(-0.1)]
    public void Validate_GdyMinRedFractionPozaZakresem_RzucaConfigExceptionZIdProfilu(double minRedFraction)
    {
        ScreenProfile profile = Profile("opencnc") with { MinRedFraction = minRedFraction };
        AppConfig cfg = Config([Machine("aidite1", profile: "opencnc")], profile);

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("opencnc", ex.Message, StringComparison.Ordinal);
        Assert.Contains("minRedFraction", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyProfilMaPusteRoi_RzucaConfigExceptionZIdProfilu()
    {
        ScreenProfile profile = Profile("amm580") with { StatusBarRoi = RectInt.Empty };
        AppConfig cfg = Config([Machine("amm580", profile: "amm580")], profile);

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("amm580", ex.Message, StringComparison.Ordinal);
        Assert.Contains("statusBarRoi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_PoprawnaKonfiguracja_NieRzuca()
    {
        AppConfig cfg = Config([Machine("m1"), Machine("m2")], Profile());
        ConfigLoader.Validate(cfg);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_GdyPollIntervalMsPonizej100_RzucaConfigExceptionZNazwaPola(int pollIntervalMs)
    {
        AppConfig cfg = Config([Machine("m1")], Profile()) with { Timing = new TimingConfig { PollIntervalMs = pollIntervalMs } };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("pollIntervalMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyFullFrameIntervalMsPonizejPollIntervalMs_RzucaConfigException()
    {
        AppConfig cfg = Config([Machine("m1")], Profile()) with
        {
            Timing = new TimingConfig { PollIntervalMs = 1000, FullFrameIntervalMs = 500 },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("fullFrameIntervalMs", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(99)] // < 100 (minimum)
    [InlineData(30001)] // > ReadTimeoutMs domyślnego (30000)
    public void Validate_GdyIdleUpdateTimeoutMsPozaZakresem_RzucaConfigException(int idleUpdateTimeoutMs)
    {
        AppConfig cfg = Config([Machine("m1")], Profile()) with
        {
            Timing = new TimingConfig { IdleUpdateTimeoutMs = idleUpdateTimeoutMs },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("idleUpdateTimeoutMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyReadTimeoutMsPonizej1000_RzucaConfigException()
    {
        AppConfig cfg = Config([Machine("m1")], Profile()) with { Timing = new TimingConfig { ReadTimeoutMs = 999 } };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("readTimeoutMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyReconnectBackoffMsZawieraUjemnaWartosc_RzucaConfigException()
    {
        AppConfig cfg = Config([Machine("m1")], Profile()) with
        {
            Timing = new TimingConfig { ReconnectBackoffMs = [2000, -5, 15000] },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("reconnectBackoffMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRules_GdyIdentyfikatorReguySieDubluje_RzucaConfigException()
    {
        var rules = new RuleSet
        {
            Rules =
            [
                new Rule { Id = "dubel", ShortText = "A", Category = "inny" },
                new Rule { Id = "dubel", ShortText = "B", Category = "inny" },
            ],
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
        Assert.Contains("dubel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRules_GdyRegexJestNiepoprawny_RzucaConfigExceptionZIdReguly()
    {
        var rules = new RuleSet
        {
            Rules = [new Rule { Id = "zly-regex", Pattern = "(niedomkniety", ShortText = "A", Category = "inny" }],
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
        Assert.Contains("zly-regex", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRules_GdyRegulaWskazujeNieistniejacyProfil_RzucaConfigException()
    {
        AppConfig cfg = Config([Machine("m1")], Profile());
        var rules = new RuleSet
        {
            Rules = [new Rule { Id = "r1", Pattern = "abc", ShortText = "A", Category = "inny", Profile = "nie-ma" }],
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, cfg));
        Assert.Contains("r1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nie-ma", ex.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void ValidateRules_PoprawneKody_NieRzucaWyjatku()
    {
        var rules = new RuleSet
        {
            Codes =
            [
                new AlarmCode { From = 700, To = 719, ShortText = "Servo {n}", Category = "restart", Priority = 90 },
                new AlarmCode { Code = 1200, ShortText = "Bazowanie (Home)", Category = "restart" },
                new AlarmCode { Code = 720, ShortText = "tuż za zakresem" },
            ],
        };

        ConfigLoader.Validate(rules, null);
    }

    [Fact]
    public void ValidateRules_GdyKodyNakladajaSie_RzucaConfigExceptionZObomaWpisami()
    {
        // Dlaczego: numer może mieć tylko jeden opis — nakładające się wpisy to pomyłka w Notatniku, którą
        // lepiej wykryć przy starcie niż cicho wybrać „pierwszy lepszy”.
        var rules = new RuleSet
        {
            Codes =
            [
                new AlarmCode { From = 700, To = 719, ShortText = "Servo {n}" },
                new AlarmCode { Code = 705, ShortText = "Servo 6" },
            ],
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
        Assert.Contains("700–719", ex.Message, StringComparison.Ordinal);
        Assert.Contains("705", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, null, "Servo")]      // brak numeru
    [InlineData(null, 719, 700, "Servo")]        // from > to
    [InlineData(null, 700, null, "Servo")]       // zakres bez końca
    [InlineData(705, 700, 719, "Servo")]         // code i zakres naraz
    [InlineData(705, null, null, "   ")]         // pusty opis
    [InlineData(-5, null, null, "Ujemny")]       // ujemny numer
    public void ValidateRules_GdyWpisKoduJestNiepoprawny_RzucaConfigException(int? code, int? from, int? to, string shortText)
    {
        var rules = new RuleSet
        {
            Codes = [new AlarmCode { Code = code, From = from, To = to, ShortText = shortText }],
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(rules, null));
        Assert.Contains("codes", ex.Message, StringComparison.Ordinal);
    }


    [Theory]
    [InlineData(0.19)]
    [InlineData(1.01)]
    [InlineData(0.0)]
    public void Validate_GdyPrzezroczystoscOknaAlertuPozaZakresem_RzucaConfigExceptionZNazwaPola(double opacity)
    {
        // Dlaczego: okno alertu prawie niewidoczne (0,0–0,19) to alert, którego operator nie zauważy, a
        // wartość powyżej 1,0 jest po prostu bezsensowna. Komunikat musi wskazywać pole po nazwie —
        // konfigurację poprawia osoba, która nie jest programistą, i musi wiedzieć, co zmienić w pliku.
        AppConfig cfg = Config([Machine("aidite1")], Profile()) with { Ui = new UiConfig { AlertOpacity = opacity } };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("ui.alertOpacity", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.7)]
    [InlineData(1.0)]
    public void Validate_PrzezroczystosciOknaAlertuZDozwolonegoZakresu_NieRzucaWyjatku(double opacity)
    {
        AppConfig cfg = Config([Machine("aidite1")], Profile()) with { Ui = new UiConfig { AlertOpacity = opacity } };

        ConfigLoader.Validate(cfg);
    }

    [Fact]
    public void LoadAppConfig_PrzykladowaKonfiguracja_CzytaPrzezroczystoscOknaAlertu()
    {
        // Dlaczego: przykładowa konfiguracja jest tym, co operator dostaje w paczce — ma nieść
        // wartość 0,7, a nie „cokolwiek, byle się wczytało”.
        AppConfig cfg = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample);

        Assert.Equal(0.7, cfg.Ui.AlertOpacity);
    }

    [Fact]
    public void SerializeIParse_KonfiguracjiZSekcjaUi_ZachowujePrzezroczystoscOknaAlertu()
    {
        // Dlaczego: program zapisuje konfigurację z powrotem na dysk (Ustawienia). Gdyby sekcja "ui" gubiła
        // się przy zapisie, okno alertu po pierwszym zapisaniu ustawień wracałoby do wartości domyślnej i
        // użytkownik musiałby ustawiać ją od nowa po każdej zmianie czegokolwiek innego.
        AppConfig original = ConfigLoader.LoadAppConfig(TestPaths.ConfigExample) with { Ui = new UiConfig { AlertOpacity = 0.45 } };

        string json = ConfigLoader.Serialize(original);
        AppConfig roundTripped = ConfigLoader.ParseAppConfig(json);

        Assert.Contains("\"alertOpacity\"", json, StringComparison.Ordinal);
        Assert.Equal(0.45, roundTripped.Ui.AlertOpacity);
        ConfigLoader.Validate(roundTripped);
    }

    [Fact]
    public void ParseAppConfig_NieznanyKluczWSekcjiUi_JestPomijanyBezOstrzezenia()
    {
        string json = File.ReadAllText(TestPaths.ConfigExample);
        int ui = json.IndexOf("\"ui\": {", StringComparison.Ordinal);
        Assert.True(ui >= 0);
        json = json.Insert(ui + "\"ui\": {".Length, " \"nieznanyKlucz\": 0.5,");
        var warnings = new List<string>();

        AppConfig cfg = ConfigLoader.ParseAppConfig(json, warnings);

        ConfigLoader.Validate(cfg);
        Assert.Empty(warnings);
        Assert.Equal(0.7, cfg.Ui.AlertOpacity);
    }


    [Theory]
    [InlineData(19)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5001)]
    public void Validate_GdyLimitZrzutowPozaZakresem_RzucaConfigExceptionZNazwaPola(int megabytes)
    {
        AppConfig cfg = Config([Machine("aidite1")], Profile()) with
        {
            Paths = new PathsConfig { CapturesMaxMegabytes = megabytes },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("capturesMaxMegabytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("20", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(200)]
    [InlineData(5000)]
    public void Validate_GdyLimitZrzutowWZakresie_Przechodzi(int megabytes)
    {
        AppConfig cfg = Config([Machine("aidite1")], Profile()) with
        {
            Paths = new PathsConfig { CapturesMaxMegabytes = megabytes },
        };

        ConfigLoader.Validate(cfg);
    }

    [Fact]
    public void Validate_GdyRetencjaZrzutowMniejszaNizDzien_RzucaConfigException()
    {
        AppConfig cfg = Config([Machine("aidite1")], Profile()) with
        {
            Paths = new PathsConfig { CaptureRetentionDays = 0 },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("captureRetentionDays", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5000, 90)]
    [InlineData(600000, 0)]
    [InlineData(600000, 4000)]
    public void UzupelnianieRaportuGrupy_OdrzucaBezsensowneOkno(int intervalMs, int windowDays)
    {
        // zbyt częste pytanie zalałoby sieć serią odpowiedzi, a okno 0 dni
        // (albo dziesięć lat) to ustawienie, po którym raport grupy cicho przestaje się uzupełniać.
        var cfg = Config([Machine("m1")], Profile()) with
        {
            Cluster = new ClusterConfig
            {
                Enabled = true,
                DowntimeSyncIntervalMs = intervalMs,
                DowntimeSyncWindowDays = windowDays,
            },
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("downtimeSync", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UzupelnianieRaportuGrupy_DomyslnieDziesiecMinutIDziewiecdziesiatDni()
    {
        var cluster = new ClusterConfig();

        Assert.Equal(600000, cluster.DowntimeSyncIntervalMs);
        Assert.Equal(90, cluster.DowntimeSyncWindowDays);
    }

    [Fact]
    public void DomyslneUstawienia_ToTrzyDniDwiescieMegabajtowIWylaczoneZrzutyPrzerw()
    {
        var paths = new PathsConfig();
        var diagnostics = new DiagnosticsConfig();

        Assert.Equal(3, paths.CaptureRetentionDays);
        Assert.Equal(200, paths.CapturesMaxMegabytes);
        Assert.False(diagnostics.CaptureBreaks);
    }
}
