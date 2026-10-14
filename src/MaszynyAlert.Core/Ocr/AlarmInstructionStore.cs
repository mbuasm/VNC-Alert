using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaszynyAlert.Core.Ocr;

public sealed class AlarmInstructionStore(string path)
{
    public const int MaxTextLength = 4000;

    /// <summary>Tyle samo wpisów, ile dopuszcza profil grupy (<c>GroupFileCodec.ValidateProfile</c>).</summary>
    public const int MaxEntries = 2048;

    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int CurrentVersion = 2;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Wpisy razem z metadanymi, także nagrobki. Brak pliku to pusty zbiór, nie błąd.</summary>
    public Dictionary<string, InstructionEntry> LoadEntries()
    {
        if (!File.Exists(Path)) return new(StringComparer.Ordinal);
        byte[] bytes;
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaxFileBytes) throw new InvalidDataException("Plik instrukcji jest zbyt duży.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        // Numer formatu czytamy osobno: plik w formacie 1 ma pod tym samym polem zwykłe napisy,
        // więc deserializacja od razu do formatu 2 rzucałaby wyjątkiem zamiast wykonać migrację.
        var probe = JsonSerializer.Deserialize<VersionProbe>(bytes, CaseInsensitive)
            ?? throw new InvalidDataException("Plik instrukcji jest pusty.");
        return probe.Version switch
        {
            1 => ReadVersion1(bytes),
            CurrentVersion => ReadVersion2(JsonSerializer.Deserialize<Document>(bytes, CaseInsensitive)
                ?? throw new InvalidDataException("Plik instrukcji jest pusty.")),
            _ => throw new InvalidDataException("Nieobsługiwany format instrukcji."),
        };
    }

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private Dictionary<string, InstructionEntry> ReadVersion1(byte[] bytes)
    {
        var legacy = JsonSerializer.Deserialize<LegacyDocument>(bytes, CaseInsensitive)
            ?? throw new InvalidDataException("Plik instrukcji jest pusty.");
        if (legacy.Instructions is null) throw new InvalidDataException("Nieobsługiwany format instrukcji.");
        // Format 1 nie zna czasu zmiany. Czas modyfikacji pliku jest najbliższą prawdą, jaką mamy, a nazwa
        // tego komputera mówi, skąd treść pochodzi — obie wartości są potrzebne dopiero w grupie.
        var editedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(Path), TimeSpan.Zero);
        string editedBy = Environment.MachineName;
        var entries = new Dictionary<string, InstructionEntry>(StringComparer.Ordinal);
        foreach (var entry in legacy.Instructions)
        {
            if (entry.Value is null) throw new InvalidDataException("Brak treści zapisanej instrukcji.");
            Validate(entry.Key, entry.Value);
            entries[entry.Key] = new InstructionEntry
            { Key = entry.Key, Text = entry.Value, EditedAt = editedAt, EditedBy = editedBy };
        }
        return entries;
    }

    private static Dictionary<string, InstructionEntry> ReadVersion2(Document document)
    {
        if (document.Instructions is null) throw new InvalidDataException("Nieobsługiwany format instrukcji.");
        var entries = new Dictionary<string, InstructionEntry>(StringComparer.Ordinal);
        foreach (var (key, stored) in document.Instructions)
        {
            if (stored is null) throw new InvalidDataException("Brak treści zapisanej instrukcji.");
            // Nagrobek rozpoznajemy po braku treści; pole "deleted" jest zapisywane dla czytelności pliku.
            string? text = stored.Deleted ? null : stored.Text;
            Validate(key, text);
            var entry = new InstructionEntry
            {
                Key = key, Text = text, EditedAt = stored.EditedAt,
                EditedBy = string.IsNullOrWhiteSpace(stored.EditedBy) ? "?" : stored.EditedBy,
            };
            if (!entry.IsValid()) throw new InvalidDataException("Nieprawidłowy wpis instrukcji.");
            entries[key] = entry;
        }
        return entries;
    }

    /// <summary>Efektywne treści bez nagrobków — warstwa, którą widzi <see cref="AlarmInstructionLayers"/>.</summary>
    public Dictionary<string, string> Load()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in LoadEntries()) if (entry.Text is { } text) texts[key] = text;
        return texts;
    }

    /// <summary>Zapis pojedynczej zmiany z tego komputera. Zwraca wpis dokładnie taki, jaki trafił do pliku.</summary>
    public InstructionEntry Save(string key, string? text, DateTimeOffset editedAt, string editedBy)
    {
        Validate(key, text);
        InstructionEntry? saved = null;
        Write(current =>
        {
            // Zmiana z tego komputera ma wygrać także wtedy, gdy zegar partnera był do przodu, a jego wpis
            // zdążył tu dotrzeć: inaczej edycja „nie brałaby się”, bo stara treść wróciłaby z sieci.
            var at = editedAt;
            if (current.TryGetValue(key, out var previous) && previous.EditedAt >= at)
                at = previous.EditedAt.AddTicks(1);
            saved = new InstructionEntry { Key = key, Text = text, EditedAt = at, EditedBy = editedBy };
            if (!saved.IsValid()) throw new ArgumentException("Nieprawidłowy wpis instrukcji.");
            return [saved];
        }, force: true);
        return saved!;
    }

    /// <summary>
    /// Scala wpisy odebrane z innego komputera regułą „nowszy wygrywa”. Zwraca <see langword="true"/>,
    /// gdy plik faktycznie się zmienił (wtedy pomoc trzeba przeładować).
    /// </summary>
    public bool Merge(IReadOnlyList<InstructionEntry> incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return incoming.Count != 0 && Write(_ => incoming, force: false);
    }

    /// <param name="prepare">Wpisy do zastosowania, policzone już pod zamkiem na podstawie stanu pliku.</param>
    /// <param name="force">Zapis własnej edycji: pomija regułę „nowszy wygrywa”.</param>
    private bool Write(Func<Dictionary<string, InstructionEntry>, IReadOnlyList<InstructionEntry>> prepare, bool force)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // Jeden plik-zamek na proces edytora i proces odbierający zmiany z sieci: czytaj → scal → zapisz.
        using var gate = new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = LoadEntries();
        var incoming = prepare(entries);
        bool changed = false;
        foreach (var entry in incoming)
        {
            if (!entry.IsValid()) continue;
            if (entries.TryGetValue(entry.Key, out var current))
            {
                if (!force && !InstructionEntry.Wins(entry, current)) continue;
                if (current == entry) continue;
            }
            entries[entry.Key] = entry;
            changed = true;
        }
        if (!changed) return false;
        Prune(entries);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new Document(CurrentVersion, entries.ToDictionary(p => p.Key,
                p => new StoredEntry(p.Value.Text, p.Value.EditedAt, p.Value.EditedBy, p.Value.Deleted), StringComparer.Ordinal)),
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never });
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Plik instrukcji jest zbyt duży.");
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak");
            else File.Move(temporary, Path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    /// <summary>
    /// Nagrobki nie mogą rosnąć bez końca. Powyżej <see cref="MaxEntries"/> znikają najstarsze z nich;
    /// samych treści nigdy nie kasujemy po cichu — wtedy zapis się nie uda i operator dostanie komunikat.
    /// </summary>
    private static void Prune(Dictionary<string, InstructionEntry> entries)
    {
        if (entries.Count <= MaxEntries) return;
        foreach (string key in entries.Values.Where(e => e.Deleted).OrderBy(e => e.EditedAt)
                     .Take(entries.Count - MaxEntries).Select(e => e.Key).ToArray())
            entries.Remove(key);
        if (entries.Count > MaxEntries)
            throw new InvalidDataException($"Instrukcji jest więcej niż {MaxEntries}; usuń część własnych treści.");
    }

    public static void Validate(string key, string? text)
    {
        if (!IsValidKey(key)) throw new ArgumentException("Nieprawidłowy identyfikator instrukcji.");
        if (text is not null && (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength))
            throw new ArgumentException($"Instrukcja musi mieć od 1 do {MaxTextLength} znaków. Aby usunąć własną treść, użyj przywrócenia domyślnej.");
    }

    public static bool IsValidKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 200
        && (key == "offline" || key.StartsWith("code:", StringComparison.Ordinal)
            || key.StartsWith("rule:", StringComparison.Ordinal) || key.StartsWith("category:", StringComparison.Ordinal))
        && !key.Any(char.IsControl);

    private sealed record VersionProbe(int Version);
    private sealed record Document(int Version, Dictionary<string, StoredEntry>? Instructions);
    private sealed record StoredEntry(string? Text, DateTimeOffset EditedAt, string? EditedBy, bool Deleted);
    private sealed record LegacyDocument(int Version, Dictionary<string, string?>? Instructions);

    /// <summary>Zmiana przekazywana procesowi z UAC (tryb bez grupy) — razem z metadanymi wpisu.</summary>
    public sealed record Change(string Key, string? Text, DateTimeOffset EditedAt, string EditedBy);
}
