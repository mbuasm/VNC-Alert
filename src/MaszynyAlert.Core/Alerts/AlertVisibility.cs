namespace MaszynyAlert.Core.Alerts;

/// <summary>
/// Reguła widoczności alertu w OKNIE ALERTU (duże czerwone okno na wszystkich komputerach) — oddzielona od
/// <c>AlertWindowViewModel</c>, żeby dało się jej użyć/przetestować bez WPF.
/// </summary>
public static class AlertVisibility
{
    public static bool IsVisibleInAlertWindow(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (alert.Status == AlertStatus.Closed)
        {
            return false;
        }

        if (alert.Status == AlertStatus.Acknowledged)
        {
            return false;
        }

        if (alert.Kind is AlertKind.MachineIdle or AlertKind.WrongScreen)
        {
            return false;
        }

        return true;
    }
}
