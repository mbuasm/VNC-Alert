namespace MaszynyAlert.Core.History;

/// <summary>
/// Nazwy maszyn w agregacjach Panelu i eksportu: bieżąca konfiguracja wykresów ma pierwszeństwo,
/// potem najnowsza niepusta nazwa w wybranych wierszach raportu, na końcu identyfikator.
/// Nie zmienia nazw w szczegółach epizodów ani kluczy, po których raport sumuje czas.
/// </summary>
internal sealed class ReportMachineNames
{
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    public ReportMachineNames(IReadOnlyList<DowntimeSlice> rows, IEnumerable<ChartMachine> configured)
    {
        foreach (var machine in configured)
            if (machine is not null) Add(machine.Id, machine.Name);
        foreach (var row in rows.OrderByDescending(s => s.Start))
            Add(row.MachineId, row.Machine);
    }

    private void Add(string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            _names.TryAdd(id, name);
    }

    public string For(DowntimeSlice row) => For(row.MachineId, row.Machine);

    public string For(string? id, string? fallback = null) =>
        id is not null && _names.TryGetValue(id, out var name) ? name
            : !string.IsNullOrWhiteSpace(fallback) ? fallback : id ?? string.Empty;
}