using System.Text;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ProgramStateConfigTests
{
    private static readonly string[] Rows = ["....", ".##.", "...."];

    /// <summary>Poprawna sekcja — punkt wyjścia dla testów walidacji.</summary>
    private static ProgramStateProfile Valid() => new()
    {
        BoxRoi = new RectInt(1, 2, 160, 29),
        AbortPattern = new AbortPattern { X = 40, Y = 9, Rows = Rows },
    };

    private static ScreenProfile Profile(ProgramStateProfile? state, string id = "p1") => new()
    {
        Id = id,
        StatusBarRoi = new RectInt(150, 0, 460, 32),
        ProgramState = state,
    };

    private static AppConfig Config(ScreenProfile profile) => new()
    {
        Machines = [new MachineConfig { Id = "m1", Name = "M 1", Host = "10.0.0.1", Port = 5900, Profile = profile.Id }],
        Profiles = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal) { [profile.Id] = profile },
    };

    // ------------------------------------------------------------------------------------------------
    // config.json
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void ParseAppConfig_SekcjaProgramState_WczytujeWszystkiePola()
    {
        const string json = """
            {
              "machines": [ { "id": "m1", "name": "M 1", "host": "10.0.0.1", "port": 5900, "profile": "p1" } ],
              "profiles": {
                "p1": {
                  "statusBarRoi": { "x": 150, "y": 0, "width": 460, "height": 32 },
                  "programState": {
                    "enabled": true,
                    "boxRoi": { "x": 1, "y": 2, "width": 160, "height": 29 },
                    "runFill": { "hueMinDeg": 105, "hueMaxDeg": 155, "minSaturation": 0.6, "minValue": 0.45 },
                    "runMinFraction": 0.6,
                    "abortWords": [ "abort", "przerwano" ],
                    "abortMaxDistance": 2,
                    "abortPattern": {
                      "x": 40, "y": 9, "whiteMinChannel": 170, "maxMismatch": 12, "maxShift": 2,
                      "rows": [ "....", ".##.", "...." ]
                    }
                  }
                }
              }
            }
            """;

        AppConfig cfg = ConfigLoader.ParseAppConfig(json);
        ConfigLoader.Validate(cfg);

        ProgramStateProfile ps = Assert.IsType<ProgramStateProfile>(cfg.Profiles["p1"].ProgramState);
        Assert.True(ps.Enabled);
        Assert.Equal(new RectInt(1, 2, 160, 29), ps.BoxRoi);
        Assert.Equal(105, ps.RunFill.HueMinDeg);
        Assert.Equal(155, ps.RunFill.HueMaxDeg);
        Assert.Equal(0.6, ps.RunFill.MinSaturation);
        Assert.Equal(0.45, ps.RunFill.MinValue);
        Assert.Equal(0.6, ps.RunMinFraction);
        Assert.Equal(["abort", "przerwano"], ps.AbortWords);
        Assert.Equal(2, ps.AbortMaxDistance);
        AbortPattern pattern = Assert.IsType<AbortPattern>(ps.AbortPattern);
        Assert.Equal(40, pattern.X);
        Assert.Equal(9, pattern.Y);
        Assert.Equal(170, pattern.WhiteMinChannel);
        Assert.Equal(12, pattern.MaxMismatch);
        Assert.Equal(2, pattern.MaxShift);
        Assert.Equal(Rows, pattern.Rows);
    }

    [Fact]
    public void ParseAppConfig_SamoPudelko_DajeWartosciDomyslneZPomiaru580()
    {
        const string json = """
            {
              "machines": [],
              "profiles": {
                "p1": {
                  "statusBarRoi": { "x": 150, "y": 0, "width": 460, "height": 32 },
                  "programState": { "boxRoi": { "x": 1, "y": 2, "width": 160, "height": 29 } }
                }
              }
            }
            """;

        ProgramStateProfile ps = ConfigLoader.ParseAppConfig(json).Profiles["p1"].ProgramState!;

        Assert.True(ps.Enabled);
        Assert.Equal(100, ps.RunFill.HueMinDeg);
        Assert.Equal(160, ps.RunFill.HueMaxDeg);
        Assert.Equal(0.5, ps.RunFill.MinSaturation);
        Assert.Equal(0.4, ps.RunFill.MinValue);
        Assert.Equal(0.5, ps.RunMinFraction);
        Assert.Equal(["abort"], ps.AbortWords);
        Assert.Equal(1, ps.AbortMaxDistance);
        Assert.Null(ps.AbortPattern);
    }

    [Fact]
    public void ParseAppConfig_BezSekcjiProgramState_ZostawiaNull()
    {
        const string json = """
            {
              "machines": [],
              "profiles": { "p1": { "statusBarRoi": { "x": 0, "y": 0, "width": 100, "height": 20 } } }
            }
            """;

        Assert.Null(ConfigLoader.ParseAppConfig(json).Profiles["p1"].ProgramState);
    }

    [Fact]
    public void PrzykladowaKonfiguracja_TylkoAmm580MaSekcjeStanuProgramu_IPrzechodziWalidacje()
    {
        ConfigLoader.Validate(TestPaths.ExampleConfig);

        foreach ((string id, ScreenProfile profile) in TestPaths.ExampleConfig.Profiles)
        {
            if (id == "amm580")
            {
                Assert.NotNull(profile.ProgramState);
            }
            else
            {
                Assert.Null(profile.ProgramState);
            }
        }
    }

    [Fact]
    public void Validate_PoprawnaSekcja_Przechodzi()
    {
        ConfigLoader.Validate(Config(Profile(Valid())));
        ConfigLoader.Validate(Config(Profile(Valid() with { AbortPattern = null })));
        ConfigLoader.Validate(Config(Profile(null)));
    }

    [Theory]
    [InlineData("boxRoi", "programState.boxRoi")]
    [InlineData("runMinFraction0", "programState.runMinFraction")]
    [InlineData("runMinFraction>1", "programState.runMinFraction")]
    [InlineData("runMinFractionNaN", "programState.runMinFraction")]
    [InlineData("runFillHue", "programState.runFill.hueMaxDeg")]
    [InlineData("runFillSaturation", "programState.runFill.minSaturation")]
    [InlineData("runFillNull", "programState.runFill")]
    [InlineData("abortMaxDistance3", "programState.abortMaxDistance")]
    [InlineData("abortMaxDistance-1", "programState.abortMaxDistance")]
    [InlineData("abortWordsEmpty", "programState.abortWords")]
    [InlineData("abortWordsBlank", "programState.abortWords")]
    [InlineData("abortWordsDigits", "programState.abortWords")]
    [InlineData("abortWordsNull", "programState.abortWords")]
    [InlineData("rowsEmpty", "programState.abortPattern.rows")]
    [InlineData("rowsUnequal", "programState.abortPattern.rows")]
    [InlineData("rowsBadChar", "programState.abortPattern.rows")]
    [InlineData("rowsEmptyRow", "programState.abortPattern.rows")]
    [InlineData("patternOutsideRight", "programState.boxRoi")]
    [InlineData("patternOutsideTop", "programState.boxRoi")]
    [InlineData("maxMismatch", "programState.abortPattern.maxMismatch")]
    [InlineData("maxShift", "programState.abortPattern.maxShift")]
    [InlineData("whiteMinChannel0", "programState.abortPattern.whiteMinChannel")]
    [InlineData("whiteMinChannel256", "programState.abortPattern.whiteMinChannel")]
    public void Validate_BlednaSekcja_RzucaZNazwaProfiluIPola(string mistake, string field)
    {
        ProgramStateProfile v = Valid();
        AbortPattern pattern = v.AbortPattern!;
        ProgramStateProfile broken = mistake switch
        {
            "boxRoi" => v with { BoxRoi = new RectInt(1, 2, 0, 29) },
            "runMinFraction0" => v with { RunMinFraction = 0 },
            "runMinFraction>1" => v with { RunMinFraction = 1.01 },
            "runMinFractionNaN" => v with { RunMinFraction = double.NaN },
            "runFillHue" => v with { RunFill = v.RunFill with { HueMaxDeg = 400 } },
            "runFillSaturation" => v with { RunFill = v.RunFill with { MinSaturation = 1.5 } },
            "runFillNull" => v with { RunFill = null! },
            "abortMaxDistance3" => v with { AbortMaxDistance = 3 },
            "abortMaxDistance-1" => v with { AbortMaxDistance = -1 },
            "abortWordsEmpty" => v with { AbortWords = [] },
            "abortWordsBlank" => v with { AbortWords = ["abort", " "] },
            "abortWordsDigits" => v with { AbortWords = ["12:"] },
            "abortWordsNull" => v with { AbortWords = null! },
            "rowsEmpty" => v with { AbortPattern = pattern with { Rows = [] } },
            "rowsUnequal" => v with { AbortPattern = pattern with { Rows = ["....", ".#."] } },
            "rowsBadChar" => v with { AbortPattern = pattern with { Rows = ["..x.", "...."] } },
            "rowsEmptyRow" => v with { AbortPattern = pattern with { Rows = [""] } },
            "patternOutsideRight" => v with { AbortPattern = pattern with { X = 158 } }, // 158..161 > 160
            "patternOutsideTop" => v with { AbortPattern = pattern with { Y = 1 } },
            "maxMismatch" => v with { AbortPattern = pattern with { MaxMismatch = -1 } },
            "maxShift" => v with { AbortPattern = pattern with { MaxShift = 4 } },
            "whiteMinChannel0" => v with { AbortPattern = pattern with { WhiteMinChannel = 0 } },
            "whiteMinChannel256" => v with { AbortPattern = pattern with { WhiteMinChannel = 256 } },
            _ => throw new ArgumentOutOfRangeException(nameof(mistake)),
        };

        ConfigException ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(Config(Profile(broken))));

        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
        if (mistake != "runFillNull" && !mistake.StartsWith("runFill", StringComparison.Ordinal))
        {
            Assert.Contains("'p1'", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Validate_SekcjaWylaczona_NieSprawdzaJejPol()
    {
        // Jak przy "idle" i "mainScreen": wyłączona sekcja nic nie robi, więc nie może blokować startu.
        var disabled = new ProgramStateProfile
        {
            Enabled = false,
            BoxRoi = RectInt.Empty,
            RunMinFraction = 7,
            AbortWords = [],
            AbortPattern = new AbortPattern { Rows = ["x"] },
        };

        ConfigLoader.Validate(Config(Profile(disabled)));
    }

    [Fact]
    public void Serialize_ProfilBezSekcji_NieZapisujeKluczaProgramState_AZSekcjaRoundTrip()
    {
        string without = ConfigLoader.Serialize(Config(Profile(null)));
        Assert.DoesNotContain("programState", without, StringComparison.Ordinal);

        string with = ConfigLoader.Serialize(TestPaths.ExampleConfig);
        AppConfig reloaded = ConfigLoader.ParseAppConfig(with);
        ProgramStateProfile original = TestPaths.Profile("amm580").ProgramState!;
        ProgramStateProfile copy = reloaded.Profiles["amm580"].ProgramState!;
        Assert.Equal(original.BoxRoi, copy.BoxRoi);
        Assert.Equal(original.RunFill, copy.RunFill);
        Assert.Equal(original.AbortWords, copy.AbortWords);
        Assert.Equal(original.AbortPattern!.Rows, copy.AbortPattern!.Rows);
        Assert.Equal(
            (original.AbortPattern.X, original.AbortPattern.Y, original.AbortPattern.WhiteMinChannel, original.AbortPattern.MaxMismatch, original.AbortPattern.MaxShift),
            (copy.AbortPattern.X, copy.AbortPattern.Y, copy.AbortPattern.WhiteMinChannel, copy.AbortPattern.MaxMismatch, copy.AbortPattern.MaxShift));
    }

    // ------------------------------------------------------------------------------------------------
    // Plik grupy komputerów (.vncgroup / group-settings)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void PlikGrupy_PrzenosiSekcjeStanuProgramuBezZmian()
    {
        GroupFile source = GroupFileTests.Invitation();
        GroupFile copy = GroupFileCodec.Parse(GroupFileCodec.Serialize(source));

        ProgramStateProfile original = source.Profile.Profiles["amm580"].ProgramState!;
        ProgramStateProfile imported = copy.Profile.Profiles["amm580"].ProgramState!;
        Assert.Equal(original.BoxRoi, imported.BoxRoi);
        Assert.Equal(original.RunFill, imported.RunFill);
        Assert.Equal(original.RunMinFraction, imported.RunMinFraction);
        Assert.Equal(original.AbortWords, imported.AbortWords);
        Assert.Equal(original.AbortMaxDistance, imported.AbortMaxDistance);
        Assert.Equal(original.AbortPattern!.Rows, imported.AbortPattern!.Rows);
        Assert.Null(copy.Profile.Profiles["opencnc"].ProgramState);
        Assert.Equal(source.Identity.ProfileHash, copy.Identity.ProfileHash);
    }

    [Fact]
    public void PlikGrupyZeStarejWersji_BezSekcji_WczytujeSieZTymSamymOdciskiem()
    {
        var json = JsonNode.Parse(GroupFileCodec.Serialize(GroupFileTests.Invitation()))!;
        Assert.True(json["profile"]!["profiles"]!["amm580"]!.AsObject().Remove("programState"));
        string olderHash = GroupFileTests.OlderVersionProfileHash(json["profile"]!);
        json["identity"]!["profileHash"] = olderHash;

        GroupFile file = GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.All(file.Profile.Profiles.Values, p => Assert.Null(p.ProgramState));
        Assert.Equal(olderHash, file.Identity.ProfileHash);
        Assert.Equal(olderHash, GroupFileCodec.ProfileHash(file.Profile));
    }

    [Fact]
    public void OdciskProfilu_ProfilBezSekcji_NieZawieraKluczaProgramState()
    {
        GroupProfile profile = GroupFileTests.Invitation().Profile;
        GroupProfile without = profile with
        {
            Profiles = profile.Profiles.ToDictionary(p => p.Key, p => p.Value with { ProgramState = null }, StringComparer.Ordinal),
        };

        string json = Encoding.UTF8.GetString(GroupFileCodec.Serialize(GroupFile.Create("Hala", without)));

        Assert.DoesNotContain("programState", json, StringComparison.Ordinal);
        // Sekcja zmienia odcisk tylko tam, gdzie jest — to zmiana ustawień obserwacji, więc ma go zmieniać.
        Assert.NotEqual(GroupFileCodec.ProfileHash(profile), GroupFileCodec.ProfileHash(without));
    }

    [Theory]
    [InlineData("profile.profiles.amm580.programState")]
    [InlineData("profile.profiles.amm580.programState.abortPattern")]
    [InlineData("profile.profiles.amm580.programState.runFill")]
    public void PlikGrupy_NieznanePoleWSekcjiStanuProgramu_JestOdrzucane(string parentPath)
    {
        var json = JsonNode.Parse(GroupFileCodec.Serialize(GroupFileTests.Invitation()))!;
        JsonNode parent = json;
        foreach (string part in parentPath.Split('.')) parent = parent[part]!;
        parent["password"] = "PRIVATE-FORBIDDEN";

        var error = Assert.Throws<InvalidDataException>(() => GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.DoesNotContain("PRIVATE-", error.ToString());
    }
}
