using System.Text.Json.Nodes;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ReportRegressionTests
{
    [Theory]
    [InlineData("machines", "[]")]
    [InlineData("machines", "null")]
    [InlineData("paths", "null")]
    [InlineData("paths.captureRetentionDays", "0")]
    [InlineData("paths.logRetentionDays", "-1")]
    [InlineData("paths.capturesDir", "\" \"")]
    [InlineData("ocr.engine", "\"windwos\"")]
    [InlineData("ocr.engine", "\"tesseract\"")]
    [InlineData("cluster.udpPort", "70000")]
    [InlineData("cluster.heartbeatIntervalMs", "0")]
    [InlineData("cluster.peerTimeoutMs", "0")]
    [InlineData("cluster.clockSkewToleranceSec", "-1")]
    [InlineData("cluster.peers", "[\"bad address\"]")]
    [InlineData("profiles.opencnc.red.hueMinDeg", "4000")]
    [InlineData("profiles.opencnc.red.hueMaxDeg", "-1")]
    [InlineData("profiles.opencnc.red.minSaturation", "2")]
    [InlineData("profiles.opencnc.red.minValue", "-1")]
    public void InvalidOperatorConfig_ReportsField(string field, string jsonValue)
    {
        var json = JsonNode.Parse(ConfigLoader.Serialize(TestPaths.ExampleConfig))!;
        json["cluster"]!["enabled"] = true;
        JsonNode parent = json;
        string[] parts = field.Split('.');
        foreach (string part in parts[..^1]) parent = parent[part]!;
        parent[parts[^1]] = JsonNode.Parse(jsonValue);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(ConfigLoader.ParseAppConfig(json.ToJsonString())));
        Assert.Contains(parts[0], ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingMachinesSection_IsRejected_ButExplicitlyDisabledMonitoringIsAllowed()
    {
        var json = JsonNode.Parse(ConfigLoader.Serialize(TestPaths.ExampleConfig))!.AsObject();
        json.Remove("machines");
        var cfg = ConfigLoader.ParseAppConfig(json.ToJsonString());
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        ConfigLoader.Validate(cfg with { Enabled = false });
        ConfigLoader.Validate(TestPaths.ExampleConfig with
        { Machines = TestPaths.ExampleConfig.Machines.Select(m => m with { Enabled = false }).ToList() });
    }

    [Fact]
    public void SaveConfig_PreservesOriginalAsBackup_AndUnrelatedSettings()
    {
        using var dir = new TempDir("config-save");
        string path = dir.File("config.json");
        var original = TestPaths.ExampleConfig;
        string text = "// komentarz operatora\n" + ConfigLoader.Serialize(original);
        File.WriteAllText(path, text);
        var changed = original with { Ui = original.Ui with { WindowsNotificationsEnabled = false },
            Cluster = original.Cluster with { Priority = 57 } };
        ConfigLoader.SaveAppConfig(path, changed);
        Assert.Equal(text, File.ReadAllText(path + ".bak"));
        var saved = ConfigLoader.LoadAppConfig(path);
        Assert.False(saved.Ui.WindowsNotificationsEnabled);
        Assert.Equal(57, saved.Cluster.Priority);
        Assert.Equal(original.Machines, saved.Machines);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void SaveConfig_FailedReplacement_LeavesOriginalIntact()
    {
        using var dir = new TempDir("config-save-failure");
        string path = dir.File("config.json");
        string original = ConfigLoader.Serialize(TestPaths.ExampleConfig);
        File.WriteAllText(path, original);
        Directory.CreateDirectory(path + ".bak"); // miejsce kopii zapasowej jest niedostępne
        var error = Record.Exception(() => ConfigLoader.SaveAppConfig(path, TestPaths.ExampleConfig));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
