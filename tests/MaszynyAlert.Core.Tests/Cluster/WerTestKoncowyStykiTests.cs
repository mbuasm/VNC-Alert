using System.Text.Json;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class WerTestKoncowyStykiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private static readonly JsonDocumentOptions KomentarzeIPrzecinki = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static JsonObject Wzorzec() =>
        JsonNode.Parse(File.ReadAllText(TestPaths.ConfigExample), documentOptions: KomentarzeIPrzecinki)!.AsObject();

    /// <summary>Słownik znaczników: baza o podanej nazwie ma kod 705 „Opis &lt;nazwa małymi&gt;” kategorii „kat-&lt;nazwa małymi&gt;”.</summary>
    private static RuleSet Znaczniki(IEnumerable<string> nazwyBaz)
    {
        var bazy = new JsonObject();
        foreach (string nazwa in nazwyBaz)
        {
            string k = nazwa.ToLowerInvariant();
            bazy[nazwa] = new JsonObject
            {
                ["codeIdPrefix"] = k + "-",
                ["codes"] = new JsonArray(new JsonObject { ["code"] = 705, ["shortText"] = $"Opis {k}", ["category"] = $"kat-{k}" }),
                ["rules"] = new JsonArray(),
            };
        }

        return ConfigLoader.ParseRules(new JsonObject { ["databases"] = bazy }.ToJsonString());
    }

    /// <summary>
    /// Przełączniki z Panelu tak, jak zapisuje je <see cref="SharedConfigSync.Edit"/> na komputerze A, a stosuje komputer B po
    /// wczytaniu pliku warstwy (inny obiekt magazynu, jak odbiór z sieci albo restart programu).
    /// </summary>
    private static AppConfig PoPrzelacznikachZPanelu(AppConfig baza, IReadOnlyDictionary<string, bool> przelaczniki)
    {
        using var dir = new TempDir("wer-koncowy-d3");
        var panel = new SharedConfigSync(new SharedConfigStore(dir.File("shared-config.json")), "PC-A");
        panel.Edit(null, null, T0, machines: przelaczniki);
        SharedConfigDocument zPliku = new SharedConfigStore(dir.File("shared-config.json")).Load()
            ?? throw new InvalidOperationException("Brak dokumentu warstwy po zapisie.");
        Assert.True(zPliku.IsValid());
        return zPliku.ApplyTo(baza);
    }


    [Fact]
    public void S1_D3XKonfiguracja_PremileWlaczoneWPanelu_ZPolemIdBloku520IBazaBezProfilu_AlarmIRaportZBazyKlucza()
    {
        JsonObject json = Wzorzec();
        json["profiles"]!["premile"]!["id"] = "aidite5x";
        var ostrzezeniaPliku = new List<string>();
        AppConfig baza = ConfigLoader.ParseAppConfig(json.ToJsonString(), ostrzezeniaPliku);
        Assert.Equal(ConfigLoader.ProfileIdWarning("premile", "aidite5x"), Assert.Single(ostrzezeniaPliku));
        Assert.False(baza.Machines.Single(m => m.Id == "premile").Enabled);

        RuleSet slownik = Znaczniki([.. baza.Profiles.Keys, "kamea"]);
        ConfigLoader.Validate(baza);
        ConfigLoader.Validate(slownik, baza);
        string literowka = Assert.Single(ConfigLoader.Warnings(slownik, baza));
        Assert.Contains("'kamea'", literowka, StringComparison.Ordinal);

        AppConfig efektywna = PoPrzelacznikachZPanelu(baza, baza.Machines.ToDictionary(m => m.Id, m => m.Enabled || m.Id == "premile"));

        ConfigLoader.Validate(efektywna);
        MachineConfig premile = efektywna.Machines.Single(m => m.Id == "premile");
        Assert.True(premile.Enabled);
        Assert.Equal(baza.Machines.Where(m => m.Id != "premile").Select(m => (m.Id, m.Enabled)),
            efektywna.Machines.Where(m => m.Id != "premile").Select(m => (m.Id, m.Enabled)));
        ScreenProfile profil = efektywna.Profiles[premile.Profile]; // tak profil trafia do MachineMonitor (MonitorSupervisor.StartAsync)
        Assert.Equal("premile", profil.Id);
        MappedMessage alarm = new MessageMapper(slownik).Map("No.705", profil.Id);
        Assert.Equal(("Opis premile", "premile-"), (alarm.ShortText, alarm.CodeNamespace));
        Assert.Equal("kat-premile", new DowntimeCategoryClassifier(slownik, efektywna.Machines).Classify("premile", "Opis premile"));
        Assert.Equal(new[] { literowka }, ConfigLoader.Warnings(slownik, efektywna));
        Assert.Empty(WatcherReadiness.MissingPasswordsAfterEnabling(baza, efektywna, _ => false));
        // Kontrola czułości: ta sama maszyna z "requiresPassword": true (np. stary config.json) — Panel w grupie by odmówił.
        AppConfig zHaslem = baza with { Machines = [.. baza.Machines.Select(m => m.Id == "premile" ? m with { RequiresPassword = true } : m)] };
        Assert.Equal(new[] { "premile" }, WatcherReadiness.MissingPasswordsAfterEnabling(zHaslem,
            PoPrzelacznikachZPanelu(zHaslem, zHaslem.Machines.ToDictionary(m => m.Id, m => m.Enabled || m.Id == "premile")), _ => false));
    }


    [Fact]
    public void S2_D3XNazwyBaz_BazaWielkimiLiterami_PoWlaczeniuWPaneluAlarmIRaportTaSamaBaza_DwieBazyZatrzymujaKontroleZawsze()
    {
        AppConfig baza = TestPaths.ExampleConfig;
        Assert.False(baza.Machines.Single(m => m.Id == "premile").Enabled);
        RuleSet slownik = Znaczniki([.. baza.Profiles.Keys.Where(k => k != "premile"), "PREMILE"]);
        ConfigLoader.Validate(slownik, baza);
        Assert.Empty(ConfigLoader.Warnings(slownik, baza));

        AppConfig efektywna = PoPrzelacznikachZPanelu(baza, baza.Machines.ToDictionary(m => m.Id, m => m.Enabled || m.Id == "premile"));
        MachineConfig premile = efektywna.Machines.Single(m => m.Id == "premile");
        Assert.True(premile.Enabled);
        MappedMessage alarm = new MessageMapper(slownik).Map("No.705", efektywna.Profiles[premile.Profile].Id);
        Assert.Equal("Opis premile", alarm.ShortText);
        Assert.Equal("kat-premile", new DowntimeCategoryClassifier(slownik, efektywna.Machines).Classify("premile", "Opis premile"));
        Assert.Empty(ConfigLoader.Warnings(slownik, efektywna));

        RuleSet dwieBazy = Znaczniki([.. baza.Profiles.Keys, "PREMILE"]);
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(dwieBazy, baza));
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(dwieBazy, efektywna));
    }


    private static DowntimeEpisode Zamkniety(string maszyna, DateTimeOffset start, double minuty, string opis = "Servo 6") =>
        new(maszyna, "Aidite " + maszyna[^1], opis, start, start.AddMinutes(minuty), start.AddMinutes(minuty),
            EpisodeId: Guid.NewGuid(), Origin: "B", Category: "restart");

    [Fact]
    public async Task S3_RaportXRequestId_NumerDziennikaRosnieTylkoGdySynchronizacjaCosZmienia_TakzeOdpowiedzBezKorelacji()
    {
        using var dir = new TempDir("wer-koncowy-requestid");
        var clock = new FakeClock(T0);
        GroupFile grupa = GroupFileTests.Invitation();
        var cfg = new ClusterConfig { Enabled = true, Group = grupa.Identity };
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(grupa.NetworkKey), clock);
        InMemoryPeerBus aBus = hub.CreateBus("A"), bBus = hub.CreateBus("B");
        await using var a = new GroupClusterNode(cfg, aBus, clock, "test");
        await using var b = new GroupClusterNode(cfg with { Priority = 200 }, bBus, clock, "test");
        a.SetReadiness(new(true, false, []));
        b.SetReadiness(new(true, false, []));
        var aDziennik = new DowntimeJournal(dir.File("a.jsonl"));
        var bDziennik = new DowntimeJournal(dir.File("b.jsonl"));
        a.Downtime = aDziennik;
        b.Downtime = bDziennik;
        int zdarzeniaPanelu = 0;
        a.DowntimeChanged += () => Interlocked.Increment(ref zdarzeniaPanelu);

        DowntimeEpisode stary = Zamkniety("aidite3", T0 - TimeSpan.FromDays(3), 20);
        DowntimeEpisode swiezy = Zamkniety("aidite2", T0 - TimeSpan.FromHours(2), 15);
        bDziennik.Merge(stary);
        bDziennik.Merge(swiezy);

        async Task Tiki(int sekundy)
        {
            for (int i = 0; i < sekundy; i++)
            {
                await a.TickAsync(default);
                await b.TickAsync(default);
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        await a.StartAsync(default);
        await b.StartAsync(default);
        await Tiki(25);

        // 1. Pierwsze uzupełnienie.
        Assert.Equal(new[] { stary.EpisodeId, swiezy.EpisodeId }.Order(), aDziennik.Snapshot().Select(e => e.EpisodeId).Order());
        long v1 = aDziennik.Version;
        Assert.True(v1 > 0);
        Assert.True(zdarzeniaPanelu > 0);
        int z1 = zdarzeniaPanelu;
        Assert.NotNull(a.Peers.Single(p => p.Host == "B").DowntimeSyncedAt);

        // 2. Stan ustalony bez nowych danych.
        await Tiki(35 * 60);
        Assert.Equal(v1, aDziennik.Version);
        Assert.Equal(z1, zdarzeniaPanelu);

        // 3. Przerwa łączności i powrót: pełne okno od nowa. B chwilowo nie odpowiada na prośby (bez dziennika), więc pełne okno A
        //    zostaje otwarte — prośby co 10 s, numer dziennika stoi.
        hub.Partition("A", "B");
        await Tiki(120);
        b.Downtime = null;
        hub.Heal();
        DateTimeOffset powrot = clock.UtcNow;
        await Tiki(30);
        Assert.Equal(v1, aDziennik.Version);
        Assert.Equal(z1, zdarzeniaPanelu);
        Assert.True(a.Peers.Single(p => p.Host == "B").DowntimeSyncedAt < powrot, "pełne okno skończyło się bez odpowiedzi na prośbę");

        // 4. W trakcie pełnego okna odpowiedź BEZ korelacji z nowym postojem: wpis przyjęty, numer rośnie, pełne okno trwa.
        DowntimeEpisode spozniony = Zamkniety("aidite5", clock.UtcNow - TimeSpan.FromHours(5), 12, "Tacka nie pobrana");
        var odpowiedz = new DowntimeSyncReply
        {
            Sender = "B", Version = "test", Group = cfg.Group, RequestId = Guid.NewGuid(), ReplyTo = "A", Episodes = [spozniony],
        };
        await bBus.SendAsync(odpowiedz, default);
        await Tiki(1);
        Assert.Contains(aDziennik.Snapshot(), e => e.EpisodeId == spozniony.EpisodeId);
        long v2 = aDziennik.Version;
        Assert.True(v2 > v1, "postój z odpowiedzi bez korelacji nie wszedł do dziennika albo nie zmienił numeru — Panel by go nie pokazał");
        Assert.Equal(z1 + 1, zdarzeniaPanelu);
        Assert.True(a.Peers.Single(p => p.Host == "B").DowntimeSyncedAt < powrot, "odpowiedź bez korelacji zakończyła pełne okno");
        await bBus.SendAsync(odpowiedz with { RequestId = Guid.NewGuid() }, default);
        await Tiki(1);
        Assert.Equal(v2, aDziennik.Version);
        Assert.Equal(z1 + 1, zdarzeniaPanelu);

        // B znów odpowiada: pełne okno kończy się odpowiedzią skorelowaną z tymi samymi postojami — numer bez zmian.
        b.Downtime = bDziennik;
        await Tiki(30);
        Assert.True(a.Peers.Single(p => p.Host == "B").DowntimeSyncedAt >= powrot, "pełne okno po powrocie łączności się nie skończyło");
        Assert.Equal(v2, aDziennik.Version);
        Assert.Equal(z1 + 1, zdarzeniaPanelu);

        // 5. Ten sam raport na obu komputerach (B dostał postój od A w ogonie albo nie — porównujemy dziennik A z sumą).
        bDziennik.Merge(spozniony);
        static string Wiersze(DowntimeJournal d) => string.Join("\n", DowntimeReport.Build(d.Snapshot(), TimeZoneInfo.Utc,
                new DowntimeCategoryClassifier(TestPaths.ExampleRules, TestPaths.ExampleConfig.Machines), now: T0.AddDays(1))
            .Select(s => $"{s.Day}|{s.Shift}|{s.MachineId}|{s.Start:O}|{s.End:O}|{s.Category}|{s.EpisodeId}"));
        Assert.Equal(Wiersze(bDziennik), Wiersze(aDziennik));
    }
}
