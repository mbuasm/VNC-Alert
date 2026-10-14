using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

/// <summary>Zrzut wyników raportu do porównania dwóch wersji programu — tylko ręcznie (<c>VNCALERT_TESTRAPORT_DUMP_OUT</c> = katalog).</summary>
public sealed class TestRaportDumpFactAttribute : FactAttribute
{
    public const string Variable = "VNCALERT_TESTRAPORT_DUMP_OUT";

    public TestRaportDumpFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Zrzut wyników raportu do porównania wersji tylko ręcznie: {Variable}=<katalog>.";
    }
}

internal sealed record TestRaportCase(int Index, IReadOnlyList<DowntimeEpisode> Episodes, TimeZoneInfo Zone,
    bool UseClassifier, DateTimeOffset? Now, DateTimeOffset Origin, bool FarFuture)
{
    public DowntimeCategoryClassifier? Classifier() =>
        UseClassifier ? new DowntimeCategoryClassifier(TestRaportScenarios.Rules, TestRaportScenarios.ClassifierMachines) : null;

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"#{Index} strefa={Zone.Id} teraz={(Now is { } n ? n.ToString("O", CultureInfo.InvariantCulture) : "null")} klasyfikator={UseClassifier} epizodów={Episodes.Count}");
}

internal static class TestRaportScenarios
{
    public const string IdleText = MaszynyAlert.Core.Alerts.AlertStateMachine.MachineIdleShortText;
    public const string IdleAtZeroText = MaszynyAlert.Core.Alerts.AlertStateMachine.MachineIdleAtZeroShortText;

    public static RuleSet Rules { get; } = ConfigLoader.LoadRules(Path.Combine(TestPaths.RepositoryRoot, "config", "rules.example.json"));

    public static readonly MachineConfig[] ClassifierMachines =
    [
        new() { Id = "m1", Name = "Maszyna 1", Host = "127.0.0.1", Profile = "opencnc" },
        new() { Id = "m2", Name = "Maszyna 2", Host = "127.0.0.1", Profile = "aidite5x" },
        new() { Id = "m3", Name = "580", Host = "127.0.0.1", Profile = "amm580" },
        new() { Id = "Maszyna 1", Name = "Maszyna 1 (id równe nazwie)", Host = "127.0.0.1", Profile = "amm580" },
    ];

    private static readonly (string? Id, string? Name)[] Machines =
    [
        ("m1", "Maszyna 1"), ("m1", "Maszyna 1"), ("m2", "Maszyna 2"), ("m3", "580"), ("m1", "Maszyna 1 (stara nazwa)"),
        (null, "Maszyna 1"), ("", "Maszyna 1"), (null, null), (null, ""), ("Maszyna 1", "Maszyna 1"), ("m3", null),
    ];

    private static readonly (string? Reason, string? Category)[] Kinds =
    [
        (IdleText, DowntimeCategories.Idle), (IdleText, DowntimeCategories.Idle), (IdleText, DowntimeCategories.Idle),
        (IdleText, null), (IdleAtZeroText, DowntimeCategories.IdleAtZero), (IdleAtZeroText, null),
        ("Servo 6", null), ("Servo 6", "restart"), ("Bazowanie (Home)", null),
        ("Otwarte drzwi bezpieczeństwa — zamknij i wciśnij Start", null), ("Zużyte narzędzie (Tool life) — bez alarmu", null),
        ("Zużyte narzędzie — wymień narzędzie", null), ("Błąd nr 705", null), ("Błąd: X", "  "), ("Drzwi", Rule.SuppressedCategory),
        ("Plan", Rule.IgnoredCategory), (IdleText, "restart"), ("Servo 6", DowntimeCategories.Idle), ("Servo 6", DowntimeCategories.IdleAtZero),
        (MaszynyAlert.Core.Machines.MachineMonitor.ProgramAbortShortText, null), (null, null), ("", "inny"), ("  " + IdleText + "  ", null),
    ];

