namespace MaszynyAlert.App.ViewModels;

/// <summary>
/// Stan „przed” i „po” zapisie ustawień tego komputera (okno Ustawień i Panel → Ustawienia → „Ten komputer”) —
/// wejście <see cref="SetupChanges.Build"/>. Sekrety są tylko flagami „wpisano nowy”, bez wartości.
/// </summary>
public sealed record SetupChangeInput
{
    /// <summary>Hasło VNC wpisane jedno dla wszystkich maszyn.</summary>
    public bool SharedVncPasswordEntered { get; init; }

    /// <summary>Nazwy maszyn z wpisanym własnym hasłem VNC.</summary>
    public IReadOnlyList<string> VncPasswordMachines { get; init; } = [];

    public bool AdminPasswordChanged { get; init; }
    public bool NetworkKeyChanged { get; init; }
    public bool UpdatesSecretChanged { get; init; }

    /// <summary>Identyfikator tokenu przed zapisem (pusty = brak).</summary>
    public string UpdatesIdBefore { get; init; } = "";

    /// <summary>Nowy identyfikator tokenu albo <see langword="null"/>, gdy bez zmian (pusty = usuń).</summary>
    public string? UpdatesIdAfter { get; init; }

    public int PriorityBefore { get; init; }
    public int PriorityAfter { get; init; }
    public bool NotificationsBefore { get; init; }
    public bool NotificationsAfter { get; init; }
    public bool AutostartBefore { get; init; }
    public bool AutostartAfter { get; init; }
}

public static class SetupChanges
{
    public static IReadOnlyList<SettingChange> Build(SetupChangeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var changes = new List<SettingChange>();
        if (input.SharedVncPasswordEntered) changes.Add(SettingChange.Secret("Hasło VNC (wszystkie maszyny)"));
        foreach (string machine in input.VncPasswordMachines) changes.Add(SettingChange.Secret("Hasło VNC — " + machine));
        if (input.NetworkKeyChanged) changes.Add(SettingChange.Secret("Klucz sieciowy"));
        if (input.AdminPasswordChanged) changes.Add(SettingChange.Secret("Hasło Panelu administracyjnego"));
        if (input.PriorityAfter != input.PriorityBefore)
            changes.Add(new SettingChange("Priorytet tego komputera", input.PriorityBefore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                input.PriorityAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (input.AutostartAfter != input.AutostartBefore)
            changes.Add(new SettingChange("Uruchamiaj przy logowaniu", OnOff(input.AutostartBefore), OnOff(input.AutostartAfter)));
        if (input.NotificationsAfter != input.NotificationsBefore)
            changes.Add(new SettingChange("Pokazuj powiadomienia Windows", OnOff(input.NotificationsBefore), OnOff(input.NotificationsAfter)));
        if (input.UpdatesIdAfter is { } id && !string.Equals(id, input.UpdatesIdBefore, StringComparison.Ordinal))
            changes.Add(new SettingChange("Identyfikator tokenu dostępu (katalog wydań)", OrNone(input.UpdatesIdBefore), OrNone(id)));
        if (input.UpdatesSecretChanged) changes.Add(SettingChange.Secret("Sekret tokenu dostępu (katalog wydań)"));
        return changes;
    }

    private static string OnOff(bool value) => value ? "włączone" : "wyłączone";

    private static string OrNone(string value) => value.Length == 0 ? "(brak)" : value;
}
