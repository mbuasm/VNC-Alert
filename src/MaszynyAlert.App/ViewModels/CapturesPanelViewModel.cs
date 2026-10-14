using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class CapturesPanelViewModel : ObservableObject
{
    /// <summary>Okno retencji wymuszane przyciskiem „Usuń zrzuty starsze niż dzień”.</summary>
    private static readonly TimeSpan ManualRetention = TimeSpan.FromDays(1);

    private readonly Func<CaptureFolderStats> _readStats;
    private readonly Func<int> _readLimitMegabytes;
    private readonly Func<TimeSpan, Task> _prune;

    public CapturesPanelViewModel(AppOrchestrator orchestrator)
        : this(() => orchestrator.Supervisor.DescribeCaptures(),
            () => orchestrator.Supervisor.CapturesMaxMegabytes,
            retention => orchestrator.Supervisor.PruneCapturesAsync(retention))
    {
    }

    /// <summary>Wariant do testów: same odczyty i jedna operacja czyszczenia, bez okna i bez orkiestratora.</summary>
    public CapturesPanelViewModel(
        Func<CaptureFolderStats> readStats,
        Func<int> readLimitMegabytes,
        Func<TimeSpan, Task> prune)
    {
        _readStats = readStats ?? throw new ArgumentNullException(nameof(readStats));
        _readLimitMegabytes = readLimitMegabytes ?? throw new ArgumentNullException(nameof(readLimitMegabytes));
        _prune = prune ?? throw new ArgumentNullException(nameof(prune));
        Refresh();
    }

    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>Czy katalog zrzutów dobił do limitu miejsca (wtedy wiersz jest wyróżniony w oknie).</summary>
    [ObservableProperty]
    private bool _isAtLimit;

    /// <summary>Wynik ostatniego ręcznego czyszczenia albo pusty tekst, gdy jeszcze go nie było.</summary>
    [ObservableProperty]
    private string _actionText = "";

    /// <summary>Odczytuje katalog na nowo — wołane przy otwarciu zakładki i po ręcznym czyszczeniu.</summary>
    public void Refresh()
    {
        CaptureFolderStats stats = _readStats();
        int limit = _readLimitMegabytes();
        SummaryText = Describe(stats, limit);
        IsAtLimit = stats.Megabytes >= limit;
    }

    /// <summary>Składa wiersz licznika. Wydzielone, żeby test mógł sprawdzić samą treść bez okna.</summary>
    public static string Describe(CaptureFolderStats stats, int limitMegabytes)
    {
        if (stats.Files == 0)
        {
            return $"Zrzuty: brak plików (limit {limitMegabytes} MB).";
        }

        string oldest = stats.Oldest is { } o
            ? $", najstarszy {o.ToLocalTime():dd.MM}"
            : "";
        return $"Zrzuty: {stats.Files} plików, {stats.Megabytes} MB (limit {limitMegabytes} MB){oldest}.";
    }

    /// <summary>
    /// „Usuń zrzuty starsze niż dzień”: wymusza krótsze okno retencji i od razu odświeża licznik. Zrzuty
    /// wskazywane przez OTWARTE alerty zostają — inaczej zniknąłby przycisk „Pokaż zrzut” przy trwającym
    /// alarmie (patrz <see cref="MonitorSupervisor.PruneCapturesAsync"/>).
    /// </summary>
    [RelayCommand]
    private async Task DeleteOlderThanDayAsync()
    {
        CaptureFolderStats before = _readStats();
        try
        {
            await _prune(ManualRetention).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionText = "Nie udało się usunąć zrzutów — sprawdź uprawnienia do katalogu.";
            return;
        }

        CaptureFolderStats after = _readStats();
        int removed = Math.Max(0, before.Files - after.Files);
        ActionText = removed == 0
            ? "Nie było zrzutów starszych niż dzień."
            : $"Usunięto {removed} zrzutów starszych niż dzień.";
        Refresh();
    }
}
