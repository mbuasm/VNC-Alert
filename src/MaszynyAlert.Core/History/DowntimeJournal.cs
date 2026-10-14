using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.Core.History;

public sealed record DowntimeEpisode(string MachineId, string MachineName, string Reason,
    DateTimeOffset StartedAt, DateTimeOffset LastObservedAt, DateTimeOffset? EndedAt, bool Interrupted = false,
    Guid EpisodeId = default, string? Origin = null,
    // Bez pola w pliku i w komunikacie, gdy kategorii nie ma.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category = null)
{
    /// <summary>Deterministyczny identyfikator dla wiersza dziennika zapisanego przed dodaniem <see cref="EpisodeId"/>.</summary>
    public static Guid LegacyId(string machineId, DateTimeOffset startedAt)
    {
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(Encoding.UTF8.GetBytes(machineId + "|" + startedAt.ToUnixTimeMilliseconds()), hash);
        return new Guid(hash[..16]);
    }

    /// <summary>
    /// Koniec epizodu na potrzeby raportu: prawdziwe albo wymuszone zamknięcie, a dla wciąż otwartego —
    /// ostatnia obserwacja. Ta sama reguła, co w <see cref="DowntimeReport"/>.
    /// </summary>
    public DateTimeOffset End => EndedAt ?? LastObservedAt;

    /// <summary>
    /// Wpis dziennika z alertu — JEDNA reguła dla obserwacji własnej (<see cref="DowntimeJournal.Record"/>) i alertu z sieci
    /// (<c>AppOrchestrator.OnRemoteEpisodesReceived</c>). Koniec zamkniętego epizodu = ostatnia obserwacja
    /// (<c>Alert.LastSeenAt</c>), bo <c>ClosedAt</c> niesie opóźnienie potwierdzenia naprawy.
    /// </summary>
    public static DowntimeEpisode FromAlert(Alert alert, string? origin)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new DowntimeEpisode(alert.MachineId, alert.MachineName, alert.ShortText,
            alert.StartedAt, alert.LastSeenAt, alert.Status == AlertStatus.Closed ? alert.LastSeenAt : null,
            EpisodeId: alert.EpisodeId, Origin: origin, Category: DowntimeCategories.Normalize(alert.Category));
    }

    /// <summary>Najdłuższy przyjmowany tekst pojedynczego pola wpisu z sieci (odczyt OCR paska statusu HMI).</summary>
    private const int MaxTextLength = 500;

    /// <summary>Najdłuższa przyjmowana kategoria wpisu z sieci (kategorie słownika to pojedyncze słowa).</summary>
    private const int MaxCategoryLength = 100;

    /// <summary>
    /// Czy wpis nadaje się do przyjęcia z sieci. Odpowiednik
    /// <c>InstructionEntry.IsValid</c>: nadawca może przysłać cokolwiek, a dziennik jest plikiem czytanym
    /// przy każdym starcie programu — wpis bez tożsamości, bez maszyny, z czasem końca przed początkiem
    /// albo z tekstem na kilobajty nie ma prawa tam trafić.
    /// </summary>
    public bool IsValid() =>
        EpisodeId != Guid.Empty
        && !string.IsNullOrWhiteSpace(MachineId) && MachineId.Length <= 100
        && MachineName is not null && MachineName.Length <= MaxTextLength
        && Reason is not null && Reason.Length <= MaxTextLength
        // Brak kategorii jest poprawny (starsza wersja w grupie jej nie zna); za długa — nie.
        && (Category is null || Category.Length <= MaxCategoryLength)
        && StartedAt <= LastObservedAt
        && (EndedAt is not { } end || end >= StartedAt);
}

