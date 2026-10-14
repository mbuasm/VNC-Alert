using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaszynyAlert.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Windows;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] DefaultEntropy = SHA256.HashData(Encoding.UTF8.GetBytes("MaszynyAlert.v1"));

    private readonly string _filePath;
    private readonly DataProtectionScope _scope;
    private readonly byte[] _entropy;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>Klucz → base64(ProtectedData.Protect(UTF8(wartość))). Wczytywane leniwie z <see cref="_filePath"/>.</summary>
    private Dictionary<string, string>? _cache;
    private bool _loaded;

    public DpapiSecretStore(string filePath, DataProtectionScope scope, byte[]? entropy = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        _filePath = filePath;
        _scope = scope;
        _entropy = entropy ?? DefaultEntropy;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Sklep sekretów wersji portable — DPAPI CurrentUser, bez uprawnień administratora.</summary>
    public static DpapiSecretStore ForCurrentUser(string appDataDir, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(appDataDir);
        return new DpapiSecretStore(Path.Combine(appDataDir, "secrets.dat"), DataProtectionScope.CurrentUser, logger: logger);
    }

    /// <summary>Sklep sekretów wersji zainstalowanej — DPAPI LocalMachine, wspólny dla każdego konta operatora na komputerze.</summary>
    public static DpapiSecretStore ForMachine(string programDataDir, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(programDataDir);
        return new DpapiSecretStore(Path.Combine(programDataDir, "secrets.dat"), DataProtectionScope.LocalMachine, logger: logger);
    }

    /// <inheritdoc />
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            EnsureLoaded();
            if (!_cache!.TryGetValue(key, out string? protectedBase64))
            {
                return null;
            }

            try
            {
                byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, _entropy, _scope);
                try { return Encoding.UTF8.GetString(plainBytes); }
                finally { CryptographicOperations.ZeroMemory(plainBytes); }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                _logger.LogWarning(
                    ex, "Nie udało się odszyfrować sekretu pod kluczem \"{Key}\" w pliku \"{Path}\" — pomijam ten wpis.", key, _filePath);
                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_lock)
        {
            using var gate = AcquireWriteGate();
            var next = ReadSnapshot();
            byte[] plainBytes = Encoding.UTF8.GetBytes(value);
            try
            {
                next[key] = Convert.ToBase64String(ProtectedData.Protect(plainBytes, _entropy, _scope));
            }
            finally { CryptographicOperations.ZeroMemory(plainBytes); }
            Save(next);
            _cache = next;
            _loaded = true;
        }
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            using var gate = AcquireWriteGate();
            var next = ReadSnapshot();
            if (!next.Remove(key))
            {
                return false;
            }

            Save(next);
            _cache = next;
            _loaded = true;
            return true;
        }
    }

    /// <summary>Ile czekać na zwolnienie blokady zapisu przez inny proces (edytor UAC, helper grupy).</summary>
    internal TimeSpan WriteLockTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Blokada międzyprocesowa cyklu odczyt–scalenie–zapis. Bez niej dwa procesy piszące różne klucze
    /// (np. edytor UAC i helper grupy) nadpisywały sobie nawzajem wpisy. Plik <c>secrets.lock</c> nigdy nie jest
    /// usuwany; sam <c>secrets.dat</c> nie może być blokowany, bo czytelnicy muszą go otwierać w każdej chwili.</summary>
    private FileStream AcquireWriteGate()
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string lockPath = Path.Combine(dir ?? string.Empty, "secrets.lock");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1); }
            catch (IOException) when (waited.Elapsed < WriteLockTimeout) { Thread.Sleep(20); }
            catch (IOException ex)
            {
                throw new IOException("Magazyn sekretów jest zajęty przez inny proces; zapis nie został wykonany.", ex);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _cache!.Keys.ToArray();
            }
        }
    }

    /// <summary>Wczytuje zapis z osobnego edytora UAC; błąd zachowuje poprzedni cache.</summary>
    public void Reload()
    {
        lock (_lock)
        {
            var snapshot = ReadSnapshot();
            _cache = snapshot;
            _loaded = true;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            _cache = ReadSnapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning("Nie udało się odczytać pliku sekretów \"{Path}\" ({ErrorType}) — hasła niedostępne. Zapis wymaga poprawnego odczytu pliku.",
                _filePath, ex.GetType().Name);
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        _loaded = true;
    }

    private Dictionary<string, string> ReadSnapshot()
    {
        try
        {
            // File.Exists ukrywa błędy dostępu. Tylko rzeczywisty brak pliku oznacza pusty magazyn.
            string json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null || parsed.Values.Any(value => value is null))
                throw new InvalidDataException("Nieprawidłowy plik sekretów — zapis zablokowany, aby zachować istniejące dane.");
            return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new(StringComparer.Ordinal); }
        catch (JsonException)
        {
            throw new InvalidDataException("Uszkodzony plik sekretów — zapis zablokowany, aby zachować istniejące dane.");
        }
    }

    private void Save(Dictionary<string, string> next)
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            // Replace zachowuje ACL istniejącego magazynu. Nie stosujemy zastępczego Delete+Move.
            if (File.Exists(_filePath)) File.Replace(tmpPath, _filePath, destinationBackupFileName: null);
            else File.Move(tmpPath, _filePath);
        }
        finally
        {
            try { File.Delete(tmpPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Nie udało się usunąć tymczasowego pliku sekretów ({ErrorType}).", ex.GetType().Name);
            }
        }
    }
}
