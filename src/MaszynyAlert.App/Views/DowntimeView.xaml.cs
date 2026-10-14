using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using MaszynyAlert.Export;

namespace MaszynyAlert.App.Views;

public partial class DowntimeView : System.Windows.Controls.UserControl
{
    private readonly AppOrchestrator _orchestrator;
    private readonly IUserMessages _messages;
    private IReadOnlyList<DowntimeSlice> _slices = [];
    // Wybór z ostatniego odświeżenia — eksport PDF/Excel opisuje dokładnie ten okres i zmianę, które widać w tabeli.
    private DateOnly? _selectedFrom;
    private DateOnly? _selectedTo;
    private string? _selectedShift;

    /// <summary>
    /// Wykresy liczą się najwyżej co tyle (Panel odświeża raport co sekundę); zmiana filtra liczy je od razu.
    /// Rysowane są od nowa tylko przy zmianie treści (<see cref="DowntimeChartSet.ContentKey"/>).
    /// </summary>
    private static readonly TimeSpan ChartsInterval = TimeSpan.FromSeconds(5);

    /// <summary>Kopia dziennika obserwacji dla adnotacji „brak danych” — odświeżana najwyżej co tyle.</summary>
    private static readonly TimeSpan ObservationRefreshInterval = TimeSpan.FromSeconds(30);

    private string? _chartsFilterKey;
    private string? _chartsContentKey;
    private DateTimeOffset _chartsAt;
    private IReadOnlyList<ObservationInterval>? _observation;
    private DateTimeOffset _observationAt;

    /// <summary>
    /// Prawda po <c>InitializeComponent</c>. Zdarzenia kontrolek (np. <c>Checked</c> pola „Pokaż postoje bez pracy” z <c>IsChecked="True"</c>)
    /// odpalają się już w trakcie wczytywania XAML-a, gdy kontrolki niżej jeszcze nie istnieją — <see cref="Refresh"/> wtedy nic nie robi.
    /// Konstruktor widoku powstaje przy starcie programu i nie może rzucać (okno błędu blokowałoby <c>--stop</c>).
    /// </summary>
    private readonly bool _initialized;

    /// <param name="messages">Dymek o wyniku zapisu raportu; <see langword="null"/> = <see cref="DesktopUserMessages"/>.</param>
    public DowntimeView(AppOrchestrator orchestrator, IUserMessages? messages = null)
    {
        _orchestrator = orchestrator;
        _messages = messages ?? DesktopUserMessages.Instance;
        InitializeComponent();
        FromDate.SelectedDate = DateTime.Today.AddDays(-6);
        ToDate.SelectedDate = DateTime.Today;
        _initialized = true;
        Loaded += (_, _) => Refresh();
    }
    private const string GroupEmptyText =
        "W danych grupy nie ma postojów (czerwonych alertów ani postojów bez pracy) dla wybranego okresu i zmiany "
        + "(weekend od soboty 06:00 do poniedziałku 06:00 nie wchodzi do raportu). "
        + "Nie znaczy to, że postoju nie było: mógł wystąpić wtedy, gdy żaden komputer grupy nie obserwował maszyn, "
        + "albo pochodzić od komputera, który jeszcze nie uzupełnił raportu.";

    private sealed record BuildInputs(DowntimeJournal Journal, long Version, RuleSet Rules, MachineConfig[] Machines,
        TimeZoneInfo Zone)
    {
        public bool SameAs(BuildInputs other) => ReferenceEquals(Journal, other.Journal) && Version == other.Version
            && ReferenceEquals(Rules, other.Rules) && Machines.SequenceEqual(other.Machines)
            && ReferenceEquals(Zone, other.Zone);
    }

    /// <summary>
    /// Raport ze WSZYSTKICH epizodów (przed filtrem okresu i zmiany), z czego powstał i czy zależy od chwili „teraz”
    /// (<see cref="DowntimeReport.DependsOnNow"/>; <see cref="LatestEnd"/> — najpóźniejszy koniec epizodu migawki).
    /// </summary>
    private sealed record BuiltReport(BuildInputs Inputs, IReadOnlyList<DowntimeSlice> Rows, DateTimeOffset LatestEnd, bool DependsOnNow);

    private BuiltReport? _built;