/// <summary>
/// Durable episode snapshots; ACK and changes of alarm code never split an episode.
/// klucz wpisu to <see cref="DowntimeEpisode.EpisodeId"/> (nie
/// <c>(MachineId, StartedAt)</c>), żeby ten sam epizod dało się scalić z obserwacją innego komputera grupy
/// (<see cref="Merge"/>) zamiast go po prostu odrzucać.
/// </summary>
public sealed class DowntimeJournal : Cluster.IDowntimeSync
{
    /// <summary>
    /// Najmniejszy przyrost <see cref="DowntimeEpisode.LastObservedAt"/>, przy którym OTWARTY epizod
    /// dopisuje się do pliku. Replikacja na żywo dostarcza ten sam epizod
    /// CO SEKUNDĘ — odbiorcy z każdego <c>StateMessage</c>, obserwatorowi z każdego <c>SetLocalState</c>
    /// — a czas ostatniej obserwacji przesuwa się przy każdym odczycie ekranu. Bez tego progu
    /// <c>downtime.jsonl</c> rósłby o wiersz na sekundę na każdy otwarty błąd, na KAŻDYM komputerze
    /// grupy (dziesiątki tysięcy wierszy na dobę) — plik, który przy starcie czyta się w całości.
    /// Próg dotyczy wyłącznie zapisu: w pamięci, a więc i w raporcie, epizod jest zawsze aktualny,
    /// a każda zmiana INNA niż sam czas obserwacji (początek, koniec, tekst, komputer) zapisuje się
    /// natychmiast. Czyste zamknięcie programu domyka to przez <see cref="Flush"/>.
    /// </summary>
    public static readonly TimeSpan ObservationWriteInterval = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DowntimeEpisode> _episodes = new();
    /// <summary>Ostatnia wersja epizodu ZAPISANA do pliku — podstawa progu <see cref="ObservationWriteInterval"/>.</summary>
    private readonly Dictionary<Guid, DowntimeEpisode> _persisted = new();
    // Nie wolno utrwalić nowej migawki, dopóki nie znamy całej zapisanej podstawy scalenia.
    private bool _readPending;
    private long _version;
    public string? Error { get; private set; }

    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public DowntimeJournal(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        _readPending = true;
        TryReadLocked();
    }

