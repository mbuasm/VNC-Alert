using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class GroupMixedVersionGoldenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("klucz-testowy-grupy"));

    /// <summary>Tożsamość grupy zapisana w złotych datagramach (generator: <c>VersionTools.Group</c>).</summary>
    private static readonly GroupIdentity Group = new()
    {
        GroupId = Guid.Parse("11111111-2222-3333-4444-555555555555"), GroupName = "Hala",
        KeyId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), ProtocolVersion = GroupIdentity.CurrentProtocolVersion,
        ProfileRevision = 3, ProfileHash = new string('c', 64),
    };

    private static string Dir => Path.Combine(TestPaths.RepositoryRoot, "tests", "MaszynyAlert.Core.Tests", "Cluster", "GrupaMieszana-0.4.148");

    /// <summary>Datagramy z <c>datagramy.txt</c> (Base64 — plik tekstowy, bez ryzyka konwersji końców linii przez git).</summary>
    internal static IReadOnlyDictionary<string, byte[]> Datagrams { get; } = File.ReadAllLines(Path.Combine(Dir, "datagramy.txt"))
        .Where(line => line.Length > 0 && !line.StartsWith('#')).Select(line => line.Split(' '))
        .ToDictionary(parts => parts[0], parts => Convert.FromBase64String(parts[1]), StringComparer.Ordinal);

    internal static byte[] Golden(string name) => Datagrams[name];

    private static string[] NamesOf0148() => Datagrams.Keys.Where(name => !name.EndsWith(".0.4.249", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToArray();

    /// <summary>Base64 odpowiada dokładnie bajtom, które zapisał harness (sumy SHA-256 i rozmiary z <c>sumy.txt</c>).</summary>
    [Fact]
    public void Datagramy_ZgadzajaSieZSumamiZHarnessu()
    {
        var sums = File.ReadAllLines(Path.Combine(Dir, "sumy.txt")).Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ')).ToDictionary(parts => parts[2], parts => (Sha: parts[0], Size: int.Parse(parts[1])), StringComparer.Ordinal);
        Assert.Equal(18, Datagrams.Count);
        Assert.Equal(sums.Keys.Order(StringComparer.Ordinal), Datagrams.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in Datagrams)
        {
            Assert.Equal(sums[name].Size, bytes.Length);
            Assert.Equal(sums[name].Sha, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    public static TheoryData<string> DatagramsOf0148()
    {
        var data = new TheoryData<string>();
        foreach (string name in NamesOf0148()) data.Add(name);
        return data;
    }

    [Fact]
    public void ZestawZlotych_ObejmujeKazdyTypKomunikatuGrupy()
    {
        string[] names = NamesOf0148();
        var types = names.Select(name => Decode(Golden(name)).GetType()).ToHashSet();
        var all = typeof(PeerMessage).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(PeerMessage)) && !t.IsAbstract).ToHashSet();
        Assert.Equal(17, names.Length);
        Assert.True(all.SetEquals(types), "Typy bez złotego datagramu 0.4.148: " + string.Join(", ", all.Except(types).Select(t => t.Name)));
    }

    /// <summary>(i) Każdy komunikat 0.4.148 ta wersja przyjmuje i koduje z powrotem bajt w bajt (ten sam podpis HMAC); dokument wspólnej konfiguracji — bez pola, którego ta wersja nie zna.</summary>
    [Theory]
    [MemberData(nameof(DatagramsOf0148))]
    public void DatagramZ0_4_148_TaWersjaCzytaIKodujeBajtWBajt(string name)
    {
        byte[] golden = Golden(name);

        PeerMessage message = Decode(golden);
        byte[] encoded = MessageCodec.Encode(message, new HmacSigner(Key));

        if (name != "13-sharedConfig")
        {
            Assert.Equal(golden, encoded);
            return;
        }
        JsonObject before = Json(golden), after = Json(encoded);
        JsonObject document = before["document"]!.AsObject();
        string[] skipped = document.Select(p => p.Key).Except(after["document"]!.AsObject().Select(p => p.Key)).ToArray();
        Assert.Single(skipped);
        document.Remove(skipped[0]);
        Assert.True(JsonNode.DeepEquals(before, after));
    }

    private static JsonObject Json(byte[] datagram) => JsonNode.Parse(datagram.AsSpan(HmacSigner.SealOverheadBytes))!.AsObject();

    [Fact]
    public void DokumentD3_TaWersja_DokladaDoFormatu0_4_148_TylkoPoleMachines()
    {
        JsonNode Payload(string name)
        {
            byte[] bytes = Golden(name);
            var node = JsonNode.Parse(bytes.AsSpan(HmacSigner.SealOverheadBytes))!.AsObject();
            node.Remove("seq"); node.Remove("sentAt");
            return node;
        }

        var withMachines = Payload("13b-sharedConfig-machines.0.4.249");
        var document = withMachines["document"]!.AsObject();
        Assert.Equal("{\"amm580\":{\"enabled\":true},\"premile\":{\"enabled\":false}}", document["machines"]!.ToJsonString());
        document.Remove("machines");

        Assert.True(JsonNode.DeepEquals(Payload("13-sharedConfig"), withMachines));
    }

    [Fact]
    public async Task PorcjeObserwacji0_4_148_WOdwrotnejKolejnosci_WszystkieTrafiajaDoDziennika()
    {
        using var dir = new TempDir("grupa-mieszana");
        var clock = new FakeClock(T0);
        var hub = new InMemoryPeerHub(new HmacSigner(Key), clock);
        var oldPc = hub.CreateBus("PC-A");
        var journal = new ObservationJournal(dir.File("observation.jsonl"));
        await using var node = new GroupClusterNode(new ClusterConfig { Enabled = true, Group = Group }, hub.CreateBus("PC-B"), clock, "test")
            { Observation = journal };
        node.SetReadiness(new(true, false, []));
        await node.StartAsync(default);

        hub.Dispatch(oldPc, Golden("01-heartbeat"));
        foreach (string name in new[] { "15-observationReply-3", "15-observationReply-2", "15-observationReply-1" })
            hub.Dispatch(oldPc, Golden(name));

        var expected = Enumerable.Range(0, 6).Select(n => new ObservationInterval("aidite3", ObservationState.Observed,
            T0.AddDays(-2).AddMinutes(10 * n), T0.AddDays(-2).AddMinutes(10 * n + 9))).ToArray();
        Assert.Equal(expected, journal.Snapshot().OrderBy(i => i.Start).ToArray());
    }

    private static PeerMessage Decode(byte[] datagram)
    {
        Assert.True(MessageCodec.TryDecode(datagram, new HmacSigner(Key), new FakeClock(T0), TimeSpan.FromSeconds(300),
            out var message, out string? reason), reason);
        return message!;
    }
}
