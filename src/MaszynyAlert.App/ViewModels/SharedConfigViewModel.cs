using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.ViewModels;

/// <summary>Rodzaj kontrolki wiersza ustawienia — decyduje, czym wiersz jest wypełniany w widoku.</summary>
public enum SharedConfigFieldKind
{
    /// <summary>Liczba całkowita (pole tekstowe).</summary>
    Integer,

    /// <summary>Liczba dziesiętna (pole tekstowe; kropka i przecinek działają tak samo).</summary>
    Decimal,

    /// <summary>Lista liczb (pole tekstowe; elementy po średniku, a gdy nie ma ułamków — także po przecinku).</summary>
    NumberList,

    /// <summary>Wartość tak/nie (pole wyboru).</summary>
    Flag,

    /// <summary>Jedna z kilku nazw (lista rozwijana).</summary>
    Choice,

    Machine,
}

public sealed partial class SharedConfigField : ObservableObject
{
    public SharedConfigField(string key, string label, string description, string unit,
        SharedConfigFieldKind kind, IReadOnlyList<string>? choices = null, string detail = "")
    {
        Key = key;
        Label = label;
        Description = description;
        Unit = unit;
        Kind = kind;
        Choices = choices ?? [];
        Detail = detail;
    }

    /// <summary>Szary dopisek za nazwą w wierszu maszyny: <c>host:port · profil</c>; pusty dla pozostałych rodzajów.</summary>
    public string Detail { get; }

    /// <summary>Nazwa pola w <c>config.json</c>, np. <c>timing.escalationAfterAckMs</c>.</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>Czy panel szczegółów ma pokazać klucz osobno — nie, gdy etykieta już nim jest.</summary>
    public bool KeyDiffersFromLabel => !string.Equals(Key, Label, StringComparison.Ordinal);

    /// <summary>Jedno–dwa zdania: czego dotyczy ustawienie, jaki ma zakres i jaka jest wartość domyślna — w jednostce pola.</summary>
    public string Description { get; }

    /// <summary>Jednostka pokazywana obok pola (np. „s”, „min”), pusta gdy nie ma sensu.</summary>
    public string Unit { get; }

    /// <summary>Czym wypełnić wiersz w widoku.</summary>
    public SharedConfigFieldKind Kind { get; }

    /// <summary>Dozwolone nazwy przy <see cref="SharedConfigFieldKind.Choice"/> (zapis jak w config.json).</summary>
    public IReadOnlyList<string> Choices { get; }

    public bool IsFlag => Kind == SharedConfigFieldKind.Flag;
    public bool IsChoice => Kind == SharedConfigFieldKind.Choice;
    public bool IsMachine => Kind == SharedConfigFieldKind.Machine;
    public bool IsTextInput => Kind is SharedConfigFieldKind.Integer or SharedConfigFieldKind.Decimal
        or SharedConfigFieldKind.NumberList;

    /// <summary>Wpisana wartość (dla pola wyboru „true”/„false”, dla listy rozwijanej nazwa z config.json).</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>Wartość z warstwy niżej (config.json + profil grupy) — to wróci po „Przywróć…”.</summary>
    [ObservableProperty]
    private string _defaultText = "";

    /// <summary>
    /// Tekst pola po ostatnim wczytaniu. Pole z tym samym tekstem jest „niezmienione”: przy zapisie idzie
    /// dokładnie wartość wczytana (bez zaokrąglania przez przeliczenie jednostek) i nie ma znacznika zmiany.
    /// </summary>
    [ObservableProperty]
    private string _loadedText = "";

    /// <summary>Komunikat błędu tego pola; pusty, gdy wartość jest w porządku.</summary>
    [ObservableProperty]
    private string _error = "";

    /// <summary>Czy to ustawienie jest zaznaczone — jego opis i klucz pokazuje panel szczegółów.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Wartość dla pola wyboru — ta sama treść co <see cref="Text"/>.</summary>
    public bool Flag
    {
        get => bool.TryParse(Text, out bool value) && value;
        set => Text = value ? "true" : "false";
    }

    public bool HasError => Error.Length > 0;

    /// <summary>
    /// Zamiana wpisu na tekst wartości, która zostanie zapisana (parsowanie i formatowanie jak przy wczytaniu); ustawia model
    /// widoku. Bez niej porównywany jest sam tekst.
    /// </summary>
    internal Func<string, string>? Normalizer { get; set; }

    /// <summary>
    /// Czy wpis oznacza tę samą WARTOŚĆ co wczytana (dla pól czasu — ta sama liczba milisekund, bo tekst w minutach jest
    /// zaokrąglony do 4 miejsc); ustawia model widoku.
    /// </summary>
    internal Func<string, bool>? SameValueAsLoaded { get; set; }

    public bool IsChanged
    {
        get
        {
            if (string.Equals(Text, LoadedText, StringComparison.Ordinal)) return false;
            if (SameValueAsLoaded is null) return true;
            try { return !SameValueAsLoaded(Text); }
            catch (FormatException) { return true; }
        }
    }

    /// <summary>Zdanie „z config.json: …” — widać, co odsłoni „Przywróć…”; w tej samej jednostce co pole.</summary>
    public string DefaultHint => "Z config.json / profilu grupy: " + Display(DefaultText);

    /// <summary>Tekst wartości tak, jak go czyta człowiek: z jednostką pola, flaga jako „włączone/wyłączone”, maszyna jako „włączona/wyłączona”.</summary>
    public string Display(string text) => Kind switch
    {
        SharedConfigFieldKind.Flag => bool.TryParse(text, out bool on) && on ? "włączone" : "wyłączone",
        SharedConfigFieldKind.Machine => bool.TryParse(text, out bool on) && on ? "włączona" : "wyłączona",
        _ when Unit.Length > 0 => text + " " + Unit,
        _ => text,
    };

    partial void OnTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(Flag));
        OnPropertyChanged(nameof(IsChanged));
    }

    partial void OnLoadedTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsChanged));
    }

    partial void OnErrorChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnDefaultTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(DefaultHint));
    }
}

public sealed partial class SharedConfigSection : ObservableObject
{
    public SharedConfigSection(string title)
    {
        Title = title;
        Fields.CollectionChanged += (_, e) =>
        {
            foreach (SharedConfigField field in e.NewItems?.OfType<SharedConfigField>() ?? [])
                field.PropertyChanged += OnFieldChanged;
        };
    }

    /// <summary>Nazwa sekcji w spisie.</summary>
    public string Title { get; }

    /// <summary>Ustawienia sekcji w kolejności pokazywania.</summary>
    public ObservableCollection<SharedConfigField> Fields { get; } = [];

    /// <summary>Skąd pochodzą wartości tej sekcji (wspólna konfiguracja czy config.json).</summary>
    [ObservableProperty]
    private string _sourceText = "";

    /// <summary>Zdanie pokazywane zamiast wierszy, gdy sekcja nie ma żadnego (sekcja „Maszyny” bez maszyn w konfiguracji); pusty = nic.</summary>
    [ObservableProperty]
    private string _emptyText = "";

    /// <summary>Czy któreś ustawienie sekcji ma błąd.</summary>
    public bool HasErrors => Fields.Any(f => f.HasError);

    /// <summary>Czy któreś ustawienie sekcji ma niezapisaną zmianę.</summary>
    public bool HasChanges => Fields.Any(f => f.IsChanged);

    /// <summary>Znacznik przy nazwie w spisie: błąd ma pierwszeństwo przed zmianą; pusty, gdy nic.</summary>
    public string StatusText => HasErrors ? "błąd" : HasChanges ? "zmiany" : "";

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SharedConfigField.Error) or nameof(SharedConfigField.Text)
            or nameof(SharedConfigField.LoadedText))) return;
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(StatusText));
    }
}

