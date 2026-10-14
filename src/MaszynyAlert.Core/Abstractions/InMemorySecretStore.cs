using System.Collections.Concurrent;

namespace MaszynyAlert.Core.Abstractions;

/// <summary>Implementacja <see cref="ISecretStore"/> trzymająca sekrety tylko w pamięci procesu —
/// do testów jednostkowych i uruchamiania na Linuksie (gdzie DPAPI nie jest dostępne).</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.TryGetValue(key, out string? value) ? value : null;

    public void Set(string key, string value) => _values[key] = value;

    public bool Remove(string key) => _values.TryRemove(key, out _);

    public IReadOnlyCollection<string> Keys => _values.Keys.ToArray();
}