    public static TimeZoneInfo[] Zones { get; } =
    [
        TestZones.Warsaw, TestZones.Warsaw, TestZones.Warsaw, TimeZoneInfo.Utc,
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"),
        TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe"),
        TestZones.WednesdayDst(5), TestZones.WednesdayDst(13), TestZones.WednesdayDst(21),
        TestZones.SaturdayDst(5), TestZones.SaturdayDst(6), TestZones.MondayDst(5), TestZones.MondayDst(6),
        // Przeskok o 30 min przez 06:00: wiosną 05:45 → 06:15 (06:00 nie istnieje), jesienią 06:15 → 05:45 (06:00 dwa razy).
        TestZones.Transitions("test-raport-30min", TimeSpan.FromHours(1), TimeSpan.FromMinutes(30),
            new DateOnly(2026, 3, 25), new TimeOnly(5, 45), new DateOnly(2026, 10, 28), new TimeOnly(6, 15)),
        TestZones.Transitions("test-raport-sobota-30min", TimeSpan.FromHours(1), TimeSpan.FromMinutes(30),
            new DateOnly(2026, 3, 28), new TimeOnly(5, 45), new DateOnly(2026, 10, 31), new TimeOnly(6, 15)),
    ];

    /// <summary>Czas ścienny „kotwic”: granice weekendu, zmian, przeskoki zegara (Warszawa, Nowy Jork, Lord Howe, strefy sztuczne).</summary>
    private static readonly DateTime[] Anchors =
    [
        new(2026, 3, 25, 5, 0, 0), new(2026, 3, 25, 6, 0, 0), new(2026, 3, 25, 14, 0, 0), new(2026, 3, 25, 22, 0, 0),
        new(2026, 3, 28, 6, 0, 0), new(2026, 3, 30, 6, 0, 0), new(2026, 3, 29, 2, 30, 0), new(2026, 3, 8, 2, 30, 0),
        new(2026, 4, 5, 1, 45, 0), new(2026, 10, 4, 2, 15, 0),
        new(2026, 10, 23, 22, 0, 0), new(2026, 10, 24, 6, 0, 0), new(2026, 10, 25, 2, 30, 0), new(2026, 10, 26, 6, 0, 0),
        new(2026, 10, 28, 6, 0, 0), new(2026, 10, 28, 14, 0, 0), new(2026, 10, 31, 6, 0, 0), new(2026, 11, 1, 1, 30, 0),
        new(2026, 10, 12, 14, 0, 0), new(2026, 10, 12, 0, 0, 0), new(2026, 10, 9, 21, 59, 59),
    ];

    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(-1), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(-30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(-1), TimeSpan.FromHours(1) + TimeSpan.FromTicks(1), TimeSpan.FromHours(8),
        TimeSpan.FromHours(-8), TimeSpan.FromDays(1), TimeSpan.FromDays(-1), TimeSpan.FromDays(2), TimeSpan.FromDays(-2),
        TimeSpan.FromDays(3),
    ];

    private static readonly TimeSpan[] Offsets =
    [
        TimeSpan.Zero, TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(-5), new(5, 30, 0), TimeSpan.FromHours(14),
        TimeSpan.FromHours(-12), new(9, 45, 0),
    ];

    public static Guid RandomGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static DateTimeOffset LocalInstant(TimeZoneInfo zone, DateTime local) => new(local, zone.GetUtcOffset(local));

