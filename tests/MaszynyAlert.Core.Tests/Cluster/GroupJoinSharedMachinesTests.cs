using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class GroupJoinSharedMachinesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, bool> Station520580() => TestPaths.ExampleConfig.Machines
        .ToDictionary(m => m.Id, m => m.Id is "amm580" or "amm520-2", StringComparer.Ordinal);

    private static string Enabled(SharedConfigDocument? document) => string.Join(",",
        (document?.ApplyTo(TestPaths.ExampleConfig) ?? TestPaths.ExampleConfig).Machines.Where(m => m.Enabled).Select(m => m.Id)
        .Order(StringComparer.Ordinal));

    private const string HallMachines = "aidite1,aidite2,aidite3,aidite4,aidite5,aidite6,aidite7";
    private const string StationMachines = "amm520-2,amm580";

    // ============================== Magazyn: zdjęcie i oddanie przełączników ==============================

    [Fact]
    public void DropMachines_ZdejmujeTylkoPrzelaczniki_CzasAutorIResztaDokumentuBezZmian()
    {
        using var dir = new TempDir("d3-dolaczenie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, new OverlayConfig { Scale = 1.5 }, T0, "STANOWISKO-520", Station520580());
        store.SaveApproval(new UpdateApproval
        {
            Version = "0.4.250.0", FileName = "VncAlert-Setup-0.4.250.0.exe", ApprovedBy = "STANOWISKO-520", ApprovedAt = T0,
        }, T0.AddMinutes(1), "STANOWISKO-520");
        SharedConfigDocument saved = store.Load()!;

        SharedConfigDocument? before = store.DropMachines();

        Assert.NotNull(before);
        Assert.Equal(saved.Hash, before!.Hash);
        SharedConfigDocument after = store.Load()!;
        Assert.Null(after.Machines);
        Assert.Equal((saved with { Machines = null }).Hash, after.Hash);
        Assert.Equal(T0.AddMinutes(1), after.EditedAt);
        Assert.Equal("STANOWISKO-520", after.EditedBy);
        Assert.Equal(30000, after.Timing!.AlertSoundRepeatMs);
        Assert.Equal("0.4.250.0", after.UpdateApproval!.Version);
        Assert.False(SharedConfigDocument.Wins(after, saved));
    }

    [Fact]
    public void DropMachines_BezPlikuAlboBezPrzelacznikow_NiczegoNieZapisujeAniNieZaklada()
    {
        using var dir = new TempDir("d3-dolaczenie");
        string path = dir.File(Path.Combine("shared-config", "shared-config.json"));
        var store = new SharedConfigStore(path);

        Assert.Null(store.DropMachines());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, T0, "PC-A");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Null(store.DropMachines());
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void RestoreMachines_NicSieWMiedzyczasieNieZmienilo_PlikBajtWBajtJakPrzedZdjeciem()
    {
        using var dir = new TempDir("d3-dolaczenie");
        string path = dir.File("shared-config.json");
        var store = new SharedConfigStore(path);
        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, T0, "STANOWISKO-520", machines: Station520580());
        byte[] original = File.ReadAllBytes(path);

        SharedConfigDocument before = store.DropMachines()!;
        Assert.NotEqual(original, File.ReadAllBytes(path));
        Assert.True(store.RestoreMachines(before));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(store.RestoreMachines(before));
    }

    [Fact]
    public void RestoreMachines_DokumentNowejGrupyWMiedzyczasie_ZostajeIWracajaTylkoPrzelaczniki()
    {
        using var dir = new TempDir("d3-dolaczenie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(null, null, T0.AddDays(-1), "STANOWISKO-520", machines: Station520580());
        SharedConfigDocument before = store.DropMachines()!;
        Assert.True(store.Merge(new SharedConfigDocument
        {
            Timing = new TimingConfig { AlertSoundRepeatMs = 25000 }, EditedAt = T0, EditedBy = "PC-HALA-1",
        }));

        Assert.True(store.RestoreMachines(before));

        SharedConfigDocument after = store.Load()!;
        Assert.Equal(T0, after.EditedAt);
        Assert.Equal("PC-HALA-1", after.EditedBy);
        Assert.Equal(25000, after.Timing!.AlertSoundRepeatMs);
        Assert.Equal(StationMachines, Enabled(after));
    }

    [Fact]
    public void RestoreMachines_PlikuNieMa_WracaCalyDokumentSprzedZmianyGrupy()
    {
        using var dir = new TempDir("d3-dolaczenie");
        string path = dir.File("shared-config.json");
        var store = new SharedConfigStore(path);
        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, T0, "STANOWISKO-520", machines: Station520580());
        SharedConfigDocument before = store.DropMachines()!;
        File.Delete(path);

        Assert.True(store.RestoreMachines(before));

        Assert.Equal(before.Hash, store.Load()!.Hash);
    }

    // ============================== Kiedy zmiana grupy zdejmuje przełączniki ==============================

    [Fact]
    public void JoinsAnotherGroup_TylkoWejscieDoWlaczonejGrupyOInnymIdentyfikatorze()
    {
        GroupIdentity a = GroupFileTests.Invitation().Identity, b = GroupFileTests.Invitation().Identity;
        var solo = new ClusterConfig();
        var legacy = new ClusterConfig { Enabled = true };
        var inA = new ClusterConfig { Enabled = true, Group = a };

        Assert.True(ClusterRuntime.JoinsAnotherGroup(solo, inA));                                  // praca samodzielna → grupa
        Assert.True(ClusterRuntime.JoinsAnotherGroup(legacy, inA));                                // stary tryb bez grupy → grupa
        Assert.True(ClusterRuntime.JoinsAnotherGroup(inA, inA with { Group = b }));                // inna grupa (nowe zaproszenie)
        Assert.True(ClusterRuntime.JoinsAnotherGroup(inA with { Enabled = false, Group = b }, inA));
        Assert.False(ClusterRuntime.JoinsAnotherGroup(inA, inA with { Priority = 7 }));            // ustawienia tej samej grupy
        Assert.False(ClusterRuntime.JoinsAnotherGroup(inA, inA with
        {
            Group = a with { KeyId = Guid.NewGuid(), ProfileRevision = a.ProfileRevision + 1 },     // klucz i generacja profilu tej samej
        }));
        Assert.False(ClusterRuntime.JoinsAnotherGroup(inA, inA with { Enabled = false }));         // wyłączenie współpracy
        Assert.False(ClusterRuntime.JoinsAnotherGroup(inA with { Enabled = false }, inA));         // ponowne włączenie tej samej
        Assert.False(ClusterRuntime.JoinsAnotherGroup(inA, solo));                                 // wyjście z grupy
        Assert.False(ClusterRuntime.JoinsAnotherGroup(solo, inA with { Enabled = false }));        // grupa zapisana, ale wyłączona
    }

    // ============================== ClusterRuntime: kolejność, cofanie, ta sama grupa ==============================

    /// <summary>Warstwa wspólna z zapisem wywołań zdejmowania/oddawania przełączników do wspólnego dziennika zdarzeń.</summary>
    private sealed class RecordingSync(SharedConfigSync inner, List<string> log) : ISharedConfigSync
    {
        public string? Hash => inner.Hash;
        public DateTimeOffset? EditedAt => inner.EditedAt;
        public SharedConfigDocument? Current => inner.Current;
        public bool Apply(SharedConfigDocument document) => inner.Apply(document);
        public SharedConfigDocument? DetachMachines() { log.Add("detach"); return inner.DetachMachines(); }
        public void RestoreMachines(SharedConfigDocument before) { log.Add("restore"); inner.RestoreMachines(before); }
    }

    /// <summary>Magistrala grupy z zapisem startu i zatrzymania do wspólnego dziennika zdarzeń.</summary>
    private sealed class RecordingBus(IPeerBus inner, string label, List<string> log) : IPeerBus
    {
        public string LocalHost => inner.LocalHost;
        public event Action<PeerMessage>? Received { add => inner.Received += value; remove => inner.Received -= value; }
        public event Action<string>? Rejected { add => inner.Rejected += value; remove => inner.Rejected -= value; }
        public Task StartAsync(CancellationToken ct) { log.Add("start:" + label); return inner.StartAsync(ct); }
        public Task SendAsync(PeerMessage msg, CancellationToken ct) => inner.SendAsync(msg, ct);
        public async ValueTask DisposeAsync() { await inner.DisposeAsync(); log.Add("stop:" + label); }
    }

    /// <summary>Jeden komputer (PC-X) z prawdziwym <see cref="ClusterRuntime"/> i <see cref="SharedConfigSync"/>; grupy A i B.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new(T0.AddMinutes(1));
        public readonly List<string> Log = [];
        public readonly HashSet<int> FailingPorts = [];
        public readonly GroupFile A = GroupFileTests.Invitation(), B = GroupFileTests.Invitation();
        private readonly InMemoryPeerHub _hubA, _hubB;
        private readonly List<ClusterRuntime> _runtimes = [];
        public readonly string Path;
        public readonly SharedConfigSync Sync;
        public readonly RecordingSync Recording;

        public Rig(TempDir dir)
        {
            _hubA = new InMemoryPeerHub(HmacSigner.FromBase64(A.NetworkKey), Clock);
            _hubB = new InMemoryPeerHub(HmacSigner.FromBase64(B.NetworkKey), Clock);
            Path = dir.File("shared-config.json");
            Sync = new SharedConfigSync(new SharedConfigStore(Path), "PC-X");
            Sync.Reload();
            Recording = new RecordingSync(Sync, Log);
        }

        public ClusterConfig InA => new() { Enabled = true, Group = A.Identity, UdpPort = 47001 };
        public ClusterConfig InB => new() { Enabled = true, Group = B.Identity, UdpPort = 47002 };

        public async Task<ClusterRuntime> StartAsync(ClusterConfig config, string? key)
        {
            var runtime = new ClusterRuntime(config, key, Clock, new AckMemory(Clock, TimeSpan.FromHours(2)), "PC-X", "test", Factory)
                { SharedConfig = Recording };
            _runtimes.Add(runtime);
            runtime.SetReadiness(new(true, false, []));
            await runtime.StartAsync(default);
            return runtime;
        }

        private IPeerBus Factory(ClusterConfig c, string k)
        {
            string label = c.Group?.GroupId == A.Identity.GroupId ? "A" : c.Group?.GroupId == B.Identity.GroupId ? "B" : "?";
            if (FailingPorts.Contains(c.UdpPort)) { Log.Add("bind-fail:" + label); throw new IOException("bind failure"); }
            return new RecordingBus((label == "A" ? _hubA : _hubB).CreateBus("PC-X", c), label, Log);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var runtime in _runtimes) await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task ZmianaGrupy_ZdejmujePrzelacznikiPoZatrzymaniuStaregoWezlaAPrzedStartemNowego_BezNowegoCzasu()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(null, null, T0, machines: Station520580()); // przełączniki zapisane w grupie A
        ClusterRuntime runtime = await rig.StartAsync(rig.InA, rig.A.NetworkKey);
        rig.Log.Clear();

        await runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default);

        Assert.Equal(["stop:A", "detach", "start:B"], rig.Log);
        SharedConfigDocument layer = rig.Sync.Current!;
        Assert.Null(layer.Machines);
        Assert.Equal(T0, layer.EditedAt);
        Assert.Equal("PC-X", layer.EditedBy);
        Assert.Equal(StationMachines, string.Join(",", runtime.DetachedMachines!.Where(p => p.Value.Enabled).Select(p => p.Key)));
        Assert.Equal(rig.B.Identity.GroupId, runtime.Configuration.Group!.GroupId);
    }

    [Fact]
    public async Task ZPracySamodzielnej_DoGrupy_ZdejmujePrzedStartemWezlaGrupy()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(null, null, T0, machines: Station520580());
        ClusterRuntime runtime = await rig.StartAsync(new ClusterConfig(), null);

        await runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default);

        Assert.Equal(["detach", "start:B"], rig.Log);
        Assert.Null(rig.Sync.Current!.Machines);
        Assert.Equal(HallMachines, Enabled(rig.Sync.Current));
    }

    [Fact]
    public async Task TaSamaGrupa_ZmianaUstawien_WylaczenieIPonowneWlaczenie_Restart_PrzelacznikiZostaja()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(null, null, T0, machines: Station520580());
        string hash = rig.Sync.Hash!;
        ClusterRuntime runtime = await rig.StartAsync(rig.InA, rig.A.NetworkKey);

        await runtime.SwitchAsync(rig.InA with { Priority = 7 }, rig.A.NetworkKey, default);
        await runtime.SwitchAsync(rig.InA with
        {
            Group = rig.A.Identity with { ProfileRevision = rig.A.Identity.ProfileRevision + 1 },
        }, rig.A.NetworkKey, default);
        await runtime.SwitchAsync(rig.InA with { Enabled = false }, rig.A.NetworkKey, default);
        await runtime.SwitchAsync(rig.InA, rig.A.NetworkKey, default);
        // Restart programu (albo aktualizacja): nowy runtime startuje od razu w grupie — przez SwitchAsync nie przechodzi.
        await runtime.StopAsync();
        ClusterRuntime restarted = await rig.StartAsync(rig.InA, rig.A.NetworkKey);

        Assert.DoesNotContain("detach", rig.Log);
        Assert.Equal(hash, rig.Sync.Hash);
        Assert.Equal(StationMachines, Enabled(rig.Sync.Current));
        Assert.Null(runtime.DetachedMachines);
        Assert.Null(restarted.DetachedMachines);
    }

    [Fact]
    public async Task NieudanePrzejscieDoInnejGrupy_OddajePrzelacznikiPrzedPowrotemDoPoprzedniej()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(null, null, T0, machines: Station520580());
        byte[] original = File.ReadAllBytes(rig.Path);
        ClusterRuntime runtime = await rig.StartAsync(rig.InA, rig.A.NetworkKey);
        rig.Log.Clear();
        rig.FailingPorts.Add(rig.InB.UdpPort);

        await Assert.ThrowsAsync<IOException>(() => runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default));

        Assert.Equal(["stop:A", "detach", "bind-fail:B", "restore", "start:A"], rig.Log);
        Assert.Equal(original, File.ReadAllBytes(rig.Path));
        Assert.Equal(rig.A.Identity.GroupId, runtime.Configuration.Group!.GroupId);
        Assert.True(runtime.NetworkActive);
        Assert.Null(runtime.DetachedMachines);
        Assert.Null(runtime.Failure);
    }

    [Fact]
    public async Task CofniecieDolaczenia_OddajePrzelacznikiSprzedDolaczenia_AResztaDokumentuNowejGrupyZostaje()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(null, null, T0.AddDays(-1), machines: Station520580());
        ClusterRuntime runtime = await rig.StartAsync(new ClusterConfig(), null);
        await runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default);
        // Przed zatwierdzeniem przyszedł dokument grupy B (bez przełączników, nowszy) — tą samą drogą co z węzła.
        Assert.True(rig.Recording.Apply(new SharedConfigDocument
        {
            Timing = new TimingConfig { AlertSoundRepeatMs = 25000 }, EditedAt = T0, EditedBy = "PC-B-1",
        }));
        rig.Log.Clear();

        await runtime.RevertSwitchAsync(new ClusterConfig(), null, default);

        Assert.Equal(["stop:B", "restore"], rig.Log);
        SharedConfigDocument layer = rig.Sync.Current!;
        Assert.Equal(StationMachines, Enabled(layer));
        Assert.Equal("PC-B-1", layer.EditedBy);
        Assert.Equal(25000, layer.Timing!.AlertSoundRepeatMs);
        Assert.False(runtime.Configuration.Enabled);
        Assert.Null(runtime.DetachedMachines);
    }

    [Fact]
    public async Task CofniecieDolaczenia_GdyPrzedNimNieByloPrzelacznikow_ZdejmujeTeKtorePrzyszlyZNowejGrupy()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, T0.AddDays(-1));
        ClusterRuntime runtime = await rig.StartAsync(new ClusterConfig(), null);
        await runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default);
        Assert.Null(runtime.DetachedMachines);
        Assert.True(rig.Recording.Apply(new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(new Dictionary<string, bool> { ["aidite1"] = false, ["amm580"] = true }),
            EditedAt = T0, EditedBy = "PC-B-1",
        }));
        rig.Log.Clear();

        await runtime.RevertSwitchAsync(new ClusterConfig(), null, default);

        Assert.Equal(["stop:B", "detach"], rig.Log);
        Assert.Null(rig.Sync.Current!.Machines);
        Assert.Equal(HallMachines, Enabled(rig.Sync.Current));
    }

    [Fact]
    public async Task NieudaneCofniecie_ZostajacWNowejGrupie_ZnowZdejmujePrzelaczniki_AKolejnaProbaJeOddaje()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(null, null, T0, machines: Station520580());
        ClusterRuntime runtime = await rig.StartAsync(rig.InA, rig.A.NetworkKey);
        await runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default);
        rig.Log.Clear();
        rig.FailingPorts.Add(rig.InA.UdpPort);

        await Assert.ThrowsAsync<IOException>(() => runtime.RevertSwitchAsync(rig.InA, rig.A.NetworkKey, default));

        Assert.Equal(["stop:B", "restore", "bind-fail:A", "detach", "start:B"], rig.Log);
        Assert.Null(rig.Sync.Current!.Machines); // zostajemy w B — przełączniki sprzed dołączenia do niej nie wchodzą
        Assert.Equal(rig.B.Identity.GroupId, runtime.Configuration.Group!.GroupId);

        rig.FailingPorts.Clear();
        rig.Log.Clear();
        await runtime.RevertSwitchAsync(rig.InA, rig.A.NetworkKey, default);

        Assert.Equal(["stop:B", "restore", "start:A"], rig.Log);
        Assert.Equal(StationMachines, Enabled(rig.Sync.Current));
        Assert.Equal(rig.A.Identity.GroupId, runtime.Configuration.Group!.GroupId);
    }

    [Fact]
    public async Task NieczytelnyPlikWarstwy_BezPrzelacznikowWPamieci_NieBlokujeDolaczenia()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        File.WriteAllText(rig.Path, "{ to nie jest JSON");
        rig.Sync.Reload();
        Assert.NotNull(rig.Sync.LoadError);
        ClusterRuntime runtime = await rig.StartAsync(new ClusterConfig(), null);

        await runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default);

        Assert.Equal(["detach", "start:B"], rig.Log);
        Assert.Equal(rig.B.Identity.GroupId, runtime.Configuration.Group!.GroupId);
        Assert.Equal("{ to nie jest JSON", File.ReadAllText(rig.Path));
    }

    [Fact]
    public async Task NieczytelnyPlikWarstwy_ZPrzelacznikamiWPamieci_DolaczenieSieNieUdaje_IZostajePoprzedniaKonfiguracja()
    {
        using var dir = new TempDir("d3-dolaczenie");
        await using var rig = new Rig(dir);
        rig.Sync.Edit(null, null, T0, machines: Station520580());
        File.WriteAllText(rig.Path, "{ to nie jest JSON"); // plik uszkodzony po wczytaniu — w pamięci są przełączniki
        ClusterRuntime runtime = await rig.StartAsync(new ClusterConfig(), null);

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.SwitchAsync(rig.InB, rig.B.NetworkKey, default));

        Assert.False(runtime.Configuration.Enabled);
        Assert.False(runtime.NetworkActive);
        Assert.DoesNotContain("start:B", rig.Log);
    }

    // ============================== Pełna droga z członkiem grupy ==============================

    [Theory]
    [InlineData(-1)]
    [InlineData(+1)]
    public async Task Dolaczenie_DoGrupyZPrzelacznikami_DolaczanyPrzyjmujePrzelacznikiGrupy_IWymianaUstaje(int localEditDays)
    {
        using var dir = new TempDir("d3-dolaczenie");
        var clock = new FakeClock(T0.AddMinutes(1));
        GroupFile invitation = GroupFileTests.Invitation();
        var group = new ClusterConfig { Enabled = true, Group = invitation.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(invitation.NetworkKey), clock);

        var member = new SharedConfigSync(new SharedConfigStore(dir.File(Path.Combine("czlonek", "shared-config.json"))), "PC-HALA-1");
        member.Reload();
        var hallSwitches = TestPaths.ExampleConfig.Machines.ToDictionary(m => m.Id, m => m.Id.StartsWith("aidite", StringComparison.Ordinal)
            || m.Id == "amm580", StringComparer.Ordinal);
        member.Edit(null, null, T0, machines: hallSwitches);
        string groupMachines = Enabled(member.Current);
        Assert.Equal(HallMachines + ",amm580", groupMachines);
        await using var node = new GroupClusterNode(group with { Priority = 1 }, hub.CreateBus("PC-HALA-1", group), clock, "test")
            { SharedConfig = member };
        node.SetReadiness(new(true, false, []));
        await node.StartAsync(default);

        var joining = new SharedConfigSync(new SharedConfigStore(dir.File(Path.Combine("dolaczany", "shared-config.json"))), "PC-DOLACZANY");
        joining.Reload();
        joining.Edit(null, null, T0.AddDays(localEditDays), machines: Station520580());
        await using var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, new AckMemory(clock, TimeSpan.FromHours(2)),
            "PC-DOLACZANY", "test", (c, _) => hub.CreateBus("PC-DOLACZANY", c)) { SharedConfig = joining };
        runtime.SetReadiness(new(true, false, []));
        await runtime.StartAsync(default);

        await runtime.SwitchAsync(group with { Priority = 5 }, invitation.NetworkKey, default);
        for (int second = 0; second < 60; second++)
        {
            await runtime.TickAsync(default);
            await node.TickAsync(default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        int changes = 0;
        member.Changed += () => changes++;
        joining.Changed += () => changes++;
        for (int second = 0; second < 60; second++)
        {
            await runtime.TickAsync(default);
            await node.TickAsync(default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(groupMachines, Enabled(member.Current));
        Assert.Equal(groupMachines, Enabled(joining.Current));
        Assert.Equal(member.Hash, joining.Hash);
        Assert.Equal(0, changes);
    }
}
