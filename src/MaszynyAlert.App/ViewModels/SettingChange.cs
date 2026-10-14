namespace MaszynyAlert.App.ViewModels;

public sealed record SettingChange(string Name, string? Before, string? After, bool IsWarning = false)
{
    /// <summary>Zmiana sekretu — bez wartości.</summary>
    public static SettingChange Secret(string name) => new(name, null, null);

    /// <summary>Ostrzeżenie o skutku zapisu — nazwa i jedno zdanie, bez wartości „przed” i „po”.</summary>
    public static SettingChange Warning(string name, string text) => new(name, null, text, IsWarning: true);

    /// <summary>Czy to sekret (lista nie pokazuje wartości).</summary>
    public bool IsSecret => !IsWarning && Before is null && After is null;

    /// <summary>Wiersz „Było: X”; przy sekrecie i ostrzeżeniu pusty.</summary>
    public string BeforeText => IsSecret || IsWarning ? "" : "Było: " + Before;

    /// <summary>Wiersz „Po zapisaniu będzie: Y”; przy sekrecie „zostanie zmienione”, przy ostrzeżeniu jego treść.</summary>
    public string AfterText => IsWarning ? After ?? "" : IsSecret ? "zostanie zmienione" : "Po zapisaniu będzie: " + After;
}

public interface ISettingsChangeConfirmation
{
    /// <summary>Pokazuje listę zmian; <see langword="true"/> = zapisz, <see langword="false"/> = anuluj (nic nie zapisane).</summary>
    /// <param name="changes">Co najmniej jedna zmiana — przy pustej liście wołający w ogóle nie pyta.</param>
    bool Confirm(IReadOnlyList<SettingChange> changes);
}

/// <summary>Odpowiedź „zapisz” bez pytania — dla modeli widoku tworzonych w testach (konstruktor bez okna).</summary>
public sealed class AlwaysConfirm : ISettingsChangeConfirmation
{
    public static AlwaysConfirm Instance { get; } = new();

    public bool Confirm(IReadOnlyList<SettingChange> changes) => true;
}