    public static TestRaportCase Next(Random random, int index)
    {
        var zone = Zones[random.Next(Zones.Length)];
        var anchor = Anchors[random.Next(Anchors.Length)];
        var local = random.Next(3) switch
        {
            0 => anchor,
            1 => anchor.AddMinutes(random.Next(-180, 181)),
            _ => anchor.AddTicks(random.Next(2) == 0 ? 1 : -1),
        };
        var origin = LocalInstant(zone, local);
        int count = random.Next(10) switch { 0 => random.Next(0, 3), 9 => random.Next(60, 140), _ => random.Next(3, 40) };
        var episodes = new List<DowntimeEpisode>(count + 4);
        for (int i = 0; i < count; i++)
        {
            var (id, name) = Machines[random.Next(Machines.Length)];
            var (reason, category) = Kinds[random.Next(Kinds.Length)];
            DateTimeOffset start, end;
            int shape = random.Next(20);
            if (shape < 3 && episodes.Count > 0)
            {
                // Identyczne granice z innym epizodem (inny rodzaj albo maszyna) — kolejność przy remisie sortowania.
                var other = episodes[random.Next(episodes.Count)];
                (start, end) = (other.StartedAt, other.End);
            }
            else
            {
                start = shape switch
                {
                    3 or 4 when episodes.Count > 0 => episodes[random.Next(episodes.Count)].StartedAt, // wspólny początek
                    5 or 6 when episodes.Count > 0 => episodes[random.Next(episodes.Count)].End, // styk
                    7 or 8 or 9 => origin + Steps[random.Next(Steps.Length)], // granica co do tiku
                    _ => origin + TimeSpan.FromMinutes(random.Next(-3 * 24 * 60, 3 * 24 * 60)) + TimeSpan.FromSeconds(random.Next(60)),
                };
                TimeSpan length = random.Next(17) switch
                {
                    0 => TimeSpan.Zero,
                    1 => -TimeSpan.FromMinutes(random.Next(1, 90)),
                    2 => TimeSpan.FromSeconds(random.Next(1, 120)),
                    3 => TimeSpan.FromHours(1),
                    4 => TimeSpan.FromHours(1) + TimeSpan.FromTicks(1),
                    5 => TimeSpan.FromHours(1) - TimeSpan.FromTicks(1),
                    6 or 7 or 8 => TimeSpan.FromMinutes(random.Next(1, 120)),
                    9 or 10 => TimeSpan.FromMinutes(random.Next(55, 600)),
                    11 => TimeSpan.FromHours(random.Next(10, 80)),
                    12 => TimeSpan.FromDays(random.Next(1, 20)),
                    13 when episodes.Count > 0 => episodes[random.Next(episodes.Count)].End - start, // koniec na końcu innego
                    14 when episodes.Count > 0 => episodes[random.Next(episodes.Count)].StartedAt - start, // koniec na początku innego
                    15 => Steps[random.Next(Steps.Length)],
                    _ => TimeSpan.FromMinutes(random.Next(1, 30)),
                };
                end = start + length;
            }
            start = start.ToOffset(Offsets[random.Next(Offsets.Length)]);
            end = end.ToOffset(Offsets[random.Next(Offsets.Length)]);
            bool open = random.Next(5) == 0;
            var lastObserved = open ? end : random.Next(4) switch
            {
                0 => start,
                1 => end - TimeSpan.FromSeconds(random.Next(0, 30)),
                _ => end,
            };
            Guid episodeId = random.Next(25) switch
            {
                0 => Guid.Empty,
                1 or 2 when episodes.Count > 0 => episodes[random.Next(episodes.Count)].EpisodeId,
                _ => RandomGuid(random),
            };
            episodes.Add(new DowntimeEpisode(id!, name!, reason!, start, lastObserved, open ? null : end,
                Interrupted: random.Next(7) == 0, EpisodeId: episodeId, Origin: random.Next(3) == 0 ? "PC-" + random.Next(3) : null,
                Category: category));
        }
        bool farFuture = random.Next(20) == 0;
        bool spanning = false;
        if (farFuture)
        {
            var far = new DateTimeOffset(9999, 12, 30, 0, 0, 0, TimeSpan.Zero);
            for (int k = random.Next(1, 4); k > 0; k--)
            {
                var (reason, category) = Kinds[random.Next(Kinds.Length)];
                var (id, name) = Machines[random.Next(Machines.Length)];
                var s = far.AddHours(random.Next(0, 24));
                var e = s.AddHours(random.Next(0, 20));
                bool open = random.Next(3) == 0;
                episodes.Add(new DowntimeEpisode(id!, name!, reason!, s, e, open ? null : e, EpisodeId: RandomGuid(random), Category: category));
            }
        }
        else if (random.Next(25) == 0)
        {
            spanning = true;
            var (id, name) = Machines[random.Next(Machines.Length)];
            var s = origin + TimeSpan.FromHours(random.Next(-30, 30));
            var e = new DateTimeOffset(9999, 12, 31, 20, 0, 0, TimeSpan.Zero);
            episodes.Add(new DowntimeEpisode(id!, name!, IdleText, s, e, random.Next(2) == 0 ? null : e, EpisodeId: RandomGuid(random),
                Category: DowntimeCategories.Idle));
        }
        DateTimeOffset? now = random.Next(12) switch
        {
            0 when !spanning => null,
            1 or 2 or 3 when episodes.Count > 0 && !farFuture =>
                episodes[random.Next(episodes.Count)] is var e && e.End.Year < 9000
                    ? e.End - DowntimeReport.FutureTolerance + TimeSpan.FromTicks(random.Next(-1, 2))
                    : origin,
            4 when farFuture => DateTimeOffset.MaxValue.AddHours(-random.Next(1, 72)),
            5 => origin.AddDays(-random.Next(1, 5)),
            _ => origin + TimeSpan.FromMinutes(random.Next(-2 * 24 * 60, 6 * 24 * 60)),
        };
        if (now is { } value && value.Year < 9000) now = value.ToOffset(Offsets[random.Next(Offsets.Length)]);
        bool useClassifier = random.Next(2) == 0;
        return new TestRaportCase(index, episodes, zone, useClassifier, now, origin, farFuture);
    }