public sealed record SharedConfigViewInput(
    AppConfig BaseConfig,
    AppConfig EffectiveConfig,
    SharedConfigDocument? Document,
    string SyncStatus,
    string? LoadError,
    bool InGroup,
    string? SharedFilePath,
    Func<string, bool>? HasLocalVncPassword = null)
{
    /// <summary>Migawka stanu z orkiestratora — używana przez Panel administracyjny.</summary>
    public static SharedConfigViewInput From(AppOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return new SharedConfigViewInput(
            orchestrator.BaseConfig,
            orchestrator.Config,
            orchestrator.SharedConfig.Current,
            orchestrator.SharedConfigSyncStatus,
            orchestrator.SharedConfig.LoadError,
            orchestrator.Config.Cluster.Enabled && orchestrator.Config.Cluster.Group is not null,
            orchestrator.SharedConfig.Path,
            orchestrator.HasLocalVncPassword);
    }
}

public sealed partial class SharedConfigViewModel : ObservableObject
{
    /// <summary>Milisekund w jednostce pola: sekunda.</summary>
    private const int SecondMs = 1000;

    /// <summary>Milisekund w jednostce pola: minuta.</summary>
    private const int MinuteMs = 60000;

    public delegate Task ApplySharedConfig(TimingConfig? timing, OverlayConfig? overlay,
        IReadOnlyDictionary<string, bool>? machines, CancellationToken ct);

    private readonly Func<SharedConfigViewInput> _input;
    private readonly ApplySharedConfig _apply;
    private readonly ISettingsChangeConfirmation _confirmation;
    private readonly List<FieldSpec<TimingConfig>> _timingSpecs = [];
    private readonly List<FieldSpec<OverlayConfig>> _overlaySpecs = [];
    private readonly Dictionary<string, int> _scaleByKey = new(StringComparer.Ordinal);
    private readonly SharedConfigSection _overlaySection = new("Nakładka");
    private readonly SharedConfigSection _machinesSection = new(MachinesSectionTitle);
    private SharedConfigDocument? _document;
    private AppConfig _baseConfig = new();
    private AppConfig _effectiveConfig = new();
    private bool _inGroup;
    private Func<string, bool>? _hasLocalVncPassword;
    private bool _loading;

    /// <summary>Wiersze sekcji <c>timing</c> (wszystkie pola <see cref="TimingConfig"/>).</summary>
    public ObservableCollection<SharedConfigField> TimingFields { get; } = [];

    /// <summary>Wiersze sekcji <c>ui.overlay</c> (wszystkie pola <see cref="OverlayConfig"/>).</summary>
    public ObservableCollection<SharedConfigField> OverlayFields { get; } = [];

    public ObservableCollection<SharedConfigField> MachineFields { get; } = [];

    /// <summary>Wszystkie wiersze.</summary>
    public IEnumerable<SharedConfigField> Fields => TimingFields.Concat(OverlayFields).Concat(MachineFields);

    private bool SectionsChanged => TimingFields.Concat(OverlayFields).Any(f => f.IsChanged);

    /// <summary>Czy administrator przełączył którąś maszynę.</summary>
    private bool MachinesChanged => MachineFields.Any(f => f.IsChanged);

    /// <summary>Nazwa sekcji maszyn w spisie.</summary>
    internal const string MachinesSectionTitle = "Maszyny";

    /// <summary>Opis każdego wiersza maszyny (panel szczegółów) — ten sam dla wszystkich maszyn.</summary>
    internal const string MachineDescription =
        "Włączona maszyna jest obserwowana i pokazywana w Podglądzie; wyłączona znika z Podglądu, nakładki i raportu. "
        + "Zmiana działa od razu na wszystkich komputerach grupy.";

    /// <summary>Komunikat przy polu, gdy zapis wyłączyłby wszystkie maszyny.</summary>
    internal const string NoEnabledMachinesMessage = "Co najmniej jedna maszyna musi zostać włączona.";

    /// <summary>Gdzie zapisuje się hasło VNC maszyny na tym komputerze (okno Ustawień przy pierwszym uruchomieniu ma tę samą grupę).</summary>
    internal const string VncPasswordPlace = "Panel → Ustawienia → „Ten komputer” → „Połączenie z maszynami”";

    internal const string MissingPasswordInGroupMessage =
        "W profilu grupy ta maszyna wymaga hasła VNC, a ten komputer nie ma go zapisanego. W grupie przełącznik działa na wszystkich "
        + "komputerach: komputer bez hasła przestaje być gotowy do obserwacji, a gdy hasła nie ma żaden, grupa zostaje bez "
        + "obserwatora — bez obserwacji są wtedy także pozostałe maszyny. Najpierw zapisz hasło tej maszyny: " + VncPasswordPlace
        + " (na komputerach, które mają obserwować), potem ją włącz. Jeśli serwer VNC tej maszyny w ogóle nie ma hasła, nie wpisuj "
        + "hasła zastępczego — potrzebny jest profil grupy z \"requiresPassword\": false.";

    /// <summary>
    /// Treść ostrzeżenia w liście zmian (poza grupą), gdy zapis włącza maszynę wymagającą hasła VNC, którego ten komputer nie ma. Poza
    /// grupą wymóg z konfiguracji nie blokuje obserwacji — monitor łączy się i bierze typ zabezpieczeń, który oferuje serwer — więc
    /// skutek zależy od serwera.
    /// </summary>
    internal const string MissingPasswordWarningText =
        "W konfiguracji ta maszyna wymaga hasła VNC, a ten komputer nie ma go zapisanego. Jeśli jej serwer VNC pyta o hasło, maszyna "
        + "nie będzie obserwowana (błąd hasła na karcie), dopóki go nie zapiszesz: " + VncPasswordPlace + ". Jeśli serwer hasła nie "
        + "ma, wpisz dla tej maszyny w config.json \"requiresPassword\": false.";

    internal static string MissingPasswordWarningName(string machineName) => "Uwaga — " + machineName + ": brak hasła VNC na tym komputerze";

    public ObservableCollection<SharedConfigSection> Sections { get; } = [];

    /// <summary>Wybrana sekcja — jej ustawienia są po prawej stronie.</summary>
    [ObservableProperty]
    private SharedConfigSection? _selectedSection;

    /// <summary>Zaznaczone ustawienie — jego opis, klucz i wartość domyślna są w panelu szczegółów.</summary>
    [ObservableProperty]
    private SharedConfigField? _selectedField;

    /// <summary>Wyjaśnienie trzech warstw konfiguracji — nagłówek zakładki.</summary>
    public string LayersText =>
        "Ustawienia mają trzy warstwy: config.json na tym komputerze, potem profil grupy komputerów, "
        + "a na wierzchu wspólna konfiguracja zapisana w tym Panelu. Warstwa wyższa przykrywa niższą, "
        + "a przyciski „Przywróć…” zdejmują nadpisanie i odsłaniają wartość z config.json.";

    /// <summary>Zdanie o zgodności wspólnej konfiguracji w grupie; poza grupą pusty tekst.</summary>
    [ObservableProperty]
    private string _syncStatus = "";

    /// <summary>Błąd odczytu pliku wspólnej konfiguracji (na czerwono); pusty, gdy odczyt się udał.</summary>
    [ObservableProperty]
    private string _loadError = "";

    /// <summary>Skąd pochodzą wartości sekcji <c>timing</c>.</summary>
    [ObservableProperty]
    private string _timingSourceText = "";

