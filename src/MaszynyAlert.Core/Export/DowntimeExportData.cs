using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Export;

/// <summary>Wiersz tabeli epizodów w PDF i arkusza „Epizody” w Excelu (jeden kawałek postoju na jednej zmianie).</summary>
/// <param name="Start">Początek kawałka — zegar ścienny strefy raportu (Excel nie zna stref).</param>
/// <param name="End">Koniec kawałka albo ostatnia czerwień — zegar ścienny strefy raportu.</param>
public sealed record ExportEpisodeRow(Guid EpisodeId, DateOnly Day, string Shift, string MachineId, string Machine,
    DateTime Start, DateTime End, double Minutes, string ErrorName, string ErrorGroup, string State, bool IsLong);

/// <summary>Suma jednej maszyny: dwa warianty czasu postoju i liczba postojów oraz czas „brak danych” (poza mianownikiem).</summary>
public sealed record ExportMachineSummary(string MachineId, string Name, DowntimeTotals Totals, TimeSpan NoConnection, TimeSpan ScreenHidden)
{
    public DowntimeKindTotals Kinds { get; init; } = DowntimeReport.ByKind([]);

    /// <summary>
    /// Czy dziennik czasu obserwacji ma JAKIKOLWIEK wpis tej maszyny. Bez niego „brak danych” tej maszyny jest nieznany
    /// (np. maszyna dodana albo włączona później) — eksport pokazuje „—”/„brak zapisu”, nie 0.
    /// </summary>
    public bool ObservationKnown { get; init; } = true;
}

/// <summary>Suma jednej nazwy błędu (po mapowaniu „Tacka” → „Ramka”) z jej grupą (rodzajem) błędu.</summary>
/// <param name="IsIdle">Czy to postój bez pracy (100 % / 0 %), a nie błąd maszyny — arkusz „Błędy” pomija takie wiersze.</param>
public sealed record ExportErrorSummary(string ErrorName, string ErrorGroup, DowntimeTotals Totals, bool IsIdle = false);

/// <summary>Suma jednego dnia zmiany.</summary>
public sealed record ExportDaySummary(DateOnly Day, DowntimeTotals Totals)
{
    /// <summary>Podział dnia na rodzaj postoju (błędy maszyn / bez pracy).</summary>
    public DowntimeKindTotals Kinds { get; init; } = DowntimeReport.ByKind([]);
}

public sealed class DowntimeExportData
{
    public const string Title = "Raport postojów maszyn";

    public const string CreatedBy = "Raport stworzony przez VNC Alert";

    public const string NoConnectionLabel = "Brak danych - brak połączenia z maszyną";

    public const string ScreenHiddenLabel = "Brak danych - ekran HMI niewidoczny";

    public const string AllShifts = "Wszystkie zmiany";

    /// <summary>
    /// Zakres danych raportu do PDF i Excela — te same słowa co nagłówki bloków zakresu w Panelu („Dane lokalne tego
    /// komputera”, „Dane grupy komputerów”), z nazwą grupy. Eksport bywa czytany bez Panelu, więc musi mówić, czyje to
    /// obserwacje.
    /// </summary>
    public static string ScopeTextFor(bool groupReport, string? groupName)
    {
        if (!groupReport) return "Dane lokalne tego komputera";
        return string.IsNullOrWhiteSpace(groupName) ? "Dane grupy komputerów" : $"Dane grupy komputerów „{groupName.Trim()}”";
    }

    /// <summary>Pierwszy dzień zmiany zakresu (wybrany w Panelu, a przy „Cała historia” — najstarszy z danych).</summary>
    public required DateOnly From { get; init; }

    /// <summary>Ostatni dzień zmiany zakresu (wybrany w Panelu, a przy „Cała historia” — najnowszy z danych albo dziś).</summary>
    public required DateOnly To { get; init; }