    public static List<DowntimeEpisode> Hall(int seed, DateTimeOffset from, int days)
    {
        var random = new Random(seed);
        var result = new List<DowntimeEpisode>();
        string[] errors = ["Servo 6", "Bazowanie (Home)", "Tacka nie pobrana", "Błąd nr 12", "Błąd: X", "Narzędzie nie wróciło"];
        for (int m = 1; m <= 7; m++)
        {
            var cursor = from.AddMinutes(m * 7);
            var end = from.AddDays(days);
            while (cursor < end)
            {
                cursor = cursor.AddMinutes(random.Next(2, 90));
                var idleEnd = cursor.AddMinutes(1 + random.Next(0, 70));
                bool atZero = random.Next(3) == 0;
                result.Add(new DowntimeEpisode("aidite" + m, "Aidite " + m, atZero ? IdleAtZeroText : IdleText, cursor, idleEnd,
                    random.Next(30) == 0 ? null : idleEnd, EpisodeId: RandomGuid(random),
                    Category: random.Next(5) == 0 ? null : atZero ? DowntimeCategories.IdleAtZero : DowntimeCategories.Idle));
                int errorsHere = random.Next(4) == 0 ? random.Next(1, 4) : 0;
                for (int k = 0; k < errorsHere; k++)
                {
                    var errorStart = random.Next(4) switch
                    {
                        0 => cursor, // ten sam początek co postój
                        1 => idleEnd, // styk z końcem postoju
                        _ => cursor + TimeSpan.FromSeconds(random.Next(0, (int)Math.Max(1, (idleEnd - cursor).TotalSeconds))),
                    };
                    var errorEnd = random.Next(4) == 0 ? idleEnd : errorStart.AddMinutes(random.Next(0, 45));
                    result.Add(new DowntimeEpisode("aidite" + m, "Aidite " + m, errors[random.Next(errors.Length)], errorStart, errorEnd,
                        errorEnd, EpisodeId: RandomGuid(random), Category: random.Next(3) == 0 ? null : "inny"));
                }
                cursor = idleEnd;
            }
        }
        return result;
    }

    public static string Canonical(DowntimeSlice s) => string.Join('|',
        s.Day.ToString("O", CultureInfo.InvariantCulture), s.Shift, s.Machine ?? "<null>", s.Reason ?? "<null>",
        s.Start.ToString("O", CultureInfo.InvariantCulture), s.End.ToString("O", CultureInfo.InvariantCulture),
        s.Interrupted, s.Ongoing, s.IsLong, s.Category ?? "<null>",
        s.MachineId ?? "<null>", s.EpisodeId, s.Kind, s.IsIdle, s.Minutes.ToString("R", CultureInfo.InvariantCulture));

    public static string Canonical(DowntimeEpisode e) => string.Join('|',
        e.MachineId ?? "<null>", e.MachineName ?? "<null>", e.Reason ?? "<null>",
        e.StartedAt.ToString("O", CultureInfo.InvariantCulture), e.LastObservedAt.ToString("O", CultureInfo.InvariantCulture),
        e.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "<null>", e.Interrupted, e.EpisodeId, e.Origin ?? "<null>",
        e.Category ?? "<null>");

    public static string Canonical(DowntimeTotals t) => string.Create(CultureInfo.InvariantCulture,
        $"{t.All.Minutes:R}/{t.All.Episodes};{t.WithoutLong.Minutes:R}/{t.WithoutLong.Episodes}");

    public static string Hash(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>Wynik raportu jako jedna linia: wiersze (skrót wszystkich pól), sumy, podział na rodzaj i scalanie.</summary>
    public static string Summary(IReadOnlyList<DowntimeEpisode> episodes, TimeZoneInfo zone,
        DowntimeCategoryClassifier? classifier, DateTimeOffset? now)
    {
        try
        {
            var rows = DowntimeReport.Build(episodes, zone, classifier, now);
            var kinds = DowntimeReport.ByKind(rows);
            var merged = DowntimeReport.MergeOverlaps(episodes);
            return $"wierszy={rows.Count} skrót={Hash(rows.Select(Canonical))} sumy={Canonical(DowntimeReport.Totals(rows))} "
                + $"błędy={Canonical(kinds.Errors)} bezPracy={Canonical(kinds.Idle)} na0={Canonical(kinds.IdleAtZero)} "
                + $"scalone={merged.Count}:{Hash(merged.Select(Canonical))}";
        }
        catch (Exception ex)
        {
            return $"WYJĄTEK {ex.GetType().Name}: {ex.Message}";
        }
    }
}

public sealed class TestRaportBuildDumpTests
{
    public const int Seed = 1_210_202_607;
    public const int Cases = 6000;

