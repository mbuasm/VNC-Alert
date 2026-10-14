namespace MaszynyAlert.Core.History;

public sealed record DowntimeSlice(DateOnly Day, string Shift, string Machine, string Reason,
    DateTimeOffset Start, DateTimeOffset End, bool Interrupted, bool Ongoing)
{
    public double Minutes => (End - Start).TotalMinutes;

    /// <summary>
    /// Kawałek DŁUGIEGO postoju: cały scalony epizod trwał dłużej niż <see cref="DowntimeReport.MaxEpisode"/>
    /// (przed przycięciem do zmian i weekendów). Raport pokazuje go oznaczonego, a sumy liczy
    /// w dwóch wariantach — z długimi postojami i bez nich (<see cref="DowntimeReport.Totals"/>).
    /// </summary>
    public bool IsLong { get; init; }

    /// <summary>
    /// Rodzaj błędu: zapisany w epizodzie (<see cref="DowntimeEpisode.Category"/>), a dla epizodu bez niego —
    /// z <see cref="DowntimeCategoryClassifier"/>; bez klasyfikatora <see cref="DowntimeCategories.Unrecognized"/>.
    /// </summary>
    public string Category { get; init; } = DowntimeCategories.Unrecognized;

    /// <summary>Rodzaj postoju: błąd maszyny albo postój bez pracy (100 % / 0 %) — z <see cref="Category"/>.</summary>
    public DowntimeKind Kind => DowntimeCategories.KindOf(Category);

    /// <summary>Czy to postój bez pracy („Stoi bez pracy (100 %)” albo „Stoi na 0 %”), a nie czerwony błąd maszyny.</summary>
    public bool IsIdle => Kind != DowntimeKind.Error;

    /// <summary>Identyfikator maszyny (<c>MachineConfig.Id</c>) — <see cref="Machine"/> to nazwa pokazywana.</summary>
    public string MachineId { get; init; } = string.Empty;

    /// <summary>
    /// Tożsamość scalonego epizodu (<see cref="DowntimeReport.MergeOverlaps"/>) — wszystkie kawałki jednego postoju
    /// (podział na zmiany i weekendy) mają ten sam identyfikator; po nim liczy się liczba postojów.
    /// </summary>
    public Guid EpisodeId { get; init; }
}

public enum DowntimeKind
{
    /// <summary>Czerwony alert błędu maszyny (narzędzie, tacka, restart, inny, nierozpoznany).</summary>
    Error,
    /// <summary>Postój bez pracy po zakończeniu programu (100 %).</summary>
    Idle,
    /// <summary>Postój na 0 % (program nie uruchomiony).</summary>
    IdleAtZero,
}

public sealed record DowntimeTotal(double Minutes, int Episodes);

public sealed record DowntimeKindTotals(DowntimeTotals Errors, DowntimeTotals Idle, DowntimeTotals IdleAtZero)
{
    public double IdleMinutes => Idle.All.Minutes + IdleAtZero.All.Minutes;

    /// <summary>Liczba postojów bez pracy (oba rodzaje).</summary>
    public int IdleEpisodes => Idle.All.Episodes + IdleAtZero.All.Episodes;
}

public sealed record DowntimeTotals(DowntimeTotal All, DowntimeTotal WithoutLong);

public static class DowntimeReport
{
    public static readonly TimeSpan MinEpisode = TimeSpan.Zero;

    public static readonly TimeSpan MaxEpisode = TimeSpan.FromHours(1);

    /// <summary>
    /// Czy scalony epizod (<see cref="MergeOverlaps"/>) należy do raportu: dłuższy niż <see cref="MinEpisode"/>,
    /// liczone na czasie całego epizodu (także części przypadającej na weekend). Górnej granicy nie
    /// ma — dłuższe niż <see cref="MaxEpisode"/> są oznaczone (<see cref="IsLong"/>). To, czy jakaś część epizodu
    /// leży w czasie raportowanym (poza weekendem), rozstrzyga dopiero <see cref="Build"/>.
    /// </summary>
    public static bool IsReported(DowntimeEpisode episode) => episode.End - episode.StartedAt > MinEpisode;

