using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class ProfileHashCompatibilityTests
{
    private static string FixtureDir => Path.Combine(TestPaths.RepositoryRoot, "tests", "MaszynyAlert.Core.Tests",
        "Config", "OdciskProfilu-0.4.79");

    public static TheoryData<string, string> GoldenProfiles()
    {
        var data = new TheoryData<string, string>();
        foreach (string line in File.ReadAllLines(Path.Combine(FixtureDir, "odciski.txt")).Where(l => l.Length > 0))
        {
            string[] parts = line.Split(' ');
            data.Add(parts[0], parts[1]);
        }
        return data;
    }

    /// <summary>
    /// Stałe, które są też poprawnym plikiem grupy. <c>domyslne-wartosci</c> ma puste obszary ekranu (sam test wartości
    /// domyślnych odcisku), więc walidacja pliku grupy go odrzuca — sprawdza go tylko test złoty (i).
    /// </summary>
    public static TheoryData<string, string> GroupFileProfiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in GoldenProfiles().Where(row => (string)row[0] != "domyslne-wartosci"))
            data.Add((string)row[0], (string)row[1]);
        return data;
    }

    private static GroupProfile LoadFixture(string name) => JsonSerializer.Deserialize<GroupProfile>(
        File.ReadAllBytes(Path.Combine(FixtureDir, name + ".json")), GroupFileCodec.Options)!;

    [Theory]
    [MemberData(nameof(GoldenProfiles))]
    public void ProfileHash_EqualsHashComputedByRelease0479(string fixture, string hash0479)
    {
        Assert.Equal(hash0479, GroupFileCodec.ProfileHash(LoadFixture(fixture)));
    }

    /// <summary>Ten sam wymóg na drodze, którą odcisk idzie do sieci: zaproszenie zapisane i wczytane z powrotem.</summary>
    [Theory]
    [MemberData(nameof(GroupFileProfiles))]
    public void GroupFileRoundTrip_CarriesHashOfRelease0479(string fixture, string hash0479)
    {
        var file = GroupFile.Create("Hala", LoadFixture(fixture));
        Assert.Equal(hash0479, file.Identity.ProfileHash);
        Assert.Equal(hash0479, GroupFileCodec.Parse(GroupFileCodec.Serialize(file)).Identity.ProfileHash);
    }

    [Fact]
    public void GoldenProfiles_HaveDistinctHashes()
    {
        var hashes = GoldenProfiles().Select(row => (string)row[1]).ToArray();
        Assert.Equal(5, hashes.Length);
        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Lista pól zamrożonych w kodzie to dokładnie lista pól, którą wypisał kod 0.4.79.</summary>
    [Fact]
    public void FrozenFieldList_IsExactlyTheFieldListOfRelease0479()
    {
        var fromRelease = File.ReadAllLines(Path.Combine(FixtureDir, "pola.txt")).Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(127, fromRelease.Count);
        Assert.True(fromRelease.SetEquals(ProfileHashSchema.Fields0479),
            "Zamrożona lista pól odcisku różni się od listy z 0.4.79: "
            + string.Join(", ", fromRelease.Except(ProfileHashSchema.Fields0479)
                .Concat(ProfileHashSchema.Fields0479.Except(fromRelease))));
        Assert.Contains("MachineConfig.rsaAesKeySha256", ProfileHashSchema.Fields0479);
    }

    [Fact]
    public void EveryProfileField_IsFrozenOrRegisteredWithDefault()
    {
        var current = ProfileHashSchema.FieldsOf(typeof(GroupProfile));
        var undecided = current.Except(ProfileHashSchema.Fields0479)
            .Except(ProfileHashSchema.AddedAfter0479.Keys).ToArray();
        Assert.True(undecided.Length == 0,
            "Nowe pole profilu grupy bez decyzji o odcisku: " + string.Join(", ", undecided)
            + ". Dopisz je do ProfileHashSchema.AddedAfter0479 z wartością domyślną (JSON) — wtedy komputer z nowszą"
            + " wersją liczy przy wartości domyślnej ten sam odcisk co 0.4.79.");
        var removed = ProfileHashSchema.Fields0479.Except(current).ToArray();
        Assert.True(removed.Length == 0,
            "Usunięto albo przemianowano pole znane 0.4.79: " + string.Join(", ", removed)
            + ". To zmienia odcisk względem hali — wymaga decyzji właściciela i wymiany wersji na wszystkich komputerach.");
        var stale = ProfileHashSchema.AddedAfter0479.Keys.Except(current).ToArray();
        Assert.True(stale.Length == 0, "Wpis w AddedAfter0479 bez pola w typach: " + string.Join(", ", stale));
        Assert.Empty(ProfileHashSchema.AddedAfter0479.Keys.Intersect(ProfileHashSchema.Fields0479));
        foreach (var (field, json) in ProfileHashSchema.AddedAfter0479)
            using (JsonDocument.Parse(json)) Assert.False(string.IsNullOrWhiteSpace(field));
    }

    [Fact]
    public void TypesInProfileGraph_HaveUniqueNames()
    {
        var names = ProfileHashSchema.TypesOf(typeof(GroupProfile)).GroupBy(t => t.Name).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToArray();
        Assert.Empty(names);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Test przyszłościowy (ii): „wersja 2” dokłada pola do typu z „wersji 1”. Typy testowe mają te same nazwy
    // (Profil, Maszyna), więc klucze pól są takie, jak przy prawdziwej zmianie typu w kolejnej wersji programu.
    // ---------------------------------------------------------------------------------------------------------

    private static class Wersja1
    {
        public sealed record Profil { public required List<Maszyna> Maszyny { get; init; } }
        public sealed record Maszyna { public required string Id { get; init; } public string? Notatka { get; init; } }
    }

    private static class Wersja2
    {
        public sealed record Profil { public required List<Maszyna> Maszyny { get; init; } }
        public sealed record Maszyna
        {
            public required string Id { get; init; }
            public string? Notatka { get; init; }
            public string? NowyOdcisk { get; init; }
            public bool NowaFlaga { get; init; }
            public Sekcja? NowaSekcja { get; init; }
        }
        public sealed record Sekcja { public int Prog { get; init; } = 5; }
    }

    private static readonly ProfileHashSchema SchemaV1 = new(ProfileHashSchema.FieldsOf(typeof(Wersja1.Profil)),
        new Dictionary<string, string>());

    private static readonly ProfileHashSchema SchemaV2 = new(ProfileHashSchema.FieldsOf(typeof(Wersja1.Profil)),
        new Dictionary<string, string>
        {
            ["Maszyna.nowyOdcisk"] = "null", ["Maszyna.nowaFlaga"] = "false", ["Maszyna.nowaSekcja"] = "null",
            ["Sekcja.prog"] = "5",
        });

    private static Wersja1.Profil V1() => new() { Maszyny = [new() { Id = "a", Notatka = null }, new() { Id = "b", Notatka = "x" }] };

    private static Wersja2.Profil V2(Func<Wersja2.Maszyna, Wersja2.Maszyna>? change = null) => new()
    {
        Maszyny = [new() { Id = "a", Notatka = null }, change?.Invoke(new() { Id = "b", Notatka = "x" }) ?? new() { Id = "b", Notatka = "x" }]
    };

    [Fact]
    public void NewFieldAtDefault_KeepsHashOfOlderVersion()
    {
        Assert.Equal(GroupFileCodec.Fingerprint(V1(), SchemaV1), GroupFileCodec.Fingerprint(V2(), SchemaV2));
    }

    [Fact]
    public void NewFieldSet_ChangesHash_SoSettingsChangeIsStillDetected()
    {
        string old = GroupFileCodec.Fingerprint(V1(), SchemaV1);
        Assert.NotEqual(old, GroupFileCodec.Fingerprint(V2(m => m with { NowyOdcisk = new string('a', 64) }), SchemaV2));
        Assert.NotEqual(old, GroupFileCodec.Fingerprint(V2(m => m with { NowaFlaga = true }), SchemaV2));
        Assert.NotEqual(old, GroupFileCodec.Fingerprint(V2(m => m with { NowaSekcja = new() }), SchemaV2));
        // Dwie różne wartości ustawione = dwa różne odciski.
        Assert.NotEqual(GroupFileCodec.Fingerprint(V2(m => m with { NowaSekcja = new() { Prog = 5 } }), SchemaV2),
            GroupFileCodec.Fingerprint(V2(m => m with { NowaSekcja = new() { Prog = 6 } }), SchemaV2));
    }

    [Fact]
    public void NewFieldWithoutRule_ChangesHash_WhichIsTheU02Defect()
    {
        var noRule = new ProfileHashSchema(ProfileHashSchema.FieldsOf(typeof(Wersja2.Profil)), new Dictionary<string, string>());
        Assert.NotEqual(GroupFileCodec.Fingerprint(V1(), SchemaV1), GroupFileCodec.Fingerprint(V2(), noRule));
    }

    /// <summary>Pola zamrożone zachowują postać z 0.4.79 także wtedy, gdy są null (tak jak „rsaAesKeySha256”: null).</summary>
    [Fact]
    public void FrozenNullField_StaysInHash()
    {
        var withNote = new ProfileHashSchema(ProfileHashSchema.FieldsOf(typeof(Wersja1.Profil)), new Dictionary<string, string>());
        var dropNote = new ProfileHashSchema(new HashSet<string>(ProfileHashSchema.FieldsOf(typeof(Wersja1.Profil))
            .Where(f => f != "Maszyna.notatka"), StringComparer.Ordinal), new Dictionary<string, string> { ["Maszyna.notatka"] = "null" });
        Assert.NotEqual(GroupFileCodec.Fingerprint(V1(), withNote), GroupFileCodec.Fingerprint(V1(), dropNote));
    }
}
