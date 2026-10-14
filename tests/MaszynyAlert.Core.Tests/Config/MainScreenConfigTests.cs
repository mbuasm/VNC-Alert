using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class MainScreenConfigTests
{
    private static ScreenProfile Profile(MainScreenWatchProfile? mainScreen, string id = "p1") => new()
    {
        Id = id,
        StatusBarRoi = new RectInt(0, 0, 100, 20),
        MainScreen = mainScreen,
    };

    private static AppConfig Config(ScreenProfile profile, TimingConfig? timing = null) => new()
    {
        Machines = [new MachineConfig { Id = "m1", Name = "M 1", Host = "10.0.0.1", Port = 5900, Profile = profile.Id }],
        Profiles = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal) { [profile.Id] = profile },
        Timing = timing ?? new TimingConfig(),
    };

    [Fact]
    public void ParseAppConfig_SekcjiMainScreen_WczytujeWariantyEtykietyIUstawienia()
    {
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
              "profiles": {
                "p1": {
                  "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
                  "mainScreen": {
                    "enabled": true,
                    "anchorLabels": [ ["Milling", "Status"], ["MillingStatus"] ],
                    "maxDistance": 1,
                    "retryScaled": false
                  }
                }
              },
              "timing": {
                "alertSoundRepeatMs": 30000,
                "mainScreenCheckIntervalMs": 15000,
                "wrongScreenAlertAfterMs": 60000,
                "wrongScreenAlertSound": true,
                "wrongScreenRemindAfterMs": 0,
                "escalationAfterAckMs": 0
              },
              "cluster": { "enabled": false }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);
        ConfigLoader.Validate(cfg);

        MainScreenWatchProfile ms = Assert.IsType<MainScreenWatchProfile>(cfg.Profiles["p1"].MainScreen);
        Assert.True(ms.Enabled);
        Assert.Equal(2, ms.AnchorLabels.Count);
        Assert.Equal(["Milling", "Status"], ms.AnchorLabels[0]);
        Assert.Equal(["MillingStatus"], ms.AnchorLabels[1]);
        Assert.Equal(1, ms.MaxDistance);
        Assert.False(ms.RetryScaled);
        Assert.Same(ms.AnchorLabels, ms.EffectiveAnchorLabels);

        Assert.Equal(30000, cfg.Timing.AlertSoundRepeatMs);
        Assert.Equal(15000, cfg.Timing.MainScreenCheckIntervalMs);
        Assert.Equal(60000, cfg.Timing.WrongScreenAlertAfterMs);
        Assert.True(cfg.Timing.WrongScreenAlertSound);
        Assert.Equal(0, cfg.Timing.WrongScreenRemindAfterMs);
        Assert.Equal(0, cfg.Timing.EscalationAfterAckMs);
        Assert.False(cfg.Cluster.Enabled);
    }

    [Fact]
    public void Validate_GdySameMessageRejoinKrotszeNizPrzerwaEpizodu_Rzuca()
    {
        var timing = new TimingConfig { EpisodeBreakAfterMs = 10000, SameMessageRejoinMs = 5000 };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(Profile(null), timing)));

        Assert.Contains("timing.sameMessageRejoinMs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAppConfig_SekcjiMainScreen_WczytujeContextRoi()
    {
        const string json = """
            {
              "machines": [],
              "profiles": {
                "p1": {
                  "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
                  "mainScreen": {
                    "contextRoi": [ { "x": 423, "y": 400, "width": 260, "height": 70 } ]
                  }
                }
              }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        MainScreenWatchProfile ms = Assert.IsType<MainScreenWatchProfile>(cfg.Profiles["p1"].MainScreen);
        RectInt roi = Assert.Single(ms.ContextRoi);
        Assert.Equal(new RectInt(423, 400, 260, 70), roi);
    }

    [Fact]
    public void ParseAppConfig_BezContextRoi_DajePustaListe()
    {
        const string json = """
            {
              "machines": [],
              "profiles": {
                "p1": {
                  "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 },
                  "mainScreen": { "enabled": true }
                }
              }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        Assert.Empty(Assert.IsType<MainScreenWatchProfile>(cfg.Profiles["p1"].MainScreen).ContextRoi);
    }

    [Fact]
    public void ParseAppConfig_BezSekcjiMainScreen_ZostawiaNull()
    {
        const string json = """
            {
              "machines": [],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 } } }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);

        Assert.Null(cfg.Profiles["p1"].MainScreen);
    }

    [Fact]
    public void Validate_GdyWariantEtykietyMaPusteSlowo_RzucaZNazwaProfiluIPola()
    {
        var mainScreen = new MainScreenWatchProfile { AnchorLabels = [["Milling", ""]] };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(Profile(mainScreen))));

        Assert.Contains("p1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mainScreen.anchorLabels", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GdyNapisOknaZaslaniajacegoMaPusteSlowo_RzucaZNazwaProfiluIPola()
    {
        var mainScreen = new MainScreenWatchProfile { OtherScreenLabels = [["Alarm", " "]] };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(Profile(mainScreen))));

        Assert.Contains("p1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mainScreen.otherScreenLabels", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrzykladowyConfig_Profil580_UznajeOknoAlarmInformationZaInnyEkran()
    {
        AppConfig cfg = TestPaths.ExampleConfig;

        MainScreenWatchProfile amm580 = Assert.IsType<MainScreenWatchProfile>(cfg.Profiles["amm580"].MainScreen);
        Assert.Contains(amm580.OtherScreenLabels, v => v.SequenceEqual(["Alarm", "Information"]));
        Assert.All(cfg.Profiles.Where(p => p.Key != "amm580" && p.Value.MainScreen is not null),
            p => Assert.Empty(p.Value.MainScreen!.OtherScreenLabels));
    }

    [Fact]
    public void Validate_GdyMaxDistancePozaZakresem_Rzuca()
    {
        var mainScreen = new MainScreenWatchProfile { MaxDistance = 9 };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(Profile(mainScreen))));

        Assert.Contains("mainScreen.maxDistance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SekcjaMainScreenWylaczona_NieSprawdzaJejPol()
    {
        var mainScreen = new MainScreenWatchProfile { Enabled = false, MaxDistance = 99, AnchorLabels = [[""]] };

        ConfigLoader.Validate(Config(Profile(mainScreen)));
    }

    [Theory]
    [InlineData("alertSoundRepeatMs", 500)]
    [InlineData("mainScreenCheckIntervalMs", 500)]
    [InlineData("wrongScreenAlertAfterMs", -1)]
    [InlineData("wrongScreenRemindAfterMs", -1)]
    [InlineData("escalationAfterAckMs", -1)]
    [InlineData("mainScreenOcrGraceMs", 4999)]
    [InlineData("sameMessageRejoinMs", -1)]
    public void Validate_NowychPolTiming_RzucaZNazwaPola(string field, int value)
    {
        TimingConfig timing = field switch
        {
            "mainScreenOcrGraceMs" => new TimingConfig { MainScreenOcrGraceMs = value },
            "sameMessageRejoinMs" => new TimingConfig { SameMessageRejoinMs = value },
            "alertSoundRepeatMs" => new TimingConfig { AlertSoundRepeatMs = value },
            "mainScreenCheckIntervalMs" => new TimingConfig { MainScreenCheckIntervalMs = value },
            "wrongScreenAlertAfterMs" => new TimingConfig { WrongScreenAlertAfterMs = value },
            "wrongScreenRemindAfterMs" => new TimingConfig { WrongScreenRemindAfterMs = value },
            "escalationAfterAckMs" => new TimingConfig { EscalationAfterAckMs = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(Profile(null), timing)));

        Assert.Contains("timing." + field, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ZeroWyłączającePonawianieIEskalację_JestDopuszczalne()
    {
        ConfigLoader.Validate(Config(Profile(null), new TimingConfig { WrongScreenRemindAfterMs = 0, EscalationAfterAckMs = 0 }));
    }

    [Fact]
    public void Validate_ZerowyProgInnegoEkranu_JestDopuszczalny()
    {
        ConfigLoader.Validate(Config(Profile(null), new TimingConfig { WrongScreenAlertAfterMs = 0 }));
    }

    [Fact]
    public void PrzykladowaKonfiguracja_TrybJednegoKomputeraIObserwacjaEkranuDlaOpencnc()
    {
        AppConfig cfg = TestPaths.ExampleConfig;
        ConfigLoader.Validate(cfg);

        Assert.False(cfg.Cluster.Enabled);

        MainScreenWatchProfile ms = Assert.IsType<MainScreenWatchProfile>(cfg.Profiles["opencnc"].MainScreen);
        Assert.True(ms.Enabled);
        Assert.Contains(ms.EffectiveAnchorLabels, v => v.SequenceEqual(["Milling", "Status"]));

        Assert.Equal(60000, cfg.Timing.AlertSoundRepeatMs);

        Assert.Equal(10000, cfg.Timing.MainScreenCheckIntervalMs);
        Assert.Equal(0, cfg.Timing.WrongScreenAlertAfterMs);
        Assert.False(cfg.Timing.WrongScreenAlertSound);
        Assert.Equal(240000, cfg.Timing.WrongScreenRemindAfterMs);
        Assert.Equal(240000, cfg.Timing.EscalationAfterAckMs);

        Assert.Equal(7, cfg.Machines.Count(m => m.Enabled));
        Assert.Equal(7, cfg.Machines.Count(m => m.Enabled && m.Profile == "opencnc"));
        Assert.Empty(cfg.Machines.Where(m => m.Enabled && m.Profile == "aidite5x"));
        Assert.Equal(
            new[] { "amm520-1", "amm520-2", "premile", "cameo1" },
            cfg.Machines.Where(m => !m.RequiresPassword).Select(m => m.Id).ToArray());
    }

    [Fact]
    public void Serialize_IParseAppConfig_ZachowujaSekcjeMainScreen()
    {
        AppConfig original = Config(Profile(new MainScreenWatchProfile { AnchorLabels = [["Job", "Progress"]], MaxDistance = 1 }));

        AppConfig roundTripped = ConfigLoader.ParseAppConfig(ConfigLoader.Serialize(original));

        MainScreenWatchProfile ms = Assert.IsType<MainScreenWatchProfile>(roundTripped.Profiles["p1"].MainScreen);
        Assert.Equal(["Job", "Progress"], Assert.Single(ms.AnchorLabels));
        Assert.Equal(1, ms.MaxDistance);
    }
}
