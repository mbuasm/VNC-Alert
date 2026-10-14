using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using Xunit.Abstractions;

namespace MaszynyAlert.Export.Tests;

public sealed class RealDowntimeFactAttribute : FactAttribute
{
    /// <summary>Katalog z kopią dzienników (tylko odczyt; test pracuje na własnej kopii w katalogu wyjścia testów).</summary>
    public const string DirectoryVariable = "VNCALERT_REAL_DOWNTIME_DIR";

    /// <summary>Opcjonalny katalog na wyniki (tabela pomiarów, zrzut sum, tekst PDF, komórki XLSX, CSV) — do porównania wersji.</summary>
    public const string OutputVariable = "VNCALERT_REAL_DOWNTIME_OUT";

    /// <summary>Opcjonalna etykieta plików wyników (np. „main”).</summary>
    public const string LabelVariable = "VNCALERT_REAL_DOWNTIME_LABEL";

    /// <summary>Opcjonalny katalog na kopie robocze (domyślnie katalog wyjścia testów); kopie są kasowane po teście.</summary>
    public const string WorkVariable = "VNCALERT_REAL_DOWNTIME_WORK";

    public RealDowntimeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DirectoryVariable)))
            Skip = $"Brak zmiennej {DirectoryVariable} (katalog z kopią downtime.jsonl) — pomiar na prawdziwych danych tylko ręcznie.";
    }
}

[Collection(GlobalFontStateCollection.Name)]
public sealed class RealDowntimeDataTests(ITestOutputHelper output)
{
    private const int Repetitions = 5;
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Stage(string Name, double MinMs, double MedianMs, double MaxMs, long MedianAllocated, long PeakWorkingSet);

    private readonly List<Stage> _stages = [];
    private readonly StringBuilder _dump = new();
    private readonly List<string> _problems = [];

    private sealed record Range(string Name, DateOnly? From, DateOnly? To);

    [RealDowntimeFact]
    public void PrawdziweDane_CzasPamiecIZgodnoscSum()
    {
        string source = Environment.GetEnvironmentVariable(RealDowntimeFactAttribute.DirectoryVariable)!;
        string work = Path.Combine(WorkRoot(), "real-downtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            Run(source, work);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Skala: te same rekordy powielone k razy (nowe identyfikatory, czas cofnięty o k × 3 tygodnie — ten sam układ dni tygodnia).
    /// Plik: konstruktor <see cref="DowntimeJournal"/> (start programu). Pamięć: odświeżenie Panelu (Build ze WSZYSTKICH epizodów co
    /// sekundę, filtr tygodnia) i wykresy tygodnia — koszt na wątku UI przy rosnącej historii.
    /// </summary>
    [RealDowntimeFact]
    public void PrawdziweDane_Skalowanie()
    {
        string source = Environment.GetEnvironmentVariable(RealDowntimeFactAttribute.DirectoryVariable)!;
        string work = Path.Combine(WorkRoot(), "real-downtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string downtime = Path.Combine(source, "downtime.jsonl");
            var machines = ReadConfig(Path.Combine(source, "config.json"));
            var rules = ReadRules(Path.Combine(source, "rules.json"));
            string[] lines = File.ReadLines(downtime).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            foreach (int k in new[] { 1, 4, 16, 32 })
            {
                string path = Path.Combine(work, $"x{k}.jsonl");
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                    for (int copy = 0; copy < k; copy++)
                        foreach (string line in lines)
                            writer.Write("\n" + (copy == 0 ? line : Shifted(line, copy)) + "\n");
                long bytes = new FileInfo(path).Length;
                var journal = Measure($"x{k}: wczytanie {bytes / 1048576.0:0.0} MB, {lines.Length * k} rekordów", () => new DowntimeJournal(path));
                Line($"x{k}: {bytes} B, epizodów {journal.Snapshot().Count}");
                File.Delete(path);
            }
            string baseCopy = Path.Combine(work, "baza.jsonl");
            File.Copy(downtime, baseCopy);
            var baseEpisodes = new DowntimeJournal(baseCopy).Snapshot();
            var now = new DateTimeOffset(baseEpisodes.Max(e => e.LastObservedAt).UtcDateTime.AddMinutes(5), TimeSpan.Zero);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Warsaw).DateTime);
            var options = new DowntimeChartOptions
            {
                From = today.AddDays(-6), To = today, GeneratedAt = now, Zone = Warsaw,
                Machines = machines.Where(m => m.Enabled).Select(m => new ChartMachine(m.Id, m.Name)).ToArray(),
            };
            foreach (int k in new[] { 1, 10, 100, 450 })
            {
                var episodes = Enumerable.Range(0, k).SelectMany(copy => baseEpisodes.Select(e => copy == 0 ? e : e with
                {
                    EpisodeId = CopyId(e.EpisodeId, copy), StartedAt = e.StartedAt.AddDays(-21 * copy),
                    LastObservedAt = e.LastObservedAt.AddDays(-21 * copy), EndedAt = e.EndedAt?.AddDays(-21 * copy),
                })).ToArray();
                DowntimeSlice[] week = [];
                Measure($"{episodes.Length} epizodów: odświeżenie Panelu (Build wszystkich + filtr tygodnia + sumy)", () =>
                {
                    var rows = DowntimeReport.Build(episodes, Warsaw, new DowntimeCategoryClassifier(rules, machines), now)
                        .Where(s => s.Day >= options.From && s.Day <= options.To).ToArray();
                    GC.KeepAlive(DowntimeReport.Totals(rows));
                    GC.KeepAlive(DowntimeReport.ByKind(rows));
                    week = rows;
                    return rows.Length;
                });
                Measure($"{episodes.Length} epizodów: wykresy tygodnia", () => DowntimeCharts.Build(week, options).ContentKey().Length);
                Measure($"{episodes.Length} epizodów: cała historia (Build + wykresy + DowntimeExportData.Create)", () =>
                {
                    var all = DowntimeReport.Build(episodes, Warsaw, new DowntimeCategoryClassifier(rules, machines), now);
                    var data = DowntimeExportData.Create(all, null, null, null, [], machines, Warsaw, now, "WER-PC",
                        DowntimeExportData.ScopeTextFor(false, null));
                    return data.Episodes.Count;
                });
            }
            Report("skala-");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static string Shifted(string line, int copy)
        {
            var node = JsonNode.Parse(line)!.AsObject();
            node["EpisodeId"] = CopyId(Guid.Parse((string)node["EpisodeId"]!), copy).ToString();
            foreach (string key in new[] { "StartedAt", "LastObservedAt", "EndedAt", "End" })
                if (node[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    node[key] = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).AddDays(-21 * copy).ToString("O", CultureInfo.InvariantCulture);
            return node.ToJsonString();
        }
    }

    [RealDowntimeFact]
    public void SyntetycznaHala_ProjekcjaOdswiezeniaPanelu()
    {
        var machines = Enumerable.Range(1, 7).Select(i => new MachineConfig
        {
            Id = "aidite" + i, Name = "Aidite " + i, Host = "127.0.0.1", Profile = "opencnc", Enabled = true,
        }).ToArray();
        var end = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        foreach (int months in new[] { 1, 3, 6, 12, 24 })
        {
            var episodes = SyntheticHall(machines, end.AddDays(-30 * months), end, seed: months);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, Warsaw).DateTime);
            var rules = new RuleSet();
            Measure($"{months} mies. ({episodes.Count} epizodów): odświeżenie Panelu (Build wszystkich + filtr tygodnia + sumy)", () =>
            {
                var rows = DowntimeReport.Build(episodes, Warsaw, new DowntimeCategoryClassifier(rules, machines), end)
                    .Where(s => s.Day >= today.AddDays(-6) && s.Day <= today).ToArray();
                GC.KeepAlive(DowntimeReport.Totals(rows));
                GC.KeepAlive(DowntimeReport.ByKind(rows));
                return rows.Length;
            });
            long fileBytes = episodes.Sum(e => (long)Math.Max(1, (e.End - e.StartedAt).TotalSeconds / 10) * 345);
            Line($"{months} mies.: {episodes.Count} epizodów, z tego bez pracy {episodes.Count(DowntimeReport.IsIdleEpisode)}; "
                + $"szacowany downtime.jsonl przy zapisie co 10 s: {fileBytes / 1048576.0:0} MB");
        }
        Report("hala-");
    }

