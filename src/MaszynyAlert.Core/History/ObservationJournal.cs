using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Machines;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.History;

public enum ObservationState
{
    None = 0,

    /// <summary>Obserwator ma połączenie z maszyną i widzi ekran główny HMI (albo profil kontroli ekranu nie wymaga).</summary>
    Observed = 1,

    ScreenHidden = 2,

    /// <summary>Maszyna włączona, ale obserwator nie ma z nią połączenia (rozłączona, łączy się, błąd).</summary>
    NoConnection = 3,
}

/// <summary>
/// Jeden przedział czasu, w którym obserwator widział maszynę w stanie <see cref="State"/>. Czasy w UTC;
/// <see cref="End"/> to ostatnia obserwacja (dla przedziału wciąż otwartego — ostatni tik).
/// </summary>
public sealed record ObservationInterval(string MachineId, ObservationState State, DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Najwcześniejszy przyjmowany początek — chroni przed wpisem bez dat (domyślne 0001-01-01).</summary>
    private static readonly DateTimeOffset EarliestStart = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Czy wpis nadaje się do dziennika (odpowiednik <see cref="DowntimeEpisode.IsValid"/>): z sieci i z pliku
    /// może przyjść cokolwiek, a dziennik czyta się w całości przy każdym starcie programu.
    /// </summary>
    /// <param name="notAfter">Najpóźniejszy dopuszczalny koniec (wpis z sieci: teraz plus tolerancja zegarów).</param>
    public bool IsValid(DateTimeOffset? notAfter = null) =>
        !string.IsNullOrWhiteSpace(MachineId) && MachineId.Length <= 100 && MachineId != "test"
        && State is ObservationState.Observed or ObservationState.ScreenHidden or ObservationState.NoConnection
        && Start >= EarliestStart && Start <= End
        && (notAfter is not { } limit || End <= limit);

    /// <summary>
    /// Stan przedziału z migawki obserwatora; <see langword="null"/> = brak wpisu (maszyna wyłączona
    /// w konfiguracji, maszyna testowa albo obraz z pliku <c>test-ekran</c>).
    /// </summary>
    public static ObservationState? StateOf(MachineStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State == MachineConnectionState.Disabled || snapshot.MachineId == "test" || snapshot.Source == "plik")
            return null;
        if (snapshot.State != MachineConnectionState.Connected) return ObservationState.NoConnection;
        return snapshot.ScreenObservationUnavailable ? ObservationState.ScreenHidden : ObservationState.Observed;
    }
}

public sealed class ObservationJournal : Cluster.IObservationSync
{
    /// <summary>
    /// Najdłuższa przerwa między kolejnymi obserwacjami, którą uznajemy jeszcze za ciągłą obserwację. Tik
    /// orkiestratora idzie co sekundę; dłuższa przerwa (uśpienie, zawieszony proces, cofnięty zegar) to dziura:
    /// otwarty przedział kończy się na ostatniej obserwacji, a nowy zaczyna od bieżącej.
    /// </summary>
    public static readonly TimeSpan MaxObservationGap = TimeSpan.FromSeconds(10);

    /// <summary>Po tylu dopisanych wierszach (ponad liczbę scalonych przedziałów) dziennik zapisuje się od nowa scalony.</summary>
    private const int CompactAfterLines = 20_000;

    /// <summary>Opcje pliku: camelCase i stany jako tekst — plik da się przeczytać w Notatniku.</summary>
    private static readonly JsonSerializerOptions FileOptions = CreateFileOptions();

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Scalone, rozłączne i posortowane przedziały per (maszyna, stan) — łącznie z bieżącym zasięgiem otwartych.</summary>
    private readonly Dictionary<(string MachineId, ObservationState State), List<ObservationSpan>> _sets = new();

    /// <summary>Otwarte przedziały lokalnej obserwacji, po jednym na maszynę.</summary>
    private readonly Dictionary<string, Open> _open = new(StringComparer.Ordinal);

    // Niezapisane przedziały zostają do ponowienia także po usunięciu ich z _open.
    // Suma ogranicza przyrost pamięci przy wielu nieudanych zapisach tego samego otwartego przedziału.
    private readonly Dictionary<(string MachineId, ObservationState State), List<ObservationSpan>> _pending = new();

    private int _lines;

    /// <summary>
    /// Od ilu wierszy próbujemy scalić. Po nieudanym scaleniu (plik zajęty, brak prawa zapisu) kolejna próba
    /// dopiero po następnych <see cref="CompactAfterLines"/> wierszach — nie przy każdym zapisie.
    /// </summary>
    private int _compactAfter = CompactAfterLines;

    public string? Error { get; private set; }