    /// <summary>Skąd pochodzą wartości sekcji <c>ui.overlay</c>.</summary>
    [ObservableProperty]
    private string _overlaySourceText = "";

    /// <summary>Ścieżka pliku wspólnej konfiguracji — żeby wiadomo było, co kopiować przy diagnostyce.</summary>
    [ObservableProperty]
    private string _filePathText = "";

    /// <summary>Komunikat po ostatniej akcji (zapis, przywrócenie, błąd).</summary>
    [ObservableProperty]
    private string _message = "";

    /// <summary>Czy w polach są zmiany, których jeszcze nie zapisano.</summary>
    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>Czy trwa zapis (na czas zapisu przyciski są zablokowane).</summary>
    [ObservableProperty]
    private bool _isBusy;

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand ResetTimingCommand { get; }
    public IAsyncRelayCommand ResetOverlayCommand { get; }
    public IRelayCommand RefreshCommand { get; }

    /// <summary>Konstruktor dla Panelu administracyjnego — stan i zapis biorą się z orkiestratora, pytanie z okna.</summary>
    public SharedConfigViewModel(AppOrchestrator orchestrator, ISettingsChangeConfirmation confirmation)
        : this(() => SharedConfigViewInput.From(orchestrator),
            (timing, overlay, machines, ct) => orchestrator.ApplySharedConfigAsync(timing, overlay, machines, ct),
            confirmation)
    {
    }

    public SharedConfigViewModel(Func<SharedConfigViewInput> input, ApplySharedConfig apply,
        ISettingsChangeConfirmation? confirmation = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _confirmation = confirmation ?? AlwaysConfirm.Instance;

        BuildTimingFields();
        BuildOverlayFields();
        BuildMachinesSection();

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ResetTimingCommand = new AsyncRelayCommand(ResetTimingAsync);
        ResetOverlayCommand = new AsyncRelayCommand(ResetOverlayAsync);
        RefreshCommand = new RelayCommand(() => { Load(); Message = ""; });

        Load();
        SelectedSection = Sections.FirstOrDefault();
    }

    public SharedConfigViewModel(Func<SharedConfigViewInput> input,
        Func<TimingConfig?, OverlayConfig?, CancellationToken, Task> apply, ISettingsChangeConfirmation? confirmation = null)
        : this(input, WithoutMachines(apply), confirmation)
    {
    }

    private static ApplySharedConfig WithoutMachines(Func<TimingConfig?, OverlayConfig?, CancellationToken, Task> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return (timing, overlay, machines, ct) => machines is null
            ? apply(timing, overlay, ct)
            : throw new NotSupportedException("Ta funkcja zapisu nie przyjmuje przełączników maszyn.");
    }

    partial void OnSelectedSectionChanged(SharedConfigSection? value)
    {
        if (value is not null && (SelectedField is null || !value.Fields.Contains(SelectedField)))
            SelectedField = value.Fields.FirstOrDefault();
    }

