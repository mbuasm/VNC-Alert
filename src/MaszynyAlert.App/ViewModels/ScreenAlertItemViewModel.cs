using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.ViewModels;

/// <summary>
/// Jeden wiersz powiadomienia "włącz okno podglądu stanu maszyny" (HMI maszyny pokazuje inny ekran niż
/// główny — <see cref="AlertKind.WrongScreen"/>) — w pop-upie <see cref="Views.ScreenPopupWindow"/> i w
/// GroupBoxie "Ekran HMI" okna statusu. Odpowiednik <see cref="IdleAlertItemViewModel"/>: jedyną akcją
/// operatora jest zamknięcie (<see cref="DismissCommand"/>), które chowa pozycję od razu; gdy ekran nadal
/// jest niewłaściwy, przypomnienie wraca po <c>TimingConfig.WrongScreenRemindAfterMs</c>.
/// </summary>
public sealed partial class ScreenAlertItemViewModel : ObservableObject
{
    public ScreenAlertItemViewModel(Alert alert, Func<AlertKey, Task> dismiss)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(dismiss);

        Key = alert.Key;
        Line1 = $"{alert.MachineName} — inny ekran od {alert.StartedAt.ToLocalTime():HH:mm}"
            + (alert.Status == AlertStatus.Escalated ? " (przypomnienie)" : string.Empty);
        Sound = alert.Sound;
        DismissCommand = new AsyncRelayCommand(() => dismiss(Key));
    }

    /// <summary>Klucz epizodu (<see cref="AlertKey.WrongScreen"/>).</summary>
    public AlertKey Key { get; }

    /// <summary>Wiersz do wyświetlenia: "NAZWA — inny ekran od HH:MM".</summary>
    public string Line1 { get; }

    /// <summary>Czy to powiadomienie ma odtworzyć dźwięk przy pojawieniu się (<c>TimingConfig.WrongScreenAlertSound</c>).</summary>
    public bool Sound { get; }

    /// <summary>Komenda przycisku "✕ Zamknij" — potwierdzenie (ACK) epizodu, patrz <see cref="Services.AppOrchestrator.DismissWrongScreenAlertAsync"/>.</summary>
    public IAsyncRelayCommand DismissCommand { get; }
}
