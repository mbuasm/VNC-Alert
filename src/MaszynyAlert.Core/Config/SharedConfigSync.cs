using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Config;

/// <summary>
/// Wspólna konfiguracja trzymana w pamięci nad jednym plikiem (<see cref="SharedConfigStore"/>) — ta sama
/// klasa obsługuje pracę bez grupy (lokalne nadpisanie <c>config.json</c> bez UAC) i pracę w grupie
/// (replikacja). Wzorzec: <see cref="MaszynyAlert.Core.Ocr.SharedInstructions"/>.
///
/// <para>W grupie różnicę robi wyłącznie to, że zmiany <b>przychodzą też z sieci</b> (<see cref="Apply"/>)
/// i <b>wychodzą</b> po każdej edycji (<see cref="Edit"/>). Rozstrzyganie konfliktów jest deterministyczne
/// (<see cref="SharedConfigDocument.Wins"/>), więc po wymianie wiadomości każdy komputer ma ten sam
/// dokument i ten sam <see cref="Hash"/>.</para>
/// </summary>
public sealed class SharedConfigSync : ISharedConfigSync
{
    private readonly SharedConfigStore _store;
    private readonly string _host;
    private readonly object _gate = new();
    private SharedConfigDocument? _current;
    private string? _loadError;

    /// <param name="store">Plik wspólnej konfiguracji.</param>
    /// <param name="host">Nazwa tego komputera — trafia do <see cref="SharedConfigDocument.EditedBy"/>.</param>
    public SharedConfigSync(SharedConfigStore store, string host)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Brak nazwy komputera.", nameof(host));
        _store = store; _host = host;
    }

    public string Path => _store.Path;

    /// <summary>Warstwa przyszła z innego komputera i została zapisana — konfigurację trzeba przeliczyć.</summary>
    public event Action? Changed;

    /// <summary>Obowiązujący dokument albo <see langword="null"/>, gdy ten komputer nie ma nadpisania.</summary>
    public SharedConfigDocument? Current { get { lock (_gate) return _current; } }

    /// <summary>Odcisk dokumentu; <see langword="null"/>, gdy dokumentu nie ma (nie ma czego wysyłać).</summary>
    public string? Hash { get { lock (_gate) return _current?.Hash; } }

    /// <summary>Czas ostatniej zmiany; <see langword="null"/>, gdy dokumentu nie ma.</summary>
    public DateTimeOffset? EditedAt { get { lock (_gate) return _current?.EditedAt; } }

    /// <summary>
    /// Powód, dla którego ostatni <see cref="Reload"/> nie wczytał pliku (<see langword="null"/>, gdy wszystko
    /// jest w porządku). Uszkodzona warstwa nie może przewrócić startu programu — Panel pokazuje powód, a
    /// program pracuje na konfiguracji z niższej warstwy.
    /// </summary>
    public string? LoadError { get { lock (_gate) return _loadError; } }

    public void Reload()
    {
        SharedConfigDocument? document;
        try { document = _store.LoadUnderGate(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            lock (_gate) _loadError = ex.Message;
            return;
        }
        lock (_gate) { _current = document; _loadError = null; }
    }

    public SharedConfigDocument Edit(TimingConfig? timing, OverlayConfig? overlay, DateTimeOffset now,
        IReadOnlyDictionary<string, bool>? machines = null)
    {
        var saved = _store.Save(timing, overlay, now, _host, machines);
        lock (_gate) { _current = saved; _loadError = null; }
        Reload();
        return saved;
    }

    public SharedConfigDocument Approve(UpdateApproval approval, DateTimeOffset now)
    {
        var saved = _store.SaveApproval(approval, now, _host);
        lock (_gate) { _current = saved; _loadError = null; }
        Reload();
        return saved;
    }

    /// <summary>
    /// Stosuje dokument odebrany z innego komputera. Zwraca <see langword="true"/> i zgłasza
    /// <see cref="Changed"/>, gdy plik faktycznie się zmienił.
    /// </summary>
    public bool Apply(SharedConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_store.Merge(document)) return false;
        Reload();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Zdejmuje z warstwy tego komputera przełączniki maszyn zapisane poza grupą, do której właśnie przechodzi
    /// (<see cref="SharedConfigStore.DropMachines"/>: czas i autor zmiany bez zmian). Zwraca dokument sprzed zdjęcia albo
    /// <see langword="null"/>, gdy nic nie zdjęto. Nie zgłasza <see cref="Changed"/> — to nie jest zmiana z innego komputera,
    /// a konfigurację efektywną liczy wołający (zmiana ustawień grupy).
    /// </summary>
    /// <exception cref="IOException">Pliku warstwy nie da się zapisać (np. zajęty dłużej niż 2 s).</exception>
    /// <exception cref="UnauthorizedAccessException">Brak prawa zapisu do katalogu warstwy.</exception>
    /// <exception cref="InvalidDataException">Plik warstwy jest nieczytelny, a w pamięci są przełączniki, których nie da się zdjąć.</exception>
    public SharedConfigDocument? DetachMachines()
    {
        SharedConfigDocument? before;
        try { before = _store.DropMachines(); }
        catch (InvalidDataException) when (Current?.Machines is null)
        {
            // Plik nieczytelny (powód jest w LoadError), a w pamięci nie ma przełączników — nie ma czego zdejmować ani wysyłać.
            return null;
        }
        Reload();
        return before;
    }

    /// <summary>
    /// Cofa <see cref="DetachMachines"/>, gdy zmiany grupy nie udało się zatwierdzić (<see cref="SharedConfigStore.RestoreMachines"/>).
    /// </summary>
    public void RestoreMachines(SharedConfigDocument before)
    {
        _store.RestoreMachines(before);
        Reload();
    }
}
