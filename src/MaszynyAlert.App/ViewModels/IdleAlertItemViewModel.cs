using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.ViewModels;

/// <summary>
/// Jeden wiersz alertu "dołóż prace" (maszyna stoi bez pracy) — w pop-upie <see cref="Views.IdlePopupWindow"/> i w
/// GroupBoxie "Maszyny bez pracy – dołóż prace" okna statusu. Odpowiednik
/// <see cref="AlertItemViewModel"/>, ale bez pola "Idę sprawdzić"/"sprawdza ..." — dla tego rodzaju alertu
/// jedyną akcją operatora jest skasowanie (<see cref="DismissCommand"/>), które ODRAZU chowa pozycję na
/// wszystkich komputerach (bez 60 s okna "sprawdza" jak przy błędach — patrz
/// <c>AlertStateMachine.OnProgressState</c> i <see cref="Services.AppOrchestrator.IdleAlerts"/>).
/// </summary>
public sealed partial class IdleAlertItemViewModel : ObservableObject
{
    /// <param name="dismissHint">
    /// Podpowiedź przy przycisku o zasięgu skasowania (<see cref="Core.Alerts.DismissScope.ButtonHint"/>) —
    /// czy zgaśnie także na pozostałych komputerach grupy, czy tylko tutaj. Puste, gdy nie ma czego dodać.
    /// </param>
    public IdleAlertItemViewModel(Alert alert, Func<AlertKey, Task> dismiss, string? dismissHint = null)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(dismiss);

        Key = alert.Key;
        Line1 = alert.DisplayLine();
        DismissHint = dismissHint;
        DismissCommand = new AsyncRelayCommand(() => dismiss(Key));
    }

    /// <summary>Klucz epizodu — identyfikuje ten alert niezależnie od komputera, który go zgłosił.</summary>
    public AlertKey Key { get; }

    /// <summary>Wiersz do wyświetlenia: "NAZWA — opis — od HH:MM" (<see cref="Alert.DisplayLine"/>).</summary>
    public string Line1 { get; }

    /// <summary>Podpowiedź (tooltip) przycisku „✕ Skasuj” o zasięgu — <see langword="null"/>, gdy okno jej nie podaje.</summary>
    public string? DismissHint { get; }

    /// <summary>
    /// Komenda przycisku "✕ Skasuj" — to samo co "Idę sprawdzić" dla błędów (ACK), rozgłaszane po sieci;
    /// znika na WSZYSTKICH komputerach (patrz <see cref="Services.AppOrchestrator.DismissIdleAlertAsync"/>).
    /// </summary>
    public IAsyncRelayCommand DismissCommand { get; }
}
