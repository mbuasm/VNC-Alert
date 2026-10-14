namespace MaszynyAlert.Core.History;

/// <summary>
/// Tożsamość maszyny wyłącznie w obliczeniach raportu. Stare wpisy pliku mogą nie mieć ID: zachowujemy ich
/// dodatni czas, rozróżniając maszyny po zapisanej nazwie. Klucz zastępczy nie trafia do dziennika ani protokołu.
/// Flaga oddziela nazwy zastępcze od każdego legalnego ID, także takiego, które zawiera znaki sterujące.
/// </summary>
internal readonly record struct ReportMachineKey(bool IsFallback, string Value)
{
    public static ReportMachineKey Of(DowntimeSlice slice) => Of(slice.MachineId, slice.Machine);
    public static ReportMachineKey Of(DowntimeEpisode episode) => Of(episode.MachineId, episode.MachineName);
    public static ReportMachineKey ForId(string id) => new(false, id);

    private static ReportMachineKey Of(string? id, string? name) =>
        string.IsNullOrEmpty(id) ? new(true, name ?? string.Empty) : ForId(id);
}