    private static List<DowntimeEpisode> SyntheticHall(MachineConfig[] machines, DateTimeOffset from, DateTimeOffset to, int seed)
    {
        var random = new Random(seed);
        var result = new List<DowntimeEpisode>();
        string[] errors = ["Servo 6", "Bazowanie (Home)", "Tacka nie pobrana", "Ciśnienie powietrza", "Błąd: X", "Narzędzie nie wróciło"];
        foreach (var machine in machines)
            for (var day = from; day < to; day = day.AddDays(1))
            {
                bool weekend = TimeZoneInfo.ConvertTime(day, Warsaw).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                double rate = weekend ? 0.25 : 1.0;
                var cursor = day;
                void Add(int count, string reason, string category, double meanMinutes)
                {
                    for (int i = 0; i < count; i++)
                    {
                        cursor = cursor.AddMinutes(random.Next(5, 60));
                        double minutes = Math.Max(0.5, -meanMinutes * Math.Log(1 - random.NextDouble()));
                        var stop = cursor.AddMinutes(minutes);
                        result.Add(new DowntimeEpisode(machine.Id, machine.Name, reason, cursor, stop, stop,
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

    /// <summary>
    /// Symulacja kompaktowania BEZ zmiany formatu: z pliku zostaje
    /// ostatni wiersz każdego epizodu (bajt w bajt, ten sam klucz co przy odczycie: <c>EpisodeId</c> albo <c>LegacyId</c>), wiersze
    /// nieczytelne zostają bez zmian. Stan dziennika po wczytaniu musi być identyczny, a raport — ten sam.
    /// </summary>
    [RealDowntimeFact]
    public void PrawdziweDane_KompaktowanieOstatniWierszEpizodu_TenSamStan()
    {
        string source = Environment.GetEnvironmentVariable(RealDowntimeFactAttribute.DirectoryVariable)!;
        string work = Path.Combine(WorkRoot(), "real-downtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string original = Path.Combine(work, "oryginal.jsonl");
            File.Copy(Path.Combine(source, "downtime.jsonl"), original);
            var lastLine = new Dictionary<Guid, (int Order, string Line)>();
            var unreadable = new List<(int Order, string Line)>();
            int order = 0;
            foreach (string line in File.ReadLines(original))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                order++;
                try
                {
                    var item = System.Text.Json.JsonSerializer.Deserialize<DowntimeEpisode>(line);
                    if (item is null) { unreadable.Add((order, line)); continue; }
                    var key = item.EpisodeId == Guid.Empty ? DowntimeEpisode.LegacyId(item.MachineId, item.StartedAt) : item.EpisodeId;
                    lastLine[key] = (order, line);
                }
                catch (System.Text.Json.JsonException) { unreadable.Add((order, line)); }
            }
            string compacted = Path.Combine(work, "skompaktowany.jsonl");
            using (var writer = new StreamWriter(compacted, false, new UTF8Encoding(false)))
                foreach (var (_, line) in lastLine.Values.Concat(unreadable).OrderBy(x => x.Order))
                    writer.Write("\n" + line + "\n");
            Line($"kompaktowanie: {new FileInfo(original).Length} B, {order} rekordów → {new FileInfo(compacted).Length} B, "
                + $"{lastLine.Count + unreadable.Count} rekordów (nieczytelnych {unreadable.Count})");

            var before = Measure("wczytanie oryginału", () => new DowntimeJournal(original));
            var after = Measure("wczytanie po kompaktowaniu", () => new DowntimeJournal(compacted));
            var a = before.Snapshot().OrderBy(e => e.EpisodeId).ToArray();
            var b = after.Snapshot().OrderBy(e => e.EpisodeId).ToArray();
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
            Assert.Equal(before.Error, after.Error);
            var now = a.Max(e => e.LastObservedAt).AddMinutes(5);
            var ra = DowntimeReport.Build(a, Warsaw, null, now);
            var rb = DowntimeReport.Build(b, Warsaw, null, now);
            Assert.Equal(ra.Count, rb.Count);
            for (int i = 0; i < ra.Count; i++) Assert.Equal(ra[i], rb[i]);
            // Po kompaktowaniu ten sam epizod dalej obserwowany: dziennik dopisuje dalej (format wiersza bez zmian).
            var open = a.First();
            Assert.True(after.Merge(open with { LastObservedAt = open.LastObservedAt.AddMinutes(1), EndedAt = null, Interrupted = false }));
            Line($"po dopisaniu do skompaktowanego: {File.ReadLines(compacted).Count(l => !string.IsNullOrWhiteSpace(l))} rekordów");
            Report("kompaktowanie-");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string WorkRoot() =>
        Environment.GetEnvironmentVariable(RealDowntimeFactAttribute.WorkVariable) is { Length: > 0 } work ? work : AppContext.BaseDirectory;

    private static Guid CopyId(Guid id, int copy)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        bytes[0] ^= (byte)copy;
        bytes[1] ^= (byte)(copy >> 8);
        bytes[15] ^= 0x5A;
        return new Guid(bytes);
    }

    private void Run(string source, string work)
    {
        string downtimeSource = Path.Combine(source, "downtime.jsonl");
        Assert.True(File.Exists(downtimeSource), "Brak " + downtimeSource);
        string? observationSource = File.Exists(Path.Combine(source, "observation.jsonl")) ? Path.Combine(source, "observation.jsonl") : null;
        var machines = ReadConfig(Path.Combine(source, "config.json"));
        var rules = ReadRules(Path.Combine(source, "rules.json"));
        long fileBytes = new FileInfo(downtimeSource).Length;
        int fileLines = File.ReadLines(downtimeSource).Count(l => !string.IsNullOrWhiteSpace(l));
        Line($"plik downtime.jsonl: {fileBytes} B, {fileLines} rekordów; maszyny w konfiguracji: "
            + string.Join(", ", machines.Select(m => $"{m.Id}={m.Name}{(m.Enabled ? "" : " (wył.)")}")));

        int copy = 0;
        string NextCopy(string file)
        {
            string target = Path.Combine(work, $"{copy++}-{Path.GetFileName(file)}");
            File.Copy(file, target);
            return target;
        }
        var journal = Measure("wczytanie downtime.jsonl (konstruktor DowntimeJournal)", () => new DowntimeJournal(NextCopy(downtimeSource)));
        Assert.Null(journal.Error);
        var episodes = Measure("DowntimeJournal.Snapshot()", () => journal.Snapshot());
        IReadOnlyList<ObservationInterval> observations = observationSource is null ? []
            : Measure("wczytanie observation.jsonl + Snapshot()", () => new ObservationJournal(NextCopy(observationSource)).Snapshot());
        Line($"epizody w pamięci: {episodes.Count}; przedziały obserwacji: {observations.Count}; bez MachineId: "
            + episodes.Count(e => string.IsNullOrEmpty(e.MachineId)));

        var lastObserved = episodes.Max(e => e.LastObservedAt);
        var now = new DateTimeOffset(lastObserved.UtcDateTime.AddMinutes(5).Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
        Line($"teraz (stała chwila pomiaru): {now:O}");

        var all = DowntimeReport.Build(episodes, Warsaw, new DowntimeCategoryClassifier(rules, machines), now);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Warsaw).DateTime);
        var lastDay = all.Max(s => s.Day);
        Range[] ranges =
        [
            new("dzien", lastDay, lastDay),
            new("tydzien", today.AddDays(-6), today),
            new("miesiac", today.AddDays(-29), today),
            new("cala-historia", null, null),
        ];

        foreach (var range in ranges)
            RunRange(range, episodes, observations, machines, rules, now);

        RunMissingMachineIdVariant(NextCopy(downtimeSource), work, machines, rules, now, observations);
        RunNameAndHistoryVariants(episodes, machines, rules, now, observations);

        Report();
        Assert.True(_problems.Count == 0, "Rozjazdy:" + Environment.NewLine + string.Join(Environment.NewLine, _problems));
    }

    private void RunRange(Range range, IReadOnlyList<DowntimeEpisode> episodes, IReadOnlyList<ObservationInterval> observations,
        MachineConfig[] machines, RuleSet rules, DateTimeOffset now)
    {
        string r = range.Name;
        var from = range.From ?? DateOnly.MinValue;
        var to = range.To ?? DateOnly.MaxValue;
        // Odświeżenie Panelu (DowntimeView.Refresh, wątek UI co 1 s): nowy klasyfikator, Build ze wszystkich epizodów, filtr, sumy,
        // wiersze obu tabel. Bez WPF — samo liczenie.
        var slices = Measure($"[{r}] Panel: klasyfikator + Build + filtr + sumy + wiersze tabel", () =>
        {
            var classifier = new DowntimeCategoryClassifier(rules, machines);
            var rows = DowntimeReport.Build(episodes, Warsaw, classifier, now)
                .Where(s => s.Day >= from && s.Day <= to).ToArray();
            var totals = DowntimeReport.Totals(rows);
            var kinds = DowntimeReport.ByKind(rows);
            double minutes = totals.All.Minutes;
            var summary = rows.GroupBy(s => s.Day).OrderByDescending(g => g.Key)
                .SelectMany(day => new[] { (day.Key, "Razem", day.Sum(s => s.Minutes)) }
                    .Concat(day.GroupBy(s => s.Shift).OrderBy(g => g.Key).Select(g => (day.Key, g.Key, g.Sum(s => s.Minutes)))))
                .ToArray();
            var table = rows.OrderByDescending(s => s.Start).Select(s => (s.Day, s.Shift, s.Machine, s.Reason, s.Minutes,
                s.Start.ToLocalTime().ToString("dd.MM HH:mm:ss"), s.End.ToLocalTime().ToString("dd.MM HH:mm:ss"))).ToArray();
            GC.KeepAlive(kinds); GC.KeepAlive(summary); GC.KeepAlive(table); GC.KeepAlive(minutes);
            return rows;
        });
        var options = new DowntimeChartOptions
        {
            From = range.From, To = range.To, Shift = null, LinePerMachine = false, IncludeIdle = true, GeneratedAt = now, Zone = Warsaw,
            Machines = machines.Where(m => m.Enabled).Select(m => new ChartMachine(m.Id, m.Name)).ToArray(),
            Observation = observations,
        };
        var charts = Measure($"[{r}] DowntimeCharts.Build (5 wykresów) + ContentKey", () =>
        {
            var set = DowntimeCharts.Build(slices, options);
            GC.KeepAlive(set.ContentKey());
            return set;
        });
        var csv = Measure($"[{r}] CSV (kod Export_Click, do pamięci)", () => Csv(slices));
        var data = Measure($"[{r}] DowntimeExportData.Create (wątek UI)", () => DowntimeExportData.Create(slices, range.From, range.To, null,
            observations, machines, Warsaw, now, "WER-PC", DowntimeExportData.ScopeTextFor(false, null)));
        // Rozgrzewka PDF (rejestracja czcionek i pamięć podręczna PDFsharp — koszt pierwszego eksportu w procesie).
        using (var warm = new MemoryStream()) Assert.True(DowntimePdfExporter.Write(data, warm).Success);
        var pdf = Measure($"[{r}] PDF (DowntimePdfExporter.Write, Task.Run)", () =>
        {
            using var stream = new MemoryStream();
            var outcome = DowntimePdfExporter.Write(data, stream);
            Assert.True(outcome.Success, outcome.Error);
            return stream.ToArray();
        });
        var xlsx = Measure($"[{r}] XLSX (DowntimeExcelExporter.Write, Task.Run)", () =>
        {
            using var stream = new MemoryStream();
            var outcome = DowntimeExcelExporter.Write(data, stream);
            Assert.True(outcome.Success, outcome.Error);
            return stream.ToArray();
        });
        Line($"[{r}] wiersze: {slices.Length}; PDF {pdf.Length} B; XLSX {xlsx.Length} B; CSV {csv.Length} B");
        CheckAndDump(r, slices, charts, data, pdf, xlsx, csv, episodes, observations);
    }

    private void RunMissingMachineIdVariant(string downtimeSource, string work, MachineConfig[] machines, RuleSet rules,
        DateTimeOffset now, IReadOnlyList<ObservationInterval> observations)
    {
        var baseline = new DowntimeJournal(downtimeSource).Snapshot();
        string? victim = baseline.GroupBy(e => e.MachineId).OrderBy(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (victim is null || baseline.Select(e => e.MachineId).Distinct().Count() < 2) { Line("wariant bez MachineId: pominięty (jedna maszyna)"); return; }
        string path = Path.Combine(work, "bez-id-downtime.jsonl");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            foreach (string line in File.ReadLines(downtimeSource))
            {
                if (string.IsNullOrWhiteSpace(line)) { writer.Write('\n'); continue; }
                var node = JsonNode.Parse(line)!.AsObject();
                if ((string?)node["MachineId"] == victim) node["MachineId"] = null;
                writer.Write(node.ToJsonString() + "\n");
            }
        var stripped = new DowntimeJournal(path).Snapshot();
        Line($"wariant bez MachineId: maszyna {victim}, epizodów bez ID: {stripped.Count(e => string.IsNullOrEmpty(e.MachineId))}");
        try
        {
            var a = DowntimeReport.Build(baseline, Warsaw, new DowntimeCategoryClassifier(rules, machines), now);
            var b = DowntimeReport.Build(stripped, Warsaw, new DowntimeCategoryClassifier(rules, machines), now);
            var ta = DowntimeReport.Totals(a);
            var tb = DowntimeReport.Totals(b);
            Dump("bez-id", "wiersze", $"{a.Count} -> {b.Count}");
            Dump("bez-id", "suma[min]", $"{F(ta.All.Minutes)} -> {F(tb.All.Minutes)}");
            Dump("bez-id", "postojow", $"{ta.All.Episodes} -> {tb.All.Episodes}");
            var data = DowntimeExportData.Create(b, null, null, null, observations, machines, Warsaw, now, "WER-PC",
                DowntimeExportData.ScopeTextFor(false, null));
            Dump("bez-id", "eksport.maszyny", string.Join("; ", data.Machines.Select(m => $"[{m.MachineId}] {m.Name} {F(m.Totals.All.Minutes)}")));
            Dump("bez-id", "wykres.maszyny", string.Join("; ", data.Charts.Machines.Categories.Zip(data.Charts.Machines.TotalTexts, (c, t) => c + "=" + t)));
            if (Math.Abs(ta.All.Minutes - tb.All.Minutes) > 1e-6 || ta.All.Episodes != tb.All.Episodes)
                _problems.Add($"wariant bez MachineId: suma {F(ta.All.Minutes)} / {ta.All.Episodes} → {F(tb.All.Minutes)} / {tb.All.Episodes}");
            Equal("bez-id: eksport = raport", tb.All.Minutes, data.Totals.All.Minutes);
            Equal("bez-id: wykres maszyn = raport", tb.All.Minutes, data.Charts.Machines.Totals.Sum());
            Equal("bez-id: maszyny eksportu = raport", tb.All.Minutes, data.Machines.Sum(m => m.Totals.All.Minutes));
            using var pdf = new MemoryStream();
            var outcome = DowntimePdfExporter.Write(data, pdf);
            if (!outcome.Success) _problems.Add("wariant bez MachineId: PDF " + outcome.Error);
            using var xlsx = new MemoryStream();
            outcome = DowntimeExcelExporter.Write(data, xlsx);
            if (!outcome.Success) _problems.Add("wariant bez MachineId: XLSX " + outcome.Error);
        }
        catch (Exception ex)
        {
            _problems.Add("wariant bez MachineId: wyjątek " + ex.GetType().Name + ": " + ex.Message);
            Dump("bez-id", "wyjatek", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void RunNameAndHistoryVariants(IReadOnlyList<DowntimeEpisode> episodes, MachineConfig[] machines, RuleSet rules,
        DateTimeOffset now, IReadOnlyList<ObservationInterval> observations)
    {
        var renamed = machines.Select(m => new MachineConfig
        {
            Id = m.Id, Name = m.Name + " [konfiguracja]", Host = m.Host, Profile = m.Profile, Enabled = m.Enabled,
        }).ToArray();
        var slices = DowntimeReport.Build(episodes, Warsaw, new DowntimeCategoryClassifier(rules, renamed), now).ToArray();
        var panelCharts = DowntimeCharts.Build(slices, new DowntimeChartOptions
        {
            GeneratedAt = now, Zone = Warsaw, Observation = observations,
            Machines = renamed.Where(m => m.Enabled).Select(m => new ChartMachine(m.Id, m.Name)).ToArray(),
        });
        var data = DowntimeExportData.Create(slices, null, null, null, observations, renamed, Warsaw, now, "WER-PC",
            DowntimeExportData.ScopeTextFor(false, null));
        Dump("B-03", "Panel wykres maszyn", string.Join("; ", panelCharts.Machines.Categories));
        Dump("B-03", "eksport maszyny", string.Join("; ", data.Machines.Select(m => m.Name)));
        Dump("B-03", "eksport wykres maszyn", string.Join("; ", data.Charts.Machines.Categories));
        Dump("B-03", "eksport wiersze (szczegóły)", string.Join("; ", data.Episodes.Select(e => e.Machine).Distinct()));

        if (observations.Count == 0) return;
        var shift = TimeSpan.FromDays(-25);
        var earlier = observations.Select(o => o with { Start = o.Start + shift, End = o.End + shift }).ToArray();
        var historyCharts = DowntimeCharts.Build(slices, new DowntimeChartOptions
        {
            GeneratedAt = now, Zone = Warsaw, Observation = earlier,
            Machines = machines.Where(m => m.Enabled).Select(m => new ChartMachine(m.Id, m.Name)).ToArray(),
        });
        var historyData = DowntimeExportData.Create(slices, null, null, null, earlier, machines, Warsaw, now, "WER-PC",
            DowntimeExportData.ScopeTextFor(false, null));
        Dump("B-02", "obserwacja od", ObservationReport.FirstEntryAt(earlier)?.ToString("O") ?? "-");
        Dump("B-02", "Panel zakres", $"{historyCharts.Header.RangeText}; zmian {historyCharts.Header.Shifts}");
        Dump("B-02", "eksport Od..Do", $"{historyData.From:yyyy-MM-dd}..{historyData.To:yyyy-MM-dd}; zmian {historyData.Charts.Header.Shifts}");
        Dump("B-02", "Panel brak danych", historyCharts.NoData is null ? "-" : string.Join(" | ", historyCharts.NoData.Lines));
        Dump("B-02", "eksport brak danych", historyData.Charts.NoData is null ? "-" : string.Join(" | ", historyData.Charts.NoData.Lines));
    }

    private void CheckAndDump(string r, DowntimeSlice[] slices, DowntimeChartSet charts, DowntimeExportData data, byte[] pdfBytes,
        byte[] xlsxBytes, byte[] csvBytes, IReadOnlyList<DowntimeEpisode> episodes, IReadOnlyList<ObservationInterval> observations)
    {
        var totals = DowntimeReport.Totals(slices);
        var kinds = DowntimeReport.ByKind(slices);
        double table = slices.Sum(s => s.Minutes);

        Dump(r, "zakres eksportu", $"{data.From:yyyy-MM-dd}..{data.To:yyyy-MM-dd}");
        Dump(r, "wiersze", slices.Length.ToString(Inv));
        Dump(r, "suma[min] z dlugimi / bez", $"{F(totals.All.Minutes)} / {F(totals.WithoutLong.Minutes)}");
        Dump(r, "postojow / dlugich", $"{totals.All.Episodes} / {totals.All.Episodes - totals.WithoutLong.Episodes}");
        Dump(r, "ByKind bledy", Totals(kinds.Errors));
        Dump(r, "ByKind bez pracy", Totals(kinds.Idle));
        Dump(r, "ByKind na 0%", Totals(kinds.IdleAtZero));

        // Tabela = ByKind = maszyny (eksport) = wykresy = eksport.
        Equal($"[{r}] tabela = Totals", table, totals.All.Minutes);
        Equal($"[{r}] ByKind = Totals", totals.All.Minutes,
            kinds.Errors.All.Minutes + kinds.Idle.All.Minutes + kinds.IdleAtZero.All.Minutes);
        Equal($"[{r}] ByKind bez długich = Totals", totals.WithoutLong.Minutes,
            kinds.Errors.WithoutLong.Minutes + kinds.Idle.WithoutLong.Minutes + kinds.IdleAtZero.WithoutLong.Minutes);
        Count($"[{r}] ByKind postojów = Totals", totals.All.Episodes, kinds.Errors.All.Episodes + kinds.Idle.All.Episodes + kinds.IdleAtZero.All.Episodes);
        Equal($"[{r}] maszyny eksportu = Totals", totals.All.Minutes, data.Machines.Sum(m => m.Totals.All.Minutes));
        Count($"[{r}] postoje maszyn eksportu = Totals", totals.All.Episodes, data.Machines.Sum(m => m.Totals.All.Episodes));
        Equal($"[{r}] maszyny eksportu ByKind błędy", kinds.Errors.All.Minutes, data.Machines.Sum(m => m.Kinds.Errors.All.Minutes));
        Equal($"[{r}] wykres maszyn = Totals", totals.All.Minutes, charts.Machines.Totals.Sum());
        Equal($"[{r}] wykres rodzajów = Totals", totals.All.Minutes, charts.Kinds.Total);
        Equal($"[{r}] wykres błędów = ByKind błędy", kinds.Errors.All.Minutes, charts.Errors.Total);
        Equal($"[{r}] słupki błędów = ByKind błędy", kinds.Errors.All.Minutes, charts.ErrorMachines.Totals.Sum());
        if (charts.Days.Note is null)
        {
            Equal($"[{r}] linia: wszystkie = Totals", totals.All.Minutes, SeriesSum(charts.Days, DowntimeCharts.AllName));
            Equal($"[{r}] linia: bez długich = Totals", totals.WithoutLong.Minutes, SeriesSum(charts.Days, DowntimeCharts.WithoutLongName));
            Equal($"[{r}] linia: błędy = ByKind", kinds.Errors.All.Minutes, SeriesSum(charts.Days, DowntimeCharts.ErrorsName));
        }
        Equal($"[{r}] eksport Totals", totals.All.Minutes, data.Totals.All.Minutes);
        Equal($"[{r}] eksport wiersze", totals.All.Minutes, data.Episodes.Sum(e => e.Minutes));
        Equal($"[{r}] wykres eksportu = Totals", totals.All.Minutes, data.Charts.Machines.Totals.Sum());
        Equal($"[{r}] eksport dni = Totals", totals.All.Minutes, data.Days.Sum(d => d.Totals.All.Minutes));
        Equal($"[{r}] eksport nazwy = Totals", totals.All.Minutes, data.Errors.Sum(e => e.Totals.All.Minutes));

        // Weekend (sobota 06:00 – poniedziałek 06:00) poza raportem; długie postoje spójne w kawałkach epizodu.
        foreach (var s in slices)
        {
            if (s.Day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) _problems.Add($"[{r}] wiersz w weekend: {s.Day} {s.Start:O}");
            if (Math.Abs((ReportCalendar.ReportedDuration(s.Start, s.End, Warsaw) - (s.End - s.Start)).TotalSeconds) > 1e-6)
                _problems.Add($"[{r}] wiersz przecina weekend: {s.Start:O}–{s.End:O}");
        }
        foreach (var group in slices.GroupBy(s => (s.MachineId, s.EpisodeId)))
            if (group.Select(s => s.IsLong).Distinct().Count() > 1) _problems.Add($"[{r}] epizod {group.Key} ma różne IsLong w kawałkach");
        var merged = DowntimeReport.MergeOverlaps(episodes).ToLookup(e => (e.MachineId, e.EpisodeId));
        foreach (var group in slices.GroupBy(s => (s.MachineId, s.EpisodeId)))
            if (merged[group.Key].FirstOrDefault() is { } episode)
            {
                bool longByDuration = episode.End - episode.StartedAt > DowntimeReport.MaxEpisode;
                if (group.First().IsLong && !longByDuration) _problems.Add($"[{r}] długi bez > 1 h: {group.Key}");
                if (!group.First().IsLong && longByDuration && !DowntimeReport.IsIdleEpisode(episode))
                    _problems.Add($"[{r}] błąd > 1 h nieoznaczony: {group.Key}");
            }
        Dump(r, "dlugie epizody", string.Join("; ", slices.Where(s => s.IsLong).GroupBy(s => s.EpisodeId)
            .Select(g => $"{g.First().Machine} {g.First().Reason} {F(g.Sum(s => s.Minutes))} min zmierzone w raporcie")));

        Dump(r, "eksport.maszyny", string.Join("; ", data.Machines.Select(m =>
            $"[{m.MachineId}] {m.Name} {F(m.Totals.All.Minutes)}/{m.Totals.All.Episodes} bledy {F(m.Kinds.Errors.All.Minutes)} "
            + $"brak-pol {F(m.NoConnection.TotalMinutes)} ekran {F(m.ScreenHidden.TotalMinutes)} znane {m.ObservationKnown}")));
        Dump(r, "wykres.maszyny", string.Join("; ", charts.Machines.Categories.Zip(charts.Machines.TotalTexts, (c, t) => c + "=" + t)));
        Dump(r, "wykres.bledy-maszyn", string.Join("; ", charts.ErrorMachines.Categories.Zip(charts.ErrorMachines.TotalTexts, (c, t) => c + "=" + t)));
        Dump(r, "wykres.rodzaje", string.Join("; ", charts.Kinds.Slices.Select(s => $"{s.Label}={s.ValueText}/{s.ShareText}")));
        Dump(r, "wykres.nazwy", string.Join("; ", charts.Errors.Slices.Select(s => $"{s.Label}={s.ValueText}/{s.ShareText}")));
        Dump(r, "wykres.naglowek", charts.Header.Text);
        Dump(r, "wykres.linia", $"{charts.Days.Days.Count} dni, nota: {charts.Days.Note ?? "-"}");
        Dump(r, "wykres.brak-danych", charts.NoData is null ? "-" : string.Join(" | ", charts.NoData.Lines));
        Dump(r, "eksport.brak-danych", $"{F(data.NoConnection.TotalMinutes)} / {F(data.ScreenHidden.TotalMinutes)}; dziennik {data.HasObservationJournal}; znany od {data.ObservationKnownFrom?.ToString("O") ?? "-"}");
        Dump(r, "eksport.naglowek-wykresow", data.Charts.Header.Text);
        if (r == "cala-historia")
        {
            DateOnly? firstSlice = slices.Length > 0 ? slices.Min(s => s.Day) : null;
            var firstObs = ObservationReport.FirstEntryAt(observations);
            Dump(r, "B-02 pierwszy wiersz / pierwsza obserwacja / eksport.Od / wykres",
                $"{firstSlice:yyyy-MM-dd} / {firstObs?.ToString("O") ?? "-"} / {data.From:yyyy-MM-dd} / {charts.Header.RangeText}");
        }
        Dump(r, "ContentKey.sha", Sha(charts.ContentKey()));

        using (var pdf = PdfProbe.Open(pdfBytes))
        {
            var lines = Enumerable.Range(1, pdf.PageCount).SelectMany(p => pdf.Lines(p)).Select(l => l.Text).ToArray();
            var rows = lines.Where(l => Regex.IsMatch(l, @"^\d{2}\.\d{2}\.\d{4} \| ")).ToArray();
            var difference = lines.Where(l => l.StartsWith("Różnica zaokrągleń | ", StringComparison.Ordinal)).ToArray();
            var razem = lines.Where(l => l.StartsWith("Razem (z długimi postojami) | ", StringComparison.Ordinal)).ToArray();
            bool full = data.Episodes.Count <= 1000;
            Dump(r, "pdf", $"stron {pdf.PageCount}; wierszy tabeli {rows.Length}; Razem {string.Join("|", razem.Select(l => Cells(l)[1]))}; "
                + $"Różnica {string.Join("|", difference.Select(l => Cells(l)[1]))}");
            if (data.Episodes.Count > 0)
            {
                if (razem.Length != 1) _problems.Add($"[{r}] PDF: {razem.Length} wierszy Razem");
                else
                {
                    decimal printedTotal = Number(Cells(razem[0])[1]);
                    decimal expectedTotal = Number(data.Totals.All.Minutes.ToString("N1", Pl));
                    if (printedTotal != expectedTotal) _problems.Add($"[{r}] PDF Razem {printedTotal} ≠ {expectedTotal}");
                    if (full)
                    {
                        if (rows.Length != data.Episodes.Count) _problems.Add($"[{r}] PDF: {rows.Length} wierszy z {data.Episodes.Count}");
                        decimal visible = rows.Sum(l => Number(Cells(l)[5]));
                        decimal diff = difference.Length == 1 ? Number(Cells(difference[0])[1]) : 0;
                        if (difference.Length > 1) _problems.Add($"[{r}] PDF: {difference.Length} wierszy Różnicy");
                        if (visible + diff != printedTotal) _problems.Add($"[{r}] PDF: wiersze {visible} + różnica {diff} ≠ Razem {printedTotal}");
                        if ((visible != printedTotal) != (difference.Length == 1)) _problems.Add($"[{r}] PDF: Różnica przy zerowej różnicy albo brak przy niezerowej");
                    }
                    else if (difference.Length != 0) _problems.Add($"[{r}] PDF: Różnica przy obciętej tabeli");
                }
            }
            Write(r, "pdf.txt", pdf.AllText());
        }

        using (var stream = new MemoryStream(xlsxBytes))
        using (var workbook = new XLWorkbook(stream))
        {
            var episodesSheet = workbook.Worksheet("Epizody").RowsUsed().Skip(1).ToArray();
            double sum = episodesSheet.Sum(row => row.Cell(7).GetDouble());
            double razem = workbook.Worksheet("Sumy").RowsUsed().First(row => row.Cell(1).GetString() == "Razem").Cell(2).GetDouble();
            Equal($"[{r}] XLSX Epizody = raport", totals.All.Minutes, sum);
            Equal($"[{r}] XLSX Razem = raport", totals.All.Minutes, razem);
            int exact = episodesSheet.Count(row => row.Cell(7).GetDouble() == data.Episodes[row.RowNumber() - 2].Minutes);
            double maxDiff = episodesSheet.Length == 0 ? 0
                : episodesSheet.Max(row => Math.Abs(row.Cell(7).GetDouble() - data.Episodes[row.RowNumber() - 2].Minutes));
            Dump(r, "xlsx", $"wierszy {episodesSheet.Length}; bitowo równe {exact}/{episodesSheet.Length}; max |komórka − minuty| {maxDiff:E2}; suma {F(sum)}; Razem {F(razem)}; "
                + $"formaty {string.Join(",", episodesSheet.Select(row => row.Cell(7).Style.NumberFormat.Format).Distinct())}");
            var cells = new StringBuilder();
            foreach (var sheet in workbook.Worksheets)
                foreach (var cell in sheet.CellsUsed())
                {
                    if (sheet.Name == "Informacje" && cell.Address.ColumnNumber == 2 && sheet.Cell(cell.Address.RowNumber, 1).GetString() == "Wygenerowano") continue;
                    string value = cell.Value.IsNumber ? cell.Value.GetNumber().ToString("R", Inv) : cell.Value.ToString(Inv);
                    cells.Append(sheet.Name).Append('!').Append(cell.Address.ToStringRelative()).Append(" = ").Append(value)
                        .Append(" [").Append(cell.Style.NumberFormat.Format).Append(']').Append('\n');
                }
            Write(r, "xlsx.txt", cells.ToString());
        }

        string csv = new UTF8Encoding(true).GetString(csvBytes).TrimStart('﻿');
        var csvRows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        // Pola w cudzysłowach (tekst OCR może mieć średnik) — siódme pole to minuty.
        decimal csvSum = csvRows.Sum(l => decimal.Parse(Regex.Matches(l, "\"((?:[^\"]|\"\")*)\"")[6].Groups[1].Value, NumberStyles.Number, Pl));
        if (csvRows.Length != slices.Length) _problems.Add($"[{r}] CSV: {csvRows.Length} wierszy z {slices.Length}");
        if (Math.Abs((double)csvSum - totals.All.Minutes) > 0.005 * Math.Max(1, slices.Length))
            _problems.Add($"[{r}] CSV suma {csvSum} ≠ {F(totals.All.Minutes)}");
        Dump(r, "csv", $"wierszy {csvRows.Length}; suma F2 {csvSum.ToString(Inv)}");
        Write(r, "csv.txt", csv);
    }

    // ── CSV dokładnie jak DowntimeView.Export_Click ─────────────────────────────────────────────

    private static byte[] Csv(IReadOnlyList<DowntimeSlice> slices)
    {
        static string Cell(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "Dzień zmiany;Zmiana;Maszyna;Powód;Od;Do;Minuty;Stan" };
        foreach (var s in slices)
            lines.Add(string.Join(';', new[] { s.Day.ToString("yyyy-MM-dd"), s.Shift, s.Machine, s.Reason,
                s.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), s.End.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                s.Minutes.ToString("F2", CultureInfo.GetCultureInfo("pl-PL")),
                ReportPresentationNames.StateText(s) }.Select(Cell)));
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true))
            foreach (var line in lines) writer.WriteLine(line);
        return stream.ToArray();
    }

    // ── Pomiar ──────────────────────────────────────────────────────────────────────────────────

    private T Measure<T>(string name, Func<T> action)
    {
        var times = new double[Repetitions];
        var allocations = new long[Repetitions];
        T result = default!;
        for (int i = 0; i < Repetitions; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            result = action();
            watch.Stop();
            allocations[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
            times[i] = watch.Elapsed.TotalMilliseconds;
        }
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        Array.Sort(times);
        Array.Sort(allocations);
        _stages.Add(new Stage(name, times[0], times[Repetitions / 2], times[^1], allocations[Repetitions / 2], process.PeakWorkingSet64));
        return result;
    }

    private void Report(string prefix = "")
    {
        var text = new StringBuilder();
        text.Append("| etap | min [ms] | mediana [ms] | max [ms] | alokacje (mediana) [MB] | szczyt working set procesu [MB] |\n|---|---:|---:|---:|---:|---:|\n");
        foreach (var s in _stages)
            text.Append(Inv, $"| {s.Name} | {s.MinMs:0.0} | {s.MedianMs:0.0} | {s.MaxMs:0.0} | {s.MedianAllocated / 1048576.0:0.00} | {s.PeakWorkingSet / 1048576.0:0} |\n");
        output.WriteLine(text.ToString());
        output.WriteLine(_dump.ToString());
        Write("", prefix + "pomiary.md", text.ToString());
        Write("", prefix + "zrzut.txt", _dump.ToString());
    }

    // ── Pomocnicze ──────────────────────────────────────────────────────────────────────────────

    private static MachineConfig[] ReadConfig(string path)
    {
        if (!File.Exists(path)) return [];
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var machines = (root["machines"]?.AsArray() ?? []).Select(n => new MachineConfig
        {
            Id = (string)n!["id"]!, Name = (string?)n["name"] ?? (string)n["id"]!, Host = "127.0.0.1",
            Profile = (string?)n["profile"] ?? "", Enabled = (bool?)n["enabled"] ?? true,
        }).ToArray();
        return machines;
    }

    private RuleSet ReadRules(string path)
    {
        if (!File.Exists(path)) return new RuleSet();
        try { return ConfigLoader.LoadRules(path); }
        catch (ConfigException ex)
        {
            Line("rules.json nieczytelny dla tej wersji — klasyfikacja bez słownika: " + ex.Message);
            return new RuleSet();
        }
    }

    private void Equal(string what, double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 1e-6 * Math.Max(1, Math.Abs(expected))) _problems.Add($"{what}: {F(expected)} ≠ {F(actual)}");
    }

    private void Count(string what, int expected, int actual)
    {
        if (expected != actual) _problems.Add($"{what}: {expected} ≠ {actual}");
    }

    private static double SeriesSum(LineChartData line, string name) =>
        line.Series.Where(s => s.Name == name).SelectMany(s => s.Points).Sum(p => p.Value);

    private static string Totals(DowntimeTotals t) =>
        $"{F(t.All.Minutes)} / {F(t.WithoutLong.Minutes)} min, {t.All.Episodes} / {t.WithoutLong.Episodes} postojów";

    private static string F(double value) => value.ToString("0.000000", Inv);

    private static decimal Number(string text) =>
        decimal.Parse(text.Replace(' ', ' ').Replace(' ', ' ').Replace(" ", "").Trim(), NumberStyles.Number, Pl);

    private static string[] Cells(string line) => line.Split(" | ", StringSplitOptions.None);

    private static string Sha(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private void Line(string text)
    {
        output.WriteLine(text);
        _dump.Append("# ").Append(text).Append('\n');
    }

    private void Dump(string range, string key, string value) => _dump.Append('[').Append(range).Append("] ").Append(key).Append(" = ").Append(value).Append('\n');

    private static void Write(string range, string name, string text)
    {
        string? dir = Environment.GetEnvironmentVariable(RealDowntimeFactAttribute.OutputVariable);
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        string label = Environment.GetEnvironmentVariable(RealDowntimeFactAttribute.LabelVariable) is { Length: > 0 } l ? l : "wynik";
        File.WriteAllText(Path.Combine(dir, $"{label}-{(range.Length > 0 ? range + "-" : "")}{name}"), text, new UTF8Encoding(false));
    }
}
