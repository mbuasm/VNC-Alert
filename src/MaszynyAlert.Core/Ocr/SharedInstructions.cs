using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Core.Ocr;

public sealed class SharedInstructions : IInstructionSync
{
    private readonly AlarmInstructionStore _store;
    private readonly string _host;
    private readonly object _gate = new();
    private Dictionary<string, InstructionEntry> _entries = new(StringComparer.Ordinal);
    private string _hash = InstructionEntry.Hash([]);

    /// <param name="store">Plik z instrukcjami.</param>
    /// <param name="host">Nazwa tego komputera — trafia do <see cref="InstructionEntry.EditedBy"/>.</param>
    public SharedInstructions(AlarmInstructionStore store, string host)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Brak nazwy komputera.", nameof(host));
        _store = store; _host = host;
    }

    public string Path => _store.Path;

    /// <summary>Zmiana przyszła z innego komputera i została zapisana — pomoc trzeba pokazać na nowo.</summary>
    public event Action? Changed;

    public string Hash { get { lock (_gate) return _hash; } }

    public DateTimeOffset? LatestEditedAt { get { lock (_gate) return InstructionEntry.LatestEditedAt(_entries.Values); } }

    /// <summary>Wpisy razem z metadanymi (także nagrobki) — panel pokazuje z nich „ostatnią zmianę”.</summary>
    public IReadOnlyDictionary<string, InstructionEntry> Entries
    { get { lock (_gate) return new Dictionary<string, InstructionEntry>(_entries, StringComparer.Ordinal); } }

    /// <summary>Same treści, bez nagrobków — warstwa nakładki dla <see cref="AlarmInstructionLayers"/>.</summary>
    public IReadOnlyDictionary<string, string> Texts
    {
        get
        {
            lock (_gate)
            {
                var texts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, entry) in _entries) if (entry.Text is { } text) texts[key] = text;
                return texts;
            }
        }
    }

    public IReadOnlyList<InstructionEntry> AllEntries() { lock (_gate) return _entries.Values.ToArray(); }

    /// <summary>Wczytuje plik ponownie. Wyjątek odczytu zostawia poprzedni zbiór i leci do wołającego.</summary>
    public void Reload()
    {
        var entries = _store.LoadEntries();
        lock (_gate) { _entries = entries; _hash = InstructionEntry.Hash(entries.Values); }
    }

    /// <summary>
    /// Zapisuje zmianę z TEGO komputera i zwraca wpis do rozgłoszenia. Własna edycja wygrywa nad tym,
    /// co jest w pliku — także gdy zegar innego komputera był do przodu (patrz <see cref="AlarmInstructionStore.Save"/>).
    /// </summary>
    public InstructionEntry Edit(string key, string? text, DateTimeOffset now)
    {
        var saved = _store.Save(key, text, now, _host);
        Reload();
        return saved;
    }

    /// <summary>
    /// Stosuje zmiany odebrane z innego komputera. Zwraca <see langword="true"/> i zgłasza
    /// <see cref="Changed"/>, gdy plik faktycznie się zmienił.
    /// </summary>
    public bool Apply(IReadOnlyList<InstructionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        bool changed = _store.Merge(entries);
        if (!changed) return false;
        Reload();
        Changed?.Invoke();
        return true;
    }
}
