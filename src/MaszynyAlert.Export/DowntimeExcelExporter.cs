using ClosedXML.Excel;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Export;

public static class DowntimeExcelExporter
{
    public const string EpisodesSheet = "Epizody";
    public const string TotalsSheet = "Sumy";
    public const string ErrorsSheet = "Błędy";
    public const string InfoSheet = "Informacje";

    private const string DateFormat = "yyyy-mm-dd";
    private const string DateTimeFormat = "yyyy-mm-dd hh:mm:ss";
    private const string MinutesFormat = "0.00";

    /// <summary>Nagłówki arkusza „Epizody” (kolejność kolumn = kolejność w pliku).</summary>
    public static readonly IReadOnlyList<string> EpisodeHeaders =
    [
        "Identyfikator epizodu", "Dzień zmiany", "Zmiana", "Maszyna", "Początek", "Koniec", "Czas [min]",
        "Nazwa błędu", "Grupa błędu", "Stan", "Długi postój (> 1 h)",
    ];

    /// <summary>Tworzy plik .xlsx w <paramref name="output"/> (strumień zostaje otwarty). Błąd = wynik z komunikatem, bez wyjątku.</summary>
    public static ExportOutcome Write(DowntimeExportData data, Stream output)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            using var workbook = new XLWorkbook();
            workbook.Properties.Title = DowntimeExportData.Title;
            workbook.Properties.Author = "VNC Alert";
            workbook.Properties.Comments = DowntimeExportData.CreatedBy;
            AddEpisodes(workbook, data);
            AddTotals(workbook, data);
            AddErrors(workbook, data);
            AddInfo(workbook, data);
            workbook.SaveAs(output);
            return ExportOutcome.Ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ExportOutcome.Fail("Nie udało się utworzyć pliku Excel. " + ex.Message);
        }
    }

    private static void AddEpisodes(XLWorkbook workbook, DowntimeExportData data)
    {
        var sheet = workbook.Worksheets.Add(EpisodesSheet);
        Header(sheet, 1, EpisodeHeaders);
        int row = 2;
        foreach (var e in data.Episodes)
        {
            sheet.Cell(row, 1).Value = e.EpisodeId.ToString();
            Date(sheet.Cell(row, 2), e.Day.ToDateTime(TimeOnly.MinValue), DateFormat);
            sheet.Cell(row, 3).Value = e.Shift;
            sheet.Cell(row, 4).Value = e.Machine;
            Date(sheet.Cell(row, 5), e.Start, DateTimeFormat);
            Date(sheet.Cell(row, 6), e.End, DateTimeFormat);
            Minutes(sheet.Cell(row, 7), e.Minutes);
            sheet.Cell(row, 8).Value = e.ErrorName;
            sheet.Cell(row, 9).Value = e.ErrorGroup;
            sheet.Cell(row, 10).Value = e.State;
            sheet.Cell(row, 11).Value = e.IsLong ? "tak" : "nie";
            row++;
        }
        // Autofiltr na nagłówku i wierszach danych (także przy pustym raporcie — sam nagłówek).
        sheet.Range(1, 1, Math.Max(1, row - 1), EpisodeHeaders.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        Widths(sheet, 38, 13, 13, 16, 20, 20, 11, 40, 15, 34, 12);
    }

    private static void AddTotals(XLWorkbook workbook, DowntimeExportData data)
    {
        var sheet = workbook.Worksheets.Add(TotalsSheet);
        int row = 1;

        row = Title(sheet, row, "Według maszyny");
        Header(sheet, row++, ["Maszyna", "Czas z długimi postojami [min]", "Czas bez długich postojów [min]", "Liczba postojów",
            "W tym długich (> 1 h)", "W tym błędy maszyn [min]", "W tym błędów maszyn", DowntimeExportData.NoConnectionLabel + " [min]",
            DowntimeExportData.ScreenHiddenLabel + " [min]"]);
        foreach (var m in data.Machines)
        {
            sheet.Cell(row, 1).Value = m.Name;
            TotalsCells(sheet, row, 2, m.Totals);
            ErrorCells(sheet, row, 6, m.Kinds);
            NoData(sheet.Cell(row, 8), m.NoConnection, data.HasObservationJournal && m.ObservationKnown);
            NoData(sheet.Cell(row, 9), m.ScreenHidden, data.HasObservationJournal && m.ObservationKnown);
            row++;
        }
        sheet.Cell(row, 1).Value = "Razem";
        TotalsCells(sheet, row, 2, data.Totals);
        ErrorCells(sheet, row, 6, data.Kinds);
        NoData(sheet.Cell(row, 8), data.NoConnection, data.HasObservationJournal);
        NoData(sheet.Cell(row, 9), data.ScreenHidden, data.HasObservationJournal);
        sheet.Row(row).Style.Font.Bold = true;
        row += 2;

        row = Title(sheet, row, "Według rodzaju postoju");
        Header(sheet, row++, ["Rodzaj postoju", "Czas z długimi postojami [min]", "Czas bez długich postojów [min]", "Liczba postojów",
            "W tym długich (> 1 h)", "Udział [%]"]);
        row = KindRows(sheet, row, data);
        row++;

        row = Title(sheet, row, "Według nazwy błędu");
        Header(sheet, row++, ["Nazwa błędu", "Grupa błędu", "Czas z długimi postojami [min]", "Czas bez długich postojów [min]",
            "Liczba postojów", "W tym długich (> 1 h)"]);
        foreach (var e in data.Errors)
        {
            sheet.Cell(row, 1).Value = e.ErrorName;
            sheet.Cell(row, 2).Value = e.ErrorGroup;
            TotalsCells(sheet, row, 3, e.Totals);
            row++;
        }
        row++;

        row = Title(sheet, row, "Według dnia zmiany");
        Header(sheet, row++, ["Dzień zmiany", "Czas z długimi postojami [min]", "Czas bez długich postojów [min]", "Liczba postojów",
            "W tym długich (> 1 h)", "W tym błędy maszyn [min]", "W tym błędów maszyn"]);
        foreach (var d in data.Days)
        {
            Date(sheet.Cell(row, 1), d.Day.ToDateTime(TimeOnly.MinValue), DateFormat);
            TotalsCells(sheet, row, 2, d.Totals);
            ErrorCells(sheet, row, 6, d.Kinds);
            row++;
        }
        Widths(sheet, 40, 18, 18, 18, 16, 18, 16, 22, 22);
    }

    /// <summary>Wiersze „Według rodzaju postoju”: błędy maszyn, bez pracy (100 %), na 0 %, razem; udział w czasie z długimi.</summary>
    private static int KindRows(IXLWorksheet sheet, int row, DowntimeExportData data)
    {
        double total = data.Totals.All.Minutes;
        void Row(string name, Core.History.DowntimeTotals totals, bool bold = false)
        {
            sheet.Cell(row, 1).Value = name;
            TotalsCells(sheet, row, 2, totals);
            if (total > 0)
            {
                sheet.Cell(row, 6).Value = Math.Round(totals.All.Minutes / total * 100, 2);
                sheet.Cell(row, 6).Style.NumberFormat.Format = MinutesFormat;
            }
            else sheet.Cell(row, 6).Value = "—";
            if (bold) sheet.Row(row).Style.Font.Bold = true;
            row++;
        }
        Row(DowntimeCharts.ErrorsName, data.Kinds.Errors);
        Row(DowntimeCharts.IdleName, data.Kinds.Idle);
        Row(DowntimeCharts.IdleAtZeroName, data.Kinds.IdleAtZero);
        Row("Razem", data.Totals, bold: true);
        return row;
    }

    private static void AddErrors(XLWorkbook workbook, DowntimeExportData data)
    {
        var sheet = workbook.Worksheets.Add(ErrorsSheet);
        int row = 1;
        sheet.Cell(row, 1).Value = "Błędy maszyn — bez postojów bez pracy („Stoi bez pracy (100 %)”, „Stoi na 0 %”). Sumy obu rodzajów są w arkuszu „Sumy”.";
        row += 2;

        double errorTotal = data.Kinds.Errors.All.Minutes;
        row = Title(sheet, row, "Według nazwy błędu");
        Header(sheet, row++, ["Nazwa błędu", "Grupa błędu", "Czas z długimi postojami [min]", "Czas bez długich postojów [min]",
            "Liczba postojów", "W tym długich (> 1 h)", "Udział w czasie błędów [%]"]);
        foreach (var e in data.Errors.Where(e => !e.IsIdle))
        {
            sheet.Cell(row, 1).Value = e.ErrorName;
            sheet.Cell(row, 2).Value = e.ErrorGroup;
            TotalsCells(sheet, row, 3, e.Totals);
            if (errorTotal > 0)
            {
                sheet.Cell(row, 7).Value = Math.Round(e.Totals.All.Minutes / errorTotal * 100, 2);
                sheet.Cell(row, 7).Style.NumberFormat.Format = MinutesFormat;
            }
            row++;
        }
        sheet.Cell(row, 1).Value = "Razem";
        TotalsCells(sheet, row, 3, data.Kinds.Errors);
        sheet.Row(row).Style.Font.Bold = true;
        row += 2;

        row = Title(sheet, row, "Według maszyny");
        Header(sheet, row++, ["Maszyna", "Czas z długimi postojami [min]", "Czas bez długich postojów [min]", "Liczba postojów",
            "W tym długich (> 1 h)"]);
        foreach (var m in data.Machines.OrderByDescending(m => m.Kinds.Errors.All.Minutes)
                     .ThenBy(m => m.Name, StringComparer.CurrentCulture))
        {
            sheet.Cell(row, 1).Value = m.Name;
            TotalsCells(sheet, row, 2, m.Kinds.Errors);
            row++;
        }
        sheet.Cell(row, 1).Value = "Razem";
        TotalsCells(sheet, row, 2, data.Kinds.Errors);
        sheet.Row(row).Style.Font.Bold = true;
        row += 2;

        row = Title(sheet, row, "Według dnia zmiany");
        Header(sheet, row++, ["Dzień zmiany", "Czas z długimi postojami [min]", "Czas bez długich postojów [min]", "Liczba postojów",
            "W tym długich (> 1 h)"]);
        foreach (var d in data.Days)
        {
            Date(sheet.Cell(row, 1), d.Day.ToDateTime(TimeOnly.MinValue), DateFormat);
            TotalsCells(sheet, row, 2, d.Kinds.Errors);
            row++;
        }
        Widths(sheet, 40, 18, 18, 18, 16, 16, 22);
    }

    /// <summary>Dwie kolumny „w tym błędy maszyn”: minuty (z długimi) i liczba.</summary>
    private static void ErrorCells(IXLWorksheet sheet, int row, int column, Core.History.DowntimeKindTotals kinds)
    {
        Minutes(sheet.Cell(row, column), kinds.Errors.All.Minutes);
        sheet.Cell(row, column + 1).Value = kinds.Errors.All.Episodes;
    }

    private static void AddInfo(XLWorkbook workbook, DowntimeExportData data)
    {
        var sheet = workbook.Worksheets.Add(InfoSheet);
        int row = 1;
        void Line(string name, Action<IXLCell> value)
        {
            sheet.Cell(row, 1).Value = name;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            value(sheet.Cell(row, 2));
            row++;
        }
        sheet.Cell(row, 1).Value = DowntimeExportData.Title;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontSize = 16;
        row++;
        sheet.Cell(row++, 1).Value = DowntimeExportData.CreatedBy;
        row++;
        Line("Zakres od (dzień zmiany)", c => Date(c, data.From.ToDateTime(TimeOnly.MinValue), DateFormat));
        Line("Zakres do (dzień zmiany)", c => Date(c, data.To.ToDateTime(TimeOnly.MinValue), DateFormat));
        Line("Zmiana", c => c.Value = data.ShiftText);
        Line("Dane", c => c.Value = data.ScopeText);
        Line("Wygenerowano", c => Date(c, data.GeneratedAt.ToLocalTime().DateTime, DateTimeFormat));
        Line("Komputer", c => c.Value = data.ComputerName);
        row++;
        Line("Łączny czas postojów z długimi postojami [min]", c => Minutes(c, data.Totals.All.Minutes));
        Line("Łączny czas postojów bez długich postojów [min]", c => Minutes(c, data.Totals.WithoutLong.Minutes));
        Line("Liczba postojów", c => c.Value = data.Totals.All.Episodes);
        Line("W tym długich postojów (> 1 h)", c => c.Value = data.LongEpisodes);
        Line("W tym błędy maszyn [min]", c => Minutes(c, data.Kinds.Errors.All.Minutes));
        Line("W tym postoje bez pracy [min]", c => Minutes(c, data.Kinds.IdleMinutes));
        Line("W tym błędów maszyn", c => c.Value = data.Kinds.Errors.All.Episodes);
        Line(DowntimeExportData.NoConnectionLabel + " [min]", c => NoData(c, data.NoConnection, data.HasObservationJournal));
        Line(DowntimeExportData.ScreenHiddenLabel + " [min]", c => NoData(c, data.ScreenHidden, data.HasObservationJournal));
        if (data.ObservationKnownFrom is { } knownFrom)
            Line("„Brak danych” znany od", c => Date(c, knownFrom.ToLocalTime().DateTime, DateTimeFormat));
        row++;
        foreach (var note in new[]
        {
            "Postój to każdy czerwony alarm maszyny („błąd maszyny”) oraz postój bez pracy („Stoi bez pracy (100 %)”, „Stoi na 0 %”), bez względu na czas trwania; długi postój trwał ponad godzinę.",
            "Arkusz „Błędy” liczy wyłącznie błędy maszyn (bez postojów bez pracy); arkusz „Sumy” — oba rodzaje razem i w podziale.",
            "Weekend (od soboty 06:00 do poniedziałku 06:00) nie wchodzi do raportu. Nocna zmiana należy do dnia jej rozpoczęcia.",
            "Arkusz „Epizody” ma jeden wiersz na kawałek postoju w jednej zmianie; kawałki jednego postoju mają ten sam identyfikator epizodu.",
            "„Brak danych” to czas, gdy program nie miał połączenia z maszyną albo nie widział jej ekranu — nie jest liczony jako postój.",
            "Daty i godziny są w czasie lokalnym komputera, który utworzył raport.",
        })
            sheet.Cell(row++, 1).Value = note;
        Widths(sheet, 48, 30);
    }


    private static int Title(IXLWorksheet sheet, int row, string text)
    {
        sheet.Cell(row, 1).Value = text;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontSize = 13;
        return row + 1;
    }

    private static void Header(IXLWorksheet sheet, int row, IReadOnlyList<string> headers)
    {
        for (int i = 0; i < headers.Count; i++) sheet.Cell(row, i + 1).Value = headers[i];
        var range = sheet.Range(row, 1, row, headers.Count);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromArgb(0xEF, 0xEE, 0xEA);
        range.Style.Alignment.WrapText = true;
    }

    private static void TotalsCells(IXLWorksheet sheet, int row, int column, Core.History.DowntimeTotals totals)
    {
        Minutes(sheet.Cell(row, column), totals.All.Minutes);
        Minutes(sheet.Cell(row, column + 1), totals.WithoutLong.Minutes);
        sheet.Cell(row, column + 2).Value = totals.All.Episodes;
        sheet.Cell(row, column + 3).Value = totals.All.Episodes - totals.WithoutLong.Episodes;
    }

    private static void NoData(IXLCell cell, TimeSpan value, bool known)
    {
        if (known) Minutes(cell, value.TotalMinutes);
        else cell.Value = "brak zapisu";
    }

    private static void Minutes(IXLCell cell, double minutes)
    {
        // Zaokrąglenie dotyczy tylko widoku komórki; zapisane liczby muszą zachować sumę danych raportu.
        cell.Value = minutes;
        cell.Style.NumberFormat.Format = MinutesFormat;
    }

    private static void Date(IXLCell cell, DateTime value, string format)
    {
        cell.Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        cell.Style.DateFormat.Format = format;
    }

    private static void Widths(IXLWorksheet sheet, params double[] widths)
    {
        for (int i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
    }
}