    /// <summary>
    /// Zapas ponad chwilę „teraz” (parametr <c>now</c> w <see cref="Build"/>), do którego raport kroi epizod. Ostatnia obserwacja
    /// odrobinę w przyszłości bywa uczciwa (rozjechane zegary komputerów grupy), doba w przód — już nie.
    /// </summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>
    /// Najdłuższy odcinek epizodu, jaki <see cref="Build"/> kroi na zmiany, gdy wołający nie poda chwili „teraz” — zabezpieczenie
    /// kosztu dla wywołań bez zegara. Rok z zapasem: realny postój (nawet czerwień zostawiona na urlop) jest dużo krótszy.
    /// </summary>
    public static readonly TimeSpan MaxSlicedSpan = TimeSpan.FromDays(400);

    /// <summary>Czy scalony epizod to długi postój: dłuższy niż <see cref="MaxEpisode"/>.</summary>
    public static bool IsLong(DowntimeEpisode episode) => episode.End - episode.StartedAt > MaxEpisode;

    public static IReadOnlyList<DowntimeSlice> Build(IEnumerable<DowntimeEpisode> episodes, TimeZoneInfo zone,
        DowntimeCategoryClassifier? classifier = null, DateTimeOffset? now = null)
    {
        var result = new List<DowntimeSlice>();
        var merged = MergeOverlaps(episodes);
        var errorsByMachine = merged.Where(e => !IsIdleEpisode(e))
            .GroupBy(ReportMachineKey.Of, e => (Start: e.StartedAt, End: e.End))
            .ToDictionary(g => g.Key, g => new ErrorIntervals(g));
        foreach (var episode in merged)
        {
            IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> own = [(episode.StartedAt, SliceEnd(episode, now))];
            TimeSpan measured = episode.End - episode.StartedAt;
            if (IsIdleEpisode(episode) && errorsByMachine.TryGetValue(ReportMachineKey.Of(episode), out var errors))
            {
                own = errors.Subtract(own[0].Start, own[0].End);
                measured = errors.Subtract(episode.StartedAt, episode.End)
                    .Aggregate(TimeSpan.Zero, (sum, part) => sum + (part.End - part.Start));
            }
            if (measured <= MinEpisode) continue;
            bool isLong = measured > MaxEpisode;
            string category = classifier?.CategoryOf(episode)
                ?? DowntimeCategories.Of(episode) ?? DowntimeCategories.Unrecognized;
            foreach (var (partStart, partEnd) in own.SelectMany(part => ReportCalendar.ReportedIntervals(part.Start, part.End, zone)))
            {
                var cursor = partStart;
                while (cursor < partEnd)
                {
                    var local = ReportCalendar.LocalWall(cursor, zone);
                    int hour = local.Hour;
                    var day = DateOnly.FromDateTime(hour < 6 ? ReportCalendar.AddWall(local, TimeSpan.FromDays(-1)) ?? local : local);
                    string shift = hour is >= 6 and < 14 ? "06:00–14:00" : hour is >= 14 and < 22 ? "14:00–22:00" : "22:00–06:00";
                    var nextLocal = ReportCalendar.AddWall(local.Date,
                        TimeSpan.FromHours(hour < 6 ? 6 : hour < 14 ? 14 : hour < 22 ? 22 : 30));
                    var boundary = ReportCalendar.NextInstant(nextLocal, zone, cursor);
                    var until = partEnd < boundary ? partEnd : boundary;
                    result.Add(new(day, shift, episode.MachineName, episode.Reason, cursor, until,
                        episode.Interrupted, episode.EndedAt is null)
                    {
                        IsLong = isLong, Category = category, MachineId = episode.MachineId, EpisodeId = episode.EpisodeId,
                    });
                    cursor = until;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Koniec krojenia epizodu: jego koniec, ale nie dalej niż <paramref name="now"/> + <see cref="FutureTolerance"/>
    /// (a bez <paramref name="now"/> — niż <see cref="MaxSlicedSpan"/> od początku). Odejmowanie i dodawanie bez przepełnienia
    /// przy datach granicznych (epizod 9999 albo „teraz” przy końcu kalendarza).
    /// </summary>
    private static DateTimeOffset SliceEnd(DowntimeEpisode episode, DateTimeOffset? now)
    {
        DateTimeOffset limit = now is { } current
            ? (current > DateTimeOffset.MaxValue - FutureTolerance ? DateTimeOffset.MaxValue : current + FutureTolerance)
            : (episode.StartedAt > DateTimeOffset.MaxValue - MaxSlicedSpan ? DateTimeOffset.MaxValue : episode.StartedAt + MaxSlicedSpan);
        return episode.End < limit ? episode.End : limit;
    }

    public static bool DependsOnNow(DateTimeOffset latestEnd, DateTimeOffset now) =>
        latestEnd >= (now > DateTimeOffset.MaxValue - FutureTolerance ? DateTimeOffset.MaxValue : now + FutureTolerance);

    /// <summary>
    /// Sumy wierszy raportu w dwóch wariantach — z długimi postojami i bez nich (<see cref="DowntimeTotals"/>).
    /// Postój podzielony na kilka zmian albo dni liczy się raz (po <see cref="DowntimeSlice.EpisodeId"/> i maszynie).
    /// Wołający filtruje wiersze (okres, zmiana) PRZED wywołaniem, więc sumy dotyczą dokładnie tego, co pokazuje tabela.
    /// </summary>
    public static DowntimeTotals Totals(IEnumerable<DowntimeSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        var list = slices as IReadOnlyCollection<DowntimeSlice> ?? slices.ToArray();
        static DowntimeTotal Sum(IEnumerable<DowntimeSlice> rows)
        {
            var array = rows as DowntimeSlice[] ?? rows.ToArray();
            return new DowntimeTotal(array.Sum(r => r.Minutes),
                array.Select(r => (Machine: ReportMachineKey.Of(r), r.EpisodeId)).Distinct().Count());
        }
        return new DowntimeTotals(Sum(list), Sum(list.Where(r => !r.IsLong)));
    }

    /// <summary>
    /// Sumy wierszy w podziale na rodzaj postoju (błędy maszyn / bez pracy 100 % / na 0 %) — każda jak <see cref="Totals"/>.
    /// Wiersz należy do dokładnie jednego rodzaju, więc suma rodzajów równa się sumie całości (wykresy i kolumny „w tym błędy”
    /// zgadzają się z kartami i tabelą).
    /// </summary>
    public static DowntimeKindTotals ByKind(IEnumerable<DowntimeSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        var list = slices as IReadOnlyCollection<DowntimeSlice> ?? slices.ToArray();
        return new DowntimeKindTotals(
            Totals(list.Where(s => s.Kind == DowntimeKind.Error)),
            Totals(list.Where(s => s.Kind == DowntimeKind.Idle)),
            Totals(list.Where(s => s.Kind == DowntimeKind.IdleAtZero)));
    }

    public static IReadOnlyList<DowntimeEpisode> MergeOverlaps(IEnumerable<DowntimeEpisode> episodes)
    {
        var result = new List<DowntimeEpisode>();
        foreach (var machine in episodes.GroupBy(e => (Machine: ReportMachineKey.Of(e), Idle: IsIdleEpisode(e))))
        {
            DowntimeEpisode? open = null;
            foreach (var episode in machine.OrderBy(e => e.StartedAt).ThenBy(e => e.End))
            {
                if (open is null) { open = episode; continue; }
                // Styk „koniec jednego = początek drugiego” też scalamy: dwa obserwatory dzielące jedną
                // sekundę widzenia tego samego błędu to nadal jeden postój, nie dwa sąsiadujące.
                if (episode.StartedAt > open.End) { result.Add(open); open = episode; continue; }
                open = Combine(open, episode);
            }
            if (open is not null) result.Add(open);
        }
        return result;
    }

    /// <summary>Czy epizod to postój bez błędu (alert „Stoi bez pracy (100 %)” / „Stoi na 0 %”) — po rodzaju albo tekście.</summary>
    public static bool IsIdleEpisode(DowntimeEpisode episode) => DowntimeCategories.IsIdle(DowntimeCategories.Of(episode));

    private sealed class ErrorIntervals
    {
        private readonly (DateTimeOffset Start, DateTimeOffset End)[] _cuts;
        private readonly DateTimeOffset[] _furthestEnd;

        public ErrorIntervals(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> cuts)
        {
            _cuts = cuts.OrderBy(c => c.Start).ToArray();
            _furthestEnd = new DateTimeOffset[_cuts.Length];
            for (int i = 0; i < _cuts.Length; i++)
                _furthestEnd[i] = i > 0 && _furthestEnd[i - 1] >= _cuts[i].End ? _furthestEnd[i - 1] : _cuts[i].End;
        }

        public List<(DateTimeOffset Start, DateTimeOffset End)> Subtract(DateTimeOffset start, DateTimeOffset end)
        {
            var parts = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            var cursor = start;
            for (int i = FirstEndingAfter(start); i < _cuts.Length && _cuts[i].Start < end; i++)
            {
                var (cutStart, cutEnd) = _cuts[i];
                if (cutEnd <= start) continue;
                if (cutStart > cursor) parts.Add((cursor, cutStart < end ? cutStart : end));
                if (cutEnd > cursor) cursor = cutEnd;
                if (cursor >= end) break;
            }
            if (cursor < end) parts.Add((cursor, end));
            return parts;
        }

        /// <summary>Pierwszy indeks, od którego najdalszy koniec prefiksu jest po <paramref name="start"/> (wcześniejsze błędy kończą się najpóźniej w <paramref name="start"/>).</summary>
        private int FirstEndingAfter(DateTimeOffset start)
        {
            int low = 0, high = _furthestEnd.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_furthestEnd[middle] > start) high = middle;
                else low = middle + 1;
            }
            return low;
        }
    }

    /// <summary>
    /// Jeden przedział z dwóch nakładających się. Koniec — i to, czy w ogóle ktoś go widział
    /// (<see cref="DowntimeEpisode.EndedAt"/>, <see cref="DowntimeEpisode.Interrupted"/>) — bierze się
    /// z wpisu sięgającego najdalej, bo to on mówi, kiedy postój się skończył; tekst błędu i nazwa maszyny
    /// z najświeższej obserwacji, tak samo jak w <see cref="DowntimeJournal.Merge"/>. Rodzaj błędu idzie razem
    /// z tekstem, ale pusty nigdy nie zastępuje znanego (<see cref="DowntimeEpisode.Category"/>).
    /// </summary>
    private static DowntimeEpisode Combine(DowntimeEpisode a, DowntimeEpisode b)
    {
        var longest = a.End >= b.End ? a : b;
        var newest = a.LastObservedAt >= b.LastObservedAt ? a : b;
        var first = a.StartedAt <= b.StartedAt ? a : b;
        return longest with
        {
            EpisodeId = first.EpisodeId, Origin = longest.Origin ?? newest.Origin,
            MachineName = newest.MachineName, Reason = newest.Reason,
            // Pusta/biała ani „bez alarmu” nigdy nie wygrywa ze znaną (DowntimeCategories.Normalize).
            Category = DowntimeCategories.Normalize(newest.Category)
                ?? DowntimeCategories.Normalize(ReferenceEquals(newest, a) ? b.Category : a.Category),
            StartedAt = first.StartedAt, LastObservedAt = newest.LastObservedAt,
        };
    }
}
