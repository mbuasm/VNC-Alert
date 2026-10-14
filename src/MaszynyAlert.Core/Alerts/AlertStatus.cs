namespace MaszynyAlert.Core.Alerts;

/// <summary>Status epizodu alertu w jego cyklu życia.</summary>
public enum AlertStatus
{
    /// <summary>Nowy lub wciąż trwający, niepotwierdzony alert — dzwoni na wszystkich komputerach.</summary>
    Active,

    /// <summary>Potwierdzony przyciskiem "Idę sprawdzić" — wyciszony, dopóki nie zostanie wyeskalowany.</summary>
    Acknowledged,

    /// <summary>Potwierdzony, ale błąd wciąż trwa dłużej niż próg eskalacji — ponownie dzwoni.</summary>
    Escalated,

    /// <summary>Epizod zakończony (czerwień zniknęła na maszynie albo maszyna wróciła online).</summary>
    Closed,
}
