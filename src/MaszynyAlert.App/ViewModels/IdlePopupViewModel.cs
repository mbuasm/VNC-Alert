using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.ViewModels;

/// <summary>
/// Stan pop-upu "MASZYNA STOI BEZ PRACY — dołóż prace" (<see cref="Views.IdlePopupWindow"/>).
/// Sama treść okna (lista + tekst zastępczy, gdy pusta) — logika POKAZYWANIA/CHOWANIA (nowy klucz → 5 s
/// auto-ukrycie, tryb przypięty bez auto-ukrycia) jest w kodzie za oknem (<c>IdlePopupWindow.xaml.cs</c>),
/// bo zależy od czasu i <c>DispatcherTimer</c> — ten widok-model jest z tego celowo odchudzony, tak jak
/// <see cref="AlertWindowViewModel"/> oddziela listę od dźwięku.
/// </summary>
public sealed partial class IdlePopupViewModel : ObservableObject
{
    private readonly Func<AlertKey, Task> _dismiss;

    public IdlePopupViewModel(Func<AlertKey, Task> dismiss)
    {
        _dismiss = dismiss ?? throw new ArgumentNullException(nameof(dismiss));
    }

    /// <summary>Alerty postoju aktualnie pokazywane w pop-upie, w kolejności zgłoszenia (najstarszy pierwszy).</summary>
    public ObservableCollection<IdleAlertItemViewModel> Items { get; } = new();

    /// <summary>Czy jest choć jedna pozycja — steruje widocznością listy (przeciwieństwo <see cref="IsEmpty"/>).</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// Czy lista jest pusta — w trybie przypiętym (<see cref="Views.IdlePopupWindow.ShowPinned"/>) pokazujemy
    /// wtedy tekst "Brak maszyn stojących bez pracy" zamiast listy. W trybie zwykłym okno jest wtedy po
    /// prostu chowane, więc ten tekst nigdy nie jest widoczny.
    /// </summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Zdanie pod listą o zasięgu „✕ Skasuj” — czy zgaśnie także na pozostałych komputerach grupy
    /// (<see cref="Core.Alerts.DismissScope.Note"/>).
    /// </summary>
    [ObservableProperty]
    private string _scopeNote = "";

    /// <summary>Czy <see cref="ScopeNote"/> ostrzega (skasowanie może nie dojść do pozostałych komputerów).</summary>
    [ObservableProperty]
    private bool _isScopeWarning;

    /// <summary>Przelicza listę pozycji na podstawie <see cref="Services.AppOrchestrator.IdleAlerts"/>. Wołane na wątku UI.</summary>
    /// <param name="scopeNote">Zdanie o zasięgu skasowania (patrz <see cref="ScopeNote"/>); puste = bez zdania.</param>
    /// <param name="isScopeWarning">Czy to zdanie jest ostrzeżeniem (inny kolor).</param>
    /// <param name="dismissHint">Podpowiedź przy przycisku „✕ Skasuj” (tooltip).</param>
    public void Refresh(IReadOnlyList<Alert> idleAlerts, string? scopeNote = null,
        bool isScopeWarning = false, string? dismissHint = null)
    {
        ArgumentNullException.ThrowIfNull(idleAlerts);

        Items.Clear();
        foreach (Alert alert in idleAlerts.OrderBy(a => a.StartedAt))
        {
            Items.Add(new IdleAlertItemViewModel(alert, _dismiss, dismissHint));
        }

        ScopeNote = scopeNote ?? "";
        IsScopeWarning = isScopeWarning;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
