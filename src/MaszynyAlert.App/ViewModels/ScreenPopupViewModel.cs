using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.ViewModels;

/// <summary>
/// Stan pop-upu "WŁĄCZ OKNO PODGLĄDU STANU MASZYNY" (<see cref="Views.ScreenPopupWindow"/>): lista maszyn,
/// których HMI pokazuje inny ekran niż główny. Logika POKAZYWANIA/CHOWANIA (nowy klucz → pokaż, pusta lista →
/// schowaj, tryb przypięty z menu zasobnika) jest w kodzie za oknem — tak jak w <see cref="IdlePopupViewModel"/>.
/// </summary>
public sealed partial class ScreenPopupViewModel : ObservableObject
{
    private readonly Func<AlertKey, Task> _dismiss;

    public ScreenPopupViewModel(Func<AlertKey, Task> dismiss)
    {
        _dismiss = dismiss ?? throw new ArgumentNullException(nameof(dismiss));
    }

    /// <summary>Powiadomienia aktualnie pokazywane w pop-upie, w kolejności zgłoszenia (najstarsze pierwsze).</summary>
    public ObservableCollection<ScreenAlertItemViewModel> Items { get; } = new();

    /// <summary>Czy jest choć jedna pozycja — steruje widocznością listy (przeciwieństwo <see cref="IsEmpty"/>).</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Czy lista jest pusta — w trybie przypiętym pokazujemy wtedy tekst "Wszystkie maszyny pokazują ekran główny".</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Przelicza listę pozycji na podstawie <see cref="Services.AppOrchestrator.WrongScreenAlerts"/>. Wołane na wątku UI.</summary>
    public void Refresh(IReadOnlyList<Alert> wrongScreenAlerts)
    {
        ArgumentNullException.ThrowIfNull(wrongScreenAlerts);

        Items.Clear();
        foreach (Alert alert in wrongScreenAlerts.OrderBy(a => a.StartedAt))
        {
            Items.Add(new ScreenAlertItemViewModel(alert, _dismiss));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
