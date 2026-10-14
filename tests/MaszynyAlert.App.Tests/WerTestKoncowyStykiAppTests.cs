using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.App.Tests;

public sealed class WerTestKoncowyStykiAppTests : IAsyncLifetime
{
    private readonly PanelTestHost _host = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _disposablesSync = [];
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "wer-koncowy-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var item in _disposables) { try { await item.DisposeAsync(); } catch (Exception) { } }
        foreach (var item in _disposablesSync) item.Dispose();
        await _host.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ── Wspólne: raport w Panelu ─────────────────────────────────────────────────────────────────────

    /// <summary>Ostatnie dni robocze przed dzisiejszym dniem zmian, w domyślnym okresie widoku (dziś − 6 … dziś).</summary>
    private static DateTime[] RecentWorkingDays(int count)
    {
        var today = DateTime.Now.AddHours(-6).Date;
        return Enumerable.Range(1, 6).Select(i => today.AddDays(-i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d >= DateTime.Today.AddDays(-6))
            .Take(count).ToArray();
    }

    private static DateTimeOffset Local(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    private static DowntimeEpisode Error(string machineId, DateTimeOffset start, double minutes, string reason = "Servo 6") =>
        new(machineId, "Aidite " + machineId[^1], reason, start, start.AddMinutes(minutes), start.AddMinutes(minutes),
            EpisodeId: Guid.NewGuid(), Category: "restart");

    private static IReadOnlyList<DowntimeSlice> Slices(DowntimeView view) =>
        (IReadOnlyList<DowntimeSlice>)typeof(DowntimeView).GetField("_slices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static object? Built(DowntimeView view) =>
        typeof(DowntimeView).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);

    /// <summary>Wszystko, co widok pokazuje: karty, ostrzeżenie (tekst i widoczność), obie tabele, wiersze raportu.</summary>
    private static string Screen(DowntimeView view)
    {
        var text = new StringBuilder();
        foreach (var block in new[] { view.TotalTime, view.TotalMinutes, view.EpisodeCount, view.EmptyText, view.ReportWarning })
            text.Append(block.Name).Append(" = ").Append(block.Text).Append('\n');
        text.Append("ReportWarningBox = ").Append(view.ReportWarningBox.Visibility).Append('\n');
        foreach (object row in view.SummaryGrid.ItemsSource?.Cast<object>() ?? []) text.Append("dzień: ").Append(row).Append('\n');
        foreach (object row in view.EpisodesGrid.ItemsSource?.Cast<object>() ?? []) text.Append("postój: ").Append(row).Append('\n');
        foreach (var s in Slices(view))
            text.Append("wiersz: ").Append(string.Join('|', s.Day.ToString("O", CultureInfo.InvariantCulture), s.Shift, s.Machine,
                s.Reason, s.Start.ToString("O", CultureInfo.InvariantCulture), s.End.ToString("O", CultureInfo.InvariantCulture),
                s.Category, s.EpisodeId)).Append('\n');
        return text.ToString();
    }

    /// <summary>Świeży widok na tym samym orkiestratorze — „liczenie od zera”.</summary>
    private static string FromScratch(AppOrchestrator orchestrator)
    {
        var fresh = new DowntimeView(orchestrator);
        fresh.Refresh();
        return Screen(fresh);
    }


    private static AlertEvent HistoryEvent(string description) => new(AlertEventKind.Raised, new Alert
    {
        Key = AlertKey.ForRule("aidite1", "servo"), MachineId = "aidite1", MachineName = "Aidite 1",
        Kind = AlertKind.Error, ShortText = description, FullText = "Servo", Category = "inny",
        StartedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, Status = AlertStatus.Active,
    });

    [Fact]
    public void S4_RaportXHistoryCsv_BladZapisuHistorii_OstrzezeniePojawiaSieIZnikaWTiku_BezPrzeliczaniaRaportu()
    {
        string data = _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(2);
        Assert.NotEmpty(days);
        orchestrator.Downtime.Merge(Error("aidite2", Local(days[0].AddHours(9)), 25));
        var history = (HistoryWriter)typeof(AppOrchestrator).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(orchestrator)!;
        string csv = Path.Combine(data, "history.csv");

        DowntimeView view = null!;
        object? built = null;
        WpfTestHost.Run(() =>
        {
            view = new DowntimeView(orchestrator);
            view.Refresh();
            built = Built(view);
            Assert.NotNull(built);
            Assert.Equal(Visibility.Collapsed, view.ReportWarningBox.Visibility);
            Assert.Equal(FromScratch(orchestrator), Screen(view));
        });

        void Tick(bool sameReport, bool warning)
        {
            WpfTestHost.Run(() =>
            {
                view.Refresh();
                if (sameReport) Assert.Same(built, Built(view));
                else { Assert.NotSame(built, Built(view)); built = Built(view); }
                Assert.Equal(warning ? Visibility.Visible : Visibility.Collapsed, view.ReportWarningBox.Visibility);
                if (warning) Assert.Contains("history.csv", view.ReportWarning.Text, StringComparison.Ordinal);
                Assert.Equal(FromScratch(orchestrator), Screen(view));
            });
        }

        // Pierwszy zapis po starcie: plik zablokowany (analiza końca nie może go otworzyć).
        using (new FileStream(csv, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            history.Append(HistoryEvent("Pierwszy"), "PC-A", "operator");
            Assert.NotNull(orchestrator.HistoryError);
            Tick(sameReport: true, warning: true);
        }

        history.Flush();
        Assert.Null(orchestrator.HistoryError);
        Tick(sameReport: true, warning: false);

        // Kolejne dopisanie (szybka ścieżka) przy blokadzie; w trakcie dziennik postojów się zmienia.
        history.Append(HistoryEvent("Drugi"), "PC-A", "operator");
        Assert.Null(orchestrator.HistoryError);
        using (new FileStream(csv, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            history.Append(HistoryEvent("Trzeci"), "PC-A", "operator");
            Assert.NotNull(orchestrator.HistoryError);
            Tick(sameReport: true, warning: true);
            orchestrator.Downtime.Merge(Error("aidite4", Local(days[0].AddHours(11)), 40, "Tacka nie pobrana"));
            Tick(sameReport: false, warning: true);
        }

        history.Flush();
        Assert.Null(orchestrator.HistoryError);
        Tick(sameReport: true, warning: false);
        string[] lines = File.ReadAllLines(csv, Encoding.UTF8);
        Assert.Equal(4, lines.Length); // nagłówek + trzy wpisy, bez duplikatów i bez zgubionych
        Assert.Equal(new[] { "Pierwszy", "Drugi", "Trzeci" }, lines.Skip(1).Select(l => l.Split(';')[3]));
    }


    [Fact]
    public async Task S5_RaportXRequestId_PostojeZSynchronizacjiGrupy_PanelPokazujeWNajblizszymTiku_PowtorzonePelneOknoNiePrzelicza()
    {
        string data = _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        var days = RecentWorkingDays(2);
        Assert.NotEmpty(days);
        orchestrator.Downtime.Merge(Error("aidite1", Local(days[0].AddHours(8)), 30));

        var clock = new PreviewManualClock(DateTimeOffset.UtcNow);
        var rules = ConfigLoader.LoadRules(Path.Combine(PreviewQtOrchestratorRig.RepositoryRoot(), "config", "rules.example.json"));
        GroupFile group = GroupFile.Create("Hala test-koncowy", GroupProfile.Capture(orchestrator.Config, rules, new Dictionary<string, string>()));
        var hub = new InMemoryPeerHub(HmacSigner.FromBase64(group.NetworkKey), clock);
        var cfg = new ClusterConfig { Enabled = true, Group = group.Identity };
        var a = new GroupClusterNode(cfg, hub.CreateBus("A"), clock, "test");
        InMemoryPeerBus bBus = hub.CreateBus("B");
        var b = new GroupClusterNode(cfg with { Priority = 200 }, bBus, clock, "test");
        _disposables.Add(a);
        _disposables.Add(b);
        a.SetReadiness(new(true, false, []));
        b.SetReadiness(new(true, false, []));
        a.Downtime = orchestrator.Downtime;
        var bJournal = new DowntimeJournal(Path.Combine(data, "partner-b-downtime.jsonl"));
        b.Downtime = bJournal;
        DowntimeEpisode fromB = Error("aidite3", Local(days[^1].AddHours(13)), 45, "Ciśnienie powietrza");
        bJournal.Merge(fromB);

        async Task Ticks(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                await a.TickAsync(default);
                await b.TickAsync(default);
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        DowntimeView view = null!;
        object? built = null;
        WpfTestHost.Run(() =>
        {
            view = new DowntimeView(orchestrator);
            view.Refresh();
            built = Built(view);
            Assert.DoesNotContain(Slices(view), s => s.EpisodeId == fromB.EpisodeId);
        });

        void Tick(bool sameReport, Guid? shown = null)
        {
            WpfTestHost.Run(() =>
            {
                view.Refresh();
                if (sameReport) Assert.Same(built, Built(view));
                else { Assert.NotSame(built, Built(view)); built = Built(view); }
                if (shown is { } id) Assert.Contains(Slices(view), s => s.EpisodeId == id);
                Assert.Equal(FromScratch(orchestrator), Screen(view));
            });
        }

        await a.StartAsync(default);
        await b.StartAsync(default);
        await Ticks(25);
        Assert.Contains(orchestrator.Downtime.Snapshot(), e => e.EpisodeId == fromB.EpisodeId);
        Tick(sameReport: false, shown: fromB.EpisodeId);

        await Ticks(12 * 60);
        Tick(sameReport: true);

        // Przerwa łączności; po powrocie B chwilowo nie odpowiada (bez dziennika) — pełne okno A otwarte, prośby co 10 s.
        hub.Partition("A", "B");
        await Ticks(120);
        b.Downtime = null;
        hub.Heal();
        await Ticks(30);
        Tick(sameReport: true);

        // W trakcie pełnego okna odpowiedź bez korelacji z nowym postojem — Panel go pokazuje.
        DowntimeEpisode late = Error("aidite6", Local(days[0].AddHours(15)), 18, "Tacka nie pobrana");
        await bBus.SendAsync(new DowntimeSyncReply
        {
            Sender = "B", Version = "test", Group = cfg.Group, RequestId = Guid.NewGuid(), ReplyTo = "A", Episodes = [late],
        }, default);
        await Ticks(1);
        Tick(sameReport: false, shown: late.EpisodeId);

        // B znów odpowiada: pełne okno kończy się tymi samymi postojami — bez przeliczania.
        b.Downtime = bJournal;
        await Ticks(30);
        Tick(sameReport: true);
    }


    private const string QtId = PreviewQtOrchestratorRig.MachineId;
    private const string OtherId = "m2";
    private const string GuardText = "Kolejna próba za 5 min";

    private string SharedConfigFile => Path.Combine(_dir, "shared-config", "shared-config.json");

    private AppOrchestrator BuildQt(int qtPort, PreviewManualClock clock)
    {
        string root = PreviewQtOrchestratorRig.RepositoryRoot();
        var example = ConfigLoader.LoadAppConfig(Path.Combine(root, "config", "config.example.json"));
        var rules = ConfigLoader.LoadRules(Path.Combine(root, "config", "rules.example.json"));
        var sample = example.Machines.First(m => m.Enabled);
        int closed;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            closed = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var cfg = example with
        {
            Machines =
            [
                sample with
                {
                    Id = QtId, Name = "520 (1)", Host = "127.0.0.1", Port = qtPort, Profile = "aidite5x", Enabled = true,
                    RequiresPassword = false, Mode = ConnectionMode.Persistent, Username = null,
                },
                sample with
                {
                    Id = OtherId, Name = "Maszyna 2", Host = "127.0.0.1", Port = closed, Enabled = false, RequiresPassword = false,
                },
            ],
            Paths = example.Paths with
            {
                HistoryFile = Path.Combine(_dir, "data", "history.csv"), LogsDir = Path.Combine(_dir, "logs"),
                CapturesDir = Path.Combine(_dir, "captures"), TestScreensDir = Path.Combine(_dir, "test-ekran"),
            },
        };
        Directory.CreateDirectory(Path.Combine(_dir, "test-ekran"));
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, acks, "TEST-PC", "test");
        var secrets = new InMemorySecretStore();
        var alerts = new AlertStateMachine(clock, cfg.Timing, acks);
        var supervisor = new MonitorSupervisor(cfg, m => secrets.Get(SecretKeys.VncPassword(m.Id)), new NullOcrEngine(),
            new MessageMapper(rules), alerts, clock, new FileCaptureStore(Path.Combine(_dir, "captures")), Path.Combine(_dir, "test-ekran"));
        var power = new PowerModeWatcher();
        var orchestrator = new AppOrchestrator(runtime, supervisor, alerts, new HistoryWriter(cfg.Paths.HistoryFile, clock), power, clock,
            new StartupConfig(cfg, cfg), new SharedConfigSync(new SharedConfigStore(SharedConfigFile), "TEST-PC"),
            new NullOcrEngine(), NullLogger<AppOrchestrator>.Instance,
            new AlarmHelpService(rules, Path.Combine(_dir, "alarm-instructions.json"), false, clock: clock), secrets);
        _disposables.Add(supervisor);
        _disposables.Add(runtime);
        _disposablesSync.Add(power);
        return orchestrator;
    }

    private static MachineStatusSnapshot Card(AppOrchestrator orchestrator, string id) =>
        orchestrator.Supervisor.Snapshots.Single(s => s.MachineId == id);

    private async Task<AppOrchestrator> QtInHoldAsync(FakeQtVncServer hmi, PreviewManualClock clock)
    {
        AppOrchestrator orchestrator = BuildQt(hmi.Port, clock);
        for (int i = 0; i < 30 && !orchestrator.Supervisor.IsRunning; i++)
        {
            await PreviewQtOrchestratorRig.Tick(orchestrator);
            if (!orchestrator.Supervisor.IsRunning) clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(orchestrator.Supervisor.IsRunning, "Monitory nie wystartowały.");
        await PreviewQtOrchestratorRig.WaitUntil(() => Card(orchestrator, QtId).LastError?.Contains(GuardText, StringComparison.Ordinal) == true,
            "bezpiecznik Qt po zamknięciu przed pierwszą klatką");
        Assert.Equal(1, hmi.Accepted);
        return orchestrator;
    }

    [Fact]
    public async Task S6_D3XBezpiecznikiQt_PanelWlaczaInnaMaszyneWTrakcieWstrzymania_PrzebudowaBezPolaczeniaZHmi()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: false);
        var clock = new PreviewManualClock(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        AppOrchestrator orchestrator = await QtInHoldAsync(hmi, clock);
        MachineMonitor before = orchestrator.Supervisor.Get(QtId)!;

        await orchestrator.ApplySharedConfigAsync(null, null, new Dictionary<string, bool> { [QtId] = true, [OtherId] = true });

        await PreviewQtOrchestratorRig.WaitUntil(() => orchestrator.Supervisor.Get(QtId) is { } m && !ReferenceEquals(m, before)
            && orchestrator.Supervisor.Get(OtherId) is not null, "przebudowa monitorów po zapisie przełącznika");
        Assert.True(orchestrator.Config.Machines.Single(m => m.Id == OtherId).Enabled);
        await Task.Delay(2500); // ≥ 2 cykle podtrzymania wstrzymania po 1 s
        Assert.Equal(1, hmi.Accepted);
        MachineStatusSnapshot card = Card(orchestrator, QtId);
        Assert.Equal(MachineConnectionState.Error, card.State);
        Assert.Contains(GuardText, card.LastError);
    }

    [Fact]
    public async Task S7_D3XBezpiecznikiQt_PrzelacznikiZPlikuWarstwy_WylaczenieIWlaczenieWTrakcieWstrzymania_ZeroNowychPolaczen()
    {
        await using var hmi = new FakeQtVncServer(handshake: true, serveFrames: false);
        var clock = new PreviewManualClock(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        AppOrchestrator orchestrator = await QtInHoldAsync(hmi, clock);
        var otherComputer = new SharedConfigStore(SharedConfigFile);

        otherComputer.Save(null, null, clock.UtcNow, "PC-B", machines: new Dictionary<string, bool> { [QtId] = false, [OtherId] = true });
        await orchestrator.ReloadSharedConfigFromDiskAsync();
        Assert.Equal(MachineConnectionState.Disabled, Card(orchestrator, QtId).State);

        clock.Advance(TimeSpan.FromSeconds(30));
        otherComputer.Save(null, null, clock.UtcNow, "PC-B", machines: new Dictionary<string, bool> { [QtId] = true, [OtherId] = true });
        await orchestrator.ReloadSharedConfigFromDiskAsync();
        await PreviewQtOrchestratorRig.WaitUntil(() => Card(orchestrator, QtId) is { State: MachineConnectionState.Error } c
            && c.LastError?.Contains(GuardText, StringComparison.Ordinal) == true, "karta 520 (1) z opisem wstrzymania po ponownym włączeniu");
        await Task.Delay(2500);
        Assert.Equal(1, hmi.Accepted);
        Assert.Contains(GuardText, Card(orchestrator, QtId).LastError);
    }
}
