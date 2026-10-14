namespace MaszynyAlert.Core.Abstractions;

/// <summary>
/// Miejsce przechowywania sekretów (hasła VNC, klucz sieciowy klastra). Implementacja na Windows
/// (<c>DpapiSecretStore</c>) szyfruje dane mechanizmem DPAPI — Core zna tylko ten interfejs.
/// </summary>
public interface ISecretStore
{
    /// <summary>Zwraca wartość sekretu albo null, gdy nie ma go pod tym kluczem.</summary>
    string? Get(string key);

    /// <summary>Zapisuje (nadpisuje) wartość sekretu pod danym kluczem.</summary>
    void Set(string key, string value);

    /// <summary>Usuwa sekret. Zwraca true, gdy istniał i został usunięty.</summary>
    bool Remove(string key);

    /// <summary>Klucze wszystkich zapisanych sekretów.</summary>
    IReadOnlyCollection<string> Keys { get; }
}
