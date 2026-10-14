using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class TimingValidationRegressionTests
{
    private static ScreenProfile Profile() => new()
    {
        Id = "p1",
        StatusBarRoi = new RectInt(0, 0, 100, 20),
    };

    private static AppConfig Config(TimingConfig timing) => new()
    {
        Machines =
        [
            new MachineConfig { Id = "m1", Name = "Maszyna 1", Host = "10.0.0.1", Port = 5900, Profile = "p1" },
        ],
        Profiles = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal) { ["p1"] = Profile() },
        Timing = timing,
    };

    [Fact]
    public void Validate_GdyIdleUpdateTimeoutMsPrzekraczaWlasnyReadTimeoutMs_RzucaConfigExceptionZObiemaNazwamiPol()
    {
        // Granica jest RELATYWNA: nie chodzi o stałe 30 s, tylko o wartość readTimeoutMs z tej samej
        // konfiguracji — inaczej obserwator czekałby na odpowiedź dłużej niż trwa cały odczyt.
        AppConfig cfg = Config(new TimingConfig { ReadTimeoutMs = 2000, IdleUpdateTimeoutMs = 3000 });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("idleUpdateTimeoutMs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("readTimeoutMs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyIdleUpdateTimeoutMsRownyReadTimeoutMs_JestDopuszczalny()
    {
        AppConfig cfg = Config(new TimingConfig { ReadTimeoutMs = 2000, IdleUpdateTimeoutMs = 2000 });

        ConfigLoader.Validate(cfg);
    }

    [Fact]
    public void Validate_GdyIdleUpdateTimeoutMsRownyStu_JestDopuszczalny()
    {
        AppConfig cfg = Config(new TimingConfig { IdleUpdateTimeoutMs = 100 });

        ConfigLoader.Validate(cfg);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    public void Validate_GdyOcrMaxAttemptsJestUjemne_RzucaConfigExceptionZNazwaPola(int attempts)
    {
        AppConfig cfg = Config(new TimingConfig { OcrMaxAttempts = attempts });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("ocrMaxAttempts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyOcrMaxAttemptsWynosiZero_JestDopuszczalny()
    {
        // 0 = „nie próbuj OCR w ogóle” (alert bez treści) — to poprawne ustawienie, nie błąd.
        AppConfig cfg = Config(new TimingConfig { OcrMaxAttempts = 0 });

        ConfigLoader.Validate(cfg);
    }

    [Theory]
    [InlineData(499)]
    [InlineData(0)]
    public void Validate_GdyPollingModeIntervalMsPonizej500_RzucaConfigExceptionZNazwaPola(int intervalMs)
    {
        AppConfig cfg = Config(new TimingConfig { PollingModeIntervalMs = intervalMs });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("pollingModeIntervalMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyEpisodeCloseAfterMsPonizej1000_RzucaConfigExceptionZNazwaPola()
    {
        AppConfig cfg = Config(new TimingConfig { EpisodeCloseAfterMs = 999 });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("episodeCloseAfterMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyConnectTimeoutMsPonizej500_RzucaConfigExceptionZNazwaPola()
    {
        AppConfig cfg = Config(new TimingConfig { ConnectTimeoutMs = 499 });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("connectTimeoutMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyOfflineAlertAfterMsJestUjemne_RzucaConfigExceptionZNazwaPola()
    {
        AppConfig cfg = Config(new TimingConfig { OfflineAlertAfterMs = -1 });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("offlineAlertAfterMs", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2000)]
    public void Validate_GdyOcrRetryIntervalMsJestUjemny_RzucaConfigExceptionZNazwaPola(int interval)
    {
        // Dlaczego: ujemny odstęp znaczyłby „ponawiaj OCR zawsze, gdy treść się zmieni” — przy migotaniu
        // ekranu 12 maszyn to OCR co sekundę razy 12 na komputerze, przy którym ktoś pracuje. Konfiguracja
        // ma się nie wczytać z czytelnym komunikatem, zamiast cicho zajechać komputer operatora.
        AppConfig cfg = Config(new TimingConfig { OcrRetryIntervalMs = interval });

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));

        Assert.Contains("ocrRetryIntervalMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyOcrRetryIntervalMsWynosiZero_JestDopuszczalny()
    {
        // Dlaczego: 0 = „ponawiaj OCR przy KAŻDEJ wykrytej zmianie treści, bez dławienia” — to świadome
        // ustawienie diagnostyczne, nie błąd konfiguracji.
        ConfigLoader.Validate(Config(new TimingConfig { OcrRetryIntervalMs = 0 }));
    }
}