    private const string ReadError = "Nie można odczytać dziennika czasu obserwacji. Raport jest niepełny.";
    private const string CorruptError = "Część dziennika czasu obserwacji jest uszkodzona. Raport może być niepełny.";
    private const string CompactError = "Nie można uporządkować dziennika czasu obserwacji. Dane zostają w starym pliku.";
    private const string WriteError = "Nie można zapisać dziennika czasu obserwacji. Dane po restarcie mogą być niepełne.";

    private readonly ILogger? _logger;

    /// <param name="path">Plik dziennika (w programie: <c>state\observation.jsonl</c> w katalogu danych).</param>
    /// <param name="logger">Opcjonalny log — jeden wpis przy każdej zmianie <see cref="Error"/>.</param>
    public ObservationJournal(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
        if (!File.Exists(path)) return;
        IReadOnlyList<ObservationInterval> intervals;
        try { intervals = ReadFile(out _lines); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pamięć zostaje pusta albo niepełna — scalanie i tak najpierw doczyta plik (CompactLocked),
            // więc nieudany odczyt przy starcie nie może skasować historii.
            SetError(ReadError);
            return;
        }
        foreach (var item in intervals) Add(item);
        lock (_gate)
            if (_lines > CompactAfterLines / 4 && _lines > 2 * Count()) CompactLocked();
    }

    /// <summary>
    /// Czyta cały plik: poprawne wpisy i liczbę niepustych wierszy. Uszkodzony wiersz ustawia <see cref="Error"/>
    /// i jest pomijany; błąd dostępu do pliku leci dalej (wołający decyduje, co wolno zrobić bez kompletu).
    /// </summary>
    private IReadOnlyList<ObservationInterval> ReadFile(out int lines)
    {
        var result = new List<ObservationInterval>();
        lines = 0;
        foreach (string line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lines++;
            try
            {
                var item = JsonSerializer.Deserialize<ObservationInterval>(line, FileOptions);
                if (item is not null && item.IsValid()) result.Add(item);
            }
            catch (JsonException) { SetError(CorruptError); }
        }
        return result;
    }