    /// <summary>Okres i zmiana, z którymi powstały <see cref="_slices"/>; <see langword="null"/> — tabele jeszcze nic nie pokazały.</summary>
    private (DateOnly From, DateOnly To, string? Shift)? _shownFilter;

    /// <summary>Strefa, w której tabela pokazuje godziny (<c>ToLocalTime</c>) — inna strefa to inne napisy przy tych samych wierszach.</summary>
    private TimeZoneInfo? _shownZone;

    /// <summary>Zmiana kilku pól filtra naraz („Cała historia”) — jedno przeliczenie na końcu, nie po każdym polu.</summary>
    private bool _settingFilter;

    public void Refresh()
    {
        if (SummaryGrid is null || !_initialized) return;
        UpdateScope();
        string? shift = ShiftFilter.SelectedIndex > 0 ? (string)((ComboBoxItem)ShiftFilter.SelectedItem).Content : null;
        var from = FromDate.SelectedDate is { } f ? DateOnly.FromDateTime(f) : DateOnly.MinValue;
        var to = ToDate.SelectedDate is { } t ? DateOnly.FromDateTime(t) : DateOnly.MaxValue;
        _selectedFrom = FromDate.SelectedDate is { } sf ? DateOnly.FromDateTime(sf) : null;
        _selectedTo = ToDate.SelectedDate is { } st ? DateOnly.FromDateTime(st) : null;
        _selectedShift = shift;
        bool rebuilt = EnsureBuilt();
        if (rebuilt || _shownFilter != (from, to, shift))
        {
            var previous = _slices;
            _slices = _built!.Rows.Where(s => s.Day >= from && s.Day <= to && (shift is null || s.Shift == shift)).ToArray();
            // Te same wiersze co na ekranie (np. zmienił się epizod spoza okresu): karty i tabele zostają, a z nimi sortowanie,
            // przewinięcie i zaznaczenie użytkownika.
            if (_shownFilter is null || !ReferenceEquals(_shownZone, _built.Inputs.Zone) || !_slices.SequenceEqual(previous)) ShowRows();
            _shownFilter = (from, to, shift);
            _shownZone = _built.Inputs.Zone;
        }
        RefreshCharts(FromDate.SelectedDate is { } chartFrom ? DateOnly.FromDateTime(chartFrom) : null,
            ToDate.SelectedDate is { } chartTo ? DateOnly.FromDateTime(chartTo) : null, shift);
        if (_orchestrator.GroupReportMode) EmptyText.Text = GroupEmptyText;
        EmptyText.Visibility = _slices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Wyłącznie sytuacje bieżące. Informacja o lokalnym zakresie raportu stoi na stałe w XAML,
        // więc błąd dziennika ani zły zakres dat nie mogą jej zastąpić.
        string? warning = from > to
            ? "Data końcowa jest wcześniejsza niż początkowa. Popraw zakres, aby zobaczyć dane."
            : CurrentWarnings(_orchestrator.Downtime.Error, _orchestrator.HistoryError);
        ReportWarning.Text = warning ?? string.Empty;
        ReportWarningBox.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool EnsureBuilt()
    {
        var journal = _orchestrator.Downtime;
        var config = _orchestrator.Config;
        var inputs = new BuildInputs(journal, journal.Version, _orchestrator.Help.Rules, [.. config.Machines],
            TimeZoneInfo.Local);
        var checkedAt = _orchestrator.Clock.Now;
        if (_built is { } built && built.Inputs.SameAs(inputs) && !built.DependsOnNow
            && !DowntimeReport.DependsOnNow(built.LatestEnd, checkedAt))
            return false;
        var episodes = journal.Snapshot();
        var classifier = new DowntimeCategoryClassifier(inputs.Rules, inputs.Machines);
        var rows = DowntimeReport.Build(episodes, inputs.Zone, classifier, now: checkedAt);
        var latestEnd = episodes.Count == 0 ? DateTimeOffset.MinValue : episodes.Max(e => e.End);
        _built = new BuiltReport(inputs, rows, latestEnd, DowntimeReport.DependsOnNow(latestEnd, checkedAt));
        return true;
    }

    /// <summary>Karty sum i obie tabele z <see cref="_slices"/>.</summary>
    private void ShowRows()
    {
        var totals = DowntimeReport.Totals(_slices);
        double minutes = totals.All.Minutes;
        double minutesWithoutLong = totals.WithoutLong.Minutes;
        TotalTime.Text = HoursText(minutes);
        TotalTimeWithoutLong.Text = "Bez długich postojów: " + HoursText(minutesWithoutLong);
        TotalMinutes.Text = minutes.ToString("N1");
        TotalMinutesWithoutLong.Text = "Bez długich postojów: " + minutesWithoutLong.ToString("N1");
        // Postój podzielony na kilka zmian albo dni liczy się raz (Totals liczy po identyfikatorze epizodu).
        EpisodeCount.Text = totals.All.Episodes.ToString();
        EpisodeCountLong.Text = $"W tym długich postojów (> 1 h): {totals.All.Episodes - totals.WithoutLong.Episodes}";
        var kinds = DowntimeReport.ByKind(_slices);
        TotalTimeByKind.Text = $"W tym błędy maszyn: {HoursText(kinds.Errors.All.Minutes)} · bez pracy: {HoursText(kinds.IdleMinutes)}";
        TotalMinutesByKind.Text = $"W tym błędy maszyn: {kinds.Errors.All.Minutes:N1} · bez pracy: {kinds.IdleMinutes:N1}";
        EpisodeCountByKind.Text = $"W tym błędów maszyn: {kinds.Errors.All.Episodes} · bez pracy: {kinds.IdleEpisodes}";
        SummaryRow Row(DateOnly day, string label, IEnumerable<DowntimeSlice> rows)
        {
            var array = rows as DowntimeSlice[] ?? rows.ToArray();
            double duration = array.Sum(s => s.Minutes);
            return new(day.ToString("dd.MM.yyyy"), label, duration, duration / 60,
                array.Where(s => !s.IsIdle).Sum(s => s.Minutes), minutes > 0 ? duration / minutes * 100 : 0);
        }
        SetRows(SummaryGrid, _slices.GroupBy(s => s.Day).OrderByDescending(g => g.Key)
            .SelectMany(day => new[] { Row(day.Key, "Razem w dniu", day) }
                .Concat(day.GroupBy(s => s.Shift).OrderBy(g => g.Key)
                    .Select(g => Row(day.Key, g.Key, g)))).ToArray());
        SetRows(EpisodesGrid, _slices.OrderByDescending(s => s.Start).Select(s => new
        {
            Day = s.Day.ToString("dd.MM.yyyy"), s.Shift, s.Machine, s.Reason, Minutes = s.Minutes,
            StartText = s.Start.ToLocalTime().ToString("dd.MM HH:mm:ss"),
            EndText = s.End.ToLocalTime().ToString("dd.MM HH:mm:ss"),
            State = StateText(s)
        }).ToArray());
    }

    private static void SetRows(System.Windows.Controls.DataGrid grid, System.Collections.IEnumerable rows)
    {
        var sort = grid.Items.SortDescriptions.ToArray();
        var directions = grid.Columns.Select(c => c.SortDirection).ToArray();
        grid.ItemsSource = rows;
        if (sort.Length == 0) return;
        using (grid.Items.DeferRefresh())
        {
            grid.Items.SortDescriptions.Clear();
            foreach (var description in sort) grid.Items.SortDescriptions.Add(description);
        }
        for (int i = 0; i < directions.Length; i++) grid.Columns[i].SortDirection = directions[i];
    }

    private void RefreshCharts(DateOnly? from, DateOnly? to, string? shift)
    {
        var now = DateTimeOffset.Now;
        bool perMachine = LinePerMachine.IsChecked == true;
        bool includeIdle = IncludeIdleCharts.IsChecked != false;
        string filterKey = $"{from}|{to}|{shift}|{perMachine}|{includeIdle}|{_orchestrator.GroupReportMode}";
        if (filterKey == _chartsFilterKey && now >= _chartsAt && now - _chartsAt < ChartsInterval) return;
        _chartsFilterKey = filterKey;
        _chartsAt = now;
        if (_observation is null || now < _observationAt || now - _observationAt > ObservationRefreshInterval)
        {
            _observation = _orchestrator.Observation.Snapshot();
            _observationAt = now;
        }
        var charts = DowntimeCharts.Build(_slices, new DowntimeChartOptions
        {
            From = from, To = to, Shift = shift, LinePerMachine = perMachine, IncludeIdle = includeIdle, GeneratedAt = now,
            Zone = TimeZoneInfo.Local,
            Machines = _orchestrator.Config.Machines.Where(m => m.Enabled).Select(m => new ChartMachine(m.Id, m.Name)).ToArray(),
            Observation = _observation,
        });
        ChartsHeader.Text = charts.Header.Text;
        string contentKey = charts.ContentKey();
        if (contentKey == _chartsContentKey) return;
        _chartsContentKey = contentKey;
        MachinesChartTitle.Text = charts.Machines.Title;
        MachinesChartSubtitle.Text = charts.Machines.Subtitle;
        MachinesChart.Data = charts.Machines;
        NoDataText.Text = charts.NoData is { } noData ? string.Join(Environment.NewLine, noData.Lines) : string.Empty;
        NoDataText.Visibility = NoDataText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        KindsChartTitle.Text = charts.Kinds.Title;
        KindsChartSubtitle.Text = charts.Kinds.Subtitle;
        KindsChart.Data = charts.Kinds;
        ErrorsChartTitle.Text = charts.Errors.Title;
        ErrorsChartSubtitle.Text = charts.Errors.Subtitle;
        ErrorsChart.Data = charts.Errors;
        ErrorMachinesChartTitle.Text = charts.ErrorMachines.Title;
        ErrorMachinesChartSubtitle.Text = charts.ErrorMachines.Subtitle;
        ErrorMachinesChart.Data = charts.ErrorMachines;
        DaysChartTitle.Text = charts.Days.Title;
        DaysChartSubtitle.Text = charts.Days.Subtitle;
        DaysChart.Data = charts.Days;
        DaysChartNote.Text = charts.Days.Note ?? string.Empty;
        DaysChartNote.Visibility = charts.Days.Note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LinePerMachine_Changed(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Przełącznik „Pokaż postoje bez pracy na wykresach” — stan widoku (nie konfiguracja), liczy wykresy od razu.</summary>
    private void IncludeIdleCharts_Changed(object sender, RoutedEventArgs e) => Refresh();

    private static string HoursText(double minutes) => $"{(int)(minutes / 60)} godz. {(int)(minutes % 60)} min";

    private static string StateText(DowntimeSlice slice)
    {
        string state = slice.Interrupted ? "Przerwana obserwacja" : slice.Ongoing ? "Otwarty epizod" : "Zakończony";
        return slice.IsLong ? state + ", " + LongDowntimeMark : state;
    }

    private const string LongDowntimeMark = "długi postój (> 1 h)";

    private static string? CurrentWarnings(params string?[] warnings)
    {
        string[] present = warnings.Where(w => !string.IsNullOrEmpty(w)).Select(w => w!).ToArray();
        return present.Length == 0 ? null : string.Join(Environment.NewLine, present);
    }
    /// <summary>
    /// Przełącza oba stałe bloki zakresu raportu. Kod dotyka WYŁĄCZNIE ich
    /// <see cref="UIElement.Visibility"/> i zmiennej listy komputerów — same napisy stoją na stałe w XAML,
    /// dokładnie jeden blok jest widoczny, więc raportu nie da się pokazać bez informacji o jego zakresie.
    /// </summary>
    private void UpdateScope()
    {
        bool group = _orchestrator.GroupReportMode;
        LocalScopeBox.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
        GroupScopeBox.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        if (group)
        {
            string name = _orchestrator.GroupReportName;
            GroupScopeSources.Text = (name.Length > 0 ? $"Grupa „{name}”. " : string.Empty)
                + _orchestrator.GroupReportSourcesText;
        }
        ExportButton.Content = group ? "Eksportuj raport grupy CSV" : "Eksportuj lokalny raport CSV";
    }

    private void Filter_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_settingFilter) Refresh();
    }

    private void Pdf_Click(object sender, RoutedEventArgs e) =>
        _ = ExportAsync("Raport PDF|*.pdf", "pdf", "PDF", ReportFileWriter.SavePdf);

    private void Excel_Click(object sender, RoutedEventArgs e) =>
        _ = ExportAsync("Skoroszyt Excel|*.xlsx", "xlsx", "Excel", ReportFileWriter.SaveExcel);

    private async Task ExportAsync(string filter, string extension, string format, Func<DowntimeExportData, string, ExportOutcome> save)
    {
        DowntimeExportData data;
        try
        {
            data = DowntimeExportData.Create(_slices, _selectedFrom, _selectedTo, _selectedShift,
                _orchestrator.Observation.Snapshot(), _orchestrator.Config.Machines, TimeZoneInfo.Local, DateTimeOffset.Now,
                Environment.MachineName, ScopeText(), linePerMachine: LinePerMachine.IsChecked == true,
                includeIdle: IncludeIdleCharts.IsChecked != false);
        }
        catch (Exception ex)
        {
            _messages.Notify(NotificationKind.Error, "Nie udało się przygotować raportu", ex.Message);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = data.FileName(extension), DefaultExt = extension };
        if (dialog.ShowDialog() != true) return;
        string path = dialog.FileName;
        SetExportBusy(true);
        ExportOutcome outcome;
        try
        {
            outcome = await Task.Run(() => save(data, path));
        }
        catch (Exception ex)
        {
            outcome = ExportOutcome.Fail(ex.Message);
        }
        finally
        {
            SetExportBusy(false);
        }
        if (outcome.Success) NotifySaved(format, path, data.Episodes.Count);
        else _messages.Notify(NotificationKind.Error, SaveFailedTitle, outcome.Error);
    }

    /// <summary>Pierwszy wiersz dymka po nieudanym zapisie raportu (CSV, PDF, Excel).</summary>
    internal const string SaveFailedTitle = "Nie udało się zapisać raportu";

    /// <summary>Pierwszy wiersz dymka po zapisie raportu: „Zapisano raport PDF” / „… Excel” / „… CSV”.</summary>
    internal static string SavedTitle(string format) => "Zapisano raport " + format;

    private void NotifySaved(string format, string path, int rows) =>
        _messages.Notify(NotificationKind.Success, SavedTitle(format), $"{Path.GetFileName(path)} · wiersze: {rows}", path);

    private void SetExportBusy(bool busy)
    {
        ExportButton.IsEnabled = !busy;
        PdfButton.IsEnabled = !busy;
        ExcelButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    /// <summary>
    /// Zakres danych do nagłówka PDF i arkusza „Informacje” — ta sama informacja co blok nad raportem. Tekst żyje w Core:
    /// napisy bloków zakresu nie mogą stać w kodzie widoku (<c>DowntimeReportScopeTests</c>).
    /// </summary>
    private string ScopeText() =>
        DowntimeExportData.ScopeTextFor(_orchestrator.GroupReportMode, _orchestrator.GroupReportName);
    /// <param name="ErrorMinutes">Minuty samych błędów maszyn w wierszu (bez postojów bez pracy) — kolumna „W tym błędy maszyn”.</param>
    private sealed record SummaryRow(string Day, string Shift, double Minutes, double Hours, double ErrorMinutes, double Share);
    private void All_Click(object sender, RoutedEventArgs e)
    {
        // Trzy pola filtra, jedno przeliczenie.
        _settingFilter = true;
        try { FromDate.SelectedDate = null; ToDate.SelectedDate = null; ShiftFilter.SelectedIndex = 0; }
        finally { _settingFilter = false; }
        Refresh();
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        // „lokalny”/„grupa” w nazwie pliku: eksport opuszcza program i bywa czytany bez panelu, w którym
        // stoi informacja o zakresie — sama nazwa musi więc mówić, czyje to obserwacje.
        string scope = _orchestrator.GroupReportMode ? "grupa" : "lokalny";
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Raport CSV|*.csv", FileName = $"VNC Alert-postoje-{scope}-{DateTime.Today:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog() != true) return;
        static string Cell(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "Dzień zmiany;Zmiana;Maszyna;Powód;Od;Do;Minuty;Stan" };
        foreach (var s in _slices)
            lines.Add(string.Join(';', new[] { s.Day.ToString("yyyy-MM-dd"), s.Shift, s.Machine, s.Reason,
                s.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), s.End.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                s.Minutes.ToString("F2", CultureInfo.GetCultureInfo("pl-PL")),
                StateText(s) }.Select(Cell)));
        try
        {
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            NotifySaved("CSV", dialog.FileName, _slices.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _messages.Notify(NotificationKind.Error, SaveFailedTitle, ex.Message); }
    }
}
