using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class AlarmInstructionsViewModel : ObservableObject
{
    private readonly AlarmHelpService _help;
    private readonly Func<string>? _groupStatusText;
    private readonly List<InstructionRow> _rows = [];
    public ObservableCollection<InstructionRow> Entries { get; } = [];
    [ObservableProperty] private InstructionRow? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _newCode = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Zdanie o wspólnych instrukcjach grupy: czy pozostałe komputery mają
    /// już te same treści. Poza grupą pusty tekst — wtedy instrukcje dotyczą tylko tego komputera.
    /// </summary>
    [ObservableProperty] private string _groupStatus = "";

    public bool CanEdit => !IsBusy;
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand RestoreCommand { get; }
    public IRelayCommand AddCodeCommand { get; }

    /// <param name="groupStatus">Skąd wziąć zdanie o zgodności instrukcji w grupie (AppOrchestrator).</param>
    public AlarmInstructionsViewModel(AlarmHelpService help, Func<string>? groupStatus = null)
    {
        _help = help;
        _groupStatusText = groupStatus;
        AddDictionary(help.Rules.Codes, help.Rules.Rules, "", "");
        foreach (var database in help.Rules.Databases.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            AddDictionary(
                database.Value.Codes,
                database.Value.Rules,
                database.Value.CodeIdPrefix ?? "",
                (string.IsNullOrWhiteSpace(database.Value.Name) ? database.Key : database.Value.Name!) + " — ");
        Add(Example("offline", "Brak łączności z maszyną", "offline", AlertKind.Offline), "Brak łączności z maszyną");
        foreach (string category in new[] { "inny", "narzedzie", "tacka", "restart" })
            Add(Example("unknown", "Nierozpoznany błąd", category), "Nierozpoznany błąd — " + category);
        foreach (string key in help.SavedKeys)
            if (key.StartsWith("code:")) AddSavedCode(key[5..]);
        ApplyFilter();
        Selected = Entries.FirstOrDefault();
        Message = help.LoadError ?? "Wybierz błąd. Instrukcja obowiązuje przy tym błędzie na wszystkich maszynach.";
        RefreshMetadata();
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RestoreCommand = new RelayCommand(() =>
        {
            if (Selected is null) return;
            Selected.Text = Selected.DefaultText;
            Message = "Przywrócono treść domyślną w edytorze. Kliknij Zapisz instrukcję, aby zastosować.";
        });
        AddCodeCommand = new RelayCommand(() =>
        {
            if (!int.TryParse(NewCode, out int code) || code is < 0 or > 9999)
            { Message = "Wpisz numer alarmu od 0 do 9999."; return; }
            var row = Add(Example($"alarm:{code}", $"Błąd nr {code}", "inny"), $"{code} — Błąd spoza słownika");
            Search = "";
            ApplyFilter();
            Selected = row;
            Message = "Możesz wpisać instrukcję dla tego numeru alarmu.";
        });
    }

    /// <summary>
    /// Odświeża to, co może zmienić się bez udziału operatora: „ostatnią zmianę” każdego wpisu i stan
    /// zgodności grupy. Treści w edytorze NIE ruszamy — niezapisany szkic operatora jest ważniejszy niż
    /// natychmiastowe pokazanie cudzej wersji, a rozbieżność widać po „Ostatniej zmianie”.
    /// </summary>
    public void RefreshMetadata()
    {
        foreach (var row in _rows) row.SetLastChange(_help.LastChange(row.Key));
        GroupStatus = _groupStatusText?.Invoke() ?? "";
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEdit));
    private void ApplyFilter()
    {
        var selected = Selected;
        Entries.Clear();
        foreach (var row in _rows.Where(r => r.Label.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))) Entries.Add(row);
        Selected = selected is not null && Entries.Contains(selected) ? selected : Entries.FirstOrDefault();
    }

    private void AddDictionary(List<AlarmCode> codes, List<Rule> rules, string codeIdPrefix, string label)
    {
        var table = new AlarmCodeTable(codes);
        for (int code = 0; code <= 9999; code++)
            if (table.TryResolve(code, out var entry))
                Add(Example($"rule:{codeIdPrefix}code-{code}", entry.ShortText, entry.Category), $"{label}{code} — {entry.ShortText}");
        foreach (var rule in rules)
            Add(Example("rule:" + rule.Id, rule.ShortText, rule.Category), label + "Opis — " + rule.ShortText);
    }

    private void AddSavedCode(string saved)
    {
        if (saved.Length is 0 or > 40) return;
        if (int.TryParse(saved, out int code) && code is >= 0 and <= 9999)
        {
            Add(Example($"alarm:{code}", $"Błąd nr {code}", "inny"), $"{code} — Błąd spoza słownika");
            return;
        }

        int separator = saved.LastIndexOf('-');
        if (separator <= 0 || separator == saved.Length - 1) return;
        string number = saved[(separator + 1)..];
        if (!int.TryParse(number, out int _)) return;
        Add(Example($"alarm:{saved}", $"Błąd nr {number}", "inny"), $"{saved[..separator]} — {number} — Błąd spoza słownika");
    }

    private InstructionRow Add(Alert example, string label)
    {
        string key = AlarmHelp.InstructionKey(example);
        var existing = _rows.FirstOrDefault(r => r.Key == key);
        if (existing is not null) return existing;
        var row = new InstructionRow(key, label, _help.BaseInstructionsFor(example), _help.InstructionsFor(example));
        _rows.Add(row);
        return row;
    }

    private async Task SaveAsync()
    {
        if (Selected is not { } row || IsBusy) return;
        IsBusy = true;
        try
        {
            await _help.SaveAsync(row.Key, row.Text == row.DefaultText ? null : row.Text);
            row.MarkSaved();
            RefreshMetadata();
            Message = _help.GroupMode
                ? "Zapisano. Nowa instrukcja jest już widoczna przy alertach i rozeszła się do komputerów grupy."
                : "Zapisano. Nowa instrukcja jest już widoczna przy alertach.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { Message = "Anulowano zapis. Treść pozostaje w edytorze, a dotychczasowa instrukcja nadal obowiązuje."; }
        catch (Exception ex) { Message = "Nie zapisano instrukcji: " + ex.Message; }
        finally { IsBusy = false; }
    }

    internal static Alert Example(string discriminator, string name, string category, AlertKind kind = AlertKind.Error) => new()
    { Key = new("editor", discriminator), MachineId = "editor", MachineName = "Podgląd instrukcji", ShortText = name, Category = category, Kind = kind };
}

public sealed partial class InstructionRow(string key, string label, string defaultText, string text) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string DefaultText { get; } = defaultText;
    private string _saved = text;
    [ObservableProperty] private string _text = text;

    [ObservableProperty] private string _lastChange = "";

    public string DraftStatus => Text != _saved ? "Niezapisane zmiany" : "Brak niezapisanych zmian";
    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(DraftStatus));
    public void MarkSaved() { _saved = Text; OnPropertyChanged(nameof(DraftStatus)); }

    public void SetLastChange(InstructionEntry? entry) => LastChange = entry is null ? ""
        : $"Ostatnia zmiana: {entry.EditedAt.ToLocalTime():dd.MM, HH:mm} — {entry.EditedBy}"
            + (entry.Deleted ? " (przywrócono domyślną)" : "");
}
