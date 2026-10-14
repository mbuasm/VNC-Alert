using System.Text.Json;
using MaszynyAlert.Core.Abstractions;

namespace MaszynyAlert.Core.Config;

/// <summary>
/// Durable data phase of applying group settings. One journal selects a complete config/rules/help
/// generation and its separately protected key. Runtime/firewall approval must precede Commit.
/// </summary>
public sealed class GroupSettingsStore
{
    private readonly string _directory;
    private readonly ISecretStore _secrets;
    private readonly Action<string, string> _publishFile;
    private string JournalPath => Path.Combine(_directory, "journal.json");

    public GroupSettingsStore(string directory, ISecretStore secrets)
        : this(directory, secrets, PublishFile) { }

    // Inject only the final filesystem operation; serialization, flush and cleanup stay real.
    internal GroupSettingsStore(string directory, ISecretStore secrets, Action<string, string> publishFile)
    {
        _directory = Path.GetFullPath(directory);
        _secrets = secrets;
        _publishFile = publishFile;
    }

    public GroupSettingsRecovery Read()
    {
        var journal = ReadJournal();
        return new(journal.Number, journal.Current, journal.Pending,
            journal.Current is { } id ? LoadGeneration(id, journal.Number) : null);
    }
    public GroupSettingsRecovery ReadForStartup()
    {
        try
        {
            var journal = ReadJournal();
            bool renewed = false;
            var data = journal.Current is { } id
                ? LoadGeneration(id, journal.Number, requireKey: false, out renewed) : null;
            return new(journal.Number, journal.Current, journal.Pending, data, renewed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
        {
            throw new InvalidDataException("Nie można odczytać ustawień grupy komputerów. " + ex.Message, ex);
        }
    }

    /// <summary>Records intent before the first secret/file write. A failed Stage is recoverable by Abort.</summary>
    public Guid Stage(GroupSettingsProposal proposal, Guid? expectedCurrent)
    {
        var data = proposal.GetData();
        string key = proposal.NetworkKey;
        data.Validate(key);
        using var gate = AcquireWriteLock();
        var journal = ReadJournal();
        if (journal.Current != expectedCurrent || journal.Pending is not null)
            throw new InvalidOperationException("Ustawienia zmieniły się albo istnieje niedokończona operacja grupy.");
        long number = checked(journal.Number + 1);
        // Verify the previous commit before accepting a replacement. Never overwrite unreadable data.
        if (journal.Current is { } current) _ = LoadGeneration(current, journal.Number);
        var id = Guid.NewGuid();
        SaveAtomic(JournalPath, journal with { Pending = id });
        _secrets.Set(SecretKey(id), key);
        SaveAtomic(GenerationPath(id), new Generation(1, number, data));
        return id;
    }

    /// <summary>Use only after runtime and firewall steps succeed. Idempotent after an uncertain response.</summary>
    public void Commit(Guid id)
    {
        using var gate = AcquireWriteLock();
        var journal = ReadJournal();
        if (journal.Current == id && journal.Pending is null) { _ = LoadGeneration(id, journal.Number); return; }
        RequirePending(journal, id);
        _ = LoadGeneration(id, checked(journal.Number + 1));
        SaveAtomic(JournalPath, new Journal(1, journal.Number + 1, id, null));
    }

    /// <summary>Call from the writer/UAC process after restoring runtime/firewall. Never deletes legacy secrets.</summary>
    public void Abort(Guid id)
    {
        using var gate = AcquireWriteLock();
        var journal = ReadJournal();
        if (journal.Pending is null && journal.Current != id) return;
        RequirePending(journal, id);
        // Keep the pending marker until cleanup succeeds, so a crash/failure can retry it.
        _secrets.Remove(SecretKey(id));
        File.Delete(GenerationPath(id));
        SaveAtomic(JournalPath, journal with { Pending = null });
    }

    public GroupSettingsData ReadStaged(Guid id)
    {
        var journal = ReadJournal();
        RequirePending(journal, id);
        return LoadGeneration(id, checked(journal.Number + 1));
    }

    /// <summary>The selected key is retrieved from the local store, never from generation JSON.</summary>
    public string ReadCommittedNetworkKey()
    {
        var journal = ReadJournal();
        if (journal.Current is not { } id) throw new InvalidOperationException("Brak zatwierdzonej grupy.");
        _ = LoadGeneration(id, journal.Number);
        return _secrets.Get(SecretKey(id))!;
    }

    private FileStream AcquireWriteLock()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, "settings.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private Journal ReadJournal()
    {
        Journal journal;
        try { journal = ReadJson<Journal>(JournalPath); }
        catch (FileNotFoundException) { return new(1, 0, null, null); }
        catch (DirectoryNotFoundException) { return new(1, 0, null, null); }
        if (journal.Version != 1 || journal.Number < 0 || (journal.Number == 0) != (journal.Current is null)
            || journal.Current == Guid.Empty || journal.Pending == Guid.Empty
            || (journal.Pending is not null && journal.Pending == journal.Current))
            throw new InvalidDataException("Uszkodzony dziennik ustawień grupy; zapis zablokowany.");
        return journal;
    }

    private GroupSettingsData LoadGeneration(Guid id, long number, bool requireKey = true)
        => LoadGeneration(id, number, requireKey, out _);

    private GroupSettingsData LoadGeneration(Guid id, long number, bool requireKey, out bool fingerprintRenewed)
    {
        var generation = ReadJson<Generation>(GenerationPath(id));
        if (generation.Version != 1 || generation.Number != number || generation.Data is null)
            throw new InvalidDataException("Uszkodzona generacja ustawień grupy.");
        string key = requireKey ? _secrets.Get(SecretKey(id)) ?? throw new InvalidDataException("Brak lokalnego klucza generacji grupy.")
            : Convert.ToBase64String(new byte[32]); // validate public data even if DPAPI key is unavailable
        generation.Data.Validate(key);
        // Odcisk profilu bierzemy z profilu, nie z zapisanego pola: aktualizacja programu, która dokłada
        // pole do profilu, nie może unieważnić ustawień grupy zapisanych przez poprzednią wersję.
        var renewed = generation.Data.Renewed();
        fingerprintRenewed = !ReferenceEquals(renewed, generation.Data);
        return renewed;
    }

    private static void RequirePending(Journal journal, Guid id)
    {
        if (id == Guid.Empty || journal.Pending != id)
            throw new InvalidOperationException("Operacja nie dotyczy oczekującej generacji grupy.");
    }

    private string GenerationPath(Guid id) => Path.Combine(_directory, $"generation-{id:N}.json");
    private static string SecretKey(Guid id) => $"cluster:generation:{id:N}";

    private static T ReadJson<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 2 * GroupFileCodec.MaxFileBytes) throw new InvalidDataException("Zbyt duży plik ustawień grupy.");
        try
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();
            GroupFileCodec.RejectDuplicateProperties(bytes);
            return JsonSerializer.Deserialize<T>(bytes, GroupFileCodec.Options) ?? throw new InvalidDataException("Pusty plik ustawień grupy.");
        }
        catch (JsonException) { throw new InvalidDataException("Uszkodzony plik ustawień grupy; zapis zablokowany."); }
    }

    private void SaveAtomic<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, GroupFileCodec.Options);
        if (bytes.Length > 2 * GroupFileCodec.MaxFileBytes) throw new InvalidDataException("Zbyt duży plik ustawień grupy.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            _publishFile(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    private static void PublishFile(string temporary, string path)
    {
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private sealed record Journal(int Version, long Number, Guid? Current, Guid? Pending);
    private sealed record Generation(int Version, long Number, GroupSettingsData Data);
}

public sealed record GroupSettingsRecovery(long Number, Guid? CommittedId, Guid? InterruptedId, GroupSettingsData? Data,
    bool ProfileFingerprintRenewed = false);
