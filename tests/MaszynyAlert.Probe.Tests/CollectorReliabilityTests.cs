using System.Text.Json;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Probe.Diagnoza;
using MaszynyAlert.Probe.Tests.TestUtil;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Probe.Tests;

public sealed class CollectorReliabilityTests
{
    [Fact]
    public async Task BrakAktualizacjiPrzyrostowej_NieKonczyKolorowIZachowujeSerieKlatek()
    {
        RgbaImage screen = DiagnozaTestConfig.WithRed(32, 32, new RectInt(0, 0, 16, 8));
        for (int y = 16; y < 24; y++)
            for (int x = 0; x < 16; x++) screen.SetPixel(x, y, new Rgba(255, 255, 0));
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = screen,
            IncrementalNoResponseWhenUnchanged = true,
        });
        string dir = DiagnozaTestConfig.TempDir("collector-incremental");
        try
        {
            var machine = DiagnozaTestConfig.Machine("premile", server.Port);
            using var report = new Report(dir, "test");
            using var collector = new MachineCollector(machine, DiagnozaTestConfig.Config(machine), Path.Combine(dir, "machine"),
                Options(), MachineMode.Trwale, null, null, report, []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(14));
            await collector.RunAsync(timeout.Token);

            Assert.Equal(1, server.ConnectionCount);
            Assert.Equal(2, collector.Stats.EpisodesClosed);
            string[] records = File.ReadAllLines(Path.Combine(dir, "machine", "epizody.jsonl"));
            Assert.Equal(2, records.Length);
            foreach (string row in records)
            {
                using var episode = JsonDocument.Parse(row);
                Assert.True(episode.RootElement.GetProperty("trwanieSek").GetDouble() >= 12, row);
                string color = episode.RootElement.GetProperty("kolor").GetString()!;
                foreach (int offset in new[] { 0, 2, 10 })
                    Assert.Single(Directory.GetFiles(Path.Combine(dir, "machine"), $"*{color}-{offset}s.png", SearchOption.AllDirectories));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LimitPrzyOcr_KonczyZapisWszystkichDanychIDomykaEpizodWRezerwie()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = DiagnozaTestConfig.WithRed(32, 32, new RectInt(4, 20, 24, 6)),
            IncrementalNoResponseWhenUnchanged = true,
        });
        string dir = DiagnozaTestConfig.TempDir("collector-limit-ocr");
        try
        {
            var machine = DiagnozaTestConfig.Machine("premile", server.Port);
            var options = Options() with { LimitBytes = 100 * 1024 };
            var ocr = new QueueOcrEngine().EnqueueText("Alarm testowy").EnqueueText(new string('X', 100 * 1024));
            using var report = new Report(dir, "test");
            string machineDir = Path.Combine(dir, "machine");
            using var collector = new MachineCollector(machine, DiagnozaTestConfig.Config(machine), machineDir,
                options, MachineMode.Trwale, null, null, report, [ocr]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await collector.RunAsync(timeout.Token);

            Assert.False(timeout.IsCancellationRequested, "kolektor nie zakończył się po limicie");
            Assert.True(collector.Stats.OverLimit);
            Assert.Contains("limit", collector.Stats.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(DiskBudget.Size(machineDir), 1, options.LimitBytes);
            Assert.Single(Directory.GetFiles(machineDir, "*.json", SearchOption.AllDirectories)); // tylko pierwszy OCR
            Assert.Single(File.ReadAllLines(Path.Combine(machineDir, "epizody.jsonl")));
            long bytes = DiskBudget.Size(machineDir);
            await Task.Delay(150);
            Assert.Equal(bytes, DiskBudget.Size(machineDir));

            DlugiKolektor.WriteSummary([collector], dir, options, DateTimeOffset.Now.AddMinutes(-1), report, [], final: true);
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "podsumowanie.json")));
            Assert.True(summary.RootElement.GetProperty("zakonczony").GetBoolean());
            Assert.True(summary.RootElement.GetProperty("maszyny").GetProperty("premile").GetProperty("limitPrzekroczony").GetBoolean());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ZadanieStop_WymagaWlasnegoPidIStartu_IDomykaEpizodPrzedPodsumowaniem()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = DiagnozaTestConfig.WithRed(32, 32, new RectInt(4, 20, 24, 6)),
            IncrementalNoResponseWhenUnchanged = true,
        });
        string dir = DiagnozaTestConfig.TempDir("collector-stop");
        try
        {
            var machine = DiagnozaTestConfig.Machine("premile", server.Port);
            var global = new GlobalOptions(dir, null, true, All: true);
            var list = new MachineListResult(DiagnozaTestConfig.Config(machine), "test", [machine], true);
            using var report = new Report(dir, "test");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<int> run = DlugiKolektor.RunAsync(list, Options(), dir, global, new ProbeSession(global), report, [], [],
                new Dictionary<string, string?>(), timeout.Token);
            try
            {
                string summaryPath = Path.Combine(dir, "dlugi", "podsumowanie.json");
                using var initial = JsonDocument.Parse(File.ReadAllText(summaryPath));
                string startedUtc = initial.RootElement.GetProperty("startedUtc").GetString()!;
                Assert.Equal(Environment.ProcessId, initial.RootElement.GetProperty("pid").GetInt32());
                Assert.False(initial.RootElement.GetProperty("zakonczony").GetBoolean());
                string request = Path.Combine(dir, "dlugi", DlugiKolektor.StopRequestFileName);

                File.WriteAllText(request, JsonSerializer.Serialize(new { pid = -1, startedUtc }));
                await Task.Delay(1200, timeout.Token);
                Assert.False(run.IsCompleted, "obcy PID zatrzymał kolektor");
                File.WriteAllText(request, JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedUtc = "stara-instancja" }));
                await Task.Delay(1200, timeout.Token);
                Assert.False(run.IsCompleted, "żądanie starej instancji zatrzymało kolektor");
                File.WriteAllText(request, "{niepełny JSON");
                await Task.Delay(1200, timeout.Token);
                Assert.False(run.IsCompleted, "niepełny JSON zatrzymał kolektor");
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir, "dlugi", "premile"), "*czerwien-0s.png", SearchOption.AllDirectories));

                File.WriteAllText(request, JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedUtc }));
                Assert.Equal(ProbeExitCodes.AllConnected, await run.WaitAsync(TimeSpan.FromSeconds(6)));
                Assert.Single(File.ReadAllLines(Path.Combine(dir, "dlugi", "premile", "epizody.jsonl")));
                using var final = JsonDocument.Parse(File.ReadAllText(summaryPath));
                Assert.Equal(startedUtc, final.RootElement.GetProperty("startedUtc").GetString());
                Assert.True(final.RootElement.GetProperty("zakonczony").GetBoolean());
                Assert.False(File.Exists(summaryPath + ".tmp"));
            }
            finally
            {
                timeout.Cancel();
                await run;
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task NiedostepnyDziennik_KonczyKolektorIPrzekazujeBladDoPodsumowania()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions());
        string dir = DiagnozaTestConfig.TempDir("collector-io");
        try
        {
            var machine = DiagnozaTestConfig.Machine("premile", server.Port);
            string machineDir = Path.Combine(dir, "machine");
            Directory.CreateDirectory(Path.Combine(machineDir, $"dziennik-{DateTimeOffset.Now:yyyy-MM-dd}.txt"));
            using var report = new Report(dir, "test");
            using var collector = new MachineCollector(machine, DiagnozaTestConfig.Config(machine), machineDir,
                Options(), MachineMode.Trwale, null, null, report, []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await collector.RunAsync(timeout.Token);
            Assert.False(timeout.IsCancellationRequested);
            Assert.True(collector.Stats.WriteFailed);
            Assert.Equal(0, server.ConnectionCount);
            Assert.True(DlugiKolektor.WriteSummary([collector], dir, Options(), DateTimeOffset.Now, report, [], final: true));
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "podsumowanie.json")));
            Assert.True(result.RootElement.GetProperty("maszyny").GetProperty("premile").GetProperty("bladZapisu").GetBoolean());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task BladPierwszegoPodsumowania_NieUruchamiaPolaczenIZwracaKod1()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions());
        string dir = DiagnozaTestConfig.TempDir("collector-summary-io");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "dlugi", "podsumowanie.json"));
            var machine = DiagnozaTestConfig.Machine("premile", server.Port);
            var global = new GlobalOptions(dir, null, true, All: true);
            var list = new MachineListResult(DiagnozaTestConfig.Config(machine), "test", [machine], true);
            string reportPath;
            using (var report = new Report(dir, "test"))
            {
                reportPath = report.Path;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                int code = await DlugiKolektor.RunAsync(list, Options(), dir, global, new ProbeSession(global), report, [], [],
                    new Dictionary<string, string?>(), timeout.Token);
                Assert.Equal(ProbeExitCodes.Error, code);
            }
            Assert.Equal(0, server.ConnectionCount);
            Assert.Contains("Nie udało się zapisać podsumowania", File.ReadAllText(reportPath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("okresowe")]
    [InlineData("ocr")]
    public async Task BladKataloguPngLubOcr_KonczyZapisBezPonawianiaPolaczen(string subdirectory)
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions());
        string dir = DiagnozaTestConfig.TempDir("collector-directory-io");
        try
        {
            var machine = DiagnozaTestConfig.Machine("premile", server.Port);
            string machineDir = Path.Combine(dir, "machine");
            string day = Path.Combine(machineDir, DateTimeOffset.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, subdirectory), "zamiast katalogu");
            using var report = new Report(dir, "test");
            using var collector = new MachineCollector(machine, DiagnozaTestConfig.Config(machine), machineDir,
                Options(), MachineMode.Trwale, null, null, report, [new QueueOcrEngine()]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await collector.RunAsync(timeout.Token);
            Assert.False(timeout.IsCancellationRequested);
            Assert.True(collector.Stats.WriteFailed);
            Assert.Equal(1, server.ConnectionCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task PrzerwaOdpytywania_NieJestLiczonaJakoPotwierdzonyCzasKoloru()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            Screen = DiagnozaTestConfig.WithRed(32, 32, new RectInt(4, 20, 24, 6)),
            IncrementalNoResponseWhenUnchanged = true,
        });
        string dir = DiagnozaTestConfig.TempDir("collector-poll-gap");
        try
        {
            var machine = DiagnozaTestConfig.Machine("premile", server.Port);
            using var report = new Report(dir, "test");
            using var collector = new MachineCollector(machine, DiagnozaTestConfig.Config(machine), Path.Combine(dir, "machine"),
                Options() with { OdpytywanieTrwaSek = 1 }, MachineMode.Odpytywanie, null, null, report, []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await collector.RunAsync(timeout.Token);
            string row = Assert.Single(File.ReadAllLines(Path.Combine(dir, "machine", "epizody.jsonl")));
            using var episode = JsonDocument.Parse(row);
            Assert.Equal("przerwa w obserwacji", episode.RootElement.GetProperty("powodZakonczenia").GetString());
            Assert.True(episode.RootElement.GetProperty("trwanieSek").GetDouble() < 4, row);
            Assert.Equal(1, server.ConnectionCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(FileShare.ReadWrite | FileShare.Delete, 0)] // Read-KolektorSummary (runner, eksport) trzyma plik przez cały zapis
    [InlineData(FileShare.Read, 400)] // Notatnik/Get-Content/antywirus: krótko, bez FileShare.Delete
    public async Task Podsumowanie_OtwartePrzezCzytelnika_NieGubiZapisuINieKonczyKolektora(FileShare share, int holdMs)
    {
        string dir = DiagnozaTestConfig.TempDir("summary-reader");
        try
        {
            using var report = new Report(dir, "test");
            Assert.True(DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: false));
            string path = Path.Combine(dir, "podsumowanie.json");
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            Task release = holdMs == 0 ? Task.CompletedTask : Task.Run(async () =>
            {
                await Task.Delay(holdMs);
                reader.Dispose();
            });
            bool saved;
            try
            {
                saved = DlugiKolektor.WriteSummary([], dir, Options(), DateTimeOffset.Now, report, [], final: true);
            }
            finally
            {
                await release;
                reader.Dispose();
            }

            Assert.True(saved, "podsumowanie nie zapisane mimo czytelnika");
            using var summary = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(summary.RootElement.GetProperty("zakonczony").GetBoolean());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OdmowaZFazy0_JestWPierwszymPodsumowaniu()
    {
        string dir = DiagnozaTestConfig.TempDir("summary-phase0");
        try
        {
            var machine = DiagnozaTestConfig.Machine("amm580", 5900);
            const string reason = "serwer odrzucił podane hasło VNC — to hasło jest dla tej maszyny nieprawidłowe.";
            using var report = new Report(dir, "test");
            using var collector = new MachineCollector(machine, DiagnozaTestConfig.Config(machine), Path.Combine(dir, "amm580"),
                Options(), MachineMode.Trwale, "SYNTHETIC_ONLY", null, report, [], reason);
            Assert.True(DlugiKolektor.WriteSummary([collector], dir, Options(), DateTimeOffset.Now, report, [], final: false));
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "podsumowanie.json")));
            JsonElement row = summary.RootElement.GetProperty("maszyny").GetProperty("amm580");
            Assert.NotEqual("-", row.GetProperty("status").GetString());
            Assert.Equal(reason, row.GetProperty("powod").GetString());
            Assert.DoesNotContain("SYNTHETIC_ONLY", File.ReadAllText(Path.Combine(dir, "podsumowanie.json")), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static DlugiOptions Options() => new(null, 600, 300, 15, 1, 1, 600, 1024 * 1024, true);
}