    /// <summary>
    /// Bieżący odczyt obserwatora (jeden tik): każda maszyna z <paramref name="current"/> przedłuża swój otwarty
    /// przedział albo zaczyna nowy (inny stan, dziura dłuższa niż <see cref="MaxObservationGap"/>), a otwarte
    /// przedziały maszyn, których w odczycie nie ma (wyłączone, usunięte z konfiguracji), domykają się na
    /// ostatniej obserwacji.
    /// </summary>
    public void Observe(IEnumerable<(string MachineId, ObservationState State)> current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            Append(); // ponów także zamknięte przedziały i dane z grupy, gdy nie ma nowych wpisów
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (machineId, state) in current)
            {
                if (!new ObservationInterval(machineId, state, now, now).IsValid() || !seen.Add(machineId)) continue;
                if (_open.TryGetValue(machineId, out var open))
                {
                    if (now < open.LastSeen || now - open.LastSeen > MaxObservationGap)
                        CloseLocked(machineId, open, open.LastSeen);
                    else if (open.State != state)
                        CloseLocked(machineId, open, now); // granica między tikami należy do poprzedniego stanu
                    else
                    {
                        open.LastSeen = now;
                        Add(new ObservationInterval(machineId, state, open.Start, now));
                        if (now - (open.PersistedEnd ?? open.Start) >= DowntimeJournal.ObservationWriteInterval)
                            Persist(machineId, open, now);
                        continue;
                    }
                }
                _open[machineId] = new Open(state, now);
            }
            foreach (var (machineId, open) in _open.Where(p => !seen.Contains(p.Key)).ToArray())
                CloseLocked(machineId, open, open.LastSeen);
        }
    }

    /// <summary>
    /// Domyka wszystkie otwarte przedziały na ich ostatniej obserwacji i zapisuje je na dysk. Wołane, gdy
    /// obserwacja ustaje: utrata roli obserwatora, zatrzymanie monitorów, wykryte uśpienie, zamknięcie programu.
    /// </summary>
    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (var (machineId, open) in _open.ToArray())
                CloseLocked(machineId, open, open.LastSeen);
            Append();
        }
    }

    /// <summary>Wszystkie scalone przedziały (z bieżącym zasięgiem otwartych), uporządkowane po maszynie, stanie i początku.</summary>
    public IReadOnlyList<ObservationInterval> Snapshot()
    {
        lock (_gate) return Flatten().OrderBy(i => i.MachineId, StringComparer.Ordinal)
            .ThenBy(i => i.State).ThenBy(i => i.Start).ToArray();
    }

    /// <inheritdoc />
    public DateTimeOffset? LatestEnd
    {
        get { lock (_gate) return _sets.Values.Where(s => s.Count > 0).Select(s => (DateTimeOffset?)s[^1].End).Max(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<ObservationInterval> IntervalsSince(DateTimeOffset since, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
            return Flatten().Where(i => i.End >= since)
                .OrderBy(i => i.End).ThenBy(i => i.MachineId, StringComparer.Ordinal).ThenBy(i => i.State)
                .ThenBy(i => i.Start).Take(limit).ToArray();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Wpis, który niczego nie dodaje do sumy, nie trafia ponownie na dysk, chyba że jego zapis wcześniej zawiódł. Nowe wpisy
    /// porcji idą do pliku jednym zapisem — porcja z sieci przychodzi na wątku odbioru UDP.
    /// </remarks>
    public bool Apply(IReadOnlyList<ObservationInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        lock (_gate)
        {
            var added = intervals.Where(i => i is not null && i.IsValid() && i.Start < i.End && Add(i)).ToArray();
            Append(added); // duplikat może ponawiać przedział przyjęty wcześniej tylko do pamięci
            CompactIfNeeded();
            return added.Length != 0;
        }
    }

    /// <summary>
    /// Zapisuje dziennik od nowa w postaci scalonej (jeden wiersz na przedział). Najpierw plik tymczasowy, potem
    /// podmiana — przerwany zapis zostawia stary plik, który dalej daje tę samą sumę.
    /// </summary>
    public void Compact()
    {
        lock (_gate) CompactLocked();
    }

    private void CloseLocked(string machineId, Open open, DateTimeOffset end)
    {
        _open.Remove(machineId);
        if (end <= open.Start) return; // jedna obserwacja to chwila, nie przedział
        open.LastSeen = end;
        Add(new ObservationInterval(machineId, open.State, open.Start, end));
        if (open.PersistedEnd != end) Persist(machineId, open, end);
    }

    private void Persist(string machineId, Open open, DateTimeOffset end)
    {
        if (Append(new ObservationInterval(machineId, open.State, open.Start, end))) open.PersistedEnd = end;
        CompactIfNeeded();
    }

    private void CompactIfNeeded()
    {
        if (_lines > _compactAfter && _lines > 2 * Count()) CompactLocked();
    }

    /// <summary>
    /// Scalenie zapisuje plik od nowa, więc najpierw DOCZYTUJE bieżący plik i dodaje go do sumy w pamięci: pamięć
    /// może nie mieć całej historii (plik zajęty albo urwany odczyt przy starcie), a przepisanie samej pamięci
    /// skasowałoby to, czego dziennik nigdy nie przeczytał. Gdy pliku nie da się przeczytać — nie scalamy wcale.
    /// </summary>
    private void CompactLocked()
    {
        string temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            if (File.Exists(_path))
                foreach (var item in ReadFile(out _)) Add(item);
            var all = Flatten().OrderBy(i => i.Start).ToArray();
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                    foreach (var interval in all) writer.Write(JsonSerializer.Serialize(interval, FileOptions) + "\n");
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            _pending.Clear(); // dopiero udana podmiana utrwala całą sumę wraz z zaległościami
            _lines = all.Length;
            _compactAfter = CompactAfterLines;
            SetError(null);
            // Scalony plik niesie już bieżący zasięg otwartych przedziałów.
            foreach (var open in _open.Values) open.PersistedEnd = open.LastSeen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetError(CompactError);
            _compactAfter = _lines + CompactAfterLines;
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private bool Append(params ObservationInterval[] intervals)
    {
        var toWrite = _pending.Count == 0 ? intervals : Flatten(_pending).Concat(intervals).ToArray();
        if (toWrite.Length == 0) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            // Jak w DowntimeJournal: wiodący znak nowej linii, żeby urwany ostatni zapis nie połknął kolejnego wiersza.
            var text = new System.Text.StringBuilder("\n");
            foreach (var interval in toWrite) text.Append(JsonSerializer.Serialize(interval, FileOptions)).Append('\n');
            File.AppendAllText(_path, text.ToString());
            _lines += toWrite.Length;
            _pending.Clear();
            if (Error == WriteError) SetError(null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var interval in intervals) AddTo(_pending, interval);
            SetError(WriteError);
            return false;
        }
    }

    /// <summary>Ustawia albo kasuje <see cref="Error"/>; do logu idzie wyłącznie ZMIANA (jedno ostrzeżenie, jedna informacja o powrocie).</summary>
    private void SetError(string? error)
    {
        if (error == Error) return;
        string? previous = Error;
        Error = error;
        if (error is not null) _logger?.LogWarning("Dziennik czasu obserwacji ({Path}): {Error}", _path, error);
        else if (previous is not null) _logger?.LogInformation("Dziennik czasu obserwacji ({Path}): zapis znów działa.", _path);
    }

    /// <summary>Dodaje przedział do sumy swojej maszyny i stanu. Zwraca <see langword="true"/>, gdy suma się powiększyła.</summary>
    private bool Add(ObservationInterval interval) => AddTo(_sets, interval);

    private static bool AddTo(Dictionary<(string MachineId, ObservationState State), List<ObservationSpan>> sets,
        ObservationInterval interval)
    {
        var key = (interval.MachineId, interval.State);
        if (!sets.TryGetValue(key, out var set)) sets[key] = set = [];
        return IntervalMath.AddTo(set, new ObservationSpan(interval.Start, interval.End));
    }

    private int Count() => _sets.Values.Sum(s => s.Count);

    private IEnumerable<ObservationInterval> Flatten() => Flatten(_sets);

    private static IEnumerable<ObservationInterval> Flatten(
        Dictionary<(string MachineId, ObservationState State), List<ObservationSpan>> sets) =>
        sets.SelectMany(p => p.Value.Select(s => new ObservationInterval(p.Key.MachineId, p.Key.State, s.Start, s.End)));

    private static JsonSerializerOptions CreateFileOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class Open(ObservationState state, DateTimeOffset start)
    {
        public ObservationState State { get; } = state;
        public DateTimeOffset Start { get; } = start;
        public DateTimeOffset LastSeen { get; set; } = start;
        /// <summary>Koniec ostatniej wersji tego przedziału dopisanej do pliku; <see langword="null"/> — jeszcze żadnej.</summary>
        public DateTimeOffset? PersistedEnd { get; set; }
    }
}

/// <summary>Przedział czasu [<see cref="Start"/>, <see cref="End"/>] na potrzeby arytmetyki zbiorów przedziałów.</summary>
internal readonly record struct ObservationSpan(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>Suma, różnica i przycięcie zbiorów przedziałów (listy rozłączne i posortowane po początku).</summary>
internal static class IntervalMath
{
    /// <summary>
    /// Dodaje przedział do posortowanej listy rozłącznych przedziałów, scalając go z nachodzącymi i stykającymi
    /// się sąsiadami. Zwraca <see langword="true"/>, gdy lista się zmieniła.
    /// </summary>
    public static bool AddTo(List<ObservationSpan> set, ObservationSpan span)
    {
        if (span.End < span.Start) return false;
        // Pierwszy przedział, który kończy się nie wcześniej niż początek nowego — tylko od niego może być styk.
        int lo = 0, hi = set.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (set[mid].End < span.Start) lo = mid + 1; else hi = mid;
        }
        int first = lo, last = lo;
        var start = span.Start;
        var end = span.End;
        while (last < set.Count && set[last].Start <= end)
        {
            if (set[last].Start <= span.Start && set[last].End >= span.End) return false; // już zawarty
            if (set[last].Start < start) start = set[last].Start;
            if (set[last].End > end) end = set[last].End;
            last++;
        }
        set.RemoveRange(first, last - first);
        set.Insert(first, new ObservationSpan(start, end));
        return true;
    }

    /// <summary>Suma dowolnych przedziałów jako lista rozłączna i posortowana.</summary>
    public static List<ObservationSpan> Union(IEnumerable<ObservationSpan> spans)
    {
        var result = new List<ObservationSpan>();
        foreach (var span in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && span.Start <= result[^1].End)
            {
                if (span.End > result[^1].End) result[^1] = result[^1] with { End = span.End };
            }
            else result.Add(span);
        }
        return result;
    }

    /// <summary>Różnica <paramref name="set"/> minus <paramref name="remove"/> (obie listy rozłączne i posortowane).</summary>
    public static List<ObservationSpan> Subtract(IReadOnlyList<ObservationSpan> set, IReadOnlyList<ObservationSpan> remove)
    {
        var result = new List<ObservationSpan>();
        int j = 0;
        foreach (var span in set)
        {
            var start = span.Start;
            while (j < remove.Count && remove[j].End <= start) j++;
            int k = j;
            while (k < remove.Count && remove[k].Start < span.End)
            {
                if (remove[k].Start > start) result.Add(new ObservationSpan(start, remove[k].Start));
                if (remove[k].End > start) start = remove[k].End;
                k++;
            }
            if (start < span.End) result.Add(new ObservationSpan(start, span.End));
        }
        return result;
    }

    /// <summary>Część przedziałów leżąca w [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public static IEnumerable<ObservationSpan> Clip(IEnumerable<ObservationSpan> set, DateTimeOffset from, DateTimeOffset to) =>
        set.Select(s => new ObservationSpan(s.Start < from ? from : s.Start, s.End > to ? to : s.End)).Where(s => s.End > s.Start);
}
