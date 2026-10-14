using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
using Xunit.Abstractions;

namespace MaszynyAlert.App.Tests;

/// <summary>Orkiestrator z plikami w katalogu roboczym testu (jak <c>DowntimeChartsViewTests</c>; monitory nieuruchomione).</summary>
internal sealed class PanelTestHost : IAsyncDisposable
{
    public const string WorkVariable = "VNCALERT_REAL_DOWNTIME_WORK";

    private readonly string _dir = Path.Combine(
        Environment.GetEnvironmentVariable(WorkVariable) is { Length: > 0 } work ? work : AppContext.BaseDirectory,
        "panel-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _disposablesSync = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables) await d.DisposeAsync();
        foreach (var d in _disposablesSync) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Katalog danych (<c>downtime.jsonl</c> obok <c>history.csv</c>, <c>state\observation.jsonl</c>).</summary>
    public string DataDirectory(bool clean = false)
    {
        string data = Path.Combine(_dir, "data");
        if (clean && Directory.Exists(data)) Directory.Delete(data, recursive: true);
        Directory.CreateDirectory(Path.Combine(data, "state"));
        return data;
    }

    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public DateTimeOffset Now => DateTimeOffset.Now;
        public long MonotonicMs => Environment.TickCount64;
    }

    public sealed class ManualClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start.ToUniversalTime();
        public DateTimeOffset Now => UtcNow.ToLocalTime();
        public long MonotonicMs { get; private set; } = 1_000_000;
        public void Advance(TimeSpan delta) { UtcNow += delta; MonotonicMs += (long)delta.TotalMilliseconds; }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }

    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public AppOrchestrator Orchestrator(string? source, IClock? clock = null)
    {
        string root = RepositoryRoot();
        var example = ConfigLoader.LoadAppConfig(Path.Combine(root, "config", "config.example.json"));
        var rules = ConfigLoader.LoadRules(Path.Combine(root, "config", "rules.example.json"));
        if (source is not null && File.Exists(Path.Combine(source, "rules.json")))
        {
            try { rules = ConfigLoader.LoadRules(Path.Combine(source, "rules.json")); }
            catch (ConfigException) { }
        }
        var sample = example.Machines.First(m => m.Enabled);
        List<MachineConfig> machines;
        if (source is not null && File.Exists(Path.Combine(source, "config.json")))
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "config.json")))!.AsObject();
            machines = (node["machines"]?.AsArray() ?? []).Select(n => sample with
            {
                Id = (string)n!["id"]!, Name = (string?)n["name"] ?? (string)n["id"]!, Host = "127.0.0.1", Port = ClosedPort(),
                Profile = example.Profiles.ContainsKey((string?)n["profile"] ?? "") ? (string)n["profile"]! : sample.Profile,
                Enabled = (bool?)n["enabled"] ?? true,
            }).ToList();
        }
        else
            machines = Enumerable.Range(1, 7).Select(i => sample with
            {
                Id = "aidite" + i, Name = "Aidite " + i, Host = "127.0.0.1", Port = ClosedPort(), Enabled = true,
            }).ToList();
        string history = Path.Combine(_dir, "data", "history.csv");
        var cfg = example with
        {
            Machines = machines,
            Paths = example.Paths with
            {
                HistoryFile = history, LogsDir = Path.Combine(_dir, "logs"),
                CapturesDir = Path.Combine(_dir, "captures"), TestScreensDir = Path.Combine(_dir, "test-ekran"),
            },
        };
        Directory.CreateDirectory(Path.Combine(_dir, "test-ekran"));
        clock ??= new RealClock();
        var secrets = new InMemorySecretStore();
        var acks = new AckMemory(clock, TimeSpan.FromHours(2));
        var alerts = new AlertStateMachine(clock, cfg.Timing, acks);
        var supervisor = new MonitorSupervisor(cfg, _ => "haslo", new NullOcrEngine(), new MessageMapper(rules), alerts, clock,
            new FileCaptureStore(Path.Combine(_dir, "captures")), Path.Combine(_dir, "test-ekran"));
        var runtime = new ClusterRuntime(new ClusterConfig(), null, clock, acks, "TEST-PC", "test");
        var power = new PowerModeWatcher();
        var orchestrator = new AppOrchestrator(runtime, supervisor, alerts, new HistoryWriter(history, clock), power, clock,
            new StartupConfig(cfg, cfg),
            new SharedConfigSync(new SharedConfigStore(Path.Combine(_dir, "shared-config", "shared-config.json")), "TEST-PC"),
            new NullOcrEngine(), NullLogger<AppOrchestrator>.Instance,
            new AlarmHelpService(rules, Path.Combine(_dir, "alarm-instructions.json"), false, clock: clock), secrets);
        _disposables.Add(supervisor); _disposables.Add(runtime); _disposablesSync.Add(power);
        return orchestrator;
    }

    public static List<DowntimeEpisode> SyntheticHall(DateTimeOffset from, DateTimeOffset to)
    {
        var random = new Random(12);
        var result = new List<DowntimeEpisode>();
        string[] errors = ["Servo 6", "Bazowanie (Home)", "Tacka nie pobrana", "Ciśnienie powietrza", "Błąd: X", "Narzędzie nie wróciło"];
        for (int m = 1; m <= 7; m++)
            for (var day = from; day < to; day = day.AddDays(1))
            {
                bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                double rate = weekend ? 0.25 : 1.0;
                var cursor = day;
                void Add(int count, string reason, string category, double meanMinutes)
                {
                    for (int i = 0; i < count; i++)
                    {
                        cursor = cursor.AddMinutes(random.Next(5, 60));
                        var stop = cursor.AddMinutes(Math.Max(0.5, -meanMinutes * Math.Log(1 - random.NextDouble())));
                        if (stop > to) return;
                        result.Add(new DowntimeEpisode("aidite" + m, "Aidite " + m, reason, cursor, stop, stop,
                            EpisodeId: Guid.NewGuid(), Category: category));
                        cursor = stop;
                    }
                }
                Add((int)Math.Round(6 * rate), "Stoi bez pracy (100 %) – dołóż prace", DowntimeCategories.Idle, 33);
                Add((int)Math.Round(3.5 * rate), errors[random.Next(errors.Length)], "inny", 20);
                Add((int)Math.Round(4.6 * rate), "Stoi na 0 % – wciśnij Start", DowntimeCategories.IdleAtZero, 5);
            }
        return result;
    }
}