    [TestRaportDumpFact]
    public void Build_ZrzutDoPorownaniaWersji()
    {
        string output = Environment.GetEnvironmentVariable(TestRaportDumpFactAttribute.Variable)!;
        string label = Environment.GetEnvironmentVariable("VNCALERT_TESTRAPORT_LABEL") is { Length: > 0 } l ? l : "wynik";
        Directory.CreateDirectory(output);
        var text = new StringBuilder();
        var random = new Random(Seed);
        int rows = 0, exceptions = 0;
        for (int i = 0; i < Cases; i++)
        {
            var scenario = TestRaportScenarios.Next(random, i);
            string summary = TestRaportScenarios.Summary(scenario.Episodes, scenario.Zone, scenario.Classifier(), scenario.Now);
            if (summary.StartsWith("WYJĄTEK", StringComparison.Ordinal)) exceptions++;
            else rows += int.Parse(summary["wierszy=".Length..summary.IndexOf(' ')], CultureInfo.InvariantCulture);
            text.Append(scenario.Describe()).Append(" => ").Append(summary).Append('\n');
        }
        text.Append(CultureInfo.InvariantCulture, $"RAZEM przypadków={Cases} wierszy={rows} wyjątków={exceptions}\n");

        var hall = TestRaportScenarios.Hall(77, new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), 380);
        var hallClassifier = new DowntimeCategoryClassifier(TestRaportScenarios.Rules, TestRaportScenarios.ClassifierMachines);
        foreach (DateTimeOffset? now in new DateTimeOffset?[] { new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), null })
            text.Append(CultureInfo.InvariantCulture, $"HALA epizodów={hall.Count} teraz={now?.ToString("O", CultureInfo.InvariantCulture) ?? "null"} => ")
                .Append(TestRaportScenarios.Summary(hall, TestZones.Warsaw, hallClassifier, now)).Append('\n');

        if (Environment.GetEnvironmentVariable("VNCALERT_REAL_DOWNTIME_DIR") is { Length: > 0 } source)
        {
            string copy = Path.Combine(output, label + "-kopia");
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(source, "downtime.jsonl"), Path.Combine(copy, "downtime.jsonl"), overwrite: true);
            var episodes = new DowntimeJournal(Path.Combine(copy, "downtime.jsonl")).Snapshot();
            var rules = TestRaportScenarios.Rules;
            if (File.Exists(Path.Combine(source, "rules.json")))
            {
                try { rules = ConfigLoader.LoadRules(Path.Combine(source, "rules.json")); }
                catch (ConfigException) { }
            }
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "config.json")))!.AsObject();
            var machines = (node["machines"]?.AsArray() ?? []).Select(n => new MachineConfig
            {
                Id = (string)n!["id"]!, Name = (string?)n["name"] ?? (string)n["id"]!, Host = "127.0.0.1", Profile = (string?)n["profile"] ?? "",
            }).ToArray();
            var details = new StringBuilder();
            foreach (DateTimeOffset? now in new DateTimeOffset?[]
                     {
                         new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.FromHours(2)),
                         new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.FromHours(2)), null,
                     })
            {
                var classifier = new DowntimeCategoryClassifier(rules, machines);
                text.Append(CultureInfo.InvariantCulture, $"PRAWDZIWE epizodów={episodes.Count} teraz={now?.ToString("O", CultureInfo.InvariantCulture) ?? "null"} => ")
                    .Append(TestRaportScenarios.Summary(episodes, TimeZoneInfo.Local, classifier, now)).Append('\n');
                foreach (var row in DowntimeReport.Build(episodes, TimeZoneInfo.Local, new DowntimeCategoryClassifier(rules, machines), now))
                    details.Append(TestRaportScenarios.Canonical(row)).Append('\n');
            }
            File.WriteAllText(Path.Combine(output, $"{label}-prawdziwe-wiersze.txt"), details.ToString(), new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(output, $"{label}-build.txt"), text.ToString(), new UTF8Encoding(false));
        Assert.True(rows > 10_000, $"wierszy {rows}");
    }
}
