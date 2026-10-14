using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class SharedConfigMachinesTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "shared-config-machines-" + Guid.NewGuid().ToString("N"));
    private SharedConfigStore Store => new(Path.Combine(_directory, "shared-config.json"));

    private static SharedConfigDocument Document(IEnumerable<KeyValuePair<string, bool>>? machines = null, string host = "PC-A",
        DateTimeOffset? at = null) => new()
    {
        Machines = machines is null ? null : SharedConfigDocument.CanonicalMachines(machines),
        EditedAt = at ?? Noon, EditedBy = host,
    };

    private static KeyValuePair<string, bool> On(string id) => new(id, true);
    private static KeyValuePair<string, bool> Off(string id) => new(id, false);

    // ============================== ApplyTo ==============================

    [Fact]
    public void ApplyTo_SwitchesOnlyTheListedMachines_AndCopiesInsteadOfMutating()
    {
        AppConfig baseConfig = TestPaths.ExampleConfig;
        MachineConfig amm580Before = baseConfig.Machines.Single(m => m.Id == "amm580");
        MachineConfig aidite1Before = baseConfig.Machines.Single(m => m.Id == "aidite1");
        Assert.False(amm580Before.Enabled);
        Assert.True(aidite1Before.Enabled);

        AppConfig effective = Document([On("amm580"), Off("aidite1"), On("nieznana-maszyna")]).ApplyTo(baseConfig);

        Assert.True(effective.Machines.Single(m => m.Id == "amm580").Enabled);
        Assert.False(effective.Machines.Single(m => m.Id == "aidite1").Enabled);
        // Baza nie została zmutowana (profil grupy dzieli te obiekty z innymi komputerami).
        Assert.False(amm580Before.Enabled);
        Assert.True(aidite1Before.Enabled);
        Assert.NotSame(baseConfig.Machines, effective.Machines);
        // Maszyna bez wpisu to TEN SAM obiekt; nieznany identyfikator nie dodaje maszyny.
        Assert.Same(baseConfig.Machines.Single(m => m.Id == "aidite2"), effective.Machines.Single(m => m.Id == "aidite2"));
        Assert.Equal(baseConfig.Machines.Count, effective.Machines.Count);
        Assert.Equal(baseConfig.Machines.Select(m => m.Id), effective.Machines.Select(m => m.Id));
        // Nic poza enabled się nie zmienia.
        Assert.Equal(amm580Before with { Enabled = true }, effective.Machines.Single(m => m.Id == "amm580"));
        Assert.Equal(baseConfig.Timing, effective.Timing);
        Assert.Equal(baseConfig.Profiles, effective.Profiles);
    }

    [Fact]
    public void ApplyTo_WithoutMachines_LeavesTheListUntouched()
    {
        AppConfig baseConfig = TestPaths.ExampleConfig;

        AppConfig effective = Document().ApplyTo(baseConfig);

        Assert.Same(baseConfig.Machines, effective.Machines);
        Assert.True(Document().IsEmpty);
        Assert.False(Document([On("amm580")]).IsEmpty);
    }

    /// <summary>
    /// Odcisk profilu grupy liczy się z <see cref="GroupProfile"/> — warstwa wspólna go nie dotyka, więc dwa komputery
    /// z różnymi przełącznikami w Panelu mają ten sam odcisk i dalej współpracują (po to maszyny NIE poszły do profilu).
    /// </summary>
    [Fact]
    public void SwitchingAMachineInTheSharedLayer_DoesNotChangeTheGroupProfileFingerprint()
    {
        AppConfig baseConfig = TestPaths.ExampleConfig;
        var profile = GroupProfile.Capture(baseConfig, TestPaths.ExampleRules, new Dictionary<string, string>());
        string before = GroupFileCodec.ProfileHash(profile);

        AppConfig effective = Document([On("amm580")]).ApplyTo(profile.ApplyTo(baseConfig));

        Assert.True(effective.Machines.Single(m => m.Id == "amm580").Enabled);
        Assert.Equal(before, GroupFileCodec.ProfileHash(GroupProfile.Capture(profile.ApplyTo(baseConfig), TestPaths.ExampleRules, new Dictionary<string, string>())));
    }

    // ============================== JSON i zgodność ==============================

    [Fact]
    public void RoundTrip_KeepsTheSwitches_AndTheFingerprint_WithCanonicalKeyOrder()
    {
        var document = Document([On("zeta"), Off("alfa"), On("mid")]);

        string json = JsonSerializer.Serialize(document, SharedConfigDocument.CanonicalJsonOptions);
        var loaded = JsonSerializer.Deserialize<SharedConfigDocument>(json, SharedConfigDocument.CanonicalJsonOptions)!;

        Assert.Contains("\"machines\":{\"alfa\":{\"enabled\":false},\"mid\":{\"enabled\":true},\"zeta\":{\"enabled\":true}}", json);
        Assert.Equal(document.Hash, loaded.Hash);
        Assert.True(loaded.Machines!["zeta"].Enabled);
        Assert.False(loaded.Machines["alfa"].Enabled);
        // Ten sam zestaw w innej kolejności = ten sam odcisk (kanoniczna kolejność kluczy).
        Assert.Equal(document.Hash, Document([Off("alfa"), On("mid"), On("zeta")]).Hash);
        Assert.NotEqual(document.Hash, Document([On("alfa"), On("mid"), On("zeta")]).Hash);
        Assert.True(loaded.IsValid());
    }

    [Fact]
    public void DocumentWithoutMachines_SerializesExactlyLikeBefore()
    {
        string json = JsonSerializer.Serialize(Document(), SharedConfigDocument.CanonicalJsonOptions);

        Assert.DoesNotContain("machines", json, StringComparison.Ordinal);
    }

    private sealed record DocumentBeforeD3
    {
        public TimingConfig? Timing { get; init; }
        public OverlayConfig? Overlay { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public UpdateApproval? UpdateApproval { get; init; }
        public DateTimeOffset EditedAt { get; init; }
        public string EditedBy { get; init; } = "";
    }

    [Fact]
    public void OlderVersion_ReadsADocumentWithMachines_IgnoringTheUnknownField()
    {
        var document = Document([On("amm580")]) with { Timing = new TimingConfig { AlertSoundRepeatMs = 30000 } };
        string json = JsonSerializer.Serialize(document, SharedConfigDocument.CanonicalJsonOptions);

        var old = JsonSerializer.Deserialize<DocumentBeforeD3>(json, SharedConfigDocument.CanonicalJsonOptions)!;

        Assert.Equal(30000, old.Timing!.AlertSoundRepeatMs);
        Assert.Equal("PC-A", old.EditedBy);
        var next = JsonSerializer.Deserialize<SharedConfigDocument>(
            json.Replace("\"editedBy\"", "\"poleZPrzyszlosci\":{\"x\":1},\"editedBy\"", StringComparison.Ordinal),
            SharedConfigDocument.CanonicalJsonOptions)!;
        Assert.True(next.Machines!["amm580"].Enabled);
    }

    [Fact]
    public void StoreFileWithMachines_IsLoadedByTheOlderVersion_AndTheOlderFileByThisVersion()
    {
        Store.Save(null, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["amm580"] = true });
        string json = File.ReadAllText(Store.Path);
        Assert.Contains("\"machines\"", json);
        Assert.Contains("\"amm580\": {", json);

        using var file = JsonDocument.Parse(json);
        var old = JsonSerializer.Deserialize<DocumentBeforeD3>(file.RootElement.GetProperty("document").GetRawText(),
            SharedConfigDocument.CanonicalJsonOptions)!;
        Assert.Equal("PC-A", old.EditedBy);

        File.WriteAllText(Store.Path, "{ \"version\": 1, \"document\": { \"timing\": null, \"overlay\": null, \"editedAt\": \"2026-10-09T12:00:00+00:00\", \"editedBy\": \"PC-B\" } }");
        var loaded = Store.Load()!;
        Assert.Null(loaded.Machines);
        Assert.True(loaded.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a\nb")]
    public void MachineIdThatIsEmptyOrHasControlCharacters_IsNotValid(string id)
    {
        var document = Document() with { Machines = new Dictionary<string, SharedMachineSettings> { [id] = new() } };

        Assert.False(document.IsValid());
    }

    [Fact]
    public void TooManyMachines_IsNotValid_ButTheLimitItselfIs()
    {
        Assert.True(Document(Enumerable.Range(0, SharedConfigDocument.MaxMachines).Select(i => On("m" + i))).IsValid());
        Assert.False(Document(Enumerable.Range(0, SharedConfigDocument.MaxMachines + 1).Select(i => On("m" + i))).IsValid());
    }


    [Fact]
    public void DocumentWithMachines_WinsTheTieAgainstTheSameDocumentStrippedByAnOlderVersion()
    {
        var full = Document([On("amm580")]) with { Timing = new TimingConfig { AlertSoundRepeatMs = 30000 } };
        var stripped = full with { Machines = null };

        Assert.True(SharedConfigDocument.Wins(full, stripped));
        Assert.False(SharedConfigDocument.Wins(stripped, full));
        // Zgoda na aktualizację ma pierwszeństwo przed maszynami (kolejność kryteriów), a nowszy czas przed wszystkim.
        var approved = stripped with { UpdateApproval = new UpdateApproval { Version = "0.4.200.0", FileName = "MaszynyAlert-Setup-0.4.200.0.exe", ApprovedBy = "PC-A", ApprovedAt = Noon } };
        Assert.True(SharedConfigDocument.Wins(approved, full));
        Assert.True(SharedConfigDocument.Wins(stripped with { EditedAt = Noon.AddSeconds(1) }, full));
    }

    [Fact]
    public void TieBetweenTwoDocumentsWithMachines_IsStillResolvedByTheFingerprint()
    {
        var a = Document([On("amm580")]);
        var b = Document([Off("amm580")]);

        Assert.NotEqual(SharedConfigDocument.Wins(a, b), SharedConfigDocument.Wins(b, a));
    }


    [Fact]
    public void SaveWithMachines_KeepsTheRestOfTheFile_AndStoresTheCanonicalSnapshot()
    {
        Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, new OverlayConfig { Scale = 1.5 }, Noon, "PC-B");
        Store.SaveApproval(new UpdateApproval { Version = "0.4.148.0", FileName = "MaszynyAlert-Setup-0.4.148.0.exe", ApprovedBy = "PC-B", ApprovedAt = Noon }, Noon.AddMinutes(1), "PC-B");

        var saved = Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, new OverlayConfig { Scale = 1.5 }, Noon.AddMinutes(2), "PC-A",
            machines: new Dictionary<string, bool> { ["zeta"] = true, ["amm580"] = true, ["cameo1"] = false });

        var loaded = Store.Load()!;
        Assert.Equal(saved.Hash, loaded.Hash);
        Assert.Equal(["amm580", "cameo1", "zeta"], loaded.Machines!.Keys);
        Assert.True(loaded.Machines["amm580"].Enabled);
        Assert.False(loaded.Machines["cameo1"].Enabled);
        Assert.Equal("0.4.148.0", loaded.UpdateApproval!.Version);
        Assert.Equal("PC-A", loaded.EditedBy);
    }

    [Fact]
    public void SaveWithoutMachines_KeepsTheSwitchesFromTheFileUnderTheLock()
    {
        Store.Save(null, null, Noon, "PC-B", machines: new Dictionary<string, bool> { ["amm580"] = true });

        var afterTiming = Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon.AddMinutes(1), "PC-A");
        Assert.True(afterTiming.Machines!["amm580"].Enabled);
        var afterApproval = Store.SaveApproval(new UpdateApproval { Version = "0.4.150.0", FileName = "MaszynyAlert-Setup-0.4.150.0.exe", ApprovedBy = "PC-A", ApprovedAt = Noon }, Noon.AddMinutes(3), "PC-A");
        Assert.True(afterApproval.Machines!["amm580"].Enabled);
        Assert.Equal(30000, Store.Load()!.Timing!.AlertSoundRepeatMs);
    }

    [Fact]
    public void EmptyRestoreFromThePanel_KeepsTheSwitches_TheyAreNotPartOfTimingOrOverlay()
    {
        Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["amm580"] = true });

        var restored = Store.Save(null, null, Noon.AddMinutes(1), "PC-A");

        Assert.Null(restored.Timing);
        Assert.False(restored.IsEmpty);
        Assert.True(restored.Machines!["amm580"].Enabled);
    }

    [Fact]
    public void MergeOfANewerDocumentWithMachines_ReplacesTheSwitches()
    {
        Store.Save(null, null, Noon, "PC-A", machines: new Dictionary<string, bool> { ["amm580"] = true });

        Assert.True(Store.Merge(Document([Off("amm580")], host: "PC-B", at: Noon.AddMinutes(1))));

        Assert.False(Store.Load()!.Machines!["amm580"].Enabled);
        Assert.False(Store.Merge(Document([On("amm580")], host: "PC-C", at: Noon)));
        Assert.False(Store.Load()!.Machines!["amm580"].Enabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
