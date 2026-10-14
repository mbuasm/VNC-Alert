using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Export.Tests;

internal static class R3TestData
{
    public static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
    public const string Local = "Dane lokalne tego komputera";

    public static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso);

    public static DowntimeEpisode Ep(string machineId, string reason, string start, double minutes, string? category = null,
        bool ongoing = false, bool interrupted = false, string? name = null, Guid? id = null)
    {
        var s = At(start);
        var end = s.AddMinutes(minutes);
        return new DowntimeEpisode(machineId, name ?? MachineName(machineId), reason, s, end, ongoing ? null : end, interrupted,
            id ?? Guid.NewGuid(), Category: category);
    }

    public static string MachineName(string id) => id switch
    {
        "a1" => "Aidite 1",
        "a2" => "Aidite 2",
        "a3" => "Aidite 3",
        "m520" => "520 (1)",
        _ => id.ToUpperInvariant(),
    };

    public static MachineConfig[] Machines() =>
    [
        new() { Id = "a1", Name = "Aidite 1", Host = "h1", Profile = "aidite" },
        new() { Id = "a2", Name = "Aidite 2", Host = "h2", Profile = "aidite" },
        new() { Id = "a3", Name = "Aidite 3", Host = "h3", Profile = "aidite" },
    ];

    /// <summary>Wiersze Panelu: Build + filtr okresu i zmiany jak w <c>DowntimeView.Refresh</c>.</summary>
    public static DowntimeSlice[] PanelSlices(IEnumerable<DowntimeEpisode> episodes, DateOnly? from, DateOnly? to,
        string? shift = null, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var f = from ?? DateOnly.MinValue;
        var t = to ?? DateOnly.MaxValue;
        return DowntimeReport.Build(episodes, zone ?? Utc, null, now ?? Now)
            .Where(s => s.Day >= f && s.Day <= t && (shift is null || s.Shift == shift)).ToArray();
    }

    public static DowntimeExportData Data(IEnumerable<DowntimeEpisode> episodes, DateOnly? from, DateOnly? to,
        string? shift = null, IReadOnlyList<ObservationInterval>? observations = null,
        TimeZoneInfo? zone = null, DateTimeOffset? now = null, string scope = Local, string computer = "TEST-PC")
    {
        var slices = PanelSlices(episodes, from, to, shift, zone, now);
        return DowntimeExportData.Create(slices, from, to, shift, observations ?? [], Machines(), zone ?? Utc, now ?? Now,
            computer, scope);
    }

    public static readonly DateOnly Mon = new(2026, 9, 28);
    public static readonly DateOnly Tue = new(2026, 10, 6);

    /// <summary>Typowy tydzień: tacka, długi postój, postój przez granicę zmian, trwający, przerwany.</summary>
    public static DowntimeEpisode[] Week() =>
    [
        Ep("a1", "Tacka nie pobrana", "2026-09-28T07:00:00Z", 12, "tacka"),
        Ep("a2", "Servo 6", "2026-09-29T15:00:00Z", 75, "restart"),
        Ep("a1", "Bazowanie (Home)", "2026-09-30T13:30:00Z", 60, "restart"),
        Ep("a3", "Tacka nie odłożona", "2026-10-01T23:00:00Z", 9, "tacka"),
        Ep("a2", "Narzędzie nie wróciło do magazynu", "2026-10-02T09:00:00Z", 5, "narzedzie", interrupted: true),
        Ep("a1", "Błąd: nieznany komunikat", "2026-10-05T10:00:00Z", 4),
        Ep("a3", "Servo 6", "2026-10-06T11:00:00Z", 30, "restart", ongoing: true),
    ];

    public static DowntimeExportData WeekData(IReadOnlyList<ObservationInterval>? observations = null, string? shift = null,
        string scope = Local) =>
        Data(Week(), Mon, Tue, shift, observations, scope: scope);

    public static DowntimeEpisode[] Random(int seed, int count)
    {
        var random = new Random(seed);
        string[] machines = ["a1", "a2", "a3", "m520"];
        string[] reasons = ["Tacka nie pobrana", "Servo 6", "Bazowanie (Home)", "Nr tacki = 0", "Ciśnienie powietrza", "Błąd: X",
            "Miejsce tacki zajęte", "Wrzeciono", "Drzwi otwarte", "Tacka nie odłożona"];
        string?[] categories = ["tacka", "restart", "inny", null, "narzedzie"];
        var start = At("2026-09-28T00:00:00Z");
        return Enumerable.Range(0, count).Select(_ =>
        {
            var s = start.AddMinutes(random.Next(0, 9 * 24 * 60));
            double minutes = random.Next(0, 10) switch { 0 => random.Next(61, 400), 1 => random.Next(1, 3), _ => random.Next(3, 59) };
            return Ep(machines[random.Next(machines.Length)], reasons[random.Next(reasons.Length)], s.ToString("o"), minutes,
                categories[random.Next(categories.Length)], ongoing: random.Next(20) == 0, interrupted: random.Next(15) == 0);
        }).ToArray();
    }

    /// <summary>Katalog roboczy testów zapisu — w katalogu wyjścia testów.</summary>
    public static string NewWorkDir()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "r3-zapis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
