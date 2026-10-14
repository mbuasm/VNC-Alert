using System.Globalization;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes.Charts;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace MaszynyAlert.Export;

public static class DowntimePdfExporter
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    private static readonly Color Ink = new(0xFF1F1F1E);
    private static readonly Color Muted = new(0xFF5F5E5A);
    private static readonly Color Rule = new(0xFFD9D8D3);
    private static readonly Color HeaderFill = new(0xFFEFEEEA);
    private static readonly Color StripeFill = new(0xFFF8F7F4);
    private static readonly Color CardFill = new(0xFFF4F3EF);
    private static readonly Color Grid = new(0xFFE3E2DD);

    private static readonly Unit SideMargin = Unit.FromCentimeter(1.8);
    private static readonly Unit ContentWidth = Unit.FromCentimeter(26.1);

    private static readonly object RenderGate = new();

    /// <summary>Tworzy PDF w <paramref name="output"/> (strumień zostaje otwarty). Błąd = wynik z komunikatem, bez wyjątku.</summary>
    public static ExportOutcome Write(DowntimeExportData data, Stream output)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(output);
        if (ExportFontResolver.EnsureRegistered() is { } fontError) return ExportOutcome.Fail(fontError);
        lock (RenderGate)
        try
        {
            var renderer = new PdfDocumentRenderer { Document = Build(data) };
            renderer.RenderDocument();
            renderer.PdfDocument.Info.Creator = "VNC Alert";
            renderer.PdfDocument.Save(output, false);
            return ExportOutcome.Ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ExportOutcome.Fail("Nie udało się utworzyć pliku PDF. " + ex.Message);
        }
    }

    /// <summary>Dokument MigraDoc (bez renderowania) — osobno, żeby test mógł sprawdzić strukturę.</summary>
    internal static Document Build(DowntimeExportData data)
    {
        var document = new Document();
        document.Info.Title = DowntimeExportData.Title;
        document.Info.Subject = $"{DowntimeExportData.Title} {RangeText(data)}";
        document.Info.Author = "VNC Alert";
        DefineStyles(document);

        var section = document.AddSection();
        var page = section.PageSetup;
        page.PageFormat = PageFormat.A4;
        page.Orientation = Orientation.Landscape;
        page.LeftMargin = SideMargin;
        page.RightMargin = SideMargin;
        page.TopMargin = Unit.FromCentimeter(1.9);
        page.BottomMargin = Unit.FromCentimeter(1.7);
        page.HeaderDistance = Unit.FromCentimeter(0.9);
        page.FooterDistance = Unit.FromCentimeter(0.8);

        AddHeaderAndFooter(section, data);
        AddTitlePage(section, data);
        AddCharts(section, data);
        AddEpisodeTable(section, data);
        return document;
    }

    private static void DefineStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = ExportFontResolver.FamilyName;
        normal.Font.Size = 9;
        normal.Font.Color = Ink;

        var title = document.Styles[StyleNames.Heading1]!;
        title.Font.Size = 24;
        title.Font.Bold = true;
        title.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var heading = document.Styles[StyleNames.Heading2]!;
        heading.Font.Size = 14;
        heading.Font.Bold = true;
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(14);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        heading.ParagraphFormat.KeepWithNext = true;

        var small = document.Styles.AddStyle("Small", StyleNames.Normal);
        small.Font.Size = 8;
        small.Font.Color = Muted;
    }

    private static void AddHeaderAndFooter(Section section, DowntimeExportData data)
    {
        var header = section.Headers.Primary.AddParagraph();
        header.Style = "Small";
        header.Format.Alignment = ParagraphAlignment.Right;
        header.AddText($"{DowntimeExportData.Title} · {RangeText(data)} · {data.ShiftText}");

        var footer = section.Footers.Primary.AddParagraph();
        footer.Style = "Small";
        footer.Format.TabStops.AddTabStop(ContentWidth, TabAlignment.Right);
        footer.AddText($"{DowntimeExportData.CreatedBy} · wygenerowano {data.GeneratedAt.ToLocalTime():dd.MM.yyyy HH:mm} · komputer {data.ComputerName}");
        footer.AddTab();
        footer.AddText("Strona ");
        footer.AddPageField();
        footer.AddText(" z ");
        footer.AddNumPagesField();
    }

    private static void AddTitlePage(Section section, DowntimeExportData data)
    {
        section.AddParagraph(DowntimeExportData.Title, StyleNames.Heading1);
        var createdBy = section.AddParagraph(DowntimeExportData.CreatedBy);
        createdBy.Format.Font.Size = 13;
        createdBy.Format.Font.Color = Muted;
        createdBy.Format.SpaceAfter = Unit.FromPoint(16);

        var info = section.AddTable();
        info.Borders.Visible = false;
        info.AddColumn(Unit.FromCentimeter(4.2));
        info.AddColumn(ContentWidth - Unit.FromCentimeter(4.2));
        void Info(string label, string value)
        {
            var row = info.AddRow();
            row.TopPadding = Unit.FromPoint(2);
            row.BottomPadding = Unit.FromPoint(2);
            var name = row.Cells[0].AddParagraph(label);
            name.Format.Font.Color = Muted;
            row.Cells[1].AddParagraph(value).Format.Font.Bold = true;
        }
        Info("Zakres (dni zmian)", RangeText(data));
        Info("Zmiana", data.ShiftText);
        Info("Dane", data.ScopeText);
        Info("Wygenerowano", data.GeneratedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Pl));
        Info("Komputer", data.ComputerName);

        section.AddParagraph("Podsumowanie", StyleNames.Heading2);
        var cards = section.AddTable();
        cards.Borders.Visible = false;
        const int columns = 4;
        var gap = Unit.FromCentimeter(0.3);
        var cardWidth = (ContentWidth - gap * (columns - 1)) / columns;
        for (int i = 0; i < columns; i++)
        {
            cards.AddColumn(cardWidth);
            if (i < columns - 1) cards.AddColumn(gap);
        }
        var cardRow = cards.AddRow();
        int cell = 0;
        void Card(string label, string value, params string[] notes)
        {
            var target = cardRow.Cells[cell];
            cell += 2;
            target.Shading.Color = CardFill;
            target.Format.LeftIndent = Unit.FromPoint(8);
            target.Format.RightIndent = Unit.FromPoint(8);
            var caption = target.AddParagraph(label);
            caption.Format.SpaceBefore = Unit.FromPoint(8);
            caption.Format.Font.Color = Muted;
            var big = target.AddParagraph(value);
            big.Format.Font.Size = 20;
            big.Format.Font.Bold = true;
            big.Format.SpaceBefore = Unit.FromPoint(4);
            big.Format.SpaceAfter = Unit.FromPoint(4);
            foreach (var note in notes)
                target.AddParagraph(note).Style = "Small";
            if (target.Elements.LastObject is Paragraph last) last.Format.SpaceAfter = Unit.FromPoint(8);
        }
        Card("Łączny czas postojów — z długimi postojami (> 1 h)", HoursText(data.Totals.All.Minutes),
            MinutesText(data.Totals.All.Minutes),
            $"W tym {DowntimeCharts.ErrorsName.ToLower(Pl)}: {MinutesText(data.Kinds.Errors.All.Minutes)} · bez pracy: {MinutesText(data.Kinds.IdleMinutes)}");
        Card("Łączny czas postojów — bez długich postojów", HoursText(data.Totals.WithoutLong.Minutes),
            MinutesText(data.Totals.WithoutLong.Minutes),
            $"W tym {DowntimeCharts.ErrorsName.ToLower(Pl)}: {MinutesText(data.Kinds.Errors.WithoutLong.Minutes)}");
        Card("Liczba postojów", data.Totals.All.Episodes.ToString(Pl),
            $"W tym długich postojów (> 1 h): {data.LongEpisodes.ToString(Pl)}",
            $"W tym błędów maszyn: {data.Kinds.Errors.All.Episodes.ToString(Pl)} · bez pracy: {data.Kinds.IdleEpisodes.ToString(Pl)}");
        if (data.HasObservationJournal)
            Card(DowntimeExportData.NoConnectionLabel, HoursText(data.NoConnection.TotalMinutes),
                "Poza czasem postojów — osobna pozycja.",
                $"{DowntimeExportData.ScreenHiddenLabel}: {HoursText(data.ScreenHidden.TotalMinutes)}");
        else
            Card(DowntimeExportData.NoConnectionLabel, "—", "Program nie ma jeszcze zapisu czasu obserwacji dla tego okresu.");

        double total = data.Totals.All.Minutes;
        string Share(double minutes) => total > 0 ? (minutes / total * 100).ToString("N1", Pl) + " %" : "—";
        var kinds = section.AddParagraph();
        kinds.Format.SpaceBefore = Unit.FromPoint(10);
        kinds.AddFormattedText("Według rodzaju postoju: ", TextFormat.Bold);
        kinds.AddFormattedText($"{DowntimeCharts.ErrorsName} {Share(data.Kinds.Errors.All.Minutes)}", TextFormat.Bold);
        kinds.AddText($" ({MinutesText(data.Kinds.Errors.All.Minutes)}, postojów: {data.Kinds.Errors.All.Episodes.ToString(Pl)}) · "
            + $"{DowntimeCharts.IdleName} {Share(data.Kinds.Idle.All.Minutes)} ({MinutesText(data.Kinds.Idle.All.Minutes)}, postojów: {data.Kinds.Idle.All.Episodes.ToString(Pl)}) · "
            + $"{DowntimeCharts.IdleAtZeroName} {Share(data.Kinds.IdleAtZero.All.Minutes)} ({MinutesText(data.Kinds.IdleAtZero.All.Minutes)}, postojów: {data.Kinds.IdleAtZero.All.Episodes.ToString(Pl)}).");

        var notesParagraph = section.AddParagraph();
        notesParagraph.Style = "Small";
        notesParagraph.Format.SpaceBefore = Unit.FromPoint(10);
        notesParagraph.AddText("Postój to każdy czerwony alarm maszyny („błąd maszyny”) oraz postój bez pracy („Stoi bez pracy (100 %)”, „Stoi na 0 %”), bez względu na czas trwania; " +
            "sumy podano razem i w podziale na te rodzaje. Długi postój trwał ponad godzinę — " +
            "jest w raporcie oznaczony, a sumy podano z długimi postojami i bez nich. Weekend (od soboty 06:00 do poniedziałku 06:00) " +
            "nie wchodzi do raportu. Nocna zmiana należy do dnia jej rozpoczęcia. „Brak danych” to czas, gdy program nie miał " +
            "połączenia z maszyną albo nie widział jej ekranu — nie jest liczony jako postój.");
        if (data.ObservationKnownFrom is { } knownFrom)
            section.AddParagraph($"„Brak danych” jest znany od {knownFrom.ToLocalTime():dd.MM.yyyy HH:mm} — wcześniej program nie zapisywał czasu obserwacji.")
                .Style = "Small";
        section.AddParagraph("Raport pokazuje postoje zaobserwowane przez program; czas, gdy program był wyłączony, nie jest w nim widoczny.")
            .Style = "Small";

        if (data.Machines.Count > 0)
        {
            section.AddParagraph("Według maszyny", StyleNames.Heading2);
            var table = NewTable(section,
                ("Maszyna", 4.9, false), ("Z długimi postojami [min]", 3.4, true), ("Bez długich postojów [min]", 3.4, true),
                ("W tym błędy maszyn [min]", 3.0, true), ("Liczba postojów", 2.6, true), ("W tym długich", 2.4, true),
                ("W tym błędów", 2.2, true), ("Brak połączenia [min]", 2.1, true), ("Ekran niewidoczny [min]", 2.1, true));
            if (data.Machines.Count <= 20) table.Rows[0].KeepWith = data.Machines.Count;
            int index = 0;
            foreach (var machine in data.Machines)
                AddRow(table, index++, false, machine.Name, Number(machine.Totals.All.Minutes),
                    Number(machine.Totals.WithoutLong.Minutes), Number(machine.Kinds.Errors.All.Minutes),
                    machine.Totals.All.Episodes.ToString(Pl),
                    (machine.Totals.All.Episodes - machine.Totals.WithoutLong.Episodes).ToString(Pl),
                    machine.Kinds.Errors.All.Episodes.ToString(Pl),
                    data.HasObservationJournal && machine.ObservationKnown ? Number(machine.NoConnection.TotalMinutes) : "—",
                    data.HasObservationJournal && machine.ObservationKnown ? Number(machine.ScreenHidden.TotalMinutes) : "—");
        }
    }

    private static void AddCharts(Section section, DowntimeExportData data)
    {
        var heading = section.AddParagraph("Wykresy", StyleNames.Heading2);
        heading.Format.PageBreakBefore = true;
        if (data.Episodes.Count == 0)
        {
            section.AddParagraph("W wybranym okresie nie ma postojów — wykresy nie są rysowane.");
            return;
        }

        var charts = data.Charts;
        section.AddParagraph(charts.Header.Text).Style = "Small";
        var chartHeight = Unit.FromCentimeter(7.6);
        ChartPair(section, charts.Machines, charts.Kinds, chartHeight);
        if (charts.NoData is { Lines.Count: > 0 } noData)
            foreach (var line in noData.Lines)
                section.AddParagraph(line).Style = "Small";

        var days = charts.Days;
        var dayTitle = section.AddParagraph(days.Title);
        dayTitle.Format.Font.Bold = true;
        dayTitle.Format.SpaceBefore = Unit.FromPoint(12);
        dayTitle.Format.KeepWithNext = true;
        if (days.IsEmpty)
            section.AddParagraph(NoMinutes).Style = "Small";
        else if (days.Days.Count < 2)
            section.Add(DayColumns(days, ContentWidth, Unit.FromCentimeter(5.6)));
        else
            section.Add(LineChart(days, ContentWidth, Unit.FromCentimeter(5.6)));
        if (days.Note is { } note) section.AddParagraph(note).Style = "Small";

        // Warstwa „Błędy maszyn” (bez postojów bez pracy) — osobna strona, żeby błędy miały pełną skalę i własne miejsce.
        var errorsHeading = section.AddParagraph("Wykresy — błędy maszyn (bez postojów bez pracy)", StyleNames.Heading2);
        errorsHeading.Format.PageBreakBefore = true;
        section.AddParagraph(charts.Errors.Subtitle).Style = "Small";
        ChartPair(section, charts.ErrorMachines, charts.Errors, Unit.FromCentimeter(8.6));
    }

    /// <summary>Dwa wykresy obok siebie (słupkowy po lewej, kołowy po prawej) z tytułami z modelu; pusty = napis z modelu.</summary>
    private static void ChartPair(Section section, BarChartData bar, PieChartData pie, Unit height)
    {
        var layout = section.AddTable();
        layout.Borders.Visible = false;
        var half = (ContentWidth - Unit.FromCentimeter(0.6)) / 2;
        layout.AddColumn(half);
        layout.AddColumn(Unit.FromCentimeter(0.6));
        layout.AddColumn(half);
        var titles = layout.AddRow();
        titles.TopPadding = Unit.FromPoint(6);
        titles.Cells[0].AddParagraph(bar.Title).Format.Font.Bold = true;
        titles.Cells[2].AddParagraph(pie.Title).Format.Font.Bold = true;
        var chartRow = layout.AddRow();
        if (bar.IsEmpty) chartRow.Cells[0].AddParagraph(bar.EmptyText).Style = "Small";
        else chartRow.Cells[0].Add(BarChart(bar, half, height));
        if (pie.IsEmpty) chartRow.Cells[2].AddParagraph(pie.EmptyText).Style = "Small";
        else chartRow.Cells[2].Add(PieChart(pie, half, height));
    }

    private const string NoMinutes = "Brak minut postoju do pokazania.";

    /// <summary>Najwięcej wierszy tabeli postojów w PDF (najnowsze); pełna lista — w Excelu.</summary>
    internal const int MaxTableRows = 1000;

    private static void AddEpisodeTable(Section section, DowntimeExportData data)
    {
        var heading = section.AddParagraph("Postoje maszyn", StyleNames.Heading2);
        heading.Format.PageBreakBefore = true;
        if (data.Episodes.Count == 0)
        {
            section.AddParagraph("W wybranym okresie i zmianie nie ma postojów.");
            return;
        }
        var shown = data.Episodes.Count > MaxTableRows ? data.Episodes.Skip(data.Episodes.Count - MaxTableRows).ToArray() : data.Episodes;
        if (shown.Count < data.Episodes.Count)
        {
            var note = section.AddParagraph(
                $"Tabela pokazuje {shown.Count} najnowszych z {data.Episodes.Count} postojów (od {shown[0].Start.ToString("dd.MM.yyyy HH:mm", Pl)}). "
                + "Pełna lista jest w pliku Excel („Zapisz Excel”); sumy, wykresy i wiersz „Razem” obejmują wszystkie postoje.");
            note.Style = "Small";
            note.Format.SpaceAfter = Unit.FromPoint(6);
            note.Format.KeepWithNext = true;
        }
        var table = NewTable(section,
            ("Dzień zmiany", 2.1, false), ("Zmiana", 2.2, false), ("Maszyna", 2.6, false), ("Początek", 2.6, false),
            ("Koniec", 2.6, false), ("Czas [min]", 1.7, true), ("Nazwa błędu", 6.1, false), ("Grupa błędu", 2.3, false),
            ("Stan", 3.9, false));
        int index = 0;
        foreach (var row in shown)
            AddRow(table, index++, row.IsLong, row.Day.ToString("dd.MM.yyyy", Pl), row.Shift, row.Machine,
                row.Start.ToString("dd.MM.yyyy HH:mm", Pl), row.End.ToString("dd.MM.yyyy HH:mm", Pl), Number(row.Minutes),
                row.ErrorName, row.ErrorGroup, row.State);
        // Przy pełnej tabeli ujawniamy różnicę między sumą WYDRUKOWANYCH liczb a wydrukowanym Razem.
        // Nie zmieniamy czasu epizodów ani sumy raportu, nie rozdzielamy zaokrągleń między identyczne postoje.
        // Przy obciętej tabeli brakujące wiersze nie są różnicą zaokrągleń — Razem nadal obejmuje całość (nota wyżej).
        if (shown.Count == data.Episodes.Count)
        {
            decimal Displayed(double minutes) => decimal.Parse(Number(minutes), NumberStyles.Number, Pl);
            decimal difference = Displayed(data.Totals.All.Minutes) - shown.Sum(row => Displayed(row.Minutes));
            if (difference != 0)
            {
                var rounding = table.AddRow();
                rounding.KeepWith = 1;
                rounding.Cells[0].MergeRight = 4;
                rounding.Cells[0].AddParagraph("Różnica zaokrągleń");
                rounding.Cells[5].AddParagraph((difference > 0 ? "+" : string.Empty) + difference.ToString("N1", Pl));
                rounding.Cells[6].MergeRight = 2;
                rounding.Cells[6].AddParagraph("Łączny czas obliczono przed zaokrągleniem wierszy.");
            }
        }
        var total = table.AddRow();
        total.Format.Font.Bold = true;
        total.Borders.Top.Width = Unit.FromPoint(1);
        total.Borders.Top.Color = Ink;
        total.Cells[0].MergeRight = 4;
        total.Cells[0].AddParagraph("Razem (z długimi postojami)");
        var sum = total.Cells[5].AddParagraph(Number(data.Totals.All.Minutes));
        sum.Format.Alignment = ParagraphAlignment.Right;
        total.Cells[6].MergeRight = 2;
        total.Cells[6].AddParagraph($"Bez długich postojów: {Number(data.Totals.WithoutLong.Minutes)} min · "
            + $"w tym {DowntimeCharts.ErrorsName.ToLower(Pl)}: {Number(data.Kinds.Errors.All.Minutes)} min");
    }

    private static Table NewTable(Section section, params (string Header, double WidthCm, bool Right)[] columns)
    {
        var table = section.AddTable();
        table.Borders.Bottom.Width = Unit.FromPoint(0.5);
        table.Borders.Bottom.Color = Rule;
        table.Format.Font.Size = 8;
        table.LeftPadding = Unit.FromPoint(3);
        table.RightPadding = Unit.FromPoint(3);
        foreach (var column in columns)
        {
            var c = table.AddColumn(Unit.FromCentimeter(column.WidthCm));
            c.Format.Alignment = column.Right ? ParagraphAlignment.Right : ParagraphAlignment.Left;
            // Liczba wyrównana do prawej nie może dotykać tekstu kolumny obok.
            if (column.Right) c.Format.RightIndent = Unit.FromPoint(6);
        }
        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = HeaderFill;
        header.TopPadding = Unit.FromPoint(3);
        header.BottomPadding = Unit.FromPoint(3);
        for (int i = 0; i < columns.Length; i++) header.Cells[i].AddParagraph(columns[i].Header);
        return table;
    }

    private static void AddRow(Table table, int index, bool emphasize, params string[] values)
    {
        var row = table.AddRow();
        row.TopPadding = Unit.FromPoint(2);
        row.BottomPadding = Unit.FromPoint(2);
        if (index % 2 == 1) row.Shading.Color = StripeFill;
        for (int i = 0; i < values.Length; i++) row.Cells[i].AddParagraph(values[i]);
        if (emphasize) row.Cells[values.Length - 1].Format.Font.Bold = true;
    }

    /// <summary>Słupki według maszyny, skumulowane jak w Panelu: bez długich postojów + długie postoje (&gt; 1 h).</summary>
    private static Chart BarChart(BarChartData model, Unit width, Unit height)
    {
        var chart = NewChart(ChartType.ColumnStacked2D, width, height);
        foreach (var series in model.Series)
        {
            var s = chart.SeriesCollection.AddSeries();
            s.Name = series.Name;
            s.FillFormat.Color = new Color(series.Color.LightArgb);
            s.LineFormat.Visible = false;
            s.Add(series.Points.Select(p => p.Value).ToArray());
        }
        chart.XValues.AddXSeries().Add(Labels(model.Categories.ToArray(), 18, 16));
        ValueAxis(chart);
        // Legenda pod wykresem mieści 3 rodzaje postoju; 8 nazw błędów (słupki błędów według maszyny) wychodziło poza lewą
        // krawędź — przy wielu seriach legenda po prawej, jak przy kole.
        if (model.Series.Count <= 3) chart.BottomArea.AddLegend();
        else chart.RightArea.AddLegend();
        return chart;
    }

    /// <summary>Udział nazw błędów — wycinki, kolory i etykiety („Nierozpoznany”, „Pozostałe (n nazw)”) z modelu Panelu.</summary>
    private static Chart PieChart(PieChartData model, Unit width, Unit height)
    {
        var chart = NewChart(ChartType.Pie2D, width, height);
        var s = chart.SeriesCollection.AddSeries();
        s.Name = model.Title;
        foreach (var slice in model.Slices)
        {
            var p = s.Add(slice.Value);
            p.FillFormat.Color = new Color(slice.Color.LightArgb);
            p.LineFormat.Color = Colors.White;
            p.LineFormat.Width = Unit.FromPoint(1);
        }
        chart.XValues.AddXSeries().Add(model.Slices.Select(p => Shorten(p.Label, 34)).ToArray());
        chart.HasDataLabel = true;
        chart.DataLabel.Type = DataLabelType.Percent;
        chart.DataLabel.Position = DataLabelPosition.OutsideEnd;
        chart.DataLabel.Font.Size = 7;
        chart.RightArea.AddLegend();
        return chart;
    }

    /// <summary>Minuty na dzień — serie z modelu Panelu (suma z długimi i bez, albo osobno dla każdej maszyny).</summary>
    private static Chart LineChart(LineChartData model, Unit width, Unit height)
    {
        var chart = NewChart(ChartType.Line, width, height);
        foreach (var series in model.Series)
        {
            var s = chart.SeriesCollection.AddSeries();
            s.Name = series.Name;
            var color = new Color(series.Color.LightArgb);
            s.LineFormat.Color = color;
            s.LineFormat.Width = Unit.FromPoint(1.5);
            s.MarkerStyle = series.Points.Count <= 62 ? MarkerStyle.Circle : MarkerStyle.None;
            s.MarkerSize = Unit.FromPoint(4);
            s.MarkerForegroundColor = color;
            s.MarkerBackgroundColor = color;
            s.Add(series.Points.Select(p => p.Value).ToArray());
        }
        chart.XValues.AddXSeries().Add(Labels(DayLabels(model.Days), 26, 10));
        ValueAxis(chart);
        chart.BottomArea.AddLegend();
        return chart;
    }

    private static Chart DayColumns(LineChartData model, Unit width, Unit height)
    {
        var chart = NewChart(ChartType.Column2D, width, height);
        foreach (var series in model.Series)
        {
            var s = chart.SeriesCollection.AddSeries();
            s.Name = series.Name;
            s.FillFormat.Color = new Color(series.Color.LightArgb);
            s.LineFormat.Visible = false;
            s.Add(series.Points.Select(p => p.Value).ToArray());
        }
        chart.XValues.AddXSeries().Add(DayLabels(model.Days));
        ValueAxis(chart);
        chart.BottomArea.AddLegend();
        return chart;
    }

    private static Chart NewChart(ChartType type, Unit width, Unit height)
    {
        var chart = new Chart(type) { Width = width, Height = height };
        chart.Format.Font.Name = ExportFontResolver.FamilyName;
        chart.Format.Font.Size = 7;
        chart.Format.Font.Color = Ink;
        chart.PlotArea.LineFormat.Visible = false;
        return chart;
    }

    /// <summary>Oś wartości bez tytułu: jednostkę (minuty) mówi tytuł wykresu nad nim — tytuł osi MigraDoc nachodził na niego.</summary>
    private static void ValueAxis(Chart chart)
    {
        chart.YAxis.MajorTickMark = TickMarkType.Outside;
        chart.YAxis.HasMajorGridlines = true;
        chart.YAxis.MajorGridlines.LineFormat.Color = Grid;
        chart.YAxis.MajorGridlines.LineFormat.Width = Unit.FromPoint(0.5);
        chart.YAxis.LineFormat.Visible = false;
        chart.YAxis.MinimumScale = 0;
        chart.YAxis.TickLabels.Format = "0";
        chart.XAxis.MajorTickMark = TickMarkType.None;
        chart.XAxis.LineFormat.Color = Rule;
    }

    /// <summary>
    /// Etykiety osi: przy wielu punktach co k-ta (reszta pusta), żeby się nie nakładały; długie nazwy skrócone.
    /// </summary>
    private static string[] Labels(string[] labels, int maxVisible, int maxLength)
    {
        int step = labels.Length <= maxVisible ? 1 : (int)Math.Ceiling(labels.Length / (double)maxVisible);
        return labels.Select((label, i) => i % step == 0 ? Shorten(label, maxLength) : string.Empty).ToArray();
    }

    private static string[] DayLabels(IReadOnlyList<DateOnly> days)
    {
        bool withYear = days.Count > 1 && days[^1].DayNumber - days[0].DayNumber > 365;
        return days.Select(day => day.ToString(withYear ? "dd.MM.yy" : "dd.MM", Pl)).ToArray();
    }

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";

    private static string RangeText(DowntimeExportData data) =>
        data.From == data.To ? data.From.ToString("dd.MM.yyyy", Pl) : $"{data.From.ToString("dd.MM.yyyy", Pl)} – {data.To.ToString("dd.MM.yyyy", Pl)}";

    private static string Number(double minutes) => minutes.ToString("N1", Pl);

    private static string MinutesText(double minutes) => $"{Number(minutes)} min";

    /// <summary>Ten sam zapis godzin co karta „Łączny czas” w Panelu (części całkowite, bez zaokrąglania).</summary>
    private static string HoursText(double minutes)
    {
        if (minutes < 0 || double.IsNaN(minutes)) minutes = 0;
        return $"{(int)(minutes / 60)} godz. {(int)(minutes % 60)} min";
    }
}
