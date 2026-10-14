using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class GroupFileTests
{
    internal static GroupFile Invitation() => GroupFile.Create("Hala — Aidite", GroupProfile.Capture(
        TestPaths.ExampleConfig, ConfigLoader.LoadRules(TestPaths.RulesExample),
        new Dictionary<string, string> { ["code:705"] = "Sprawdź alarm osi 6." }));

    [Fact]
    public void RoundTrip_PreservesActiveMachinesRulesAndInstructions()
    {
        var source = Invitation();
        var copy = GroupFileCodec.Parse(GroupFileCodec.Serialize(source));
        Assert.Equal(source.Identity, copy.Identity);
        Assert.Equal(source.NetworkKey, copy.NetworkKey);
        Assert.Equal(7, copy.Profile.Machines.Count(m => m.Enabled));
        Assert.Equal(5, copy.Profile.Machines.Count(m => !m.Enabled));
        Assert.Equal(
            new[] { "amm520-1", "amm520-2", "premile", "cameo1" },
            copy.Profile.Machines.Where(m => !m.RequiresPassword).Select(m => m.Id).ToArray());
        Assert.Equal(source.Profile.Rules.Codes.Count, copy.Profile.Rules.Codes.Count);
        Assert.Equal("Sprawdź alarm osi 6.", copy.Profile.Instructions["code:705"]);
    }

    [Fact]
    public void Export_DoesNotCarryLocalPreferencesPathsOrAdminSecrets()
    {
        var local = TestPaths.ExampleConfig with
        {
            Enabled = false, RulesFile = "LOCAL-RULE-PATH.json",
            Paths = TestPaths.ExampleConfig.Paths with { LogsDir = "LOCAL-LOG-PATH" },
            Ui = TestPaths.ExampleConfig.Ui with { WindowsNotificationsEnabled = false }
        };
        var file = GroupFile.Create("Hala", GroupProfile.Capture(local,
            ConfigLoader.LoadRules(TestPaths.RulesExample), new Dictionary<string, string>()));
        var json = Encoding.UTF8.GetString(GroupFileCodec.Serialize(file));
        Assert.DoesNotContain("LOCAL-", json);
        foreach (string field in new[] { "rulesFile", "paths", "ui", "secrets", "password" })
        {
            Assert.False(JsonNode.Parse(json)!.AsObject().ContainsKey(field));
        }
        var applied = file.Profile.ApplyTo(local);
        Assert.False(applied.Enabled);
        Assert.Equal(local.Paths, applied.Paths);
        Assert.Equal(local.RulesFile, applied.RulesFile);
        Assert.Equal(local.Ui, applied.Ui);
        Assert.Equal(local.Cluster, applied.Cluster);
        Assert.DoesNotContain(file.NetworkKey, file.ToString());
    }

    [Theory]
    [InlineData("schemaVersion", "99")]
    [InlineData("identity.protocolVersion", "1")]
    [InlineData("identity.groupId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("identity.keyId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("identity.profileRevision", "0")]
    [InlineData("identity.profileHash", "\"broken\"")]
    [InlineData("networkKey", "\"PRIVATE-INVALID-KEY\"")]
    [InlineData("udpPort", "65536")]
    [InlineData("peers", "[\"224.0.0.1\"]")]
    [InlineData("profile.rules", "null")]
    [InlineData("profile.rules.rules", "[null]")]
    [InlineData("profile.rules.codes", "[null]")]
    [InlineData("profile.instructions", "{\"code:705\":null}")]
    [InlineData("profile.profiles.opencnc.mainScreen.anchorLabels", "[null]")]
    [InlineData("profile.profiles.amm580.mainScreen.otherScreenLabels", "null")]
    [InlineData("profile.profiles.amm580.mainScreen.otherScreenLabels", "[[\"Alarm\", \"\"]]")]
    [InlineData("profile.timing.reconnectBackoffMs", "null")]
    [InlineData("profile.profiles.opencnc.statusBarRoi", "{\"x\":0,\"y\":0,\"width\":10,\"height\":10,\"path\":0}")]
    [InlineData("profile.profiles.opencnc.statusBarRoi.width", "\"PRIVATE-INVALID-TEXT\"")]
    [InlineData("profile.profiles.opencnc.red.minSaturation", "2")]
    [InlineData("profile.profiles.opencnc.ocrScale", "99999")]
    [InlineData("profile.timing.idleAlertAfterMs", "1")]
    [InlineData("profile.rules.databases", "null")]
    [InlineData("profile.rules.databases.amm580", "null")]
    [InlineData("profile.rules.databases.amm580.rules", "null")]
    [InlineData("profile.rules.databases.amm580.codes", "null")]
    [InlineData("profile.rules.databases.amm580.codeIdPrefix", "\"PRIVATE-0123456789012345678901234567890123456789012345678901234567890123456789\"")]
    [InlineData("profile.profiles.amm580.programState.abortWords", "null")]
    [InlineData("profile.profiles.amm580.programState.abortWords", "[\"PRIVATE-0123456789012345678901234567890123456789012345678901234567890123456789\"]")]
    [InlineData("profile.profiles.amm580.programState.runFill", "null")]
    [InlineData("profile.profiles.amm580.programState.boxRoi.width", "0")]
    [InlineData("profile.profiles.amm580.programState.abortPattern.rows", "[null]")]
    [InlineData("profile.profiles.amm580.programState.abortPattern.rows", "[\"#.\",\"#\"]")]
    [InlineData("profile.profiles.amm580.programState.abortPattern.rows", "[\"PRIVATE-\"]")]
    [InlineData("profile.profiles.amm580.programState.abortPattern.maxShift", "9")]
    [InlineData("profile.profiles.amm580.programState.abortPattern.x", "500")]
    public void InvalidImport_IsRejectedWithoutEchoingContent(string field, string value)
    {
        var json = JsonNode.Parse(GroupFileCodec.Serialize(Invitation()))!;
        JsonNode parent = json;
        var parts = field.Split('.');
        foreach (var part in parts[..^1]) parent = parent[part]!;
        parent[parts[^1]] = JsonNode.Parse(value);
        var error = Assert.Throws<InvalidDataException>(() => GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.DoesNotContain("PRIVATE-", error.ToString());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("paths")]
    [InlineData("secrets")]
    [InlineData("adminPasswordHash")]
    public void UnknownFields_AreRejectedAtEveryObjectLevel(string field)
    {
        foreach (string parentPath in new[] { "", "profile", "profile.profiles.opencnc" })
        {
            var json = JsonNode.Parse(GroupFileCodec.Serialize(Invitation()))!;
            JsonNode parent = json;
            foreach (var part in parentPath.Split('.', StringSplitOptions.RemoveEmptyEntries)) parent = parent[part]!;
            parent[field] = "PRIVATE-FORBIDDEN";
            Assert.Throws<InvalidDataException>(() => GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
    }

    [Fact]
    public void DuplicatePropertiesAndMissingRequiredFields_AreRejected()
    {
        string json = Encoding.UTF8.GetString(GroupFileCodec.Serialize(Invitation()));
        string duplicate = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1");
        Assert.NotEqual(json, duplicate);
        Assert.Throws<InvalidDataException>(() => GroupFileCodec.Parse(Encoding.UTF8.GetBytes(duplicate)));
        var document = JsonNode.Parse(json)!;
        document.AsObject().Remove("networkKey");
        Assert.Throws<InvalidDataException>(() => GroupFileCodec.Parse(Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Fact]
    public void OversizedNonSeekableStream_IsRejectedAfterReadingAtMostLimitPlusOne()
    {
        using var input = new NonSeekableStream(new byte[GroupFileCodec.MaxFileBytes + 50000]);
        Assert.Throws<InvalidDataException>(() => GroupFileCodec.Read(input));
        Assert.Equal(GroupFileCodec.MaxFileBytes + 1, input.BytesRead);
    }

    [Fact]
    public void InvitationFromOlderProgramVersion_IsAccepted_WithFingerprintRecomputed()
    {
        var json = JsonNode.Parse(GroupFileCodec.Serialize(Invitation()))!;
        string olderHash = OlderVersionProfileHash(json["profile"]!, FieldsAddedOn15092026);
        json["identity"]!["profileHash"] = olderHash;
        foreach (string added in FieldsAddedOn15092026)
            Assert.True(json["profile"]!["timing"]!.AsObject().Remove(added), added);

        var file = GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Equal(7, file.Profile.Machines.Count(m => m.Enabled));
        Assert.Equal(180000, file.Profile.Timing.IdleAtZeroAfterMs); // domyślna wartość nowej wersji
        Assert.NotEqual(olderHash, file.Identity.ProfileHash);
        Assert.Equal(GroupFileCodec.ProfileHash(file.Profile), file.Identity.ProfileHash);
        // Reszta tożsamości grupy zostaje bez zmian — to ta sama grupa, nie nowa.
        Assert.Equal(Invitation().Identity.ProfileRevision, file.Identity.ProfileRevision);
    }

    /// <summary>
    /// Odcisk profilu leży w tym samym pliku, co profil, więc nie jest podpisem i niczego nie chroni —
    /// bierzemy go z profilu. Odcisk w złym FORMACIE nadal odrzuca <c>GroupIdentity.Validate</c>
    /// (przypadek „identity.profileHash” w <see cref="InvalidImport_IsRejectedWithoutEchoingContent"/>).
    /// </summary>
    [Fact]
    public void ForeignButWellFormedFingerprint_IsReplacedByTheComputedOne()
    {
        var json = JsonNode.Parse(GroupFileCodec.Serialize(Invitation()))!;
        json["identity"]!["profileHash"] = new string('a', 64);
        var file = GroupFileCodec.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Equal(GroupFileCodec.ProfileHash(file.Profile), file.Identity.ProfileHash);
        // Zapis oddaje odcisk wyliczony, więc plik naprawia się przy pierwszym ponownym eksporcie.
        Assert.Equal(file.Identity.ProfileHash,
            GroupFileCodec.Parse(GroupFileCodec.Serialize(file)).Identity.ProfileHash);
    }

    [Fact]
    public void ProfileHash_IgnoresDictionaryOrderAndInstructionsButDetectsDetectionChanges()
    {
        var original = Invitation().Profile;
        var reordered = original with
        { Profiles = original.Profiles.Reverse().ToDictionary(p => p.Key, p => p.Value) };
        Assert.Equal(GroupFileCodec.ProfileHash(original), GroupFileCodec.ProfileHash(reordered));
        var changed = original with { Timing = original.Timing with { EpisodeCloseAfterMs = 40000 } };
        Assert.NotEqual(GroupFileCodec.ProfileHash(original), GroupFileCodec.ProfileHash(changed));

        var withInstructions = original with { Instructions = new() { ["code:705"] = "Nowa instrukcja." } };
        Assert.Equal(GroupFileCodec.ProfileHash(original), GroupFileCodec.ProfileHash(withInstructions));

        var identity = Invitation().Identity;
        Assert.Equal(GroupCompatibility.Compatible, identity.Compare(identity with { }));
    }

    [Fact]
    public void ProfileUpdateAndKeyRotation_HaveSeparateIdentities()
    {
        var original = Invitation();
        var updated = original.UpdateProfile(original.Profile with
        { Timing = original.Profile.Timing with { EpisodeCloseAfterMs = 40000 } });
        Assert.Equal(original.Identity.GroupId, updated.Identity.GroupId);
        Assert.Equal(original.Identity.KeyId, updated.Identity.KeyId);
        Assert.Equal(original.NetworkKey, updated.NetworkKey);
        Assert.Equal(2, updated.Identity.ProfileRevision);
        var rotated = updated.RotateKey();
        Assert.Equal(updated.Identity.GroupId, rotated.Identity.GroupId);
        Assert.Equal(updated.Identity.ProfileHash, rotated.Identity.ProfileHash);
        Assert.NotEqual(updated.Identity.KeyId, rotated.Identity.KeyId);
        Assert.NotEqual(updated.NetworkKey, rotated.NetworkKey);
    }

    internal static readonly string[] FieldsAddedOn15092026 =
        ["sameMessageRejoinMs", "idleAtZeroAfterMs", "mainScreenOcrGraceMs"];

    /// <summary>
    /// Odcisk profilu tak, jak policzyłaby go WCZEŚNIEJSZA wersja programu: nad kształtem profilu bez
    /// wskazanych pól sekcji <c>timing</c>. Kanonizacja jest tu powtórzona celowo — test nie może liczyć
    /// odcisku „przed zmianą” tym samym kodem, który sprawdza.
    /// </summary>
    internal static string OlderVersionProfileHash(JsonNode profile, params string[] removedTimingFields)
    {
        var older = JsonNode.Parse(profile.ToJsonString())!;
        older["instructions"] = new JsonObject();
        foreach (string field in removedTimingFields)
            Assert.True(older["timing"]!.AsObject().Remove(field), field);
        using var document = JsonDocument.Parse(older.ToJsonString());
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, document.RootElement);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        public int BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int length = Math.Min(count, bytes.Length - BytesRead);
            Array.Copy(bytes, BytesRead, buffer, offset, length);
            BytesRead += length;
            return length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