    partial void OnSelectedFieldChanged(SharedConfigField? oldValue, SharedConfigField? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>
    /// Odświeżenie „z zewnątrz”: po zmianie konfiguracji na innym komputerze grupy
    /// (<paramref name="changedElsewhere"/> = <see langword="true"/>) albo przy pokazaniu zakładki.
    /// Niezapisanych wpisów administratora NIE nadpisujemy — przy zmianie z sieci mówimy, co się stało,
    /// a przy samym pokazaniu zakładki nic (wpisy są jego). Zdarzenie <c>ConfigChanged</c> po WŁASNYM
    /// zapisie też tu trafia (z opóźnieniem wątku okna) — w trakcie zapisu jest pomijane, bo
    /// <see cref="SaveAsync"/> sam wczyta wartości po zakończeniu.
    /// </summary>
    public void RefreshFromOutside(bool changedElsewhere = true)
    {
        if (IsBusy) return;
        if (HasUnsavedChanges)
        {
            if (changedElsewhere)
                Message = "Konfiguracja zmieniła się na innym komputerze — Odśwież, żeby zobaczyć nowe wartości.";
            return;
        }

        Load();
    }

    /// <summary>Wczytuje wartości z bieżącego stanu i kasuje znacznik niezapisanych zmian.</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            SharedConfigViewInput input = _input();
            _document = input.Document;
            _baseConfig = input.BaseConfig;
            _effectiveConfig = input.EffectiveConfig;
            _inGroup = input.InGroup;
            _hasLocalVncPassword = input.HasLocalVncPassword;

            foreach (FieldSpec<TimingConfig> spec in _timingSpecs)
                Fill(spec.Field, spec.Format(input.EffectiveConfig.Timing), spec.Format(input.BaseConfig.Timing));

            foreach (FieldSpec<OverlayConfig> spec in _overlaySpecs)
                Fill(spec.Field, spec.Format(input.EffectiveConfig.Ui.Overlay), spec.Format(input.BaseConfig.Ui.Overlay));

            SyncMachineRows(input);

            SyncStatus = input.SyncStatus;
            LoadError = input.LoadError ?? "";
            TimingSourceText = SourceText(input.Document?.Timing is not null, input.Document);
            OverlaySourceText = SourceText(input.Document?.Overlay is not null, input.Document);
            string machinesSourceText = SourceText(input.Document?.Machines is not null, input.Document);
            foreach (SharedConfigSection section in Sections)
            {
                section.SourceText = ReferenceEquals(section, _overlaySection) ? OverlaySourceText
                    : ReferenceEquals(section, _machinesSection) ? machinesSourceText
                    : TimingSourceText;
            }
            FilePathText = string.IsNullOrEmpty(input.SharedFilePath)
                ? "" : "Plik wspólnej konfiguracji: " + input.SharedFilePath;
            HasUnsavedChanges = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private static void Fill(SharedConfigField field, string text, string defaultText)
    {
        field.LoadedText = text;
        field.Text = text;
        field.DefaultText = defaultText;
        field.Error = "";
    }

    /// <summary>
    /// Wiersze maszyn z konfiguracji obowiązującej: odtwarzane od nowa tylko, gdy zestaw maszyn (identyfikatory, nazwy, adresy,
    /// profile) się zmienił — inaczej wypełniane w miejscu, żeby zaznaczenie i wpisy nie ginęły przy każdym odświeżeniu.
    /// </summary>
    private void SyncMachineRows(SharedConfigViewInput input)
    {
        IReadOnlyList<MachineConfig> machines = input.EffectiveConfig.Machines;
        bool sameSet = MachineFields.Count == machines.Count
            && MachineFields.Zip(machines).All(p => p.First.Key == MachineKey(p.Second.Id) && p.First.Label == p.Second.Name
                && p.First.Detail == MachineDetail(p.Second));
        if (!sameSet)
        {
            bool reselect = SelectedField is { IsMachine: true };
            foreach (SharedConfigField old in MachineFields) old.PropertyChanged -= OnFieldChanged;
            MachineFields.Clear();
            _machinesSection.Fields.Clear();
            foreach (MachineConfig machine in machines)
            {
                var field = new SharedConfigField(MachineKey(machine.Id), machine.Name, MachineDescription, "",
                    SharedConfigFieldKind.Machine, detail: MachineDetail(machine));
                string id = machine.Id;
                field.Normalizer = text => Text(Flag(text));
                field.SameValueAsLoaded = text => Flag(text) == EnabledOf(_effectiveConfig, id);
                field.PropertyChanged += OnFieldChanged;
                MachineFields.Add(field);
                _machinesSection.Fields.Add(field);
            }
            if (reselect) SelectedField = _machinesSection.Fields.FirstOrDefault();
        }
        _machinesSection.EmptyText = machines.Count == 0 ? "Brak maszyn w konfiguracji (config.json / profil grupy)." : "";

        foreach ((SharedConfigField field, MachineConfig machine) in MachineFields.Zip(machines))
            Fill(field, Text(machine.Enabled), Text(EnabledOf(input.BaseConfig, machine.Id)));
    }

    /// <summary>Klucz wiersza maszyny — nazwa jak w JSON warstwy wspólnej: <c>machines.&lt;id&gt;.enabled</c>.</summary>
    internal static string MachineKey(string machineId) => "machines." + machineId + ".enabled";

    private static string MachineDetail(MachineConfig machine) => $"{machine.Host}:{machine.Port} · {machine.Profile}";

    private static bool EnabledOf(AppConfig config, string machineId) =>
        config.Machines.FirstOrDefault(m => m.Id == machineId)?.Enabled ?? false;

    private static string SourceText(bool overridden, SharedConfigDocument? document) =>
        overridden && document is not null
            ? $"Źródło wartości: wspólna konfiguracja — ostatnia zmiana wspólnej konfiguracji (dowolnego ustawienia): "
              + $"{document.EditedAt.ToLocalTime():dd.MM HH:mm} na {document.EditedBy}."
            : "Źródło wartości: z config.json / profilu grupy (bez nadpisania).";

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(SharedConfigField.Text)) return;
        HasUnsavedChanges = true;
    }

    public IReadOnlyList<SettingChange> PendingChanges()
    {
        var changes = Fields.Where(f => f.IsChanged)
            .Select(f => new SettingChange(ChangeName(f), f.Display(f.LoadedText), f.Display(SavedText(f))))
            .ToList();
        if (changes.Count == 0) return changes;
        if (SectionsChanged)
        {
            if (_document?.Timing is null)
                changes.Add(new SettingChange("Źródło wartości: czas i progi", "config.json / profil grupy", "wspólna konfiguracja"));
            if (_document?.Overlay is null)
                changes.Add(new SettingChange("Źródło wartości: nakładka", "config.json / profil grupy", "wspólna konfiguracja"));
        }
        if (MachinesChanged && _document?.Machines is null)
            changes.Add(new SettingChange("Źródło wartości: maszyny", "config.json / profil grupy", "wspólna konfiguracja"));
        if (MachinesChanged)
        {
            foreach (MachineConfig machine in MissingPasswordAfter(ProjectedMachines()))
                changes.Add(SettingChange.Warning(MissingPasswordWarningName(machine.Name), MissingPasswordWarningText));
        }
        return changes;
    }

    /// <summary>
    /// Lista maszyn z przełącznikami z pól sekcji „Maszyny” (wpis, którego nie da się odczytać, liczy się jak wartość wczytana — jego
    /// błąd pokaże zapis). Wiersze powstają z konfiguracji obowiązującej w tej samej kolejności (<see cref="SyncMachineRows"/>).
    /// </summary>
    private List<MachineConfig> ProjectedMachines() =>
        MachineFields.Zip(_effectiveConfig.Machines)
            .Select(p => bool.TryParse(p.First.Text.Trim(), out bool on) ? p.Second with { Enabled = on } : p.Second)
            .ToList();

    private List<MachineConfig> MissingPasswordAfter(List<MachineConfig> next)
    {
        if (_hasLocalVncPassword is not { } hasPassword) return [];
        AppConfig current = _inGroup ? _effectiveConfig with { Enabled = true } : _effectiveConfig;
        IReadOnlyList<string> missing = Core.Cluster.WatcherReadiness.MissingPasswordsAfterEnabling(
            current, current with { Machines = next }, hasPassword);
        return next.Where(m => missing.Contains(m.Id, StringComparer.Ordinal)).ToList();
    }

    private static string ChangeName(SharedConfigField field) => field.IsMachine ? "Maszyna: " + field.Label : field.Label;

    /// <summary>Tekst wartości, która zostanie zapisana (wpis sparsowany i sformatowany jak przy wczytaniu); błędny wpis — bez zmian.</summary>
    private static string SavedText(SharedConfigField field)
    {
        if (field.Normalizer is not { } normalize) return field.Text;
        try { return normalize(field.Text); }
        catch (FormatException) { return field.Text; }
    }

    private static string SectionsHash(SharedConfigDocument? document, bool includeMachines = false) =>
        new SharedConfigDocument
        {
            Timing = document?.Timing, Overlay = document?.Overlay,
            Machines = includeMachines ? document?.Machines : null,
        }.Hash;

    /// <summary>
    /// Co zmieni „Przywróć timing…” (<paramref name="timing"/> = <see langword="true"/>) albo „Przywróć nakładkę…”:
    /// pola, których wartość obowiązująca różni się od tej z config.json / profilu grupy, oraz źródło wartości, gdy
    /// sekcja jest nadpisana wspólną konfiguracją. Pusta lista = nie ma czego przywracać.
    /// </summary>
    public IReadOnlyList<SettingChange> ResetChanges(bool timing) => ResetChanges(timing, _input());

    private IReadOnlyList<SettingChange> ResetChanges(bool timing, SharedConfigViewInput input)
    {
        IEnumerable<(SharedConfigField Field, string Current, string Default)> rows = timing
            ? _timingSpecs.Select(s => (s.Field, s.Format(input.EffectiveConfig.Timing), s.Format(input.BaseConfig.Timing)))
            : _overlaySpecs.Select(s => (s.Field, s.Format(input.EffectiveConfig.Ui.Overlay), s.Format(input.BaseConfig.Ui.Overlay)));
        var changes = rows.Where(r => !string.Equals(r.Current, r.Default, StringComparison.Ordinal))
            .Select(r => new SettingChange(r.Field.Label, r.Field.Display(r.Current), r.Field.Display(r.Default)))
            .ToList();
        bool overridden = timing ? input.Document?.Timing is not null : input.Document?.Overlay is not null;
        if (overridden)
            changes.Add(new SettingChange(timing ? "Źródło wartości: czas i progi" : "Źródło wartości: nakładka",
                "wspólna konfiguracja", "config.json / profil grupy"));
        return changes;
    }

    private async Task SaveAsync()
    {
        if (IsBusy) return;
        if (!TryBuild(out TimingConfig timing, out OverlayConfig overlay, out IReadOnlyDictionary<string, bool>? machines))
        {
            return;
        }

        IReadOnlyList<SettingChange> changes = PendingChanges();
        if (changes.Count == 0)
        {
            Message = "Brak zmian do zapisania.";
            return;
        }
        if (!_confirmation.Confirm(changes))
        {
            Message = "Nie zapisano — anulowano. Wpisy zostały w polach.";
            return;
        }
        bool includeMachines = machines is not null;
        if (!string.Equals(SectionsHash(_input().Document, includeMachines),
            SectionsHash(_document, includeMachines), StringComparison.Ordinal))
        {
            Message = ChangedElsewhereMessage;
            return;
        }

        bool sectionsChanged = SectionsChanged;
        IsBusy = true;
        try
        {
            await _apply(sectionsChanged ? timing : _document?.Timing, sectionsChanged ? overlay : _document?.Overlay,
                machines, CancellationToken.None);
        }
        catch (ConfigException ex)
        {
            string message = ToFieldUnits(ex.Message);
            AssignError(message);
            Message = "Nie zapisano: " + message;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Nie zapisano wspólnej konfiguracji: " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Load();
        Message = "Zapisano. W grupie komputerów zmiana rozejdzie się sama; poza grupą działa na tym komputerze.";
        Saved?.Invoke(SavedTitle);
    }

    public event Action<string>? Saved;

    /// <summary>Tytuł dymka po udanym zapisie.</summary>
    internal const string SavedTitle = "Zapisano ustawienia";

    /// <summary>Tytuł dymka po udanym „Przywróć…”.</summary>
    internal const string RestoredTitle = "Przywrócono ustawienia";

    /// <summary>Komunikat, gdy warstwa wspólna zmieniła się między wczytaniem (albo pytaniem) a zapisem.</summary>
    internal const string ChangedElsewhereMessage =
        "Konfiguracja zmieniła się na innym komputerze — sprawdź i spróbuj ponownie (Odśwież).";

    /// <summary>Ile razy „Przywróć…” pyta od nowa, gdy w trakcie pytania przyszła zmiana z innego komputera.</summary>
    private const int MaxResetQuestions = 3;

    private Task ResetTimingAsync() => ResetAsync(timing: true, "Przywrócono ustawienia czasu z config.json / profilu grupy.");

    private Task ResetOverlayAsync() => ResetAsync(timing: false, "Przywrócono ustawienia nakładki z config.json / profilu grupy.");

    private async Task ResetAsync(bool timing, string done)
    {
        if (IsBusy) return;
        SharedConfigDocument? current;
        for (int question = 1; ; question++)
        {
            SharedConfigViewInput input = _input();
            IReadOnlyList<SettingChange> changes = ResetChanges(timing, input);
            if (changes.Count == 0)
            {
                Message = "Brak zmian — wartości już pochodzą z config.json / profilu grupy.";
                return;
            }
            if (!_confirmation.Confirm(changes))
            {
                Message = "Nie przywrócono — anulowano.";
                return;
            }

            current = _input().Document;
            if (string.Equals(SectionsHash(current), SectionsHash(input.Document), StringComparison.Ordinal)) break;
            if (question >= MaxResetQuestions)
            {
                Message = ChangedElsewhereMessage;
                return;
            }
        }

        IsBusy = true;
        try
        {
            await _apply(timing ? null : current?.Timing, timing ? current?.Overlay : null, null, CancellationToken.None);
        }
        catch (ConfigException ex)
        {
            Message = "Nie przywrócono: " + ToFieldUnits(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Nie przywrócono wspólnej konfiguracji: " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Load();
        Message = done;
        Saved?.Invoke(RestoredTitle);
    }

    private bool TryBuild(out TimingConfig timing, out OverlayConfig overlay, out IReadOnlyDictionary<string, bool>? machines)
    {
        timing = _effectiveConfig.Timing;
        overlay = _effectiveConfig.Ui.Overlay;
        machines = null;
        bool ok = true;

        foreach (FieldSpec<TimingConfig> spec in _timingSpecs)
        {
            if (!spec.Field.IsChanged) { spec.Field.Error = ""; continue; }
            try
            {
                timing = spec.Parse(timing, spec.Field.Text);
                spec.Field.Error = "";
            }
            catch (FormatException ex)
            {
                spec.Field.Error = ex.Message;
                ok = false;
            }
        }

        foreach (FieldSpec<OverlayConfig> spec in _overlaySpecs)
        {
            if (!spec.Field.IsChanged) { spec.Field.Error = ""; continue; }
            try
            {
                overlay = spec.Parse(overlay, spec.Field.Text);
                spec.Field.Error = "";
            }
            catch (FormatException ex)
            {
                spec.Field.Error = ex.Message;
                ok = false;
            }
        }

        List<MachineConfig> machineList = _effectiveConfig.Machines;
        if (MachinesChanged)
        {
            var switches = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach ((SharedConfigField field, MachineConfig machine) in MachineFields.Zip(_effectiveConfig.Machines))
            {
                try { switches[machine.Id] = Flag(field.Text); field.Error = ""; }
                catch (FormatException ex) { field.Error = ex.Message; ok = false; }
            }
            if (ok && !switches.Values.Any(on => on))
            {
                MachineFields.First(f => f.IsChanged).Error = NoEnabledMachinesMessage;
                ok = false;
            }
            if (ok)
            {
                List<MachineConfig> next = _effectiveConfig.Machines.Select(m => m with { Enabled = switches[m.Id] }).ToList();
                if (_inGroup)
                {
                    foreach (MachineConfig refused in MissingPasswordAfter(next))
                    {
                        MachineFields.First(f => f.Key == MachineKey(refused.Id)).Error = MissingPasswordInGroupMessage;
                        ok = false;
                    }
                }
                if (ok)
                {
                    machines = switches;
                    machineList = next;
                }
            }
        }
        else
        {
            foreach (SharedConfigField field in MachineFields) field.Error = "";
        }

        if (!ok)
        {
            Message = "Nie zapisano — popraw pola opisane na czerwono.";
            ShowFirstError();
            return false;
        }

        try
        {
            ConfigLoader.Validate(_baseConfig with
            {
                Timing = timing,
                Machines = machineList,
                Ui = _baseConfig.Ui with { Overlay = overlay },
            });
        }
        catch (ConfigException ex)
        {
            string message = ToFieldUnits(ex.Message);
            AssignError(message);
            Message = "Nie zapisano: " + message;
            ShowFirstError();
            return false;
        }

        return true;
    }

    /// <summary>Przypisuje komunikat walidacji do pola o tej nazwie; gdy się nie da — zostaje komunikat zbiorczy.</summary>
    private void AssignError(string message)
    {
        // Komunikat bywa o dwóch polach naraz („fullFrameIntervalMs nie może być mniejsze niż pollIntervalMs”) —
        // bierzemy to, które pada PIERWSZE, bo o nie chodzi walidacji.
        SharedConfigField? best = null;
        int bestIndex = int.MaxValue;
        foreach (SharedConfigField field in Fields)
        {
            int index = message.IndexOf(field.Key, StringComparison.Ordinal);
            if (index < 0 || index >= bestIndex) continue;
            best = field;
            bestIndex = index;
        }

        if (best is not null) best.Error = message;
    }

    /// <summary>
    /// Błąd w sekcji, której administrator teraz nie widzi, byłby widoczny tylko jako znacznik w spisie — więc
    /// gdy wybrana sekcja jest bez błędów, przechodzimy do pierwszej sekcji z błędem i zaznaczamy to pole.
    /// </summary>
    private void ShowFirstError()
    {
        if (SelectedSection?.HasErrors == true)
        {
            if (SelectedField?.HasError != true) SelectedField = SelectedSection.Fields.First(f => f.HasError);
            return;
        }

        SharedConfigSection? section = Sections.FirstOrDefault(s => s.HasErrors);
        if (section is null) return;
        SelectedSection = section;
        SelectedField = section.Fields.First(f => f.HasError);
    }


    private static readonly Regex KeyWithValue = new(@"'(?<key>timing\.[A-Za-z]+)' \((?<value>-?\d+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex Milliseconds = new(@"(?<value>-?\d+) ms\b", RegexOptions.CultureInvariant);
    private static readonly Regex ListValue = new(@"wartość \((?<value>-?\d+)\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Liczby milisekund w komunikacie walidacji → jednostka pola: <c>'timing.x' (N)</c> według pola x,
    /// <c>N ms</c> i wartość z listy według pierwszego pola w komunikacie, które ma jednostkę czasu.
    /// </summary>
    internal string ToFieldUnits(string message)
    {
        int? firstScale = null;
        string converted = KeyWithValue.Replace(message, match =>
        {
            if (!_scaleByKey.TryGetValue(match.Groups["key"].Value, out int scale)) return match.Value;
            firstScale ??= scale;
            return $"'{match.Groups["key"].Value}' ({Duration(int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture), scale)} {UnitName(scale)})";
        });

        if (firstScale is null)
        {
            foreach ((string key, int scale) in _scaleByKey)
            {
                if (!message.Contains(key, StringComparison.Ordinal)) continue;
                firstScale = scale;
                break;
            }
        }

        if (firstScale is not { } unitScale) return converted;
        converted = Milliseconds.Replace(converted, match =>
            $"{Duration(int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture), unitScale)} {UnitName(unitScale)}");
        return ListValue.Replace(converted, match =>
            $"wartość ({Duration(int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture), unitScale)} {UnitName(unitScale)})");
    }

    private static string UnitName(int scale) => scale == MinuteMs ? "min" : "s";


    private void BuildTimingFields()
    {
        SharedConfigSection idle = Section("Postój maszyny");
        SharedConfigSection alarm = Section("Alarm i dźwięk");
        SharedConfigSection connection = Section("Połączenie z maszyną");
        SharedConfigSection screen = Section("Ekran HMI i OCR");

        // --- Postój maszyny ---
        AddDuration(idle, "timing.idleAlertAfterMs", "Postój na 100 % — dołóż prace",
            "Ile maszyna musi nieprzerwanie stać z postępem 100 %, zanim zgłosimy postój „dołóż prace”. Minimum 10 s (ok. 0,17 min), domyślnie 2 min.",
            MinuteMs, t => t.IdleAlertAfterMs, (t, v) => t with { IdleAlertAfterMs = v });

        AddDuration(idle, "timing.idleAtZeroAfterMs", "Postój na 0 % — wciśnij Start",
            "Ile postęp musi nieprzerwanie stać na 0 % (przy zamkniętym błędzie), zanim zgłosimy postój „wciśnij Start”. Minimum 0,5 min (30 s), domyślnie 3 min.",
            MinuteMs, t => t.IdleAtZeroAfterMs, (t, v) => t with { IdleAtZeroAfterMs = v });

        AddTiming(idle, "timing.idlePopupSeconds", "Czas pokazywania pop-upu postoju",
            "Ile sekund widać okienko „maszyna stoi bez pracy”, zanim samo się schowa. 0 = czeka na „✕ Skasuj”. Zakres 0–60, domyślnie 0.",
            "s", t => Text(t.IdlePopupSeconds), (t, s) => t with { IdlePopupSeconds = Integer(s) });

        // --- Alarm i dźwięk ---
        AddDuration(alarm, "timing.alertSoundRepeatMs", "Powtarzanie dźwięku alarmu",
            "Co ile powtarzać dźwięk alarmu, dopóki jest choć jeden niepotwierdzony błąd. Ułamki dozwolone (0,5 = 30 s). Minimum 1 s, domyślnie 1 min.",
            MinuteMs, t => t.AlertSoundRepeatMs, (t, v) => t with { AlertSoundRepeatMs = v });

        AddDuration(alarm, "timing.escalationAfterAckMs", "Eskalacja po „Idę sprawdzić”",
            "Po jakim czasie od potwierdzenia błąd, który nadal trwa, dzwoni ponownie. 0 = eskalacja wyłączona. Domyślnie 4 min.",
            MinuteMs, t => t.EscalationAfterAckMs, (t, v) => t with { EscalationAfterAckMs = v });

        AddDuration(alarm, "timing.episodeCloseAfterMs", "Zamknięcie alertu po zniknięciu błędu",
            "Po jakim czasie bez czerwieni na ekranie alert zamyka się sam, gdy nikt go nie potwierdził. Minimum 1 s, domyślnie 30 s.",
            SecondMs, t => t.EpisodeCloseAfterMs, (t, v) => t with { EpisodeCloseAfterMs = v });

        AddDuration(alarm, "timing.episodeBreakAfterMs", "Przerwanie epizodu błędu",
            "Po jakim czasie ciągłego braku czerwieni kolejny czerwony komunikat o innej treści jest już NOWYM błędem (nowy dźwięk). Krótsze mrugnięcia HMI nie przerywają epizodu. Nie więcej niż czas zamknięcia alertu, domyślnie 10 s.",
            SecondMs, t => t.EpisodeBreakAfterMs, (t, v) => t with { EpisodeBreakAfterMs = v });

        AddDuration(alarm, "timing.sameMessageRejoinMs", "Powrót tego samego komunikatu",
            "Jak długo po zamknięciu epizodu ten sam komunikat wracający na pasek dołącza do poprzedniego alertu, zamiast dzwonić jak nowy błąd. 0 = bez łączenia; inaczej nie krócej niż przerwanie epizodu. Domyślnie 2 min.",
            MinuteMs, t => t.SameMessageRejoinMs, (t, v) => t with { SameMessageRejoinMs = v });

        AddTiming(alarm, "timing.ackMemoryHours", "Pamięć potwierdzeń alertów",
            "Jak długo pamiętać potwierdzenia „Idę sprawdzić”, żeby komputer przejmujący obserwację nie wznowił zgłoszonych już alertów. Domyślnie 2 godziny.",
            "godz.", t => Text(t.AckMemoryHours), (t, s) => t with { AckMemoryHours = Integer(s) });

        // --- Połączenie z maszyną ---
        AddDuration(connection, "timing.pollIntervalMs", "Odstęp odczytów paska statusu",
            "Co ile obserwator pobiera pasek statusu maszyny przy stałym połączeniu. Krócej = szybsza reakcja i większy ruch w sieci. Minimum 0,1 s, domyślnie 1 s.",
            SecondMs, t => t.PollIntervalMs, (t, v) => t with { PollIntervalMs = v });

        AddDuration(connection, "timing.fullFrameIntervalMs", "Odstęp pełnych klatek ekranu",
            "Co ile zamiast samego paska statusu pobrać pełny obraz ekranu HMI. Nie może być mniejszy niż odstęp odczytów. Domyślnie 60 s (1 min).",
            SecondMs, t => t.FullFrameIntervalMs, (t, v) => t with { FullFrameIntervalMs = v });

        AddDuration(connection, "timing.pollingModeIntervalMs", "Odstęp w trybie połączeń chwilowych",
            "Co ile powtarzać cykl połącz – pobierz – rozłącz dla maszyn obsługiwanych bez stałego połączenia. Minimum 0,5 s, domyślnie 5 s.",
            SecondMs, t => t.PollingModeIntervalMs, (t, v) => t with { PollingModeIntervalMs = v });

        AddDuration(connection, "timing.offlineAlertAfterMs", "Alert o braku łączności",
            "Po jakim czasie bez łączności z maszyną pokazać alert „brak łączności”. 0 = od razu przy zerwaniu połączenia. Domyślnie 3 min.",
            MinuteMs, t => t.OfflineAlertAfterMs, (t, v) => t with { OfflineAlertAfterMs = v });

        AddTiming(connection, "timing.offlineAlertSound", "Dźwięk przy braku łączności",
            "Czy alert o braku łączności ma dzwonić. Domyślnie wyłączone — restart maszyny jest normalną sytuacją i nie powinien budzić hali.",
            "", t => Text(t.OfflineAlertSound), (t, s) => t with { OfflineAlertSound = Flag(s) },
            SharedConfigFieldKind.Flag);

        AddTiming(connection, "timing.reconnectBackoffMs", "Odstępy ponawiania połączenia",
            "Kolejne odstępy przy ponawianiu połączenia po utracie łączności, w sekundach, oddzielone średnikiem (np. 2; 5; 15; 30 — przecinek też rozdziela, gdy nie ma ułamków); ostatnia wartość powtarza się dalej. Domyślnie 2; 5; 15; 30.",
            "s", t => SecondsList(t.ReconnectBackoffMs), (t, s) => t with { ReconnectBackoffMs = ParseSecondsList(s) },
            SharedConfigFieldKind.NumberList);
        _scaleByKey["timing.reconnectBackoffMs"] = SecondMs;

        AddDuration(connection, "timing.connectTimeoutMs", "Limit czasu na połączenie",
            "Ile czekać na nawiązanie połączenia VNC z maszyną, zanim uznamy próbę za nieudaną. Minimum 0,5 s, domyślnie 10 s.",
            SecondMs, t => t.ConnectTimeoutMs, (t, v) => t with { ConnectTimeoutMs = v });

        AddDuration(connection, "timing.readTimeoutMs", "Limit czasu na odczyt z maszyny",
            "Twardy limit na pojedynczy odczyt z gniazda VNC — chroni przed zerwanym albo zawieszonym połączeniem. Minimum 1 s, domyślnie 30 s.",
            SecondMs, t => t.ReadTimeoutMs, (t, v) => t with { ReadTimeoutMs = v });

        AddDuration(connection, "timing.idleUpdateTimeoutMs", "Oczekiwanie na zmianę ekranu",
            "Ile czekać na odpowiedź maszyny o zmianie obrazu, zanim uznamy „bez zmian” i pójdziemy dalej bez rozłączania. Od 0,1 s do wartości limitu odczytu, domyślnie 1 s.",
            SecondMs, t => t.IdleUpdateTimeoutMs, (t, v) => t with { IdleUpdateTimeoutMs = v });

        // --- Ekran HMI i OCR ---
        AddDuration(screen, "timing.mainScreenCheckIntervalMs", "Kontrola ekranu głównego HMI",
            "Co ile sprawdzać (odczyt pełnej klatki), czy HMI pokazuje ekran główny z alertami i procentem. Minimum 1 s, domyślnie 10 s.",
            SecondMs, t => t.MainScreenCheckIntervalMs, (t, v) => t with { MainScreenCheckIntervalMs = v });

        AddDuration(screen, "timing.mainScreenOcrGraceMs", "Ważność odczytu OCR pełnej klatki",
            "Ile czekać na wynik OCR pełnej klatki (kontrola ekranu i odczyt procentu), zanim uznamy odczyt za przeterminowany. Minimum 5 s, domyślnie 15 s.",
            SecondMs, t => t.MainScreenOcrGraceMs, (t, v) => t with { MainScreenOcrGraceMs = v });

        AddDuration(screen, "timing.wrongScreenAlertAfterMs", "Zgłoszenie niewłaściwego ekranu HMI",
            "Ile HMI musi nieprzerwanie pokazywać inny ekran niż główny, zanim pokażemy „włącz okno podglądu stanu maszyny”. 0 = od razu przy pierwszym pewnym odczycie. Domyślnie 0 s.",
            SecondMs, t => t.WrongScreenAlertAfterMs, (t, v) => t with { WrongScreenAlertAfterMs = v });

        AddTiming(screen, "timing.wrongScreenAlertSound", "Dźwięk przy niewłaściwym ekranie",
            "Czy powiadomienie o niewłaściwym ekranie HMI ma zagrać dźwięk (raz, przy pojawieniu się). Domyślnie wyłączone — to przypomnienie organizacyjne, nie awaria maszyny.",
            "", t => Text(t.WrongScreenAlertSound), (t, s) => t with { WrongScreenAlertSound = Flag(s) },
            SharedConfigFieldKind.Flag);

        AddDuration(screen, "timing.wrongScreenRemindAfterMs", "Przypomnienie o ekranie HMI",
            "Po jakim czasie od zamknięcia (✕) przypomnieć o niewłaściwym ekranie HMI, jeśli nadal jest niewłaściwy — program jest wtedy ślepy na błędy tej maszyny. 0 = bez ponawiania. Domyślnie 4 min.",
            MinuteMs, t => t.WrongScreenRemindAfterMs, (t, v) => t with { WrongScreenRemindAfterMs = v });

        AddTiming(screen, "timing.ocrMaxAttempts", "Liczba prób odczytu tekstu (OCR)",
            "Ile razy powtórzyć odczyt tekstu błędu w jednym epizodzie, gdy poprzednia próba nie dała tekstu. 0 = bez ponawiania. Domyślnie 3 próby.",
            "prób", t => Text(t.OcrMaxAttempts), (t, s) => t with { OcrMaxAttempts = Integer(s) });

        AddDuration(screen, "timing.ocrRetryIntervalMs", "Odstęp prób OCR po zmianie paska",
            "Minimalny odstęp między odczytami OCR wywołanymi zmianą treści paska statusu — HMI pokazuje komunikaty jednego błędu po kolei. 0 = bez ograniczenia. Domyślnie 2 s.",
            SecondMs, t => t.OcrRetryIntervalMs, (t, v) => t with { OcrRetryIntervalMs = v });
    }

    private void BuildMachinesSection() => Sections.Add(_machinesSection);

    private void BuildOverlayFields()
    {
        Sections.Add(_overlaySection);

        AddOverlay("ui.overlay.enabled", "Nakładka widoczna po starcie",
            "Czy pasek kafelków ze stanem maszyn ma się pokazać zaraz po uruchomieniu programu. Operator i tak włącza go oraz wyłącza z menu zasobnika. W config.example.json włączona.",
            "", o => Text(o.Enabled), (o, s) => o with { Enabled = Flag(s) }, SharedConfigFieldKind.Flag);

        AddOverlay("ui.overlay.opacity", "Przezroczystość nakładki",
            "Przezroczystość nakładki, gdy kursor NIE jest nad nią; po najechaniu myszą jest w pełni widoczna. Zakres 0,2–1,0, domyślnie 0.55.",
            "", o => Text(o.Opacity), (o, s) => o with { Opacity = Decimal(s) }, SharedConfigFieldKind.Decimal);

        AddOverlay("ui.overlay.position", "Róg ekranu dla nakładki",
            "W którym rogu obszaru roboczego nakładka staje, zanim operator ją przeciągnie. Do wyboru: top-left, top-right, bottom-left, bottom-right. Domyślnie top-left.",
            "", o => Text(o.Position), (o, s) => o with { Position = Position(s) }, SharedConfigFieldKind.Choice,
            ["top-left", "top-right", "bottom-left", "bottom-right"]);

        AddOverlay("ui.overlay.scale", "Powiększenie kafelków",
            "Powiększenie kafelków nakładki — dla ekranu 4K albo dla operatora patrzącego z dystansu. Zakres 0,6–2,0, domyślnie 1.0.",
            "×", o => Text(o.Scale), (o, s) => o with { Scale = Decimal(s) }, SharedConfigFieldKind.Decimal);

        AddOverlay("ui.overlay.layout", "Układ kafelków",
            "Jak ułożyć kafelki maszyn: one-row — jeden poziomy wiersz (jak dotąd); two-rows — dwa wiersze, jeden nad drugim (przy sześciu maszynach 2 × 3); one-column — słupek pionowy; two-columns i three-columns — siatka, kafelki idą wierszami. Domyślnie one-row. Operator może wybrać inny układ z menu zasobnika; jego wybór obowiązuje, dopóki ta wartość się nie zmieni.",
            "", o => OverlayLayouts.Name(o.Layout), (o, s) => o with { Layout = Layout(s) }, SharedConfigFieldKind.Choice,
            OverlayLayouts.All.Select(OverlayLayouts.Name).ToArray());

        AddOverlay("ui.overlay.clickThrough", "Przenikanie kliknięć",
            "Czy kliknięcia mają przechodzić „na wylot” przez nakładkę. Domyślnie wyłączone: kliknięcie kafelka otwiera Podgląd tej maszyny.",
            "", o => Text(o.ClickThrough), (o, s) => o with { ClickThrough = Flag(s) }, SharedConfigFieldKind.Flag);
    }

    private SharedConfigSection Section(string title)
    {
        var section = new SharedConfigSection(title);
        Sections.Add(section);
        return section;
    }

    /// <summary>
    /// Pole czasu zapisanego w milisekundach, pokazywane w sekundach (<paramref name="scale"/> = 1000) albo
    /// minutach (60 000). Ułamki dozwolone; wpis jest zaokrąglany do pełnych milisekund.
    /// </summary>
    private void AddDuration(SharedConfigSection section, string key, string label, string description, int scale,
        Func<TimingConfig, int> read, Func<TimingConfig, int, TimingConfig> write)
    {
        _scaleByKey[key] = scale;
        AddTiming(section, key, label, description, UnitName(scale),
            t => Duration(read(t), scale), (t, s) => write(t, ParseDuration(s, scale)), SharedConfigFieldKind.Decimal);
        TimingFields[^1].SameValueAsLoaded = text => ParseDuration(text, scale) == read(_effectiveConfig.Timing);
    }

    private void AddTiming(SharedConfigSection section, string key, string label, string description, string unit,
        Func<TimingConfig, string> format, Func<TimingConfig, string, TimingConfig> parse,
        SharedConfigFieldKind kind = SharedConfigFieldKind.Integer, IReadOnlyList<string>? choices = null)
    {
        var field = new SharedConfigField(key, label, description, unit, kind, choices);
        field.PropertyChanged += OnFieldChanged;
        TimingFields.Add(field);
        section.Fields.Add(field);
        field.Normalizer = text => format(parse(_effectiveConfig.Timing, text));
        field.SameValueAsLoaded = text => string.Equals(format(parse(_effectiveConfig.Timing, text)), format(_effectiveConfig.Timing),
            StringComparison.Ordinal);
        _timingSpecs.Add(new FieldSpec<TimingConfig>(field, format, parse));
    }

    private void AddOverlay(string key, string label, string description, string unit,
        Func<OverlayConfig, string> format, Func<OverlayConfig, string, OverlayConfig> parse,
        SharedConfigFieldKind kind = SharedConfigFieldKind.Integer, IReadOnlyList<string>? choices = null)
    {
        var field = new SharedConfigField(key, label, description, unit, kind, choices);
        field.PropertyChanged += OnFieldChanged;
        OverlayFields.Add(field);
        field.Normalizer = text => format(parse(_effectiveConfig.Ui.Overlay, text));
        field.SameValueAsLoaded = text => string.Equals(format(parse(_effectiveConfig.Ui.Overlay, text)), format(_effectiveConfig.Ui.Overlay),
            StringComparison.Ordinal);
        _overlaySection.Fields.Add(field);
        _overlaySpecs.Add(new FieldSpec<OverlayConfig>(field, format, parse));
    }

    /// <summary>Wiersz razem z zamianą wartości w jedną i drugą stronę.</summary>
    private sealed record FieldSpec<T>(SharedConfigField Field, Func<T, string> Format, Func<T, string, T> Parse);

    // Formatowanie: zawsze kultura niezmienna (kropka dziesiętna), żeby plik konfiguracji wyglądał tak samo
    // niezależnie od ustawień regionalnych komputera.
    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Text(bool value) => value ? "true" : "false";
    private static string Text(OverlayPosition value) => value switch
    {
        OverlayPosition.TopLeft => "top-left",
        OverlayPosition.TopRight => "top-right",
        OverlayPosition.BottomLeft => "bottom-left",
        _ => "bottom-right",
    };

    /// <summary>Milisekundy w jednostce pola: sekundy dokładnie (3 miejsca), minuty do 4 miejsc po kropce.</summary>
    private static string Duration(int milliseconds, int scale) =>
        ((decimal)milliseconds / scale).ToString(scale == MinuteMs ? "0.####" : "0.###", CultureInfo.InvariantCulture);

    private static string SecondsList(int[] values) => string.Join("; ", values.Select(v => Duration(v, SecondMs)));

    private static int Integer(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw new FormatException("Wpisz liczbę całkowitą (same cyfry).");

    /// <summary>Przecinek i kropka działają tak samo — administrator nie ma pamiętać o kulturze pliku.</summary>
    private static double Decimal(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : throw new FormatException("Wpisz liczbę, np. 0.55 albo 0,55.");

    /// <summary>Czas w jednostce pola → pełne milisekundy (zaokrąglenie „od zera” przy połówce).</summary>
    private static int ParseDuration(string text, int scale)
    {
        string unit = scale == MinuteMs ? "minut" : "sekund";
        string example = scale == MinuteMs ? "np. 2 albo 1,5" : "np. 10 albo 0,5";
        string normalized = text.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
        {
            // Liczba poza zakresem decimal (np. „1e30”) to też „za duża”, nie „wpisz liczbę”.
            bool huge = double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDouble)
                && Math.Abs(asDouble) > (double)decimal.MaxValue / scale;
            throw new FormatException(huge ? $"Liczba {unit} jest za duża." : $"Wpisz liczbę {unit}, {example}.");
        }
        if (Math.Abs(value) > decimal.MaxValue / scale)
            throw new FormatException($"Liczba {unit} jest za duża.");
        decimal milliseconds = Math.Round(value * scale, MidpointRounding.AwayFromZero);
        if (milliseconds is < int.MinValue or > int.MaxValue)
            throw new FormatException($"Liczba {unit} jest za duża.");
        return (int)milliseconds;
    }

    /// <summary>
    /// Lista sekund. Średnik zawsze rozdziela elementy (wtedy przecinek w elemencie jest ułamkiem: „2,5; 5”);
    /// bez średnika rozdziela przecinek („2, 5, 15, 30”).
    /// </summary>
    private static int[] ParseSecondsList(string text)
    {
        char separator = text.Contains(';') ? ';' : ',';
        string[] parts = text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new FormatException("Wpisz co najmniej jedną liczbę sekund, np. 2; 5; 15; 30.");
        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            try { values[i] = ParseDuration(parts[i], SecondMs); }
            catch (FormatException ex) when (!ex.Message.Contains("za duża", StringComparison.Ordinal))
            {
                throw new FormatException("Wpisz liczby sekund oddzielone średnikiem, np. 2; 5; 15; 30.");
            }
        }
        return values;
    }

    private static bool Flag(string text) =>
        bool.TryParse(text.Trim(), out bool value) ? value
            : throw new FormatException("Zaznacz albo odznacz to ustawienie.");

    private static OverlayLayout Layout(string text) =>
        OverlayLayouts.TryParse(text, out OverlayLayout layout) ? layout
            : throw new FormatException("Wybierz jedną z wartości: " + OverlayLayouts.NamesText + ".");

    private static OverlayPosition Position(string text) => text.Trim().ToLowerInvariant() switch
    {
        "top-left" => OverlayPosition.TopLeft,
        "top-right" => OverlayPosition.TopRight,
        "bottom-left" => OverlayPosition.BottomLeft,
        "bottom-right" => OverlayPosition.BottomRight,
        _ => throw new FormatException("Wybierz jedną z wartości: top-left, top-right, bottom-left, bottom-right."),
    };
}
