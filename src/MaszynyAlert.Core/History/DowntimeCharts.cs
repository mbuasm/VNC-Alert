using System.Globalization;
using System.Text;

namespace MaszynyAlert.Core.History;

public readonly record struct ChartColor(uint LightArgb, uint DarkArgb)
{
    /// <summary>Kolor dla motywu: ciemny albo jasny (PDF i Excel — zawsze jasny).</summary>
    public uint For(bool dark) => dark ? DarkArgb : LightArgb;

    /// <summary>Kolor jasnego motywu jako „#RRGGBB” (PDF, Excel).</summary>
    public string LightHex => "#" + (LightArgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
}

public static class ChartPalette
{
    private static readonly ChartColor[] Slots =
    [
        new(0xFF004489, 0xFF8DBFFE), // niebieski   (jasny motyw: ciemny; ciemny motyw: jasny)
        new(0xFFEB6510, 0xFFC05008), // pomarańczowy (jasny / ciemny)
        new(0xFF007172, 0xFF06A3A4), // morski       (średni)
        new(0xFFB88513, 0xFF966B01), // żółty        (jasny / ciemny) — 3,3:1 na białym
        new(0xFF880458, 0xFFFE9FCC), // różowy       (ciemny / jasny)
        new(0xFF33A307, 0xFF298408), // zielony      (jasny / ciemny)
        new(0xFF6F4AD3, 0xFF997FFD), // fioletowy    (średni)
        new(0xFFF85553, 0xFFD73337), // czerwony     (jasny / ciemny)
    ];

    /// <summary>Liczba kolorów seryjnych.</summary>
    public const int SlotCount = 8;

    /// <summary>Kolor seryjny o numerze <paramref name="index"/> (od 0).</summary>
    public static ChartColor Slot(int index) =>
        index is >= 0 and < SlotCount ? Slots[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>„Pozostałe” — jasna szarość, celowo bez barwy.</summary>
    public static readonly ChartColor Remainder = new(0xFFB9B7B0, 0xFF5C5B57);

    /// <summary>„Nierozpoznany” — ciemniejsza szarość niż „Pozostałe” (rozróżnienie jasnością).</summary>
    public static readonly ChartColor Unrecognized = new(0xFF78766F, 0xFFA8A69E);

    /// <summary>
    /// Grupa „Błędy maszyn” na wykresach rodzaju postoju (słupki według maszyny, kołowy według rodzaju, linia błędów) — pierwszy
    /// kolor palety, żeby błędy były najbardziej nasycone, a postoje bez pracy stonowane (<see cref="Idle"/>, <see cref="IdleAtZero"/>).
    /// </summary>
    public static ChartColor Errors => Slot(0);

    public static readonly ChartColor Idle = new(0xFF65676B, 0xFF939599);

    /// <summary>
    /// „Stoi na 0 %” — jaśniejsza (jasny motyw) albo ciemniejsza (ciemny motyw) szarość niż <see cref="Idle"/>: kontrast
    /// między nimi 1,68 / 1,58, do tła 3,4:1 / 3,1:1, do <see cref="Errors"/> 2,9 / 2,5 (walidator w <c>ChartPaletteTests</c>).
    /// </summary>
    public static readonly ChartColor IdleAtZero = new(0xFF8A8C90, 0xFF717377);
}

/// <summary>Punkt serii: etykieta osi (maszyna, dzień), wartość w minutach i gotowy tekst wartości.</summary>
/// <param name="LongValue">
/// Część wartości przypadająca na DŁUGIE postoje (&gt; 1 h, <see cref="DowntimeSlice.IsLong"/>) — słupki według maszyny rysują ją
/// kreskowaniem, a podpowiedź mówi „w tym długie”. Zero dla serii, w których podział nie ma sensu (dni).
/// </param>
public sealed record ChartPoint(string Label, double Value, string ValueText, double LongValue = 0);

/// <summary>Seria wykresu: nazwa do legendy, kolor i punkty (w kolejności kategorii albo dni wykresu).</summary>
public sealed record ChartSeries(string Name, ChartColor Color, IReadOnlyList<ChartPoint> Points);

/// <summary>
/// Wykres słupkowy (skumulowany): kategorie = maszyny, serie = składniki słupka (rodzaje postoju albo nazwy błędów),
/// <see cref="TotalTexts"/> = wartość na końcu słupka. Długie postoje (&gt; 1 h) niesie <see cref="ChartPoint.LongValue"/>.
/// </summary>
public sealed record BarChartData(string Title, string Subtitle, string ValueAxisTitle,
    IReadOnlyList<string> Categories, IReadOnlyList<ChartSeries> Series, IReadOnlyList<double> Totals,
    IReadOnlyList<string> TotalTexts)
{
    /// <summary>Brak jakiegokolwiek postoju — widok pokazuje napis zamiast pustych osi.</summary>
    public bool IsEmpty => Totals.All(t => t <= 0);

    /// <summary>Napis zamiast pustych osi (różny dla wykresu wszystkich postojów i wykresu samych błędów).</summary>
    public string EmptyText { get; init; } = "W wybranym zakresie nie ma postojów — słupki są puste.";

    /// <summary>Czy którykolwiek punkt ma część długich postojów (legenda kreskowania w Panelu).</summary>
    public bool HasLong => Series.Any(s => s.Points.Any(p => p.LongValue > 0));
}

/// <summary>Rodzaj wycinka wykresu kołowego.</summary>
public enum PieSliceKind
{
    /// <summary>Jedna nazwa błędu.</summary>
    Name,
    /// <summary><see cref="DowntimePresentation.UnrecognizedName"/> — zawsze osobno, nigdy w „Pozostałe”.</summary>
    Unrecognized,
    /// <summary><see cref="DowntimePresentation.RemainderName"/> — suma najmniejszych nazw.</summary>
    Remainder,
}

/// <param name="Share">Udział w łącznym czasie wykresu, 0–1.</param>
/// <param name="NameCount">Ile nazw błędów składa się na wycinek (1, a dla „Pozostałe” — ile zebrano).</param>
public sealed record PieSlice(string Label, double Value, double Share, string ValueText, string ShareText,
    ChartColor Color, PieSliceKind Kind, int NameCount);

/// <summary>Wykres kołowy: udział czasu postojów według nazwy błędu albo według rodzaju postoju.</summary>
public sealed record PieChartData(string Title, string Subtitle, IReadOnlyList<PieSlice> Slices, double Total, string TotalText)
{
    public bool IsEmpty => Total <= 0;

    /// <summary>Napis zamiast pustego koła (wykres błędów: „brak błędów”, nie „brak postojów”).</summary>
    public string EmptyText { get; init; } = "W wybranym zakresie nie ma postojów — brak udziałów do pokazania.";
}

/// <summary>
/// Wykres liniowy: minuty postojów na dzień zmian. <see cref="Days"/> to kolejne dni zakresu BEZ sobót i niedziel
/// (weekend nie należy do raportu — <see cref="ReportCalendar"/>); punkty każdej serii odpowiadają im po kolei.
/// </summary>
/// <param name="Note">Uwaga pod wykresem (np. przycięcie bardzo długiego zakresu) albo <see langword="null"/>.</param>
public sealed record LineChartData(string Title, string Subtitle, string ValueAxisTitle, string CategoryAxisTitle,
    IReadOnlyList<DateOnly> Days, IReadOnlyList<ChartSeries> Series, bool PerMachine, string? Note)
{
    public bool IsEmpty => Series.All(s => s.Points.All(p => p.Value <= 0));
}

public sealed record NoDataEntry(string MachineId, string Machine, TimeSpan NoConnection, TimeSpan ScreenHidden);

public sealed record NoDataAnnotation(IReadOnlyList<NoDataEntry> Machines, IReadOnlyList<string> Lines);

/// <summary>Nagłówek sekcji wykresów: zakres dat, liczba zmian, liczba maszyn, chwila wygenerowania.</summary>
/// <param name="Shifts">Zmiany w zakresie, bez weekendów: dni robocze × liczba zmian (1 przy filtrze zmiany, inaczej 3).</param>
/// <param name="WorkingDays">Dni robocze (pon.–pt.) w zakresie wykresów.</param>
public sealed record DowntimeChartHeader(DateOnly? From, DateOnly? To, string? Shift, int WorkingDays, int Shifts,
    int Machines, DateTimeOffset GeneratedAt, string RangeText, string Text);

public sealed record DowntimeChartSet(DowntimeChartHeader Header, BarChartData Machines, PieChartData Kinds, PieChartData Errors,
    BarChartData ErrorMachines, LineChartData Days, NoDataAnnotation? NoData)
{
    /// <summary>
    /// Klucz treści wykresów: teksty i wartości po zaokrągleniu do pokazywanej dokładności, BEZ chwili wygenerowania.
    /// Panel odświeża raport co sekundę — rysuje wykresy od nowa tylko wtedy, gdy klucz się zmienił.
    /// </summary>
    public string ContentKey()
    {
        var key = new StringBuilder();
        key.Append(Header.RangeText).Append('|').Append(Header.Shifts).Append('|').Append(Header.Machines).Append('\n');
        void Add(ChartSeries series)
        {
            key.Append(series.Name).Append(':');
            foreach (var point in series.Points)
            {
                key.Append(point.Label).Append('=').Append(point.ValueText);
                if (point.LongValue > 0) key.Append('~').Append(DowntimeCharts.MinutesText(point.LongValue));
                key.Append(';');
            }
            key.Append('\n');
        }
        void Bar(BarChartData bar)
        {
            key.Append(bar.Title).Append(string.Join(";", bar.Categories)).Append('\n');
            foreach (var series in bar.Series) Add(series);
            key.Append(string.Join(";", bar.TotalTexts)).Append('\n');
        }
        void Pie(PieChartData pie)
        {
            key.Append(pie.Title).Append(':');
            foreach (var slice in pie.Slices) key.Append(slice.Label).Append('=').Append(slice.ValueText).Append('/').Append(slice.ShareText).Append(';');
            key.Append('\n');
        }
        Bar(Machines);
        Pie(Kinds);
        Pie(Errors);
        Bar(ErrorMachines);
        key.Append(Days.Title).Append(':').Append(Days.PerMachine).Append(Days.Note).Append('\n');
        foreach (var series in Days.Series) Add(series);
        if (NoData is not null) foreach (var line in NoData.Lines) key.Append(line).Append('\n');
        return key.ToString();
    }
}

/// <summary>Maszyna z konfiguracji: identyfikator i nazwa pokazywana (maszyny bez postojów też mają słupek 0).</summary>
public sealed record ChartMachine(string Id, string Name);

/// <summary>Parametry budowy wykresów — te same filtry, co tabela raportu.</summary>
public sealed record DowntimeChartOptions
{
    /// <summary>Pierwszy dzień zmian zakresu; <see langword="null"/> = od najstarszego postoju lub wpisu obserwacji („Cała historia”).</summary>
    public DateOnly? From { get; init; }

    /// <summary>Ostatni dzień zmian zakresu; <see langword="null"/> = do dziś (albo do najmłodszego postoju).</summary>
    public DateOnly? To { get; init; }

    /// <summary>Filtr zmiany („06:00–14:00”…) albo <see langword="null"/> = wszystkie.</summary>
    public string? Shift { get; init; }

    /// <summary>Maszyny z konfiguracji, w kolejności konfiguracji (włączone) — słupek dostają też te bez postojów.</summary>
    public IReadOnlyList<ChartMachine> Machines { get; init; } = [];

    /// <summary>Filtr maszyn (identyfikatory) albo <see langword="null"/> = wszystkie.</summary>
    public IReadOnlySet<string>? MachineFilter { get; init; }

    /// <summary>Wykres liniowy: osobna seria dla każdej maszyny zamiast sumy.</summary>
    public bool LinePerMachine { get; init; }

    public bool IncludeIdle { get; init; } = true;

    /// <summary>Chwila wygenerowania i zarazem „teraz” (koniec zakresu „do dziś”).</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;

    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public IReadOnlyList<ObservationInterval>? Observation { get; init; }
}

public static class DowntimeCharts
{
    public const string WithoutLongName = "Bez długich postojów";

    public const string LongName = "Długie postoje (> 1 h)";

    /// <summary>Seria wykresu liniowego z długimi postojami.</summary>
    public const string AllName = "Wszystkie postoje";

    public const string ErrorsName = "Błędy maszyn";

    /// <summary>Seria wykresu dziennego przy wyłączonych postojach bez pracy: błędy maszyn bez długich postojów.</summary>
    public const string ErrorsWithoutLongName = "Błędy bez długich postojów";

    /// <summary>Nazwa rodzaju „Bez pracy (100 %)” — ta sama co w kolumnie „Grupa błędu” (<see cref="DowntimePresentation.CategoryName"/>).</summary>
    public static string IdleName => DowntimePresentation.CategoryName(DowntimeCategories.Idle);

    /// <summary>Nazwa rodzaju „Stoi na 0 %” — ta sama co w kolumnie „Grupa błędu”.</summary>
    public static string IdleAtZeroName => DowntimePresentation.CategoryName(DowntimeCategories.IdleAtZero);

    /// <summary>Pozycja zbiorcza wykresu liniowego w trybie „osobno dla każdej maszyny”.</summary>
    public const string OtherMachinesName = "Pozostałe maszyny";

    public const string NoConnectionName = "Brak danych - brak połączenia z maszyną";

    public const string ScreenHiddenName = "Brak danych - ekran HMI niewidoczny";

    /// <summary>Ile największych nazw błędów dostaje własny wycinek (reszta — „Pozostałe”; „Nierozpoznany” zawsze osobno).</summary>
    public const int MaxPieNames = 6;

    /// <summary>Ile maszyn dostaje własną linię w trybie „osobno dla każdej maszyny” (reszta — „Pozostałe maszyny”).</summary>
    public const int MaxLineMachines = 7;

    /// <summary>Najwięcej dni roboczych na osi wykresu liniowego; dłuższy zakres pokazuje najnowsze dni (z uwagą).</summary>
    public const int MaxLineDays = 520;

    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly string[] ShiftNames = ["06:00–14:00", "14:00–22:00", "22:00–06:00"];

    /// <summary>Wszystkie wykresy z wierszy raportu <paramref name="slices"/> (już przefiltrowanych jak tabela).</summary>
    public static DowntimeChartSet Build(IReadOnlyList<DowntimeSlice> slices, DowntimeChartOptions options)
    {
        ArgumentNullException.ThrowIfNull(slices);
        ArgumentNullException.ThrowIfNull(options);
        var rows = slices.Where(s => s is not null && Accepts(options, s)).ToArray();
        // Warstwa „błędy maszyn”: wiersze bez postojów bez pracy — wykresy błędów liczą się z nich niezależnie od przełącznika.
        var errorRows = rows.Where(s => !s.IsIdle).ToArray();
        var machines = MachinesOf(rows, options);
        var range = RangeOf(rows, options);
        var (days, truncated) = AxisDays(range);
        var bar = BuildMachines(rows, machines, options.IncludeIdle);
        var kinds = BuildKinds(rows);
        var (pie, sliceOfName) = BuildErrors(errorRows);
        var errorBar = BuildErrorMachines(errorRows, machines, pie, sliceOfName);
        var line = BuildDays(rows, errorRows, machines, days, options.LinePerMachine, truncated, options.IncludeIdle);
        var noData = options.Observation is null ? null : BuildNoData(options, machines, range);
        var header = BuildHeader(options, range, machines.Count);
        return new DowntimeChartSet(header, bar, kinds, pie, errorBar, line, noData);
    }

    /// <summary>Minuty jako tekst: „12,3 min”.</summary>
    public static string MinutesText(double minutes) => minutes.ToString("N1", Pl) + " min";

    /// <summary>Czas jako tekst: „2 godz. 5 min”, „35 min”, „&lt; 1 min”, „0 min”.</summary>
    public static string DurationText(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0 min";
        long minutes = (long)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes == 0) return "< 1 min";
        return minutes >= 60 ? $"{minutes / 60} godz. {minutes % 60} min" : $"{minutes} min";
    }

    /// <summary>
    /// Filtr okresów liczonych dla adnotacji „brak danych”: bez weekendu (<see cref="ReportCalendar.ReportedIntervals"/>)
    /// i — przy filtrze zmiany — tylko części przedziału w tej zmianie (granice zmian jak w <see cref="DowntimeReport.Build"/>).
    /// Postać zgodna z parametrem <c>countedParts</c> w <see cref="ObservationReport.Summarize"/>.
    /// </summary>
    public static Func<DateTimeOffset, DateTimeOffset, IEnumerable<(DateTimeOffset Start, DateTimeOffset End)>> CountedParts(
        TimeZoneInfo zone, string? shift)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (start, end) => ReportCalendar.ReportedIntervals(start, end, zone)
            .SelectMany(part => ShiftParts(part.Start, part.End, shift, zone));
    }

    /// <summary>Najdłuższy odcinek, dla którego <see cref="CountedWindows"/> liczy okna z góry (dłuższy — filtr liczony na przedział).</summary>
    private static readonly TimeSpan MaxPrecomputedSpan = TimeSpan.FromDays(3 * 366);

    private static Func<DateTimeOffset, DateTimeOffset, IEnumerable<(DateTimeOffset Start, DateTimeOffset End)>> CountedWindows(
        TimeZoneInfo zone, string? shift, IReadOnlyList<ObservationInterval> intervals, DateTimeOffset from, DateTimeOffset to)
    {
        var perInterval = CountedParts(zone, shift);
        var valid = intervals.Where(i => i is not null && i.End > i.Start && i.End > from && i.Start < to).ToArray();
        if (valid.Length == 0) return perInterval;
        var lo = valid.Min(i => i.Start);
        var hi = valid.Max(i => i.End);
        if (lo < from) lo = from;
        if (hi > to) hi = to;
        if (hi <= lo || hi - lo > MaxPrecomputedSpan) return perInterval;
        var windows = perInterval(lo, hi).ToArray();
        return (start, end) => Clip(windows, start, end);
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Clip(
        (DateTimeOffset Start, DateTimeOffset End)[] windows, DateTimeOffset start, DateTimeOffset end)
    {
        // Pierwsze okno, które kończy się po początku przedziału (okna rozłączne, rosnące).
        int low = 0, high = windows.Length;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (windows[mid].End <= start) low = mid + 1; else high = mid;
        }
        for (int i = low; i < windows.Length && windows[i].Start < end; i++)
        {
            var s = windows[i].Start > start ? windows[i].Start : start;
            var e = windows[i].End < end ? windows[i].End : end;
            if (s < e) yield return (s, e);
        }
    }

    private static bool Accepts(DowntimeChartOptions options, DowntimeSlice slice) =>
        (options.MachineFilter is null || options.MachineFilter.Contains(slice.MachineId))
        && (options.Shift is null || slice.Shift == options.Shift)
        && (options.From is not { } from || slice.Day >= from)
        && (options.To is not { } to || slice.Day <= to);

    private static ReportMachineKey KeyOf(DowntimeSlice slice) =>
        ReportMachineKey.Of(slice);

    private sealed record ReportMachine(ReportMachineKey Key, string Name);

    /// <summary>Maszyny wykresu: z konfiguracji (także bez postojów) i z wierszy raportu (np. z grupy), bez powtórzeń.</summary>
    private static List<ReportMachine> MachinesOf(IReadOnlyList<DowntimeSlice> rows, DowntimeChartOptions options)
    {
        var names = new ReportMachineNames(rows, options.Machines);
        var result = new List<ReportMachine>();
        var seen = new HashSet<ReportMachineKey>();
        foreach (var machine in options.Machines)
            if (machine is not null && !string.IsNullOrWhiteSpace(machine.Id)
                && (options.MachineFilter is null || options.MachineFilter.Contains(machine.Id))
                && seen.Add(ReportMachineKey.ForId(machine.Id)))
                result.Add(new ReportMachine(ReportMachineKey.ForId(machine.Id), names.For(machine.Id, machine.Name)));
        foreach (var row in rows)
            if (seen.Add(KeyOf(row))) result.Add(new ReportMachine(KeyOf(row), names.For(row)));
        return result;
    }

    private static (DateOnly From, DateOnly To)? RangeOf(IReadOnlyList<DowntimeSlice> rows, DowntimeChartOptions options)
    {
        var today = ReportHistoryRange.ShiftDay(options.GeneratedAt, options.Zone);
        DateOnly? first = ReportHistoryRange.FirstDay(rows, options.Observation, options.Zone);
        DateOnly? last = rows.Count > 0 ? rows.Max(r => r.Day) : null;
        DateOnly from = options.From ?? first ?? today;
        // Dni po dzisiejszym (dniu zmian chwili wygenerowania) nie mają jeszcze danych — zero byłoby fałszywym „brakiem
        // postojów”, więc zakres kończy się na dziś, chyba że raport ma wiersze późniejsze (rozjechane zegary grupy).
        DateOnly upper = last is { } l && l > today ? l : today;
        DateOnly to = options.To is { } chosen && chosen < upper ? chosen : upper;
        return from > to ? null : (from, to);
    }

    /// <summary>Dni robocze (pon.–pt.) w [<paramref name="from"/>; <paramref name="to"/>] — arytmetycznie, bez wyliczania dni.</summary>
    private static int WorkingDaysBetween(DateOnly from, DateOnly to)
    {
        long total = (long)to.DayNumber - from.DayNumber + 1;
        if (total <= 0) return 0;
        long count = total / 7 * 5;
        var day = from.DayOfWeek;
        for (long i = 0; i < total % 7; i++, day = (DayOfWeek)(((int)day + 1) % 7))
            if (day is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) count++;
        return (int)Math.Min(count, int.MaxValue);
    }

    /// <summary>
    /// Dni osi wykresu liniowego: dni robocze pełnego zakresu, przycięte do najnowszych <see cref="MaxLineDays"/> (z uwagą
    /// pod wykresem). Słupki, kołowy, nagłówek i „brak danych” liczą cały zakres.
    /// </summary>
    private static (List<DateOnly> Days, bool Truncated) AxisDays((DateOnly From, DateOnly To)? range)
    {
        var days = new List<DateOnly>();
        if (range is not { } r) return (days, false);
        var (from, to) = r;
        // Najpierw przycięcie zakresu (bez wyliczania milionów dni przy dacie granicznej), potem dni robocze.
        bool truncated = false;
        int maxCalendarDays = MaxLineDays / 5 * 7 + 7;
        if (to.DayNumber - from.DayNumber >= maxCalendarDays)
        {
            from = DateOnly.FromDayNumber(to.DayNumber - maxCalendarDays + 1);
            truncated = true;
        }
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(day);
            if (day == DateOnly.MaxValue) break;
        }
        if (days.Count > MaxLineDays)
        {
            days.RemoveRange(0, days.Count - MaxLineDays);
            truncated = true;
        }
        return (days, truncated);
    }

    /// <summary>Składniki słupka według maszyny: rodzaj postoju, nazwa serii i kolor (błędy zawsze pierwsze).</summary>
    private static (DowntimeKind Kind, string Name, ChartColor Color)[] KindSeries(bool includeIdle) => includeIdle
        ? [(DowntimeKind.Error, ErrorsName, ChartPalette.Errors), (DowntimeKind.Idle, IdleName, ChartPalette.Idle),
            (DowntimeKind.IdleAtZero, IdleAtZeroName, ChartPalette.IdleAtZero)]
        : [(DowntimeKind.Error, ErrorsName, ChartPalette.Errors)];

    private static BarChartData BuildMachines(IReadOnlyList<DowntimeSlice> rows, List<ReportMachine> machines, bool includeIdle)
    {
        var kinds = KindSeries(includeIdle);
        var byMachine = rows.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToArray());
        var stats = machines.Select((machine, index) =>
        {
            var mine = byMachine.TryGetValue(machine.Key, out var list) ? list : [];
            var values = kinds.Select(k => (
                value: mine.Where(s => s.Kind == k.Kind).Sum(s => s.Minutes),
                longValue: mine.Where(s => s.Kind == k.Kind && s.IsLong).Sum(s => s.Minutes))).ToArray();
            return (machine, index, values, total: values.Sum(v => v.value));
        }).OrderByDescending(s => s.total).ThenBy(s => s.index).ToArray();
        var categories = stats.Select(s => s.machine.Name).ToArray();
        var series = kinds.Select((kind, k) => new ChartSeries(kind.Name, kind.Color,
            stats.Select(s => new ChartPoint(s.machine.Name, s.values[k].value, MinutesText(s.values[k].value), s.values[k].longValue)).ToArray()))
            .ToArray();
        return new BarChartData("Czas postojów według maszyny",
            includeIdle
                ? "Minuty postojów w wybranym zakresie. Długość słupka to suma; jego części — błędy maszyn oraz postoje bez pracy "
                    + "(100 % i na 0 %). Kreskowanie w części: długie postoje (> 1 h)."
                : "Minuty błędów maszyn w wybranym zakresie — postoje bez pracy ukryte (przełącznik nad wykresami). "
                    + "Kreskowanie w słupku: długie postoje (> 1 h).",
            includeIdle ? "Minuty postojów" : "Minuty błędów maszyn", categories, series, stats.Select(s => s.total).ToArray(),
            stats.Select(s => MinutesText(s.total)).ToArray())
        {
            EmptyText = includeIdle
                ? "W wybranym zakresie nie ma postojów — słupki są puste."
                : "W wybranym zakresie nie ma błędów maszyn — słupki są puste (postoje bez pracy są ukryte).",
        };
    }

    /// <summary>Czas postojów według rodzaju: błędy maszyn, bez pracy (100 %), na 0 % — zawsze z postojami bez pracy.</summary>
    private static PieChartData BuildKinds(IReadOnlyList<DowntimeSlice> rows)
    {
        double total = rows.Sum(s => s.Minutes);
        var slices = new List<PieSlice>();
        foreach (var (kind, name, color) in KindSeries(includeIdle: true))
        {
            double minutes = rows.Where(s => s.Kind == kind).Sum(s => s.Minutes);
            if (minutes <= 0) continue;
            double share = total > 0 ? minutes / total : 0;
            slices.Add(new PieSlice(name, minutes, share, MinutesText(minutes), ShareText(share), color, PieSliceKind.Name, 1));
        }
        return new PieChartData("Czas postojów według rodzaju",
            "Udział błędów maszyn i postojów bez pracy w łącznym czasie postojów (z długimi postojami). "
            + "Błąd maszyny to każdy czerwony alert, także nierozpoznany.",
            slices, total, MinutesText(total));
    }

    private static string ShareText(double share) => (share * 100).ToString("N1", Pl) + " %";

    private static (PieChartData Pie, IReadOnlyDictionary<string, int> SliceOfName) BuildErrors(IReadOnlyList<DowntimeSlice> errorRows)
    {
        var groups = errorRows.GroupBy(DowntimePresentation.ChartErrorName, StringComparer.Ordinal)
            .Select(g => (name: g.Key, minutes: g.Sum(s => s.Minutes)))
            .Where(g => g.minutes > 0).ToArray();
        double total = groups.Sum(g => g.minutes);
        var named = groups.Where(g => g.name != DowntimePresentation.UnrecognizedName)
            .OrderByDescending(g => g.minutes).ThenBy(g => g.name, StringComparer.Ordinal).ToArray();
        var slices = new List<PieSlice>();
        var sliceOfName = new Dictionary<string, int>(StringComparer.Ordinal);
        PieSlice Slice(string label, double minutes, ChartColor color, PieSliceKind kind, int count)
        {
            double share = total > 0 ? minutes / total : 0;
            return new PieSlice(label, minutes, share, MinutesText(minutes), ShareText(share), color, kind, count);
        }
        // Wycinek „Pozostałe” powstaje dopiero przy co najmniej DWÓCH nazwach ponad limit — jedna nazwa idzie wprost.
        int own = named.Length <= MaxPieNames + 1 ? named.Length : MaxPieNames;
        for (int i = 0; i < own; i++)
        {
            sliceOfName[named[i].name] = slices.Count;
            slices.Add(Slice(named[i].name, named[i].minutes, ChartPalette.Slot(i), PieSliceKind.Name, 1));
        }
        double unrecognized = groups.Where(g => g.name == DowntimePresentation.UnrecognizedName).Sum(g => g.minutes);
        if (unrecognized > 0)
        {
            sliceOfName[DowntimePresentation.UnrecognizedName] = slices.Count;
            slices.Add(Slice(DowntimePresentation.UnrecognizedName, unrecognized, ChartPalette.Unrecognized, PieSliceKind.Unrecognized, 1));
        }
        if (named.Length > own)
        {
            var rest = named[own..];
            foreach (var group in rest) sliceOfName[group.name] = slices.Count;
            slices.Add(Slice($"{DowntimePresentation.RemainderName} ({rest.Length} {NamesWord(rest.Length)})",
                rest.Sum(g => g.minutes), ChartPalette.Remainder, PieSliceKind.Remainder, rest.Length));
        }
        var pie = new PieChartData("Błędy maszyn według nazwy",
            "Udział nazw błędów w czasie błędów maszyn — bez postojów bez pracy, z długimi postojami; „Nierozpoznany” — błędy, "
            + "których nazwy nie rozpoznano.",
            slices, total, MinutesText(total))
        {
            EmptyText = "W wybranym zakresie nie ma błędów maszyn — są tylko postoje bez pracy albo nie ma postojów.",
        };
        return (pie, sliceOfName);
    }

    /// <summary>
    /// Błędy maszyn według maszyny (bez postojów bez pracy): części słupka = pozycje wykresu kołowego błędów (te same nazwy
    /// i kolory, „Pozostałe” i „Nierozpoznany” też), kolejność maszyn malejąco po czasie błędów. Pokazuje, która maszyna ma
    /// najwięcej awarii, nie tylko najwięcej stania.
    /// </summary>
    private static BarChartData BuildErrorMachines(IReadOnlyList<DowntimeSlice> errorRows, List<ReportMachine> machines,
        PieChartData pie, IReadOnlyDictionary<string, int> sliceOfName)
    {
        var byMachine = errorRows.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToArray());
        int sliceCount = pie.Slices.Count;
        var stats = machines.Select((machine, index) =>
        {
            var values = new double[sliceCount];
            var longValues = new double[sliceCount];
            if (byMachine.TryGetValue(machine.Key, out var mine))
                foreach (var row in mine)
                {
                    if (!sliceOfName.TryGetValue(DowntimePresentation.ChartErrorName(row), out int slice)) continue;
                    values[slice] += row.Minutes;
                    if (row.IsLong) longValues[slice] += row.Minutes;
                }
            return (machine, index, values, longValues, total: values.Sum());
        }).OrderByDescending(s => s.total).ThenBy(s => s.index).ToArray();
        var series = pie.Slices.Select((slice, k) => new ChartSeries(slice.Label, slice.Color,
            stats.Select(s => new ChartPoint(s.machine.Name, s.values[k], MinutesText(s.values[k]), s.longValues[k])).ToArray())).ToArray();
        return new BarChartData("Błędy maszyn według maszyny",
            "Minuty błędów maszyn w wybranym zakresie, bez postojów bez pracy. Części słupka to nazwy błędów — te same kolory, "
            + "co na wykresie kołowym błędów. Kreskowanie w części: długie postoje (> 1 h).",
            "Minuty błędów maszyn", stats.Select(s => s.machine.Name).ToArray(), series, stats.Select(s => s.total).ToArray(),
            stats.Select(s => MinutesText(s.total)).ToArray())
        {
            EmptyText = "W wybranym zakresie nie ma błędów maszyn — słupki są puste.",
        };
    }

    private static string NamesWord(int count) =>
        count == 1 ? "nazwa" : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? "nazwy" : "nazw";

    private static LineChartData BuildDays(IReadOnlyList<DowntimeSlice> rows, IReadOnlyList<DowntimeSlice> errorRows,
        List<ReportMachine> machines, List<DateOnly> days, bool perMachine, bool truncated, bool includeIdle)
    {
        ChartPoint[] PointsOf(IEnumerable<DowntimeSlice> source)
        {
            var byDay = source.GroupBy(s => s.Day).ToDictionary(g => g.Key, g => g.Sum(s => s.Minutes));
            return days.Select(day =>
            {
                double value = byDay.TryGetValue(day, out var minutes) ? minutes : 0;
                return new ChartPoint(day.ToString("dd.MM", Pl), value, MinutesText(value));
            }).ToArray();
        }
        var lineRows = includeIdle ? rows : errorRows;
        string sumName = includeIdle ? AllName : ErrorsName;
        var series = new List<ChartSeries>();
        if (!perMachine)
        {
            if (includeIdle)
            {
                series.Add(new ChartSeries(AllName, ChartPalette.Slot(2), PointsOf(rows)));
                series.Add(new ChartSeries(WithoutLongName, ChartPalette.Slot(1), PointsOf(rows.Where(s => !s.IsLong))));
                series.Add(new ChartSeries(ErrorsName, ChartPalette.Errors, PointsOf(errorRows)));
            }
            else
            {
                series.Add(new ChartSeries(ErrorsName, ChartPalette.Errors, PointsOf(errorRows)));
                series.Add(new ChartSeries(ErrorsWithoutLongName, ChartPalette.Slot(1), PointsOf(errorRows.Where(s => !s.IsLong))));
            }
        }
        else
        {
            var names = machines.ToDictionary(m => m.Key, m => m.Name);
            var ranked = lineRows.GroupBy(KeyOf).Select(g => (key: g.Key, rows: g.ToArray(), total: g.Sum(s => s.Minutes)))
                .Where(g => g.total > 0)
                .OrderByDescending(g => g.total).ThenBy(g => names.TryGetValue(g.key, out var n) ? n : g.key.Value, StringComparer.Ordinal)
                .ToArray();
            int own = ranked.Length <= MaxLineMachines + 1 ? ranked.Length : MaxLineMachines;
            for (int i = 0; i < own; i++)
                series.Add(new ChartSeries(names.TryGetValue(ranked[i].key, out var name) ? name : ranked[i].rows[0].Machine,
                    ChartPalette.Slot(i), PointsOf(ranked[i].rows)));
            if (ranked.Length > own)
                series.Add(new ChartSeries(OtherMachinesName, ChartPalette.Remainder, PointsOf(ranked[own..].SelectMany(g => g.rows))));
            if (series.Count == 0) series.Add(new ChartSeries(sumName, ChartPalette.Slot(2), PointsOf(lineRows)));
        }
        string? note = truncated
            ? $"Zakres jest dłuższy niż {MaxLineDays} dni roboczych — wykres pokazuje najnowsze {days.Count}; słupki i udziały liczą cały zakres."
            : null;
        string subtitle = perMachine
            ? (includeIdle ? "Każda linia — jedna maszyna (wszystkie postoje, z długimi). Soboty i niedziele pominięte."
                : "Każda linia — jedna maszyna (same błędy maszyn, z długimi). Soboty i niedziele pominięte.")
            : (includeIdle ? "Suma wszystkich maszyn: wszystkie postoje (z długimi i bez nich) oraz osobno same błędy maszyn. Soboty i niedziele pominięte."
                : "Suma błędów maszyn wszystkich maszyn (postoje bez pracy ukryte): z długimi postojami i bez nich. Soboty i niedziele pominięte.");
        return new LineChartData(includeIdle ? "Minuty postojów na dzień" : "Minuty błędów maszyn na dzień", subtitle,
            includeIdle ? "Minuty postojów" : "Minuty błędów maszyn", "Dzień zmiany", days, series, perMachine, note);
    }

    private static NoDataAnnotation BuildNoData(DowntimeChartOptions options, List<ReportMachine> machines,
        (DateOnly From, DateOnly To)? range)
    {
        var intervals = options.Observation!;
        var zone = options.Zone;
        var firstEntry = ObservationReport.FirstEntryAt(intervals);
        var lines = new List<string>();
        var entries = new List<NoDataEntry>();
        if (firstEntry is null)
        {
            lines.Add(NoConnectionName + ": dziennik czasu obserwacji nie ma jeszcze wpisów, więc tego czasu nie da się pokazać.");
            return new NoDataAnnotation(entries, lines);
        }
        if (range is not { } r || WorkingDaysBetween(r.From, r.To) == 0)
        {
            lines.Add(NoConnectionName + ": brak dni roboczych w wybranym zakresie.");
            return new NoDataAnnotation(entries, lines);
        }
        var start = WallInstant(r.From, zone);
        var end = r.To == DateOnly.MaxValue ? DateTimeOffset.MaxValue : WallInstant(r.To.AddDays(1), zone);
        if (end > options.GeneratedAt) end = options.GeneratedAt;
        var counted = CountedWindows(zone, options.Shift, intervals, start, end);
        var byMachine = intervals.Where(i => i is not null).GroupBy(i => i.MachineId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<ObservationInterval>)g.ToArray(), StringComparer.Ordinal);
        foreach (var machine in machines)
        {
            // Wiersz bez identyfikatora maszyny (klucz zastępczy z nazwy) — dziennik obserwacji go nie zna. Maszyna z danych grupy
            // spoza konfiguracji tego komputera MA identyfikator i jest liczona, jeśli dziennik (wymieniany w grupie) ją zna.
            if (machine.Key.IsFallback || !byMachine.TryGetValue(machine.Key.Value, out var mine)) continue;
            var summary = ObservationReport.Summarize(mine, machine.Key.Value, start, end, counted);
            if (summary.NoConnection > TimeSpan.Zero || summary.ScreenHidden > TimeSpan.Zero)
                entries.Add(new NoDataEntry(machine.Key.Value, machine.Name, summary.NoConnection, summary.ScreenHidden));
        }
        var noConnection = entries.Where(e => e.NoConnection > TimeSpan.Zero).ToArray();
        lines.Add(noConnection.Length == 0
            ? NoConnectionName + ": w wybranym zakresie nie zapisano (poza sumami postojów)."
            : NoConnectionName + " (poza sumami postojów): "
                + string.Join("; ", noConnection.Select(e => $"{e.Machine} — {DurationText(e.NoConnection)}")) + ".");
        var hidden = entries.Where(e => e.ScreenHidden > TimeSpan.Zero).ToArray();
        if (hidden.Length > 0)
            lines.Add(ScreenHiddenName + " (poza sumami postojów): "
                + string.Join("; ", hidden.Select(e => $"{e.Machine} — {DurationText(e.ScreenHidden)}")) + ".");
        if (firstEntry > start)
            lines.Add($"Dziennik czasu obserwacji prowadzony jest od {ReportCalendar.LocalWall(firstEntry.Value, zone):dd.MM.yyyy HH:mm}; "
                + "wcześniej czas bez połączenia nie jest znany.");
        return new NoDataAnnotation(entries, lines);
    }

    private static DowntimeChartHeader BuildHeader(DowntimeChartOptions options, (DateOnly From, DateOnly To)? fullRange, int machineCount)
    {
        int shiftsPerDay = options.Shift is null ? ShiftNames.Length : 1;
        int workingDays = fullRange is { } r ? WorkingDaysBetween(r.From, r.To) : 0;
        string range;
        if (fullRange is not { } full)
            range = options.From is { } f && options.To is { } t && f > t ? "zakres dat jest niepoprawny" : "brak dni roboczych w zakresie";
        else if (workingDays == 0)
            range = "brak dni roboczych w zakresie";
        else
        {
            var lastWorking = LastWorkingDay(full.To);
            range = $"{options.From ?? full.From:dd.MM.yyyy} – {options.To ?? lastWorking:dd.MM.yyyy}";
            if (options.From is null && options.To is null) range = "cała historia, " + range;
            // Zakres sięga w przyszłość: dane kończą się na dziś (RangeOf) — mówimy to wprost.
            if (options.To is { } chosenTo && lastWorking < DateOnly.MaxValue)
            {
                var next = lastWorking.AddDays(1);
                while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && next < DateOnly.MaxValue) next = next.AddDays(1);
                if (next <= chosenTo) range += $", dane do {lastWorking:dd.MM.yyyy}";
            }
        }
        var generated = ReportCalendar.LocalWall(options.GeneratedAt, options.Zone);
        long shifts = (long)workingDays * shiftsPerDay;
        string text = $"Zakres: {range} (bez weekendów) · Zmiana: {options.Shift ?? "wszystkie"} · "
            + $"Zmian w zakresie: {shifts} · Maszyn: {machineCount} · Wygenerowano: {generated:dd.MM.yyyy HH:mm}";
        return new DowntimeChartHeader(options.From, options.To, options.Shift, workingDays, (int)Math.Min(shifts, int.MaxValue),
            machineCount, options.GeneratedAt, range, text);
    }

    /// <summary>Ostatni dzień roboczy nie później niż <paramref name="day"/> (gdy zakres ma dni robocze, cofnięcie ≤ 2 dni).</summary>
    private static DateOnly LastWorkingDay(DateOnly day)
    {
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && day > DateOnly.MinValue) day = day.AddDays(-1);
        return day;
    }

    /// <summary>Początek dnia zmian (06:00 zegara ściennego strefy) jako chwila bezwzględna.</summary>
    private static DateTimeOffset WallInstant(DateOnly day, TimeZoneInfo zone) =>
        ReportCalendar.NextInstant(day.ToDateTime(new TimeOnly(ReportCalendar.ShiftDayStartHour, 0)), zone, DateTimeOffset.MinValue);

    internal static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ShiftParts(DateTimeOffset start, DateTimeOffset end,
        string? shift, TimeZoneInfo zone)
    {
        if (shift is null)
        {
            if (start < end) yield return (start, end);
            yield break;
        }
        var cursor = start;
        while (cursor < end)
        {
            var local = ReportCalendar.LocalWall(cursor, zone);
            int hour = local.Hour;
            string name = hour is >= 6 and < 14 ? ShiftNames[0] : hour is >= 14 and < 22 ? ShiftNames[1] : ShiftNames[2];
            var nextLocal = ReportCalendar.AddWall(local.Date,
                TimeSpan.FromHours(hour < 6 ? 6 : hour < 14 ? 14 : hour < 22 ? 22 : 30));
            var boundary = ReportCalendar.NextInstant(nextLocal, zone, cursor);
            var until = end < boundary ? end : boundary;
            if (name == shift) yield return (cursor, until);
            cursor = until;
        }
    }
}
