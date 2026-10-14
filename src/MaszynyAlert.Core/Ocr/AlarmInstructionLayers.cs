namespace MaszynyAlert.Core.Ocr;

public sealed class AlarmInstructionLayers
{
    private volatile Dictionary<string, string> _profile = new(StringComparer.Ordinal);
    private volatile Dictionary<string, string> _local = new(StringComparer.Ordinal);
    private volatile Dictionary<string, string> _effective = new(StringComparer.Ordinal);

    /// <summary>Instrukcje samego profilu grupy (baza), bez lokalnej nakładki.</summary>
    public IReadOnlyDictionary<string, string> Profile => _profile;

    /// <summary>Lokalna nakładka wczytana z pliku.</summary>
    public IReadOnlyDictionary<string, string> Local => _local;

    /// <summary>Wynik scalenia: profil przykryty nakładką.</summary>
    public IReadOnlyDictionary<string, string> Effective => _effective;

    public void SetProfile(IReadOnlyDictionary<string, string> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        _profile = new Dictionary<string, string>(instructions, StringComparer.Ordinal);
        Merge();
    }

    public void SetLocal(IReadOnlyDictionary<string, string> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        _local = new Dictionary<string, string>(instructions, StringComparer.Ordinal);
        Merge();
    }

    /// <summary>Klucze, dla których lokalna nakładka daje inną treść niż profil (nowe albo zmienione lokalnie).</summary>
    public IReadOnlyCollection<string> LocalOverrides => _local
        .Where(kv => !_profile.TryGetValue(kv.Key, out string? fromProfile) || !string.Equals(fromProfile, kv.Value, StringComparison.Ordinal))
        .Select(kv => kv.Key)
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToArray();

    private void Merge()
    {
        var merged = new Dictionary<string, string>(_profile, StringComparer.Ordinal);
        foreach (var entry in _local) merged[entry.Key] = entry.Value;
        _effective = merged;
    }
}
