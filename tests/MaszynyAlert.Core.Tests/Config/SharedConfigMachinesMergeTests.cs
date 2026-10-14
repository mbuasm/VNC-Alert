using System.Security.Cryptography;
using System.Text;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class SharedConfigMachinesMergeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("klucz-testowy-grupy"));

    private static readonly GroupIdentity Group = new()
    {
        GroupId = Guid.Parse("11111111-2222-3333-4444-555555555555"), GroupName = "Hala",
        KeyId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), ProtocolVersion = GroupIdentity.CurrentProtocolVersion,
        ProfileRevision = 3, ProfileHash = new string('c', 64),
    };

    private static SharedConfigDocument DocumentFrom0148()
    {
        Assert.True(MessageCodec.TryDecode(Cluster.GroupMixedVersionGoldenTests.Golden("13-sharedConfig"), new HmacSigner(Key),
            new FakeClock(T0), TimeSpan.FromSeconds(300), out var message, out string? reason), reason);
        SharedConfigDocument document = Assert.IsType<SharedConfigMessage>(message).Document;
        Assert.Null(document.Machines);
        return document;
    }

    private static Dictionary<string, bool> Switches(params (string Id, bool On)[] items) =>
        items.ToDictionary(i => i.Id, i => i.On, StringComparer.Ordinal);

    private static string Describe(SharedConfigDocument? document) => document?.Machines is not { } machines ? "null"
        : string.Join(",", machines.Select(p => p.Key + "=" + (p.Value.Enabled ? "on" : "off")));

    private static UpdateApproval Approval(string version, DateTimeOffset at) => new()
    {
        Version = version, FileName = "MaszynyAlert-Setup-" + version + ".exe", ApprovedBy = "PC-NOWY", ApprovedAt = at,
    };

    // ============================== Pola dokumentu starszej wersji ==============================

    [Fact]
    public void DokumentStarszejWersji_WygrywaWKazdymPoluKtoreNiesie_APrzelacznikiZostajaZPliku()
    {
        using var dir = new TempDir("d3-scalanie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(new TimingConfig { AlertSoundRepeatMs = 90000 }, new OverlayConfig { Scale = 1.0 }, T0.AddMinutes(-2), "PC-NOWY",
            machines: Switches(("amm580", true), ("aidite1", false)));
        store.SaveApproval(Approval("0.4.148.0", T0.AddMinutes(-2)), T0.AddMinutes(-1), "PC-NOWY");
        SharedConfigDocument old = DocumentFrom0148();

        Assert.True(store.Merge(old));

        SharedConfigDocument loaded = store.Load()!;
        Assert.Equal(30000, loaded.Timing!.AlertSoundRepeatMs);
        Assert.Equal(1.5, loaded.Overlay!.Scale);
        Assert.False(loaded.Overlay.Enabled);
        Assert.Equal("0.4.249.0", loaded.UpdateApproval!.Version);
        Assert.Equal(T0, loaded.EditedAt);
        Assert.Equal("PC-A", loaded.EditedBy);
        Assert.Equal("aidite1=off,amm580=on", Describe(loaded));
        Assert.Equal((old with { Machines = loaded.Machines }).Hash, loaded.Hash);
        AppConfig effective = loaded.ApplyTo(TestPaths.ExampleConfig);
        Assert.True(effective.Machines.Single(m => m.Id == "amm580").Enabled);
        Assert.False(effective.Machines.Single(m => m.Id == "aidite1").Enabled);
        Assert.Equal(30000, effective.Timing.AlertSoundRepeatMs);
    }

    /// <summary>
    /// „Przywróć…” obu sekcji na starszej wersji daje dokument bez nadpisań — puste pola wygrywają (sekcje wracają do config.json),
    /// a przełączniki zostają, bo starsza wersja ich nie zna i nie mogła ich „przywrócić”.
    /// </summary>
    [Fact]
    public void PrzywrocNaStarszejWersji_PustePolaWygrywaja_PrzelacznikiZostaja()
    {
        using var dir = new TempDir("d3-scalanie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, new OverlayConfig { Scale = 1.5 }, T0, "PC-NOWY",
            machines: Switches(("amm580", true)));

        Assert.True(store.Merge(new SharedConfigDocument { EditedAt = T0.AddMinutes(1), EditedBy = "PC-A" }));

        SharedConfigDocument loaded = store.Load()!;
        Assert.Null(loaded.Timing);
        Assert.Null(loaded.Overlay);
        Assert.Null(loaded.UpdateApproval);
        Assert.Equal("PC-A", loaded.EditedBy);
        Assert.Equal("amm580=on", Describe(loaded));
        Assert.False(loaded.IsEmpty);
        Assert.True(loaded.ApplyTo(TestPaths.ExampleConfig).Machines.Single(m => m.Id == "amm580").Enabled);
    }

    // ============================== Dokument z przełącznikami ==============================

    /// <summary>
    /// Dokument nowej wersji z polem <c>machines</c> zastępuje przełączniki w CAŁOŚCI (migawka, nie suma słowników) — także pusty
    /// słownik, który jest jawną informacją, a nie brakiem pola.
    /// </summary>
    [Fact]
    public void NowszyDokumentZPrzelacznikami_ZastepujeJeWCalosci_TakzePustySlownik()
    {
        using var dir = new TempDir("d3-scalanie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(null, null, T0, "PC-NOWY", machines: Switches(("amm580", true), ("aidite1", false)));

        Assert.True(store.Merge(new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(Switches(("premile", true))), EditedAt = T0.AddMinutes(1), EditedBy = "PC-B",
        }));
        Assert.Equal("premile=on", Describe(store.Load()));

        Assert.True(store.Merge(new SharedConfigDocument
        {
            Machines = new Dictionary<string, SharedMachineSettings>(StringComparer.Ordinal), EditedAt = T0.AddMinutes(2), EditedBy = "PC-B",
        }));
        SharedConfigDocument loaded = store.Load()!;
        Assert.NotNull(loaded.Machines);
        Assert.Empty(loaded.Machines);
    }

    [Fact]
    public void StarszyDokumentBezPrzelacznikow_Przegrywa_PlikBajtWBajt()
    {
        using var dir = new TempDir("d3-scalanie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(null, null, T0.AddMinutes(1), "PC-NOWY", machines: Switches(("amm580", true)));
        byte[] before = File.ReadAllBytes(store.Path);

        Assert.False(store.Merge(DocumentFrom0148())); // 08:00 < 08:01

        Assert.Equal(before, File.ReadAllBytes(store.Path));
    }

    // ============================== Brak wiecznego przepisywania pliku ==============================

    /// <summary>
    /// Starsza wersja wysyła swój dokument co 10 s, dopóki odciski się różnią (a różnią się zawsze — u niej nie ma pola
    /// <c>machines</c>). Drugi i każdy kolejny odbiór — ten sam dokument albo dokument z pliku okrojony z przełączników — daje
    /// dokument równy plikowi i niczego nie zapisuje.
    /// </summary>
    [Fact]
    public void PonownyOdbiorTegoSamegoDokumentu_NicNieZapisuje()
    {
        using var dir = new TempDir("d3-scalanie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(null, null, T0.AddMinutes(-1), "PC-NOWY", machines: Switches(("amm580", true)));
        SharedConfigDocument old = DocumentFrom0148();
        Assert.True(store.Merge(old));
        byte[] afterFirst = File.ReadAllBytes(store.Path);
        string hash = store.Load()!.Hash;

        for (int i = 0; i < 5; i++)
        {
            Assert.False(store.Merge(DocumentFrom0148()));
            Assert.False(store.Merge(store.Load()! with { Machines = null }));
        }

        Assert.Equal(afterFirst, File.ReadAllBytes(store.Path));
        Assert.Equal(hash, store.Load()!.Hash);
    }

    /// <summary>
    /// Remis (ten sam czas, autor i obecność zgody) z dokumentem bez przełączników rozstrzyga kryterium maszyn
    /// w <see cref="SharedConfigDocument.Wins"/>, liczone na dokumencie TAKIM, JAK PRZYSZEDŁ. Gdyby scalanie najpierw doklejało
    /// przełączniki, a dopiero potem pytało o regułę, remis rozstrzygałby odcisk; test dobiera dokument, przy którym odcisk
    /// przyznałby wygraną, więc złapie taką zmianę kolejności.
    /// </summary>
    [Fact]
    public void RemisZDokumentemBezPrzelacznikow_DalejRozstrzygaKryteriumMaszyn_NieOdcisk()
    {
        using var dir = new TempDir("d3-scalanie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, T0, "PC-A", machines: Switches(("amm580", true)));
        SharedConfigDocument file = store.Load()!;
        SharedConfigDocument tie = Enumerable.Range(1, 500)
            .Select(i => file with { Machines = null, Timing = file.Timing! with { AlertSoundRepeatMs = 30000 + 1000 * i } })
            .First(candidate => SharedConfigDocument.Wins(candidate with { Machines = file.Machines }, file));
        Assert.False(SharedConfigDocument.Wins(tie, file));
        byte[] before = File.ReadAllBytes(store.Path);

        Assert.False(store.Merge(tie));

        Assert.Equal(before, File.ReadAllBytes(store.Path));
        Assert.Equal(30000, store.Load()!.Timing!.AlertSoundRepeatMs);
    }

    [Fact]
    public void KeepMachinesFromFile_BezPlikuAlboBezPrzelacznikowWPliku_ZwracaDokumentJakPrzyszedl()
    {
        SharedConfigDocument old = DocumentFrom0148();
        Assert.Same(old, SharedConfigStore.KeepMachinesFromFile(old, null));
        Assert.Same(old, SharedConfigStore.KeepMachinesFromFile(old, old with { EditedAt = T0.AddMinutes(-1) }));
        var withMachines = old with { Machines = SharedConfigDocument.CanonicalMachines(Switches(("amm580", false))) };
        Assert.Same(withMachines, SharedConfigStore.KeepMachinesFromFile(withMachines,
            old with { EditedAt = T0.AddMinutes(-1), Machines = SharedConfigDocument.CanonicalMachines(Switches(("amm580", true))) }));
    }

    [Fact]
    public void KazdaWlasnaSciezkaZapisu_ZostawiaPrzelacznikiZPliku()
    {
        using var dir = new TempDir("d3-scalanie");
        var store = new SharedConfigStore(dir.File("shared-config.json"));
        var sync = new SharedConfigSync(store, "PC-NOWY");
        sync.Reload();
        sync.Edit(new TimingConfig { AlertSoundRepeatMs = 30000 }, new OverlayConfig { Scale = 1.5 }, T0,
            machines: Switches(("amm580", true), ("aidite1", false)));

        Assert.Equal("aidite1=off,amm580=on", Describe(sync.Edit(new TimingConfig { AlertSoundRepeatMs = 45000 }, null, T0.AddMinutes(1))));
        Assert.Equal("aidite1=off,amm580=on", Describe(sync.Edit(null, new OverlayConfig { Scale = 1.5 }, T0.AddMinutes(2))));
        Assert.Equal("aidite1=off,amm580=on", Describe(sync.Edit(null, null, T0.AddMinutes(3))));
        Assert.Equal("aidite1=off,amm580=on", Describe(sync.Approve(Approval("0.4.250.0", T0.AddMinutes(4)), T0.AddMinutes(4))));
        sync.Reload();
        Assert.Equal("aidite1=off,amm580=on", Describe(sync.Current));
    }

    // ============================== Uzgodnienie grupy ==============================

    /// <summary>
    /// Dwa komputery nowej wersji z tym samym dokumentem dostają nowszy dokument starszej wersji: oba doklejają te same przełączniki,
    /// więc mają identyczny dokument (ten sam odcisk) i nie mają sobie nic do wysłania.
    /// </summary>
    [Fact]
    public void DwaKomputeryNowejWersji_PoDokumencieStarszej_MajaTenSamDokument()
    {
        using var dir = new TempDir("d3-scalanie");
        var b = new SharedConfigStore(dir.File("b.json"));
        var c = new SharedConfigStore(dir.File("c.json"));
        SharedConfigDocument toggled = b.Save(null, null, T0.AddMinutes(-1), "PC-NOWY", machines: Switches(("amm580", true)));
        Assert.True(c.Merge(toggled));

        Assert.True(b.Merge(DocumentFrom0148()));
        Assert.True(c.Merge(DocumentFrom0148()));

        Assert.Equal(b.Load()!.Hash, c.Load()!.Hash);
        Assert.False(b.Merge(c.Load()!));
        Assert.False(c.Merge(b.Load()!));
    }

    /// <summary>
    /// Grupa samej nowej wersji: komputer, który nie dostał jeszcze przełączników (był wyłączony albo odcięty), zapisuje czas. Jego
    /// nowszy dokument bez <c>machines</c> nie kasuje przełączników u pozostałych, a dokument scalony (czas z jednego, przełączniki
    /// z drugiego) wygrywa u niego remis — grupa uzgadnia się na obu zmianach.
    /// </summary>
    [Fact]
    public void GrupaNowejWersji_ZapisNaKomputerzeBezPrzelacznikow_NieKasujeIch_IGrupaSieUzgadnia()
    {
        using var dir = new TempDir("d3-scalanie");
        var a = new SharedConfigStore(dir.File("a.json"));
        var b = new SharedConfigStore(dir.File("b.json"));
        b.Save(null, null, T0, "PC-B", machines: Switches(("amm580", true)));
        SharedConfigDocument timingOnA = a.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, T0.AddMinutes(1), "PC-A");
        Assert.Null(timingOnA.Machines);

        Assert.True(b.Merge(timingOnA));
        Assert.True(a.Merge(b.Load()!));

        Assert.Equal(a.Load()!.Hash, b.Load()!.Hash);
        Assert.Equal("amm580=on", Describe(a.Load()));
        Assert.Equal(30000, a.Load()!.Timing!.AlertSoundRepeatMs);
        Assert.Equal("PC-A", a.Load()!.EditedBy);
    }

    /// <summary>
    /// CHARAKTERYSTYKA samego magazynu: plik z przełącznikami scalony z dokumentem grupy bez pola <c>machines</c> (w grupie nikt maszyn
    /// nie przełączał — wygląda jak dokument starszej wersji) zachowuje swoje przełączniki, a dokument scalony wygrywa remis u członka
    /// grupy nowej wersji. Dlatego DOŁĄCZENIE do innej grupy zdejmuje lokalne przełączniki, zanim ruszy wymiana
    /// (<see cref="ClusterRuntime.SwitchAsync"/> → <see cref="SharedConfigStore.DropMachines"/>) — pełna
    /// droga: <c>Cluster/WerTestD3GrupaDolaczanieTests</c> i <c>Cluster/GroupJoinSharedMachinesTests</c>. Ten test pilnuje, że sam
    /// magazyn się nie zmienił.
    /// </summary>
    [Fact]
    public void Magazyn_ScalenieZDokumentemGrupyBezPrzelacznikow_ZostawiaLokalne_DlategoDolaczenieJeZdejmuje()
    {
        using var dir = new TempDir("d3-scalanie");
        var joining = new SharedConfigStore(dir.File("dolaczajacy.json"));
        var member = new SharedConfigStore(dir.File("czlonek.json"));
        joining.Save(null, null, T0.AddDays(-1), "STANOWISKO-520", machines: Switches(("aidite1", false), ("amm580", true)));
        SharedConfigDocument group = member.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, T0, "PC-HALA");
        Assert.Null(group.Machines);

        Assert.True(joining.Merge(group));
        Assert.Equal("aidite1=off,amm580=on", Describe(joining.Load()));
        Assert.True(member.Merge(joining.Load()!));

        Assert.Equal("aidite1=off,amm580=on", Describe(member.Load()));
        Assert.Equal(30000, member.Load()!.Timing!.AlertSoundRepeatMs);
    }

    [Fact]
    public async Task WymianaZeStarszaWersja_PrzezMinute_PlikZapisanyRaz_580DalejWlaczona()
    {
        using var dir = new TempDir("d3-scalanie");
        var clock = new FakeClock(T0);
        var hub = new InMemoryPeerHub(new HmacSigner(Key), clock);
        var oldPc = hub.CreateBus("PC-A");
        var sync = new SharedConfigSync(new SharedConfigStore(dir.File("shared-config.json")), "PC-B");
        sync.Reload();
        int changed = 0, nodeChanged = 0;
        sync.Changed += () => changed++;
        await using var node = new GroupClusterNode(new ClusterConfig { Enabled = true, Group = Group }, hub.CreateBus("PC-B"), clock, "test")
            { SharedConfig = sync };
        node.SharedConfigChanged += () => nodeChanged++;
        node.SetReadiness(new(true, false, []));
        await node.StartAsync(default);
        sync.Edit(null, null, T0.AddMinutes(-1), machines: Switches(("amm580", true)));
        var fromNew = new List<SharedConfigDocument>();
        oldPc.Received += message => { if (message is SharedConfigMessage shared) fromNew.Add(shared.Document); };
        SharedConfigDocument old = DocumentFrom0148();
        byte[]? afterFirst = null;
        string? hashAfterFirst = null;

        for (int second = 0; second < 60; second++)
        {
            await oldPc.SendAsync(new HeartbeatMessage
            {
                Sender = "PC-A", Version = "0.4.148.0", Group = Group, Role = PeerRole.Watcher, Priority = 1,
                Readiness = new(true, false, []), SharedConfigHash = old.Hash, SharedConfigEditedAt = old.EditedAt,
            }, default);
            if (second % 10 == 0)
                await oldPc.SendAsync(new SharedConfigMessage { Sender = "PC-A", Version = "0.4.148.0", Group = Group, Document = old }, default);
            if (second == 0)
            {
                afterFirst = File.ReadAllBytes(sync.Path);
                hashAfterFirst = sync.Hash;
            }
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, changed);
        Assert.Equal(1, nodeChanged);
        Assert.Equal(afterFirst, File.ReadAllBytes(sync.Path));
        Assert.Equal(hashAfterFirst, sync.Hash);
        Assert.Equal("PC-A", sync.Current!.EditedBy);
        Assert.Equal("0.4.249.0", sync.Current.UpdateApproval!.Version);
        Assert.True(sync.Current.ApplyTo(TestPaths.ExampleConfig).Machines.Single(m => m.Id == "amm580").Enabled);
        Assert.InRange(fromNew.Count, 1, 7);
        Assert.All(fromNew, document => Assert.Equal("amm580=on", Describe(document)));
    }
}