    /// <summary>
    /// Odczyt jest pełny albo nie zmienia pamięci. Po odzyskaniu pliku scalamy z nim obserwacje przyjęte
    /// w czasie awarii; otwarte dane bieżącego procesu nie dostają wymuszonego zamknięcia przy restarcie.
    /// </summary>
    private bool TryReadLocked()
    {
        try
        {
            var restored = new Dictionary<Guid, DowntimeEpisode>();
            bool corrupt = false;
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<DowntimeEpisode>(line);
                    if (item is null) continue;
                    if (item.EpisodeId == Guid.Empty)
                        item = item with { EpisodeId = DowntimeEpisode.LegacyId(item.MachineId, item.StartedAt) };
                    // Pusta/biała albo „bez alarmu”/„nie błąd” kategoria w pliku (ręczna zmiana, obcy wpis) = nieznana.
                    if (DowntimeCategories.Normalize(item.Category) != item.Category)
                        item = item with { Category = DowntimeCategories.Normalize(item.Category) };
                    restored[item.EpisodeId] = item;
                }
                catch (JsonException) { corrupt = true; }
            }
            // No observations exist while the app was off. Never count that gap as downtime.
            // Zostaje to w pamięci i NIE trafia do _persisted: dopóki nic więcej się nie zmieni, plik
            // słusznie niesie wpis otwarty — a gdy epizod trwa dalej na innym komputerze grupy,
            // MergeEnd odwoła to wymuszone zamknięcie zamiast zamrozić epizod na chwili restartu.
            var pending = _episodes.Values.ToArray();
            _episodes.Clear();
            _persisted.Clear();
            foreach (var (key, item) in restored)
            {
                _persisted[key] = item;
                _episodes[key] = item.EndedAt is null
                    ? item with { EndedAt = item.LastObservedAt, Interrupted = true } : item;
            }
            _version++;
            _readPending = false;
            Error = corrupt ? "Część historii jest uszkodzona. Raport może być niepełny." : null;
            foreach (var item in pending) MergeLocked(item);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "Nie można odczytać historii postojów. Raport jest niepełny.";
            return false;
        }
    }

    /// <summary>Zapisuje lokalną obserwację (<paramref name="origin"/> domyślnie ten komputer). Cienka nakładka na <see cref="Merge"/>.</summary>
    public bool Record(Alert alert, string? origin = null)
    {
        if (!IsRecordedKind(alert.Kind) || alert.MachineId == "test") return false;
        // ClosedAt includes the detector's confirmation delay. Use the last observation.
        return Merge(DowntimeEpisode.FromAlert(alert, origin ?? Environment.MachineName));
    }

    public static bool IsRecordedKind(AlertKind kind) => kind is AlertKind.Error or AlertKind.MachineIdle;

    /// <summary>
    /// Domyka epizod znanym zamknięciem z sieci (<c>EpisodeClosure</c>). Bez
    /// wcześniej znanego wpisu (ten komputer nigdy nie widział epizodu aktywnego) nic nie da się scalić —
    /// tę lukę domyka dopiero uzupełnianie po włączeniu komputera.
    /// </summary>
    /// <param name="closedAt">
    /// <c>EpisodeClosure.At</c>, czyli <c>Alert.ClosedAt</c> obserwatora. Niesie opóźnienie potwierdzenia
    /// naprawy (<c>episodeCloseAfterMs</c>, domyślnie 30 s) — dokładnie to, czego <see cref="Record"/>
    /// unika, biorąc <c>LastSeenAt</c>. Zamknięcie z sieci mówi więc tylko „epizod naprawdę się skończył”:
    /// koniec bierzemy z ostatniej czerwonej obserwacji, a znacznika zamknięcia używamy dopiero wtedy, gdy
    /// jest od niej WCZEŚNIEJSZY. Inaczej raport tej samej maszyny na komputerze bez obserwacji byłby
    /// dłuższy niż u obserwatora, a wspólny raport ma pokazywać wszędzie to samo.
    /// </param>
    public bool MergeClosure(Guid episodeId, DateTimeOffset closedAt, string? origin = null)
    {
        lock (_gate)
        {
            if (_readPending) TryReadLocked();
            if (!_episodes.TryGetValue(episodeId, out var existing)) return false;
            var end = existing.LastObservedAt < closedAt ? existing.LastObservedAt : closedAt;
            return MergeLocked(existing with
            {
                EndedAt = end, Interrupted = false, Origin = origin ?? existing.Origin,
            });
        }
    }

    /// <summary>
    /// Scala przychodzący wpis z zapamiętanym pod tym samym <see cref="DowntimeEpisode.EpisodeId"/>: <c>StartedAt</c> = wcześniejszy, <c>LastObservedAt</c> = późniejszy, koniec według
    /// <see cref="MergeEnd"/>, a <c>Reason</c>/<c>MachineName</c>/<c>Origin</c> z wpisu o najnowszym
    /// <c>LastObservedAt</c> (najlepszy dostępny odczyt OCR), <c>Category</c> razem z tekstem, ale nigdy pusta zamiast
    /// znanej (<see cref="DowntimeEpisode.Category"/>). Zapis na dysk następuje tylko, gdy scalenie coś
    /// zmieniło (plik zostaje append-only, ostatnia wersja wygrywa przy odczycie) — i nie częściej dla samego
    /// czasu obserwacji niż <see cref="ObservationWriteInterval"/>.
    /// </summary>
    /// <returns><see langword="true"/>, gdy scalenie zmieniło stan epizodu w pamięci.</returns>
    public bool Merge(DowntimeEpisode incoming)
    {
        if (incoming is null || incoming.EpisodeId == Guid.Empty || incoming.MachineId == "test") return false;
        lock (_gate) return MergeLocked(incoming);
    }

    /// <summary>
    /// Dopisuje do pliku wszystko, co czeka za progiem <see cref="ObservationWriteInterval"/>. Do wywołania
    /// przy czystym zamknięciu programu, żeby przerwany epizod miał na dysku ostatnią znaną obserwację.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_readPending && !TryReadLocked()) return;
            foreach (var episode in _episodes.Values.ToArray())
                if (!_persisted.TryGetValue(episode.EpisodeId, out var written) || written != episode)
                    Append(episode);
        }
    }

    private bool MergeLocked(DowntimeEpisode incoming)
    {
        if (_readPending) TryReadLocked();
        // Jedno wejście dla obserwacji własnej, epizodów z sieci i Apply: pusta/biała kategoria oraz „bez alarmu” /
        // „nie błąd” to „nieznana” (DowntimeCategories.Normalize) — nie trafia do pliku i nie nadpisuje znanej.
        if (DowntimeCategories.Normalize(incoming.Category) != incoming.Category)
            incoming = incoming with { Category = DowntimeCategories.Normalize(incoming.Category) };
        DowntimeEpisode result;
        if (_episodes.TryGetValue(incoming.EpisodeId, out var old))
        {
            if (old.MachineId != incoming.MachineId) return false; // obcy epizod pod tym samym Guidem — nie ufamy
            bool incomingIsNewer = incoming.LastObservedAt >= old.LastObservedAt;
            var textSource = incomingIsNewer ? incoming : old;
            var (endedAt, interrupted) = MergeEnd(old, incoming);
            result = old with
            {
                MachineName = textSource.MachineName, Reason = textSource.Reason,
                StartedAt = old.StartedAt < incoming.StartedAt ? old.StartedAt : incoming.StartedAt,
                LastObservedAt = incomingIsNewer ? incoming.LastObservedAt : old.LastObservedAt,
                EndedAt = endedAt, Interrupted = interrupted,
                Origin = textSource.Origin ?? old.Origin,
                // Kategoria idzie z tym samym wpisem, co tekst, ale pusta nigdy nie wygrywa ze znaną.
                Category = textSource.Category ?? (incomingIsNewer ? old.Category : incoming.Category),
            };
        }
        else result = incoming;
        if (result == old) return false;
        _episodes[incoming.EpisodeId] = result;
        _version++;
        if (!_readPending && ShouldAppend(result)) Append(result);
        return true;
    }

    /// <summary>
    /// Czy wersja <paramref name="result"/> ma trafić na dysk teraz. Nowy epizod i każda zmiana poza samym
    /// <see cref="DowntimeEpisode.LastObservedAt"/> (koniec epizodu, tekst błędu, komputer) — zawsze;
    /// samo przesunięcie czasu obserwacji — nie częściej niż <see cref="ObservationWriteInterval"/>.
    /// </summary>
    private bool ShouldAppend(DowntimeEpisode result)
    {
        if (!_persisted.TryGetValue(result.EpisodeId, out var written)) return true;
        if (result with { LastObservedAt = written.LastObservedAt } != written) return true;
        return result.LastObservedAt - written.LastObservedAt >= ObservationWriteInterval;
    }

    private void Append(DowntimeEpisode episode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            // Append a leading newline as well: a partial last write cannot swallow the next record.
            File.AppendAllText(_path, "\n" + JsonSerializer.Serialize(episode) + "\n");
            _persisted[episode.EpisodeId] = episode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "Nie można zapisać historii postojów. Dane po restarcie mogą być niepełne."; }
    }

    /// <summary>
    /// Koniec epizodu po scaleniu dwóch obserwacji. Kolejność reguł jest istotna:
    /// <list type="number">
    /// <item>prawdziwe zamknięcie (<c>Interrupted == false</c>) wygrywa zawsze — także nad późniejszym
    /// zamknięciem wymuszonym przy starcie programu; inaczej restart jednego komputera „odbierałby”
    /// epizod domknięty przez drugi. Przy dwóch prawdziwych wygrywa późniejsze;</item>
    /// <item>bez prawdziwego zamknięcia: wpis wciąż OTWARTY, którego ostatnia obserwacja jest późniejsza
    /// niż wymuszone zamknięcie drugiego, odwołuje to zamknięcie — epizod trwa dalej. Bez tej reguły
    /// epizod przerwany restartem tego komputera zostawałby zamrożony na chwili restartu, mimo że
    /// obserwator grupy widzi go dalej, a raport pokazywałby postój krótszy niż rzeczywisty;</item>
    /// <item>same zamknięcia wymuszone: późniejsze, dalej jako przerwane.</item>
    /// </list>
    /// </summary>
    private static (DateTimeOffset? EndedAt, bool Interrupted) MergeEnd(DowntimeEpisode a, DowntimeEpisode b)
    {
        var ends = new[] { (End: a.EndedAt, a.Interrupted), (End: b.EndedAt, b.Interrupted) }
            .Where(e => e.End is not null).ToArray();
        if (ends.Length == 0) return (null, false);
        if (ends.Where(e => !e.Interrupted).OrderByDescending(e => e.End).FirstOrDefault() is { End: not null } real)
            return (real.End, false);
        var forced = ends.OrderByDescending(e => e.End).First().End!.Value;
        bool stillObserved = a.EndedAt is null && a.LastObservedAt > forced
            || b.EndedAt is null && b.LastObservedAt > forced;
        return stillObserved ? (null, false) : (forced, true);
    }

    public IReadOnlyList<DowntimeEpisode> Snapshot()
    {
        lock (_gate) return _episodes.Values.OrderByDescending(e => e.StartedAt).ToArray();
    }

    /// <inheritdoc />
    public DateTimeOffset? LatestObservedAt
    {
        get { lock (_gate) return _episodes.Count == 0 ? null : _episodes.Values.Max(e => e.LastObservedAt); }
    }

    /// <inheritdoc />
    public IReadOnlyList<DowntimeEpisode> EpisodesSince(DateTimeOffset since, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
            return _episodes.Values.Where(e => e.LastObservedAt >= since)
                .OrderBy(e => e.LastObservedAt).ThenBy(e => e.EpisodeId).Take(limit).ToArray();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Wpis z sieci przechodzi przez to samo <see cref="Merge"/>, co obserwacja własna — reguły scalania
    /// (najwcześniejszy początek, najpóźniejsza obserwacja, pierwszeństwo prawdziwego zamknięcia) są więc
    /// dokładnie te same, a powtórzona porcja nic nie zmienia.
    /// </remarks>
    public bool Apply(IReadOnlyList<DowntimeEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        bool changed = false;
        lock (_gate)
            foreach (var episode in episodes)
                // Ten sam odsiew, co w Merge: maszyna „test” nigdy nie trafia do raportu, także gdy
                // przyszła z innego komputera.
                if (episode is not null && episode.IsValid() && episode.MachineId != "test"
                    && MergeLocked(episode)) changed = true;
        return changed;
    }
}