    /// <summary>Wybrana zmiana („06:00–14:00” …) albo <see cref="AllShifts"/>.</summary>
    public required string ShiftText { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
    public required string ComputerName { get; init; }

    /// <summary>Zakres danych: „Dane lokalne tego komputera” albo „Dane grupy komputerów „…””.</summary>
    public required string ScopeText { get; init; }

    public required IReadOnlyList<ExportEpisodeRow> Episodes { get; init; }
    public required DowntimeTotals Totals { get; init; }

    public required DowntimeKindTotals Kinds { get; init; }
    public required IReadOnlyList<ExportMachineSummary> Machines { get; init; }
    public required IReadOnlyList<ExportErrorSummary> Errors { get; init; }
    public required IReadOnlyList<ExportDaySummary> Days { get; init; }

    public required DowntimeChartSet Charts { get; init; }

    /// <summary>Łączny czas „brak połączenia z maszyną” w zakresie (poza weekendem i poza mianownikiem).</summary>
    public required TimeSpan NoConnection { get; init; }

    /// <summary>Łączny czas „ekran HMI niewidoczny” w zakresie.</summary>
    public required TimeSpan ScreenHidden { get; init; }

    /// <summary>Czy dziennik czasu obserwacji ma JAKIKOLWIEK wpis — bez niego „brak danych” jest nieznany, nie zerowy.</summary>
    public required bool HasObservationJournal { get; init; }

    /// <summary>
    /// Chwila, od której dziennik czasu obserwacji zna „brak danych”, gdy jest PÓŹNIEJSZA niż początek zakresu (dziennik
    /// powstał w trakcie zakresu); <see langword="null"/> — dziennik obejmuje cały zakres albo go nie ma.
    /// </summary>
    public DateTimeOffset? ObservationKnownFrom { get; init; }

    /// <summary>Liczba długich postojów (&gt; 1 h) — różnica obu wariantów liczby postojów.</summary>
    public int LongEpisodes => Totals.All.Episodes - Totals.WithoutLong.Episodes;

    /// <summary>Nazwa pliku: <c>raport-postojow-RRRR-MM-DD_RRRR-MM-DD.pdf</c> albo <c>.xlsx</c>.</summary>
    public string FileName(string extension) => FileNameFor(From, To, extension);

    /// <summary>Nazwa pliku eksportu dla zakresu dni zmian; rozszerzenie z kropką albo bez.</summary>
    public static string FileNameFor(DateOnly from, DateOnly to, string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return $"raport-postojow-{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.{extension.TrimStart('.')}";
    }

    public static DowntimeExportData Create(IReadOnlyList<DowntimeSlice> slices, DateOnly? from, DateOnly? to, string? shift,
        IReadOnlyList<ObservationInterval> observations, IEnumerable<MachineConfig> machines, TimeZoneInfo zone,
        DateTimeOffset now, string computerName, string scopeText, bool linePerMachine = false, bool includeIdle = true)
    {
        ArgumentNullException.ThrowIfNull(slices);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(zone);

        var today = ReportHistoryRange.ShiftDay(now, zone);
        DateOnly effectiveFrom = from ?? ReportHistoryRange.FirstDay(slices, observations, zone) ?? to ?? today;
        DateOnly effectiveTo = to ?? (slices.Count > 0 ? Max(slices.Max(s => s.Day), today) : today);
        if (effectiveTo < effectiveFrom) effectiveTo = effectiveFrom;

        var rows = slices
            .OrderBy(s => s.Start).ThenBy(s => s.Machine, StringComparer.CurrentCulture)
            .Select(s => new ExportEpisodeRow(s.EpisodeId, s.Day, s.Shift, s.MachineId, s.Machine,
                ReportCalendar.LocalWall(s.Start, zone), ReportCalendar.LocalWall(s.End, zone), s.Minutes,
                ReportPresentationNames.ErrorName(s.Reason), ReportPresentationNames.CategoryName(s.Category),
                ReportPresentationNames.StateText(s), s.IsLong))
            .ToArray();

        var configuredMachines = machines.Where(m => m is not null && m.Enabled && !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new ChartMachine(m.Id, m.Name)).ToArray();

        var charts = DowntimeCharts.Build(slices, new DowntimeChartOptions
        {
            From = effectiveFrom, To = effectiveTo, Shift = shift, LinePerMachine = linePerMachine, IncludeIdle = includeIdle,
            GeneratedAt = now, Zone = zone,
            Machines = configuredMachines,
            Observation = observations,
        });
        var noData = (charts.NoData?.Machines ?? []).ToDictionary(e => ReportMachineKey.ForId(e.MachineId));

        var firstEntry = ObservationReport.FirstEntryAt(observations);
        var rangeStart = ReportCalendar.NextInstant(effectiveFrom.ToDateTime(new TimeOnly(ReportCalendar.ShiftDayStartHour, 0)),
            zone, DateTimeOffset.MinValue);
        var names = new ReportMachineNames(slices, configuredMachines);

        var machineIds = slices.Select(ReportMachineKey.Of).Concat(noData.Keys)
            .Distinct();
        var machineSummaries = new List<ExportMachineSummary>();
        foreach (var id in machineIds)
        {
            var mine = slices.Where(s => ReportMachineKey.Of(s) == id).ToArray();
            var totals = DowntimeReport.Totals(mine);
            var entry = noData.GetValueOrDefault(id);
            machineSummaries.Add(new ExportMachineSummary(id.IsFallback ? string.Empty : id.Value,
                mine.Length > 0 ? names.For(mine[0]) : names.For(id.Value, entry?.Machine), totals,
                entry?.NoConnection ?? TimeSpan.Zero, entry?.ScreenHidden ?? TimeSpan.Zero)
            {
                ObservationKnown = !id.IsFallback && ObservationReport.FirstEntryAt(observations, id.Value) is not null,
                Kinds = DowntimeReport.ByKind(mine),
            });
        }

        return new DowntimeExportData
        {
            From = effectiveFrom, To = effectiveTo, ShiftText = shift ?? AllShifts,
            GeneratedAt = now, ComputerName = computerName, ScopeText = scopeText,
            Episodes = rows,
            Totals = DowntimeReport.Totals(slices),
            Kinds = DowntimeReport.ByKind(slices),
            Machines = machineSummaries.OrderByDescending(m => m.Totals.All.Minutes)
                .ThenBy(m => m.Name, StringComparer.CurrentCulture).ToArray(),
            Errors = slices.GroupBy(s => ReportPresentationNames.ErrorName(s.Reason))
                .Select(g => new ExportErrorSummary(g.Key,
                    // Ta sama nazwa bywa w dwóch grupach (stary epizod „nierozpoznany” obok nowego) — bierzemy najczęstszą znaną.
                    g.Select(s => ReportPresentationNames.CategoryName(s.Category))
                        .GroupBy(c => c).OrderByDescending(c => c.Key != "Nierozpoznany").ThenByDescending(c => c.Count())
                        .First().Key,
                    DowntimeReport.Totals(g),
                    // Nazwa jest postojem bez pracy, gdy wszystkie jej wiersze są takie (tekst alertu postoju jest stały).
                    IsIdle: g.All(s => s.IsIdle)))
                .OrderByDescending(e => e.Totals.All.Minutes).ThenBy(e => e.ErrorName, StringComparer.CurrentCulture).ToArray(),
            Days = slices.GroupBy(s => s.Day).OrderBy(g => g.Key)
                .Select(g => new ExportDaySummary(g.Key, DowntimeReport.Totals(g)) { Kinds = DowntimeReport.ByKind(g) }).ToArray(),
            NoConnection = noData.Values.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.NoConnection),
            ScreenHidden = noData.Values.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.ScreenHidden),
            Charts = charts,
            HasObservationJournal = firstEntry is not null,
            ObservationKnownFrom = firstEntry is { } known && known > rangeStart ? known : null,
        };
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
