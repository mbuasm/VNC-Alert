using System.IO;
using System.Diagnostics;
using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Services;

public sealed class AlarmHelpService
{
    private readonly SharedInstructions _localInstructions;
    private readonly SharedInstructions _groupInstructions;
    private readonly AlarmInstructionLayers _layers = new();
    private readonly IClock _clock;
    private readonly bool _elevate;
    private bool _group;

    public event Action? Changed;

    /// <summary>Zmiana zapisana na TYM komputerze, do rozgłoszenia w grupie.</summary>
    public event Action<InstructionEntry>? LocalEdit;

    public string? LoadError { get; private set; }
    public RuleSet Rules { get; private set; }

    /// <summary>Czy instrukcje są wspólne dla grupy komputerów (wtedy nie ma osobnej warstwy lokalnej).</summary>
    public bool GroupMode => _group;

    /// <summary>Wspólny zbiór grupy — to jego pilnuje <c>ClusterRuntime</c>, także zanim grupa ruszy.</summary>
    public SharedInstructions GroupInstructions => _groupInstructions;

    public AlarmHelpService(RuleSet rules, string path, bool elevate, string? groupPath = null, IClock? clock = null)
    {
        Rules = rules;
        _clock = clock ?? SystemClock.Instance;
        _elevate = elevate;
        _localInstructions = new SharedInstructions(new AlarmInstructionStore(path), Environment.MachineName);
        // Wspólny plik grupy leży w WŁASNYM katalogu: zapis atomowy tworzy obok pliki .lock/.tmp/.bak,
        // więc zapisywalny dla użytkownika musi być cały katalog, a nie tylko jeden plik.
        _groupInstructions = new SharedInstructions(new AlarmInstructionStore(
            groupPath ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!,
                "group-instructions", "group-instructions.json")), Environment.MachineName);
        // Zmiana przyjęta z sieci: pomoc, pop-upy i panel mają pokazać nową treść bez restartu.
        _groupInstructions.Changed += () => { Reload(); Changed?.Invoke(); };
        Reload();
    }

    private SharedInstructions Active => _group ? _groupInstructions : _localInstructions;

    /// <summary>Podmienia bazę (reguły i instrukcje profilu). Nakładka z pliku pozostaje ponad nią.</summary>
    public void UseProfile(GroupProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Rules = profile.Rules;
        _layers.SetProfile(profile.Instructions);
        Changed?.Invoke();
    }

    /// <summary>
    /// Włącza albo wyłącza wspólne instrukcje grupy. Wołane przy starcie i po każdej zmianie ustawień
    /// grupy, żeby edytor i pomoc od razu korzystały z właściwego pliku.
    /// </summary>
    public void UseGroup(bool group)
    {
        if (_group == group) return;
        _group = group;
        Reload();
        Changed?.Invoke();
    }

    /// <summary>Wczytuje ponownie nakładkę; błąd odczytu zostawia poprzednią nakładkę i profil bez zmian.</summary>
    public void Reload()
    {
        try
        {
            // Oba pliki, nie tylko czynny: wspólny zbiór musi mieć poprawny odcisk także wtedy, gdy grupa
            // dopiero się włączy, a lokalny — gdy grupa zostanie wyłączona.
            _localInstructions.Reload();
            _groupInstructions.Reload();
            _layers.SetLocal(Active.Texts);
            LoadError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        { LoadError = "Nie można wczytać własnych instrukcji: " + ex.Message; }
    }

    public string InstructionsFor(Alert alert) => _layers.Effective.GetValueOrDefault(AlarmHelp.InstructionKey(alert))
        ?? AlarmHelp.InstructionsFor(alert);

    /// <summary>Treść bez nakładki: z profilu grupy, a gdy profil jej nie ma — wbudowana. To „domyślna” treść edytora.</summary>
    public string BaseInstructionsFor(Alert alert) => _layers.Profile.GetValueOrDefault(AlarmHelp.InstructionKey(alert))
        ?? AlarmHelp.InstructionsFor(alert);

    public string For(Alert alert) => AlarmHelp.Format(alert, InstructionsFor(alert));

    /// <summary>Klucze z własną treścią (profil lub nakładka).</summary>
    public IEnumerable<string> SavedKeys => _layers.Effective.Keys;

    /// <summary>Efektywne instrukcje (profil przykryty nakładką) — do eksportu profilu grupy.</summary>
    public IReadOnlyDictionary<string, string> Snapshot => new Dictionary<string, string>(_layers.Effective, StringComparer.Ordinal);

    /// <summary>Instrukcje samego profilu, bez nakładki — do odtworzenia poprzedniego profilu przy rollbacku.</summary>
    public IReadOnlyDictionary<string, string> ProfileInstructions => new Dictionary<string, string>(_layers.Profile, StringComparer.Ordinal);

    /// <summary>
    /// Klucze, których nakładka różni się od profilu. W grupie zawsze pusto: nie ma warstwy lokalnej,
    /// więc nie ma czego zgłaszać jako rozbieżność.
    /// </summary>
    public IReadOnlyCollection<string> LocalOverrides => _group ? [] : _layers.LocalOverrides;

    /// <summary>Kto i kiedy zmienił instrukcję po raz ostatni; <see langword="null"/>, gdy nikt jej nie ruszał.</summary>
    public InstructionEntry? LastChange(string key) => Active.Entries.GetValueOrDefault(key);

    public async Task SaveAsync(string key, string? text)
    {
        AlarmInstructionStore.Validate(key, text);
        InstructionEntry? saved;
        if (_group)
        {
            // Wspólny plik grupy jest zapisywalny dla użytkownika, więc edycja nie prosi o UAC —
            // dokładnie tak samo, jak zapis zmiany odebranej z innego komputera.
            saved = _groupInstructions.Edit(key, text, _clock.UtcNow);
        }
        else if (_elevate)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppPaths.ExeDir };
            start.ArgumentList.Add("--save-alarm-instruction");
            start.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                new AlarmInstructionStore.Change(key, text, _clock.UtcNow, Environment.MachineName),
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
            using var process = Process.Start(start) ?? throw new IOException("Nie uruchomiono zapisu instrukcji.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("Instrukcja nie została zapisana. Poprzednia treść pozostaje bez zmian.");
            saved = null;
        }
        else saved = _localInstructions.Edit(key, text, _clock.UtcNow);

        Reload();
        if (LoadError is not null) throw new IOException(LoadError);
        Changed?.Invoke();
        if (saved is not null && _group) LocalEdit?.Invoke(saved);
    }

    // Only this fixed data file can be written by the elevated helper. No host, OCR or VNC is started.
    internal static int SaveElevated(string payload)
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!AppPaths.IsInstalled || !new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return 1;
            if (payload.Length > 32000) throw new ArgumentException("Instrukcja jest zbyt długa.");
            var change = JsonSerializer.Deserialize<AlarmInstructionStore.Change>(Convert.FromBase64String(payload))
                ?? throw new ArgumentException("Brak treści do zapisania.");
            new AlarmInstructionStore(Path.Combine(AppPaths.DataDir, "alarm-instructions.json"))
                .Save(change.Key, change.Text, change.EditedAt, change.EditedBy);
            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Nie udało się zapisać instrukcji.\n\n" + ex.Message,
                "VNC Alert", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }
}
